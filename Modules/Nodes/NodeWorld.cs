using Godot;
using System;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// A world of nodes on a global lattice, so anything placed anywhere lines up
/// with everything else.
///
/// What each node LOOKS like comes from its <see cref="INodeType"/>: the type
/// turns a cell position into geometry, and this class only meshes what it
/// returns. Setting <see cref="Shaped"/> false falls back to plain cubes.
///
/// CHUNK-OWNED, NOT WORLD-OWNED
///
/// The world's state used to be two structures that spanned all of space: a
/// dictionary of every filled cell, and a sub-cell occupancy map stamped from
/// every node in it. Both grew without bound and neither could give anything
/// back, which is what limited the world to a region small enough to hold in
/// RAM all at once — the reason a planet was out of reach.
///
/// Now every chunk owns its own cells (<see cref="NodeChunkStore"/>), its own
/// mesh, and its own collision, and can be dropped whole when the player
/// leaves. Occupancy is no longer stored at all: it is re-derived per meshing
/// job (<see cref="ChunkOccupancy"/>) from data the chunk and its neighbours
/// already hold, because a node's shape depends only on its position and seed.
///
/// The consequences are what the rest of the streaming work rests on. Memory
/// is bounded by the loaded radius rather than by everywhere the player has
/// ever been; a chunk can be generated, meshed and freed independently of
/// every other; and meshing reads only immutable chunk data, so it is free to
/// leave the main thread.
/// </summary>
[Tool]
public partial class NodeWorld : StaticBody3D
{
    private float _nodeSize = 1f;
    private Color _colorA = new(0.30f, 0.30f, 0.34f);
    private Color _colorB = new(0.62f, 0.62f, 0.66f);

    private bool _shaped = true;
    private string _nodeType = "raw";
    private int _seed = 1337;
    private float _flowScale = 6f;
    private float _roughness = 0.35f;
    private float _growth = 0.7f;

    [Export(PropertyHint.Range, "0.1,4,0.05")]
    public float NodeSize
    {
        get => _nodeSize;
        set { _nodeSize = Mathf.Max(0.05f, value); RebuildIfReady(); }
    }

    [Export]
    public Color ColorA { get => _colorA; set { _colorA = value; RebuildIfReady(); } }

    [Export]
    public Color ColorB { get => _colorB; set { _colorB = value; RebuildIfReady(); } }

    [ExportGroup("Nodes")]
    /// <summary>Shape nodes with their node type instead of drawing plain
    /// cubes. The grid, picking and editing are unchanged either way — only
    /// the geometry inside each cell differs.</summary>
    [Export]
    public bool Shaped { get => _shaped; set { _shaped = value; InvalidateNodeType(); } }

    [Export]
    public string NodeType { get => _nodeType; set { _nodeType = value; InvalidateNodeType(); } }

    [Export]
    public int Seed { get => _seed; set { _seed = value; InvalidateNodeType(); } }

    [Export(PropertyHint.Range, "0.5,40,0.5")]
    public float FlowScale { get => _flowScale; set { _flowScale = Mathf.Max(0.5f, value); InvalidateNodeType(); } }

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float Roughness { get => _roughness; set { _roughness = Mathf.Clamp(value, 0f, 1f); InvalidateNodeType(); } }

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float Growth { get => _growth; set { _growth = Mathf.Clamp(value, 0f, 1f); InvalidateNodeType(); } }

    private INodeType _type;
    private readonly Dictionary<NodeMaterial, INodeType> _typesByMaterial = new();

    /// <summary>
    /// The node type that carves a given material.
    ///
    /// The world's exported <see cref="NodeType"/> selects the type for the
    /// DEFAULT material only; everything else is named by the palette. That
    /// way the existing dropdown still chooses what the rock looks like, while
    /// a capping material can insist on plain cubes regardless of it.
    ///
    /// Types are cached per material because constructing one builds its
    /// geometry tables, and the mesher asks per node.
    /// </summary>
    private INodeType TypeOf(NodeMaterial material)
    {
        if (_typesByMaterial.TryGetValue(material, out INodeType cached))
            return cached;

        string id = material == NodeMaterial.Raw ? _nodeType : NodeMaterials.TypeIdOf(material);

        if (!NodeTypes.IsKnown(id))
        {
            GD.PushWarning($"NodeWorld: unknown node type '{id}' — using raw.");
            id = "raw";
        }

        INodeType type = NodeTypes.Create(id, _seed, _flowScale, _roughness, _growth);
        _typesByMaterial[material] = type;
        return type;
    }

    /// <summary>
    /// The type-lookup delegate handed to worker jobs.
    ///
    /// Cached rather than built per call because a job passes it into the
    /// occupancy fill, which invokes it once per node. Populated on the main
    /// thread before any job starts, so the dictionary behind it is only ever
    /// read while workers run.
    /// </summary>
    private Func<NodeMaterial, INodeType> _typeLookup;

    /// <summary>
    /// Makes sure every material's type is constructed before workers start.
    ///
    /// <see cref="TypeOf"/> memoises into a plain dictionary, so letting two
    /// worker threads race to construct the same type would corrupt it. There
    /// are only a handful of materials, so building them all up front is
    /// cheaper than guarding the cache on every one of the millions of lookups
    /// a build makes.
    /// </summary>
    private void WarmTypeCache()
    {
        foreach (NodeMaterial material in Enum.GetValues<NodeMaterial>())
            TypeOf(material);

        _typeLookup ??= TypeOf;
    }

    /// <summary>The type for the default material, for the paths that predate
    /// per-node materials (picking heuristics, warm-up).</summary>
    private INodeType Type => _type ??= TypeOf(NodeMaterial.Raw);

    /// <summary>
    /// Sub-cells per node edge.
    ///
    /// Every type in a world must agree on this: occupancy is a single
    /// lattice, and two types on different lattices would disagree about what
    /// is solid and tear holes in the culling.
    /// </summary>
    private int Sub => Type.Subdivision;

    private void InvalidateNodeType()
    {
        _type = null;
        _typesByMaterial.Clear();
        _typeLookup = null;
        RebuildIfReady();
    }

    /// <summary>What a cell is made of, defaulting to the base rock for a cell
    /// that is not filled.</summary>
    public NodeMaterial MaterialAt(Vector3I cell)
    {
        byte material = _store.Get(cell);
        return material == NodeChunkStore.Air ? NodeMaterial.Raw : (NodeMaterial)material;
    }

    /// <summary>Sub-cells per node edge — the lattice node geometry is
    /// expressed on. Public so the editor's highlight can trace a node's
    /// actual filled shape rather than assuming a cube.</summary>
    public int Subdivision => Sub;

    /// <summary>
    /// The sub-cells a node fills, as flat (i, j, k) triples in node-local
    /// coordinates. What the highlight outlines, and what the mesher tests
    /// against — so the two never disagree about a node's shape.
    /// </summary>
    public int[] NodeSubCells(Vector3I cell)
    {
        INodeType type = TypeOf(MaterialAt(cell));
        return type.OccupiedCells(type.ShapeAt(cell));
    }

    /// <summary>Nodes across every loaded chunk. Walks the chunk list rather
    /// than a global map, so it is a count of what is RESIDENT — which is the
    /// number that matters now that the world extends past what is loaded.</summary>
    public int NodeCount => _store.NodeCount;

    /// <summary>Chunks currently resident.</summary>
    public int LoadedChunks => _store.ChunkCount;

    /// <summary>Roughly how many bytes the resident chunks hold.</summary>
    public long ApproximateBytes => _store.ApproximateBytes();

    /// <summary>
    /// The world's cells, owned per chunk. Public so the streamer can install
    /// generated chunks and drop departed ones without going through the
    /// single-cell edit path.
    /// </summary>
    public NodeChunkStore Store => _store;

    private readonly NodeChunkStore _store = new();

    /// <summary>Cells per chunk edge, from the store that defines it.</summary>
    public const int ChunkSize = NodeChunkStore.ChunkSize;

    /// <summary>One chunk's scene nodes, created on demand.</summary>
    private sealed class ChunkNodes
    {
        public readonly MeshInstance3D MeshInstance;
        public readonly CollisionShape3D CollisionShape;
        public ConcavePolygonShape3D Trimesh;

        public ChunkNodes(Node parent, Vector3I coord)
        {
            MeshInstance = new MeshInstance3D { Name = $"Mesh{coord.X}_{coord.Y}_{coord.Z}" };
            CollisionShape = new CollisionShape3D { Name = $"Col{coord.X}_{coord.Y}_{coord.Z}" };
            parent.AddChild(MeshInstance);
            parent.AddChild(CollisionShape);
        }

        public void Dispose()
        {
            MeshInstance.QueueFree();
            CollisionShape.QueueFree();
        }
    }

    private readonly Dictionary<Vector3I, ChunkNodes> _chunks = new();
    private readonly HashSet<Vector3I> _dirty = new();
    private readonly List<Vector3I> _scratch = new();

    // Batch state: whether edits are being deferred, whether one is queued,
    // and whether it needs the whole-world path rather than the dirty one.
    private bool _deferRebuild;
    private bool _rebuildPending;
    private bool _fullRebuildNeeded;

    /// <summary>Chunk containing a cell.</summary>
    private static Vector3I ChunkOf(Vector3I cell) => NodeChunkStore.ChunkOf(cell);

    /// <summary>Floor division, so negative coordinates map to the cell below
    /// rather than truncating toward zero.</summary>
    internal static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        return value % divisor != 0 && (value < 0) != (divisor < 0) ? q - 1 : q;
    }

    /// <summary>
    /// A cube's six faces: the neighbour direction that hides the face, and
    /// its four corners as per-axis picks from (min, max).
    /// </summary>
    private static readonly (Vector3I Dir, int[] Xs, int[] Ys, int[] Zs)[] CubeFaces =
    {
        (new Vector3I(0, 1, 0),  new[]{0,1,1,0}, new[]{1,1,1,1}, new[]{0,0,1,1}), // up
        (new Vector3I(0, -1, 0), new[]{0,1,1,0}, new[]{0,0,0,0}, new[]{0,0,1,1}), // down
        (new Vector3I(1, 0, 0),  new[]{1,1,1,1}, new[]{0,1,1,0}, new[]{0,0,1,1}), // +x
        (new Vector3I(-1, 0, 0), new[]{0,0,0,0}, new[]{0,1,1,0}, new[]{0,0,1,1}), // -x
        (new Vector3I(0, 0, 1),  new[]{0,0,1,1}, new[]{0,1,1,0}, new[]{1,1,1,1}), // +z
        (new Vector3I(0, 0, -1), new[]{0,0,1,1}, new[]{0,1,1,0}, new[]{0,0,0,0}), // -z
    };

    /// <summary>The four corners of one cube face, in winding order.</summary>
    private static void FaceCorners(in (Vector3I Dir, int[] Xs, int[] Ys, int[] Zs) face,
        Vector3 min, Vector3 max, Span<Vector3> corners)
    {
        for (int i = 0; i < 4; i++)
        {
            corners[i] = new Vector3(
                face.Xs[i] == 0 ? min.X : max.X,
                face.Ys[i] == 0 ? min.Y : max.Y,
                face.Zs[i] == 0 ? min.Z : max.Z);
        }
    }

    /// <summary>Frees the scene nodes of chunks whose data holds nothing.</summary>
    private void DiscardEmptyChunks()
    {
        _scratch.Clear();
        foreach (var kv in _chunks)
        {
            NodeChunkStore.Chunk data = _store.Find(kv.Key);
            if (data == null || data.SolidCount == 0)
                _scratch.Add(kv.Key);
        }

        foreach (Vector3I dead in _scratch)
        {
            _chunks[dead].Dispose();
            _chunks.Remove(dead);
        }
    }

    /// <summary>
    /// Marks every chunk whose mesh a change at `cell` could alter.
    ///
    /// Radius 2, matching how far an edit can change occupancy: a node's rim
    /// reaches one cell out, and whether a node is enclosed depends on its own
    /// 3x3x3, so an edit alters the answer up to two cells away.
    /// </summary>
    private void MarkDirty(Vector3I cell)
    {
        for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
                for (int dz = -2; dz <= 2; dz++)
                    _dirty.Add(ChunkOf(cell + new Vector3I(dx, dy, dz)));
    }

    private StandardMaterial3D _material;

    private readonly List<Vector3> _vertices = new();
    private readonly List<Vector3> _normals = new();
    private readonly List<Color> _colors = new();
    private readonly List<int> _indices = new();

    /// <summary>
    /// The occupancy window used by main-thread meshing.
    ///
    /// One instance, reused across chunks: it is a fixed-size buffer that
    /// <see cref="ChunkOccupancy.Reset"/> clears, so meshing the whole world
    /// allocates it once rather than once per chunk.
    /// </summary>
    private readonly ChunkOccupancy _occupancy = new();

    /// <summary>0..1 while the world is meshing, 1 once it is done.</summary>
    public float BuildProgress { get; private set; }

    /// <summary>True once the world has finished its first full build, so
    /// collision exists and it is safe to drop the player in.</summary>
    public bool IsWorldReady => _ready;

    /// <summary>Raised once, when the first full build completes.</summary>
    public event System.Action WorldReady;

    private bool _ready;
    private readonly List<Vector3I> _pending = new();
    private int _pendingIndex;

    /// <summary>
    /// Meshes the world a few chunks per frame instead of all at once, so a
    /// large level can show progress rather than freezing.
    ///
    /// No occupancy pre-pass any more. It used to stamp every node in the
    /// world into a global map before a single chunk could be meshed — the
    /// stall the loading bar sat through between generating and meshing.
    /// Occupancy is now derived per chunk inside the meshing loop, from data
    /// the chunk already holds, so there is nothing to do here but queue.
    /// </summary>
    public void BeginIncrementalBuild()
    {
        EndGenerating();
        WarmTypeCache();

        DiscardEmptyChunks();

        _pending.Clear();
        foreach (var kv in _store.Chunks)
        {
            if (kv.Value.SolidCount > 0)
                _pending.Add(kv.Key);
        }

        _pendingIndex = 0;
        _ready = _pending.Count == 0;
        BuildProgress = _ready ? 1f : 0f;
        SetProcess(!_ready);

        if (_ready)
            WorldReady?.Invoke();
    }

    /// <summary>Chunks meshed per frame during an incremental build.</summary>
    [Export(PropertyHint.Range, "1,64,1")]
    public int ChunksPerFrame { get; set; } = 2;

    /// <summary>
    /// Milliseconds of meshing per frame, once <see cref="ChunksPerFrame"/>
    /// chunks are done.
    ///
    /// The count is the floor that guarantees forward progress; this is the
    /// ceiling that keeps a frame from running long.
    /// </summary>
    [Export(PropertyHint.Range, "2,100,1")]
    public float MeshMillisecondsPerFrame { get; set; } = 12f;

    public override void _Process(double delta)
    {
        if (_pendingIndex >= _pending.Count)
        {
            SetProcess(false);
            return;
        }

        ulong deadline = Time.GetTicksMsec()
            + (ulong)Mathf.Max(1f, MeshMillisecondsPerFrame);
        int floor = _pendingIndex + Mathf.Max(1, ChunksPerFrame);

        while (_pendingIndex < _pending.Count)
        {
            MeshChunk(_pending[_pendingIndex]);
            _pendingIndex++;

            // The floor is met first, so a frame always makes progress even if
            // a single chunk overruns the budget on its own.
            if (_pendingIndex >= floor && Time.GetTicksMsec() >= deadline)
                break;
        }

        BuildProgress = _pending.Count == 0
            ? 1f
            : _pendingIndex / (float)_pending.Count;

        if (_pendingIndex >= _pending.Count)
        {
            SetProcess(false);
            _dirty.Clear();
            BuildProgress = 1f;
            if (!_ready)
            {
                WarmUpEditPath();
                _ready = true;
                WorldReady?.Invoke();
            }
        }
    }

    /// <summary>
    /// Runs one real edit and undoes it while the loading screen is still up,
    /// so the engine's one-time cost for REPLACING a mesh (pipeline recompile,
    /// physics buffer growth, JIT over the dirty-rebuild path) lands in the
    /// loading bar rather than on the player's first mined node.
    /// </summary>
    private void WarmUpEditPath()
    {
        // Any resident node will do; take one deterministically so the warm-up
        // is reproducible rather than depending on hash iteration order.
        Vector3I victim = default;
        bool found = false;

        foreach (var kv in _store.Chunks)
        {
            if (kv.Value.SolidCount == 0)
                continue;

            Vector3I origin = NodeChunkStore.OriginOf(kv.Key);
            for (int x = 0; x < ChunkSize && !found; x++)
                for (int y = 0; y < ChunkSize && !found; y++)
                    for (int z = 0; z < ChunkSize && !found; z++)
                    {
                        if (kv.Value.Get(NodeChunkStore.LocalIndex(x, y, z)) == NodeChunkStore.Air)
                            continue;

                        victim = new Vector3I(origin.X + x, origin.Y + y, origin.Z + z);
                        found = true;
                    }

            if (found)
                break;
        }

        if (!found)
            return;

        NodeMaterial material = MaterialAt(victim);
        RemoveNode(victim);
        AddNode(victim, material);
    }

    public override void _Ready()
    {
        // A generator child readies BEFORE this node (Godot readies children
        // first) and may already have started an incremental build. Rebuilding
        // here would throw that away and do the whole world synchronously.
        if (_generating || _pending.Count > 0 || _ready)
            return;

        SetProcess(false);
        Rebuild();
    }

    private bool _generating;

    /// <summary>
    /// Told by a generator that it is still producing nodes, so an empty world
    /// must not be mistaken for a finished one.
    /// </summary>
    public void BeginGenerating()
    {
        _generating = true;
        _ready = false;

        BuildProgress = 0f;
    }

    /// <summary>
    /// Adds a node during generation, without the dirty-marking and re-stamping
    /// an interactive edit needs.
    ///
    /// Occupancy is no longer maintained incrementally — it is derived when a
    /// chunk is meshed — so this is now simply a write into the owning chunk.
    /// </summary>
    public bool AddNodeGenerated(Vector3I cell, NodeMaterial material) =>
        _store.Set(cell, (byte)material);

    /// <summary>Whether a generator is still producing nodes.</summary>
    public bool IsGenerating => _generating;

    /// <summary>Ends the generating state.</summary>
    private void EndGenerating() => _generating = false;

    private void RebuildIfReady()
    {
        if (IsNodeReady())
            Rebuild();
    }

    // ------------------------------------------------------------ node access

    public bool HasNode(Vector3I cell) => _store.Has(cell);

    /// <summary>Grid cell containing a world-space point.</summary>
    public Vector3I CellAt(Vector3 worldPoint)
    {
        Vector3 local = ToLocal(worldPoint) / _nodeSize;
        return new Vector3I(
            Mathf.FloorToInt(local.X),
            Mathf.FloorToInt(local.Y),
            Mathf.FloorToInt(local.Z));
    }

    /// <summary>World-space centre of a cell.</summary>
    public Vector3 CellCentre(Vector3I cell)
    {
        return ToGlobal((new Vector3(cell.X, cell.Y, cell.Z) + Vector3.One * 0.5f) * _nodeSize);
    }

    /// <summary>
    /// Runs several edits and meshes once at the end, instead of once per
    /// node.
    /// </summary>
    public void Batch(System.Action edits, bool wholesale = false, bool deferMesh = false)
    {
        bool outermost = !_deferRebuild;
        if (wholesale)
            _fullRebuildNeeded = true;
        _deferRebuild = true;
        try
        {
            edits();
        }
        finally
        {
            if (outermost)
            {
                _deferRebuild = false;

                // deferMesh hands meshing to the caller (the incremental
                // loading path), so the queued rebuild is dropped — INCLUDING
                // the full-rebuild flag. Leaving that set would make the next
                // single edit take the whole-world path instead of the dirty
                // one.
                if (deferMesh)
                {
                    _rebuildPending = false;
                    _fullRebuildNeeded = false;
                }

                if (_rebuildPending)
                {
                    _rebuildPending = false;
                    if (_fullRebuildNeeded)
                    {
                        _fullRebuildNeeded = false;
                        Rebuild();
                    }
                    else
                    {
                        RebuildDirty();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Re-meshes the dirty chunks now, or defers until the Batch closes.
    /// </summary>
    private void RebuildOrDefer()
    {
        if (_deferRebuild)
        {
            _rebuildPending = true;
            return;
        }

        if (_fullRebuildNeeded)
        {
            _fullRebuildNeeded = false;
            Rebuild();
        }
        else
        {
            RebuildDirty();
        }
    }

    /// <summary>
    /// Fills a cell. `material` decides both its appearance and which node
    /// type shapes it.
    ///
    /// No occupancy bookkeeping: the map that used to be patched around every
    /// edit is derived per chunk at mesh time now, so marking the affected
    /// chunks dirty is the whole of what an edit has to record.
    /// </summary>
    public bool AddNode(Vector3I cell, NodeMaterial material = NodeMaterial.Raw)
    {
        if (!_store.Set(cell, (byte)material))
            return false;

        if (!_fullRebuildNeeded)
            MarkDirty(cell);

        RebuildOrDefer();
        return true;
    }

    public bool RemoveNode(Vector3I cell)
    {
        if (!_store.Set(cell, NodeChunkStore.Air))
            return false;

        if (!_fullRebuildNeeded)
            MarkDirty(cell);

        RebuildOrDefer();
        return true;
    }

    /// <summary>Fills a solid box of nodes, inclusive of both corners.</summary>
    public void Fill(Vector3I from, Vector3I to, NodeMaterial material = NodeMaterial.Raw)
    {
        var min = new Vector3I(Mathf.Min(from.X, to.X), Mathf.Min(from.Y, to.Y), Mathf.Min(from.Z, to.Z));
        var max = new Vector3I(Mathf.Max(from.X, to.X), Mathf.Max(from.Y, to.Y), Mathf.Max(from.Z, to.Z));
        for (int x = min.X; x <= max.X; x++)
        {
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                    _store.Set(new Vector3I(x, y, z), (byte)material);
            }
        }

        _fullRebuildNeeded = true;
        RebuildOrDefer();
    }

    public void Clear()
    {
        _store.Clear();

        _fullRebuildNeeded = true;
        RebuildOrDefer();
    }

    /// <summary>
    /// Drops a chunk entirely — its cells, its mesh and its collision.
    ///
    /// The operation the old world could not perform at all, and the whole
    /// point of chunk-owned storage: walking away from terrain has to give its
    /// memory back, or the world is bounded by everywhere the player has ever
    /// been rather than by where they are.
    /// </summary>
    public void UnloadChunk(Vector3I chunk)
    {
        if (_chunks.TryGetValue(chunk, out ChunkNodes scene))
        {
            scene.Dispose();
            _chunks.Remove(chunk);
        }

        _store.Unload(chunk);
        _dirty.Remove(chunk);
    }

    /// <summary>Is this chunk's data resident?</summary>
    public bool IsChunkLoaded(Vector3I chunk) => _store.IsLoaded(chunk);

    /// <summary>
    /// Does this chunk have geometry built?
    ///
    /// Distinct from having DATA: the streamer generates a margin of chunks
    /// beyond what it draws, purely so the chunks inside can cull their
    /// boundary faces against real neighbours. Those have data and no mesh,
    /// and the streamer needs to tell the two apart to know what still owes
    /// geometry when the player moves toward it.
    /// </summary>
    public bool HasChunkMesh(Vector3I chunk) => _chunks.ContainsKey(chunk);

    /// <summary>
    /// Queues a chunk to be re-meshed. For the streamer, which installs chunk
    /// data directly into the store and then asks for geometry.
    /// </summary>
    public void QueueChunkMesh(Vector3I chunk)
    {
        _dirty.Add(chunk);
    }

    /// <summary>
    /// Meshes everything queued by <see cref="QueueChunkMesh"/>.
    ///
    /// Separate from the queueing so the streamer can decide its own pacing:
    /// it queues under its per-frame budget and flushes, rather than having
    /// each queued chunk trigger a rebuild of its own.
    /// </summary>
    public void FlushQueuedMeshes()
    {
        if (_dirty.Count == 0)
            return;

        WarmTypeCache();
        RebuildDirty();
    }

    // -------------------------------------------------------------- ray picking

    /// <summary>
    /// Steps a ray through the grid and returns the first node it enters,
    /// plus the empty cell it passed through immediately before — the face it
    /// arrived through, which is where a placed node belongs.
    /// </summary>
    public bool RayPick(Vector3 worldFrom, Vector3 worldDir, float maxDistance,
        out Vector3I hitCell, out Vector3I emptyCell)
    {
        hitCell = default;
        emptyCell = default;

        float step = _nodeSize * 0.2f;
        var previous = CellAt(worldFrom);
        bool started = false;

        for (float travelled = 0f; travelled <= maxDistance; travelled += step)
        {
            Vector3I cell = CellAt(worldFrom + worldDir * travelled);
            if (started && cell == previous)
                continue;

            if (_store.Has(cell))
            {
                hitCell = cell;
                emptyCell = started ? previous : cell;
                return true;
            }

            previous = cell;
            started = true;
        }

        return false;
    }

    // -------------------------------------------------------------------- mesh

    /// <summary>
    /// Rebuilds every resident chunk. Use this after a wholesale change; a
    /// single node edit should go through the dirty-chunk path instead.
    /// </summary>
    public void Rebuild()
    {
        WarmTypeCache();
        DiscardEmptyChunks();

        foreach (var kv in _store.Chunks)
        {
            if (kv.Value.SolidCount > 0)
                MeshChunk(kv.Key);
        }

        _dirty.Clear();
        _pending.Clear();
        BuildProgress = 1f;
        if (!_ready)
        {
            _ready = true;
            WorldReady?.Invoke();
        }
    }

    /// <summary>
    /// Re-meshes only the chunks marked dirty. A one-node edit touches its
    /// own chunk plus any neighbour whose surface it changes.
    /// </summary>
    private void RebuildDirty()
    {
        if (_dirty.Count == 0)
            return;

        WarmTypeCache();

        foreach (Vector3I chunk in _dirty)
        {
            NodeChunkStore.Chunk data = _store.Find(chunk);
            if (data == null || data.SolidCount == 0)
            {
                if (_chunks.TryGetValue(chunk, out ChunkNodes empty))
                {
                    empty.Dispose();
                    _chunks.Remove(chunk);
                }

                continue;
            }

            MeshChunk(chunk);
        }

        _dirty.Clear();
    }

    /// <summary>
    /// Builds one chunk's mesh and collision straight from the store.
    ///
    /// No node list is passed in any more. The chunk IS the list — a dense
    /// array of its own cells — so the mesher walks it directly instead of
    /// being handed the result of bucketing every node in the world.
    /// </summary>
    private void MeshChunk(Vector3I chunk)
    {
        NodeChunkStore.Chunk data = _store.Find(chunk);
        if (data == null || data.SolidCount == 0)
            return;

        _vertices.Clear();
        _normals.Clear();
        _colors.Clear();
        _indices.Clear();

        if (_shaped)
        {
            _occupancy.Reset(chunk);
            _occupancy.Fill(_store, _typeLookup ?? TypeOf);
        }

        Vector3I origin = NodeChunkStore.OriginOf(chunk);

        // Allocated once outside the loop: a stackalloc per node would grow
        // the stack frame by every iteration and eventually overflow.
        Span<Vector3> corners = stackalloc Vector3[4];

        for (int lx = 0; lx < ChunkSize; lx++)
        {
            for (int ly = 0; ly < ChunkSize; ly++)
            {
                for (int lz = 0; lz < ChunkSize; lz++)
                {
                    byte raw = data.Get(NodeChunkStore.LocalIndex(lx, ly, lz));
                    if (raw == NodeChunkStore.Air)
                        continue;

                    var cell = new Vector3I(origin.X + lx, origin.Y + ly, origin.Z + lz);

                    // ENCLOSED — every neighbour that could expose a face is
                    // solid, so nothing this node emits can be seen.
                    //
                    // Measured at 76% of the nodes a chunk meshes: the inside
                    // of an island is most of its volume, and every one of
                    // those nodes was having its shape solved, its quads
                    // walked and each quad's occlusion cells tested, only to
                    // contribute nothing. Testing 26 bytes first is far
                    // cheaper than the work it avoids.
                    if (_shaped && Enclosed(cell))
                        continue;

                    var material = (NodeMaterial)raw;

                    Vector3 min = new Vector3(cell.X, cell.Y, cell.Z) * _nodeSize;
                    Vector3 max = min + Vector3.One * _nodeSize;

                    // Alternating on all three axes, so no two touching cubes
                    // share a shade and every node's shape stays readable.
                    bool even = ((cell.X + cell.Y + cell.Z) & 1) == 0;
                    Color color;
                    if (material == NodeMaterial.Raw)
                    {
                        color = even ? _colorA : _colorB;
                    }
                    else
                    {
                        NodeMaterials.Entry entry = NodeMaterials.Get(material);
                        color = even ? entry.ColorA : entry.ColorB;
                    }

                    if (_shaped)
                    {
                        AddShapedNode(cell, material, min, color);
                        continue;
                    }

                    foreach (var face in CubeFaces)
                    {
                        if (_store.Has(cell + face.Dir))
                            continue;
                        FaceCorners(face, min, max, corners);
                        AddFace(color, corners, new Vector3(face.Dir.X, face.Dir.Y, face.Dir.Z));
                    }
                }
            }
        }

        if (!_chunks.TryGetValue(chunk, out ChunkNodes target))
        {
            target = new ChunkNodes(this, chunk);
            _chunks[chunk] = target;
        }

        var mesh = new ArrayMesh();
        if (_vertices.Count > 0)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = _indices.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            // One shared material rather than a fresh one per rebuild: a new
            // StandardMaterial3D every edit means a new shader instance and a
            // cold pipeline cache each time.
            mesh.SurfaceSetMaterial(0, SharedMaterial);
        }

        target.MeshInstance.Mesh = mesh;
        UpdateCollision(target, data, origin, mesh);
    }

    /// <summary>
    /// Are all six face neighbours solid?
    ///
    /// The test for a cube-faced hull: a face is drawn only when the cell in
    /// front of it is empty, so a node with six solid neighbours contributes
    /// no hull triangles.
    /// </summary>
    private bool FaceEnclosed(Vector3I cell)
    {
        return _store.Has(cell + Vector3I.Right)
            && _store.Has(cell + Vector3I.Left)
            && _store.Has(cell + Vector3I.Up)
            && _store.Has(cell + Vector3I.Down)
            && _store.Has(cell + Vector3I.Back)
            && _store.Has(cell + Vector3I.Forward);
    }

    /// <summary>
    /// Is every sub-cell this node could possibly show already solid?
    ///
    /// The cheap skip for interior nodes, and it has to be asked in SUB-CELL
    /// terms rather than in whole neighbours. A raw node's geometry spans
    /// -1..Sub in each axis — rims straddle the lattice edges and corners it
    /// won — so "all 26 neighbours are solid" is not sufficient: a neighbour
    /// can be present and still have vacated the very space this node's rim
    /// grows into, leaving that rim exposed. Culling on neighbour presence
    /// measured 298k triangles of real surface removed.
    ///
    /// So the shell just outside the node's core is tested against the same
    /// occupancy map the per-quad test uses. If every sub-cell around the node
    /// is filled by something, no quad of it can face open space, and the
    /// whole node can be skipped without solving its shape at all.
    ///
    /// This is a conservative test: it may answer false for a node that is in
    /// fact invisible, which only costs the per-quad work that used to happen
    /// anyway. It must never answer true for one that is visible, which is why
    /// the shell it checks is the full reach of the geometry rather than the
    /// node's own cell.
    /// </summary>
    private bool Enclosed(Vector3I cell)
    {
        const int Sub = RawNodeGeometry.Sub;

        // TWO sub-cells of shell, not one.
        //
        // One was tried and is unsound: a quad of this node can sit at the
        // node's own boundary and face outward, and the sub-cell that hides it
        // is then a full cell beyond — outside a one-deep shell. Audited
        // against the per-quad test, a one-deep shell wrongly culled 6783
        // nodes whose faces were genuinely visible.
        //
        // Two is the reach the geometry actually has: a corner win straddles
        // the lattice corner, so a rim can start one sub-cell outside the node
        // and extend another beyond that.
        for (int i = -2; i <= Sub + 1; i++)
            for (int j = -2; j <= Sub + 1; j++)
                for (int k = -2; k <= Sub + 1; k++)
                {
                    // Interior of the node itself: its own geometry fills this
                    // and it tells us nothing about what is visible.
                    bool inside = i >= 0 && i < Sub && j >= 0 && j < Sub && k >= 0 && k < Sub;
                    if (inside)
                        continue;

                    if (!_occupancy.Solid(cell, i, j, k))
                        return false;
                }

        return true;
    }

    private StandardMaterial3D SharedMaterial => _material ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true,
        Roughness = 1f,
    };

    /// <summary>
    /// Rebuilds collision from the BLOCK HULL, not the rendered bismuth
    /// surface. The physics engine builds a BVH over every triangle it is
    /// given, and crystal rims multiply that count for relief no player can
    /// feel through a capsule — plain cube faces are ~10x fewer triangles.
    /// </summary>
    private void UpdateCollision(ChunkNodes scene, NodeChunkStore.Chunk data,
        Vector3I origin, ArrayMesh rendered)
    {
        if (!_shaped)
        {
            scene.CollisionShape.Shape = _vertices.Count > 0 ? rendered.CreateTrimeshShape() : null;
            return;
        }

        _collisionVertices.Clear();
        Span<Vector3> corners = stackalloc Vector3[4];

        for (int lx = 0; lx < ChunkSize; lx++)
        {
            for (int ly = 0; ly < ChunkSize; ly++)
            {
                for (int lz = 0; lz < ChunkSize; lz++)
                {
                    if (data.Get(NodeChunkStore.LocalIndex(lx, ly, lz)) == NodeChunkStore.Air)
                        continue;

                    var cell = new Vector3I(origin.X + lx, origin.Y + ly, origin.Z + lz);

                    // The hull is built from cube faces, so only the six face
                    // neighbours can hide one. Cheaper than the full enclosure
                    // test above and sufficient here.
                    if (FaceEnclosed(cell))
                        continue;
                    Vector3 min = new Vector3(cell.X, cell.Y, cell.Z) * _nodeSize;
                    Vector3 max = min + Vector3.One * _nodeSize;

                    foreach (var face in CubeFaces)
                    {
                        if (_store.Has(cell + face.Dir))
                            continue;

                        FaceCorners(face, min, max, corners);
                        _collisionVertices.Add(corners[0]);
                        _collisionVertices.Add(corners[1]);
                        _collisionVertices.Add(corners[2]);
                        _collisionVertices.Add(corners[0]);
                        _collisionVertices.Add(corners[2]);
                        _collisionVertices.Add(corners[3]);
                    }
                }
            }
        }

        if (_collisionVertices.Count == 0)
        {
            scene.CollisionShape.Shape = null;
            return;
        }

        // Reusing the shape instance rather than allocating a new one lets the
        // physics server update in place. Assigning Shape only once — on the
        // first build — matters too: re-assigning re-registers the shape with
        // the physics server, where writing Data updates it in place.
        if (scene.Trimesh == null)
        {
            scene.Trimesh = new ConcavePolygonShape3D();
            scene.Trimesh.Data = _collisionVertices.ToArray();
            scene.CollisionShape.Shape = scene.Trimesh;
        }
        else
        {
            scene.Trimesh.Data = _collisionVertices.ToArray();
        }
    }

    private readonly List<Vector3> _collisionVertices = new();

    /// <summary>
    /// Emits one bismuth node by copying its prebuilt variant into the mesh.
    ///
    /// This is the whole per-node cost at planet scale: six noise samples to
    /// find the variant, then a straight copy of that variant's quads with a
    /// scale and a translate. No geometry is solved here and no neighbouring
    /// node is consulted for SHAPE — the face field already guarantees the
    /// shapes interlock.
    /// </summary>
    private void AddShapedNode(Vector3I cell, NodeMaterial material, Vector3 min, Color color)
    {
        INodeType type = TypeOf(material);
        NodeMesh variant = type.MeshFor(type.ShapeAt(cell));
        if (variant.Vertices.Length == 0)
            return;

        float quarter = _nodeSize / RawNodeGeometry.Sub;

        // Vertices arrive grouped four per quad, matching the variant's index
        // runs of six, so quads can be skipped without re-indexing anything.
        int quadCount = variant.Vertices.Length / 4;
        for (int q = 0; q < quadCount; q++)
        {
            int source = q * 4;
            if (IsBuried(cell, variant, q))
                continue;

            int start = _vertices.Count;
            for (int v = 0; v < 4; v++)
            {
                _vertices.Add(min + variant.Vertices[source + v] * quarter);
                _normals.Add(variant.Normals[source + v]);
                _colors.Add(color);
            }

            _indices.Add(start);
            _indices.Add(start + 1);
            _indices.Add(start + 2);
            _indices.Add(start);
            _indices.Add(start + 2);
            _indices.Add(start + 3);
        }
    }

    /// <summary>
    /// Is every quarter-cell in front of this quad filled? Only then is the
    /// quad safe to drop.
    ///
    /// Testing that a neighbouring BLOCK merely exists is not enough: under
    /// edge-and-corner growth a rim can retreat inward, leaving the shared
    /// boundary exposed even with a solid node next door, and culling on
    /// presence alone tears visible holes.
    /// </summary>
    private bool IsBuried(Vector3I cell, NodeMesh variant, int quad)
    {
        int from = variant.OccludedStart[quad];
        int to = variant.OccludedStart[quad + 1];
        if (from == to)
            return false;

        for (int c = from; c < to; c += 3)
        {
            if (!_occupancy.Solid(cell,
                    variant.OccludedCells[c],
                    variant.OccludedCells[c + 1],
                    variant.OccludedCells[c + 2]))
                return false;
        }

        return true;
    }

    /// <summary>Emits one quad, wound so it faces along `normal`.</summary>
    private void AddFace(Color color, Span<Vector3> corners, Vector3 normal)
    {
        Vector3 v0 = corners[0], v1 = corners[1], v2 = corners[2], v3 = corners[3];

        // Godot treats clockwise winding as front-facing.
        if ((v2 - v0).Cross(v1 - v0).Dot(normal) < 0f)
            (v1, v3) = (v3, v1);

        int start = _vertices.Count;
        _vertices.Add(v0);
        _vertices.Add(v1);
        _vertices.Add(v2);
        _vertices.Add(v3);
        for (int k = 0; k < 4; k++)
        {
            _normals.Add(normal);
            _colors.Add(color);
        }

        _indices.Add(start);
        _indices.Add(start + 1);
        _indices.Add(start + 2);
        _indices.Add(start);
        _indices.Add(start + 2);
        _indices.Add(start + 3);
    }
}
