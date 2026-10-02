using Godot;
using System;
using System.Threading.Tasks;
using GameBase.Nodes;
using GameBase.Planets;
using GameBase.Terrain;
using GameBase.Tools;

namespace GameBase.Tests;

/// <summary>
/// The shovel on the planet's own ground rather than a flat test bed: a hill
/// slope that was steep before anyone touched it, and the edge where sand
/// thins onto a mountain's rock.
/// </summary>
public sealed class TerrainToolTests : TestSuite
{
    private const float Radius = 6000f;

    private sealed class Site
    {
        public NodeWorld World;
        public Vector3 Ground;
        public Vector3 Up;
        public StaticBody3D User;
        public float Slope;
    }

    /// <summary>
    /// Finds a spot in a region type whose slope lies in a range, builds the
    /// chunks around it into a world, and stands a body there turned to the
    /// planet's up (the tools read gravity from their user).
    /// </summary>
    private async Task<Site> Build(RegionType type, float minSlope, float maxSlope, Func<PlanetGenerator, Vector3, bool> also = null)
    {
        var terrain = new PlanetTerrain(Radius, 1, new TerrainSettings());
        var generator = new PlanetGenerator(new VoronoiGrid(2f), terrain, SandRules.From(new TerrainSettings()));
        var random = new Random(61);

        for (int n = 0; n < 20000; n++)
        {
            Vector3 u = new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f).Normalized();
            if (terrain.Regions.Types[terrain.RegionAt(u.X, u.Y, u.Z)] != type)
                continue;

            // Plain ground: clear of a sky-island zone, its crater and the
            // cracked land round it.
            if (terrain.Islands.ZoneAt(u.X, u.Y, u.Z, out double inside) >= 0 && inside > -0.45)
                continue;

            Vector3 ground = TerrainShapes.SurfacePoint(terrain, u);
            Vector3 outward = -Gradient(terrain, ground).Normalized();
            float slope = Mathf.RadToDeg(outward.AngleTo(u));

            if (slope < minSlope || slope > maxSlope || (also != null && !also(generator, ground)))
                continue;

            // The region's own ground: not a canyon's floor or stepped walls.
            if (terrain.Describe(ground).Type != type || terrain.Canyons.Near(u, out _, out _, out _))
                continue;

            return await BuildAt(generator, ground, u, slope);
        }

        throw new TestFailure($"no {type} ground between {minSlope} and {maxSlope} degrees");
    }

    private async Task<Site> BuildAt(PlanetGenerator generator, Vector3 ground, Vector3 u, float slope)
    {
        {
            var world = new NodeWorld();
            Root.AddChild(world);
            world.Configure(generator.Grid);

            Vector3I middle = NodeChunkStore.ChunkOf(generator.Grid.CellAt(ground));
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                var materials = new byte[NodeChunkStore.ChunkVolume];
                var fills = new byte[NodeChunkStore.ChunkVolume];
                Vector3I chunk = middle + new Vector3I(x, y, z);
                generator.Generate(chunk, materials, fills);
                world.InstallChunk(chunk, materials, fills);
            }

            world.MeshAllNow();

            // Turned so its up is the planet's up here, and out of the way of
            // every ray.
            Vector3 east = u.Cross(Mathf.Abs(u.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            var user = new StaticBody3D { CollisionLayer = 0, CollisionMask = 0 };
            Root.AddChild(user);
            user.GlobalTransform = new Transform3D(new Basis(east, u, east.Cross(u)), ground + u * 2f);

            await PhysicsFrames();
            return new Site { World = world, Ground = ground, Up = u, User = user, Slope = slope };
        }
    }

    /// <summary>
    /// Flat ground where the lattice leans far from gravity -- near halfway
    /// between the planet's axes -- raises as it does at the pole: most of a
    /// node, then stops. It once did not move at all on sloped ground there.
    ///
    /// Near, not exactly on, the diagonal: exactly on it, the aim runs down
    /// the lattice's own axis of symmetry, straight through a vertex of the
    /// drawn ground, and a physics ray there can slip between the triangles
    /// meeting at it.
    /// </summary>
    [Test]
    public async Task RaisingWorksWhereTheLatticeLeans()
    {
        var generator = new PlanetGenerator(new VoronoiGrid(2f), 6000f, 6f);
        Vector3 u = new Vector3(1f, 1.07f, 0.93f).Normalized();
        Site site = await BuildAt(generator, u * 6000f, u, 0f);

        float before = Height(site, site.Ground);
        float jump = await Hold(site, new ShovelTool(), ShovelMode.Raise, site.Ground, 240);
        float after = Height(site, site.Ground);

        GD.Print($"    flat ground between the axes: raised {after - before:0.00}, biggest frame {jump:0.00}");

        Check(after > before + 1.2f, $"a held raise only lifted the ground {after - before:0.00}");
        Check(after < before + 3f, $"a held raise did not stop, reaching {after - before:0.00}");
        Check(jump < 0.2f, $"the ground jumped {jump:0.00} in one frame");
    }

    private static Vector3 Gradient(ITerrainShape shape, Vector3 p)
    {
        const float h = 0.5f;
        return new Vector3(
            (float)(shape.Distance(p.X + h, p.Y, p.Z) - shape.Distance(p.X - h, p.Y, p.Z)),
            (float)(shape.Distance(p.X, p.Y + h, p.Z) - shape.Distance(p.X, p.Y - h, p.Z)),
            (float)(shape.Distance(p.X, p.Y, p.Z + h) - shape.Distance(p.X, p.Y, p.Z - h))) / (2 * h);
    }

    /// <summary>How high the drawn ground stands at a spot, along up, from where it was found.</summary>
    private static float Height(Site site, Vector3 spot) =>
        site.World.Raycast(spot + site.Up * 20f, spot - site.Up * 20f, out NodeHit hit)
            ? (hit.Point - site.Ground).Dot(site.Up)
            : float.NaN;

    /// <summary>Holds a mode on a spot for some frames at 60 a second, looking straight down.</summary>
    private async Task<float> Hold(Site site, ShovelTool shovel, ShovelMode mode, Vector3 spot, int frames,
        ToolStroke stroke = null)
    {
        stroke ??= new ToolStroke();
        float last = Height(site, spot), biggest = 0f;

        for (int frame = 0; frame < frames; frame++)
        {
            Vector3 eye = spot + site.Up * ((float.IsNaN(last) ? 0f : last) + 3f);
            var context = new ToolContext(site.World, eye, -site.Up, null, site.User, 1f / 60f, 0f, stroke, (int)mode);

            if (shovel.TryTarget(context, out NodeHit target))
                shovel.Mine(context, target);

            site.World.MeshAllNow();
            await PhysicsFrames(1);

            float now = Height(site, spot);
            if (!float.IsNaN(now) && !float.IsNaN(last))
                biggest = Mathf.Max(biggest, Mathf.Abs(now - last));
            last = now;
        }

        return biggest;
    }

    /// <summary>
    /// On a hillside already steep before anyone touched it, a held raise
    /// still grows smoothly and stops: it builds a little shelf out of the
    /// slope, not a runaway pillar.
    /// </summary>
    [Test]
    public async Task RaisingOnAHillsideStops()
    {
        Site site = await Build(RegionType.Hills, 18f, 32f);
        var shovel = new ShovelTool();
        float before = Height(site, site.Ground);

        float jump = await Hold(site, shovel, ShovelMode.Raise, site.Ground, 300);
        float after = Height(site, site.Ground);

        GD.Print($"    on a {site.Slope:0} degree hillside: raised {after - before:0.00}, biggest frame {jump:0.00}");
        Check(jump < 0.2f, $"the ground jumped {jump:0.00} in one frame");
        Check(after > before + 0.5f, $"a held raise barely lifted the hillside, {before:0.00} to {after:0.00}");
        Check(after < before + 4f, $"a held raise on a hillside kept climbing, to {after - before:0.00}");
    }

    /// <summary>Level on a hillside cuts and fills toward the level through where it was pressed.</summary>
    [Test]
    public async Task LevelFlattensAHillside()
    {
        Site site = await Build(RegionType.Hills, 12f, 22f);
        var shovel = new ShovelTool();

        Vector3 east = -Gradient(new PlanetTerrain(Radius, 1, new TerrainSettings()), site.Ground).Normalized();
        east = (east - site.Up * east.Dot(site.Up)).Normalized();
        Vector3 downhill = east, uphill = -east;

        float spread(Site s) => Mathf.Abs(Height(s, site.Ground + uphill * 1.5f) - Height(s, site.Ground + downhill * 1.5f));
        float before = spread(site);

        await Hold(site, shovel, ShovelMode.Level, site.Ground, 240);
        float after = spread(site);

        GD.Print($"    on a {site.Slope:0} degree hillside: 3 units across fell {before:0.00} before levelling, {after:0.00} after");
        Check(after < before * 0.6f, $"Level left a {after:0.00} fall across 3 units (was {before:0.00})");
    }

    /// <summary>
    /// Where sand thins onto rock, Smooth shapes the sand and leaves every
    /// raw node where it was.
    /// </summary>
    [Test]
    public async Task SmoothingWhereSandMeetsRockKeepsTheRock()
    {
        // A mountain spot with sand on it and rock close underneath.
        Site site = await Build(RegionType.Mountains, 25f, 42f, (generator, ground) =>
        {
            Vector3 up = ground.Normalized();
            generator.Classify(generator.Grid.CellAt(ground - up * 1f), out byte shallow, out _);
            generator.Classify(generator.Grid.CellAt(ground - up * 5f), out byte deep, out _);
            return shallow == (byte)NodeMaterial.Sand && deep == (byte)NodeMaterial.Stone;
        });

        var shovel = new ShovelTool();
        Vector3I centre = site.World.Grid.CellAt(site.Ground);

        int Raw()
        {
            int count = 0;
            for (int x = -5; x <= 5; x++)
            for (int y = -5; y <= 5; y++)
            for (int z = -5; z <= 5; z++)
                count += site.World.TypeAt(centre + new Vector3I(x, y, z)) is RawNode ? 1 : 0;
            return count;
        }

        int rock = Raw();
        float jump = await Hold(site, shovel, ShovelMode.Smooth, site.Ground, 120);

        Equal(Raw(), rock, "raw nodes around the smoothing");
        Check(jump < 0.3f, $"smoothing jumped the ground {jump:0.00} in one frame");
    }
}
