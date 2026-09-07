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
        CrystalMask(cell, out uint low, out uint high);
        int orientation = OrientationOf(cell, type);
        int[] local = type.OccupiedCells(
            type.ShapeAt(cell, LocalMask(cell, type), low, high, orientation));

        // Rotated so the editor's highlight traces the shape actually drawn.
        var world = new int[local.Length];
        for (int c = 0; c < local.Length; c += 3)
        {
            NodeOrientation.ToWorld(orientation, RawNodeGeometry.Sub,
                local[c], local[c + 1], local[c + 2],
                out world[c], out world[c + 1], out world[c + 2]);
        }

        return world;
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
    /// <summary>
    /// Cells per edge of a MESH SECTION — the unit geometry is rebuilt in.
    ///
    /// Deliberately smaller than a chunk. A chunk is the right size for
    /// STREAMING, where the per-chunk overhead of a scene node, a dictionary
    /// entry and a store lookup is what matters, and 32 keeps that overhead
    /// 64x lower than the 8 it replaced. It is exactly the wrong size for
    /// EDITING, where the cost is the volume rebuilt: mining one node meant
    /// re-walking a 32-cell chunk and stamping the 36-cell occupancy window
    /// around it, 46656 cells to change one.
    ///
    /// Sections separate the two. Storage and streaming still work in chunks;
    /// geometry is rebuilt in 8-cell sections, so an edit touches a 12-cell
    /// window — a twenty-seventh of the volume.
    /// </summary>
    public const int SectionSize = 8;

    /// <summary>Sections per chunk edge.</summary>
    private const int SectionsPerChunk = ChunkSize / SectionSize;

    /// <summary>The section containing a cell, in section coordinates.</summary>
    private static Vector3I SectionOf(Vector3I cell) => new(
        FloorDiv(cell.X, SectionSize), FloorDiv(cell.Y, SectionSize),
        FloorDiv(cell.Z, SectionSize));

    /// <summary>The min corner of a section, in cells.</summary>
    private static Vector3I SectionOrigin(Vector3I section) => new(
        section.X * SectionSize, section.Y * SectionSize, section.Z * SectionSize);

    /// <summary>One section's scene nodes, created on demand.</summary>
    private sealed class SectionNodes
    {
        public readonly MeshInstance3D MeshInstance;
        public readonly CollisionShape3D CollisionShape;
        public ConcavePolygonShape3D Trimesh;

        public SectionNodes(Node parent, Vector3I coord)
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

    private readonly Dictionary<Vector3I, SectionNodes> _sections = new();

    /// <summary>Sections whose geometry no longer matches the store.</summary>
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

    /// <summary>Frees the scene nodes of sections whose chunk no longer holds
    /// anything.</summary>
    private void DiscardEmptyChunks()
    {
        _scratch.Clear();
        foreach (var kv in _sections)
        {
            NodeChunkStore.Chunk data = _store.Find(ChunkOf(SectionOrigin(kv.Key)));
            if (data == null || data.SolidCount == 0)
                _scratch.Add(kv.Key);
        }

        foreach (Vector3I dead in _scratch)
        {
            _sections[dead].Dispose();
            _sections.Remove(dead);
        }
    }

    /// <summary>Queues every section of a chunk for meshing.</summary>
    private void QueueChunkSections(Vector3I chunk)
    {
        Vector3I baseSection = new(
            chunk.X * SectionsPerChunk,
            chunk.Y * SectionsPerChunk,
            chunk.Z * SectionsPerChunk);

        for (int x = 0; x < SectionsPerChunk; x++)
            for (int y = 0; y < SectionsPerChunk; y++)
                for (int z = 0; z < SectionsPerChunk; z++)
                    _dirty.Add(new Vector3I(
                        baseSection.X + x, baseSection.Y + y, baseSection.Z + z));
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
                    _dirty.Add(SectionOf(cell + new Vector3I(dx, dy, dz)));
    }

    private StandardMaterial3D _material;

    /// <summary>
    /// Everything one section build writes into.
    ///
    /// Bundled rather than left as fields on the world because building the
    /// GEOMETRY of a section is now done on worker threads: it is pure
    /// computation over the store, touching no engine object, and it is 98% of
    /// the cost of streaming a chunk. Several workers can be inside it at
    /// once, so the buffers cannot be shared -- each job carries its own.
    ///
    /// Measured before this change: geometry 7.33ms per section against 0.03ms
    /// to upload the mesh and 0.09ms to update collision. A chunk is 64
    /// sections, so a frame that meshed two chunks spent close to a second
    /// inside this code while the game waited.
    /// </summary>
    private sealed class MeshScratch
    {
        public readonly List<Vector3> Vertices = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Color> Colors = new();
        public readonly List<int> Indices = new();
        public readonly List<Vector3> CollisionVertices = new();

        /// <summary>
        /// The occupancy window for this build.
        ///
        /// A fixed-size buffer that <see cref="ChunkOccupancy.Reset"/> clears,
        /// so a worker allocates one for its lifetime rather than one per
        /// section.
        /// </summary>
        public readonly ChunkOccupancy Occupancy = new();

        public void Clear()
        {
            Vertices.Clear();
            Normals.Clear();
            Colors.Clear();
            Indices.Clear();
            CollisionVertices.Clear();
        }
    }

    /// <summary>The scratch main-thread meshing uses, reused across sections.</summary>
    private readonly MeshScratch _scratchMesh = new();

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
            if (kv.Value.SolidCount == 0)
                continue;

            Vector3I baseSection = new(
                kv.Key.X * SectionsPerChunk,
                kv.Key.Y * SectionsPerChunk,
                kv.Key.Z * SectionsPerChunk);

            for (int x = 0; x < SectionsPerChunk; x++)
                for (int y = 0; y < SectionsPerChunk; y++)
                    for (int z = 0; z < SectionsPerChunk; z++)
                        _pending.Add(new Vector3I(
                            baseSection.X + x, baseSection.Y + y, baseSection.Z + z));
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
            MeshSection(_pending[_pendingIndex]);
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
        _meshed.Clear();

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
        Vector3I baseSection = new(
            chunk.X * SectionsPerChunk,
            chunk.Y * SectionsPerChunk,
            chunk.Z * SectionsPerChunk);

        for (int x = 0; x < SectionsPerChunk; x++)
            for (int y = 0; y < SectionsPerChunk; y++)
                for (int z = 0; z < SectionsPerChunk; z++)
                {
                    var section = new Vector3I(
                        baseSection.X + x, baseSection.Y + y, baseSection.Z + z);

                    if (_sections.TryGetValue(section, out SectionNodes scene))
                    {
                        scene.Dispose();
                        _sections.Remove(section);
                    }

                    _dirty.Remove(section);
                }

        _store.Unload(chunk);
        _meshed.Remove(chunk);
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
    public bool HasChunkMesh(Vector3I chunk) => _meshed.Contains(chunk);

    /// <summary>
    /// Chunks whose sections have been built.
    ///
    /// Recorded rather than inferred from whether any scene node exists,
    /// because a chunk can legitimately mesh to NOTHING — every section empty,
    /// or every face buried by a neighbour. Judging by scene nodes told the
    /// streamer such a chunk still owed geometry, so it re-queued it on every
    /// rescan and the world never finished loading.
    /// </summary>
    private readonly HashSet<Vector3I> _meshed = new();

    /// <summary>
    /// Queues a chunk to be re-meshed. For the streamer, which installs chunk
    /// data directly into the store and then asks for geometry.
    /// </summary>
    public void QueueChunkMesh(Vector3I chunk)
    {
        QueueChunkSections(chunk);
        _meshed.Add(chunk);
    }

    /// <summary>
    /// Records a chunk as meshed without building anything.
    ///
    /// For chunks a generator has rejected analytically: they hold nothing, so
    /// there is no geometry to make, but everything downstream still has to see
    /// them as done. Leaving one unrecorded makes it invisible to the readiness
    /// check, which then waits on it forever.
    /// </summary>
    public void MarkChunkMeshed(Vector3I chunk) => _meshed.Add(chunk);

    /// <summary>
    /// Meshes everything queued by <see cref="QueueChunkMesh"/>.
    ///
    /// Separate from the queueing so the streamer can decide its own pacing:
    /// it queues under its per-frame budget and flushes, rather than having
    /// each queued chunk trigger a rebuild of its own.
    /// </summary>
    public bool HasQueuedMeshes => _dirty.Count > 0;

    public void FlushQueuedMeshes()
    {
        if (_dirty.Count == 0)
            return;

        WarmTypeCache();
        RebuildDirty();
    }

    /// <summary>
    /// Meshes some of what is queued and returns whether anything is left.
    ///
    /// The streaming form of <see cref="FlushQueuedMeshes"/>. Queueing a chunk
    /// dirties its 64 sections, and meshing all of them in one call is what a
    /// frame cannot afford: even spread over worker threads the batch lands as
    /// a single visible hitch, because the frame cannot end until the last
    /// section is installed.
    ///
    /// So a call takes only `budgetMs` worth, in parallel groups, and leaves
    /// the rest dirty for the next frame. The work per frame is bounded by
    /// TIME rather than by section count, which is the only bound that holds
    /// when sections differ in cost by an order of magnitude.
    /// </summary>
    public bool FlushQueuedMeshes(float budgetMs)
    {
        if (_dirty.Count == 0)
            return false;

        WarmTypeCache();

        ulong deadline = Time.GetTicksUsec() + (ulong)(Mathf.Max(1f, budgetMs) * 1000f);

        // A group per pass, so the deadline is consulted between groups rather
        // than only at the end of the batch. Sized to the thread count: fewer
        // would leave cores idle, many more would overshoot the budget by a
        // whole group's worth.
        int group = MeshThreads;

        while (_dirty.Count > 0)
        {
            MeshDirtyGroup(group);

            if (Time.GetTicksUsec() >= deadline)
                break;
        }

        return _dirty.Count > 0;
    }

    /// <summary>
    /// Builds up to `limit` dirty sections in parallel and installs them.
    /// </summary>
    private void MeshDirtyGroup(int limit)
    {
        int count = Mathf.Min(limit, _dirty.Count);
        if (count <= 0)
            return;

        if (_parallelSections.Length < count)
            _parallelSections = new Vector3I[count * 2];
        if (_parallelScratch.Length < count)
            System.Array.Resize(ref _parallelScratch, count * 2);

        int n = 0;
        foreach (Vector3I section in _dirty)
        {
            _parallelSections[n++] = section;
            if (n == count)
                break;
        }

        for (int i = 0; i < n; i++)
        {
            _dirty.Remove(_parallelSections[i]);
            _parallelScratch[i] ??= new MeshScratch();
        }

        BuildAndApply(n);
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
                QueueChunkSections(kv.Key);
        }

        foreach (Vector3I section in _dirty)
            MeshSection(section);

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
    /// Re-meshes only the sections marked dirty.
    ///
    /// A one-node edit reaches two cells, so it dirties the sections within
    /// that radius — one when the edit is well inside a section, up to eight
    /// when it sits on a corner. Each is an 8-cell cube rather than a 32-cell
    /// chunk, which is what keeps mining a node cheap: the occupancy window
    /// around a section is a twenty-seventh of the volume of one around a
    /// chunk.
    /// </summary>
    private void RebuildDirty()
    {
        if (_dirty.Count == 0)
            return;

        WarmTypeCache();

        // ONE SECTION: not worth a thread. Handing a single 7ms job to the
        // pool costs more in scheduling and hand-back than it saves, and this
        // is the common case for an edit.
        if (_dirty.Count == 1)
        {
            foreach (Vector3I only in _dirty)
                MeshSection(only);

            _dirty.Clear();
            return;
        }

        MeshSectionsParallel();
        _dirty.Clear();
    }

    /// <summary>
    /// Builds every dirty section's geometry across worker threads, then
    /// installs the results on the calling thread.
    ///
    /// WHY THIS IS THE FIX FOR THE FRAME SPIKES
    ///
    /// Streaming one chunk means meshing its 64 sections, and geometry was
    /// measured at 7.33ms of pure computation each. Done in sequence on the
    /// main thread that is close to half a second with the game frozen for all
    /// of it: the frame profile showed a healthy 6.9ms median but a 771ms 99th
    /// percentile and a 912ms worst frame.
    ///
    /// The work parallelises cleanly because a section build reads the node
    /// store (immutable while a build runs) and writes only into its own
    /// scratch. Nothing touches an engine object until the results come back,
    /// and that half is cheap: 0.03ms to upload a mesh and 0.09ms to update
    /// collision, so the main thread keeps the part it must own and sheds the
    /// 98% it never needed to do.
    /// </summary>
    private void MeshSectionsParallel()
    {
        int count = _dirty.Count;
        if (_parallelSections.Length < count)
            _parallelSections = new Vector3I[count * 2];
        if (_parallelScratch.Length < count)
            System.Array.Resize(ref _parallelScratch, count * 2);

        int n = 0;
        foreach (Vector3I section in _dirty)
            _parallelSections[n++] = section;

        // A scratch per SLOT rather than per thread, reused across rebuilds:
        // the buffers grow to the largest section they have held and then stop
        // allocating, and the partitioner never gives one slot to two threads.
        for (int i = 0; i < n; i++)
            _parallelScratch[i] ??= new MeshScratch();

        BuildAndApply(n);
    }

    /// <summary>
    /// Builds the first `n` staged sections across worker threads, then
    /// installs them on the calling thread.
    /// </summary>
    private void BuildAndApply(int n)
    {
        Vector3I[] sections = _parallelSections;
        MeshScratch[] scratch = _parallelScratch;

        System.Threading.Tasks.Parallel.For(0, n, new System.Threading.Tasks.ParallelOptions
        {
            // Leave the machine something. Meshing is background work with a
            // deadline in frames, not the only thing the player is running,
            // and saturating every core is what made the streamer unusable
            // before.
            MaxDegreeOfParallelism = MeshThreads,
        },
        i => BuildSectionGeometry(sections[i], scratch[i]));

        // MAIN THREAD: the rendering and physics servers are not thread-safe,
        // so every engine call happens here, in a plain loop over finished
        // geometry.
        for (int i = 0; i < n; i++)
            ApplySectionGeometry(sections[i], scratch[i]);
    }

    /// <summary>
    /// How many threads section geometry may use.
    ///
    /// Capped rather than taking every core: chunk generation is already
    /// running its own workers, and the two together saturating the machine is
    /// what made an earlier version of the streamer stop unrelated
    /// applications dead.
    /// </summary>
    private static int MeshThreads =>
        Mathf.Clamp(System.Environment.ProcessorCount - 2, 1, 6);

    private Vector3I[] _parallelSections = new Vector3I[64];
    private MeshScratch[] _parallelScratch = new MeshScratch[64];

    /// <summary>
    /// Builds one chunk's mesh and collision straight from the store.
    ///
    /// No node list is passed in any more. The chunk IS the list — a dense
    /// array of its own cells — so the mesher walks it directly instead of
    /// being handed the result of bucketing every node in the world.
    /// </summary>
    private void MeshSection(Vector3I section)
    {
        BuildSectionGeometry(section, _scratchMesh);
        ApplySectionGeometry(section, _scratchMesh);
    }

    /// <summary>
    /// Builds one section's vertices and collision hull into `scratch`.
    ///
    /// PURE COMPUTATION -- no engine object is created or touched, so this can
    /// run on any thread. It reads the node store, which is immutable while a
    /// mesh job is outstanding, and writes only into the scratch it was given.
    /// Everything that talks to the rendering or physics server lives in
    /// <see cref="ApplySectionGeometry"/> instead.
    /// </summary>
    private void BuildSectionGeometry(Vector3I section, MeshScratch scratch)
    {
        Vector3I origin = SectionOrigin(section);

        scratch.Clear();

        if (_shaped)
        {
            scratch.Occupancy.Reset(origin);
            scratch.Occupancy.Fill(_store, _typeLookup ?? TypeOf, _radialUp, _gravityCentre);
        }

        // Allocated once outside the loop: a stackalloc per node would grow
        // the stack frame by every iteration and eventually overflow.
        Span<Vector3> corners = stackalloc Vector3[4];

        for (int lx = 0; lx < SectionSize; lx++)
        {
            for (int ly = 0; ly < SectionSize; ly++)
            {
                for (int lz = 0; lz < SectionSize; lz++)
                {
                    var cell = new Vector3I(origin.X + lx, origin.Y + ly, origin.Z + lz);

                    // A section may straddle chunks that are not all resident,
                    // so it reads through the store rather than one chunk's
                    // array.
                    byte raw = _store.Get(cell);
                    if (raw == NodeChunkStore.Air)
                        continue;

                    // ENCLOSED — every neighbour that could expose a face is
                    // solid, so nothing this node emits can be seen.
                    //
                    // Measured at 76% of the nodes a chunk meshes: the inside
                    // of an island is most of its volume, and every one of
                    // those nodes was having its shape solved, its quads
                    // walked and each quad's occlusion cells tested, only to
                    // contribute nothing. Testing 26 bytes first is far
                    // cheaper than the work it avoids.
                    if (_shaped && Enclosed(cell, scratch))
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
                        AddShapedNode(cell, material, min, color, scratch);
                        continue;
                    }

                    foreach (var face in CubeFaces)
                    {
                        if (_store.Has(cell + face.Dir))
                            continue;
                        FaceCorners(face, min, max, corners);
                        AddFace(color, corners, new Vector3(face.Dir.X, face.Dir.Y, face.Dir.Z), scratch);
                    }
                }
            }
        }

        BuildCollisionHull(origin, scratch);
    }

    /// <summary>
    /// Hands finished geometry to the rendering and physics servers.
    ///
    /// MAIN THREAD ONLY -- this is the half that creates engine objects. It is
    /// also the cheap half: measured at 0.03ms to upload a section's mesh and
    /// 0.09ms to update its collision, against 7.33ms to compute them.
    /// </summary>
    private void ApplySectionGeometry(Vector3I section, MeshScratch scratch)
    {
        // Nothing to draw: drop the scene nodes rather than leaving an empty
        // mesh behind, so a section carved away stops costing anything.
        if (scratch.Vertices.Count == 0)
        {
            if (_sections.TryGetValue(section, out SectionNodes empty))
            {
                empty.Dispose();
                _sections.Remove(section);
            }

            return;
        }

        if (!_sections.TryGetValue(section, out SectionNodes target))
        {
            target = new SectionNodes(this, section);
            _sections[section] = target;
        }

        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = scratch.Vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = scratch.Normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = scratch.Colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = scratch.Indices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // One shared material rather than a fresh one per rebuild: a new
        // StandardMaterial3D every edit means a new shader instance and a
        // cold pipeline cache each time.
        mesh.SurfaceSetMaterial(0, SharedMaterial);

        target.MeshInstance.Mesh = mesh;
        ApplyCollision(target, mesh, scratch);
    }

    /// <summary>Are all 26 surrounding cells solid?</summary>
    private bool AllNeighboursSolid(Vector3I cell)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0)
                        continue;

                    if (!_store.Has(new Vector3I(cell.X + dx, cell.Y + dy, cell.Z + dz)))
                        return false;
                }

        return true;
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
    private bool Enclosed(Vector3I cell, MeshScratch scratch)
    {
        const int Sub = RawNodeGeometry.Sub;

        // CHEAP TEST FIRST.
        //
        // The shell scan below is 14x14x14 occupancy probes per node, and the
        // walk runs it on every solid cell of a section. A node with any empty
        // neighbour cannot possibly be enclosed, and twenty-six byte reads
        // settle that far faster than 2744 bit tests.
        //
        // Necessary, not sufficient: a node can have all 26 neighbours present
        // and still show a face, because a neighbour's rim may have retreated
        // out of the space between them. So this only rejects -- when it says
        // "maybe", the full shell scan still decides.
        //
        // Measured: the walk fell from 293 seconds to a fraction of it, and
        // the same test in ChunkOccupancy.Fill took that phase from 330
        // seconds to 20.
        if (!AllNeighboursSolid(cell))
            return false;

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

                    if (!scratch.Occupancy.Solid(cell, i, j, k))
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
    /// Builds collision from the BLOCK HULL, not the rendered bismuth
    /// surface. The physics engine builds a BVH over every triangle it is
    /// given, and crystal rims multiply that count for relief no player can
    /// feel through a capsule — plain cube faces are ~10x fewer triangles.
    ///
    /// Pure computation into `scratch`, so it runs on the worker alongside the
    /// render geometry; <see cref="ApplyCollision"/> installs the result.
    /// </summary>
    private void BuildCollisionHull(Vector3I origin, MeshScratch scratch)
    {
        // An unshaped world collides against the rendered mesh itself, which
        // does not exist until the main thread uploads it. Nothing to
        // precompute here; ApplyCollision handles that case.
        if (!_shaped)
            return;

        Span<Vector3> corners = stackalloc Vector3[4];

        for (int lx = 0; lx < SectionSize; lx++)
        {
            for (int ly = 0; ly < SectionSize; ly++)
            {
                for (int lz = 0; lz < SectionSize; lz++)
                {
                    var cell = new Vector3I(origin.X + lx, origin.Y + ly, origin.Z + lz);
                    if (!_store.Has(cell))
                        continue;

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
                        scratch.CollisionVertices.Add(corners[0]);
                        scratch.CollisionVertices.Add(corners[1]);
                        scratch.CollisionVertices.Add(corners[2]);
                        scratch.CollisionVertices.Add(corners[0]);
                        scratch.CollisionVertices.Add(corners[2]);
                        scratch.CollisionVertices.Add(corners[3]);
                    }
                }
            }
        }

    }

    /// <summary>
    /// Installs the hull built by <see cref="BuildCollisionHull"/>.
    ///
    /// MAIN THREAD ONLY: everything here touches the physics server.
    /// </summary>
    private void ApplyCollision(SectionNodes scene, ArrayMesh rendered, MeshScratch scratch)
    {
        // An unshaped world collides against the rendered mesh itself, which
        // only exists once the mesh has been uploaded, so there is nothing for
        // the worker to precompute.
        if (!_shaped)
        {
            scene.CollisionShape.Shape = rendered.CreateTrimeshShape();
            return;
        }

        if (scratch.CollisionVertices.Count == 0)
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
            scene.Trimesh.Data = scratch.CollisionVertices.ToArray();
            scene.CollisionShape.Shape = scene.Trimesh;
        }
        else
        {
            scene.Trimesh.Data = scratch.CollisionVertices.ToArray();
        }
    }

    /// <summary>
    /// Emits one bismuth node by copying its prebuilt variant into the mesh.
    ///
    /// This is the whole per-node cost at planet scale: six noise samples to
    /// find the variant, then a straight copy of that variant's quads with a
    /// scale and a translate. No geometry is solved here and no neighbouring
    /// node is consulted for SHAPE — the face field already guarantees the
    /// shapes interlock.
    /// </summary>
    private void AddShapedNode(Vector3I cell, NodeMaterial material, Vector3 min, Color color,
        MeshScratch scratch)
    {
        INodeType type = TypeOf(material);
        CrystalMask(cell, out uint crystalLow, out uint crystalHigh);

        // The shape is DECIDED in the node's own frame and DRAWN in the
        // world's. Rebasing the mask picks the right shape for a node on the
        // side of a planet; rotating the vertices below is what actually points
        // it away from the core. Doing only the first would leave soil deciding
        // as though it were on a wall while still bevelling toward world up.
        int orientation = OrientationOf(cell, type);
        NodeMesh variant = type.MeshFor(
            type.ShapeAt(cell, LocalMask(cell, type), crystalLow, crystalHigh, orientation));
        if (variant.Vertices.Length == 0)
            return;

        float quarter = _nodeSize / RawNodeGeometry.Sub;

        // Vertices arrive grouped four per quad, matching the variant's index
        // runs of six, so quads can be skipped without re-indexing anything.
        int quadCount = variant.Vertices.Length / 4;
        for (int q = 0; q < quadCount; q++)
        {
            int source = q * 4;
            if (IsBuried(cell, variant, q, scratch, orientation))
                continue;

            int start = scratch.Vertices.Count;
            for (int v = 0; v < 4; v++)
            {
                scratch.Vertices.Add(min + NodeOrientation.ToWorld(
                    orientation, RawNodeGeometry.Sub, variant.Vertices[source + v]) * quarter);
                scratch.Normals.Add(NodeOrientation.DirectionToWorld(
                    orientation, variant.Normals[source + v]));
                scratch.Colors.Add(color);
            }

            scratch.Indices.Add(start);
            scratch.Indices.Add(start + 1);
            scratch.Indices.Add(start + 2);
            scratch.Indices.Add(start);
            scratch.Indices.Add(start + 2);
            scratch.Indices.Add(start + 3);
        }
    }

    /// <summary>
    /// Which of a cell's six face neighbours hold something solid, as a bitmask
    /// indexed by <see cref="NodeFace"/>.
    ///
    /// For node types whose shape follows the SURFACE rather than the rock —
    /// soil rounding off where it meets air and stepping up where it meets more
    /// soil. Such a type cannot answer from position alone: where the ground
    /// ends is what the generator decided, not something a node can evaluate
    /// for itself.
    ///
    /// Six store reads, each a cached chunk lookup and an array index, and the
    /// mesher is already reading these same cells to cull buried faces. The
    /// shape stays a pure function of its inputs, so it remains cacheable and
    /// safe on any thread.
    /// </summary>
    private int NeighbourMask(Vector3I cell)
    {
        int mask = 0;
        for (int face = 0; face < NodeFace.Offsets.Length; face++)
        {
            if (_store.Has(cell + NodeFace.Offsets[face]))
                mask |= 1 << face;
        }

        return mask;
    }

    /// <summary>
    /// Treat "up" as pointing away from <see cref="GravityCentre"/> rather than
    /// along world +Y.
    ///
    /// Off for a flat world, which is what every existing level wants. On for a
    /// planet, where the surface has no single up and soil capping the wrong
    /// face is the difference between ground you walk on and ground you walk
    /// around.
    /// </summary>
    [ExportGroup("Gravity")]
    [Export]
    public bool RadialUp
    {
        get => _radialUp;
        set { _radialUp = value; RebuildIfReady(); }
    }

    private bool _radialUp;

    /// <summary>What node geometry falls toward when <see cref="RadialUp"/> is
    /// on. The planet sits on the origin.</summary>
    [Export]
    public Vector3 GravityCentre
    {
        get => _gravityCentre;
        set { _gravityCentre = value; RebuildIfReady(); }
    }

    private Vector3 _gravityCentre = Vector3.Zero;

    /// <summary>Which of the six axis directions is up for this cell.</summary>
    private int OrientationOf(Vector3I cell, INodeType type) =>
        _radialUp && type.FollowsGravity
            ? NodeOrientation.Facing(cell, _gravityCentre)
            : NodeOrientation.PosY;

    /// <summary>
    /// The neighbour mask as the SHAPE RULE should read it: rewritten so the
    /// direction pointing away from the planet plays the part of +Y.
    ///
    /// Every shape rule is written for a flat world and says "the cell above",
    /// "the sides facing air". Rebasing the mask keeps that vocabulary intact
    /// while changing which world directions it refers to, so the rules need no
    /// changes and stay a table lookup.
    /// </summary>
    private int LocalMask(Vector3I cell, INodeType type)
    {
        int orientation = OrientationOf(cell, type);
        int mask = NodeOrientation.Rebase(NeighbourMask(cell), orientation);

        if (!_radialUp || !type.FollowsGravity)
            return mask;

        // A SPHERE IS NOT A HILLSIDE.
        //
        // Carved from cubes, a sphere's surface is a staircase: walk round it
        // and the ground steps down about once per node, entirely from
        // curvature. The soil rule cannot tell that from real terrain -- it
        // sees a solid uphill neighbour with another solid cell above it, which
        // is its definition of rising ground -- so it adds a lip and bevels the
        // downhill side on almost every node.
        //
        // Measured on a perfectly smooth sphere, that fired on 7% of surface
        // nodes near an axis and 84% at 40 degrees from it, which is the tilted
        // stepped look a flat planet should not have.
        //
        // The world knows what the node cannot: whether a step is terrain or
        // curvature.
        //
        // A SIDE COUNTS AS GROUND IF THE CRUST CONTINUES THAT WAY, even when
        // the particular cell beside this one happens to be empty because the
        // staircase steps down there. What decides it is depth: the neighbour
        // one step down-and-across is at the same depth this node is, so if
        // THAT is solid the ground continues and there is no edge to bevel.
        //
        // The result is that only a genuine drop -- terrain, or a mined hole --
        // exposes a side, which is what the bevel was written for.
        int fixedMask = mask;
        Vector3I up = NodeOrientation.UpOf(orientation);

        for (int face = 0; face < 4; face++)
        {
            int bit = face switch
            {
                0 => NodeFace.NegX,
                1 => NodeFace.PosX,
                2 => NodeFace.NegZ,
                _ => NodeFace.PosZ,
            };

            if (NodeFace.Has(mask, bit))
                continue;

            // The cell beside this one, one step further in: where the crust
            // continues when the surface is merely curving away.
            Vector3I side = NodeOrientation.FaceOffset(orientation, bit);
            if (_store.Has(cell + side - up))
                fixedMask |= 1 << bit;
        }

        // The rise is cleared outright. Its whole job is to bridge a step UP,
        // and on a sphere every apparent step up is curvature.
        return fixedMask & ~(1 << NodeFace.UpNegX | 1 << NodeFace.UpPosX
                           | 1 << NodeFace.UpNegZ | 1 << NodeFace.UpPosZ);
    }

    /// <summary>
    /// Which of the 26 cells around this one hold CRYSTAL, as 26 bits split
    /// across two words.
    ///
    /// For materials that step aside where a crystal rim grows into them.
    /// Whether a rim reaches in is a pure function of position and the node
    /// type can recompute it, but whether there is rock there at all is what
    /// the generator placed — only the mesher knows that. Without it a soil
    /// node carves itself against imaginary crystal on every side and the
    /// whole field is indented rather than just the rock boundary.
    /// </summary>
    private void CrystalMask(Vector3I cell, out uint low, out uint high)
    {
        low = 0u;
        high = 0u;

        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0)
                        continue;

                    byte material = _store.Get(
                        new Vector3I(cell.X + dx, cell.Y + dy, cell.Z + dz));

                    if (material == NodeChunkStore.Air
                        || NodeMaterials.TypeIdOf((NodeMaterial)material) != "raw")
                        continue;

                    int bit = NodeFace.NeighbourBit(dx, dy, dz);
                    if (bit < 32)
                        low |= 1u << bit;
                    else
                        high |= 1u << (bit - 32);
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
    private bool IsBuried(Vector3I cell, NodeMesh variant, int quad, MeshScratch scratch,
        int orientation)
    {
        int from = variant.OccludedStart[quad];
        int to = variant.OccludedStart[quad + 1];
        if (from == to)
            return false;

        for (int c = from; c < to; c += 3)
        {
            // The occlusion cells describe the shape in ITS frame, and the
            // occupancy map is in the world's, so they have to be rotated the
            // same way the vertices are. Probing the unrotated cells would test
            // a quad against space on the wrong side of the node.
            NodeOrientation.ToWorld(orientation, RawNodeGeometry.Sub,
                variant.OccludedCells[c],
                variant.OccludedCells[c + 1],
                variant.OccludedCells[c + 2],
                out int i, out int j, out int k);

            if (!scratch.Occupancy.Solid(cell, i, j, k))
                return false;
        }

        return true;
    }

    /// <summary>Emits one quad, wound so it faces along `normal`.</summary>
    private void AddFace(Color color, Span<Vector3> corners, Vector3 normal,
        MeshScratch scratch)
    {
        Vector3 v0 = corners[0], v1 = corners[1], v2 = corners[2], v3 = corners[3];

        // Godot treats clockwise winding as front-facing.
        if ((v2 - v0).Cross(v1 - v0).Dot(normal) < 0f)
            (v1, v3) = (v3, v1);

        int start = scratch.Vertices.Count;
        scratch.Vertices.Add(v0);
        scratch.Vertices.Add(v1);
        scratch.Vertices.Add(v2);
        scratch.Vertices.Add(v3);
        for (int k = 0; k < 4; k++)
        {
            scratch.Normals.Add(normal);
            scratch.Colors.Add(color);
        }

        scratch.Indices.Add(start);
        scratch.Indices.Add(start + 1);
        scratch.Indices.Add(start + 2);
        scratch.Indices.Add(start);
        scratch.Indices.Add(start + 2);
        scratch.Indices.Add(start + 3);
    }
}
