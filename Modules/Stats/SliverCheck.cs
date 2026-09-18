using System;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Measures the SLIVERS at the surface: nodes the sphere clip has cut down to
/// a scrap of their full volume.
///
/// A cell whose site sits just inside the radius keeps only the thin wedge
/// below the surface. It is a real node -- it stores, it meshes, it can be
/// mined -- but it is a few centimetres thick, which is what makes it hard to
/// aim at and what makes the surface read as shattered rather than as ground.
///
/// Reported by CAP AREA against the node's own cross-section, so the number
/// means the same thing at any node size.
/// </summary>
public partial class SliverCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Columns { get; set; } = 400;
    [Export] public bool QuitWhenDone { get; set; } = true;

    private NodeWorld _world;
    private ChunkStreamer _streamer;
    private int _frames;
    private bool _done;

    public override void _Ready()
    {
        Node scene = GetTree().CurrentScene;
        _world = NodeSearch.FindByType<NodeWorld>(scene);
        _streamer = NodeSearch.FindByType<ChunkStreamer>(scene);
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
        GD.Print("=== SLIVER CHECK ===");

        if (_world?.Grid is not OrganicGrid grid)
        {
            GD.Print("not the organic planet");
            return;
        }

        float node = grid.NodeSize;
        var rng = new Random(4242);

        // A node's full height, for scale: how thick a surface node SHOULD be.
        int sampled = 0;
        int thin10 = 0, thin25 = 0, thin50 = 0;
        double thinnest = 999;
        double totalThickness = 0;

        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxFaceCorners];

        for (int i = 0; i < Columns * 40 && sampled < Columns; i++)
        {
            // A random direction, then the outermost solid node along it.
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I top = default;
            bool found = false;

            for (float r = grid.SurfaceRadius + node; r > grid.SurfaceRadius - node * 4; r -= node * 0.25f)
            {
                Vector3I cell = grid.CellAt(dir * r);
                if (!grid.Contains(cell)) continue;
                if (!_world.HasNode(cell)) continue;
                top = cell; found = true; break;
            }

            if (!found) continue;

            int count = grid.Faces(top, walls, sides, corners);
            if (count == 0) continue;

            // RADIAL EXTENT of the node: how far it reaches along its own
            // outward direction, lowest corner to highest. That is its
            // thickness as the player experiences it.
            float lo = float.MaxValue, hi = float.MinValue;

            int at = 0;
            for (int n = 0; n < count; n++)
            {
                for (int c = 0; c < sides[n]; c++)
                {
                    float d = corners[at + c].Length();
                    if (d < lo) lo = d;
                    if (d > hi) hi = d;
                }
                at += sides[n];
            }

            if (lo > hi) continue;

            double thickness = (hi - lo) / node;

            sampled++;
            totalThickness += thickness;
            if (thickness < thinnest) thinnest = thickness;
            if (thickness < 0.10) thin10++;
            if (thickness < 0.25) thin25++;
            if (thickness < 0.50) thin50++;
        }

        if (sampled == 0) { GD.Print("no surface nodes sampled"); return; }

        GD.Print($"  surface nodes sampled: {sampled}");
        GD.Print($"  radial thickness, as a fraction of one node:");
        GD.Print($"    mean:     {totalThickness / sampled:F3}");
        GD.Print($"    thinnest: {thinnest:F3}");
        GD.Print($"  SLIVERS -- nodes thinner than:");
        GD.Print($"    half a node:    {thin50,4}  ({100.0 * thin50 / sampled:F1}%)");
        GD.Print($"    a quarter node: {thin25,4}  ({100.0 * thin25 / sampled:F1}%)");
        GD.Print($"    a tenth node:   {thin10,4}  ({100.0 * thin10 / sampled:F1}%)   <-- unaimable");
        // WHAT DROPPING THEM WOULD COST.
        //
        // A sliver removed exposes whatever is under it, and the node below
        // has its own cap at its own height -- so the surface drops by however
        // far apart the two are. Measured here as the gap between a sliver's
        // cap and the top of the node beneath it.
        GD.Print("  -- if slivers were dropped outright --");

        int holes = 0, measured = 0;
        double worstDrop = 0, totalDrop = 0;

        for (int i = 0; i < Columns * 40 && measured < 200; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            // Find a sliver: outermost solid node that is thin.
            Vector3I top = default;
            bool found = false;

            for (float r = grid.SurfaceRadius + node; r > grid.SurfaceRadius - node * 4; r -= node * 0.25f)
            {
                Vector3I cell = grid.CellAt(dir * r);
                if (!grid.Contains(cell) || !_world.HasNode(cell)) continue;
                top = cell; found = true; break;
            }

            if (!found) continue;

            int count = grid.Faces(top, walls, sides, corners);
            if (count == 0) continue;

            float lo = float.MaxValue, hi = float.MinValue;
            int at = 0;
            for (int n = 0; n < count; n++)
            {
                for (int c = 0; c < sides[n]; c++)
                {
                    float d = corners[at + c].Length();
                    if (d < lo) lo = d;
                    if (d > hi) hi = d;
                }
                at += sides[n];
            }

            if (lo > hi) continue;
            if ((hi - lo) / node >= 0.5) continue;   // not a sliver

            // How far down is the next solid node's top?
            float below = -1;
            for (float r = lo - 0.01f; r > grid.SurfaceRadius - node * 5; r -= node * 0.1f)
            {
                Vector3I cell = grid.CellAt(dir * r);
                if (cell == top) continue;
                if (!grid.Contains(cell) || !_world.HasNode(cell)) continue;

                int c2 = grid.Faces(cell, walls, sides, corners);
                if (c2 == 0) continue;

                float top2 = float.MinValue;
                int at2 = 0;
                for (int n = 0; n < c2; n++)
                {
                    for (int c = 0; c < sides[n]; c++)
                    {
                        float d = corners[at2 + c].Length();
                        if (d > top2) top2 = d;
                    }
                    at2 += sides[n];
                }

                below = top2;
                break;
            }

            measured++;

            if (below < 0)
            {
                // The ray probe can miss a node that sits sideways. Ask the
                // grid for the sliver's actual wall neighbours before calling
                // it a hole -- a cell with solid rock beside and below it is
                // covered however the ray fell.
                bool anySolidNeighbour = false;
                int wallCount = grid.WallCount(top);

                for (int w = 0; w < wallCount && !anySolidNeighbour; w++)
                {
                    if (!grid.WallNeighbour(top, w, out Vector3I nb)) continue;
                    if (!grid.Contains(nb) || !_world.HasNode(nb)) continue;

                    // Only counts as cover if it is not itself outside.
                    if (grid.CentreOf(nb).Length() <= grid.CentreOf(top).Length())
                        anySolidNeighbour = true;
                }

                if (!anySolidNeighbour) holes++;
                continue;
            }

            double drop = (hi - below) / node;
            totalDrop += drop;
            if (drop > worstDrop) worstDrop = drop;
        }

        if (measured > 0)
        {
            GD.Print($"    slivers examined:        {measured}");
            GD.Print($"    surface drop, mean:      {totalDrop / Math.Max(1, measured - holes):F2} nodes");
            GD.Print($"    surface drop, worst:     {worstDrop:F2} nodes");
            GD.Print($"    NOTHING underneath:      {holes}  <-- these become real holes");
        }

        GD.Print("=== END SLIVER CHECK ===");
    }
}
