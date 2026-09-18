using System;
using System.Collections.Generic;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Checks that a node is CLOSED: that its own faces still meet each other.
///
/// The divot check asks whether two NEIGHBOURS agree about a shared corner.
/// This asks something narrower and, it turns out, more important: whether one
/// node's cap still meets its own side walls.
///
/// A cap corner and the top corner of the wall beneath it are the same point of
/// the same polyhedron. Lowering one without the other opens the node along its
/// whole top rim -- which reads in game as faces that clip through one another
/// and faces that are simply not there.
/// </summary>
public partial class SeamCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Cells { get; set; } = 400;
    [Export] public int Digs { get; set; } = 40;
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
        GD.Print("=== SEAM CHECK ===");

        if (_world?.Grid is not OrganicGrid grid)
        {
            GD.Print("not the organic planet");
            return;
        }

        GD.Print("-- before any digging --");
        Survey(grid);

        // Now dig, which is when the depression actually fires.
        var rng = new Random(606);
        int dug = 0;

        for (int i = 0; i < Digs * 200 && dug < Digs; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I hit = grid.CellAt(dir * (grid.SurfaceRadius - grid.NodeSize * 0.25f));

            if (!grid.Contains(hit) || !_world.HasNode(hit)) continue;
            if (!_world.RemoveNode(hit)) continue;

            dug++;
        }

        GD.Print($"-- after {dug} digs --");
        Survey(grid);

        GD.Print("=== END SEAM CHECK ===");
    }

    /// <summary>
    /// For every surface cell, does each cap corner still have a wall corner
    /// sitting on top of it?
    /// </summary>
    private void Survey(OrganicGrid grid)
    {
        var rng = new Random(1234);

        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxFaceCorners];

        int cells = 0, openCells = 0, capCorners = 0, orphaned = 0;
        double worstGap = 0;

        for (int i = 0; i < Cells * 60 && cells < Cells; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I cell = grid.CellAt(dir * (grid.SurfaceRadius - grid.NodeSize * 0.25f));
            if (!grid.Contains(cell) || !_world.HasNode(cell)) continue;

            int count = grid.Faces(cell, walls, sides, corners);
            if (count == 0) continue;

            // Split the faces into the cap and everything else.
            var cap = new List<Vector3>();
            var wallCorners = new List<Vector3>();

            int at = 0;

            for (int n = 0; n < count; n++)
            {
                if (sides[n] >= 3)
                {
                    for (int c = 0; c < sides[n]; c++)
                    {
                        if (walls[n] == cell)
                            cap.Add(corners[at + c]);
                        else
                            wallCorners.Add(corners[at + c]);
                    }
                }

                at += sides[n];
            }

            if (cap.Count == 0) continue;

            cells++;

            bool open = false;

            // EVERY CAP CORNER IS ALSO A WALL CORNER on a closed polyhedron:
            // the cap is bounded by the tops of the walls, so each of its
            // corners is where two walls and the cap meet.
            foreach (Vector3 corner in cap)
            {
                capCorners++;

                float nearest = float.MaxValue;

                foreach (Vector3 other in wallCorners)
                {
                    float gap = corner.DistanceTo(other);
                    if (gap < nearest) nearest = gap;
                }

                if (nearest > worstGap) worstGap = nearest;

                // A millimetre: these should be the SAME point, computed twice.
                if (nearest > 0.001f)
                {
                    orphaned++;
                    open = true;
                }
            }

            if (open) openCells++;
        }

        if (cells == 0) { GD.Print("    no surface cells sampled"); return; }

        GD.Print($"    surface cells:              {cells}");
        GD.Print($"    cap corners:                {capCorners}");
        GD.Print($"    cap corners with NO wall corner on them: {orphaned}"
            + $"  ({100.0 * orphaned / Math.Max(1, capCorners):F1}%)");
        GD.Print($"    cells left OPEN:            {openCells}"
            + $"  ({100.0 * openCells / cells:F1}%)");
        GD.Print($"    worst gap:                  {worstGap:F4} units");
        GD.Print(orphaned == 0
            ? "    VERDICT: every node is closed"
            : "    VERDICT: FAILED -- nodes are split between cap and walls");
    }
}
