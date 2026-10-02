using Godot;

namespace GameBase.Terrain;

/// <summary>
/// Every number that shapes the terrain, in one resource so it can be tuned in
/// the inspector (<c>Modules/Terrain/DefaultTerrain.tres</c>) without touching
/// code. Distances are world units; a node is two.
///
/// Read once, when a planet is built: <see cref="PlanetTerrain"/> copies what
/// it needs, so worker threads never touch the resource.
/// </summary>
[GlobalClass]
public partial class TerrainSettings : Resource
{
    // ------------------------------------------------------------- regions

    /// <summary>
    /// How many regions the planet is split into. About 100 makes each one
    /// 1-3 km across on the default planet.
    /// </summary>
    [ExportGroup("Regions")]
    [Export(PropertyHint.Range, "8,400,1")] public int RegionCount { get; set; } = 100;

    /// <summary>Share of regions that are flat. The rest after hills and basins are mountains.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float FlatShare { get; set; } = 0.15f;

    /// <summary>Share of regions that are hills.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float HillsShare { get; set; } = 0.30f;

    /// <summary>Share of regions that are basins.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float BasinShare { get; set; } = 0.30f;

    /// <summary>How far region borders wander from straight lines.</summary>
    [Export(PropertyHint.Range, "0,1500,10")] public float BorderWander { get; set; } = 350f;

    /// <summary>How wide the blend between two regions is.</summary>
    [Export(PropertyHint.Range, "20,1500,10")] public float BlendWidth { get; set; } = 300f;

    /// <summary>How far in from its edge a mountain range takes to reach full height.</summary>
    [Export(PropertyHint.Range, "50,2000,10")] public float MountainRise { get; set; } = 600f;

    // ------------------------------------------------------------- heights

    /// <summary>How much flat ground rolls, up and down.</summary>
    [ExportGroup("Heights")]
    [Export(PropertyHint.Range, "0,10,0.1")] public float FlatRoll { get; set; } = 1.5f;

    /// <summary>The middle height of hill country.</summary>
    [Export(PropertyHint.Range, "-20,60,1")] public float HillMiddle { get; set; } = 12f;

    /// <summary>How far hills rise above and dip below their middle.</summary>
    [Export(PropertyHint.Range, "0,80,1")] public float HillSwing { get; set; } = 30f;

    /// <summary>
    /// How deep the hollows in flats and hills sink below the ground around
    /// them: small dips, not the basin regions.
    /// </summary>
    [Export(PropertyHint.Range, "0,100,1")] public float HollowDepth { get; set; } = 34f;

    /// <summary>Sea level against the base radius. Negative keeps ordinary flat ground dry.</summary>
    [Export(PropertyHint.Range, "-50,50,1")] public float SeaLevel { get; set; } = -8f;

    /// <summary>The lowest a mountain range's peaks reach.</summary>
    [Export(PropertyHint.Range, "20,600,5")] public float PeakLow { get; set; } = 150f;

    /// <summary>The highest a mountain range's peaks reach.</summary>
    [Export(PropertyHint.Range, "20,600,5")] public float PeakHigh { get; set; } = 300f;

    /// <summary>The lowest a towering range's horns reach: these stand over every other range.</summary>
    [Export(PropertyHint.Range, "20,1000,5")] public float TowerPeakLow { get; set; } = 350f;

    /// <summary>The highest a towering range's horns reach.</summary>
    [Export(PropertyHint.Range, "20,1000,5")] public float TowerPeakHigh { get; set; } = 500f;

    // -------------------------------------------------------------- basins

    /// <summary>
    /// The fewest oceans the basins gather into. Every basin region belongs
    /// to one; the basin share is split between them.
    /// </summary>
    [ExportGroup("Basins")]
    [Export(PropertyHint.Range, "1,10,1")] public int OceansLow { get; set; } = 2;

    /// <summary>The most oceans the basins gather into.</summary>
    [Export(PropertyHint.Range, "1,10,1")] public int OceansHigh { get; set; } = 4;

    /// <summary>The shallowest an ocean's shelf lies below the plains.</summary>
    [Export(PropertyHint.Range, "0,400,5")] public float BasinDepthLow { get; set; } = 60f;

    /// <summary>The deepest an ocean's shelf lies below the plains.</summary>
    [Export(PropertyHint.Range, "0,400,5")] public float BasinDepthHigh { get; set; } = 120f;

    /// <summary>How far out from shore the shelf takes to reach its depth: long, so the shore is gentle.</summary>
    [Export(PropertyHint.Range, "50,2000,10")] public float BasinRise { get; set; } = 700f;

    /// <summary>The shallowest an ocean's deep water lies below the plains.</summary>
    [Export(PropertyHint.Range, "0,1000,5")] public float DeepLow { get; set; } = 250f;

    /// <summary>The deepest an ocean's deep water lies below the plains.</summary>
    [Export(PropertyHint.Range, "0,1000,5")] public float DeepHigh { get; set; } = 400f;

    /// <summary>How far from shore the shelf ends and the floor starts falling to the deep.</summary>
    [Export(PropertyHint.Range, "100,8000,50")] public float ShelfWidth { get; set; } = 800f;

    /// <summary>How far from shore the deep floor is reached.</summary>
    [Export(PropertyHint.Range, "100,8000,50")] public float DeepWidth { get; set; } = 2000f;

    // ------------------------------------------------------------- canyons

    /// <summary>The fewest canyons a planet has.</summary>
    [ExportGroup("Canyons")]
    [Export(PropertyHint.Range, "0,40,1")] public int CanyonsLow { get; set; } = 5;

    /// <summary>The most canyons a planet has.</summary>
    [Export(PropertyHint.Range, "0,40,1")] public int CanyonsHigh { get; set; } = 10;

    /// <summary>How wide a canyon is at its mouth, rim to rim; it narrows upstream.</summary>
    [Export(PropertyHint.Range, "10,400,5")] public float CanyonWidth { get; set; } = 120f;

    /// <summary>How wide a canyon is at its head.</summary>
    [Export(PropertyHint.Range, "10,400,5")] public float CanyonHeadWidth { get; set; } = 45f;

    /// <summary>The shortest a canyon runs, from its head to the basin it drains into.</summary>
    [Export(PropertyHint.Range, "200,8000,50")] public float CanyonLengthLow { get; set; } = 1500f;

    /// <summary>The longest a canyon runs.</summary>
    [Export(PropertyHint.Range, "200,8000,50")] public float CanyonLengthHigh { get; set; } = 3500f;

    // --------------------------------------------------------- sky islands

    /// <summary>The fewest sky-island zones a planet has.</summary>
    [ExportGroup("Sky islands")]
    [Export(PropertyHint.Range, "0,20,1")] public int IslandZonesLow { get; set; } = 3;

    /// <summary>The most sky-island zones a planet has.</summary>
    [Export(PropertyHint.Range, "0,20,1")] public int IslandZonesHigh { get; set; } = 5;

    /// <summary>How deep the chasm under the islands is, below its rim.</summary>
    [Export(PropertyHint.Range, "20,800,5")] public float ChasmDepth { get; set; } = 250f;

    /// <summary>How high over the rim the highest islands float.</summary>
    [Export(PropertyHint.Range, "100,3000,10")] public float IslandHeight { get; set; } = 1000f;

    // ---------------------------------------------------------------- 3D

    /// <summary>How far jagged mountains are warped sideways: the source of their overhangs.</summary>
    [ExportGroup("Overhangs and arches")]
    [Export(PropertyHint.Range, "0,40,0.5")] public float JaggedWarp { get; set; } = 14f;

    /// <summary>How far rounded mountains are warped.</summary>
    [Export(PropertyHint.Range, "0,40,0.5")] public float RoundedWarp { get; set; } = 5f;

    /// <summary>How far towering mountains are warped: their horns' crags.</summary>
    [Export(PropertyHint.Range, "0,40,0.5")] public float TowerWarp { get; set; } = 14f;

    /// <summary>How far hills are warped.</summary>
    [Export(PropertyHint.Range, "0,40,0.5")] public float HillWarp { get; set; } = 3f;

    /// <summary>
    /// Radius of the tunnels that punch arches through thin ridges. Arches only
    /// form where a ridge is thinner than about twice this.
    /// </summary>
    [Export(PropertyHint.Range, "0,40,0.5")] public float ArchRadius { get; set; } = 14f;

    /// <summary>How far a mesa's caprock hangs out over its walls.</summary>
    [Export(PropertyHint.Range, "0,20,0.5")] public float CaprockLip { get; set; } = 6f;

    // ---------------------------------------------------------------- sand

    /// <summary>How deep the sand is on gentle ground.</summary>
    [ExportGroup("Sand")]
    [Export(PropertyHint.Range, "0,20,0.5")] public float SandDepth { get; set; } = 6f;

    /// <summary>Ground up to this slope, in degrees, has its full depth of sand.</summary>
    [Export(PropertyHint.Range, "0,90,1")] public float SandFullSlope { get; set; } = 40f;

    /// <summary>Ground past this slope, in degrees, is bare rock.</summary>
    [Export(PropertyHint.Range, "0,90,1")] public float SandNoneSlope { get; set; } = 50f;

    /// <summary>Sand thinner than this is left off altogether, rather than speckling the rock.</summary>
    [Export(PropertyHint.Range, "0,6,0.25")] public float SandMinimum { get; set; } = 1.5f;
}
