using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using GameBase.Nodes.Meshing;

namespace GameBase.Nodes;

/// <summary>
/// The world's nodes: their data, their geometry and their collision.
///
/// Data lives in a <see cref="NodeChunkStore"/>, one chunk (32 cells a side) at
/// a time. Geometry is built per SECTION (8 cells a side) so an edit rebuilds a
/// few hundred cells rather than a chunk's thirty thousand. Each section is
/// built by running every <see cref="INodeMesher"/> over it -- one per
/// <see cref="NodeForm"/> -- on a worker thread; only the upload happens on the
/// main thread, under a per-frame time budget, with edits served ahead of the
/// streaming backlog so a dig shows on the next frame or two.
///
/// Raw and particle nodes collide on separate bodies, so a ray that hits the
/// world knows which form it touched without guessing from the point.
///
/// Everything here is in the world's local space, with the planet's centre at
/// the origin; the public queries take and return global positions.
/// </summary>
public partial class NodeWorld : Node3D
{
    public const int SectionSize = SectionSample.Size;
    private const int SectionShift = 3;
    private const int SectionsPerChunkShift = 2;

    /// <summary>Milliseconds a frame may spend uploading finished sections.</summary>
    [Export(PropertyHint.Range, "0.5,20,0.5")]
    public float MeshBudgetMs { get; set; } = 4f;

    /// <summary>Worker threads building sections. 0 picks half the machine.</summary>
    [Export(PropertyHint.Range, "0,32,1")]
    public int MeshWorkers { get; set; }

    /// <summary>The physics layer both collision bodies sit on.</summary>
    [Export(PropertyHint.Layers3DPhysics)]
    public uint CollisionLayer { get; set; } = 1;

    public VoronoiGrid Grid { get; private set; }

    public NodeChunkStore Store { get; } = new();

    private static readonly INodeMesher[] Meshers = { new RawNodeMesher(), new ParticleNodeMesher() };

    // ------------------------------------------------------------ lifecycle

    /// <summary>Points the world at a grid, dropping everything it held.</summary>
    public void Configure(VoronoiGrid grid)
    {
        Clear();
        Grid = grid;
    }

    /// <summary>Drops every chunk, section and pending build.</summary>
    public void Clear()
    {
        foreach (SectionNodes nodes in _sections.Values)
            nodes.Free();

        _sections.Clear();
        _urgent.Clear();
        _urgentOrder.Clear();
        _group = null;
        _backlog.Clear();
        _backlogOrder.Clear();
        _tickets.Clear();
        Store.Clear();

        // Anything still building describes the world just dropped; its
        // results are discarded on arrival.
        Interlocked.Increment(ref _generation);
    }

    public override void _Process(double delta) => Pump(MeshBudgetMs);

    public override void _ExitTree()
    {
        Interlocked.Increment(ref _generation);
    }

    // --------------------------------------------------------------- queries

    /// <summary>The kind of node in a cell, or null for air (or unloaded).</summary>
    public NodeType TypeAt(Vector3I cell) => NodeTypes.Of(Store.MaterialAt(cell));

    /// <summary>A particle cell's fill; <see cref="NodeFill.Full"/> for raw, empty for air.</summary>
    public byte FillAt(Vector3I cell)
    {
        byte material = Store.MaterialAt(cell);

        if (material == NodeChunkStore.Air)
            return NodeFill.Empty;

        return NodeTypes.IsRaw(material) ? NodeFill.Full : Store.FillAt(cell);
    }

    /// <summary>The node whose region contains a global point.</summary>
    public Vector3I CellAt(Vector3 globalPoint) => Grid.CellAt(ToLocal(globalPoint));

    /// <summary>A node's site, in global space.</summary>
    public Vector3 SiteOf(Vector3I cell) => ToGlobal(Grid.SiteOf(cell));

    /// <summary>A cell's lattice point, in global space.</summary>
    public Vector3 LatticePoint(Vector3I cell) => ToGlobal(Grid.LatticePoint(cell));

    public float NodeSize => Grid?.NodeSize ?? 1f;

    public bool IsChunkLoaded(Vector3I chunk) => Store.IsLoaded(chunk);

    public bool IsCellLoaded(Vector3I cell) => Store.IsLoaded(NodeChunkStore.ChunkOf(cell));

    // ----------------------------------------------------------------- edits

    /// <summary>
    /// Puts a node in a cell, replacing whatever was there. Raw nodes are always
    /// full; a particle takes the fill given. Returns whether anything changed.
    /// </summary>
    public bool SetNode(Vector3I cell, NodeType type, byte fill = NodeFill.Full)
    {
        if (type == null)
            return ClearNode(cell);

        if (!IsCellLoaded(cell))
            return false;

        byte stored = type.Form == NodeForm.Raw ? NodeFill.Full : fill;

        if (!Store.Set(cell, (byte)type.Material, stored))
            return false;

        MarkEdited(cell);
        return true;
    }

    /// <summary>Empties a cell. Returns whether anything changed.</summary>
    public bool ClearNode(Vector3I cell)
    {
        if (!IsCellLoaded(cell) || !Store.Set(cell, NodeChunkStore.Air, NodeFill.Empty))
            return false;

        MarkEdited(cell);
        return true;
    }

    /// <summary>
    /// Sets a particle cell's surface distance (see <see cref="NodeFill"/>),
    /// turning the cell to air once it holds nothing. Raw cells are left alone.
    /// Returns whether anything changed.
    /// </summary>
    public bool SetParticleLevel(Vector3I cell, ParticleNode type, float level) =>
        SetParticleFill(cell, type, NodeFill.FromLevel(level));

    /// <summary>
    /// <see cref="SetParticleLevel"/> with the fill byte already chosen, for
    /// edits that round it themselves.
    /// </summary>
    public bool SetParticleFill(Vector3I cell, ParticleNode type, byte fill)
    {
        if (NodeTypes.IsRaw(Store.MaterialAt(cell)))
            return false;

        return fill == NodeFill.Empty ? ClearNode(cell) : SetNode(cell, type, fill);
    }

    /// <summary>
    /// A particle cell's surface distance in nodes: fully outside for air,
    /// fully inside for raw (rock counts as buried), the stored level otherwise.
    /// </summary>
    public float ParticleLevel(Vector3I cell)
    {
        byte material = Store.MaterialAt(cell);

        if (material == NodeChunkStore.Air)
            return -NodeFill.Range;

        return NodeTypes.IsRaw(material) ? NodeFill.Range : NodeFill.ToLevel(Store.FillAt(cell));
    }

    /// <summary>
    /// Queues every section an edit at this cell can change, ahead of the
    /// streaming backlog.
    ///
    /// TWO cells out. The particle surface through a cell depends on the cells
    /// around it, one out. But whether a cell hides the rock face turned toward
    /// it depends on ITS neighbours (see <see cref="SectionSample.Covers"/>),
    /// so emptying one cell can uncover a rock face two cells away -- and a
    /// section left unbuilt there keeps a hole where that face should be.
    /// </summary>
    private void MarkEdited(Vector3I cell)
    {
        Vector3I reach = Vector3I.One * SectionSample.Border;
        Vector3I low = SectionOf(cell - reach);
        Vector3I high = SectionOf(cell + reach);

        for (int x = low.X; x <= high.X; x++)
        for (int y = low.Y; y <= high.Y; y++)
        for (int z = low.Z; z <= high.Z; z++)
        {
            var section = new Vector3I(x, y, z);

            if (Store.IsLoaded(ChunkOfSection(section)) && _urgent.Add(section))
                _urgentOrder.Enqueue(section);
        }
    }

    // --------------------------------------------------------------- chunks

    /// <summary>
    /// Installs a generated chunk and queues its geometry, plus the bordering
    /// sections of chunks already here: their faces toward this chunk were
    /// hidden while it was unknown and can now be drawn properly.
    /// </summary>
    public void InstallChunk(Vector3I chunk, byte[] materials, byte[] fills)
    {
        Store.Install(chunk, materials, fills);

        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        Vector3I low = SectionOf(origin - Vector3I.One);
        Vector3I high = SectionOf(origin + Vector3I.One * NodeChunkStore.ChunkSize);

        for (int x = low.X; x <= high.X; x++)
        for (int y = low.Y; y <= high.Y; y++)
        for (int z = low.Z; z <= high.Z; z++)
        {
            var section = new Vector3I(x, y, z);

            if (Store.IsLoaded(ChunkOfSection(section)) && _backlog.Add(section))
                _backlogOrder.Enqueue(section);
        }
    }

    /// <summary>Drops a chunk's data and geometry.</summary>
    public void UnloadChunk(Vector3I chunk)
    {
        Store.Unload(chunk);

        Vector3I first = new(chunk.X << SectionsPerChunkShift, chunk.Y << SectionsPerChunkShift,
            chunk.Z << SectionsPerChunkShift);
        int count = 1 << SectionsPerChunkShift;

        for (int x = 0; x < count; x++)
        for (int y = 0; y < count; y++)
        for (int z = 0; z < count; z++)
        {
            var section = new Vector3I(first.X + x, first.Y + y, first.Z + z);

            if (_sections.Remove(section, out SectionNodes nodes))
                nodes.Free();

            _urgent.Remove(section);
            _backlog.Remove(section);
            _tickets.Remove(section);
        }
    }

    // --------------------------------------------------------------- meshing

    private readonly Dictionary<Vector3I, SectionNodes> _sections = new();

    /// <summary>
    /// Sections an edit changed and that are waiting to be built, in the order
    /// they were first marked. They go out together, as one
    /// <see cref="EditGroup"/>, ahead of anything streaming queued.
    ///
    /// IN ORDER. A held shovel re-marks the same few dozen sections every
    /// frame. Taken in a set's own order, the ones that happened to come first
    /// were rebuilt every time and the rest could wait for as long as the
    /// button was held -- the ground under the player kept its old collision
    /// while the particle surface rose past it, and swallowed them. A section keeps its
    /// place in the queue when it is marked again, so every one is reached.
    /// </summary>
    private readonly HashSet<Vector3I> _urgent = new();
    private readonly Queue<Vector3I> _urgentOrder = new();

    /// <summary>
    /// Every section a round of edits changed, built together and uploaded in
    /// the same frame -- never some before others.
    ///
    /// One edit reshapes a surface that runs across several sections. Uploaded
    /// one by one as each worker finished, a pit's floor could leave the
    /// section above before it arrived in the section below: a frame or two
    /// with a hole in the ground, which showed the sky through the planet and
    /// could drop a player standing on it. Only one group is out at a time;
    /// edits made while it builds wait for the next.
    /// </summary>
    private sealed class EditGroup
    {
        public int Remaining;
        public readonly ConcurrentQueue<SectionGeometry> Done = new();
    }

    private EditGroup _group;

    /// <summary>Sections streaming queued, built in the order they arrived.</summary>
    private readonly HashSet<Vector3I> _backlog = new();
    private readonly Queue<Vector3I> _backlogOrder = new();

    /// <summary>
    /// The newest build uploaded for each section. Every build takes a ticket
    /// from one rising counter, and a result is uploaded only if its ticket is
    /// newer than what is already there -- so a slow worker finishing an old
    /// build can never overwrite geometry from a newer one.
    ///
    /// NEWER, NOT NEWEST. A section being dug is rebuilt every frame, and while
    /// the button is held there is always a newer build in flight; uploading
    /// only the very latest would show nothing until the digging stopped.
    /// </summary>
    private readonly Dictionary<Vector3I, long> _tickets = new();
    private long _nextTicket;

    private readonly ConcurrentQueue<SectionGeometry> _finished = new();
    private readonly ConcurrentBag<SectionGeometry> _spare = new();
    private int _inFlight;
    private int _generation;

    [ThreadStatic] private static SectionSample _sample;

    /// <summary>Sections uploaded since the world was configured.</summary>
    public int BuiltSections { get; private set; }

    /// <summary>Sections queued or building.</summary>
    public int PendingSections => _urgent.Count + _backlog.Count + Volatile.Read(ref _inFlight);

    public bool HasPendingMeshes => PendingSections > 0 || !_finished.IsEmpty || _group != null;

    private int WorkerCount => MeshWorkers > 0
        ? MeshWorkers
        : Math.Max(1, System.Environment.ProcessorCount / 2);

    /// <summary>Uploads finished sections and starts new builds, within a budget.</summary>
    public void Pump(float budgetMs)
    {
        if (Grid == null)
            return;

        ulong deadline = Time.GetTicksUsec() + (ulong)(Math.Max(0.1f, budgetMs) * 1000f);

        // A finished edit group goes up whole, whatever the budget: half of one
        // is exactly the hole the group exists to prevent.
        if (_group != null && Volatile.Read(ref _group.Remaining) == 0)
        {
            while (_group.Done.TryDequeue(out SectionGeometry edited))
                Apply(edited);

            _group = null;
        }

        while (Time.GetTicksUsec() < deadline && _finished.TryDequeue(out SectionGeometry done))
            Apply(done);

        if (_group == null && _urgent.Count > 0)
            StartGroup();

        // Streaming goes out in batches, since a task per section spends more
        // on scheduling than on the build of an empty section of sky.
        while (Volatile.Read(ref _inFlight) < WorkerCount)
        {
            var batch = new List<Vector3I>(StreamBatch);

            while (batch.Count < StreamBatch && TryTakeBacklog(out Vector3I section))
                batch.Add(section);

            if (batch.Count == 0)
                break;

            Dispatch(batch, null);
        }
    }

    /// <summary>Sections one streaming task builds.</summary>
    private const int StreamBatch = 16;

    /// <summary>Sends every edited section out as one group, spread over the workers.</summary>
    private void StartGroup()
    {
        var sections = new List<Vector3I>(_urgent.Count);

        while (TryTakeUrgent(out Vector3I section))
            sections.Add(section);

        var group = new EditGroup { Remaining = sections.Count };
        _group = group;

        int tasks = Math.Min(WorkerCount, sections.Count);

        for (int t = 0; t < tasks; t++)
        {
            var share = new List<Vector3I>(sections.Count / tasks + 1);

            for (int n = t; n < sections.Count; n += tasks)
                share.Add(sections[n]);

            Dispatch(share, group);
        }
    }

    /// <summary>
    /// Builds and uploads everything queued, on the calling thread, and waits
    /// for any worker still running. For tests and tools with no frame loop.
    /// </summary>
    public void MeshAllNow()
    {
        while (Volatile.Read(ref _inFlight) > 0)
            Thread.Yield();

        if (_group != null)
        {
            while (_group.Done.TryDequeue(out SectionGeometry edited))
                Apply(edited);

            _group = null;
        }

        while (_finished.TryDequeue(out SectionGeometry done))
            Apply(done);

        while (TryTakeUrgent(out Vector3I section) || TryTakeBacklog(out section))
        {
            SectionGeometry geometry = Build(section, ++_nextTicket, Volatile.Read(ref _generation));
            Apply(geometry);
        }
    }

    private bool TryTakeUrgent(out Vector3I section)
    {
        while (_urgentOrder.Count > 0)
        {
            section = _urgentOrder.Dequeue();

            if (_urgent.Remove(section))
                return true;
        }

        section = default;
        return false;
    }

    private bool TryTakeBacklog(out Vector3I section)
    {
        while (_backlogOrder.Count > 0)
        {
            section = _backlogOrder.Dequeue();

            if (_backlog.Remove(section))
                return true;
        }

        section = default;
        return false;
    }

    /// <summary>
    /// Builds sections on a worker. Results go to the group, when there is
    /// one, and otherwise straight to the upload queue.
    /// </summary>
    private void Dispatch(List<Vector3I> sections, EditGroup group)
    {
        var tickets = new long[sections.Count];
        for (int n = 0; n < sections.Count; n++)
            tickets[n] = ++_nextTicket;

        int generation = Volatile.Read(ref _generation);

        Interlocked.Increment(ref _inFlight);

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                for (int n = 0; n < sections.Count; n++)
                {
                    if (generation != Volatile.Read(ref _generation))
                        break;

                    // A section that fails still counts off, or its group
                    // would wait forever.
                    try
                    {
                        SectionGeometry built = Build(sections[n], tickets[n], generation);
                        (group?.Done ?? _finished).Enqueue(built);
                    }
                    catch (Exception error)
                    {
                        GD.PushError($"NodeWorld: building section {sections[n]} failed - {error}");
                    }
                    finally
                    {
                        if (group != null)
                            Interlocked.Decrement(ref group.Remaining);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        });
    }

    /// <summary>Runs every mesher over one section. Pure computation; any thread.</summary>
    private SectionGeometry Build(Vector3I section, long ticket, int generation)
    {
        if (!_spare.TryTake(out SectionGeometry geometry))
            geometry = new SectionGeometry();

        geometry.Clear();
        geometry.Section = section;
        geometry.Ticket = ticket;
        geometry.Generation = generation;

        // Open sky, border and all: nothing to draw, and no need to read a cell
        // to know it. Most of the sky around a player is this.
        Vector3I origin = SectionOrigin(section);
        Vector3I border = Vector3I.One * SectionSample.Border;

        if (Store.IsUniformAir(origin - border, origin + Vector3I.One * (SectionSample.Size - 1) + border))
            return geometry;

        SectionSample sample = _sample ??= new SectionSample();
        sample.Read(Store, Grid, origin);

        foreach (INodeMesher mesher in Meshers)
            mesher.Build(sample, Grid, geometry);

        return geometry;
    }

    /// <summary>Hands a finished section to the renderer and the physics server.</summary>
    private void Apply(SectionGeometry geometry)
    {
        Vector3I section = geometry.Section;

        bool current = geometry.Generation == Volatile.Read(ref _generation)
            && (!_tickets.TryGetValue(section, out long uploaded) || geometry.Ticket > uploaded)
            && Store.IsLoaded(ChunkOfSection(section));

        if (current)
        {
            _tickets[section] = geometry.Ticket;
            Upload(section, geometry);
            BuiltSections++;
        }

        _spare.Add(geometry);
    }

    private void Upload(Vector3I section, SectionGeometry geometry)
    {
        if (geometry.IsEmpty)
        {
            if (_sections.Remove(section, out SectionNodes gone))
                gone.Free();

            return;
        }

        if (!_sections.TryGetValue(section, out SectionNodes nodes))
        {
            EnsureBodies();
            nodes = new SectionNodes(this, _rawBody, _particleBody, section,
                Grid.LatticePoint(SectionOrigin(section)));
            _sections[section] = nodes;
        }

        var mesh = new ArrayMesh();

        if (!geometry.Raw.IsEmpty)
        {
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, geometry.Raw.ToArrays());
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, NodeMaterials.Raw);
        }

        var particleCollision = new List<Vector3>();

        foreach (byte material in geometry.ParticleMaterials)
        {
            MeshBuffers buffers = geometry.Particle(material);

            if (buffers.IsEmpty)
                continue;

            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, buffers.ToArrays());
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1,
                NodeMaterials.Particle((ParticleNode)NodeTypes.Of(material)));

            particleCollision.AddRange(buffers.Collision);
        }

        nodes.Mesh.Mesh = mesh;
        nodes.SetCollision(geometry.Raw.Collision, particleCollision);
    }

    // -------------------------------------------------------------- physics

    private StaticBody3D _rawBody;
    private StaticBody3D _particleBody;

    private void EnsureBodies()
    {
        if (_rawBody != null)
            return;

        _rawBody = new StaticBody3D { Name = "RawBody", CollisionLayer = CollisionLayer };
        _particleBody = new StaticBody3D { Name = "ParticleBody", CollisionLayer = CollisionLayer };
        AddChild(_rawBody);
        AddChild(_particleBody);
    }

    /// <summary>
    /// The first node surface between two global points.
    ///
    /// Asked of the physics server, which holds exactly the surfaces that are
    /// drawn, so the hit is the thing under the crosshair. Which body was hit
    /// says the form; the cell comes from nudging the point just inside the
    /// surface.
    /// </summary>
    public bool Raycast(Vector3 from, Vector3 to, out NodeHit hit,
        Godot.Collections.Array<Rid> exclude = null)
    {
        hit = default;

        if (Grid == null || _rawBody == null || !IsInsideTree())
            return false;

        var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayer);
        if (exclude != null)
            query.Exclude = exclude;

        Godot.Collections.Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);

        if (result.Count == 0)
            return false;

        var collider = result["collider"].As<GodotObject>();
        var point = (Vector3)result["position"];
        var normal = ((Vector3)result["normal"]).Normalized();
        float nudge = NodeSize * 0.02f;

        if (collider == _rawBody)
            return RawHit(point, normal, (to - from).Normalized(), nudge, out hit);

        if (collider == _particleBody)
        {
            hit = ParticleHit(point, normal);
            return true;
        }

        return false;
    }

    private bool RawHit(Vector3 point, Vector3 normal, Vector3 direction, float nudge, out NodeHit hit)
    {
        Vector3I cell = CellAt(point - normal * nudge);

        // Right at an edge the nudge along the normal can land in the
        // neighbour; stepping along the ray itself resolves it.
        if (TypeAt(cell) is not RawNode)
            cell = CellAt(point + direction * nudge);

        if (TypeAt(cell) is not RawNode type)
        {
            hit = default;
            return false;
        }

        hit = new NodeHit(point, normal, type, cell, CellAt(point + normal * nudge));
        return true;
    }

    private NodeHit ParticleHit(Vector3 point, Vector3 normal)
    {
        Vector3I cell = CellAt(point - normal * NodeSize * 0.25f);
        NodeType type = TypeAt(cell);

        // The smooth surface does not follow cell walls, so the cell just under
        // it may be air or rock; the nearest particle names the material.
        if (type is not ParticleNode)
        {
            type = NodeTypes.Sand;

            for (int n = 0; n < 27; n++)
            {
                var step = new Vector3I(n % 3 - 1, n / 3 % 3 - 1, n / 9 - 1);

                if (TypeAt(cell + step) is ParticleNode near)
                {
                    type = near;
                    break;
                }
            }
        }

        return new NodeHit(point, normal, type, cell, CellAt(point + normal * NodeSize * 0.25f));
    }

    // -------------------------------------------------------------- helpers

    public static Vector3I SectionOf(Vector3I cell) =>
        new(cell.X >> SectionShift, cell.Y >> SectionShift, cell.Z >> SectionShift);

    public static Vector3I SectionOrigin(Vector3I section) =>
        new(section.X << SectionShift, section.Y << SectionShift, section.Z << SectionShift);

    private static Vector3I ChunkOfSection(Vector3I section) =>
        new(section.X >> SectionsPerChunkShift, section.Y >> SectionsPerChunkShift,
            section.Z >> SectionsPerChunkShift);

    /// <summary>Triangles currently drawn. Diagnostic.</summary>
    public int TriangleCount
    {
        get
        {
            int total = 0;

            foreach (SectionNodes nodes in _sections.Values)
            {
                if (nodes.Mesh.Mesh is not ArrayMesh mesh)
                    continue;

                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                    total += mesh.SurfaceGetArrayIndexLen(s) / 3;
            }

            return total;
        }
    }

    /// <summary>
    /// The meshes currently drawn, each with the local position its vertices
    /// are relative to. For tests and tools.
    /// </summary>
    public IEnumerable<(Vector3 Origin, ArrayMesh Mesh)> SectionMeshes
    {
        get
        {
            foreach (SectionNodes nodes in _sections.Values)
            {
                if (nodes.Mesh.Mesh is ArrayMesh mesh)
                    yield return (nodes.Mesh.Position, mesh);
            }
        }
    }

    /// <summary>The scene nodes that draw and collide one section.</summary>
    private sealed class SectionNodes
    {
        public readonly MeshInstance3D Mesh;
        private readonly CollisionShape3D _raw;
        private readonly CollisionShape3D _particle;

        public SectionNodes(Node3D parent, StaticBody3D rawBody, StaticBody3D particleBody,
            Vector3I section, Vector3 origin)
        {
            string name = $"{section.X}_{section.Y}_{section.Z}";

            Mesh = new MeshInstance3D { Name = "Section" + name, Position = origin };
            _raw = new CollisionShape3D { Name = "Raw" + name, Position = origin };
            _particle = new CollisionShape3D { Name = "Particle" + name, Position = origin };

            parent.AddChild(Mesh);
            rawBody.AddChild(_raw);
            particleBody.AddChild(_particle);
        }

        /// <summary>
        /// Particle collision is solid from BOTH sides. Its triangles come from
        /// a surface that is reshaped every frame a shovel is held, and a
        /// one-sided sheet lets anything that ends up a hair behind it -- a
        /// frame of collision lag, one triangle wound the wrong way -- drop
        /// straight through and on down into the planet.
        /// </summary>
        public void SetCollision(List<Vector3> raw, List<Vector3> particle)
        {
            Assign(_raw, raw, twoSided: false);
            Assign(_particle, particle, twoSided: true);
        }

        private static void Assign(CollisionShape3D target, List<Vector3> triangles, bool twoSided)
        {
            if (triangles.Count == 0)
            {
                target.Shape = null;
                return;
            }

            if (target.Shape is ConcavePolygonShape3D shape)
                shape.Data = triangles.ToArray();
            else
                target.Shape = new ConcavePolygonShape3D
                {
                    Data = triangles.ToArray(),
                    BackfaceCollision = twoSided,
                };
        }

        /// <summary>
        /// Freed at once rather than queued, so the old collision is gone before
        /// anything casts against the new -- a queued free leaves two surfaces
        /// registered for the rest of the frame.
        /// </summary>
        public void Free()
        {
            Mesh.Free();
            _raw.Free();
            _particle.Free();
        }
    }
}
