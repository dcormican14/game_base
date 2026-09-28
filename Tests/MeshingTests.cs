using Godot;
using System.Linq;
using GameBase.Nodes;

namespace GameBase.Tests;

/// <summary>What the two meshers draw.</summary>
public sealed class MeshingTests : TestSuite
{
    private const float Surface = 10.3f;

    [Test]
    public void FlatSandMeshesFlat()
    {
        NodeWorld world = FlatBed.Build(Root, Surface);

        var sand = DrawnGeometry.Triangles(world)
            .Where(t => !t.Raw && DrawnGeometry.Interior(t.Centre))
            .ToList();

        Check(sand.Count > 100, $"only {sand.Count} sand triangles drawn");

        foreach (var triangle in sand)
        {
            Near(triangle.A.Y, Surface, 0.03f, "sand vertex height");
            Check(triangle.NormalA.Y > 0.99f, $"sand normal {triangle.NormalA} is not up");
            Check(triangle.Facing.Y > 0f, "a sand triangle is wound to face down");
        }
    }

    [Test]
    public void BuriedRockDrawsNothing()
    {
        NodeWorld world = FlatBed.Build(Root, Surface);

        int rock = DrawnGeometry.Triangles(world).Count(t => t.Raw && DrawnGeometry.Interior(t.Centre));
        Equal(rock, 0, "rock triangles under a full bed of sand");
    }

    /// <summary>Where the shell lies on the rock, deep under the surface, neither draws anything.</summary>
    [Test]
    public void BuriedShellDrawsNothing()
    {
        NodeWorld world = FlatBed.Build(Root, Surface);

        int buried = DrawnGeometry.Triangles(world)
            .Count(t => !t.Raw && DrawnGeometry.Interior(t.Centre) && t.Centre.Y < Surface - 1f);
        Equal(buried, 0, "particle triangles under the surface of an untouched bed");
    }

    /// <summary>
    /// Mining the rock out from under the particle shell leaves the shell where
    /// it was, rounded underneath: it does not spread down the cavity's walls.
    /// </summary>
    [Test]
    public void UnderminedShellStaysPut()
    {
        NodeWorld world = FlatBed.Build(Root, Surface);
        const int Half = 3;

        for (int x = -Half; x <= Half; x++)
        for (int y = -3; y <= 2; y++)
        for (int z = -Half; z <= Half; z++)
        {
            var cell = new Vector3I(x, y, z);
            if (world.TypeAt(cell) is RawNode)
                world.ClearNode(cell);
        }

        world.MeshAllNow();

        var shell = DrawnGeometry.Triangles(world)
            .Where(t => !t.Raw && Mathf.Abs(t.Centre.X) < 10f && Mathf.Abs(t.Centre.Z) < 10f)
            .ToList();

        Check(shell.Any(t => t.Centre.Y < Surface - 3f), "the cavity shows no underside of the shell");

        float lowest = shell.Min(t => Mathf.Min(t.A.Y, Mathf.Min(t.B.Y, t.C.Y)));
        Check(lowest > 1.5f, $"the shell reaches down to {lowest:0.00}, into the cavity below it");
    }

    [Test]
    public void LoneRockIsAClosedSolid()
    {
        var grid = new VoronoiGrid(2f);
        var world = Add(new NodeWorld());
        world.Configure(grid);

        var materials = new byte[NodeChunkStore.ChunkVolume];
        var fills = new byte[NodeChunkStore.ChunkVolume];
        System.Array.Fill(materials, NodeChunkStore.Air);

        // Every chunk a lone node's faces could border must be loaded, or the
        // unknown neighbours would cover it.
        for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
        for (int z = -1; z <= 1; z++)
            world.InstallChunk(new Vector3I(x, y, z), (byte[])materials.Clone(), (byte[])fills.Clone());

        var cell = new Vector3I(5, 6, 7);
        Check(world.SetNode(cell, NodeTypes.Stone), "placing a node changed nothing");
        world.MeshAllNow();

        var triangles = DrawnGeometry.Triangles(world);
        Check(triangles.Count >= 20, $"a lone node drew only {triangles.Count} triangles");

        Vector3 site = world.Grid.SiteOf(cell);
        Vector3 closure = Vector3.Zero;

        foreach (var triangle in triangles)
        {
            Check(triangle.Raw, "a lone rock drew particle geometry");
            Check(triangle.Facing.Dot(triangle.Centre - site) > 0f, "a rock face is wound inward");
            closure += triangle.Facing;
        }

        // Area-weighted normals of a closed solid cancel out.
        Check(closure.Length() < 0.01f, $"the node is not closed (normals sum to {closure})");
    }

    /// <summary>
    /// Where sand meets rock the two surfaces are built by different rules, and
    /// any seam between them is a window straight through the planet. Every ray
    /// into a dug pit must stop on something -- tried on pits of two shapes, so
    /// the seams fall in different places.
    /// </summary>
    [Test]
    public async System.Threading.Tasks.Task PitIsWatertight()
    {
        foreach ((float radiusSquared, int seed) in new[] { (12f, 3), (6f, 17) })
        {
            NodeWorld world = FlatBed.Build(Root, Surface);

            for (int x = -4; x <= 4; x++)
            for (int y = -2; y <= 7; y++)
            for (int z = -4; z <= 4; z++)
            {
                var cell = new Vector3I(x, y, z);
                if (x * x + z * z <= radiusSquared && world.TypeAt(cell) is ParticleNode)
                    world.ClearNode(cell);
            }

            world.MeshAllNow();
            await PhysicsFrames();

            var random = new System.Random(seed);
            int escaped = 0;
            const int Rays = 1500;

            for (int n = 0; n < Rays; n++)
            {
                var from = new Vector3(random.NextSingle() * 8f - 4f, Surface + 4f, random.NextSingle() * 8f - 4f);
                var aim = new Vector3(random.NextSingle() - 0.5f, -1f, random.NextSingle() - 0.5f).Normalized();

                if (!world.Raycast(from, from + aim * 40f, out _))
                    escaped++;
            }

            Equal(escaped, 0, $"rays of {Rays} that passed through pit {seed}");
            world.Free();
        }
    }

    [Test]
    public void DiggingSandExposesRock()
    {
        NodeWorld world = FlatBed.Build(Root, Surface);
        Check(world.TypeAt(world.CellAt(new Vector3(0, -2, 0))) is RawNode, "no rock at the test spot");

        // Empty a column of sand down to the rock.
        for (int x = -2; x <= 2; x++)
        for (int y = -3; y <= 7; y++)
        for (int z = -2; z <= 2; z++)
        {
            var cell = new Vector3I(x, y, z);
            if (world.TypeAt(cell) is ParticleNode)
                world.ClearNode(cell);
        }

        world.MeshAllNow();

        int rock = DrawnGeometry.Triangles(world).Count(t => t.Raw);
        Check(rock > 0, "rock at the bottom of a dug pit is not drawn");
    }
}
