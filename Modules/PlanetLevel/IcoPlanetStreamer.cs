using Godot;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Streams the even-node planet: a solid ball of stone on the icosphere grid.
///
/// The counterpart to <see cref="CubePlanetStreamer"/>, and the same shape --
/// residency, the view test, the grid. What differs is underneath: nodes here
/// are hexagonal prisms whose walls are the bisectors between neighbouring
/// sites, so the surface reads as an even field rather than as rhombuses that
/// tilt toward the seams.
/// </summary>
[Tool]
public partial class IcoPlanetStreamer : ChunkStreamer
{
    /// <summary>The world to stream into. Defaults to the parent.</summary>
    [Export] public NodePath NodeWorldPath { get; set; } = "";

    private float _radius = 120f;

    /// <summary>
    /// Distance from the centre to the surface, in nodes.
    ///
    /// Sets how many sites the surface carries, and so how fine the lattice is.
    /// </summary>
    [Export(PropertyHint.Range, "30,2000,1")]
    public float Radius
    {
        get => _radius;
        set { _radius = value; Reset(); }
    }

    private NodeWorld _world;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IcoPlanetSource>
        _sources = new();

    /// <summary>The icosphere grid this world's nodes live on.</summary>
    public IcoSphereGrid Grid { get; private set; }

    protected override NodeWorld World => _world;

    public override void _Ready()
    {
        _world = !NodeWorldPath.IsEmpty ? GetNodeOrNull<NodeWorld>(NodeWorldPath) : null;
        _world ??= GetParentOrNull<NodeWorld>();

        if (_world == null)
        {
            GD.PushWarning("IcoPlanetStreamer: needs a NodeWorld parent (or NodeWorldPath) - nothing streamed.");
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
        IcoSphereGrid grid = Grid;
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

        Grid = new IcoSphereGrid(new IcoSphereTables(_radius, 1f), Vector3.Zero);
        _world.Grid = Grid;

        _sources.Clear();
        _world.Clear();
        ForceRescan();
    }

    // ------------------------------------------------------------- residency

    /// <summary>Cell space has no altitude to scale the view by, so the plain
    /// load radius decides how much surface is resident.</summary>
    protected override int EffectiveLoadRadius(Vector3I centre) => LoadRadius;

    /// <summary>The distance band is meaningless in cell space -- a chunk's
    /// shell range already says whether it can hold rock.</summary>
    protected override void ChunkBand(Vector3I centre, int radius, out int lo, out int hi)
    {
        lo = 1;
        hi = 0;
    }

    /// <summary>Rejects chunks of open space before they are queued.</summary>
    protected override bool CouldHoldAnything(Vector3I chunk) => SourceFor().CouldHoldRock(chunk);

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
        IcoSphereGrid grid = Grid;
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

    private IcoPlanetSource SourceFor()
    {
        IcoSphereGrid grid = Grid;

        IcoPlanetSource source = _sources.GetOrAdd(
            System.Environment.CurrentManagedThreadId,
            _ => new IcoPlanetSource(grid));

        if (!ReferenceEquals(source.Grid, grid))
        {
            source = new IcoPlanetSource(grid);
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
