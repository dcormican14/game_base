using Godot;
using GameBase.Nodes;
using GameBase.Player;

namespace GameBase.Planets;

/// <summary>
/// A planet: the shape, the world of nodes it is built from, and the streamer
/// that keeps the part around the player resident.
///
/// The one place a planet's numbers live. It builds the <see cref="VoronoiGrid"/>
/// and <see cref="PlanetGenerator"/> from its exports and hands them to its
/// <see cref="NodeWorld"/> and <see cref="ChunkStreamer"/> children, so the
/// world and the streamer stay general and know nothing about planets.
///
/// SIZE. A planet is round, but a player standing on this one should see a flat
/// world. The horizon dips below level by about sqrt(2h / R) radians for an eye
/// h above the ground; at the default radius and a standing eye that is about
/// 1.3 degrees -- flat to the eye -- while the ground still curves away
/// honestly for anyone who flies high enough to look.
///
/// (Namespace <c>GameBase.Planets</c>, not <c>GameBase.Planet</c>, so the class
/// name does not collide with its own namespace.)
/// </summary>
public partial class Planet : Node3D
{
    /// <summary>Distance from the centre to the particle surface, in world units.</summary>
    [Export(PropertyHint.Range, "100,20000,10")] public float Radius { get; set; } = 6000f;

    /// <summary>How wide one node is.</summary>
    [Export(PropertyHint.Range, "0.5,8,0.25")] public float NodeSize { get; set; } = 2f;

    /// <summary>How far node sites stray from a plain lattice; 0 gives cubes.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.05")] public float Jitter { get; set; } = 0.5f;

    /// <summary>How deep the shell of particle nodes over the raw nodes is, in world units.</summary>
    [Export(PropertyHint.Range, "0,64,0.5")] public float ParticleDepth { get; set; } = 6f;

    public NodeWorld World { get; private set; }

    public ChunkStreamer Streamer { get; private set; }

    public PlanetGenerator Generator { get; private set; }

    /// <summary>Gravity toward this planet's centre.</summary>
    public IGravityField Gravity => new RadialGravity(GlobalPosition);

    public override void _Ready()
    {
        World = GetNodeOrNull<NodeWorld>("NodeWorld") ?? AddNamed(new NodeWorld(), "NodeWorld");
        Streamer = GetNodeOrNull<ChunkStreamer>("Streamer") ?? AddNamed(new ChunkStreamer(), "Streamer");

        Build();
    }

    /// <summary>Rebuilds the planet from the current exports, dropping everything resident.</summary>
    public void Build()
    {
        var grid = new VoronoiGrid(NodeSize, Jitter);
        Generator = new PlanetGenerator(grid, Radius, ParticleDepth);

        World.Configure(grid);
        Streamer.World = World;
        Streamer.Generator = Generator;
        Streamer.Restart();
    }

    /// <summary>Which way is up at a global point.</summary>
    public Vector3 UpAt(Vector3 globalPoint)
    {
        Vector3 offset = globalPoint - GlobalPosition;
        return offset.LengthSquared() > 0.0001f ? offset.Normalized() : Vector3.Up;
    }

    /// <summary>The point on the untouched particle surface above a direction from the centre.</summary>
    public Vector3 SurfacePoint(Vector3 direction) =>
        GlobalPosition + direction.Normalized() * Radius;

    private T AddNamed<T>(T node, string name) where T : Node
    {
        node.Name = name;
        AddChild(node);
        return node;
    }
}
