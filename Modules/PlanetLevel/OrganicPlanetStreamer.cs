using Godot;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Streams the organic planet: a solid ball of irregular Voronoi rock.
///
/// The third streamer, and the same shape as the other two -- residency, the
/// view test, the grid. What differs is underneath: a node here is the region
/// closer to its own jittered site than to any other, so cells are irregular
/// polyhedra of about fifteen faces and the world has no grain to it at all.
/// </summary>
[Tool]
public partial class OrganicPlanetStreamer : ChunkStreamer
{
    /// <summary>The world to stream into. Defaults to the parent.</summary>
    [Export] public NodePath NodeWorldPath { get; set; } = "";

    private float _radius = 120f;

    /// <summary>Distance from the centre to the surface, in nodes.</summary>
    [Export(PropertyHint.Range, "30,2000,1")]
    public float Radius
    {
        get => _radius;
        set { _radius = value; Reset(); }
    }

    private float _nodeSize = 2f;

    /// <summary>
    /// How wide one node is.
    ///
    /// LARGER HERE THAN ON THE OTHER WORLDS, and deliberately. A Voronoi cell
    /// costs about a hundred times what a cubed-sphere block does to build --
    /// twenty bisector planes clipped against each other rather than six fixed
    /// faces -- and that cost is per CELL. Doubling the node size puts eight
    /// times fewer of them in the same planet, which is the only lever that
    /// moves the total by the order of magnitude needed.
    ///
    /// It also suits the look: irregular rock reads better at a size where the
    /// shape of an individual cell is legible.
    /// </summary>
    [Export(PropertyHint.Range, "0.5,8,0.25")]
    public float NodeSize
    {
        get => _nodeSize;
        set { _nodeSize = value; Reset(); }
    }

    private float _jitter = 0.5f;

    /// <summary>
    /// How far a site may wander from its lattice point, as a fraction of a
    /// cell.
    ///
    /// Zero gives a plain cubic lattice -- every node an identical cube -- and
    /// the rock looks machined. Raising it breaks the regularity up; at 0.5 a
    /// cell has about fifteen faces and no two are alike.
    ///
    /// Capped at 0.5 by the grid, and not arbitrarily: the lookup searches the
    /// twenty-seven cells around a point, which is only guaranteed to contain
    /// the true owner while a site stays within a quarter-cell of its lattice
    /// point. Past that, digging and collision start resolving to the wrong
    /// node for some points and not others.
    /// </summary>
    [Export(PropertyHint.Range, "0,0.5,0.05")]
    public float Jitter
    {
        get => _jitter;
        set { _jitter = value; Reset(); }
    }

    private float _soilLayers = 3f;

    /// <summary>
    /// How many layers of nodes deep the topsoil runs.
    ///
    /// Given in LAYERS rather than world units so it means the same thing at
    /// any node size -- three layers is three nodes down whether a node is one
    /// unit across or four.
    ///
    /// The layers are approximate by nature: sites are jittered, so the number
    /// of nodes between the surface and the rock varies from place to place
    /// around the figure asked for. That variation is the point of the world,
    /// not a shortfall in it.
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.5")]
    public float SoilLayers
    {
        get => _soilLayers;
        set { _soilLayers = value; Reset(); }
    }

    private NodeWorld _world;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, OrganicPlanetSource>
        _sources = new();

    /// <summary>The Voronoi grid this world's nodes live on.</summary>
    public OrganicGrid Grid { get; private set; }

    protected override NodeWorld World => _world;

    public override void _Ready()
    {
        _world = !NodeWorldPath.IsEmpty ? GetNodeOrNull<NodeWorld>(NodeWorldPath) : null;
        _world ??= GetParentOrNull<NodeWorld>();

        if (_world == null)
        {
                GD.PushWarning("OrganicPlanetStreamer: needs a NodeWorld parent"
                + " (or NodeWorldPath) - nothing streamed.");
            return;
        }

        base._Ready();
        Reset();
    }

    /// <summary>
    /// Where a player should stand to be on the surface above a direction.
    ///
    /// Shell 0 is the surface by definition and the ball is solid, so the
    /// ground is one radius out along any direction.
    /// </summary>
    public Vector3 SurfacePoint(Vector3 direction, float clearance = 3f)
    {
        OrganicGrid grid = Grid;
        if (grid == null)
            return Vector3.Zero;

        Vector3 unit = direction.LengthSquared() > 0.0001f
            ? direction.Normalized() : Vector3.Up;

        return unit * (grid.SurfaceRadius + clearance);
    }

    /// <summary>Rebuilds the grid and drops everything resident.</summary>
    private void Reset()
    {
        if (_world == null)
            return;

        Grid = new OrganicGrid(_radius, _nodeSize, Vector3.Zero, _jitter, _soilLayers);

        _world.Grid = Grid;

        _sources.Clear();
        _world.Clear();
        ForceRescan();
    }

    // ------------------------------------------------------------- residency

    /// <summary>The plain load radius decides how much world is resident.</summary>
    protected override int EffectiveLoadRadius(Vector3I centre) => LoadRadius;

    /// <summary>
    /// The distance band is left empty, as on the other planets.
    ///
    /// A chunk here is a box in the same space the planet is a ball in, so
    /// whether it can hold rock is a box-versus-sphere test that
    /// <see cref="CouldHoldAnything"/> already does exactly -- and it is
    /// sharper than any band on a coordinate axis could be.
    /// </summary>
    protected override void ChunkBand(Vector3I centre, int radius, out int lo, out int hi)
    {
        lo = 1;
        hi = 0;
    }

    private float _shellDepth = 48f;

    /// <summary>
    /// How deep below the surface to stream, in nodes.
    ///
    /// THE ORGANIC WORLD NEEDS THIS AND THE OTHERS DO NOT. On the cubed sphere
    /// and the icosphere a chunk coordinate is (u, v, shell), so the streamer's
    /// residency ball is a patch of SURFACE by however many chunks of depth,
    /// and the shell axis bounds it naturally.
    ///
    /// Here a chunk coordinate is a box in space, so the same ball is a ball in
    /// SPACE: at the default load radius it spans 256 nodes across, centred on
    /// a player standing 120 from the middle -- which is the entire planet,
    /// core included. Measured before this existed: 3692 chunks resident, the
    /// streamer never reaching ready, and the player falling through a world
    /// that was still building its interior.
    ///
    /// Rock deeper than this is skipped, exactly as the other worlds skip
    /// shells past their count.
    /// </summary>
    [Export(PropertyHint.Range, "8,400,1")]
    public float ShellDepth
    {
        get => _shellDepth;
        set { _shellDepth = value; ForceRescan(); }
    }

    /// <summary>
    /// Rejects chunks of open space, and chunks buried too deep to matter,
    /// before either is queued.
    /// </summary>
    protected override bool CouldHoldAnything(Vector3I chunk)
    {
        if (!SourceFor().CouldHoldRock(chunk))
            return false;

        OrganicGrid grid = Grid;
        if (grid == null)
            return false;

        // BURIED CHUNKS ARE KEPT, not skipped.
        //
        // Skipping them looks like an easy saving and is not: the mesher treats
        // a neighbour whose chunk is absent as SOLID, which is what stops the
        // underside of the loaded region opening onto the core. A chunk dropped
        // for being deep therefore hides the faces of the chunk above it too,
        // and the surface comes apart -- measured at 1445 buried faces drawn
        // and most of the ground missing entirely.
        //
        // What bounds the world here is the sphere test above, and the
        // streamer's own load radius. Depth is left to those.
        return true;
    }

    // ------------------------------------------------------------ visibility

    [ExportGroup("Visibility")]
    private float _alwaysRadius = 48f;

    /// <summary>Chunks nearer than this stay meshed whatever way the player
    /// faces, so the ground underfoot never pops.</summary>
    [Export(PropertyHint.Range, "8,400,1")]
    public float AlwaysRadius
    {
        get => _alwaysRadius;
        set { _alwaysRadius = value; ForceRescan(); }
    }

    private bool _cullToView = true;

    /// <summary>Turn the frustum test off to mesh the whole residency ball.</summary>
    [Export]
    public bool CullToView
    {
        get => _cullToView;
        set { _cullToView = value; ForceRescan(); }
    }

    private float _viewMargin = 0.6f;

    /// <summary>How far past the frustum edge to keep meshing, as a fraction of
    /// a chunk's own radius.</summary>
    [Export(PropertyHint.Range, "0,1.5,0.05")]
    public float ViewMargin
    {
        get => _viewMargin;
        set { _viewMargin = value; ForceRescan(); }
    }

    private float _turnThreshold = 12f;

    /// <summary>How far the player must turn, in degrees, before the view is
    /// rescanned.</summary>
    [Export(PropertyHint.Range, "1,90,1")]
    public float TurnThreshold
    {
        get => _turnThreshold;
        set { _turnThreshold = value; }
    }

    private Camera3D _camera;
    private Vector3 _eye;
    private Vector3 _gaze;
    private Vector3 _scanGaze = Vector3.Zero;
    private float _cosLimit;
    private bool _haveView;

    protected override void BeforeScan()
    {
        RefreshView();
        _scanGaze = _haveView ? _gaze : Vector3.Zero;
    }

    private void RefreshView()
    {
        _haveView = false;
        if (!_cullToView)
            return;

        if (_camera == null || !IsInstanceValid(_camera))
            _camera = GetViewport()?.GetCamera3D();

        if (_camera == null)
            return;

        Transform3D transform = _camera.GlobalTransform;
        _eye = transform.Origin;
        _gaze = -transform.Basis.Z.Normalized();

        float half = Mathf.DegToRad(_camera.Fov * 0.5f);
        Vector2 size = GetViewport().GetVisibleRect().Size;
        float aspect = size.Y > 0f ? Mathf.Max(1f, size.X / size.Y) : 1f;

        float diagonal = Mathf.Atan(Mathf.Tan(half) * Mathf.Sqrt(1f + aspect * aspect));
        _cosLimit = Mathf.Cos(Mathf.Min(diagonal + 0.35f, Mathf.Pi * 0.98f));

        _haveView = true;
    }

    protected override bool ViewChanged()
    {
        if (!_cullToView)
            return false;

        Camera3D camera = _camera != null && IsInstanceValid(_camera)
            ? _camera : GetViewport()?.GetCamera3D();

        if (camera == null)
            return false;

        if (_scanGaze == Vector3.Zero)
            return true;

        Vector3 facing = -camera.GlobalTransform.Basis.Z.Normalized();
        return facing.Dot(_scanGaze) < Mathf.Cos(Mathf.DegToRad(_turnThreshold));
    }

    /// <summary>Is this chunk in front of the player, or close enough not to
    /// care?</summary>
    protected override bool CouldBeSeen(Vector3I chunk)
    {
        OrganicGrid grid = Grid;
        if (!_haveView || grid == null)
            return true;

        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        const int Half = NodeChunkStore.ChunkSize / 2;
        var middle = new Vector3I(origin.X + Half, origin.Y + Half, origin.Z + Half);

        if (!grid.Contains(middle))
            return true;

        Vector3 centre = grid.CentreOf(middle);
        float chunkRadius = NodeChunkStore.ChunkSize * grid.NodeSize * 0.87f;

        Vector3 offset = centre - _eye;
        float distance = offset.Length();

        if (distance <= _alwaysRadius + chunkRadius)
            return true;

        float cos = offset.Dot(_gaze) / distance;
        float slack = Mathf.Min(1f, chunkRadius * (1f + _viewMargin) / distance);

        return cos >= _cosLimit - slack;
    }

    // ------------------------------------------------------------ generation

    private OrganicPlanetSource SourceFor()
    {
        OrganicGrid grid = Grid;

        OrganicPlanetSource source = _sources.GetOrAdd(
            System.Environment.CurrentManagedThreadId,
            _ => new OrganicPlanetSource(grid));

        if (!ReferenceEquals(source.Grid, grid))
        {
            source = new OrganicPlanetSource(grid);
            _sources[System.Environment.CurrentManagedThreadId] = source;
        }

        return source;
    }

    protected override int GenerateChunk(Vector3I chunk, byte[] cells)
    {
        if (Grid == null)
            return 0;

        return SourceFor().Generate(chunk, cells);
    }
}
