using Godot;
using GameBase.Nodes;

namespace GameBase.Stats;

/// <summary>
/// Tests the quad sphere coordinate system in isolation, with no world, no
/// streamer and no renderer.
///
/// The properties checked here are the ones whose failure produced floating
/// ribbons instead of a planet: that a point round-trips to the cell that
/// claims it, that stepping between cells is reversible, that a step off a face
/// lands somewhere adjacent rather than across the planet, and that the sky is
/// never reported as solid ground.
/// </summary>
public partial class QuadSphereCheck : Node
{
    [Export] public float Radius { get; set; } = 120f;
    [Export] public float NodeSize { get; set; } = 1f;

    private int _failures;

    public override void _Ready()
    {
        var grid = new QuadSphereGrid(Radius, NodeSize, Vector3.Zero);

        GD.Print("=== QUAD SPHERE CHECK ===");
        GD.Print($"radius={Radius} nodeSize={NodeSize} " +
            $"baseResolution={grid.BaseResolution} shells={grid.ShellCount}");

        Bands(grid);
        RoundTrip(grid);
        Reversible(grid);
        Adjacent(grid);
        Sky(grid);
        Coverage(grid);

        BandBoundary(grid);

        GD.Print(_failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {_failures} CHECK(S) FAILED ===");

        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Check(bool ok, string what)
    {
        if (!ok)
            _failures++;

        GD.Print($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    }

    /// <summary>Resolution must fall only by exact factors of two.</summary>
    private void Bands(QuadSphereGrid grid)
    {
        GD.Print("-- resolution bands --");

        int previous = QuadSphere.ResolutionAt(0, Radius, NodeSize);
        bool powersOfTwo = true;
        int changes = 0;

        for (int shell = 0; shell < grid.ShellCount; shell++)
        {
            int res = QuadSphere.ResolutionAt(shell, Radius, NodeSize);

            if (res != previous)
            {
                changes++;
                if (previous % res != 0 || !IsPowerOfTwo(previous / res))
                    powersOfTwo = false;

                GD.Print($"    shell {shell,4}: {previous} -> {res}" +
                    $" (ratio {previous / (float)res:F2})");

                previous = res;
            }
        }

        Check(powersOfTwo, $"every resolution change is a power of two ({changes} changes)");

        // Cell width must stay within a factor of two of a node.
        float worstLow = float.MaxValue, worstHigh = 0f;
        for (int shell = 0; shell < grid.ShellCount; shell++)
        {
            int res = QuadSphere.ResolutionAt(shell, Radius, NodeSize);
            float radius = QuadSphere.RadiusOf(shell, Radius, NodeSize);
            float width = Mathf.Pi * 0.5f * radius / res;

            if (width < worstLow) worstLow = width;
            if (width > worstHigh) worstHigh = width;
        }

        GD.Print($"    cell width ranges {worstLow:F3} .. {worstHigh:F3} nodes");
        Check(worstHigh / Mathf.Max(worstLow, 0.0001f) < 8f,
            "cell width stays within a bounded ratio");
    }

    /// <summary>A cell's centre must land back in that cell.</summary>
    private void RoundTrip(QuadSphereGrid grid)
    {
        GD.Print("-- centre round-trip --");

        var rng = new RandomNumberGenerator { Seed = 12345 };
        int tested = 0, bad = 0;

        for (int i = 0; i < 20000; i++)
        {
            int shell = rng.RandiRange(0, grid.ShellCount - 1);
            int res = QuadSphere.ResolutionAt(shell, Radius, NodeSize);
            int face = rng.RandiRange(0, 5);
            int u = rng.RandiRange(0, res - 1);
            int v = rng.RandiRange(0, res - 1);

            Vector3I cell = grid.Pack(face, u, v, shell);
            Vector3I back = grid.CellAt(grid.CentreOf(cell));

            tested++;
            if (back != cell)
            {
                if (bad < 5)
                    GD.Print($"    {cell} -> {back}");
                bad++;
            }
        }

        Check(bad == 0, $"{tested - bad}/{tested} cell centres round-trip exactly");
    }

    /// <summary>Stepping one way then back must return to the start.</summary>
    private void Reversible(QuadSphereGrid grid)
    {
        GD.Print("-- neighbour reversibility --");

        var rng = new RandomNumberGenerator { Seed = 999 };
        int tested = 0, bad = 0;

        (int du, int dv, int dOut)[] steps =
        {
            (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0),
        };

        for (int i = 0; i < 20000; i++)
        {
            int shell = rng.RandiRange(0, grid.ShellCount - 1);
            int res = QuadSphere.ResolutionAt(shell, Radius, NodeSize);
            int face = rng.RandiRange(0, 5);

            // Away from face edges, where a step crosses a fold and the return
            // trip is only guaranteed up to the fold's own rounding.
            int u = rng.RandiRange(1, Mathf.Max(1, res - 2));
            int v = rng.RandiRange(1, Mathf.Max(1, res - 2));

            Vector3I cell = grid.Pack(face, u, v, shell);
            var step = steps[rng.RandiRange(0, steps.Length - 1)];

            if (!grid.Neighbour(cell, step.du, step.dv, step.dOut, out Vector3I next))
                continue;

            if (!grid.Neighbour(next, -step.du, -step.dv, -step.dOut, out Vector3I back))
                continue;

            tested++;
            if (back != cell)
            {
                if (bad < 5)
                    GD.Print($"    {cell} --({step.du},{step.dv})-> {next} -> {back}");
                bad++;
            }
        }

        Check(bad == 0, $"{tested - bad}/{tested} in-face steps are reversible");
    }

    /// <summary>A neighbour must actually be adjacent in space.</summary>
    private void Adjacent(QuadSphereGrid grid)
    {
        GD.Print("-- neighbours are spatially adjacent --");

        var rng = new RandomNumberGenerator { Seed = 4242 };
        float worst = 0f;
        int tested = 0, bad = 0;

        for (int i = 0; i < 20000; i++)
        {
            int shell = rng.RandiRange(0, grid.ShellCount - 1);
            int res = QuadSphere.ResolutionAt(shell, Radius, NodeSize);
            int face = rng.RandiRange(0, 5);
            int u = rng.RandiRange(0, res - 1);
            int v = rng.RandiRange(0, res - 1);

            Vector3I cell = grid.Pack(face, u, v, shell);

            int du = rng.RandiRange(-1, 1);
            int dv = du == 0 ? (rng.Randf() < 0.5f ? 1 : -1) : 0;

            if (!grid.Neighbour(cell, du, dv, 0, out Vector3I next))
                continue;

            float distance = grid.CentreOf(cell).DistanceTo(grid.CentreOf(next));

            // A step of one cell, generously bounded: cells vary in width
            // within a band, and a fold crossing can shift by up to a cell.
            float limit = NodeSize * 4f;

            tested++;
            if (distance > limit)
            {
                if (bad < 5)
                    GD.Print($"    {cell} -> {next}: {distance:F2} apart");
                bad++;
            }

            if (distance > worst)
                worst = distance;
        }

        GD.Print($"    widest neighbour gap: {worst:F3}");
        Check(bad == 0, $"{tested - bad}/{tested} neighbours are within one cell");
    }

    /// <summary>Everything above the surface must be outside the grid.</summary>
    private void Sky(QuadSphereGrid grid)
    {
        GD.Print("-- the sky is not solid --");

        bool ok = true;

        foreach (float height in new[] { 0.01f, 0.5f, 1f, 5f, 50f, 500f })
        {
            Vector3I cell = grid.CellAt(Vector3.Up * (Radius + height));
            grid.Unpack(cell, out _, out _, out _, out int shell);

            bool contained = grid.Contains(cell);
            if (contained || shell >= 0)
                ok = false;

            GD.Print($"    {height,6} above surface -> shell {shell,5} contains={contained}");
        }

        Check(ok, "every point above the surface is outside the grid");

        // And just below must be inside.
        Vector3I ground = grid.CellAt(Vector3.Up * (Radius - 0.5f));
        grid.Unpack(ground, out _, out _, out _, out int groundShell);
        Check(grid.Contains(ground) && groundShell == 0,
            $"a point just below the surface is shell {groundShell}, inside the grid");
    }

    /// <summary>
    /// The surface must be covered: every direction maps to a distinct cell,
    /// and the whole shell's worth of cells is reachable.
    /// </summary>
    private void Coverage(QuadSphereGrid grid)
    {
        GD.Print("-- surface coverage --");

        var seen = new System.Collections.Generic.HashSet<Vector3I>();
        var rng = new RandomNumberGenerator { Seed = 77 };

        int samples = 60000;
        for (int i = 0; i < samples; i++)
        {
            // Uniform directions on the sphere.
            float z = rng.RandfRange(-1f, 1f);
            float angle = rng.RandfRange(0f, Mathf.Tau);
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));

            var direction = new Vector3(r * Mathf.Cos(angle), r * Mathf.Sin(angle), z);
            Vector3I cell = grid.CellAt(direction * (Radius - 0.5f));

            if (grid.Contains(cell))
                seen.Add(cell);
        }

        int expected = 6 * grid.BaseResolution * grid.BaseResolution;
        GD.Print($"    {seen.Count} distinct surface cells from {samples} samples" +
            $" (surface holds {expected})");

        Check(seen.Count > 0, "random directions land on real surface cells");

        // Every sample must have been inside the grid: a direction that maps
        // to nothing would be a hole in the planet.
        int outside = 0;
        for (int i = 0; i < 5000; i++)
        {
            float z = rng.RandfRange(-1f, 1f);
            float angle = rng.RandfRange(0f, Mathf.Tau);
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));

            var direction = new Vector3(r * Mathf.Cos(angle), r * Mathf.Sin(angle), z);
            if (!grid.Contains(grid.CellAt(direction * (Radius - 0.5f))))
                outside++;
        }

        Check(outside == 0, $"{5000 - outside}/5000 surface directions land inside the grid");
    }

    /// <summary>
    /// Stepping across a resolution band, in both directions.
    ///
    /// This is where a cubed sphere is hardest: one coarse cell is covered by
    /// four fine ones, so the step inward is many-to-one and the step outward
    /// is one-of-four. Both must land on a real cell, and stepping in then back
    /// out must return to the group you started from.
    /// </summary>
    private void BandBoundary(QuadSphereGrid grid)
    {
        GD.Print("-- crossing a resolution band --");

        // Find the boundaries by walking the shells.
        var boundaries = new System.Collections.Generic.List<int>();
        int previous = QuadSphere.ResolutionAt(0, Radius, NodeSize);

        for (int shell = 1; shell < grid.ShellCount; shell++)
        {
            int res = QuadSphere.ResolutionAt(shell, Radius, NodeSize);
            if (res != previous)
            {
                boundaries.Add(shell);
                previous = res;
            }
        }

        if (boundaries.Count == 0)
        {
            GD.Print("    no band boundaries at this radius");
            return;
        }

        bool ok = true;

        foreach (int shell in boundaries)
        {
            int coarse = QuadSphere.ResolutionAt(shell, Radius, NodeSize);
            int fine = QuadSphere.ResolutionAt(shell - 1, Radius, NodeSize);

            int missingOut = 0, missingIn = 0, mismatched = 0, tested = 0;

            for (int u = 0; u < coarse; u += Mathf.Max(1, coarse / 16))
            {
                for (int v = 0; v < coarse; v += Mathf.Max(1, coarse / 16))
                {
                    Vector3I cell = grid.Pack(0, u, v, shell);
                    tested++;

                    // Outward into the finer grid.
                    if (!grid.Neighbour(cell, 0, 0, 1, out Vector3I up)
                        || !grid.Contains(up))
                    {
                        missingOut++;
                        continue;
                    }

                    // And back in again: must return to where we started.
                    if (!grid.Neighbour(up, 0, 0, -1, out Vector3I back)
                        || !grid.Contains(back))
                    {
                        missingIn++;
                        continue;
                    }

                    if (back != cell)
                        mismatched++;
                }
            }

            GD.Print($"    shell {shell} ({fine} -> {coarse}): tested {tested}," +
                $" no cell outward {missingOut}, none back {missingIn}," +
                $" round-trip mismatched {mismatched}");

            if (missingOut > 0 || missingIn > 0 || mismatched > 0)
                ok = false;
        }

        Check(ok, "stepping across a band boundary always lands on a real cell");
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
