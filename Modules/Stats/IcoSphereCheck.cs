using Godot;
using System;
using GameBase.Nodes;

namespace GameBase.Stats;

/// <summary>
/// Measures the even-node grid against what it is supposed to deliver.
///
/// The two claims this world is built on are testable numbers, not opinions, so
/// they are tested rather than eyeballed:
///
///  1. SITES ARE EVENLY DISTRIBUTED. Reported as the spread of centre-to-centre
///     spacing. The cubed sphere it replaces measures 1.33; a Fibonacci
///     lattice, which is about as even as a sphere gets but has no usable
///     structure, measures 1.13.
///
///  2. FACES ARE BISECTORS. Every wall should be equidistant from the two nodes
///     it separates. Reported as the worst error over many sampled walls, which
///     should be at floating-point noise.
///
/// Also checks the things that make the grid USABLE rather than merely even:
/// that a position round-trips back to the node it came from, and that
/// neighbours are mutual.
/// </summary>
public partial class IcoSphereCheck : Node
{
    [Export] public float Radius { get; set; } = 120f;
    [Export] public float NodeSize { get; set; } = 1f;

    /// <summary>Quit when done, so the scene can run headless in a check.</summary>
    [Export] public bool QuitWhenDone { get; set; } = true;

    public override void _Ready()
    {
        var grid = new IcoSphereGrid(Radius, NodeSize, Vector3.Zero);
        IcoSphereTables tables = grid.Tables;

        GD.Print("=== ICOSPHERE GRID CHECK ===");
        GD.Print($"surface radius {tables.SurfaceRadius}  node size {tables.NodeSize}");
        GD.Print($"surface level  {tables.SurfaceLevel}  shells {tables.ShellCount}");
        GD.Print($"sites at surface {tables.Sites(0)}");

        Banding(tables);
        Spacing(grid);
        Bisectors(grid);
        RoundTrip(grid);
        Mutual(grid);

        GD.Print("=== END ICOSPHERE GRID CHECK ===");

        if (QuitWhenDone)
            GetTree().Quit(0);
    }

    /// <summary>
    /// Cell width from the surface to the core.
    ///
    /// The banding exists to hold this near constant: halving the radius
    /// quarters the area, and dropping a subdivision level quarters the site
    /// count, so the two should cancel.
    /// </summary>
    private static void Banding(IcoSphereTables tables)
    {
        GD.Print("-- shells: how cell width holds toward the core --");
        GD.Print($"  {"shell",6} {"radius",9} {"level",6} {"sites",9} {"width",9}");

        int lastLevel = -1;
        for (int shell = 0; shell < tables.ShellCount; shell++)
        {
            int level = tables.Level(shell);
            if (level == lastLevel)
                continue;

            lastLevel = level;

            float radius = tables.Radius(shell);
            int sites = tables.Sites(shell);
            float width = Mathf.Sqrt(4f * Mathf.Pi * radius * radius / sites);

            GD.Print($"  {shell,6} {radius,9:F2} {level,6} {sites,9} {width,9:F3}");
        }
    }

    /// <summary>
    /// Spread of centre-to-centre spacing over the surface shell.
    ///
    /// Every lateral neighbour of every sampled node, so pentagons are included
    /// rather than sampled around.
    /// </summary>
    private static void Spacing(IcoSphereGrid grid)
    {
        float min = float.MaxValue, max = 0f, total = 0f;
        int count = 0, pentagons = 0;

        int divisions = grid.Tables.Divisions(0);
        int step = Mathf.Max(1, divisions / 24);

        for (int face = 0; face < IcoSphere.FaceCount; face++)
        {
            for (int i = 0; i <= divisions; i += step)
            {
                for (int j = 0; i + j <= divisions; j += step)
                {
                    // The face's own corners are the pentagon sites, and a
                    // stepped sweep walks straight past them unless they are
                    // asked for by name -- which is how an earlier run of this
                    // check reported zero pentagons on a grid that has twelve.
                    SampleSpacing(grid, grid.Pack(face, i, j, 0),
                        ref min, ref max, ref total, ref count, ref pentagons);
                }
            }

            for (int corner = 0; corner < 3; corner++)
            {
                Vector3I at = corner switch
                {
                    0 => grid.Pack(face, 0, 0, 0),
                    1 => grid.Pack(face, divisions, 0, 0),
                    _ => grid.Pack(face, 0, divisions, 0),
                };

                SampleSpacing(grid, at,
                    ref min, ref max, ref total, ref count, ref pentagons);
            }
        }

        GD.Print("-- 1. even distribution (surface shell) --");
        GD.Print($"  samples {count}, of which {pentagons} pentagon sites");
        GD.Print($"  spacing {min:F4} .. {max:F4}  mean {total / Mathf.Max(1, count):F4}");
        GD.Print($"  max/min {max / Mathf.Max(0.0001f, min):F4}"
            + "   (cubed sphere 1.333, fibonacci 1.128)");
    }

    /// <summary>How many oddities have been printed, so a broken grid reports a
    /// handful of examples rather than tens of thousands of lines.</summary>
    private static int Reported;

    /// <summary>Adds one site's lateral spacings to the running spread.</summary>
    private static void SampleSpacing(IcoSphereGrid grid, Vector3I cell,
        ref float min, ref float max, ref float total, ref int count, ref int pentagons)
    {
        cell = grid.Canonical(cell);

        Vector3 centre = grid.CentreOf(cell);
        int walls = 0;

        for (int n = 0; n < IcoSphereGrid.MaxLateral; n++)
        {
            if (!grid.Neighbour(cell, n, 0, out Vector3I at))
                continue;

            walls++;

            float d = centre.DistanceTo(grid.CentreOf(at));

            // A neighbour twice the expected distance away is not a spacing
            // problem, it is a WRONG neighbour -- so it is reported rather than
            // averaged into the spread, where it would look like distortion.
            if (d > grid.NodeSize * 1.75f && Reported < 6)
            {
                grid.Unpack(cell, out int cf, out int ci, out int cj, out _);
                grid.Unpack(at, out int af, out int ai, out int aj, out _);
                GD.Print($"    FAR step {n}: ({cf},{ci},{cj}) -> ({af},{ai},{aj})"
                    + $"  d={d:F4}");
                Reported++;
            }

            if (d < min) min = d;
            if (d > max) max = d;
            total += d;
            count++;
        }

        if (walls == 5)
            pentagons++;
    }

    /// <summary>
    /// Is every wall the perpendicular bisector of the two nodes it separates?
    ///
    /// Measured on the wall's own corners: each corner should be exactly as far
    /// from this node's site as from the neighbour's. Any error here means two
    /// nodes disagree about where their shared wall is, which is a gap or an
    /// overlap in the world.
    /// </summary>
    private static void Bisectors(IcoSphereGrid grid)
    {
        Span<Vector3> corners = stackalloc Vector3[IcoSphereGrid.MaxLateral];

        float worst = 0f;
        int walls = 0, cells = 0, degenerate = 0;

        int divisions = grid.Tables.Divisions(0);
        int step = Mathf.Max(1, divisions / 16);

        for (int face = 0; face < IcoSphere.FaceCount; face++)
        {
            for (int i = 0; i <= divisions; i += step)
            {
                for (int j = 0; i + j <= divisions; j += step)
                {
                    Vector3I cell = grid.Pack(face, i, j, 0);

                    int count = grid.TopCorners(cell, corners);
                    cells++;

                    if (count < 3)
                    {
                        degenerate++;
                        continue;
                    }

                    Vector3 here = grid.DirectionOf(cell);

                    // Each corner is where three bisector planes meet, so it
                    // must be equidistant from this site and from BOTH the
                    // neighbours that share it. Measured as an angle, since the
                    // sites and the corner all sit on the same sphere.
                    for (int n = 0; n < count; n++)
                    {
                        Vector3 corner = corners[n].Normalized();
                        float mine = corner.Dot(here);

                        // The two neighbours flanking this corner.
                        for (int k = 0; k < 2; k++)
                        {
                            int which = (n + k) % count;

                            if (!grid.Neighbour(cell, which, 0, out Vector3I at))
                                continue;

                            float theirs = corner.Dot(grid.DirectionOf(at));
                            float error = Mathf.Abs(mine - theirs);

                            if (error > worst)
                                worst = error;

                            walls++;
                        }
                    }
                }
            }
        }

        GD.Print("-- 2. walls are perpendicular bisectors --");
        GD.Print($"  {cells} cells sampled, {degenerate} with too few corners");
        GD.Print($"  {walls} corner/neighbour pairs checked");
        GD.Print($"  worst equidistance error = {worst:F6}   (0 = exact bisector)");
    }

    /// <summary>
    /// Does a node's own centre land back in that node?
    ///
    /// The property every lookup in the game depends on -- digging, picking,
    /// collision and streaming all convert a position to a node and must get
    /// the one they started from.
    /// </summary>
    private static void RoundTrip(IcoSphereGrid grid)
    {
        int divisions = grid.Tables.Divisions(0);
        int step = Mathf.Max(1, divisions / 24);
        int total = 0, wrong = 0;

        for (int face = 0; face < IcoSphere.FaceCount; face++)
        {
            for (int i = 0; i <= divisions; i += step)
            {
                for (int j = 0; i + j <= divisions; j += step)
                {
                    Vector3I cell = grid.Pack(face, i, j, 0);
                    Vector3 want = grid.CentreOf(cell);
                    Vector3I back = grid.CellAt(want);

                    total++;

                    // Compared by POSITION, not by address: a site on a face
                    // edge is shared, so two addresses can name the same node
                    // and both are right.
                    float off = grid.CentreOf(back).DistanceTo(want);

                    if (off > grid.NodeSize * 0.5f)
                    {
                        if (wrong < 6)
                        {
                            grid.Unpack(back, out int bf, out int bi, out int bj, out int bs);
                            GD.Print($"    MISS ({face},{i},{j}) -> ({bf},{bi},{bj},{bs})"
                                + $"  off by {off:F4}");
                        }

                        wrong++;
                    }
                }
            }
        }

        GD.Print("-- position round-trips to the same node --");
        GD.Print($"  {total - wrong} / {total} correct"
            + (wrong == 0 ? "" : $"   MISMATCHES: {wrong}"));
    }

    /// <summary>
    /// If A is B's neighbour, is B one of A's?
    ///
    /// A one-way neighbour would make the mesher cull a wall on one side and
    /// draw it on the other, which is exactly the class of bug that shows up as
    /// a hole you can see through.
    /// </summary>
    private static void Mutual(IcoSphereGrid grid)
    {
        int divisions = grid.Tables.Divisions(0);
        int step = Mathf.Max(1, divisions / 16);
        int total = 0, broken = 0;

        for (int face = 0; face < IcoSphere.FaceCount; face++)
        {
            for (int i = 0; i <= divisions; i += step)
            {
                for (int j = 0; i + j <= divisions; j += step)
                {
                    Vector3I cell = grid.Pack(face, i, j, 0);
                    Vector3 centre = grid.CentreOf(cell);

                    for (int n = 0; n < IcoSphereGrid.MaxLateral; n++)
                    {
                        if (!grid.Neighbour(cell, n, 0, out Vector3I at))
                            continue;

                        total++;

                        bool found = false;
                        for (int m = 0; m < IcoSphereGrid.MaxLateral && !found; m++)
                        {
                            if (grid.Neighbour(at, m, 0, out Vector3I back))
                                found = grid.CentreOf(back).DistanceTo(centre)
                                    < grid.NodeSize * 0.5f;
                        }

                        if (!found)
                            broken++;
                    }
                }
            }
        }

        GD.Print("-- neighbours are mutual --");
        GD.Print($"  {total - broken} / {total} mutual"
            + (broken == 0 ? "" : $"   ONE-WAY: {broken}"));
    }
}
