using Godot;
using System;
using GameBase.Nodes;
using GameBase.Terrain;

namespace GameBase.Planets;

/// <summary>
/// What a planet is made of: raw nodes (stone) for its body, under a skin of
/// particle nodes (sand) that follows whatever shape its terrain gives it.
///
/// The SHAPE is the terrain's business (<see cref="ITerrainShape"/>); this
/// decides the material, node by node, in one place (<see cref="Classify"/>):
///   - RAW NODES are every node whose SITE lies deeper below the surface than
///     the sand there. Raised ground is rock all the way through, and the top
///     of the rock is as irregular as the Voronoi cells are -- which is what
///     digging down through the sand reveals.
///   - PARTICLE NODES fill the rest up to the surface. Each stores its lattice
///     point's signed distance to it (see <see cref="NodeFill"/>), so the
///     sand's surface lies exactly on the terrain, smooth, rather than
///     stepping from node to node.
///   - SAND IS THIN ON STEEP GROUND. It lies its full depth on gentle ground,
///     thins between <see cref="SandRules.FullSlope"/> and
///     <see cref="SandRules.NoneSlope"/> -- about the slope sand stands at --
///     and cliffs, crags and the undersides of overhangs are bare rock.
///
/// Distances come from the shape sampled every other node and interpolated
/// (<see cref="DistanceBlock"/>), so a chunk costs a few thousand lookups of the
/// shape rather than tens of thousands, and every chunk -- and the far
/// terrain's first ring -- reads the same samples.
///
/// Pure and thread-safe, as <see cref="IChunkGenerator"/> requires.
/// </summary>
public sealed class PlanetGenerator : IChunkGenerator
{
    /// <summary>A plain round planet: sand exactly at the radius over rock.</summary>
    public PlanetGenerator(VoronoiGrid grid, float radius, float particleDepth)
        : this(grid, new FlatTerrain(radius), SandRules.Uniform(particleDepth))
    {
    }

    public PlanetGenerator(VoronoiGrid grid, ITerrainShape shape, SandRules sand)
    {
        Grid = grid;
        Shape = shape;
        Sand = sand;
    }

    public VoronoiGrid Grid { get; }

    public ITerrainShape Shape { get; }

    public SandRules Sand { get; }

    /// <summary>World units between the shape's samples: every other node.</summary>
    public double SampleSpacing => Grid.NodeSize * 2.0;

    private static readonly byte Stone = (byte)NodeMaterial.Stone;
    private static readonly byte SandByte = (byte)NodeMaterial.Sand;

    public Vector3 DownAt(Vector3 localPoint) =>
        localPoint.LengthSquared() > 0.0001f ? -localPoint.Normalized() : Vector3.Down;

    [ThreadStatic] private static DistanceBlock _block;

    public void Generate(Vector3I chunk, byte[] materials, byte[] fills)
    {
        switch (Uniform(chunk))
        {
            case -1:
                Array.Fill(materials, NodeChunkStore.Air);
                Array.Fill(fills, NodeFill.Empty);
                return;

            case 1:
                Array.Fill(materials, Stone);
                Array.Fill(fills, NodeFill.Full);
                return;
        }

        const int Size = NodeChunkStore.ChunkSize;
        Vector3I origin = NodeChunkStore.OriginOf(chunk);

        DistanceBlock block = _block ??= new DistanceBlock();
        Bounds(origin, origin + Vector3I.One * (Size - 1), out Vector3 min, out Vector3 max);
        block.FillBox(Shape, SampleSpacing, min, max);

        for (int x = 0; x < Size; x++)
        for (int y = 0; y < Size; y++)
        for (int z = 0; z < Size; z++)
        {
            var cell = new Vector3I(origin.X + x, origin.Y + y, origin.Z + z);
            int index = NodeChunkStore.LocalIndex(x, y, z);

            Classify(block, cell, out materials[index], out fills[index]);
        }
    }

    /// <summary>What one cell of the untouched planet holds.</summary>
    public void Classify(Vector3I cell, out byte material, out byte fill)
    {
        var block = new DistanceBlock();
        Bounds(cell, cell, out Vector3 min, out Vector3 max);
        block.FillBox(Shape, SampleSpacing, min, max);

        Classify(block, cell, out material, out fill);
    }

    private void Classify(DistanceBlock block, Vector3I cell, out byte material, out byte fill)
    {
        Vector3 site = Grid.SiteOf(cell);
        float deep = block.Normalized(site, out Vector3 gradient);

        // Below the deepest sand anywhere: rock, without asking the slope.
        if (deep > Sand.Depth)
        {
            material = Stone;
            fill = NodeFill.Full;
            return;
        }

        float depth = Sand.DepthOn(-gradient, site);

        if (deep > depth)
        {
            material = Stone;
            fill = NodeFill.Full;
            return;
        }

        // Bare rock here: whatever is outside it is open air, with no sand to
        // hold a surface.
        if (depth <= 0f)
        {
            material = NodeChunkStore.Air;
            fill = NodeFill.Empty;
            return;
        }

        float level = block.Normalized(Grid.LatticePoint(cell), out _) / Grid.NodeSize;
        fill = NodeFill.FromLevel(level);
        material = fill == NodeFill.Empty ? NodeChunkStore.Air : SandByte;
    }

    /// <summary>
    /// The box a run of cells reads the shape over: every lattice point and
    /// every site, which strays up to half a node from its lattice point.
    /// </summary>
    private void Bounds(Vector3I first, Vector3I last, out Vector3 min, out Vector3 max)
    {
        float reach = Grid.NodeSize * 0.6f;
        min = Grid.LatticePoint(first) - Vector3.One * reach;
        max = Grid.LatticePoint(last) + Vector3.One * reach;
    }

    // --------------------------------------------------------------- culling

    /// <summary>
    /// Is a whole chunk sky, or buried rock? First by radius alone -- the
    /// shape's <see cref="ITerrainShape.Top"/> and <see cref="ITerrainShape.Bottom"/>
    /// -- then by a coarse look at the shape itself.
    /// </summary>
    /// <returns>-1 all air, +1 all rock, 0 anything else.</returns>
    public int Uniform(Vector3I chunk)
    {
        ChunkDistances(chunk, out double near, out double far);

        // Particle cells reach a node and a half past the surface (they hold the
        // negative side of the distance); sites stray half a node.
        double reach = Grid.NodeSize * 2.0;

        if (near > Shape.Top + reach)
            return -1;

        if (far < Shape.Bottom - reach)
            return 1;

        const int Size = NodeChunkStore.ChunkSize;
        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        Bounds(origin, origin + Vector3I.One * (Size - 1), out Vector3 min, out Vector3 max);

        // Sampled at the lattice the cells are read from, so the answer holds
        // for the interpolated field too, not only the shape between samples.
        min -= Vector3.One * (float)SampleSpacing;
        max += Vector3.One * (float)SampleSpacing;

        // Air must be clear of every particle cell's reach; rock must be
        // deeper than any sand.
        return DistanceBlock.Uniform(Shape, min, max,
            NodeFill.Range * Grid.NodeSize + reach, Sand.Depth + reach);
    }

    /// <summary>Distances in double precision: a planet's surface is thousands of units out.</summary>
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
