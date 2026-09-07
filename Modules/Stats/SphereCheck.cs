using Godot;
using System;
using System.Collections.Generic;
using GameBase.Nodes;

namespace GameBase.Stats;

/// <summary>Temporary: is the cubed-sphere coordinate system sound?</summary>
public partial class SphereCheck : Node
{
    public override void _Ready()
    {
        const float R = 800f, N = 1f;
        var origin = Vector3.Zero;
        var rng = new Random(7);

        GD.Print($"[cs] faceResolution at surface = {CubedSphere.FaceResolution(R, N)}");

        // 1. ROUND TRIP: a cell's centre must map back to that same cell.
        int trips = 0, bad = 0;
        for (int shell = 0; shell < 700; shell += 37)
        {
            int res = CubedSphere.ResolutionAt(shell, R, N);
            for (int t = 0; t < 400; t++)
            {
                int face = rng.Next(6);
                int u = rng.Next(res);
                int v = rng.Next(res);

                Vector3 c = CubedSphere.CentreOf(face, u, v, shell, R, N, origin);
                CubedSphere.CellAt(c, R, N, origin,
                    out int f2, out int u2, out int v2, out int s2);

                trips++;
                if (f2 != face || u2 != u || v2 != v || s2 != shell)
                {
                    bad++;
                    if (bad <= 4)
                        GD.Print($"[cs] ROUNDTRIP ({face},{u},{v},{shell}) -> "
                               + $"({f2},{u2},{v2},{s2})  res={res}");
                }
            }
        }
        GD.Print($"[cs] round trip: {bad} failures of {trips}");

        // 2. UNIFORMITY: how much do neighbouring cell centres vary in spacing?
        foreach (int shell in new[] { 0, 100, 400, 700, 780 })
        {
            int res = CubedSphere.ResolutionAt(shell, R, N);
            float lo = float.MaxValue, hi = 0f;
            for (int t = 0; t < 4000; t++)
            {
                int face = rng.Next(6);
                int u = rng.Next(res - 1);
                int v = rng.Next(res - 1);

                // AXIS neighbours only. Diagonal neighbours are sqrt(2) apart
                // on any square grid, which is not non-uniformity.
                Vector3 a = CubedSphere.CentreOf(face, u, v, shell, R, N, origin);
                Vector3 b = CubedSphere.CentreOf(face, u + 1, v, shell, R, N, origin);
                Vector3 c2 = CubedSphere.CentreOf(face, u, v + 1, shell, R, N, origin);
                float d = a.DistanceTo(b);
                float d2 = a.DistanceTo(c2);
                lo = Mathf.Min(lo, Mathf.Min(d, d2));
                hi = Mathf.Max(hi, Mathf.Max(d, d2));
            }
            GD.Print($"[cs] shell {shell,4} res={res,5} radius={CubedSphere.RadiusOf(shell, R, N),6:F0} "
                   + $"neighbour spacing {lo:F3}..{hi:F3} (ratio {hi / Mathf.Max(lo, 0.0001f):F2})");
        }

        // 3. THE SURFACE IS LEVEL: every cell of shell 0 at one radius.
        {
            int res = CubedSphere.ResolutionAt(0, R, N);
            float lo = float.MaxValue, hi = 0f;
            for (int t = 0; t < 20000; t++)
            {
                int face = rng.Next(6);
                Vector3 c = CubedSphere.CentreOf(face, rng.Next(res), rng.Next(res), 0, R, N, origin);
                float d = c.Length();
                lo = Mathf.Min(lo, d); hi = Mathf.Max(hi, d);
            }
            GD.Print($"[cs] surface shell radius {lo:F4}..{hi:F4} (spread {hi - lo:F4} nodes)");
        }

        // 4. NO GAPS AT FACE SEAMS: a point just either side of a seam must
        // land in adjacent cells of the two faces, not in a hole.
        {
            int seamBad = 0;
            for (int t = 0; t < 5000; t++)
            {
                // A direction very near the +Y / +X seam.
                float a = (float)(rng.NextDouble() * 2 - 1);
                var n = new Vector3(1f, 1f, a).Normalized();
                Vector3 p = n * (R - 0.5f);

                CubedSphere.CellAt(p, R, N, origin,
                    out int f, out int u, out int v, out int s);

                Vector3 back = CubedSphere.CentreOf(f, u, v, s, R, N, origin);
                if (back.DistanceTo(p) > 2f) seamBad++;
            }
            GD.Print($"[cs] seam: {seamBad} of 5000 points land further than 2 nodes "
                   + "from their cell centre");
        }

        GetTree().Quit();
    }
}
