using System;
using Godot;
using GameBase.Nodes;
using GameBase.Levels;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Checks the topsoil's spiral pattern by reading the colours off the built
/// mesh, rather than by re-deriving what they ought to be.
///
/// WHY READ THE MESH. The pattern is generated maths, and generated maths is
/// easy to check against itself and hard to check against the eye. Summing the
/// spiral over octaves and squashing the result into a colour range has one
/// failure that a formula review does not catch: the values can pile up at one
/// end, so the planet comes out a single flat tone while every line of the
/// derivation reads correctly. The first version of this pattern did exactly
/// that -- 63% of the surface saturated to fully dark.
///
/// So this measures the SPREAD of the colours actually laid down: how much of
/// the planet is light soil, how much is dark arm, and how much is the
/// in-between that makes it read as a pattern rather than as two-tone paint.
/// </summary>
public partial class SpiralCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public bool QuitWhenDone { get; set; } = true;

    private ChunkStreamer _streamer;
    private int _frames;
    private bool _done;

    public override void _Ready()
    {
        _streamer = NodeSearch.FindByType<ChunkStreamer>(GetTree().CurrentScene);
    }

    public override void _Process(double delta)
    {
        if (_done) return;
        _frames++;
        bool ready = _streamer == null || _streamer.IsReady;
        if (!ready && _frames < WaitFrames) return;
        _done = true;
        Run();
        if (QuitWhenDone) GetTree().Quit(0);
    }

    private void Run()
    {
        GD.Print("=== SPIRAL CHECK ===");

        var cap = NodeSearch.FindByType<TopsoilCap>(GetTree().CurrentScene);
        var mesh = cap?.GetNodeOrNull<MeshInstance3D>("Soil");

        if (mesh?.Mesh == null)
        {
            GD.Print("  [FAIL] no topsoil mesh to read");
            return;
        }

        var arrays = mesh.Mesh.SurfaceGetArrays(0);
        var colors = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
        var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();

        if (colors.Length == 0)
        {
            GD.Print("  [FAIL] the shell has no vertex colours");
            return;
        }

        // HOW FAR ALONG THE LIGHT-TO-DARK RANGE each vertex sits, measured on
        // the green channel: the two tans differ most there relative to noise,
        // and using one channel keeps this independent of overall brightness.
        float light = cap.Soil.G;
        float dark = cap.SoilDark.G;

        var t = new float[colors.Length];

        for (int i = 0; i < colors.Length; i++)
            t[i] = Mathf.Clamp((light - colors[i].G) / Mathf.Max(0.0001f, light - dark), 0f, 1f);

        var sorted = (float[])t.Clone();
        Array.Sort(sorted);

        int n = sorted.Length;
        double mean = 0;
        foreach (float v in t) mean += v;
        mean /= n;

        int lightest = 0, darkest = 0, mid = 0;

        foreach (float v in t)
        {
            if (v < 0.10f) lightest++;
            else if (v > 0.90f) darkest++;
            if (v > 0.20f && v < 0.80f) mid++;
        }

        GD.Print($"  vertices: {n}");
        GD.Print($"  tone (0 = light soil, 1 = dark arm):");
        GD.Print($"    min {sorted[0]:F2}  p10 {sorted[n / 10]:F2}  median "
            + $"{sorted[n / 2]:F2}  p90 {sorted[9 * n / 10]:F2}  max {sorted[n - 1]:F2}");
        GD.Print($"    mean {mean:F2}");
        GD.Print($"    light soil (<0.10): {100f * lightest / n:F1}%");
        GD.Print($"    dark arm   (>0.90): {100f * darkest / n:F1}%");
        GD.Print($"    in between:         {100f * mid / n:F1}%");

        // NEITHER END MAY OWN THE PLANET. This is the specific failure the
        // first pattern had, and the only one that cannot be seen by reading
        // the formula.
        bool spread = lightest < n * 0.55f && darkest < n * 0.55f && mid > n * 0.15f;

        GD.Print(spread
            ? "  [PASS] the pattern spans light soil to dark arm"
            : "  [FAIL] the pattern has collapsed toward one tone");

        // IS IT SMOOTH ON THE GROUND? A pattern that jumps from one vertex to
        // its neighbour reads as speckle, not as a spiral. Measured between
        // vertices that are actually adjacent on the shell.
        var rng = new Random(4242);
        double worst = 0, jumpTotal = 0;
        int pairs = 0;

        for (int i = 0; i < 20000 && pairs < 4000; i++)
        {
            int a = rng.Next(n);

            // Its neighbour along the row: consecutive indices are adjacent on
            // a face, except where a row or a face wraps -- excluded by
            // distance rather than by index arithmetic.
            int b = a + 1;
            if (b >= n) continue;

            float apart = verts[a].DistanceTo(verts[b]);

            // Roughly one quad at this resolution; anything longer is a seam.
            if (apart > 4.5f) continue;

            double jump = Math.Abs(t[a] - t[b]);

            jumpTotal += jump;
            if (jump > worst) worst = jump;
            pairs++;
        }

        if (pairs > 0)
        {
            GD.Print($"  neighbouring vertices compared: {pairs}");
            GD.Print($"    mean step {jumpTotal / pairs:F3}, worst {worst:F3}");

            GD.Print(worst < 0.5
                ? "  [PASS] the pattern varies smoothly across the surface"
                : "  [FAIL] the pattern jumps between neighbours");

            // WHERE the worst ones are, so a failure says something more
            // useful than that one exists.
            var offenders = new System.Collections.Generic.List<(double jump, Vector3 at)>();

            for (int a = 0; a + 1 < n; a++)
            {
                if (verts[a].DistanceTo(verts[a + 1]) > 4.5f) continue;

                double jump = Math.Abs(t[a] - t[a + 1]);
                if (jump > 0.30) offenders.Add((jump, verts[a]));
            }

            offenders.Sort((x, y) => y.jump.CompareTo(x.jump));

            GD.Print($"  steps over 0.30: {offenders.Count} of {n} vertices");

            for (int k = 0; k < Math.Min(6, offenders.Count); k++)
            {
                Vector3 d = offenders[k].at.Normalized();

                GD.Print($"    {offenders[k].jump:F3} at dir "
                    + $"({d.X:F3}, {d.Y:F3}, {d.Z:F3})");
            }
        }

        GD.Print("=== END SPIRAL CHECK ===");
    }
}
