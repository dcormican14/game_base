using Godot;
using System.Collections.Generic;
using GameBase.Density;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Builds a field of floating islands out of nodes, from a layered density
/// map.
///
/// Unlike <see cref="NodeLevel"/>, which rasterises a shape it decides
/// procedurally, this asks a field: for every cell in the region, is the
/// density positive? The shape lives entirely in
/// <see cref="IslandDensity"/> — this class only walks cells, asks, and fills.
///
/// That split is what makes the world endless in principle. The field is
/// defined over all of space and depends on nothing but position and seed, so
/// the region walked here is a window onto it, not the extent of it. Streaming
/// chunks in as the player moves needs a different DRIVER, not a different
/// field.
///
/// Attach to a NodeWorld (or instance IslandLevel.tscn); it is a [Tool]
/// script, so changing any export regenerates live in the editor.
/// </summary>
[Tool]
public partial class IslandLevel : Node
{
    private int _seed = 90210;

    // Region
    // Sized so a rebuild is a few seconds rather than ten: the field costs
    // roughly half a microsecond per cell and the region's cell count grows
    // with the cube of its size, so this is the knob that decides whether
    // editing a dial is pleasant or painful. At the default it holds about a
    // dozen islands.
    // Islands are now large enough that a region holds far more rock than it
    // used to — the same radius that held 165k nodes with the old small
    // islands holds well over a million with these.
    //
    // The binding constraint is not generation time but MEMORY: the world's
    // occupancy map stores 64 sub-cells per node, so a million nodes is
    // roughly 2 GB of dictionary, and the machine starts thrashing. 87% of
    // those entries are for fully-buried nodes no face ever tests, so there is
    // a large optimisation available there — until it lands, this is the size
    // that fits comfortably.
    private int _regionRadius = 85;
    private int _regionHeight = 90;

    // Placement
    private float _spacing = 190f;
    private float _islandDensity = 0.8f;
    private float _minRadius = 8f;
    private float _maxRadius = 135f;
    private float _sizeBias = 1.85f;
    private float _depthRatio = 2.2f;
    private float _maxDepth = 230f;
    private float _verticalSpread = 1.15f;
    private float _clustering = 0.95f;
    private float _clusterScale = 620f;
    private int _maxPerCell = 5;
    private float _clusterContrast = 3.4f;
    private float _profileVariance = 0.45f;
    private float _outlineStrength = 0.62f;
    private float _outlineScale = 0.85f;
    private bool _anchorAtOrigin = true;
    private float _anchorRadius = 30f;

    // Shaping
    private int _erosionOctaves = 3;
    private float _erosionStrength = 0.5f;
    private float _erosionScale = 34f;
    private float _erosionBias = 1.35f;
    private float _topRelief = 2.6f;
    private float _capThickness = 2f;
    private bool _scaleCapWithIsland;
    private float _taperExponent = 1.15f;
    private float _erosionRimFade = 6f;
    private float _warpStrength = 0.28f;
    private float _warpScale = 60f;

    private bool _autoBuild = true;

    [ExportGroup("Region")]
    /// <summary>Half-width of the generated region in nodes. The field itself
    /// is infinite; this is only how much of it is built now.</summary>
    [Export(PropertyHint.Range, "32,512,1")]
    public int RegionRadius { get => _regionRadius; set { _regionRadius = Mathf.Max(16, value); RebuildIfReady(); } }

    /// <summary>Half-height of the generated region in nodes.</summary>
    [Export(PropertyHint.Range, "16,512,1")]
    public int RegionHeight { get => _regionHeight; set { _regionHeight = Mathf.Max(8, value); RebuildIfReady(); } }

    [ExportGroup("Placement")]
    /// <summary>Nodes between island slots. Larger means sparser sky.</summary>
    [Export(PropertyHint.Range, "20,400,1")]
    public float Spacing { get => _spacing; set { _spacing = Mathf.Max(8f, value); RebuildIfReady(); } }

    /// <summary>Fraction of slots that hold an island. Below 1 gives clusters
    /// and open sky rather than an even scatter.</summary>
    [Export(PropertyHint.Range, "0.05,1,0.01")]
    public float IslandDensity { get => _islandDensity; set { _islandDensity = Mathf.Clamp(value, 0.01f, 1f); RebuildIfReady(); } }

    /// <summary>Radius of the smallest islands, in nodes.</summary>
    [Export(PropertyHint.Range, "3,60,0.5")]
    public float MinRadius { get => _minRadius; set { _minRadius = Mathf.Max(2f, value); RebuildIfReady(); } }

    /// <summary>Radius of the largest islands, in nodes.</summary>
    [Export(PropertyHint.Range, "6,300,0.5")]
    public float MaxRadius { get => _maxRadius; set { _maxRadius = Mathf.Max(3f, value); RebuildIfReady(); } }

    /// <summary>How strongly sizes skew small. 1 spreads them evenly; higher
    /// makes large islands progressively rarer.</summary>
    [Export(PropertyHint.Range, "1,6,0.1")]
    public float SizeBias { get => _sizeBias; set { _sizeBias = Mathf.Max(0.2f, value); RebuildIfReady(); } }

    /// <summary>How far an island's spike hangs below it, as a multiple of its
    /// radius.</summary>
    [Export(PropertyHint.Range, "0.3,6,0.05")]
    public float DepthRatio { get => _depthRatio; set { _depthRatio = Mathf.Max(0.1f, value); RebuildIfReady(); } }

    /// <summary>Longest an island's spike may be, in nodes, however large the
    /// island. Bounds the tallest silhouettes and the cost of every density
    /// query.</summary>
    [Export(PropertyHint.Range, "20,600,5")]
    public float MaxDepth { get => _maxDepth; set { _maxDepth = Mathf.Max(8f, value); RebuildIfReady(); } }

    /// <summary>How freely islands drift vertically. 0 puts them all in one
    /// band; 1 scatters them fully through the region's height.</summary>
    [Export(PropertyHint.Range, "0,3,0.05")]
    public float VerticalSpread { get => _verticalSpread; set { _verticalSpread = Mathf.Max(0f, value); RebuildIfReady(); } }

    /// <summary>How strongly islands gather into archipelagos with open sky
    /// between them. 0 is an even scatter across the whole sky.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float Clustering { get => _clustering; set { _clustering = Mathf.Clamp(value, 0f, 1f); RebuildIfReady(); } }

    /// <summary>Nodes per lobe of the clustering field — roughly the size of
    /// one archipelago. Wants to be several times Spacing.</summary>
    [Export(PropertyHint.Range, "60,2000,10")]
    public float ClusterScale { get => _clusterScale; set { _clusterScale = Mathf.Max(16f, value); RebuildIfReady(); } }

    /// <summary>How many islands may share one lattice cell where the sky is
    /// generous. Above 1 lets islands genuinely crowd together.</summary>
    [Export(PropertyHint.Range, "1,6,1")]
    public int MaxPerCell { get => _maxPerCell; set { _maxPerCell = Mathf.Clamp(value, 1, 8); RebuildIfReady(); } }

    /// <summary>How sharply crowded sky separates from empty sky. 1 fades
    /// gradually; higher gives archipelagos real edges.</summary>
    [Export(PropertyHint.Range, "0.5,6,0.1")]
    public float ClusterContrast { get => _clusterContrast; set { _clusterContrast = Mathf.Max(0.2f, value); RebuildIfReady(); } }

    /// <summary>Guarantee an island centred on the origin, so the player
    /// always has ground to spawn onto. Off leaves placement entirely to the
    /// seed, which may well put nothing above the origin at all.</summary>
    [Export]
    public bool AnchorAtOrigin { get => _anchorAtOrigin; set { _anchorAtOrigin = value; RebuildIfReady(); } }

    /// <summary>Radius of the guaranteed origin island, in nodes.</summary>
    [Export(PropertyHint.Range, "6,120,0.5")]
    public float AnchorRadius { get => _anchorRadius; set { _anchorRadius = Mathf.Max(4f, value); RebuildIfReady(); } }

    [ExportGroup("Erosion")]
    /// <summary>Octaves of side erosion — how fine the jagged detail goes.</summary>
    [Export(PropertyHint.Range, "0,8,1")]
    public int ErosionOctaves { get => _erosionOctaves; set { _erosionOctaves = Mathf.Clamp(value, 0, 8); RebuildIfReady(); } }

    /// <summary>How deeply the sides are cut, as a fraction of island radius.
    /// The main "how jagged" dial.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ErosionStrength { get => _erosionStrength; set { _erosionStrength = Mathf.Clamp(value, 0f, 1f); RebuildIfReady(); } }

    /// <summary>Nodes per lobe of the coarsest erosion octave.</summary>
    [Export(PropertyHint.Range, "6,120,1")]
    public float ErosionScale { get => _erosionScale; set { _erosionScale = Mathf.Max(2f, value); RebuildIfReady(); } }

    /// <summary>How much erosion concentrates toward the bottom, leaving the
    /// rim under the flat top clean.</summary>
    [Export(PropertyHint.Range, "0,3,0.05")]
    public float ErosionBias { get => _erosionBias; set { _erosionBias = Mathf.Max(0f, value); RebuildIfReady(); } }

    [ExportGroup("Surface")]
    /// <summary>Amplitude of the rolling relief on island tops, in nodes.</summary>
    [Export(PropertyHint.Range, "0,12,0.1")]
    public float TopRelief { get => _topRelief; set { _topRelief = Mathf.Max(0f, value); RebuildIfReady(); } }

    /// <summary>Thickness of the dark capping layer on island tops, in
    /// nodes. 0 leaves the tops bare rock.</summary>
    [Export(PropertyHint.Range, "0,8,0.5")]
    public float CapThickness { get => _capThickness; set { _capThickness = Mathf.Max(0f, value); RebuildIfReady(); } }

    /// <summary>Thin the cap on smaller islands rather than keeping it a fixed
    /// number of nodes, so it reads as a skin at every size.</summary>
    [Export]
    public bool ScaleCapWithIsland { get => _scaleCapWithIsland; set { _scaleCapWithIsland = value; RebuildIfReady(); } }

    [ExportGroup("Outline")]
    /// <summary>How much island SHAPE varies island to island — profile taper
    /// and erosion wear. 0 makes every island the same silhouette at a
    /// different scale.</summary>
    [Export(PropertyHint.Range, "0,0.9,0.01")]
    public float ProfileVariance { get => _profileVariance; set { _profileVariance = Mathf.Clamp(value, 0f, 0.9f); RebuildIfReady(); } }

    /// <summary>How far islands are stretched and pinched out of round. 0
    /// leaves them discs for erosion to fray.</summary>
    [Export(PropertyHint.Range, "0,1.2,0.01")]
    public float OutlineStrength { get => _outlineStrength; set { _outlineStrength = Mathf.Max(0f, value); RebuildIfReady(); } }

    /// <summary>Lobe size of the outline distortion, as a multiple of the
    /// island's own radius. Around 1 gives peninsulas and bays at every island
    /// size.</summary>
    [Export(PropertyHint.Range, "0.2,3,0.05")]
    public float OutlineScale { get => _outlineScale; set { _outlineScale = Mathf.Max(0.05f, value); RebuildIfReady(); } }

    [ExportGroup("Profile")]
    /// <summary>Shapes the vertical profile. Above 1 narrows steadily from the
    /// top down, reading as a cone; below 1 holds the width further down and
    /// then pinches hard into a spike.</summary>
    [Export(PropertyHint.Range, "0.3,3,0.02")]
    public float TaperExponent { get => _taperExponent; set { _taperExponent = Mathf.Max(0.05f, value); RebuildIfReady(); } }

    /// <summary>How abruptly erosion fades in below the rim. Higher brings the
    /// jagged sides right up under the flat top; lower leaves a clean band.</summary>
    [Export(PropertyHint.Range, "1,20,0.5")]
    public float ErosionRimFade { get => _erosionRimFade; set { _erosionRimFade = Mathf.Max(0.5f, value); RebuildIfReady(); } }

    /// <summary>How far space is bent before the body is measured in it, as a
    /// fraction of island radius. This is what produces overhangs; 0 leaves
    /// sides that are rough but never fold back.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float WarpStrength { get => _warpStrength; set { _warpStrength = Mathf.Max(0f, value); RebuildIfReady(); } }

    /// <summary>Nodes per lobe of the domain warp.</summary>
    [Export(PropertyHint.Range, "10,200,1")]
    public float WarpScale { get => _warpScale; set { _warpScale = Mathf.Max(2f, value); RebuildIfReady(); } }

    [ExportGroup("General")]
    [Export]
    public int Seed { get => _seed; set { _seed = value; RebuildIfReady(); } }

    /// <summary>Rebuild whenever an export changes. Turn off to edit by hand
    /// without it being regenerated underneath you.</summary>
    [Export]
    public bool AutoBuild { get => _autoBuild; set { _autoBuild = value; RebuildIfReady(); } }

    /// <summary>The world to fill. Defaults to the parent when left empty.</summary>
    [Export] public NodePath NodeWorldPath { get; set; } = "";

    /// <summary>Mesh a few chunks per frame rather than all at once, so a
    /// loading screen can show progress instead of the game freezing.</summary>
    [Export] public bool IncrementalBuild { get; set; } = true;

    private NodeWorld _world;

    public override void _Ready() => Build();

    /// <summary>Stops any generation still running when the level is removed,
    /// so worker threads cannot outlive the scene they are filling.</summary>
    public override void _ExitTree() => CancelIncrementalGenerate();

    private void RebuildIfReady()
    {
        if (IsNodeReady() && _autoBuild)
            Build();
    }

    /// <summary>
    /// The field this level was built from, for anything that needs to draw
    /// the same world — the coarse horizon beyond the node grid, above all.
    /// Null until the first build.
    /// </summary>
    public IslandDensity Field { get; private set; }

    /// <summary>
    /// The cell the generated region is centred on.
    ///
    /// The region is built once, about the origin, and does NOT follow the
    /// player — so anything that needs to line up with the detailed world (the
    /// coarse horizon's hole, above all) must use this rather than the
    /// player's position. When streaming arrives this becomes the streaming
    /// centre and everything downstream keeps working.
    /// </summary>
    public Vector3I RegionCentre => Vector3I.Zero;

    /// <summary>The node world's cell size, so the horizon's coarse blocks
    /// line up with the detailed ones.</summary>
    public float WorldNodeSize => _world?.NodeSize ?? 1f;

    /// <summary>The node world's two rock shades, so distant terrain reads as
    /// the same material as near terrain.</summary>
    public Color WorldColorA => _world?.ColorA ?? new Color(0.30f, 0.30f, 0.34f);

    /// <inheritdoc cref="WorldColorA"/>
    public Color WorldColorB => _world?.ColorB ?? new Color(0.62f, 0.62f, 0.66f);

    /// <summary>The field this level is carved from, rebuilt from the current
    /// dials.</summary>
    private IslandDensity BuildField()
    {
        var placement = new IslandPlacement(_seed, _spacing, _islandDensity,
            _minRadius, Mathf.Max(_minRadius, _maxRadius), _sizeBias, _depthRatio,
            _verticalSpread, _anchorAtOrigin, _anchorRadius,
            _clustering, _clusterScale, _maxPerCell, _clusterContrast, _maxDepth);

        return new IslandDensity(_seed, placement)
        {
            ErosionOctaves = _erosionOctaves,
            ErosionStrength = _erosionStrength,
            ErosionScale = _erosionScale,
            ErosionBias = _erosionBias,
            TopRelief = _topRelief,
            CapThickness = _capThickness,
            ScaleCapWithIsland = _scaleCapWithIsland,
            TaperExponent = _taperExponent,
            ErosionRimFade = _erosionRimFade,
            WarpStrength = _warpStrength,
            WarpScale = _warpScale,
            ProfileVariance = _profileVariance,
            OutlineStrength = _outlineStrength,
            OutlineScale = _outlineScale,
        };
    }

    /// <summary>Clears the world and regenerates the islands.</summary>
    public void Build()
    {
        _world = !NodeWorldPath.IsEmpty ? GetNodeOrNull<NodeWorld>(NodeWorldPath) : null;
        _world ??= GetParent<NodeWorld>();
        if (_world == null)
        {
            GD.PushWarning("IslandLevel: needs a NodeWorld parent (or NodeWorldPath) — nothing built.");
            return;
        }

        IslandDensity field = BuildField();
        Field = field;

        // In the editor, and whenever incremental build is off, do the whole
        // thing here and now: a tool script changing a dial wants the result
        // immediately, and there is no loading screen in front of it.
        if (!IncrementalBuild || Engine.IsEditorHint())
        {
            CancelIncrementalGenerate();

            // One rebuild for the whole level rather than one per node: a
            // rebuild costs the same whether one node changed or a million.
            _world.Batch(() =>
            {
                _world.Clear();
                Generate(field);
            }, wholesale: true);

            return;
        }

        BeginIncrementalGenerate(field);
    }

    // ------------------------------------------------- incremental generation

    // Generating the whole region takes seconds, and doing it inside _Ready
    // blocks the first frame — so the loading screen it is supposed to sit
    // behind cannot appear until the work it is reporting on has finished.
    // Spreading the walk over frames is what lets the screen draw at all.
    private bool _incremental;
    private IslandDensity _pendingField;
    private List<(int tx, int tz)> _pendingTiles;
    private int _pendingTile;
    private int _tileColumn;
    private List<Island> _genCandidates;
    private List<int> _genColumn;

    /// <summary>
    /// Milliseconds of generation per frame.
    ///
    /// A time budget rather than a fixed tile count, because tiles vary
    /// enormously in cost — an empty stretch of sky is nearly free while a
    /// tile through the middle of a large island runs the whole layer stack on
    /// every cell. A fixed count either crawls through the dense tiles or
    /// stutters on them; a budget spends the same time either way.
    ///
    /// 12 ms leaves room in a 16 ms frame for the loading screen to draw.
    /// </summary>
    [Export(PropertyHint.Range, "2,100,1")]
    public float GenerateMillisecondsPerFrame { get; set; } = 12f;

    /// <summary>0..1 across the generation pass, before meshing starts.</summary>
    public float GenerateProgress =>
        _pendingTiles == null || _pendingTiles.Count == 0
            ? 1f
            : Mathf.Clamp(_pendingTile / (float)_pendingTiles.Count, 0f, 1f);

    /// <summary>Tiles whose field evaluation has finished, however many have
    /// been applied to the world yet. Diagnostic: the gap between this and
    /// GenerateProgress is how far the workers are ahead of the main thread,
    /// which says whether generation is compute-bound or apply-bound.</summary>
    public float ComputeProgress
    {
        get
        {
            if (_pendingTiles == null || _pendingTiles.Count == 0)
                return 1f;

            lock (_doneLock)
                return Mathf.Clamp(_tilesFinished / (float)_pendingTiles.Count, 0f, 1f);
        }
    }

    /// <summary>Columns per tile edge, the unit generation resumes at.</summary>
    private const int TileSize = 16;

    /// <summary>True while the region is still being walked.</summary>
    public bool IsGenerating => _pendingTiles != null;

    /// <summary>Queues every tile of the region and starts walking them a few
    /// per frame.</summary>
    private void BeginIncrementalGenerate(IslandDensity field)
    {
        const int Tile = 16;

        _pendingField = field;
        _incremental = true;
        _genCandidates = new List<Island>();
        _genColumn = new List<int>();
        _pendingTiles = new List<(int, int)>();
        _pendingTile = 0;
        _tileColumn = 0;

        int r = _regionRadius;
        for (int tx = -r; tx <= r; tx += Tile)
            for (int tz = -r; tz <= r; tz += Tile)
                _pendingTiles.Add((tx, tz));

        // Cleared up front so the world is empty while it fills, rather than
        // showing the previous level until the new one overwrites it.
        _world.Batch(() => _world.Clear(), wholesale: true, deferMesh: true);

        // An empty world looks finished. Tell it otherwise, or the loading
        // screen releases the player into the void on the first frame.
        _world.BeginGenerating();

        StartWorkers();
        SetProcess(true);
    }

    /// <summary>Drops any generation in flight, so a rebuild mid-walk does not
    /// leave the old pass writing into the new world.</summary>
    private void CancelIncrementalGenerate()
    {
        // Stop the workers and WAIT for them. They hold references to the tile
        // list and the field this call is about to drop, and a rebuild
        // triggered mid-generation would otherwise leave the old pass running
        // against freed state and enqueueing nodes into the new world.
        if (_workers != null)
        {
            _abortWorkers = true;
            try
            {
                _workers.Wait();
            }
            catch (System.AggregateException e)
            {
                GD.PushError($"IslandLevel: generation worker failed — {e.InnerException}");
            }

            _workers = null;
        }

        lock (_doneLock)
        {
            _done.Clear();
            _spare.Clear();
        }

        _incremental = false;
        _pendingTiles = null;
        _pendingField = null;
        _genCandidates = null;
        _genColumn = null;
        SetProcess(false);
    }

    // ------------------------------------------------------ parallel generation

    /// <summary>
    /// Worker threads to evaluate the density field on. 0 uses one fewer than
    /// the machine has cores, leaving one for the main thread — which still
    /// has to apply the results, drive the loading screen and render.
    ///
    /// The field is a pure function of position and seed, so tiles are
    /// genuinely independent: this is the one part of world generation that
    /// parallelises without coordination.
    /// </summary>
    [Export(PropertyHint.Range, "0,32,1")]
    public int WorkerThreads { get; set; }

    private readonly object _doneLock = new();
    private readonly Queue<List<GeneratedNode>> _done = new();
    private readonly Queue<List<GeneratedNode>> _spare = new();
    private System.Threading.Tasks.Task _workers;
    private volatile bool _abortWorkers;
    private int _nextTile = -1;
    private int _tilesFinished;

    /// <summary>Starts the workers chewing through the tile list.</summary>
    private void StartWorkers()
    {
        _abortWorkers = false;
        _nextTile = -1;
        _tilesFinished = 0;

        int threads = WorkerThreads > 0
            ? WorkerThreads
            : Mathf.Max(1, System.Environment.ProcessorCount - 1);

        IslandDensity field = _pendingField;
        var tiles = _pendingTiles;
        int r = _regionRadius;
        int h = _regionHeight;
        const int Tile = 16;

        _workers = System.Threading.Tasks.Task.Run(() =>
        {
            System.Threading.Tasks.Parallel.For(0, threads,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
                _ =>
                {
                    // Per-thread scratch, so no worker touches another's
                    // buffers. The field itself holds no shared mutable state
                    // — its one cache is [ThreadStatic] for this reason.
                    var candidates = new List<Island>();
                    var reaching = new List<Island>();
                    var column = new List<int>();

                    while (!_abortWorkers)
                    {
                        int index = System.Threading.Interlocked.Increment(ref _nextTile);
                        if (index >= tiles.Count)
                            return;

                        (int tx, int tz) = tiles[index];
                        int xEnd = Mathf.Min(tx + Tile - 1, r);
                        int zEnd = Mathf.Min(tz + Tile - 1, r);

                        List<GeneratedNode> batch = Rent();

                        var min = new Vector3(tx + 0.5f, -h + 0.5f, tz + 0.5f);
                        var max = new Vector3(xEnd + 0.5f, h + 0.5f, zEnd + 0.5f);
                        field.Placement.NearBox(min, max, candidates);

                        if (candidates.Count > 0)
                        {
                            for (int x = tx; x <= xEnd; x++)
                                for (int z = tz; z <= zEnd; z++)
                                    FillColumn(field, candidates, column, x, z, batch, reaching);
                        }

                        lock (_doneLock)
                        {
                            _done.Enqueue(batch);
                            _tilesFinished++;
                        }
                    }
                });
        });
    }

    /// <summary>A recycled batch buffer, so a long build does not churn the
    /// heap with a list per tile.</summary>
    private List<GeneratedNode> Rent()
    {
        lock (_doneLock)
        {
            if (_spare.Count > 0)
            {
                List<GeneratedNode> reused = _spare.Dequeue();
                reused.Clear();
                return reused;
            }
        }

        return new List<GeneratedNode>(4096);
    }

    /// <summary>Applies finished tiles to the world, within a time budget.
    /// Returns true when every tile has been generated AND applied.</summary>
    private bool DrainWorkers(ulong deadline)
    {
        int applied = 0;

        while (true)
        {
            List<GeneratedNode> batch = null;
            lock (_doneLock)
            {
                if (_done.Count > 0)
                    batch = _done.Dequeue();
            }

            if (batch == null)
                break;

            // The world is Godot state: only this thread may touch it.
            for (int i = 0; i < batch.Count; i++)
                _world.AddNodeGenerated(batch[i].Cell, batch[i].Material);

            applied++;
            _pendingTile++;

            lock (_doneLock)
                _spare.Enqueue(batch);

            if (Time.GetTicksMsec() >= deadline)
                break;
        }

        return _pendingTile >= _pendingTiles.Count;
    }

    public override void _Process(double delta)
    {
        if (_pendingTiles == null)
        {
            SetProcess(false);
            return;
        }

        // The workers evaluate the field; this thread only applies what they
        // produce. Both halves are budgeted: the apply loop stops at the
        // deadline even with tiles still queued, so a burst of finished work
        // cannot stall a frame.
        ulong deadline = Time.GetTicksMsec()
            + (ulong)Mathf.Max(1f, GenerateMillisecondsPerFrame);

        // Batch so the world defers its bookkeeping across the whole slice
        // rather than per node; meshing is deferred to its own incremental
        // pass either way.
        bool finished = false;
        _world.Batch(() => finished = DrainWorkers(deadline),
            wholesale: true, deferMesh: true);

        if (!finished)
            return;

        // Region walked and applied. Hand off to the world's incremental
        // mesher, which reports the rest of the loading bar.
        CancelIncrementalGenerate();
        _world.BeginIncrementalBuild();
    }

    /// <summary>
    /// Walks the region and fills every cell the field says is rock.
    ///
    /// The walk is diced into blocks, and the islands that can reach each
    /// block are gathered ONCE per block rather than once per cell. That is
    /// the difference between tractable and not: the region holds tens of
    /// millions of cells, and a placement search at each would dwarf the cost
    /// of the field itself.
    /// </summary>
    private void Generate(IslandDensity field)
    {
        // Horizontal tiles only. The candidate list is gathered per tile, but
        // each column inside it is walked over the region's FULL height in one
        // pass — the capping layer is decided by what lies above a cell, and a
        // column broken into vertical blocks would see a false open sky at
        // every block boundary and lay a band of cap through solid rock.
        const int Tile = 16;

        var candidates = new List<Island>();
        var column = new List<int>();

        int r = _regionRadius;
        int h = _regionHeight;

        for (int tx = -r; tx <= r; tx += Tile)
        {
            for (int tz = -r; tz <= r; tz += Tile)
            {
                int xEnd = Mathf.Min(tx + Tile - 1, r);
                int zEnd = Mathf.Min(tz + Tile - 1, r);

                // Cells are sampled at their centres, so the tile's real
                // extent in field space runs half a node past each end.
                var min = new Vector3(tx + 0.5f, -h + 0.5f, tz + 0.5f);
                var max = new Vector3(xEnd + 0.5f, h + 0.5f, zEnd + 0.5f);

                field.Placement.NearBox(min, max, candidates);
                if (candidates.Count == 0)
                    continue;

                FillTile(field, candidates, column, tx, tz, xEnd, zEnd);
            }
        }
    }

    /// <summary>
    /// Fills one horizontal tile, column by column over the full region
    /// height.
    ///
    /// Walking as columns is what makes the capping layer cheap: how far a
    /// cell sits below open sky falls out of a single descending pass, where
    /// asking the field again at each cell would double the cost of the build.
    /// </summary>
    private void FillTile(IslandDensity field, List<Island> candidates, List<int> column,
        int x0, int z0, int x1, int z1)
    {
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                FillColumn(field, candidates, column, x, z);
    }

    /// <summary>
    /// Fills one column of the region, top to bottom.
    ///
    /// This is the unit generation resumes at, so it must be self-contained:
    /// everything it needs is its own candidates and the column scratch list,
    /// and it leaves no state behind between calls.
    /// </summary>
    // The islands that actually reach the column being filled — a subset of
    // the tile's candidates, reused between columns so the walk allocates
    // nothing.
    private readonly List<Island> _reaching = new();

    /// <summary>One generated node, waiting to be applied to the world.</summary>
    private readonly struct GeneratedNode
    {
        public readonly Vector3I Cell;
        public readonly NodeMaterial Material;

        public GeneratedNode(Vector3I cell, NodeMaterial material)
        {
            Cell = cell;
            Material = material;
        }
    }

    /// <summary>
    /// Fills one column of the region.
    ///
    /// When `into` is given the results are appended there instead of written
    /// to the world, and the call touches no shared state beyond the scratch
    /// lists handed to it — which is what lets worker threads run it. The
    /// world is a Godot node and may only be modified from the main thread, so
    /// applying the results is the main thread's job.
    /// </summary>
    private void FillColumn(IslandDensity field, List<Island> candidates, List<int> column,
        int x, int z, List<GeneratedNode> into = null, List<Island> reaching = null)
    {
        int h = _regionHeight;
        column.Clear();

        // Most of a column is empty sky, and asking the full layered field
        // about each of those cells is where a naive build spends nearly all
        // its time. The span is a cheap bound on where this column's islands
        // could possibly reach, so the walk covers only that stretch.
        if (!field.VerticalSpan(candidates, x + 0.5f, z + 0.5f,
                out int spanMin, out int spanMax, reaching ?? _reaching))
            return;

        int yLo = Mathf.Max(-h, spanMin);
        int yHi = Mathf.Min(h, spanMax);

        for (int y = yLo; y <= yHi; y++)
        {
            if (field.At(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), reaching ?? _reaching) > 0f)
                column.Add(y);
        }

        if (column.Count == 0)
            return;

        // The cap is a skin whose thickness scales with the island wearing it,
        // so it is decided per column from whichever island is nearest —
        // which, for a column that actually holds rock, is the island the rock
        // belongs to.
        int capNodes = CapNodesAt(field, reaching ?? _reaching, x + 0.5f, z + 0.5f);

        // Descending, counting how deep below open sky each cell sits. The
        // counter resets at every gap, which is what makes the cap follow
        // overhangs and cave roofs rather than only the island's outermost top.
        //
        // The topmost cell starts at depth 0 — open sky above it. That is
        // exact unless the column is clipped by the region's own ceiling, in
        // which case an island cut off by the region edge gets a cap on the
        // cut. Harmless: the region is a window, and its ceiling is placed
        // above every island anyway.
        int depth = 0;
        for (int i = column.Count - 1; i >= 0; i--)
        {
            int y = column[i];

            // A gap above this cell means it is a fresh surface with sky over
            // it.
            if (i < column.Count - 1 && column[i + 1] != y + 1)
                depth = 0;

            NodeMaterial material = capNodes > 0 && depth < capNodes
                ? NodeMaterial.Dark
                : NodeMaterial.Raw;

            if (into != null)
                into.Add(new GeneratedNode(new Vector3I(x, y, z), material));
            else if (_incremental)
                _world.AddNodeGenerated(new Vector3I(x, y, z), material);
            else
                _world.AddNode(new Vector3I(x, y, z), material);

            depth++;
        }
    }

    /// <summary>
    /// The cap thickness that applies at a column: the nearest candidate
    /// island's.
    ///
    /// Nearest in the horizontal only — islands stack vertically, and the one
    /// whose rock fills a column is the one it sits under, which vertical
    /// distance would get wrong.
    /// </summary>
    private static int CapNodesAt(IslandDensity field, List<Island> candidates, float x, float z)
    {
        int capNodes = 0;
        float best = float.MaxValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            Island island = candidates[i];
            float dx = x - island.Centre.X;
            float dz = z - island.Centre.Z;

            // Relative to the island's own radius, so a big island a little
            // further off does not lose to a pebble underfoot.
            float distance = (dx * dx + dz * dz) / Mathf.Max(island.Radius * island.Radius, 1f);
            if (distance < best)
            {
                best = distance;
                capNodes = field.CapNodesFor(island);
            }
        }

        return capNodes;
    }
}
