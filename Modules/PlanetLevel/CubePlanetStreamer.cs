using Godot;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Streams the cube planet: a solid ball of stone blocks on the cubed-sphere
/// grid, meshed only where the player can see it.
///
/// The counterpart to <see cref="PlanetStreamer"/> with the density field taken
/// out. That one exists to build a planet with landscape; this one exists to
/// build a planet that reads as BLOCKS, so it has no terrain noise, no caverns
/// and no soil -- see <see cref="CubePlanetSource"/> for why relief works
/// against the look rather than for it.
///
/// What is left is the part that matters here: residency, the view test, and
/// the grid. Attach as a child of a NodeWorld.
/// </summary>
[Tool]
public partial class CubePlanetStreamer : ChunkStreamer
{
    /// <summary>The world to stream into. Defaults to the parent.</summary>
    [Export] public NodePath NodeWorldPath { get; set; } = "";

    private float _radius = 800f;

    /// <summary>
    /// Distance from the centre to the surface, in nodes -- so this is also the
    /// planet's radius in BLOCKS.
    ///
    /// The dial that makes a node feel like a cube. A block is one unit across
    /// whatever this is set to, so the larger the radius the flatter the ground
    /// underfoot and the more the surface reads as a field of cubes rather than
    /// as a ball. At 800 a face of the sphere is 1280 blocks across.
    /// </summary>
    [Export(PropertyHint.Range, "60,4000,1")]
    public float Radius
    {
        get => _radius;
        set { _radius = value; Reset(); }
    }

    private NodeWorld _world;

    /// <summary>
    /// One source per worker thread.
    ///
    /// The source is stateless and immutable, so this is not strictly needed --
    /// it keeps the shape identical to the other streamers, for one small
    /// object per core.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CubePlanetSource>
        _sources = new();

    /// <summary>
    /// The cubed-sphere grid this world's cells live on.
    ///
    /// Handed to the NodeWorld, which is what turns a cell from a cube into an
    /// ARC -- a wedge whose top and bottom faces lie on concentric shells and
    /// whose sides follow the planet's curve.
    /// </summary>
    public QuadSphereGrid Grid { get; private set; }

    protected override NodeWorld World => _world;

    public override void _Ready()
    {
        _world = !NodeWorldPath.IsEmpty ? GetNodeOrNull<NodeWorld>(NodeWorldPath) : null;
        _world ??= GetParentOrNull<NodeWorld>();

        if (_world == null)
        {
            GD.PushWarning("CubePlanetStreamer: needs a NodeWorld parent (or NodeWorldPath) - nothing streamed.");
            return;
        }

        base._Ready();
        Reset();
    }

    /// <summary>
    /// Where a player should stand to be on the surface above a direction.
    ///
    /// A planet has no constant spawn point, so this has to be asked rather
    /// than written into the scene. Shell 0 is the surface by definition and
    /// the ball is solid, so the ground is exactly one radius out along any
    /// direction -- no field to evaluate and no relief to allow for.
    /// </summary>
    public Vector3 SurfacePoint(Vector3 direction, float clearance = 3f)
    {
        QuadSphereGrid grid = Grid;
        if (grid == null)
            return Vector3.Zero;

        Vector3 unit = direction.LengthSquared() > 0.0001f
            ? direction.Normalized() : Vector3.Up;

        return unit * (grid.SurfaceRadius + clearance);
    }

    /// <summary>
    /// Rebuilds the grid and drops everything resident.
    ///
    /// Wholesale rather than incremental because the radius changes the grid's
    /// resolution, and every cell address in the world means something
    /// different afterwards.
    /// </summary>
    private void Reset()
    {
        if (_world == null)
            return;

        // Tables built once and shared: every worker's grid reads the same
        // immutable arrays rather than recomputing per-shell values.
        Grid = new QuadSphereGrid(new QuadSphereTables(_radius, 1f), Vector3.Zero);
        _world.Grid = Grid;

        // The old sources describe the old grid.
        _sources.Clear();

        _world.Clear();
        ForceRescan();
    }

    // ------------------------------------------------------------- residency

    /// <summary>
    /// Cell space has no altitude to scale the view by: a chunk coordinate is
    /// (u, v, shell), so flying away from the planet moves the player off the
    /// grid rather than to a larger shell index. What decides how much surface
    /// is in view is the plain load radius, in chunks across the face.
    /// </summary>
    protected override int EffectiveLoadRadius(Vector3I centre) => LoadRadius;

    /// <summary>
    /// The distance band is meaningless in cell space -- a chunk's shell range
    /// already says whether it can hold rock, and <see cref="CouldHoldAnything"/>
    /// checks it for the price of two integers. An empty band leaves that test
    /// to do the work.
    /// </summary>
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

    /// <summary>
    /// Chunks nearer than this are kept meshed whatever way the player faces,
    /// in NODES.
    ///
    /// The frustum test alone would drop the ground under the player's own feet
    /// the moment they looked at the horizon, and pop it back when they looked
    /// down. This is the "or within a certain radius" half: a sphere of
    /// guaranteed geometry around the player that the view test can only ADD
    /// to, never remove from.
    /// </summary>
    [Export(PropertyHint.Range, "8,400,1")]
    public float AlwaysRadius
    {
        get => _alwaysRadius;
        set { _alwaysRadius = value; ForceRescan(); }
    }

    private bool _cullToView = true;

    /// <summary>Turn the frustum test off to mesh the whole residency ball,
    /// which is what to do when measuring what the test is worth.</summary>
    [Export]
    public bool CullToView
    {
        get => _cullToView;
        set { _cullToView = value; ForceRescan(); }
    }

    private float _viewMargin = 0.25f;

    /// <summary>
    /// How far past the frustum edge to keep meshing, as a fraction of a
    /// chunk's own radius.
    ///
    /// Slack matters because the test runs on a RESCAN, not per frame: between
    /// scans the player can turn, and a chunk culled exactly at the frustum
    /// edge would be missing for the frames it takes to notice. Widening the
    /// cone is far cheaper than scanning more often.
    /// </summary>
    [Export(PropertyHint.Range, "0,1.5,0.05")]
    public float ViewMargin
    {
        get => _viewMargin;
        set { _viewMargin = value; ForceRescan(); }
    }

    private float _turnThreshold = 12f;

    /// <summary>
    /// How far the player must turn, in DEGREES, before the view is rescanned.
    ///
    /// Not zero, because a rescan walks the residency ball and mouse movement is
    /// continuous -- reacting to every twitch would scan every frame. The margin
    /// baked into the cone covers the gap: chunks are kept well past the frustum
    /// edge, so the view can drift this far before anything it should be showing
    /// is actually missing.
    /// </summary>
    [Export(PropertyHint.Range, "1,90,1")]
    public float TurnThreshold
    {
        get => _turnThreshold;
        set { _turnThreshold = value; }
    }

    private Camera3D _camera;

    // The view, snapshotted once per scan rather than read per chunk: a scan
    // tests thousands of chunks, and re-reading a moving camera inside it could
    // accept one chunk and reject its neighbour on a different frustum.
    private Vector3 _eye;
    private Vector3 _gaze;
    private Vector3 _scanGaze = Vector3.Zero;
    private float _cosLimit;
    private bool _haveView;

    /// <summary>Snapshots the camera for this scan.</summary>
    protected override void BeforeScan()
    {
        RefreshView();
        _scanGaze = _haveView ? _gaze : Vector3.Zero;
    }

    /// <summary>
    /// Reads the camera once, ahead of a scan.
    ///
    /// The half-angle used is the CORNER one, not the vertical field of view: a
    /// frustum's corners reach further than its edges, and culling on the
    /// vertical angle would clip the corners of the screen.
    /// </summary>
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

        // Vertical half-angle widened to the diagonal by the aspect ratio, then
        // by a fixed margin. Godot's Fov is the vertical field of view in
        // degrees under the default KeepAspect of Height.
        float half = Mathf.DegToRad(_camera.Fov * 0.5f);
        Vector2 size = GetViewport().GetVisibleRect().Size;
        float aspect = size.Y > 0f ? Mathf.Max(1f, size.X / size.Y) : 1f;

        float diagonal = Mathf.Atan(Mathf.Tan(half) * Mathf.Sqrt(1f + aspect * aspect));
        _cosLimit = Mathf.Cos(Mathf.Min(diagonal + 0.35f, Mathf.Pi * 0.98f));

        _haveView = true;
    }

    /// <summary>Has the camera turned past the threshold since the last scan?</summary>
    protected override bool ViewChanged()
    {
        if (!_cullToView)
            return false;

        Camera3D camera = _camera != null && IsInstanceValid(_camera)
            ? _camera : GetViewport()?.GetCamera3D();

        if (camera == null)
            return false;

        // No snapshot yet: the first frame with a camera is owed a scan.
        if (_scanGaze == Vector3.Zero)
            return true;

        Vector3 facing = -camera.GlobalTransform.Basis.Z.Normalized();
        return facing.Dot(_scanGaze) < Mathf.Cos(Mathf.DegToRad(_turnThreshold));
    }

    /// <summary>
    /// Is this chunk in front of the player, or close enough not to care?
    ///
    /// The chunk is tested as a SPHERE around its centre rather than as a box of
    /// eight corners: cone-versus-sphere is one dot product and a compare,
    /// against eight projections for the box -- and on a curved grid a chunk's
    /// corners are not where a box's corners would be anyway.
    /// </summary>
    protected override bool CouldBeSeen(Vector3I chunk)
    {
        QuadSphereGrid grid = Grid;
        if (!_haveView || grid == null)
            return true;

        // The chunk's middle cell, in world space. A chunk here is an arced box,
        // so its centre comes from the grid rather than from averaging
        // coordinates.
        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        const int Half = NodeChunkStore.ChunkSize / 2;
        var middle = new Vector3I(origin.X + Half, origin.Y + Half, origin.Z + Half);

        // An address the grid does not contain says nothing about visibility, so
        // it is left to CouldHoldAnything rather than culled here.
        if (!grid.Contains(middle))
            return true;

        Vector3 centre = grid.CentreOf(middle);

        // The radius of a sphere enclosing the chunk: half a chunk's diagonal,
        // so a chunk straddling the frustum edge counts as visible.
        float chunkRadius = NodeChunkStore.ChunkSize * grid.NodeSize * 0.87f;

        Vector3 offset = centre - _eye;
        float distance = offset.Length();

        // NEAR THE PLAYER: kept whatever way they face. This is also what makes
        // the test safe -- the chunk the player stands in is never culled,
        // however the camera swings.
        if (distance <= _alwaysRadius + chunkRadius)
            return true;

        // Cone test, widened by the angle the chunk's own radius subtends at
        // this distance so a chunk near the edge is not clipped in half.
        float cos = offset.Dot(_gaze) / distance;
        float slack = Mathf.Min(1f, chunkRadius * (1f + _viewMargin) / distance);

        return cos >= _cosLimit - slack;
    }

    // ------------------------------------------------------------ generation

    /// <summary>A source on the calling thread, for the residency scan.</summary>
    private CubePlanetSource SourceFor()
    {
        QuadSphereGrid grid = Grid;

        CubePlanetSource source = _sources.GetOrAdd(
            System.Environment.CurrentManagedThreadId,
            _ => new CubePlanetSource(grid));

        // The grid was rebuilt under this thread's source: replace it, or the
        // chunk would be filled against the world as it used to be.
        if (!ReferenceEquals(source.Grid, grid))
        {
            source = new CubePlanetSource(grid);
            _sources[System.Environment.CurrentManagedThreadId] = source;
        }

        return source;
    }

    /// <summary>Generates one chunk, on whichever worker thread is calling.</summary>
    protected override int GenerateChunk(Vector3I chunk, byte[] cells)
    {
        if (Grid == null)
            return 0;

        return SourceFor().Generate(chunk, cells);
    }
}
