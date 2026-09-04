using Godot;
using GameBase.Density;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Streams an endless field of floating islands around the player.
///
/// The replacement for <see cref="IslandLevel"/>'s fixed region. That class
/// generated a box of a chosen radius, once, and everything outside it simply
/// did not exist — the region dial was the world's size, and it was bounded by
/// memory rather than by anything about the world. This keeps a ball of chunks
/// resident around the player instead, generating what comes into range and
/// freeing what leaves.
///
/// Nothing about the field changed to allow that. It was always defined over
/// all of space, a pure function of position and seed; what was missing was a
/// driver that treated it as a window rather than as an extent, and storage
/// that could give memory back. Both now exist, so this class is small: it
/// owns the field, hands chunks to <see cref="IslandChunkSource"/>, and lets
/// <see cref="ChunkStreamer"/> decide what to ask for and when.
///
/// Attach as a child of a NodeWorld, in place of the IslandLevel generator.
/// </summary>
[Tool]
public partial class IslandStreamer : ChunkStreamer
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

    // Placement and shaping. These mirror IslandLevel's dials exactly — the
    // field is the same field, and this class only changes how much of it is
    // built at a time.
    [ExportGroup("Placement")]
    [Export(PropertyHint.Range, "20,400,1")] public float Spacing { get; set; } = 190f;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float IslandDensity { get; set; } = 0.8f;
    [Export(PropertyHint.Range, "3,60,0.5")] public float MinRadius { get; set; } = 8f;
    [Export(PropertyHint.Range, "6,300,0.5")] public float MaxRadius { get; set; } = 135f;
    [Export(PropertyHint.Range, "1,6,0.1")] public float SizeBias { get; set; } = 1.85f;
    [Export(PropertyHint.Range, "0.3,6,0.05")] public float DepthRatio { get; set; } = 2.2f;
    [Export(PropertyHint.Range, "20,600,5")] public float MaxDepth { get; set; } = 230f;
    [Export(PropertyHint.Range, "0,3,0.05")] public float VerticalSpread { get; set; } = 1.15f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float Clustering { get; set; } = 0.95f;
    [Export(PropertyHint.Range, "60,2000,10")] public float ClusterScale { get; set; } = 620f;
    [Export(PropertyHint.Range, "1,6,1")] public int MaxPerCell { get; set; } = 5;
    [Export(PropertyHint.Range, "0.5,6,0.1")] public float ClusterContrast { get; set; } = 3.4f;

    /// <summary>
    /// Guarantee an island at the origin, so the player has ground to spawn
    /// onto. Unlike the fixed region, an endless world does not otherwise
    /// promise anything in particular where the player starts.
    /// </summary>
    [Export] public bool AnchorAtOrigin { get; set; } = true;
    [Export(PropertyHint.Range, "6,120,0.5")] public float AnchorRadius { get; set; } = 30f;

    [ExportGroup("Erosion")]
    [Export(PropertyHint.Range, "0,8,1")] public int ErosionOctaves { get; set; } = 5;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ErosionStrength { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "6,120,1")] public float ErosionScale { get; set; } = 34f;
    [Export(PropertyHint.Range, "0,3,0.05")] public float ErosionBias { get; set; } = 1.35f;

    [ExportGroup("Surface")]
    [Export(PropertyHint.Range, "0,12,0.1")] public float TopRelief { get; set; } = 2.6f;
    [Export(PropertyHint.Range, "0,8,0.5")] public float CapThickness { get; set; } = 2f;
    [Export] public bool ScaleCapWithIsland { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float CaveStrength { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0.5,3,0.05")] public float TaperExponent { get; set; } = 1.15f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float WarpStrength { get; set; } = 0.28f;
    [Export(PropertyHint.Range, "10,200,1")] public float WarpScale { get; set; } = 60f;

    private NodeWorld _world;

    /// <summary>
    /// One chunk source per worker thread.
    ///
    /// A source carries scratch lists it reuses across the cells of a chunk —
    /// the candidate islands, the ones actually reaching a column — which is
    /// what keeps generation allocation-free. Shared between threads those
    /// lists are cleared and appended by several workers at once, and the
    /// generator throws or silently produces the wrong terrain. Giving each
    /// thread its own is the whole fix, and it costs one small object per
    /// core.
    ///
    /// [ThreadStatic] cannot serve here: the sources must be rebuilt when the
    /// field changes, and a thread-static field cannot be reached from the
    /// thread that changed it.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IslandChunkSource>
        _sources = new();

    /// <summary>The field being streamed, for anything that wants to sample it
    /// (a horizon, a minimap, spawn logic).</summary>
    public IslandDensity Field { get; private set; }

    protected override NodeWorld World => _world;

    public override void _Ready()
    {
        _world = !NodeWorldPath.IsEmpty ? GetNodeOrNull<NodeWorld>(NodeWorldPath) : null;
        _world ??= GetParentOrNull<NodeWorld>();

        if (_world == null)
        {
            GD.PushWarning("IslandStreamer: needs a NodeWorld parent (or NodeWorldPath) — nothing streamed.");
            return;
        }

        base._Ready();
        Reset();
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

    private IslandDensity BuildField()
    {
        var placement = new IslandPlacement(_seed, Spacing, IslandDensity,
            MinRadius, Mathf.Max(MinRadius, MaxRadius), SizeBias, DepthRatio,
            VerticalSpread, AnchorAtOrigin, AnchorRadius,
            Clustering, ClusterScale, MaxPerCell, ClusterContrast, MaxDepth);

        return new IslandDensity(_seed, placement)
        {
            ErosionOctaves = ErosionOctaves,
            ErosionStrength = ErosionStrength,
            ErosionScale = ErosionScale,
            ErosionBias = ErosionBias,
            TopRelief = TopRelief,
            CapThickness = CapThickness,
            ScaleCapWithIsland = ScaleCapWithIsland,
            CaveStrength = CaveStrength,
            TaperExponent = TaperExponent,
            WarpStrength = WarpStrength,
            WarpScale = WarpScale,
        };
    }

    /// <summary>
    /// Generates one chunk, on whichever worker thread is calling.
    ///
    /// The field itself is safe to share — it is immutable, and its one memo
    /// is thread-static — so only the per-chunk scratch has to be per-thread.
    /// </summary>
    protected override int GenerateChunk(Vector3I chunk, byte[] cells)
    {
        IslandDensity field = Field;
        if (field == null)
            return 0;

        IslandChunkSource source = _sources.GetOrAdd(
            System.Environment.CurrentManagedThreadId,
            _ => new IslandChunkSource(field));

        // A source built against a superseded field would generate terrain
        // that disagrees with its neighbours. Rebuilding is cheap; the source
        // holds nothing but scratch.
        if (!ReferenceEquals(source.Field, field))
        {
            source = new IslandChunkSource(field);
            _sources[System.Environment.CurrentManagedThreadId] = source;
        }

        return source.Generate(chunk, cells);
    }
}
