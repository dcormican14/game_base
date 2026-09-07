using Godot;
using System;
using System.Collections.Generic;
using GameBase.Nodes;

namespace GameBase.Stats;

/// <summary>Temporary: does neighbour lookup hold together, seams included?</summary>
public partial class GridCheck : Node
{
    public override void _Ready()
    {
        var grid = new SphereGrid(800f, 1f, Vector3.Zero);
        var rng = new Random(11);
        GD.Print($"[gr] baseResolution={grid.BaseResolution}");

        // 1. PACK / UNPACK round trip.
        int bad = 0;
        for (int t = 0; t < 20000; t++)
        {
            int face = rng.Next(6);
            int shell = rng.Next(0, 700);
            int res = CubedSphere.ResolutionAt(shell, 800f, 1f);
            int u = rng.Next(res), v = rng.Next(res);

            Vector3I packed = grid.Pack(face, u, v, shell);
            grid.Unpack(packed, out int f2, out int u2, out int v2, out int s2);
            if (f2 != face || u2 != u || v2 != v || s2 != shell) bad++;
        }
        GD.Print($"[gr] pack/unpack: {bad} failures of 20000");

        // 2. RECIPROCITY: stepping there and back must return the start.
        // This is what holds a seam together -- if two faces disagree about
        // who is next to whom, the world tears along their join.
        int steps = 0, notReciprocal = 0, offFace = 0;
        var examples = new List<string>();

        for (int t = 0; t < 60000; t++)
        {
            int face = rng.Next(6);
            int shell = rng.Next(0, 400);
            int res = CubedSphere.ResolutionAt(shell, 800f, 1f);

            // Bias toward EDGES, where the interesting cases are.
            int u = rng.Next(4) == 0 ? (rng.Next(2) == 0 ? 0 : res - 1) : rng.Next(res);
            int v = rng.Next(4) == 0 ? (rng.Next(2) == 0 ? 0 : res - 1) : rng.Next(res);

            Vector3I cell = grid.Pack(face, u, v, shell);

            foreach ((int du, int dv) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                if (!grid.Neighbour(cell, du, dv, 0, out Vector3I next)) continue;
                steps++;

                grid.Unpack(next, out int nf, out _, out _, out _);
                if (nf != face) offFace++;

                // The two centres must be about one node apart, whatever face
                // the neighbour turned out to be on.
                float d = grid.CentreOf(cell).DistanceTo(grid.CentreOf(next));
                if (d > 3f)
                {
                    notReciprocal++;
                    if (examples.Count < 5)
                        examples.Add($"({face},{u},{v},{shell}) +({du},{dv}) -> "
                                   + $"face {nf}, centres {d:F2} apart");
                }
            }
        }

        GD.Print($"[gr] neighbour steps={steps} crossedAFace={offFace} "
               + $"tooFarApart={notReciprocal}");
        foreach (string e in examples) GD.Print($"[gr]   {e}");

        // 3. NO CELL IS ITS OWN NEIGHBOUR, and no two directions collide.
        int selfHits = 0;
        for (int t = 0; t < 20000; t++)
        {
            int face = rng.Next(6);
            int shell = rng.Next(0, 400);
            int res = CubedSphere.ResolutionAt(shell, 800f, 1f);
            int u = rng.Next(res), v = rng.Next(res);
            Vector3I cell = grid.Pack(face, u, v, shell);

            var seen = new HashSet<Vector3I>();
            foreach ((int du, int dv) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                if (!grid.Neighbour(cell, du, dv, 0, out Vector3I next)) continue;
                if (next == cell || !seen.Add(next)) selfHits++;
            }
        }
        GD.Print($"[gr] degenerate neighbours: {selfHits}");

        // 4. RADIAL steps stay radial.
        int radialBad = 0;
        for (int t = 0; t < 20000; t++)
        {
            int face = rng.Next(6);
            int shell = rng.Next(1, 400);
            int res = CubedSphere.ResolutionAt(shell, 800f, 1f);
            Vector3I cell = grid.Pack(face, rng.Next(res), rng.Next(res), shell);

            if (!grid.Neighbour(cell, 0, 0, -1, out Vector3I out1)) continue;
            float r0 = grid.CentreOf(cell).Length();
            float r1 = grid.CentreOf(out1).Length();
            if (r1 <= r0) radialBad++;
        }
        GD.Print($"[gr] outward steps that did not increase radius: {radialBad}");

        GetTree().Quit();
    }
}
