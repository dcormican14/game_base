using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using GameBase.Core;
using GameBase.Nodes;
using GameBase.Planets;

namespace GameBase.Terrain;

/// <summary>
/// The ground beyond the loaded chunks, drawn out to the horizon.
///
/// WHAT IT DRAWS. The same shape the chunks are built from -- the full 3D
/// field, so arches, overhangs and towering horns stand on the horizon, not a
/// height map that would flatten them -- sampled more coarsely the further
/// away it is. Space is cut into cubic BLOCKS of 16 cells a side, in LEVELS:
/// a level-1 cell is two nodes (4 units), each level up doubles it. A block is
/// split into its eight children when the eye is within
/// <see cref="SplitFactor"/> of its own size, so detail falls off with distance
/// at a steady rate: roughly the same number of cells cover every stretch of
/// the view. Each block is meshed with surface nets, as the sand is.
///
/// Level-1 blocks are the size of a chunk, and sample the shape on the very
/// lattice the chunk generator does, so where one hands over to the other the
/// ground barely moves.
///
/// HANDOVER. Near the camera the real chunks draw the ground. Between
/// <see cref="HandoverStart"/> and <see cref="HandoverEnd"/> they dissolve away
/// with distance (<see cref="NodeMaterials.SetHandover"/>) and the level-1
/// blocks standing in for them dissolve in, pixel for pixel on the same
/// pattern, so neither ground shows an edge. A level-1 block over a chunk that
/// is drawn (<see cref="NodeWorld.ChunkMeshed"/>) and lies wholly nearer than
/// the handover is not drawn at all -- digging near the player never shows the
/// old ground inside a pit -- and comes back whole the moment the chunk is
/// dropped. Over a chunk not drawn yet it shows whole, so streaming never
/// opens a hole.
///
/// SEAMS. Every block also meshes one cell past each side. Two blocks of the
/// same level build that overlap from the same samples, so they draw the
/// very same triangles there and meet without a line. Where a block meets
/// anything else -- a coarser or finer block, or the real chunks -- the edges
/// cannot agree, and a SKIRT hanging straight down from the overlap's rim,
/// as far as the ground below goes, fills the crack from below
/// (<see cref="FarMesher"/>); and near where a coarser level takes over, a
/// block's ground slides onto the coarser ground (MORPH), so the two meet at
/// one height rather than a ledge the skirt must hide. An earlier try sank
/// the overlap into the ground instead, side by side as neighbours changed:
/// it sank sides it should not have -- next to empty space no block was
/// built for -- and opened the very cracks it was for, showing the sky in
/// lines along every seam and beside freshly loaded chunks.
///
/// KEEPING UP. Space known to be all air or all rock -- found by a cheap probe
/// of every block the view splits -- is never visited again, so the blocks
/// wanted are only those near the ground. Blocks no longer wanted are skipped
/// by the worker that picks them up, and blocks under drawn chunks, only ever
/// a fallback, are built last.
///
/// CHANGES WITHOUT HOLES. When the eye moves, blocks split and merge, but a
/// block is only taken away in the same frame its replacement goes up: a
/// split waits for all its children, a merge for its parent.
///
/// Everything is in the planet's local space; this node sits at the planet's
/// centre, as the node world does.
/// </summary>
public partial class FarTerrain : Node3D
{
    /// <summary>How far out the far terrain is drawn, in world units.</summary>
    [Export(PropertyHint.Range, "500,20000,50")] public float DrawDistance { get; set; } = 4000f;

    /// <summary>A block is split when the eye is within this many of its own sizes.</summary>
    [Export(PropertyHint.Range, "1.5,8,0.1")] public float SplitFactor { get; set; } = 3f;

    /// <summary>Worker threads building blocks. 0 picks half the machine.</summary>
    [Export(PropertyHint.Range, "0,32,1")] public int Workers { get; set; }

    private int WorkerCount => Workers > 0 ? Workers : Math.Max(4, System.Environment.ProcessorCount / 2);

    /// <summary>Milliseconds a frame may spend uploading finished blocks, once the level is playable.</summary>
    [Export(PropertyHint.Range, "0.5,20,0.5")] public float UploadBudgetMs { get; set; } = 6f;

    /// <summary>
    /// Where, from the camera, the real chunks start to dissolve into the far
    /// terrain.
    ///
    /// WIDE ON PURPOSE. Real rock is whole Voronoi nodes, standing proud of or
    /// sunk into the smooth shape the far terrain draws by up to about a node
    /// (typically 0.6 units, see FarTerrainTests.FirstRingMeetsTheDrawnGround);
    /// over a narrow dissolve that step read as a ledge. Over a hundred units
    /// it is a gradual change. The streamer must keep the ground loaded out to
    /// the end: its load radius of 5 chunks guarantees about 210 units from
    /// the player in the worst direction.
    /// </summary>
    [Export(PropertyHint.Range, "20,1000,5")] public float HandoverStart { get; set; } = 110f;

    /// <summary>Where the real chunks are gone and the far terrain draws alone.</summary>
    [Export(PropertyHint.Range, "20,1000,5")] public float HandoverEnd { get; set; } = 210f;

    /// <summary>
    /// How much further out blocks with small separate features (sky islands)
    /// keep their detail, as a multiple of <see cref="SplitFactor"/>.
    /// </summary>
    [Export(PropertyHint.Range, "1,4,0.1")] public float IntricateDetail { get; set; } = 2f;

    /// <summary>What to follow. Defaults to the streamer's target, or the player.</summary>
    public Node3D Target { get; set; }

    /// <summary>Has the first view been built whole?</summary>
    public bool IsReady { get; private set; }

    /// <summary>How much of the first view is built, 0..1.</summary>
    public float Progress { get; private set; }

    /// <summary>Blocks meant to be on screen now. Diagnostic.</summary>
    public int WantedBlocks => _desired.Count;

    /// <summary>Blocks waiting for or being built. Diagnostic.</summary>
    public int PendingBlocks => _pending.Count + _inFlight;

    /// <summary>Every block held: wanted, on screen, or building. Diagnostic.</summary>
    public int HeldBlocks => _blocks.Count;

    /// <summary>Blocks drawn this frame. Diagnostic.</summary>
    public int DrawnBlocks { get; private set; }

    /// <summary>What the far terrain holds for a block. Diagnostic.</summary>
    public string DescribeBlock(int level, Vector3I coord)
    {
        var key = new BlockKey(level, coord);
        if (!_blocks.TryGetValue(key, out Block block))
            return $"{key}: none (wanted {_desired.Contains(key)})";

        return $"{key}: {block.State}, shown {block.Shown}, drawn {block.Instance?.Visible}, "
            + $"handover {block.Handover}, triangles {(block.Indices?.Length ?? 0) / 3}, wanted {_desired.Contains(key)}";
    }

    /// <summary>The blocks drawn now with any part nearer the camera than a distance. Diagnostic.</summary>
    public List<string> DescribeNear(float within)
    {
        var lines = new List<string>();
        foreach (Block block in _blocks.Values)
        {
            if (block.Instance == null || !block.Instance.Visible)
                continue;

            Aabb box = BoxOf(block.Key);
            float near = DistanceTo(box);
            if (near >= within)
                continue;

            lines.Add($"{block.Key}: nearest {near:0}, farthest {Farthest(box, _eye):0}, handover {block.Handover}, "
                + $"over drawn {OverDrawnChunks(block.Key)}, chunk drawn {(_world != null && block.Key.Level == 1 ? _world.IsChunkMeshed(block.Key.Coord) : false)}, "
                + $"triangles {(block.Indices?.Length ?? 0) / 3}");
        }

        return lines;
    }

    /// <summary>How many wanted blocks each level has, finest first. Diagnostic.</summary>
    public string LevelCounts
    {
        get
        {
            var counts = new int[CoarsestLevel + 1];
            foreach (BlockKey key in _desired)
                counts[key.Level]++;

            return string.Join(" ", counts[1..]);
        }
    }

    /// <summary>Cells a block has along each side.</summary>
    public const int Cells = 16;

    /// <summary>The coarsest level, whose blocks the view starts from.</summary>
    public const int CoarsestLevel = 6;

    /// <summary>How far the eye moves before the blocks are laid out again.</summary>
    private const float Relayout = 8f;

    /// <summary>The least time between layouts, however fast the eye moves.</summary>
    private const ulong LayoutEveryUsec = 100_000;

    /// <summary>
    /// Blocks known to be all rock (+1) or all air (-1): never visited again,
    /// nor anything inside them. Blocks known to hold ground are 0.
    /// </summary>
    private readonly ConcurrentDictionary<BlockKey, sbyte> _uniform = new();

    /// <summary>Blocks the view splits, waiting to be probed for being all air or rock.</summary>
    private readonly List<BlockKey> _probes = new();
    private readonly HashSet<BlockKey> _probing = new();
    private int _probed;
    private ulong _laidOutTime;

    private ITerrainShape _shape;
    private SandRules _sand;
    private float _node = 2f;
    private NodeWorld _world;
    private ChunkStreamer _streamer;
    private ShaderMaterial _material;

    /// <summary>The same, for blocks standing in for drawn chunks: cut away nearer than the handover.</summary>
    private ShaderMaterial _handoverMaterial;

    private readonly Dictionary<BlockKey, Block> _blocks = new();
    private readonly HashSet<BlockKey> _desired = new();
    private readonly List<Block> _pending = new();
    private readonly ConcurrentQueue<Built> _finished = new();
    private int _inFlight;
    private int _generation;

    private Vector3 _eye;
    private Vector3 _anchor;
    private Vector3 _laidOutAt;
    private Vector3 _anchoredAt;
    private bool _laidOut;
    private bool _dirty;

    // ------------------------------------------------------------ set-up

    /// <summary>Points the far terrain at a planet's shape and world, dropping anything built.</summary>
    public void Configure(PlanetGenerator generator, NodeWorld world, ChunkStreamer streamer)
    {
        Clear();

        if (_world != null)
        {
            _world.ChunkUnloaded -= OnChunkUnloaded;
        }

        _shape = generator.Shape;
        _sand = generator.Sand;
        _node = generator.Grid.NodeSize;
        _world = world;
        _streamer = streamer;

        if (_world != null)
        {
            _world.ChunkUnloaded += OnChunkUnloaded;
        }

        _material ??= BuildMaterial();
        _handoverMaterial ??= BuildMaterial();
        _handoverMaterial.SetShaderParameter("handover", true);

        foreach (ShaderMaterial material in new[] { _material, _handoverMaterial })
        {
            material.SetShaderParameter("handover_start", HandoverStart);
            material.SetShaderParameter("handover_end", HandoverEnd);
        }
        NodeMaterials.SetHandover(HandoverStart, HandoverEnd);
    }

    /// <summary>Drops every block, built or building.</summary>
    public void Clear()
    {
        Interlocked.Increment(ref _generation);

        foreach (Block block in _blocks.Values)
            block.Free();

        _blocks.Clear();
        _desired.Clear();
        _pending.Clear();
        _finished.Clear();
        _uniform.Clear();
        _probes.Clear();
        _probing.Clear();
        _laidOut = false;
        IsReady = false;
        Progress = 0f;
    }

    public override void _ExitTree()
    {
        Interlocked.Increment(ref _generation);
        NodeMaterials.SetHandover(0f, 0f);

        if (_world != null)
        {
            _world.ChunkUnloaded -= OnChunkUnloaded;
        }
    }

    private static ShaderMaterial BuildMaterial()
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Modules/Terrain/FarTerrain.gdshader") };

        // Handed over exactly as the sand's own material takes its colours
        // (see NodeMaterials.Particle), so the two match where they meet.
        Color sand = NodeMaterials.Albedo(Palette.Sand), shade = NodeMaterials.Albedo(Palette.SandShade);
        material.SetShaderParameter("sand_color", new Vector3(sand.R, sand.G, sand.B));
        material.SetShaderParameter("sand_shade", new Vector3(shade.R, shade.G, shade.B));
        material.SetShaderParameter("rock_light", Palette.StoneLight);
        material.SetShaderParameter("rock_dark", Palette.StoneDark);
        material.SetShaderParameter("haze_color", Palette.Wine);

        return material;
    }

    /// <summary>
    /// A dropped chunk's stand-in comes back at once, from inside the unload
    /// -- not on this node's next frame, which could come after the chunk's
    /// ground is already gone and show a frame of sky.
    /// </summary>
    private void OnChunkUnloaded(Vector3I chunk)
    {
        if (!_blocks.TryGetValue(new BlockKey(1, chunk), out Block block) || !block.Shown || block.Instance == null)
            return;

        block.Handover = false;
        block.Instance.MaterialOverride = _material;
        block.Instance.Visible = true;
    }

    // --------------------------------------------------------------- frame

    public override void _Process(double delta)
    {
        if (_shape == null || !FindTarget())
            return;

        // Detail follows what is looking -- the camera, wherever it is -- and
        // the level-1 ring the loaded chunks, which follow the target.
        Camera3D camera = GetViewport()?.GetCamera3D();
        _anchor = ToLocal(Target.GlobalPosition);
        _eye = camera != null ? ToLocal(camera.GlobalPosition) : _anchor;

        // Laid out again when the eye has moved, or when probes have found
        // empty space to leave out -- but never more often than this, however
        // fast the eye goes.
        ulong now = Time.GetTicksUsec();
        bool moved = _eye.DistanceTo(_laidOutAt) > Relayout || _anchor.DistanceTo(_anchoredAt) > Relayout;
        bool learned = Volatile.Read(ref _probed) != 0 && now - _laidOutTime > 2 * LayoutEveryUsec;

        if (!_laidOut || ((moved || learned) && now - _laidOutTime > LayoutEveryUsec))
            Layout();

        Collect(IsReady ? UploadBudgetMs : 12f);
        Dispatch();

        if (_dirty)
            Reconcile();

        UpdateVisibility();

        if (!IsReady)
            CheckReady();
    }

    private bool FindTarget()
    {
        if (Target != null && IsInstanceValid(Target))
            return true;

        Target = _streamer?.Target;

        if (Target == null || !IsInstanceValid(Target))
        {
            Target = null;
            foreach (Node node in GetTree().GetNodesInGroup(Groups.Player))
            {
                if (node is Node3D found)
                {
                    Target = found;
                    break;
                }
            }
        }

        return Target != null;
    }

    // -------------------------------------------------------------- layout

    /// <summary>A block: its level and its position in blocks of that level.</summary>
    public readonly record struct BlockKey(int Level, Vector3I Coord)
    {
        public BlockKey Parent => new(Level + 1, new Vector3I(
            FloorHalf(Coord.X), FloorHalf(Coord.Y), FloorHalf(Coord.Z)));

        public BlockKey Child(int n) => new(Level - 1, Coord * 2 + new Vector3I(n & 1, (n >> 1) & 1, (n >> 2) & 1));

        // Written out: the record's own would print Parent, and its parent,
        // and on without end.
        public override string ToString() => $"L{Level} {Coord}";

        private static int FloorHalf(int v) => v >> 1;
    }

    /// <summary>The width of one cell at a level.</summary>
    public float CellSize(int level) => _node * (1 << level);

    /// <summary>The width of one block at a level.</summary>
    public float BlockSize(int level) => CellSize(level) * Cells;

    public Aabb BoxOf(BlockKey key)
    {
        float size = BlockSize(key.Level);
        return new Aabb(new Vector3(key.Coord.X, key.Coord.Y, key.Coord.Z) * size, Vector3.One * size);
    }

    private float _occluder;
    private float _horizonOfEye;

    /// <summary>Works out which blocks the view wants, and queues any not built.</summary>
    private void Layout()
    {
        _laidOut = true;
        _laidOutAt = _eye;
        _anchoredAt = _anchor;
        _laidOutTime = Time.GetTicksUsec();
        Interlocked.Exchange(ref _probed, 0);
        _desired.Clear();

        // The horizon: ground at least this far out hides whatever is behind
        // the curve of the planet. Only the lowest ground anywhere can be
        // trusted to be there -- a basin or a sky-island chasm lets the eye
        // see far lower, and trusting anything higher left their floors
        // undrawn a hundred units out.
        float eyeRadius = _eye.Length();
        _occluder = Mathf.Min((float)_shape.Bottom, eyeRadius - 1f);
        _horizonOfEye = eyeRadius > _occluder ? Mathf.Acos(Mathf.Clamp(_occluder / eyeRadius, -1f, 1f)) : 0f;

        float top = BlockSize(CoarsestLevel);
        var middle = new Vector3I(Mathf.FloorToInt(_eye.X / top), Mathf.FloorToInt(_eye.Y / top), Mathf.FloorToInt(_eye.Z / top));
        int reach = Mathf.CeilToInt(DrawDistance / top) + 1;

        for (int x = -reach; x <= reach; x++)
        for (int y = -reach; y <= reach; y++)
        for (int z = -reach; z <= reach; z++)
            Visit(new BlockKey(CoarsestLevel, middle + new Vector3I(x, y, z)));

        // Queue what is wanted and not built; forget what is neither wanted
        // nor on screen.
        _pending.Clear();
        var drop = new List<BlockKey>();

        foreach (BlockKey key in _desired)
        {
            if (!_blocks.TryGetValue(key, out Block block))
            {
                block = new Block(key);
                _blocks[key] = block;
            }

            if (block.State == BlockState.Queued)
                _pending.Add(block);
        }

        foreach ((BlockKey key, Block block) in _blocks)
        {
            if (!_desired.Contains(key) && !block.Shown && block.State != BlockState.Building)
                drop.Add(key);
        }

        foreach (BlockKey key in drop)
        {
            _blocks[key].Free();
            _blocks.Remove(key);
        }

        // Nearest and finest first: what the eye sees largest. Level-1 blocks
        // under drawn chunks, well inside the handover, last: they are only a
        // fallback for when those chunks go.
        foreach (Block block in _pending)
        {
            Aabb box = BoxOf(block.Key);
            block.Priority = DistanceTo(box) / BlockSize(block.Key.Level);

            if (block.Key.Level == 1 && _world != null && _world.IsChunkMeshed(block.Key.Coord)
                && Farthest(box, _eye) < HandoverStart)
                block.Priority += 1000f;
        }

        _pending.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        _dirty = true;
    }

    private void Visit(BlockKey key)
    {
        // All air or all rock: nothing here, nor in anything inside it.
        if (_uniform.TryGetValue(key, out sbyte side) && side != 0)
            return;

        Aabb box = BoxOf(key);

        if (!CrossesShell(box))
            return;

        float distance = DistanceTo(box);
        if (distance > DrawDistance || !AboveHorizon(box))
            return;

        if (key.Level > 1 && ShouldSplit(key, box, distance))
        {
            // Probe it, so next time an empty one is not split again.
            if (side == 0 && !_uniform.ContainsKey(key) && _probing.Add(key))
                _probes.Add(key);

            for (int n = 0; n < 8; n++)
                Visit(key.Child(n));
            return;
        }

        _desired.Add(key);
    }

    private bool ShouldSplit(BlockKey key, Aabb box, float distance)
    {
        if (distance < SplitFactor * BlockSize(key.Level))
            return true;

        // Sky islands and the like keep their detail further out: coarse
        // cells would melt them into blobs, or lose the small ones.
        if (key.Level <= 5 && distance < SplitFactor * IntricateDetail * BlockSize(key.Level)
            && _shape.Intricate(box.Position, box.End))
            return true;

        // Level 1 must stand wherever a real chunk could be loaded -- a
        // coarser block would draw over the real ground -- so level 2 splits
        // out to the streamer's unload radius, and a little past it.
        if (key.Level == 2 && _streamer != null)
            return DistanceTo(box, _anchor) < LoadedReach;

        return false;
    }

    /// <summary>How far from the target a real chunk could be loaded, and a little past it.</summary>
    private float LoadedReach =>
        (Mathf.Max(_streamer.UnloadRadius, _streamer.LoadRadius + 1) + 1.5f) * BlockSize(1);

    /// <summary>
    /// About how far from the eye a block splits into finer ones: the
    /// nearest its level is drawn. Level 1 never splits, but its nearest
    /// ground is the real chunks' until the handover ends.
    /// </summary>
    private float SplitRange(BlockKey key)
    {
        if (key.Level == 1)
            return HandoverEnd;

        Aabb box = BoxOf(key);
        float range = SplitFactor * BlockSize(key.Level);

        if (key.Level <= 5 && _shape.Intricate(box.Position, box.End))
            range *= IntricateDetail;
        if (key.Level == 2 && _streamer != null)
            range = Mathf.Max(range, LoadedReach);

        return range;
    }

    /// <summary>
    /// Over what distances from the eye a block's ground slides onto its
    /// parent's (see FarMesher, MORPH): from a little past where it starts
    /// being drawn, all the way by just short of where its parent stops
    /// splitting -- so wherever it meets a coarser block, it matches it.
    /// </summary>
    private (float Start, float End) MorphRange(BlockKey key)
    {
        if (key.Level >= CoarsestLevel)
            return (1e9f, 2e9f);

        float inner = SplitRange(key), outer = SplitRange(key.Parent);
        float start = inner + 0.25f * (outer - inner);
        float end = Mathf.Max(outer - 2f * CellSize(key.Level + 1), start + 1f);
        return (start, end);
    }

    /// <summary>Does a box reach the band the ground can be in?</summary>
    private bool CrossesShell(Aabb box)
    {
        Vector3 min = box.Position, max = box.End;
        double nearSq = 0, farSq = 0;

        for (int axis = 0; axis < 3; axis++)
        {
            double low = min[axis], high = max[axis];
            double nearest = low > 0 ? low : high < 0 ? high : 0;
            double furthest = Math.Max(Math.Abs(low), Math.Abs(high));
            nearSq += nearest * nearest;
            farSq += furthest * furthest;
        }

        float pad = CellSize(1);
        return Math.Sqrt(nearSq) <= _shape.Top + pad && Math.Sqrt(farSq) >= _shape.Bottom - pad;
    }

    private float DistanceTo(Aabb box) => DistanceTo(box, _eye);

    /// <summary>The furthest a box's corners are from a point.</summary>
    private static float Farthest(Aabb box, Vector3 point)
    {
        Vector3 min = box.Position, max = box.End;
        Vector3 far = new(
            Mathf.Abs(point.X - min.X) > Mathf.Abs(point.X - max.X) ? min.X : max.X,
            Mathf.Abs(point.Y - min.Y) > Mathf.Abs(point.Y - max.Y) ? min.Y : max.Y,
            Mathf.Abs(point.Z - min.Z) > Mathf.Abs(point.Z - max.Z) ? min.Z : max.Z);
        return far.DistanceTo(point);
    }

    private static float DistanceTo(Aabb box, Vector3 point)
    {
        Vector3 min = box.Position, max = box.End;
        Vector3 nearest = new(
            Mathf.Clamp(point.X, min.X, max.X), Mathf.Clamp(point.Y, min.Y, max.Y), Mathf.Clamp(point.Z, min.Z, max.Z));
        return nearest.DistanceTo(point);
    }

    /// <summary>Could any of a box be seen over the curve of the planet?</summary>
    private bool AboveHorizon(Aabb box)
    {
        Vector3 centre = box.GetCenter();
        float radius = centre.Length();
        float half = box.Size.Length() * 0.5f;

        if (radius < half + 1f || _eye.LengthSquared() < 1f)
            return true;

        float apart = _eye.Normalized().AngleTo(centre / radius) - Mathf.Asin(Mathf.Min(1f, half / radius));
        if (apart <= 0f)
            return true;

        float top = Mathf.Min((float)_shape.Top, radius + half);
        float beyond = top > _occluder ? Mathf.Acos(Mathf.Clamp(_occluder / top, -1f, 1f)) : 0f;

        return apart <= _horizonOfEye + beyond;
    }

    // ------------------------------------------------------------ building

    private enum BlockState
    {
        Queued,
        Building,
        Built,
    }

    private sealed class Block
    {
        public Block(BlockKey key) => Key = key;

        public readonly BlockKey Key;
        public BlockState State;
        public float Priority;

        /// <summary>Part of the arrangement on screen (it may still be hidden under real chunks).</summary>
        public bool Shown;

        public MeshInstance3D Instance;
        public Vector3[] Vertices;
        public int[] Indices;

        /// <summary>Dissolving in as the real ground dissolves out (see HANDOVER).</summary>
        public bool Handover;


        /// <summary>No longer wanted: a worker that has not started on it skips it.</summary>
        public volatile bool Cancelled;

        public void Free()
        {
            Cancelled = true;

            if (Instance != null && IsInstanceValid(Instance))
                Instance.QueueFree();

            Instance = null;
        }
    }

    private readonly record struct Built(BlockKey Key, int Generation, BlockMesh Mesh);

    private void Dispatch()
    {
        // Probes first: each is a fraction of a build, and each that finds
        // empty space spares building everything inside it.
        while (_inFlight < WorkerCount && _probes.Count > 0)
        {
            BlockKey key = _probes[^1];
            _probes.RemoveAt(_probes.Count - 1);

            ITerrainShape shape = _shape;
            float cell = CellSize(key.Level);
            int generation = Volatile.Read(ref _generation);

            Interlocked.Increment(ref _inFlight);

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (generation != Volatile.Read(ref _generation))
                        return;

                    int side = FarMesher.Probe(shape, key.Coord, cell);
                    _uniform[key] = (sbyte)side;
                    if (side != 0)
                        Interlocked.Exchange(ref _probed, 1);
                }
                catch (Exception error)
                {
                    GD.PushError($"FarTerrain: probing {key} failed - {error}");
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            });
        }

        while (_inFlight < WorkerCount && _pending.Count > 0)
        {
            Block block = _pending[^1];
            _pending.RemoveAt(_pending.Count - 1);

            if (block.State != BlockState.Queued)
                continue;

            block.State = BlockState.Building;
            BlockKey key = block.Key;
            int generation = Volatile.Read(ref _generation);
            ITerrainShape shape = _shape;
            SandRules sand = _sand;
            float cell = CellSize(key.Level);

            Interlocked.Increment(ref _inFlight);

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Dropped while it waited for a worker: nothing to do.
                    if (block.Cancelled || generation != Volatile.Read(ref _generation))
                    {
                        _finished.Enqueue(new Built(key, generation, null));
                        return;
                    }

                    int side = _uniform.TryGetValue(key, out sbyte known) ? known : FarMesher.Probe(shape, key.Coord, cell);
                    _uniform[key] = (sbyte)side;

                    BlockMesh mesh = side != 0 ? BlockMesh.Empty : FarMesher.Build(shape, sand, key.Coord, cell, probed: true);
                    _finished.Enqueue(new Built(key, generation, mesh));
                }
                catch (Exception error)
                {
                    GD.PushError($"FarTerrain: building {key} failed - {error}");
                    _finished.Enqueue(new Built(key, generation, BlockMesh.Empty));
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            });
        }
    }

    private void Collect(float budgetMs)
    {
        ulong deadline = Time.GetTicksUsec() + (ulong)(budgetMs * 1000f);

        while (Time.GetTicksUsec() < deadline && _finished.TryDequeue(out Built done))
        {
            if (done.Generation != Volatile.Read(ref _generation))
                continue;

            if (!_blocks.TryGetValue(done.Key, out Block block) || block.State != BlockState.Building)
                continue;

            // Skipped by its worker: queued again should it be wanted again.
            if (done.Mesh == null)
            {
                block.State = BlockState.Queued;
                continue;
            }

            block.State = BlockState.Built;
            block.Vertices = done.Mesh.Vertices;
            block.Indices = done.Mesh.Indices;

            if (!done.Mesh.IsEmpty)
            {
                (float morphStart, float morphEnd) = MorphRange(done.Key);
                block.Instance = new MeshInstance3D
                {
                    Mesh = done.Mesh.ToArrayMesh(morphStart, morphEnd),
                    MaterialOverride = _material,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Visible = false,
                    Name = $"L{done.Key.Level}_{done.Key.Coord.X}_{done.Key.Coord.Y}_{done.Key.Coord.Z}",
                };
                AddChild(block.Instance);
            }

            _dirty = true;
        }
    }

    // ----------------------------------------------------------- reconcile

    /// <summary>
    /// Brings what is on screen in line with what the view wants, without
    /// ever leaving a hole: each block on screen that is no longer wanted is
    /// swapped out in the same frame as everything replacing it goes in.
    /// </summary>
    private void Reconcile()
    {
        _dirty = false;

        // Wanted blocks under each ancestor, and on-screen blocks under each.
        var wantedUnder = new Dictionary<BlockKey, List<BlockKey>>();
        foreach (BlockKey key in _desired)
        {
            for (BlockKey up = key.Parent; up.Level <= CoarsestLevel; up = up.Parent)
            {
                if (!wantedUnder.TryGetValue(up, out List<BlockKey> list))
                    wantedUnder[up] = list = new List<BlockKey>();
                list.Add(key);
            }
        }

        var shown = new List<BlockKey>();
        foreach ((BlockKey key, Block block) in _blocks)
        {
            if (block.Shown)
                shown.Add(key);
        }

        var shownUnder = new Dictionary<BlockKey, List<BlockKey>>();
        foreach (BlockKey key in shown)
        {
            for (BlockKey up = key.Parent; up.Level <= CoarsestLevel; up = up.Parent)
            {
                if (!shownUnder.TryGetValue(up, out List<BlockKey> list))
                    shownUnder[up] = list = new List<BlockKey>();
                list.Add(key);
            }
        }

        // Whether every wanted block under a coarse one is built, asked once
        // per coarse block rather than once per block under it.
        var ready = new Dictionary<BlockKey, bool>();

        foreach (BlockKey key in _desired)
        {
            Block block = _blocks[key];
            if (block.Shown || block.State != BlockState.Built)
                continue;

            BlockKey? ancestor = ShownAncestor(key);

            if (ancestor is BlockKey split)
            {
                // A split: the coarse block goes when every wanted block
                // inside it is ready.
                // A level-1 block that would not be drawn anyway -- under
                // drawn chunks, nearer than the handover -- need not be built
                // first: waiting on it (it is built last) left the coarse
                // block standing over the player.
                List<BlockKey> inside = wantedUnder[split];
                if (!ready.TryGetValue(split, out bool all))
                    ready[split] = all = inside.TrueForAll(k => _blocks[k].State == BlockState.Built || Covered(k));

                if (!all)
                    continue;

                foreach (BlockKey k in inside)
                    Show(_blocks[k]);

                Hide(split);
            }
            else
            {
                // A merge, or new ground coming into view.
                Show(block);

                if (shownUnder.TryGetValue(key, out List<BlockKey> finer))
                {
                    foreach (BlockKey k in finer)
                        Hide(k);
                }
            }
        }

        // On screen, not wanted, and nothing wanted over or under it: out of
        // view now.
        foreach (BlockKey key in shown)
        {
            if (!_blocks.TryGetValue(key, out Block block) || !block.Shown || _desired.Contains(key))
                continue;

            if (wantedUnder.ContainsKey(key) || WantedAncestor(key))
                continue;

            Hide(key);
        }
    }

    /// <summary>A level-1 block wholly nearer than the handover, over a drawn chunk: never drawn.</summary>
    private bool Covered(BlockKey key) =>
        key.Level == 1 && _world != null && _world.IsChunkMeshed(key.Coord)
        && Farthest(BoxOf(key), _eye) < HandoverStart;

    /// <summary>
    /// Is every chunk under a block drawn? Asked of levels 1 to 3 (up to 64
    /// chunks); anything coarser is never over the real ground once settled.
    /// </summary>
    private bool OverDrawnChunks(BlockKey key)
    {
        if (_world == null || key.Level > 3)
            return false;

        int span = 1 << (key.Level - 1);
        Vector3I first = key.Coord * span;

        for (int x = 0; x < span; x++)
        for (int y = 0; y < span; y++)
        for (int z = 0; z < span; z++)
        {
            if (!_world.IsChunkMeshed(first + new Vector3I(x, y, z)))
                return false;
        }

        return true;
    }

    private BlockKey? ShownAncestor(BlockKey key)
    {
        for (BlockKey up = key.Parent; up.Level <= CoarsestLevel; up = up.Parent)
        {
            if (_blocks.TryGetValue(up, out Block block) && block.Shown)
                return up;
        }

        return null;
    }

    private bool WantedAncestor(BlockKey key)
    {
        for (BlockKey up = key.Parent; up.Level <= CoarsestLevel; up = up.Parent)
        {
            if (_desired.Contains(up))
                return true;
        }

        return false;
    }

    private void Show(Block block)
    {
        block.Shown = true;
    }

    private void Hide(BlockKey key)
    {
        if (!_blocks.TryGetValue(key, out Block block))
            return;

        block.Shown = false;
        if (block.Instance != null)
            block.Instance.Visible = false;

        if (!_desired.Contains(key))
        {
            block.Free();
            _blocks.Remove(key);
        }
    }

    // ---------------------------------------------------------- visibility

    /// <summary>
    /// Draws what is on screen, with the handover for level-1 blocks over
    /// drawn chunks (see HANDOVER).
    /// </summary>
    private void UpdateVisibility()
    {
        int drawn = 0;

        foreach (Block block in _blocks.Values)
        {
            if (block.Instance == null)
                continue;

            if (!block.Shown)
            {
                block.Instance.Visible = false;
                continue;
            }

            // Over drawn chunks, at any level: gone where wholly nearer than
            // the handover, and otherwise cut away nearer than it. A coarse
            // block can stand there a moment while its finer ones are built,
            // and must not cover the real ground (or a pit dug in it) either.
            bool visible = true, handover = false;
            if (OverDrawnChunks(block.Key))
            {
                if (Farthest(BoxOf(block.Key), _eye) < HandoverStart)
                    visible = false;
                else
                    handover = true;
            }

            if (block.Instance.Visible != visible)
                block.Instance.Visible = visible;

            if (block.Handover != handover)
            {
                block.Handover = handover;
                block.Instance.MaterialOverride = handover ? _handoverMaterial : _material;
            }

            if (visible)
                drawn++;
        }

        DrawnBlocks = drawn;
    }

    private void CheckReady()
    {
        int wanted = _desired.Count, ready = 0;

        foreach (BlockKey key in _desired)
        {
            if (_blocks.TryGetValue(key, out Block block) && block.Shown)
                ready++;
        }

        Progress = wanted == 0 ? 1f : Mathf.Max(Progress, ready / (float)wanted);

        if (_laidOut && ready == wanted)
        {
            IsReady = true;
            Progress = 1f;
        }
    }

    // -------------------------------------------------------------- probes

    /// <summary>
    /// The first far-terrain triangle a segment meets, among the blocks drawn
    /// now (global points). For tests: the far terrain has no collision.
    /// </summary>
    public bool Raycast(Vector3 from, Vector3 to, out Vector3 hit)
    {
        Vector3 a = ToLocal(from), b = ToLocal(to);
        Vector3 direction = b - a;
        float best = 1f;
        bool found = false;

        foreach (Block block in _blocks.Values)
        {
            if (block.Instance == null || !block.Instance.Visible || block.Indices == null)
                continue;

            if (!BoxOf(block.Key).Grow(CellSize(block.Key.Level)).IntersectsSegment(a, b))
                continue;

            for (int i = 0; i < block.Indices.Length; i += 3)
            {
                if (Intersect(a, direction, block.Vertices[block.Indices[i]], block.Vertices[block.Indices[i + 1]],
                        block.Vertices[block.Indices[i + 2]], out float t) && t < best)
                {
                    best = t;
                    found = true;
                }
            }
        }

        hit = ToGlobal(a + direction * best);
        return found;
    }

    /// <summary>Möller-Trumbore, either side: t along the segment, 0..1.</summary>
    private static bool Intersect(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        Vector3 e1 = b - a, e2 = c - a;
        Vector3 p = direction.Cross(e2);
        float det = e1.Dot(p);
        if (Mathf.Abs(det) < 1e-9f)
            return false;

        float inv = 1f / det;
        Vector3 s = origin - a;
        float u = s.Dot(p) * inv;
        if (u < 0f || u > 1f)
            return false;

        Vector3 q = s.Cross(e1);
        float v = direction.Dot(q) * inv;
        if (v < 0f || u + v > 1f)
            return false;

        t = e2.Dot(q) * inv;
        return t >= 0f && t <= 1f;
    }
}
