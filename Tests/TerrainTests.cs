using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using GameBase.Nodes;
using GameBase.Planets;
using GameBase.Terrain;

namespace GameBase.Tests;

/// <summary>The planet's terrain: its shape, its regions, and the chunks built from it.</summary>
public sealed class TerrainTests : TestSuite
{
    private const float Radius = 6000f;
    private const ulong Seed = 1;

    private static PlanetTerrain Terrain(ulong seed = Seed) => new(Radius, seed, new TerrainSettings());

    private static PlanetGenerator Generator(ITerrainShape shape) =>
        new(new VoronoiGrid(2f), shape, SandRules.From(new TerrainSettings()));

    private static Vector3 Direction(Random random) =>
        new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f)
            .Normalized();

    /// <summary>The chunk holding the ground along a direction.</summary>
    private static Vector3I SurfaceChunk(PlanetGenerator generator, Vector3 direction) =>
        NodeChunkStore.ChunkOf(generator.Grid.CellAt(TerrainShapes.SurfacePoint(generator.Shape, direction)));

    /// <summary>A direction inside a region of a type (and, for mountains, a character).</summary>
    private static Vector3 Into(PlanetTerrain terrain, RegionType type, MountainCharacter? character = null)
    {
        RegionMap map = terrain.Regions;
        for (int i = 0; i < map.Count; i++)
        {
            if (map.Types[i] != type || (character != null && map.Characters[i] != character))
                continue;

            return new Vector3((float)map.X[i], (float)map.Y[i], (float)map.Z[i]);
        }

        throw new TestFailure($"no {type} {character} region");
    }

    // ------------------------------------------------------------- cost

    /// <summary>
    /// What the shape and a chunk cost. Printed, so a change that slows the
    /// terrain shows; failed only well past the budget the 60-second load
    /// needs.
    /// </summary>
    [Test]
    public void GenerationIsFastEnough()
    {
        PlanetTerrain terrain = Terrain();
        PlanetGenerator generator = Generator(terrain);
        var random = new Random(5);

        var watch = Stopwatch.StartNew();
        double sink = 0;
        const int Points = 20000;
        for (int n = 0; n < Points; n++)
        {
            Vector3 p = Direction(random) * (Radius + random.NextSingle() * 200f);
            sink += terrain.Distance(p.X, p.Y, p.Z);
        }
        double perPoint = watch.Elapsed.TotalMilliseconds * 1000.0 / Points;

        var materials = new byte[NodeChunkStore.ChunkVolume];
        var fills = new byte[NodeChunkStore.ChunkVolume];
        var chunks = new List<Vector3I>();
        foreach (RegionType type in new[] { RegionType.Flat, RegionType.Hills, RegionType.Mountains, RegionType.Basin })
            chunks.Add(SurfaceChunk(generator, Into(terrain, type)));
        chunks.Add(SurfaceChunk(generator, IslandZone(terrain, 0)));

        watch.Restart();
        foreach (Vector3I chunk in chunks)
            generator.Generate(chunk, materials, fills);
        double perChunk = watch.Elapsed.TotalMilliseconds / chunks.Count;

        GD.Print($"    terrain: {perPoint:0.00} us a point, {perChunk:0.0} ms a surface chunk ({sink:0})");
        Check(perChunk < 120, $"a surface chunk took {perChunk:0} ms");
    }

    // ------------------------------------------------------ determinism

    [Test]
    public void SameSeedSameWorld()
    {
        var random = new Random(3);
        PlanetGenerator a = Generator(Terrain(42)), b = Generator(Terrain(42)), c = Generator(Terrain(43));

        var ma = new byte[NodeChunkStore.ChunkVolume];
        var fa = new byte[NodeChunkStore.ChunkVolume];
        var mb = new byte[NodeChunkStore.ChunkVolume];
        var fb = new byte[NodeChunkStore.ChunkVolume];

        int differ = 0;
        for (int n = 0; n < 4; n++)
        {
            Vector3I chunk = SurfaceChunk(a, Direction(random));

            a.Generate(chunk, ma, fa);
            b.Generate(chunk, mb, fb);
            Check(ma.SequenceEqual(mb) && fa.SequenceEqual(fb), $"seed 42 built chunk {chunk} two ways");

            c.Generate(chunk, mb, fb);
            if (!fa.SequenceEqual(fb))
                differ++;
        }

        Check(differ > 0, "seeds 42 and 43 built the same ground");
    }

    [Test]
    public void ParallelMatchesSerial()
    {
        PlanetTerrain terrain = Terrain();
        PlanetGenerator generator = Generator(terrain);
        var random = new Random(9);

        var chunks = new List<Vector3I>();
        for (int n = 0; n < 6; n++)
        {
            Vector3I surface = SurfaceChunk(generator, Direction(random));
            chunks.Add(surface);
            chunks.Add(surface + Vector3I.Up);
        }

        var serial = chunks.Select(chunk =>
        {
            var m = new byte[NodeChunkStore.ChunkVolume];
            var f = new byte[NodeChunkStore.ChunkVolume];
            generator.Generate(chunk, m, f);
            return (m, f);
        }).ToList();

        var parallel = new (byte[] m, byte[] f)[chunks.Count];
        Parallel.For(0, chunks.Count, i =>
        {
            var m = new byte[NodeChunkStore.ChunkVolume];
            var f = new byte[NodeChunkStore.ChunkVolume];
            generator.Generate(chunks[i], m, f);
            parallel[i] = (m, f);
        });

        for (int i = 0; i < chunks.Count; i++)
        {
            Check(serial[i].m.SequenceEqual(parallel[i].m) && serial[i].f.SequenceEqual(parallel[i].f),
                $"chunk {chunks[i]} differs when built alongside others");
        }
    }

    // ------------------------------------------------------------ shape

    /// <summary>
    /// The shape never changes faster than it says it does. Chunk culling
    /// trusts that bound; a shape steeper than it claims could skip ground.
    /// </summary>
    [Test]
    public void SlopeBoundHolds()
    {
        PlanetTerrain terrain = Terrain();
        var random = new Random(17);
        double worst = 0;
        Vector3 where = default;
        const double Step = 0.5;

        for (int n = 0; n < 30000; n++)
        {
            Vector3 u = Direction(random);
            double r = Radius - 40 + random.NextDouble() * 360;
            double x = u.X * r, y = u.Y * r, z = u.Z * r;

            // The shape as culling sees it: the terrain inside a box round
            // both points, which near sky islands counts just the islands
            // that reach the box.
            var at = new Vector3((float)x, (float)y, (float)z);
            ITerrainShape shape = ((ITerrainShape)terrain).Within(at - Vector3.One, at + Vector3.One);

            double d = shape.Distance(x, y, z);

            Vector3 step = Direction(random);
            double e = shape.Distance(x + step.X * Step, y + step.Y * Step, z + step.Z * Step);
            double change = Math.Abs(e - d) / Step;

            if (change > worst)
            {
                worst = change;
                where = new Vector3((float)x, (float)y, (float)z);
            }
        }

        TerrainInfo info = terrain.Describe(where);
        GD.Print($"    steepest change: {worst:0.00} per unit (bound {terrain.Slope}), "
            + $"in {info.Type} {info.Character}, {where.Length() - Radius:0} up");
        Check(worst < terrain.Slope, $"the shape changed {worst:0.00} per unit, past its bound of {terrain.Slope}");
    }

    /// <summary>Nothing solid above the top, nothing hollow below the bottom.</summary>
    [Test]
    public void TopAndBottomBoundTheGround()
    {
        PlanetTerrain terrain = Terrain();
        var random = new Random(23);

        for (int n = 0; n < 4000; n++)
        {
            Vector3 u = Direction(random);
            double above = terrain.Top, below = terrain.Bottom;

            Check(terrain.Distance(u.X * above, u.Y * above, u.Z * above) < 0, $"solid above the top along {u}");
            Check(terrain.Distance(u.X * below, u.Y * below, u.Z * below) > 8, $"not deep rock at the bottom along {u}");
        }
    }

    /// <summary>
    /// A chunk skipped as all air or all rock really is: every cell checked
    /// one by one agrees. Tried around the ground of every kind of region,
    /// where a wrong skip would leave a flat-faced cube of rock or a hole.
    /// </summary>
    [Test]
    public void SkippedChunksMatchTheirCells()
    {
        PlanetTerrain terrain = Terrain();
        PlanetGenerator generator = Generator(terrain);
        var random = new Random(41);
        int skipped = 0;

        var spots = new List<Vector3>();
        foreach (RegionType type in new[] { RegionType.Flat, RegionType.Hills, RegionType.Mountains, RegionType.Basin })
            spots.Add(Into(terrain, type));
        spots.Add(CanyonMiddle(terrain, 0));
        spots.Add(IslandZone(terrain, 0));
        foreach (MountainCharacter character in Enum.GetValues<MountainCharacter>())
            spots.Add(Into(terrain, RegionType.Mountains, character));

        foreach (Vector3 spot in spots)
        {
            Vector3I middle = SurfaceChunk(generator, spot);

            for (int x = -1; x <= 1; x++)
            for (int y = -5; y <= 5; y++)
            for (int z = -1; z <= 1; z++)
            {
                Vector3I chunk = middle + new Vector3I(x, y, z);
                int side = generator.Uniform(chunk);
                if (side == 0)
                    continue;

                skipped++;
                byte expected = side > 0 ? (byte)NodeMaterial.Stone : NodeChunkStore.Air;
                Vector3I origin = NodeChunkStore.OriginOf(chunk);

                for (int n = 0; n < 60; n++)
                {
                    Vector3I cell = origin + new Vector3I(random.Next(32), random.Next(32), random.Next(32));
                    generator.Classify(cell, out byte material, out _);
                    Equal(material, expected, $"cell {cell} of chunk {chunk}, skipped as {(side > 0 ? "rock" : "air")}");
                }
            }
        }

        Check(skipped > 20, $"only {skipped} chunks were skipped: the check proves little");
    }

    // ---------------------------------------------------------- regions

    [Test]
    public void RegionsSplitAsPlanned()
    {
        PlanetTerrain terrain = Terrain();
        var settings = new TerrainSettings();
        var random = new Random(31);
        var counts = new int[Enum.GetValues<RegionType>().Length];
        const int Samples = 20000;

        for (int n = 0; n < Samples; n++)
        {
            Vector3 u = Direction(random);
            counts[(int)terrain.Regions.Types[terrain.Regions.Nearest(u.X, u.Y, u.Z)]]++;
        }

        float Share(RegionType type) => counts[(int)type] / (float)Samples;
        GD.Print($"    area: {Share(RegionType.Flat):P0} flat, {Share(RegionType.Hills):P0} hills, "
            + $"{Share(RegionType.Mountains):P0} mountains, {Share(RegionType.Basin):P0} basins");

        Near(Share(RegionType.Flat), settings.FlatShare, 0.06f, "flat share");
        Near(Share(RegionType.Hills), settings.HillsShare, 0.06f, "hills share");
        Near(Share(RegionType.Basin), settings.BasinShare, 0.06f, "basin share");
        Near(Share(RegionType.Mountains), 1f - settings.FlatShare - settings.HillsShare - settings.BasinShare,
            0.06f, "mountain share");
    }

    private static Vector3 CanyonMiddle(PlanetTerrain terrain, int which)
    {
        Canyons.Path path = terrain.Canyons.Paths[which];
        int middle = path.Count / 2;
        return new Vector3((float)path.X[middle], (float)path.Y[middle], (float)path.Z[middle]);
    }

    private static Vector3 IslandZone(PlanetTerrain terrain, int which)
    {
        SkyIslands.Zone zone = terrain.Islands.Zones[which];
        return new Vector3((float)zone.X, (float)zone.Y, (float)zone.Z);
    }

    /// <summary>
    /// Basins gather into a few oceans, each one connected body, with a
    /// shallow shelf by the coast and deep water further out.
    /// </summary>
    [Test]
    public void BasinsGatherIntoOceans()
    {
        PlanetTerrain terrain = Terrain();
        var settings = new TerrainSettings();
        RegionMap map = terrain.Regions;

        Check(map.Oceans >= settings.OceansLow && map.Oceans <= settings.OceansHigh,
            $"{map.Oceans} oceans, not {settings.OceansLow}-{settings.OceansHigh}");

        // Every basin region belongs to an ocean, and each ocean is one body:
        // from any of its regions, stepping only between close neighbours in
        // the same ocean reaches all the others.
        var sizes = new int[map.Oceans];
        for (int i = 0; i < map.Count; i++)
        {
            Equal(map.Types[i] == RegionType.Basin, map.Ocean[i] >= 0, $"region {i} is a basin exactly when it is ocean");
            if (map.Ocean[i] >= 0)
                sizes[map.Ocean[i]]++;
        }

        double spacing = Math.Sqrt(4 * Math.PI / map.Count);
        for (int o = 0; o < map.Oceans; o++)
        {
            var reached = new HashSet<int>();
            var frontier = new Stack<int>();
            int start = Array.IndexOf(map.Ocean, o);
            frontier.Push(start);
            reached.Add(start);

            while (frontier.Count > 0)
            {
                int i = frontier.Pop();
                for (int j = 0; j < map.Count; j++)
                {
                    if (map.Ocean[j] != o || reached.Contains(j))
                        continue;

                    double dot = map.X[i] * map.X[j] + map.Y[i] * map.Y[j] + map.Z[i] * map.Z[j];
                    if (Math.Acos(Math.Clamp(dot, -1, 1)) < spacing * 1.8)
                    {
                        reached.Add(j);
                        frontier.Push(j);
                    }
                }
            }

            Equal(reached.Count, sizes[o], $"regions of ocean {o} joined up");
        }

        // Shelf by the coast, deep water out in the middle.
        var floors = new List<double>();
        for (int i = 0; i < map.Count; i++)
        {
            if (map.Types[i] != RegionType.Basin)
                continue;

            var u = new Vector3((float)map.X[i], (float)map.Y[i], (float)map.Z[i]);
            if (terrain.Islands.ZoneAt(u.X, u.Y, u.Z, out double inside) >= 0 && inside > -0.2)
                continue;

            floors.Add(TerrainShapes.SurfaceRadius(terrain, u) - Radius);
        }

        floors.Sort();
        GD.Print($"    {map.Oceans} oceans of {string.Join(", ", sizes)} regions; "
            + $"floors {floors[0]:0}..{floors[^1]:0} (median {floors[floors.Count / 2]:0})");

        Check(floors[0] >= -settings.DeepHigh - 10, $"an ocean sinks to {floors[0]:0}");
        Check(floors[0] <= -settings.DeepLow * 0.9, $"no ocean reaches deep water: the deepest is {floors[0]:0}");
        Check(floors[^1] < -20, $"an ocean region barely sinks, to {floors[^1]:0}");
    }

    /// <summary>
    /// A handful of canyons, each leaving a basin and running out through
    /// plains and hills -- never mountains or sky islands -- cut well below
    /// the ground beside them.
    /// </summary>
    [Test]
    public void CanyonsRunOutOfBasins()
    {
        PlanetTerrain terrain = Terrain();
        var settings = new TerrainSettings();
        RegionMap map = terrain.Regions;
        IReadOnlyList<Canyons.Path> paths = terrain.Canyons.Paths;

        Check(paths.Count >= settings.CanyonsLow && paths.Count <= settings.CanyonsHigh,
            $"{paths.Count} canyons, not {settings.CanyonsLow}-{settings.CanyonsHigh}");

        var depths = new List<double>();
        foreach (Canyons.Path path in paths)
        {
            Equal(map.Types[terrain.RegionAt(path.X[0], path.Y[0], path.Z[0])], RegionType.Basin, "the canyon's mouth");

            for (int i = 0; i < path.Count; i++)
            {
                RegionType type = map.Types[terrain.RegionAt(path.X[i], path.Y[i], path.Z[i])];
                Check(type != RegionType.Mountains, "a canyon runs through mountains");
                Check(terrain.Islands.ZoneAt(path.X[i], path.Y[i], path.Z[i], out double inside) < 0 || inside < -0.3,
                    "a canyon runs into a sky-island zone");
            }

            // Its floor against the ground to one side, a third of the way up.
            int at = path.Count / 3;
            var u = new Vector3((float)path.X[at], (float)path.Y[at], (float)path.Z[at]);
            var next = new Vector3((float)path.X[at + 1], (float)path.Y[at + 1], (float)path.Z[at + 1]);
            Vector3 side = u.Cross(next - u).Normalized();
            Vector3 beside = (u + side * (250f / Radius)).Normalized();

            double floor = TerrainShapes.SurfaceRadius(terrain, u);
            double ground = TerrainShapes.SurfaceRadius(terrain, beside);
            depths.Add(ground - floor);

            // Reported as a canyon, whatever it cuts through.
            Equal(terrain.Describe(u * (float)floor).Type, RegionType.Canyon, "the terrain on a canyon's floor");
        }

        depths.Sort();
        GD.Print($"    {paths.Count} canyons, cut {depths[0]:0}..{depths[^1]:0} below the ground beside them");
        Check(depths[0] > 20, $"a canyon is only {depths[0]:0} deep");
    }

    /// <summary>
    /// Three to five sky-island zones, each with its chasm floor far below the
    /// rim and ground floating over open air.
    /// </summary>
    [Test]
    public void SkyIslandsFloatOverAChasm()
    {
        PlanetTerrain terrain = Terrain();
        var settings = new TerrainSettings();
        IReadOnlyList<SkyIslands.Zone> zones = terrain.Islands.Zones;

        Check(zones.Count >= settings.IslandZonesLow && zones.Count <= settings.IslandZonesHigh,
            $"{zones.Count} zones, not {settings.IslandZonesLow}-{settings.IslandZonesHigh}");

        var random = new Random(71);
        foreach (SkyIslands.Zone zone in zones)
        {
            var centre = new Vector3((float)zone.X, (float)zone.Y, (float)zone.Z);
            Vector3 east = centre.Cross(Mathf.Abs(centre.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            Vector3 north = centre.Cross(east);

            int floating = 0, floors = 0;
            double deepest = double.MaxValue;

            for (int n = 0; n < 200; n++)
            {
                double angle = random.NextDouble() * Math.Tau;
                double out1 = Math.Sqrt(random.NextDouble()) * zone.Radius * 0.85 / Radius;
                Vector3 u = (centre + (east * (float)Math.Cos(angle) + north * (float)Math.Sin(angle)) * (float)out1).Normalized();

                // Up the column from below the chasm: solid, then air, then
                // solid again is ground floating.
                bool wasSolid = true, sawAir = false;
                double bottom = double.NaN;
                for (double r = terrain.Bottom; r < terrain.Top; r += 2)
                {
                    bool solid = terrain.Distance(u.X * r, u.Y * r, u.Z * r) > 0;
                    if (wasSolid && !solid && double.IsNaN(bottom))
                        bottom = r - Radius;
                    if (!solid)
                        sawAir = true;
                    else if (sawAir)
                    {
                        floating++;
                        break;
                    }

                    wasSolid = solid;
                }

                if (!double.IsNaN(bottom))
                {
                    floors++;
                    deepest = Math.Min(deepest, bottom);
                }
            }

            GD.Print($"    zone rim {zone.Rim:0}: floor down to {deepest - zone.Rim:0}, {floating} of 200 columns have ground floating");
            Check(floating >= 20, $"only {floating} of 200 columns in a zone have anything floating");
            Check(deepest < zone.Rim - settings.ChasmDepth * 0.8, $"the chasm floor only reaches {deepest - zone.Rim:0} below the rim");
        }
    }

    /// <summary>
    /// Each zone's crater is filled with broken ground: slabs, chunks and
    /// shards by the thousand, heaped highest at the middle, thinning as they
    /// climb; the land round the rim cracked by crevices.
    /// </summary>
    [Test]
    public void SkyIslandsFillTheCrater()
    {
        PlanetTerrain terrain = Terrain();
        var settings = new TerrainSettings();
        var random = new Random(29);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        SkyIslands.Zone zone = terrain.Islands.Zones[0];
        (int slabs, int chunks, int shards) = zone.Counts;
        double layMs = watch.Elapsed.TotalMilliseconds;

        var centre = new Vector3((float)zone.X, (float)zone.Y, (float)zone.Z);
        Vector3 east = centre.Cross(Mathf.Abs(centre.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        Vector3 north = centre.Cross(east);

        Vector3 At(double inward, double angle) =>
            (centre + (east * (float)Math.Cos(angle) + north * (float)Math.Sin(angle))
                * (float)((1 - inward) * zone.Radius * 0.95 / Radius)).Normalized();

        bool Solid(Vector3 u, double above)
        {
            double r = Radius + zone.Rim + above;
            return terrain.Distance(u.X * r, u.Y * r, u.Z * r) > 0;
        }

        // The highest solid ground over the rim, in columns near the middle
        // and near the edge.
        double Top(double inward)
        {
            double top = double.NegativeInfinity;
            for (int n = 0; n < 60; n++)
            {
                Vector3 u = At(inward + (random.NextDouble() - 0.5) * 0.1, random.NextDouble() * Math.Tau);
                for (double above = settings.IslandHeight + 60; above > -settings.ChasmDepth; above -= 2)
                {
                    if (Solid(u, above))
                    {
                        top = Math.Max(top, above);
                        break;
                    }
                }
            }

            return top;
        }

        double middle = Top(0.93), edge = Top(0.15);

        // How much of the heap is solid, low and high.
        double Filled(double from, double to)
        {
            int solid = 0, all = 0;
            for (int n = 0; n < 4000; n++)
            {
                double inward = Math.Sqrt(random.NextDouble());
                double above = from + random.NextDouble() * (to - from);
                if (above > 30 + (settings.IslandHeight - 30) * Math.Pow(inward, 1.6))
                    continue;

                all++;
                if (Solid(At(inward, random.NextDouble() * Math.Tau), above))
                    solid++;
            }

            return all == 0 ? 0 : (double)solid / all;
        }

        double low = Filled(-settings.ChasmDepth + 40, 0), high = Filled(settings.IslandHeight * 0.55, settings.IslandHeight);

        // Crevices round the rim: the ground a little way out, all round.
        var heights = new List<double>();
        for (int n = 0; n < 400; n++)
        {
            double angle = random.NextDouble() * Math.Tau;
            Vector3 u = At(-0.08, angle);
            heights.Add(TerrainShapes.SurfaceRadius(terrain, u) - Radius - zone.Rim);
        }

        heights.Sort();
        double median = heights[heights.Count / 2];
        int crevices = heights.FindAll(h => h < median - 10).Count;

        GD.Print($"    zone 0: {slabs} slabs, {chunks} chunks, {shards} shards (laid out in {layMs:0} ms); "
            + $"highest {middle:0} over the rim at the middle, {edge:0} near the edge; "
            + $"solid {low:P0} low, {high:P0} high; {crevices} of 400 spots round the rim in a crevice");

        Check(slabs > 300 && chunks > 300 && shards > 300, "too few pieces of some kind");
        Check(slabs + chunks + shards > 5000, $"only {slabs + chunks + shards} pieces fill the crater");
        Check(middle > settings.IslandHeight * 0.7, $"the heap only reaches {middle:0} over the rim at its middle");
        Check(edge < settings.IslandHeight * 0.35, $"the heap reaches {edge:0} over the rim near its edge");
        Check(high < low, $"the heap is no sparser high ({high:P0}) than low ({low:P0})");
        Check(crevices >= 8, $"only {crevices} of 400 spots round the rim fall into a crevice");
    }

    /// <summary>The slope bound holds in and over the zones too, for boxes of every size the far terrain asks about.</summary>
    [Test]
    public void SlopeBoundHoldsOverSkyIslands()
    {
        PlanetTerrain terrain = Terrain();
        var random = new Random(31);
        double worst = 0;
        const double Step = 0.5;

        foreach (SkyIslands.Zone zone in terrain.Islands.Zones)
        {
            var centre = new Vector3((float)zone.X, (float)zone.Y, (float)zone.Z);
            for (int n = 0; n < 6000; n++)
            {
                Vector3 u = (centre + Direction(random) * (float)(random.NextDouble() * zone.Radius * 1.3 / Radius)).Normalized();
                double r = Radius + zone.Rim - 260 + random.NextDouble() * 1300;
                double x = u.X * r, y = u.Y * r, z = u.Z * r;

                var at = new Vector3((float)x, (float)y, (float)z);
                float half = new[] { 1f, 40f, 600f }[n % 3];
                ITerrainShape shape = ((ITerrainShape)terrain).Within(at - Vector3.One * half, at + Vector3.One * half);

                Vector3 step = Direction(random);
                double d = shape.Distance(x, y, z);
                double e = shape.Distance(x + step.X * Step, y + step.Y * Step, z + step.Z * Step);
                worst = Math.Max(worst, Math.Abs(e - d) / Step);
            }
        }

        GD.Print($"    steepest change over the zones: {worst:0.00} per unit (bound {terrain.Slope})");
        Check(worst < terrain.Slope, $"the shape changed {worst:0.00} per unit over a zone, past its bound of {terrain.Slope}");
    }

    [Test]
    public void EveryMountainCharacterAppears()
    {
        PlanetTerrain terrain = Terrain();
        foreach (MountainCharacter character in Enum.GetValues<MountainCharacter>())
            Into(terrain, RegionType.Mountains, character);
    }

    /// <summary>
    /// The highest ground in each region of a type (and, for mountains,
    /// whether towering or not), sampled on a grid across it.
    /// </summary>
    private static List<double> Highest(PlanetTerrain terrain, RegionType type, bool? towering = null)
    {
        RegionMap map = terrain.Regions;
        var highest = new List<double>();
        const double Step = 40, Reach = 1400;

        for (int region = 0; region < map.Count; region++)
        {
            if (map.Types[region] != type
                || (towering != null && (map.Characters[region] == MountainCharacter.Towering) != towering))
                continue;

            var middle = new Vector3((float)map.X[region], (float)map.Y[region], (float)map.Z[region]);
            Vector3 east = middle.Cross(Mathf.Abs(middle.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            Vector3 north = middle.Cross(east);
            double top = double.NegativeInfinity;

            for (double a = -Reach; a <= Reach; a += Step)
            for (double b = -Reach; b <= Reach; b += Step)
            {
                Vector3 u = (middle + east * (float)(a / Radius) + north * (float)(b / Radius)).Normalized();
                if (terrain.RegionAt(u.X, u.Y, u.Z) != region)
                    continue;

                // On a heightfield the distance at the base sphere is the height.
                top = Math.Max(top, terrain.Distance(u.X * Radius, u.Y * Radius, u.Z * Radius));
            }

            highest.Add(top);
        }

        highest.Sort();
        return highest;
    }

    [Test]
    public void MountainsAndHillsReachTheirHeights()
    {
        PlanetTerrain terrain = Terrain();
        var settings = new TerrainSettings();

        List<double> ranges = Highest(terrain, RegionType.Mountains, towering: false);
        List<double> towers = Highest(terrain, RegionType.Mountains, towering: true);
        List<double> hills = Highest(terrain, RegionType.Hills);

        GD.Print($"    mountain tops {ranges[0]:0}..{ranges[^1]:0} (median {ranges[ranges.Count / 2]:0}), "
            + $"towering tops {towers[0]:0}..{towers[^1]:0} (median {towers[towers.Count / 2]:0}), "
            + $"hill tops {hills[0]:0}..{hills[^1]:0} (median {hills[hills.Count / 2]:0})");

        Check(ranges[^1] <= settings.PeakHigh + settings.JaggedWarp, $"a range tops out at {ranges[^1]:0}");
        Check(ranges[ranges.Count / 2] >= settings.PeakLow * 0.8, $"half the ranges stay under {ranges[ranges.Count / 2]:0}");
        Check(towers[^1] <= settings.TowerPeakHigh + settings.TowerWarp, $"a towering range tops out at {towers[^1]:0}");
        Check(towers[towers.Count / 2] >= settings.TowerPeakLow * 0.8,
            $"half the towering ranges stay under {towers[towers.Count / 2]:0}");
        Check(towers[towers.Count / 2] > ranges[^1], "the towering ranges do not stand over the rest");
        // Hill country beside a range rises into it at the blend.
        Check(hills[^1] <= settings.PeakLow, $"hills reach {hills[^1]:0}");
        Check(hills[hills.Count / 2] >= 20, $"half the hill country stays under {hills[hills.Count / 2]:0}");
    }

    // ------------------------------------------------------------ sand

    /// <summary>A full depth of sand over rock on level ground.</summary>
    [Test]
    public void FlatGroundHasItsSand()
    {
        PlanetTerrain terrain = Terrain();
        PlanetGenerator generator = Generator(terrain);
        var settings = new TerrainSettings();

        Vector3 up = Into(terrain, RegionType.Flat);
        Vector3 ground = TerrainShapes.SurfacePoint(terrain, up);

        generator.Classify(generator.Grid.CellAt(ground - up * (settings.SandDepth - 2f)), out byte shallow, out _);
        generator.Classify(generator.Grid.CellAt(ground - up * (settings.SandDepth + 3f)), out byte deep, out _);

        Equal(shallow, (byte)NodeMaterial.Sand, "two units above the bottom of the sand");
        Equal(deep, (byte)NodeMaterial.Stone, "three units below the bottom of the sand");
    }

    /// <summary>Cliffs are bare rock: no sand where the ground is steeper than sand stands.</summary>
    [Test]
    public void CliffsAreBareRock()
    {
        PlanetTerrain terrain = Terrain();
        PlanetGenerator generator = Generator(terrain);
        var random = new Random(47);
        int cliffs = 0;

        for (int n = 0; n < 4000 && cliffs < 40; n++)
        {
            Vector3 u = Direction(random);
            if (terrain.Regions.Types[terrain.RegionAt(u.X, u.Y, u.Z)] != RegionType.Mountains)
                continue;

            // The slope as the chunks see it: the shape sampled every other
            // node. Finer than that -- a knife-edge crest, a single ledge --
            // the ground is built smoother than it is measured exactly.
            Vector3 ground = TerrainShapes.SurfacePoint(terrain, u);
            var field = new DistanceBlock();
            field.FillBox(terrain, generator.SampleSpacing, ground - Vector3.One * 6, ground + Vector3.One * 6);
            Vector3 outward = -field.GradientAt(ground).Normalized();
            if (Mathf.RadToDeg(outward.AngleTo(u)) < 70f)
                continue;

            cliffs++;
            for (float along = -1.5f; along <= 1.5f; along += 0.75f)
            {
                generator.Classify(generator.Grid.CellAt(ground + outward * along), out byte material, out _);
                Check(material != (byte)NodeMaterial.Sand, $"sand on a {Mathf.RadToDeg(outward.AngleTo(u)):0} degree cliff at {ground}");
            }
        }

        Check(cliffs >= 10, $"only found {cliffs} cliffs to look at");
    }

    private static Vector3 Gradient(ITerrainShape shape, Vector3 p)
    {
        const float h = 0.5f;
        return new Vector3(
            (float)(shape.Distance(p.X + h, p.Y, p.Z) - shape.Distance(p.X - h, p.Y, p.Z)),
            (float)(shape.Distance(p.X, p.Y + h, p.Z) - shape.Distance(p.X, p.Y - h, p.Z)),
            (float)(shape.Distance(p.X, p.Y, p.Z + h) - shape.Distance(p.X, p.Y, p.Z - h))) / (2 * h);
    }

    // ------------------------------------------------------------ spawn

    /// <summary>
    /// The spawn point is on the ground and nothing is over it: the top of
    /// whatever is there, never under an arch or an overhang.
    /// </summary>
    [Test]
    public void SurfacePointIsOnTopOfTheGround()
    {
        var random = new Random(53);

        foreach (ulong seed in new ulong[] { 1, 2, 99 })
        {
            PlanetTerrain terrain = Terrain(seed);

            for (int n = 0; n < 25; n++)
            {
                Vector3 u = Direction(random);
                Vector3 ground = TerrainShapes.SurfacePoint(terrain, u);

                Near((float)terrain.Distance(ground.X, ground.Y, ground.Z), 0f, 0.05f, $"distance at the ground along {u}");

                for (double above = ground.Length() + 1; above < terrain.Top; above += 1.5)
                    Check(terrain.Distance(u.X * above, u.Y * above, u.Z * above) < 0,
                        $"solid ground {above - ground.Length():0} units over the spawn point along {u} (seed {seed})");
            }
        }
    }

    // ------------------------------------------------------------- seed

    [Test]
    public void SeedsReadAsNumbersOrWords()
    {
        Equal(WorldSeed.Parse("12345"), 12345UL, "a number");
        Equal(WorldSeed.Parse("  12345 "), 12345UL, "a number with spaces");
        Equal(WorldSeed.Parse("banana"), WorldSeed.Parse("banana"), "a word, twice");
        Check(WorldSeed.Parse("banana") != WorldSeed.Parse("Banana"), "case matters");
        Check(WorldSeed.Parse("banana") != WorldSeed.Parse("bananas"), "every letter matters");

        string random = WorldSeed.RandomText();
        Equal(WorldSeed.Parse(random).ToString(), random, "a random seed reads back as itself");
    }
}
