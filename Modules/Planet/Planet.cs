using System;
using Godot;
using GameBase.Core;
using GameBase.Nodes;
using GameBase.Player;
using GameBase.Terrain;

namespace GameBase.Planets;

/// <summary>
/// A planet: its terrain, the world of nodes it is built from, the streamer
/// that keeps the part around the player resident, and the far terrain that
/// draws the rest out to the horizon.
///
/// The one place a planet's numbers live. It builds the <see cref="VoronoiGrid"/>,
/// the terrain shape and the <see cref="PlanetGenerator"/> from its exports and
/// the world seed, and hands them to its children, so the world, the streamer
/// and the far terrain stay general and know nothing about planets.
///
/// SIZE. A planet is round, but a player standing on this one should see a flat
/// world. The horizon dips below level by about sqrt(2h / R) radians for an eye
/// h above the ground; at the default radius and a standing eye that is about
/// 1.3 degrees -- flat to the eye -- while the ground still curves away
/// honestly for anyone who climbs high enough to look.
///
/// LOADING. The planet is what a loading screen waits on: ready once the
/// ground around the player is built AND the far terrain has drawn the
/// horizon, so the first frame of play shows the whole view.
///
/// (Namespace <c>GameBase.Planets</c>, not <c>GameBase.Planet</c>, so the class
/// name does not collide with its own namespace.)
/// </summary>
public partial class Planet : Node3D, ILoadProgress, IStatsReport
{
    /// <summary>Distance from the centre to flat ground, in world units.</summary>
    [Export(PropertyHint.Range, "100,20000,10")] public float Radius { get; set; } = 6000f;

    /// <summary>How wide one node is.</summary>
    [Export(PropertyHint.Range, "0.5,8,0.25")] public float NodeSize { get; set; } = 2f;

    /// <summary>How far node sites stray from a plain lattice; 0 gives cubes.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.05")] public float Jitter { get; set; } = 0.5f;

    /// <summary>The numbers that shape the terrain. Left empty, the defaults.</summary>
    [Export] public TerrainSettings Terrain { get; set; }

    /// <summary>
    /// A plain round planet with no terrain, as it was before there was any:
    /// for diagnostics that want a known, featureless surface.
    /// </summary>
    [Export] public bool FlatWorld { get; set; }

    /// <summary>
    /// The seed, as text. Left empty, the one chosen on the main menu (see
    /// <see cref="WorldSeed"/>).
    /// </summary>
    [Export] public string Seed { get; set; } = "";

    /// <summary>Draw the terrain beyond the loaded chunks out to the horizon.</summary>
    [Export] public bool DrawFarTerrain { get; set; } = true;

    public NodeWorld World { get; private set; }

    public ChunkStreamer Streamer { get; private set; }

    public FarTerrain Far { get; private set; }

    public PlanetGenerator Generator { get; private set; }

    /// <summary>The terrain's shape.</summary>
    public ITerrainShape Shape => Generator?.Shape;

    /// <summary>The seed text this planet was built from.</summary>
    public string SeedText { get; private set; } = "";

    /// <summary>Gravity toward this planet's centre.</summary>
    public IGravityField Gravity => new RadialGravity(GlobalPosition);

    public bool IsReady { get; private set; }

    public float Progress
    {
        get
        {
            if (IsReady)
                return 1f;

            float near = Streamer?.Progress ?? 0f;
            return Far == null ? near : 0.6f * near + 0.4f * Far.Progress;
        }
    }

    public event Action BecameReady;

    public override void _Ready()
    {
        World = GetNodeOrNull<NodeWorld>("NodeWorld") ?? AddNamed(new NodeWorld(), "NodeWorld");
        Streamer = GetNodeOrNull<ChunkStreamer>("Streamer") ?? AddNamed(new ChunkStreamer(), "Streamer");

        if (DrawFarTerrain)
            Far = GetNodeOrNull<FarTerrain>("FarTerrain") ?? AddNamed(new FarTerrain(), "FarTerrain");

        Build();
    }

    public override void _Process(double delta)
    {
        if (IsReady || Streamer == null || !Streamer.IsReady || (Far != null && !Far.IsReady))
            return;

        IsReady = true;
        BecameReady?.Invoke();
    }

    /// <summary>Rebuilds the planet from the current exports and seed, dropping everything resident.</summary>
    public void Build()
    {
        Terrain ??= new TerrainSettings();
        SeedText = string.IsNullOrWhiteSpace(Seed) ? WorldSeed.Current : Seed.Trim();

        var grid = new VoronoiGrid(NodeSize, Jitter);
        ITerrainShape shape = FlatWorld
            ? new FlatTerrain(Radius)
            : new PlanetTerrain(Radius, WorldSeed.Parse(SeedText), Terrain);

        SandRules sand = FlatWorld ? SandRules.Uniform(Terrain.SandDepth) : SandRules.From(Terrain);
        Generator = new PlanetGenerator(grid, shape, sand);

        IsReady = false;

        World.Configure(grid);
        Streamer.World = World;
        Streamer.Generator = Generator;
        Streamer.Restart();

        Far?.Configure(Generator, World, Streamer);
    }

    /// <summary>Which way is up at a global point.</summary>
    public Vector3 UpAt(Vector3 globalPoint)
    {
        Vector3 offset = globalPoint - GlobalPosition;
        return offset.LengthSquared() > 0.0001f ? offset.Normalized() : Vector3.Up;
    }

    /// <summary>
    /// The top of the untouched ground along a direction from the centre: the
    /// first solid thing coming down from the sky, so never the ground
    /// underneath an arch or an overhang.
    /// </summary>
    public Vector3 SurfacePoint(Vector3 direction)
    {
        Vector3 local = GlobalBasis.Inverse() * direction;
        return ToGlobal(TerrainShapes.SurfacePoint(Shape, local));
    }

    /// <summary>What the terrain is like at a global point.</summary>
    public TerrainInfo Describe(Vector3 globalPoint) => Shape.Describe(ToLocal(globalPoint));

    /// <summary>
    /// For the stats overlay: the kind of terrain the player is in, and the
    /// blend when near a border.
    /// </summary>
    public string StatsLine
    {
        get
        {
            Node3D target = Streamer?.Target;
            if (Shape == null || target == null || !IsInstanceValid(target))
                return null;

            TerrainInfo info = Describe(target.GlobalPosition);

            float strongest = Mathf.Max(Mathf.Max(info.FlatWeight, info.HillsWeight),
                Mathf.Max(info.MountainWeight, info.BasinWeight));
            if (strongest > 0.95f || info.Type is RegionType.SkyIslands or RegionType.Canyon)
                return $"Terrain {info.Name}";

            return $"Terrain {info.Name}  ({info.Blend})";
        }
    }

    private T AddNamed<T>(T node, string name) where T : Node
    {
        node.Name = name;
        AddChild(node);
        return node;
    }
}
