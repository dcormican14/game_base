using Godot;
using System;
using System.Collections.Generic;

namespace GameBase.Blocks;

/// <summary>
/// A world-space grid of uniform cubes. Blocks live on a global lattice, so
/// anything placed anywhere lines up with everything else — unlike the bismuth
/// blobs, whose jittered per-instance grids made cells non-uniform and confined
/// edits to a single blob.
///
/// One mesh and one collision shape are rebuilt from the block set, emitting
/// only faces whose neighbour is absent. Blocks alternate colour on all three
/// axes so each cube reads as a discrete solid.
///
/// The block set is the whole state: no generated field underneath, so there
/// is nothing to diff against and every cube is equally editable.
/// </summary>
[Tool]
public partial class BlockWorld : StaticBody3D
{
    private float _blockSize = 1f;
    private Color _colorA = new(0.30f, 0.30f, 0.34f);
    private Color _colorB = new(0.62f, 0.62f, 0.66f);

    private bool _bismuth = true;
    private int _seed = 1337;
    private float _flowScale = 6f;
    private float _roughness = 0.35f;
    private float _growth = 0.7f;

    [Export(PropertyHint.Range, "0.1,4,0.05")]
    public float BlockSize
    {
        get => _blockSize;
        set { _blockSize = Mathf.Max(0.05f, value); RebuildIfReady(); }
    }

    [Export]
    public Color ColorA { get => _colorA; set { _colorA = value; RebuildIfReady(); } }

    [Export]
    public Color ColorB { get => _colorB; set { _colorB = value; RebuildIfReady(); } }

    [ExportGroup("Bismuth")]
    /// <summary>Shape blocks as tiered crystal instead of plain cubes. The
    /// grid, picking and editing are unchanged either way — only the geometry
    /// inside each cell differs.</summary>
    [Export]
    public bool Bismuth { get => _bismuth; set { _bismuth = value; InvalidateField(); } }

    /// <summary>Seed of the flow field. Same seed, same planet.</summary>
    [Export]
    public int Seed { get => _seed; set { _seed = value; InvalidateField(); } }

    /// <summary>Cells per lobe of the flow. Larger means long, lazy currents
    /// through the rock; smaller means a busier, more granular crystal.</summary>
    [Export(PropertyHint.Range, "1,32,0.5")]
    public float FlowScale { get => _flowScale; set { _flowScale = Mathf.Max(0.5f, value); InvalidateField(); } }

    /// <summary>0 lets the flow decide every contest, so growth runs in long
    /// directional currents; 1 makes contests essentially random and the
    /// crystal chaotic.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float Roughness { get => _roughness; set { _roughness = Mathf.Clamp(value, 0f, 1f); InvalidateField(); } }

    /// <summary>How many edge and corner contests are awarded at all. 0 leaves
    /// every rim flat (plain cubes); 1 claims every one.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")]
    public float Growth { get => _growth; set { _growth = Mathf.Clamp(value, 0f, 1f); InvalidateField(); } }

    private BismuthField _field;

    /// <summary>The flow field, rebuilt lazily after any dial changes.</summary>
    private BismuthField Field => _field ??= new BismuthField(_seed, _flowScale, _roughness, _growth);

    private void InvalidateField()
    {
        _field = null;
        RebuildIfReady();
    }

    /// <summary>Number of blocks currently in the world.</summary>
    public int BlockCount => _blocks.Count;

    private readonly HashSet<Vector3I> _blocks = new();

    /// <summary>
    /// Blocks per chunk edge, so an edit re-meshes only the region it touches.
    /// 8 measured fastest of 16/12/8/6 (~3.4 ms per edit vs ~520 ms for a full
    /// rebuild). Smaller is not better: an edit dirties the 3x3x3 of chunks
    /// around it, so at 6 that spans 8 chunks instead of 2.
    /// </summary>
    private const int ChunkSize = 8;

    /// <summary>One chunk's scene nodes, created on demand.</summary>
    private sealed class Chunk
    {
        public readonly MeshInstance3D MeshInstance;
        public readonly CollisionShape3D CollisionShape;
        public ConcavePolygonShape3D Trimesh;

        public Chunk(Node parent, Vector3I coord)
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

    private readonly Dictionary<Vector3I, Chunk> _chunks = new();
    private readonly Dictionary<Vector3I, List<Vector3I>> _chunkBlocks = new();
    private readonly HashSet<Vector3I> _dirty = new();
    private readonly List<Vector3I> _scratch = new();

    // Batch state: whether edits are being deferred, whether one is queued,
    // and whether it needs the whole-world path rather than the dirty one.
    private bool _deferRebuild;
    private bool _rebuildPending;
    private bool _fullRebuildNeeded;

    /// <summary>Chunk containing a cell. Floor division, so negative
    /// coordinates land in the chunk below rather than truncating to zero.</summary>
    private static Vector3I ChunkOf(Vector3I cell) => new(
        BismuthShape.FloorDiv(cell.X, ChunkSize),
        BismuthShape.FloorDiv(cell.Y, ChunkSize),
        BismuthShape.FloorDiv(cell.Z, ChunkSize));

    /// <summary>
    /// A cube's six faces: the neighbour direction that hides the face, and
    /// its four corners as per-axis picks from (min, max). Shared by the plain
    /// cube mesher and the collision hull, which previously hand-wrote the
    /// same twenty-four corner expressions twice.
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

    /// <summary>Sorts every block into its chunk, replacing the previous
    /// buckets.</summary>
    private void BucketBlocksIntoChunks()
    {
        _chunkBlocks.Clear();
        foreach (Vector3I cell in _blocks)
        {
            Vector3I chunk = ChunkOf(cell);
            if (!_chunkBlocks.TryGetValue(chunk, out List<Vector3I> list))
            {
                list = new List<Vector3I>();
                _chunkBlocks[chunk] = list;
            }

            list.Add(cell);
        }
    }

    /// <summary>Frees the scene nodes of chunks that no longer hold blocks.</summary>
    private void DiscardEmptyChunks()
    {
        _scratch.Clear();
        foreach (var kv in _chunks)
        {
            if (!_chunkBlocks.ContainsKey(kv.Key))
                _scratch.Add(kv.Key);
        }

        foreach (Vector3I dead in _scratch)
        {
            _chunks[dead].Dispose();
            _chunks.Remove(dead);
        }
    }

    /// <summary>
    /// Marks every chunk whose mesh a change at `cell` could alter. A block's
    /// bismuth rim reaches one block outward and its neighbours' culling
    /// depends on it, so the 3x3x3 of surrounding cells is what actually needs
    /// re-meshing — not just the chunk the block sits in.
    /// </summary>
    private void MarkDirty(Vector3I cell)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    _dirty.Add(ChunkOf(cell + new Vector3I(dx, dy, dz)));
    }

    private StandardMaterial3D _material;

    private readonly List<Vector3> _vertices = new();
    private readonly List<Vector3> _normals = new();
    private readonly List<Color> _colors = new();
    private readonly List<int> _indices = new();

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
    /// large level can show progress rather than freezing for half a second.
    /// Call instead of Rebuild when a loading screen is driving the wait.
    /// </summary>
    public void BeginIncrementalBuild()
    {
        if (_bismuth)
            BuildOccupancy();

        BucketBlocksIntoChunks();

        _pending.Clear();
        foreach (var kv in _chunkBlocks)
            _pending.Add(kv.Key);
        _pendingIndex = 0;
        _ready = _pending.Count == 0;
        BuildProgress = _ready ? 1f : 0f;
        SetProcess(!_ready);

        if (_ready)
            WorldReady?.Invoke();
    }

    /// <summary>Chunks meshed per frame during an incremental build. Each is
    /// only a few ms, so a handful per frame keeps the loading screen
    /// responsive while still finishing quickly.</summary>
    [Export(PropertyHint.Range, "1,64,1")]
    public int ChunksPerFrame { get; set; } = 6;

    public override void _Process(double delta)
    {
        if (_pendingIndex >= _pending.Count)
        {
            SetProcess(false);
            return;
        }

        int end = Mathf.Min(_pendingIndex + ChunksPerFrame, _pending.Count);
        for (; _pendingIndex < end; _pendingIndex++)
        {
            Vector3I chunk = _pending[_pendingIndex];
            if (_chunkBlocks.TryGetValue(chunk, out List<Vector3I> cells))
                MeshChunk(chunk, cells);
        }

        BuildProgress = _pending.Count == 0 ? 1f : _pendingIndex / (float)_pending.Count;

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
    /// loading bar rather than on the player's first mined block.
    ///
    /// Verified lossless: block set, occupancy and geometry are identical
    /// afterwards.
    /// </summary>
    private void WarmUpEditPath()
    {
        if (_blocks.Count == 0)
            return;

        // Any block will do; take one deterministically so the warm-up is
        // reproducible rather than depending on hash iteration order.
        Vector3I victim = default;
        bool found = false;
        foreach (Vector3I cell in _blocks)
        {
            if (!found || cell.Y > victim.Y ||
                (cell.Y == victim.Y && (cell.X < victim.X ||
                    (cell.X == victim.X && cell.Z < victim.Z))))
            {
                victim = cell;
                found = true;
            }
        }

        if (!found)
            return;

        // Remove and re-add through the ordinary edit path, so exactly the
        // machinery a player edit uses is exercised.
        RemoveBlock(victim);
        AddBlock(victim);
    }

    public override void _Ready()
    {
        // A generator child readies BEFORE this node (Godot readies children
        // first) and may already have started an incremental build. Rebuilding
        // here would throw that away and do the whole world synchronously —
        // the freeze the incremental path exists to avoid.
        if (_pending.Count > 0 || _ready)
            return;

        SetProcess(false);
        Rebuild();
    }

    private void RebuildIfReady()
    {
        if (IsNodeReady())
            Rebuild();
    }

    // ------------------------------------------------------------ block access

    public bool HasBlock(Vector3I cell) => _blocks.Contains(cell);

    /// <summary>Grid cell containing a world-space point.</summary>
    public Vector3I CellAt(Vector3 worldPoint)
    {
        Vector3 local = ToLocal(worldPoint) / _blockSize;
        return new Vector3I(
            Mathf.FloorToInt(local.X),
            Mathf.FloorToInt(local.Y),
            Mathf.FloorToInt(local.Z));
    }

    /// <summary>World-space centre of a cell.</summary>
    public Vector3 CellCentre(Vector3I cell)
    {
        return ToGlobal((new Vector3(cell.X, cell.Y, cell.Z) + Vector3.One * 0.5f) * _blockSize);
    }

    /// <summary>
    /// Runs several edits and meshes once at the end, instead of once per
    /// block. A rebuild costs the same whether one block changed or a hundred,
    /// so any multi-block operation should be wrapped in this.
    /// </summary>
    /// <param name="wholesale">True when the batch rewrites most of the world
    /// (generating a level, clearing it). Queues one full rebuild and skips
    /// per-block dirty marking, which is pure waste when everything is about
    /// to be re-meshed anyway.</param>
    /// <param name="deferMesh">Skip the rebuild entirely and leave the caller
    /// to drive meshing, which is how the incremental loading path works.</param>
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

                // deferMesh leaves the caller to drive meshing (the
                // incremental loading path), so the queued rebuild is dropped.
                // deferMesh hands meshing to the caller (the incremental
                // loading path), so the queued rebuild is dropped — INCLUDING
                // the full-rebuild flag. Leaving that set would make the next
                // single edit take the whole-world path instead of the dirty
                // one, turning the first mined block into a half-second stall.
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
    /// Falls back to a full rebuild when nothing was marked, which is what a
    /// wholesale change like Clear or Fill leaves behind.
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

    public bool AddBlock(Vector3I cell)
    {
        if (!_blocks.Add(cell))
            return false;

        // Skipped when a full rebuild is already queued: marking the 3x3x3
        // around every block during level generation is a million wasted hash
        // operations for a dirty set that is about to be discarded, and the
        // occupancy map is rebuilt wholesale at the end anyway.
        if (!_fullRebuildNeeded)
        {
            if (_bismuth)
                StampOccupancy(cell, 1);
            MarkDirty(cell);
        }

        RebuildOrDefer();
        return true;
    }

    public bool RemoveBlock(Vector3I cell)
    {
        if (!_blocks.Remove(cell))
            return false;

        if (!_fullRebuildNeeded)
        {
            if (_bismuth)
                StampOccupancy(cell, -1);
            MarkDirty(cell);
        }

        RebuildOrDefer();
        return true;
    }

    /// <summary>Fills a solid box of blocks, inclusive of both corners.</summary>
    public void Fill(Vector3I from, Vector3I to)
    {
        var min = new Vector3I(Mathf.Min(from.X, to.X), Mathf.Min(from.Y, to.Y), Mathf.Min(from.Z, to.Z));
        var max = new Vector3I(Mathf.Max(from.X, to.X), Mathf.Max(from.Y, to.Y), Mathf.Max(from.Z, to.Z));
        for (int x = min.X; x <= max.X; x++)
        {
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                    _blocks.Add(new Vector3I(x, y, z));
            }
        }

        _fullRebuildNeeded = true;
        RebuildOrDefer();
    }

    public void Clear()
    {
        _blocks.Clear();
        _fullRebuildNeeded = true;
        RebuildOrDefer();
    }

    // -------------------------------------------------------------- ray picking

    /// <summary>
    /// Steps a ray through the grid and returns the first block it enters,
    /// plus the empty cell it passed through immediately before — the face it
    /// arrived through, which is where a placed block belongs.
    ///
    /// Stepping in cell units (rather than mapping a physics contact point)
    /// keeps picking unambiguous: a raycast hit lands exactly on a face, which
    /// is a cell boundary, and rounding that point can pick either neighbour.
    /// </summary>
    public bool RayPick(Vector3 worldFrom, Vector3 worldDir, float maxDistance,
        out Vector3I hitCell, out Vector3I emptyCell)
    {
        hitCell = default;
        emptyCell = default;

        float step = _blockSize * 0.2f;
        var previous = CellAt(worldFrom);
        bool started = false;

        for (float travelled = 0f; travelled <= maxDistance; travelled += step)
        {
            Vector3I cell = CellAt(worldFrom + worldDir * travelled);
            if (started && cell == previous)
                continue;

            if (_blocks.Contains(cell))
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
    /// Rebuilds every chunk. Use this after a wholesale change; a single block
    /// edit should go through the dirty-chunk path instead, which is what
    /// keeps editing responsive on a large world.
    /// </summary>
    public void Rebuild()
    {
        if (_bismuth)
            BuildOccupancy();

        BucketBlocksIntoChunks();
        DiscardEmptyChunks();

        foreach (var kv in _chunkBlocks)
            MeshChunk(kv.Key, kv.Value);

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
    /// Re-meshes only the chunks marked dirty. A one-block edit touches its
    /// own chunk plus any neighbour whose surface it changes, which is a few
    /// thousand blocks rather than the whole world — the difference between a
    /// half-second freeze and an unnoticeable hitch on a large level.
    /// </summary>
    private void RebuildDirty()
    {
        if (_dirty.Count == 0)
            return;

        // Occupancy is NOT rebuilt here. It is maintained incrementally by
        // AddBlock/RemoveBlock, because rebuilding all ~2.7M quarter-cells for
        // a one-block change measured 228 ms — 98% of the cost of an edit.

        foreach (Vector3I chunk in _dirty)
        {
            if (!_chunkBlocks.TryGetValue(chunk, out List<Vector3I> list))
                list = null;

            // Re-collect this chunk's blocks; the edit may have added or
            // removed some.
            var fresh = new List<Vector3I>();
            Vector3I origin = chunk * ChunkSize;
            for (int x = 0; x < ChunkSize; x++)
                for (int y = 0; y < ChunkSize; y++)
                    for (int z = 0; z < ChunkSize; z++)
                    {
                        var cell = new Vector3I(origin.X + x, origin.Y + y, origin.Z + z);
                        if (_blocks.Contains(cell))
                            fresh.Add(cell);
                    }

            if (fresh.Count == 0)
            {
                _chunkBlocks.Remove(chunk);
                if (_chunks.TryGetValue(chunk, out Chunk empty))
                {
                    empty.Dispose();
                    _chunks.Remove(chunk);
                }

                continue;
            }

            _chunkBlocks[chunk] = fresh;
            MeshChunk(chunk, fresh);
        }

        _dirty.Clear();
    }

    /// <summary>Builds one chunk's mesh and collision from its block list.</summary>
    private void MeshChunk(Vector3I chunk, List<Vector3I> cells)
    {
        _vertices.Clear();
        _normals.Clear();
        _colors.Clear();
        _indices.Clear();

        // Allocated once outside the loop: a stackalloc per block would grow
        // the stack frame by every iteration and eventually overflow.
        Span<Vector3> corners = stackalloc Vector3[4];

        foreach (Vector3I cell in cells)
        {
            Vector3 min = new Vector3(cell.X, cell.Y, cell.Z) * _blockSize;
            Vector3 max = min + Vector3.One * _blockSize;

            // Alternating on all three axes, so no two touching cubes share a
            // shade and every block's shape stays readable.
            Color color = (cell.X + cell.Y + cell.Z) % 2 == 0 ? _colorA : _colorB;

            if (_bismuth)
            {
                AddBismuthBlock(cell, min, color);
                continue;
            }

            foreach (var face in CubeFaces)
            {
                if (_blocks.Contains(cell + face.Dir))
                    continue;
                FaceCorners(face, min, max, corners);
                AddFace(color, corners, new Vector3(face.Dir.X, face.Dir.Y, face.Dir.Z));
            }
        }

        if (!_chunks.TryGetValue(chunk, out Chunk target))
        {
            target = new Chunk(this, chunk);
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
        UpdateCollision(target, cells, mesh);
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
    private void UpdateCollision(Chunk chunk, List<Vector3I> cells, ArrayMesh rendered)
    {
        if (!_bismuth)
        {
            chunk.CollisionShape.Shape = _vertices.Count > 0 ? rendered.CreateTrimeshShape() : null;
            return;
        }

        _collisionVertices.Clear();
        Span<Vector3> corners = stackalloc Vector3[4];
        foreach (Vector3I cell in cells)
        {
            Vector3 min = new Vector3(cell.X, cell.Y, cell.Z) * _blockSize;
            Vector3 max = min + Vector3.One * _blockSize;

            foreach (var face in CubeFaces)
            {
                if (_blocks.Contains(cell + face.Dir))
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

        if (_collisionVertices.Count == 0)
        {
            chunk.CollisionShape.Shape = null;
            return;
        }

        // Reusing the shape instance rather than allocating a new one lets the
        // physics server update in place. Assigning Shape only once — on the
        // first build — matters too: re-assigning re-registers the shape with
        // the physics server, where writing Data updates it in place.
        if (chunk.Trimesh == null)
        {
            chunk.Trimesh = new ConcavePolygonShape3D();
            chunk.Trimesh.Data = _collisionVertices.ToArray();
            chunk.CollisionShape.Shape = chunk.Trimesh;
        }
        else
        {
            chunk.Trimesh.Data = _collisionVertices.ToArray();
        }
    }

    private readonly List<Vector3> _collisionVertices = new();

    /// <summary>
    /// Emits one bismuth block by copying its prebuilt variant into the mesh.
    ///
    /// This is the whole per-block cost at planet scale: six noise samples to
    /// find the variant, then a straight copy of that variant's quads with a
    /// scale and a translate. No geometry is solved here and no neighbouring
    /// block is consulted for SHAPE — the face field already guarantees the
    /// shapes interlock. Neighbours are consulted only to cull faces that are
    /// buried, which is pure rendering economy and cannot change the surface.
    /// </summary>
    private void AddBismuthBlock(Vector3I cell, Vector3 min, Color color)
    {
        BismuthShape.Variant variant = BismuthShape.Get(Field.MaskFor(cell));
        if (variant.Vertices.Length == 0)
            return;

        float quarter = _blockSize / BismuthShape.Sub;

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
    /// boundary exposed even with a solid block next door, and culling on
    /// presence alone tears visible holes.
    /// </summary>
    private bool IsBuried(Vector3I cell, BismuthShape.Variant variant, int quad)
    {
        // Every quarter-cell the quad covers must be filled by SOMETHING, or
        // part of the quad is still visible and dropping it opens a hole.
        //
        // The question asked is "is this space solid in the world", not "does
        // one particular neighbour fill it". Rims from edge and corner wins
        // reach diagonally, so the block that buries a quad is frequently not
        // the face neighbour a bake-time tag would have named — trusting that
        // tag left two thirds of the emitted surface buried but still drawn.
        int from = variant.OccludedStart[quad];
        int to = variant.OccludedStart[quad + 1];
        if (from == to)
            return false;

        for (int c = from; c < to; c += 3)
        {
            if (!SolidAt(cell,
                    variant.OccludedCells[c],
                    variant.OccludedCells[c + 1],
                    variant.OccludedCells[c + 2]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Every quarter-cell the world occupies, in global quarter-cell
    /// coordinates. Built once per rebuild by letting each block stamp in the
    /// cells it owns, so culling is a single hash lookup.
    ///
    /// The alternative — asking, per quad, which of the 27 blocks around a
    /// quarter-cell might reach into it — re-derives the same answer thousands
    /// of times and measured 15x slower than building this set up front.
    /// </summary>
    /// <summary>
    /// How many blocks fill each global quarter-cell; culling asks whether the
    /// count is above zero. Counted rather than a plain set so removing a block
    /// cannot erase a cell another block still fills.
    ///
    /// Maintained INCREMENTALLY: rebuilding it wholesale per edit re-inserted
    /// ~2.7M entries and measured 228 ms, 98% of the cost of an edit.
    /// </summary>
    private readonly Dictionary<Vector3I, int> _occupied = new();

    /// <summary>Rebuilds the whole occupancy map. Only for a wholesale
    /// change; a single edit uses the incremental add/remove below.</summary>
    private void BuildOccupancy()
    {
        _occupied.Clear();
        foreach (Vector3I cell in _blocks)
            StampOccupancy(cell, 1);
    }

    /// <summary>
    /// Adds (delta +1) or removes (delta -1) one block's quarter-cells from
    /// the occupancy map. Cells whose count falls to zero are dropped, so the
    /// map stays exactly what a full rebuild would produce.
    /// </summary>
    private void StampOccupancy(Vector3I cell, int delta)
    {
        int[] cells = BismuthShape.OccupiedCells(Field.MaskFor(cell));
        for (int c = 0; c < cells.Length; c += 3)
        {
            var key = new Vector3I(
                cell.X * BismuthShape.Sub + cells[c],
                cell.Y * BismuthShape.Sub + cells[c + 1],
                cell.Z * BismuthShape.Sub + cells[c + 2]);

            int count = _occupied.GetValueOrDefault(key) + delta;
            if (count > 0)
                _occupied[key] = count;
            else
                _occupied.Remove(key);
        }
    }

    private bool SolidAt(Vector3I cell, int i, int j, int k) =>
        _occupied.ContainsKey(new Vector3I(
            cell.X * BismuthShape.Sub + i,
            cell.Y * BismuthShape.Sub + j,
            cell.Z * BismuthShape.Sub + k));

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
