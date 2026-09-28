using Godot;
using System;
using GameBase.Nodes;

namespace GameBase.Planets;

/// <summary>
/// A flat, featureless planet: a ball of raw nodes (stone) all the way
/// through, under a shell of particle nodes (sand) whose surface is a perfect
/// sphere.
///
/// The two forms are laid down differently, each in the way its form works:
///   - RAW NODES are every node whose SITE lies inside <see cref="RockRadius"/>.
///     The top of the rock is therefore as irregular as the Voronoi cells are,
///     which is what digging down through the shell reveals.
///   - PARTICLE NODES fill the rest up to <see cref="Radius"/>. Each stores its
///     lattice point's signed distance to that sphere (see
///     <see cref="NodeFill"/>), so the shell's surface lies exactly on it,
///     smooth, rather than stepping from node to node.
///
/// Which form a node takes is decided node by node, in one place
/// (<see cref="Classify"/>): pockets of particle nodes inside the planet would
/// be a rule added there, and every mesher and tool would take them as they
/// take the shell.
///
/// Pure and thread-safe, as <see cref="IChunkGenerator"/> requires.
/// </summary>
public sealed class PlanetGenerator : IChunkGenerator
{
    public PlanetGenerator(VoronoiGrid grid, float radius, float particleDepth)
    {
        Grid = grid;
        Radius = radius;
        ParticleDepth = Mathf.Max(0f, particleDepth);
    }

    public VoronoiGrid Grid { get; }

    /// <summary>Distance from the centre to the particle surface.</summary>
    public float Radius { get; }

    /// <summary>How thick the shell of particle nodes is, in world units.</summary>
    public float ParticleDepth { get; }

    /// <summary>Sites inside this radius are rock.</summary>
    public float RockRadius => Radius - ParticleDepth;

    private static readonly byte Stone = (byte)NodeMaterial.Stone;
    private static readonly byte Sand = (byte)NodeMaterial.Sand;

    public Vector3 DownAt(Vector3 localPoint) =>
        localPoint.LengthSquared() > 0.0001f ? -localPoint.Normalized() : Vector3.Down;

    /// <summary>
    /// Particle cells reach a node past the surface (they hold the negative side of
    /// the distance), and a site can sit half a cell off its lattice point.
    /// </summary>
    private float Reach => Radius + Grid.NodeSize * 2f;

    public void Generate(Vector3I chunk, byte[] materials, byte[] fills)
    {
        ChunkDistances(chunk, out double near, out double far);

        // Wholly sky, or wholly buried in rock: no need to look at a cell.
        // Sky is still generated, as air: anything built up into it has to
        // have somewhere to go.
        if (near > Reach)
        {
            Array.Fill(materials, NodeChunkStore.Air);
            Array.Fill(fills, NodeFill.Empty);
            return;
        }

        if (far < RockRadius - Grid.NodeSize)
        {
            Array.Fill(materials, Stone);
            Array.Fill(fills, NodeFill.Full);
            return;
        }

        const int Size = NodeChunkStore.ChunkSize;
        Vector3I origin = NodeChunkStore.OriginOf(chunk);

        for (int x = 0; x < Size; x++)
        for (int y = 0; y < Size; y++)
        for (int z = 0; z < Size; z++)
        {
            var cell = new Vector3I(origin.X + x, origin.Y + y, origin.Z + z);
            int index = NodeChunkStore.LocalIndex(x, y, z);

            Classify(cell, out materials[index], out fills[index]);
        }
    }

    /// <summary>What one cell of the untouched planet holds.</summary>
    public void Classify(Vector3I cell, out byte material, out byte fill)
    {
        if (Length(Grid.SiteOf(cell)) < RockRadius)
        {
            material = Stone;
            fill = NodeFill.Full;
            return;
        }

        float level = (float)((Radius - Length(Grid.LatticePoint(cell))) / Grid.NodeSize);
        fill = NodeFill.FromLevel(level);
        material = fill == NodeFill.Empty ? NodeChunkStore.Air : Sand;
    }

    /// <summary>Distances in double precision: a planet's surface is thousands of units out.</summary>
    private static double Length(Vector3 v) =>
        Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y + (double)v.Z * v.Z);

    /// <summary>The nearest and furthest a chunk's cells (with jitter slack) are from the centre.</summary>
    private void ChunkDistances(Vector3I chunk, out double near, out double far)
    {
        float node = Grid.NodeSize;
        float slack = node;
        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        const int Last = NodeChunkStore.ChunkSize - 1;

        double nearSq = 0, farSq = 0;

        for (int axis = 0; axis < 3; axis++)
        {
            double low = origin[axis] * (double)node - slack;
            double high = (origin[axis] + Last) * (double)node + slack;

            double nearest = low > 0 ? low : high < 0 ? high : 0;
            double furthest = Math.Max(Math.Abs(low), Math.Abs(high));

            nearSq += nearest * nearest;
            farSq += furthest * furthest;
        }

        near = Math.Sqrt(nearSq);
        far = Math.Sqrt(farSq);
    }
}
