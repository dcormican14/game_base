using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GameBase.Core;
using GameBase.Nodes;
using GameBase.Planets;
using GameBase.Terrain;

namespace GameBase.Tests;

/// <summary>The far terrain: that it draws the same ground, keeps its features, and never leaves a hole.</summary>
public sealed class FarTerrainTests : TestSuite
{
    private const float Radius = 6000f;

    /// <summary>
    /// A known arch on a flat planet, at the north pole: a ring standing
    /// upright on the ground, <see cref="Span"/> across inside, its rock
    /// <see cref="Thickness"/> thick -- the smallest the far terrain promises
    /// to keep.
    /// </summary>
    private sealed class ArchWorld : ITerrainShape
    {
        public const float Span = 40f;
        public const float Thickness = 20f;
        private const float Ring = (Span + Thickness) / 2f;

        public double BaseRadius => Radius;
        public double Top => Radius + Ring + Thickness;
        public double Bottom => Radius - 64;
        public double Slope => 1.5;

        public double Distance(double x, double y, double z)
        {
            double ground = Radius - Math.Sqrt(x * x + y * y + z * z);

            // The ring lies in the x-y plane, centred on the ground.
            double dy = y - Radius;
            double around = Math.Sqrt(x * x + dy * dy) - Ring;
            double arch = Thickness / 2 - Math.Sqrt(around * around + z * z);

            return Math.Max(ground, arch);
        }

        public TerrainInfo Describe(Vector3 point) => default;
    }

    private static List<(Vector3 A, Vector3 B, Vector3 C)> Mesh(ITerrainShape shape, int level, Aabb around)
    {
        float cell = 2f * (1 << level);
        float size = cell * FarTerrain.Cells;
        var triangles = new List<(Vector3, Vector3, Vector3)>();

        Vector3I low = new(Mathf.FloorToInt(around.Position.X / size), Mathf.FloorToInt(around.Position.Y / size),
            Mathf.FloorToInt(around.Position.Z / size));
        Vector3I high = new(Mathf.FloorToInt(around.End.X / size), Mathf.FloorToInt(around.End.Y / size),
            Mathf.FloorToInt(around.End.Z / size));

        for (int x = low.X; x <= high.X; x++)
        for (int y = low.Y; y <= high.Y; y++)
        for (int z = low.Z; z <= high.Z; z++)
        {
            BlockMesh mesh = FarMesher.Build(shape, SandRules.Uniform(6f), new Vector3I(x, y, z), cell);
            for (int i = 0; i < mesh.Indices.Length; i += 3)
                triangles.Add((mesh.Vertices[mesh.Indices[i]], mesh.Vertices[mesh.Indices[i + 1]], mesh.Vertices[mesh.Indices[i + 2]]));
        }

        return triangles;
    }

    private static bool Hits(List<(Vector3 A, Vector3 B, Vector3 C)> triangles, Vector3 from, Vector3 to)
    {
        Vector3 direction = to - from;

        foreach ((Vector3 a, Vector3 b, Vector3 c) in triangles)
        {
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = direction.Cross(e2);
            float det = e1.Dot(p);
            if (Mathf.Abs(det) < 1e-9f)
                continue;

            Vector3 s = from - a;
            float u = s.Dot(p) / det;
            Vector3 q = s.Cross(e1);
            float v = direction.Dot(q) / det;
            float t = e2.Dot(q) / det;

            if (u >= 0 && v >= 0 && u + v <= 1 && t >= 0 && t <= 1)
                return true;
        }

        return false;
    }

    /// <summary>
    /// An arch as thin as the far terrain promises to keep survives the three
    /// finest levels -- out past a kilometre -- with its opening still open.
    /// </summary>
    [Test]
    public void ArchesSurviveTheFarTerrain()
    {
        var shape = new ArchWorld();
        var around = new Aabb(new Vector3(-60, Radius - 20, -30), new Vector3(120, 100, 60));

        // Halfway up the opening, and the middle of the arch's rock.
        const float Under = ArchWorld.Span / 4;
        const float Crown = ArchWorld.Span / 2 + ArchWorld.Thickness / 2;

        for (int level = 1; level <= 3; level++)
        {
            List<(Vector3, Vector3, Vector3)> triangles = Mesh(shape, level, around);

            // Straight through the opening, side to side of the ring's plane.
            Check(!Hits(triangles, new Vector3(0, Radius + Under, -80), new Vector3(0, Radius + Under, 80)),
                $"level {level} ({2 << level}-unit cells) closed the arch's opening");

            // Into the crown of the arch.
            Check(Hits(triangles, new Vector3(0, Radius + Crown, -80), new Vector3(0, Radius + Crown, 80)),
                $"level {level} ({2 << level}-unit cells) lost the arch itself");
        }
    }

    /// <summary>
    /// The first ring's ground lies where the real chunks' does: every vertex
    /// it draws is within a node of the surface the chunks are built on.
    /// </summary>
    [Test]
    public void FirstRingAgreesWithTheChunks()
    {
        var terrain = new PlanetTerrain(Radius, 1, new TerrainSettings());
        var generator = new PlanetGenerator(new VoronoiGrid(2f), terrain, SandRules.From(new TerrainSettings()));
        var random = new Random(7);
        int checkedVertices = 0;
        float worst = 0f;

        for (int n = 0; n < 6; n++)
        {
            Vector3 u = new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f).Normalized();
            Vector3 ground = TerrainShapes.SurfacePoint(terrain, u);
            Vector3I chunk = NodeChunkStore.ChunkOf(generator.Grid.CellAt(ground));

            BlockMesh mesh = FarMesher.Build(terrain, generator.Sand, chunk, 4f);

            var field = new DistanceBlock();
            float size = 64f;
            Vector3 corner = new Vector3(chunk.X, chunk.Y, chunk.Z) * size;
            field.FillBox(terrain, generator.SampleSpacing, corner - Vector3.One * 8, corner + Vector3.One * (size + 8));

            // The ground's vertices; the skirt's lie inside it on purpose.
            for (int v = 0; v < mesh.SurfaceVertices; v++)
            {
                float off = Mathf.Abs(field.Normalized(mesh.Vertices[v], out _));
                worst = Mathf.Max(worst, off);
                checkedVertices++;
            }
        }

        GD.Print($"    first ring: {checkedVertices} vertices, furthest {worst:0.00} from the chunks' ground");
        Check(checkedVertices > 500, $"only {checkedVertices} vertices to check");
        Check(worst < 2f, $"a first-ring vertex is {worst:0.00} units off the chunks' ground");
    }

    /// <summary>
    /// Where the real chunks hand over to the first ring, the two grounds
    /// stand at nearly the same height: looking straight down on a chunk, the
    /// drawn real ground and the first ring's ground are measured against
    /// each other, over flat, hill and mountain ground.
    /// </summary>
    [Test]
    public async Task FirstRingMeetsTheDrawnGround()
    {
        var terrain = new PlanetTerrain(Radius, 1, new TerrainSettings());
        var generator = new PlanetGenerator(new VoronoiGrid(2f), terrain, SandRules.From(new TerrainSettings()));
        RegionMap map = terrain.Regions;
        var random = new Random(13);
        var report = new List<string>();
        float worstMean = 0f;

        foreach (RegionType type in new[] { RegionType.Flat, RegionType.Hills, RegionType.Mountains })
        {
            int region = Array.IndexOf(map.Types, type);
            var u = new Vector3((float)map.X[region], (float)map.Y[region], (float)map.Z[region]);
            Vector3 ground = TerrainShapes.SurfacePoint(terrain, u);
            Vector3I chunk = NodeChunkStore.ChunkOf(generator.Grid.CellAt(ground));

            var world = new NodeWorld();
            Add(world);
            world.Configure(generator.Grid);
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                var materials = new byte[NodeChunkStore.ChunkVolume];
                var fills = new byte[NodeChunkStore.ChunkVolume];
                generator.Generate(chunk + new Vector3I(x, y, z), materials, fills);
                world.InstallChunk(chunk + new Vector3I(x, y, z), materials, fills);
            }

            world.MeshAllNow();
            await PhysicsFrames(1);

            var far = new List<(Vector3, Vector3, Vector3)>();
            BlockMesh mesh = FarMesher.Build(terrain, generator.Sand, chunk, 4f);
            // Skirts and all: a skirt standing up out of the ground would be met first.
            for (int i = 0; i < mesh.Indices.Length; i += 3)
                far.Add((mesh.Vertices[mesh.Indices[i]], mesh.Vertices[mesh.Indices[i + 1]], mesh.Vertices[mesh.Indices[i + 2]]));

            var real = new List<(Vector3, Vector3, Vector3)>();
            foreach ((Vector3 origin, ArrayMesh drawn) in world.SectionMeshes)
            {
                for (int s = 0; s < drawn.GetSurfaceCount(); s++)
                {
                    var arrays = drawn.SurfaceGetArrays(s);
                    Vector3[] vertices = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
                    int[] indices = arrays[(int)Godot.Mesh.ArrayType.Index].AsInt32Array();
                    for (int i = 0; i < indices.Length; i += 3)
                        real.Add((origin + vertices[indices[i]], origin + vertices[indices[i + 1]], origin + vertices[indices[i + 2]]));
                }
            }

            // Straight down onto both, at spots across the chunk.
            var gaps = new List<float>();
            Vector3 corner = new Vector3(chunk.X, chunk.Y, chunk.Z) * 64f;
            for (int n = 0; n < 300; n++)
            {
                Vector3 spot = corner + new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) * 64f;
                Vector3 up = spot.Normalized();
                Vector3 top = up * (float)terrain.Top, bottom = up * (float)terrain.Bottom;
                if (!FirstHit(real, top, bottom, out float a) || !FirstHit(far, top, bottom, out float b))
                    continue;

                gaps.Add((b - a) * (top - bottom).Length());
            }

            gaps.Sort((p, q) => Mathf.Abs(p).CompareTo(Mathf.Abs(q)));
            float mean = 0f;
            foreach (float g in gaps)
                mean += g;
            mean /= Math.Max(1, gaps.Count);
            float meanAbs = 0f;
            foreach (float g in gaps)
                meanAbs += Mathf.Abs(g);
            meanAbs /= Math.Max(1, gaps.Count);

            report.Add($"{type}: {gaps.Count} spots, real stands {mean:+0.00;-0.00} over far on average, "
                + $"typically {meanAbs:0.00} apart, 90% within {Mathf.Abs(gaps[gaps.Count * 9 / 10]):0.00}, worst {Mathf.Abs(gaps[^1]):0.00}");
            worstMean = Mathf.Max(worstMean, meanAbs);

            world.QueueFree();
        }

        foreach (string line in report)
            GD.Print($"    {line}");

        Check(worstMean < 1f, $"the grounds are typically {worstMean:0.00} apart where they hand over");
    }

    /// <summary>
    /// Morphed onto its parent's ground (as the shader does near where the
    /// parent takes over), a level-1 block stands where the level-2 block
    /// over it draws the ground -- no ledge where the two meet. Measured
    /// straight down from each vertex onto the level-2 mesh, on mountains,
    /// where the levels differ most.
    /// </summary>
    [Test]
    public void MorphedGroundMeetsTheCoarserLevel()
    {
        var terrain = new PlanetTerrain(Radius, 1, new TerrainSettings());
        var sand = SandRules.From(new TerrainSettings());
        RegionMap map = terrain.Regions;
        var random = new Random(5);
        float before = 0f, after = 0f, worst = 0f;
        int measured = 0;

        for (int region = 0; region < map.Count && measured < 1200; region++)
        {
            if (map.Types[region] != RegionType.Mountains)
                continue;

            var u = new Vector3((float)map.X[region], (float)map.Y[region], (float)map.Z[region]);
            Vector3 ground = TerrainShapes.SurfacePoint(terrain, u);
            var coord = new Vector3I(Mathf.FloorToInt(ground.X / 64f), Mathf.FloorToInt(ground.Y / 64f), Mathf.FloorToInt(ground.Z / 64f));

            BlockMesh fine = FarMesher.Build(terrain, sand, coord, 4f);
            BlockMesh coarse = FarMesher.Build(terrain, sand, new Vector3I(coord.X >> 1, coord.Y >> 1, coord.Z >> 1), 8f);

            var triangles = new List<(Vector3, Vector3, Vector3)>();
            for (int i = 0; i < coarse.Indices.Length; i += 3)
                triangles.Add((coarse.Vertices[coarse.Indices[i]], coarse.Vertices[coarse.Indices[i + 1]], coarse.Vertices[coarse.Indices[i + 2]]));

            var box = new Aabb(new Vector3(coord.X, coord.Y, coord.Z) * 64f, Vector3.One * 64f);
            for (int n = 0; n < 300 && fine.SurfaceVertices > 0; n++)
            {
                int v = random.Next(fine.SurfaceVertices);
                Vector3 vertex = fine.Vertices[v];
                if (!box.HasPoint(vertex))
                    continue;

                // Straight down onto the coarse ground, from just above each
                // point: the vertex, and where it morphs to.
                Vector3 morphed = vertex + fine.MorphOffsets[v];
                if (!GapBelow(triangles, vertex, out float gapBefore) || !GapBelow(triangles, morphed, out float gapAfter))
                    continue;

                before += gapBefore;
                after += gapAfter;
                worst = Mathf.Max(worst, gapAfter);
                measured++;
            }
        }

        before /= Math.Max(1, measured);
        after /= Math.Max(1, measured);
        GD.Print($"    {measured} vertices: {before:0.00} from the coarser ground unmorphed, {after:0.00} morphed, worst {worst:0.00}");
        Check(measured > 300, $"only {measured} vertices measured");
        Check(after < 0.2f && after < before * 0.25f, $"morphing only brought the levels from {before:0.00} to {after:0.00} apart");
    }

    /// <summary>How far a point is above or below the first ground met coming down on it from above.</summary>
    private static bool GapBelow(List<(Vector3 A, Vector3 B, Vector3 C)> triangles, Vector3 point, out float gap)
    {
        Vector3 up = point.Normalized();
        Vector3 top = point + up * 40f, bottom = point - up * 40f;
        gap = 0f;
        if (!FirstHit(triangles, top, bottom, out float t))
            return false;

        gap = Mathf.Abs((point - top.Lerp(bottom, t)).Dot(up));
        return true;
    }

    /// <summary>How far along a segment it first meets a triangle (0..1), if at all.</summary>
    private static bool FirstHit(List<(Vector3 A, Vector3 B, Vector3 C)> triangles, Vector3 from, Vector3 to, out float t)
    {
        t = 2f;
        Vector3 direction = to - from;

        foreach ((Vector3 a, Vector3 b, Vector3 c) in triangles)
        {
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = direction.Cross(e2);
            float det = e1.Dot(p);
            if (Mathf.Abs(det) < 1e-9f)
                continue;

            Vector3 s = from - a;
            float u = s.Dot(p) / det;
            Vector3 q = s.Cross(e1);
            float v = direction.Dot(q) / det;
            float at = e2.Dot(q) / det;

            if (u >= 0 && v >= 0 && u + v <= 1 && at >= 0 && at < t)
                t = at;
        }

        return t <= 1f;
    }

    /// <summary>
    /// Does a segment (global) meet anything the node world DRAWS? Asked of the
    /// meshes rather than of physics: a section's collision is swapped a frame
    /// behind its mesh, and the question here is what the player sees.
    /// </summary>
    private static bool Drawn(NodeWorld world, Vector3 from, Vector3 to)
    {
        Vector3 a = world.ToLocal(from), b = world.ToLocal(to);

        foreach ((Vector3 origin, ArrayMesh mesh) in world.SectionMeshes)
        {
            if (!mesh.GetAabb().Grow(0.5f).IntersectsSegment(a - origin, b - origin))
                continue;

            var triangles = new List<(Vector3, Vector3, Vector3)>();
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                var arrays = mesh.SurfaceGetArrays(s);
                Vector3[] vertices = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
                int[] indices = arrays[(int)Godot.Mesh.ArrayType.Index].AsInt32Array();

                for (int i = 0; i < indices.Length; i += 3)
                    triangles.Add((origin + vertices[indices[i]], origin + vertices[indices[i + 1]], origin + vertices[indices[i + 2]]));
            }

            if (Hits(triangles, a, b))
                return true;
        }

        return false;
    }

    /// <summary>Where a ray should have met the ground, and what the world and far terrain hold there.</summary>
    private static string WhatIsThere(Planet planet, Vector3 from, Vector3 direction)
    {
        Vector3 local = planet.ToLocal(from);
        for (float t = 0; t < 600; t += 0.25f)
        {
            Vector3 p = local + direction * t;
            if (planet.Shape.Distance(p.X, p.Y, p.Z) < 0)
                continue;

            Vector3I chunk = NodeChunkStore.ChunkOf(planet.World.Grid.CellAt(p));
            return $"ground {t:0.0} away in chunk {chunk} (loaded {planet.World.IsChunkLoaded(chunk)}, "
                + $"meshed {planet.World.IsChunkMeshed(chunk)}); far {planet.Far.DescribeBlock(1, chunk)}";
        }

        return "no ground along it";
    }

    /// <summary>
    /// After a jump to ground that is not loaded, every frame, every way down
    /// from the player meets either real ground or far terrain: streaming
    /// never shows the sky through the planet.
    /// </summary>
    [Test]
    public async Task NoHolesWhileStreaming()
    {
        WorldSeed.Chosen = "1";
        var planet = GD.Load<PackedScene>("res://Modules/Planet/Planet.tscn").Instantiate<Planet>();
        var target = new Node3D { Name = "Target" };
        target.AddToGroup(Groups.Player);

        Add(planet);
        Add(target);

        Vector3 start = planet.SurfacePoint(Vector3.Up);
        target.GlobalPosition = start + Vector3.Up * 2f;

        ulong began = Time.GetTicksMsec();
        while (!planet.IsReady)
        {
            Check(Time.GetTicksMsec() - began < 90000, "the planet never became ready");
            await PhysicsFrames(1);
        }

        // Somewhere 300 units away, not yet loaded.
        Vector3 away = (Vector3.Up + Vector3.Right * 0.05f).Normalized();
        Vector3 up = away;
        target.GlobalPosition = planet.SurfacePoint(away) + up * 2f;

        Vector3 east = up.Cross(Vector3.Forward).Normalized();
        Vector3 north = up.Cross(east);
        var directions = new List<Vector3> { -up };
        for (int d = 0; d < 8; d++)
        {
            float angle = Mathf.Tau * d / 8f;
            Vector3 across = east * Mathf.Cos(angle) + north * Mathf.Sin(angle);
            directions.Add((across - up * 0.4f).Normalized());
        }

        int frames = 0, misses = 0;
        string first = null;

        for (; frames < 400; frames++)
        {
            await PhysicsFrames(1);

            foreach (Vector3 direction in directions)
            {
                Vector3 from = target.GlobalPosition;
                Vector3 to = from + direction * 600f;

                bool seen = Drawn(planet.World, from, to) || planet.Far.Raycast(from, to, out _);
                if (!seen)
                {
                    misses++;
                    first ??= $"frame {frames}, looking {direction}: {WhatIsThere(planet, from, direction)}";
                }
            }

            if (planet.Streamer.PendingChunks == 0 && !planet.World.HasPendingMeshes && frames > 60)
                break;
        }

        GD.Print($"    streamed for {frames} frames after the jump, {misses} rays met nothing");
        if (first != null)
            GD.Print($"    first miss: {first}");
        Check(misses == 0, $"{misses} rays met nothing while streaming; the first at {first}");
    }
}
