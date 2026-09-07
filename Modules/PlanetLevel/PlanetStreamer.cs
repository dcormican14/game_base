using Godot;
using GameBase.Density;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Streams a whole planet around the player.
///
/// The planet's counterpart to <see cref="IslandStreamer"/>, and deliberately
/// the same shape: it owns a field, hands chunks to a source, and lets
/// <see cref="ChunkStreamer"/> decide what to ask for and when. The two are
/// interchangeable — swap which one is a child of the NodeWorld and the same
/// pipeline builds a different world.
///
/// That interchangeability is the point of the split. Nothing below this class
/// knows what shape the world is; the field is just a function of position, so
/// a sphere costs the streaming layer nothing that a field of islands did not
/// already cost it.
///
/// Attach as a child of a NodeWorld.
/// </summary>
[Tool]
public partial class PlanetStreamer : ChunkStreamer
{
    /// <summary>The world to stream into. Defaults to the parent.</summary>
    [Export] public NodePath NodeWorldPath { get; set; } = "";

    private int _seed = 90210;

    [ExportGroup("World")]
    [Export]
    public int Seed
    {
        get => _seed;
        set { _seed = value; Reset(); }
    }

    private float _radius = 400f;
    /// <summary>Distance from the centre to mean sea level, in nodes.</summary>
    [Export(PropertyHint.Range, "60,4000,1")]
    public float Radius
    {
        get => _radius;
        set { _radius = value; Reset(); }
    }

    [ExportGroup("Terrain")]
    private float _terrainHeight = 34f;
    /// <summary>Nodes between the deepest basin and the highest peak.</summary>
    [Export(PropertyHint.Range, "0,300,1")]
    public float TerrainHeight
    {
        get => _terrainHeight;
        set { _terrainHeight = value; Reset(); }
    }

    private float _terrainScale = 420f;
    /// <summary>Nodes per lobe of the coarsest terrain octave — continent size.</summary>
    [Export(PropertyHint.Range, "40,3000,5")]
    public float TerrainScale
    {
        get => _terrainScale;
        set { _terrainScale = value; Reset(); }
    }

    private int _terrainOctaves = 4;
    [Export(PropertyHint.Range, "1,8,1")]
    public int TerrainOctaves
    {
        get => _terrainOctaves;
        set { _terrainOctaves = value; Reset(); }
    }

    private float _ridged = 0.55f;
    /// <summary>0 rolling dunes, 1 sharp mountain chains.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float Ridged
    {
        get => _ridged;
        set { _ridged = value; Reset(); }
    }

    private float _warpStrength = 0.22f;
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float WarpStrength
    {
        get => _warpStrength;
        set { _warpStrength = value; Reset(); }
    }

    private float _warpScale = 160f;
    [Export(PropertyHint.Range, "20,800,5")]
    public float WarpScale
    {
        get => _warpScale;
        set { _warpScale = value; Reset(); }
    }

    [ExportGroup("Core")]
    private float _solidDepth = 48f;
    /// <summary>How deep the crust stays completely solid, in nodes.</summary>
    [Export(PropertyHint.Range, "0,400,1")]
    public float SolidDepth
    {
        get => _solidDepth;
        set { _solidDepth = value; Reset(); }
    }

    private float _coreDensity = 0.25f;
    /// <summary>Fraction of cells still filled at the very centre.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float CoreDensity
    {
        get => _coreDensity;
        set { _coreDensity = value; Reset(); }
    }

    private float _coreFalloff = 1.6f;
    /// <summary>1 thins linearly with depth; higher keeps the mantle solid.</summary>
    [Export(PropertyHint.Range, "0.2,6,0.05")]
    public float CoreFalloff
    {
        get => _coreFalloff;
        set { _coreFalloff = value; Reset(); }
    }

    private float _coreScale = 38f;
    /// <summary>Nodes per lobe of the cavern noise — how big the voids are.</summary>
    [Export(PropertyHint.Range, "6,300,1")]
    public float CoreScale
    {
        get => _coreScale;
        set { _coreScale = value; Reset(); }
    }

    private int _coreOctaves = 3;
    [Export(PropertyHint.Range, "1,6,1")]
    public int CoreOctaves
    {
        get => _coreOctaves;
        set { _coreOctaves = value; Reset(); }
    }

    private float _innerCore = 16f;
    /// <summary>Radius of the solid inner core, in nodes.</summary>
    [Export(PropertyHint.Range, "0,200,1")]
    public float InnerCore
    {
        get => _innerCore;
        set { _innerCore = value; Reset(); }
    }

    [ExportGroup("Surface")]
    private float _capThickness = 3f;
    /// <summary>Nodes of topsoil over the crust.</summary>
    [Export(PropertyHint.Range, "0,12,0.5")]
    public float CapThickness
    {
        get => _capThickness;
        set { _capThickness = value; Reset(); }
    }

    private NodeWorld _world;

    /// <summary>
    /// One chunk source per worker thread.
    ///
    /// The planet source is stateless, so this is not the correctness
    /// requirement it is for islands — but keeping the shape identical means
    /// the two streamers stay comparable, and it costs one small object per
    /// core.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, PlanetChunkSource>
        _sources = new();

    /// <summary>The field being streamed, for anything that wants to sample it
    /// (spawn logic, a horizon, a minimap).</summary>
    public PlanetDensity Field { get; private set; }

    protected override NodeWorld World => _world;

    public override void _Ready()
    {
        _world = !NodeWorldPath.IsEmpty ? GetNodeOrNull<NodeWorld>(NodeWorldPath) : null;
        _world ??= GetParentOrNull<NodeWorld>();

        if (_world == null)
        {
            GD.PushWarning("PlanetStreamer: needs a NodeWorld parent (or NodeWorldPath) — nothing streamed.");
            return;
        }

        base._Ready();
        Reset();
    }

    /// <summary>
    /// Where a player should stand to be on the surface above a direction.
    ///
    /// The planet has no fixed "up", so a spawn point cannot be a constant.
    /// This puts the player just clear of the ground along `direction`, which
    /// is what a level scene needs to place them.
    /// </summary>
    public Vector3 SurfacePoint(Vector3 direction, float clearance = 3f)
    {
        PlanetDensity field = Field;
        if (field == null)
            return Vector3.Zero;

        Vector3 unit = direction.LengthSquared() > 0.0001f
            ? direction.Normalized() : Vector3.Up;

        return unit * (field.GroundRadius(unit) + clearance);
    }

    /// <summary>
    /// Rebuilds the field and drops everything resident, so the world reflects
    /// the current dials.
    ///
    /// Wholesale rather than incremental because every dial here changes the
    /// field everywhere: there is no such thing as a partially stale world
    /// when the shape function itself has changed.
    /// </summary>
    private void Reset()
    {
        if (_world == null)
            return;

        Field = BuildField();

        // The old sources describe the old field. Dropping them makes the next
        // job on each thread build one against the field now in force.
        _sources.Clear();

        _world.Clear();
        ForceRescan();
    }

    private PlanetDensity BuildField() => new(_seed)
    {
        Radius = _radius,
        TerrainHeight = _terrainHeight,
        TerrainScale = _terrainScale,
        TerrainOctaves = _terrainOctaves,
        Ridged = _ridged,
        WarpStrength = _warpStrength,
        WarpScale = _warpScale,
        SolidDepth = _solidDepth,
        CoreDensity = _coreDensity,
        CoreFalloff = _coreFalloff,
        CoreScale = _coreScale,
        CoreOctaves = _coreOctaves,
        InnerCore = _innerCore,
        CapThickness = _capThickness,
    };

    private int _maxLoadRadius = 256;
    /// <summary>
    /// The furthest the view may reach when high above the planet, in chunks.
    ///
    /// Only reached from altitude, and only ever spent on chunks that survive
    /// the sky reject -- so the cost is the planet's SURFACE in view, not the
    /// volume of the ball.
    /// </summary>
    [Export(PropertyHint.Range, "4,120,1")]
    public int MaxLoadRadius
    {
        get => _maxLoadRadius;
        set { _maxLoadRadius = value; ForceRescan(); }
    }

    /// <summary>
    /// View distance grows with height above the ground.
    ///
    /// A fixed radius is a fixed number of chunks in every direction. Standing
    /// on the surface that is right; in the air it is not, because the ground
    /// falls out of the ball entirely and every resident chunk is sky.
    /// Measured while flying straight up, at 3500 nodes the nearest rock sat 97
    /// chunks below a radius of 3, and the streamer was churning through
    /// hundreds of guaranteed-empty chunks with nothing to show.
    ///
    /// Reaching just past the ground is what matters: the radius is the
    /// altitude in chunks plus the ordinary ground-level radius, so the surface
    /// stays inside the ball however high the player goes. It is affordable
    /// only because CouldHoldAnything discards the sky first -- the ball itself
    /// grows as the cube of this, while what is actually queued grows as the
    /// visible area of a sphere.
    /// </summary>
    protected override int EffectiveLoadRadius(Vector3I centre)
    {
        PlanetDensity field = Field;
        if (field == null)
            return LoadRadius;

        // Height above the mean surface, in chunks, from the scan centre.
        var at = new Vector3(centre.X, centre.Y, centre.Z) * NodeChunkStore.ChunkSize;
        float altitude = at.Length() - field.Radius;
        if (altitude <= 0f)
            return LoadRadius;

        int reach = LoadRadius + Mathf.CeilToInt(altitude / NodeChunkStore.ChunkSize);
        return Mathf.Min(reach, Mathf.Max(LoadRadius, MaxLoadRadius));
    }

    /// <summary>
    /// The shell of chunk distances a planet's rock can occupy.
    ///
    /// Everything nearer than the deepest basin is inside the planet and
    /// everything past the highest peak is space, so the scan only has to
    /// consider chunks whose distance from the centre falls between. That
    /// turns a radius-80 sweep from four million chunks into the few thousand
    /// that make up the visible crust.
    ///
    /// Generous at both ends by a chunk, since a chunk is a box and its corners
    /// reach further than its centre.
    /// </summary>
    protected override void ChunkBand(Vector3I centre, int radius, out int lo, out int hi)
    {
        PlanetDensity field = Field;
        if (field == null)
        {
            lo = 1; hi = 0;
            return;
        }

        const float Size = NodeChunkStore.ChunkSize;

        // In chunks, with a chunk of slack for the box corners.
        float inner = Mathf.Max(0f, (field.Radius - field.TerrainHeight) / Size - 1.8f);
        float outer = field.MaxRadius / Size + 1.8f;

        lo = Mathf.FloorToInt(inner * inner);
        hi = Mathf.CeilToInt(outer * outer);
    }

    /// <summary>
    /// Rejects chunks of open space before they are queued.
    ///
    /// Without this the residency ball has to be small, because its cost grows
    /// with the cube of the radius and a planet sits inside an enormous volume
    /// of sky. With it the scan can reach far enough to keep the planet in view
    /// from altitude, since everything it skips is space that provably holds
    /// nothing.
    /// </summary>
    protected override bool CouldHoldAnything(Vector3I chunk)
    {
        PlanetDensity field = Field;
        if (field == null)
            return true;

        return SourceFor().CouldHoldRock(chunk);
    }

    /// <summary>A source on the calling thread, for the residency scan.</summary>
    private PlanetChunkSource SourceFor()
    {
        PlanetDensity field = Field;
        PlanetChunkSource source = _sources.GetOrAdd(
            System.Environment.CurrentManagedThreadId,
            _ => new PlanetChunkSource(field));

        if (!ReferenceEquals(source.Field, field))
        {
            source = new PlanetChunkSource(field);
            _sources[System.Environment.CurrentManagedThreadId] = source;
        }

        return source;
    }

    /// <summary>
    /// Generates one chunk, on whichever worker thread is calling.
    ///
    /// The field is immutable and allocation-free, so it is safe to share; the
    /// source is per-thread all the same, matching the island streamer.
    /// </summary>
    protected override int GenerateChunk(Vector3I chunk, byte[] cells)
    {
        PlanetDensity field = Field;
        if (field == null)
            return 0;

        PlanetChunkSource source = _sources.GetOrAdd(
            System.Environment.CurrentManagedThreadId,
            _ => new PlanetChunkSource(field));

        // A source built against a superseded field would generate terrain
        // that disagrees with its neighbours.
        if (!ReferenceEquals(source.Field, field))
        {
            source = new PlanetChunkSource(field);
            _sources[System.Environment.CurrentManagedThreadId] = source;
        }

        return source.Generate(chunk, cells);
    }
}
