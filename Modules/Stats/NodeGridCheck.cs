using Godot;
using System;
using GameBase.Nodes;

namespace GameBase.Stats;

/// <summary>
/// Checks that a grid keeps the promises <see cref="INodeGrid"/> makes, whatever
/// grid it is.
///
/// The mesher is written against the interface, so a grid that answers its
/// questions inconsistently produces geometry that is wrong in ways no amount
/// of staring at the mesher will explain. The contract worth testing is small:
///
///   - a wall's two corners are shared with the node on the other side, so
///     neighbours meet exactly and no gap opens between them;
///   - corner order and wall order agree, so wall `w` really does span corner
///     `w` to corner `w + 1`;
///   - the outer face is further from the centre than the inner one, so a node
///     is not built inside out.
///
/// Run against both grids, because an abstraction only holds if every
/// implementation holds.
/// </summary>
public partial class NodeGridCheck : Node
{
    [Export] public float Radius { get; set; } = 120f;
    [Export] public float NodeSize { get; set; } = 1f;
    [Export] public bool QuitWhenDone { get; set; } = true;

    public override void _Ready()
    {
        GD.Print("=== NODE GRID CONTRACT CHECK ===");

        Check("quad sphere", new QuadSphereGrid(Radius, NodeSize, Vector3.Zero));
        Check("icosphere", new IcoSphereGrid(Radius, NodeSize, Vector3.Zero));

        GD.Print("=== END NODE GRID CONTRACT CHECK ===");

        if (QuitWhenDone)
            GetTree().Quit(0);
    }

    private static void Check(string label, INodeGrid grid)
    {
        GD.Print($"-- {label} --");
        GD.Print($"   surface {grid.SurfaceRadius}  shells {grid.ShellCount}"
            + $"  max walls {grid.MaxWalls}");

        Span<Vector3> top = stackalloc Vector3[grid.MaxWalls];
        Span<Vector3> bottom = stackalloc Vector3[grid.MaxWalls];
        Span<Vector3> theirs = stackalloc Vector3[grid.MaxWalls];

        int nodes = 0, walls = 0;
        int inverted = 0, mismatchedCount = 0, unsharedWalls = 0;
        float worstShare = 0f;

        foreach (Vector3I cell in Sample(grid))
        {
            int count = grid.TopCorners(cell, top);
            int lower = grid.BottomCorners(cell, bottom);

            nodes++;

            if (count != lower || count != grid.WallCount(cell))
            {
                mismatchedCount++;
                continue;
            }

            // A node must not be built inside out: its outer face is further
            // from the planet's centre than its inner one.
            for (int c = 0; c < count; c++)
            {
                if (top[c].DistanceTo(grid.Origin) <= bottom[c].DistanceTo(grid.Origin))
                {
                    inverted++;
                    break;
                }
            }

            // Every wall is shared with the node behind it. The two nodes must
            // put that wall in the same place, or the world has a seam.
            for (int w = 0; w < count; w++)
            {
                if (!grid.WallNeighbour(cell, w, out Vector3I other))
                    continue;

                walls++;

                int theirCount = grid.TopCorners(other, theirs);
                if (theirCount == 0)
                    continue;

                Vector3 a = top[w];
                Vector3 b = top[(w + 1) % count];

                // Both of this wall's corners should appear among the
                // neighbour's corners, since they are the same two points.
                float worst = Mathf.Max(
                    Nearest(a, theirs, theirCount),
                    Nearest(b, theirs, theirCount));

                if (worst > grid.NodeSize * 0.05f)
                    unsharedWalls++;

                if (worst > worstShare)
                    worstShare = worst;
            }
        }

        GD.Print($"   sampled {nodes} nodes, {walls} walls");
        GD.Print($"   corner/wall counts disagreeing: {mismatchedCount}");
        GD.Print($"   nodes built inside out:         {inverted}");
        GD.Print($"   walls not shared with neighbour:{unsharedWalls}");
        GD.Print($"   worst corner separation:        {worstShare:F5}");
    }

    /// <summary>Distance from a point to the nearest of a set.</summary>
    private static float Nearest(Vector3 point, ReadOnlySpan<Vector3> among, int count)
    {
        float best = float.MaxValue;

        for (int i = 0; i < count; i++)
            best = Mathf.Min(best, point.DistanceTo(among[i]));

        return best;
    }

    /// <summary>
    /// A spread of nodes over the surface, found by walking rather than by
    /// enumerating addresses -- the two grids number their cells completely
    /// differently, and the point here is to test them the same way.
    /// </summary>
    private static System.Collections.Generic.List<Vector3I> Sample(INodeGrid grid)
    {
        var found = new System.Collections.Generic.List<Vector3I>();
        var seen = new System.Collections.Generic.HashSet<Vector3I>();
        var queue = new System.Collections.Generic.Queue<Vector3I>();

        // Start from a handful of directions so the walk covers more than one
        // face, and reaches seams from both sides.
        Vector3[] seeds =
        {
            Vector3.Up, Vector3.Down, Vector3.Left,
            Vector3.Right, Vector3.Forward, Vector3.Back,
            new Vector3(1f, 1f, 1f).Normalized(),
            new Vector3(-1f, 1f, 1f).Normalized(),
        };

        foreach (Vector3 seed in seeds)
        {
            Vector3I at = grid.Canonical(
                grid.CellAt(grid.Origin + seed * (grid.SurfaceRadius - grid.NodeSize * 0.5f)));

            if (grid.Contains(at) && seen.Add(at))
                queue.Enqueue(at);
        }

        // Breadth-first over the walls, so the sample is a connected patch
        // around each seed rather than a scatter of addresses that may not
        // exist.
        while (queue.Count > 0 && found.Count < 4000)
        {
            Vector3I cell = queue.Dequeue();
            found.Add(cell);

            int count = grid.WallCount(cell);

            for (int w = 0; w < count; w++)
            {
                if (!grid.WallNeighbour(cell, w, out Vector3I next))
                    continue;

                next = grid.Canonical(next);

                if (grid.Contains(next) && seen.Add(next))
                    queue.Enqueue(next);
            }
        }

        return found;
    }
}
