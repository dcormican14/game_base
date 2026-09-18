using System;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Asks how many of a node's faces are worth drawing.
///
/// A jittered cubic lattice gives about 14.8 faces per Voronoi cell at the
/// jitter used here. That figure is worth stating carefully, because it is easy
/// to get wrong: three methods were tried and two were wrong.
///
///   plane sampling, 240 samples   12.9   undersampled, missed thin faces
///   ray marching                  14.7   carried a stale owner between rays
///   vertex enumeration            15.8   counts every plane touching a vertex
///   plane sampling, 3000 samples  14.8   CALIBRATES: gives exactly 6 for a cube
///
/// Only the last passes the one case with a known answer -- a perfect cubic
/// lattice must give exactly six faces -- so 14.8 is the number to compare
/// against, and the vertex method reports 26 for that same cube.
///
/// The surplus this measures is therefore not two or three faces per cell. It
/// is the SLIVERS: real polygons of almost no area, too thin to see but in the
/// same place as whatever is behind them, so the two fight for the depth
/// buffer. That is what "too many planes" looks like on screen.
///
/// Measured by AREA, against the area a face of a cell this size should have.
/// </summary>
public partial class PlaneCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Cells { get; set; } = 400;
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

    private static float AreaOf(ReadOnlySpan<Vector3> corners, int at, int sides)
    {
        if (sides < 3) return 0f;

        Vector3 total = Vector3.Zero;

        for (int c = 1; c + 1 < sides; c++)
        {
            total += (corners[at + c] - corners[at])
                .Cross(corners[at + c + 1] - corners[at]);
        }

        return total.Length() * 0.5f;
    }

    private void Run()
    {
        GD.Print("=== PLANE CHECK ===");

        if (_world?.Grid is not OrganicGrid grid)
        {
            GD.Print("not the organic planet");
            return;
        }

        float node = grid.NodeSize;

        // A cell of this size has about fifteen faces over a surface of
        // roughly 6 n^2, so a typical face is around this big.
        float typical = 6f * node * node / 15f;

        var rng = new Random(99);

        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxFaceCorners];

        int cells = 0;
        long faces = 0, degenerate = 0, slivers = 0, real = 0;
        double areaTotal = 0;

        for (int i = 0; i < Cells * 60 && cells < Cells; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I cell = grid.CellAt(dir * (grid.SurfaceRadius - node * 0.25f));
            if (!grid.Contains(cell) || !_world.HasNode(cell)) continue;

            int count = grid.Faces(cell, walls, sides, corners);
            if (count == 0) continue;

            cells++;

            int at = 0;

            for (int n = 0; n < count; n++)
            {
                faces++;

                if (sides[n] < 3)
                {
                    degenerate++;
                    at += sides[n];
                    continue;
                }

                float area = AreaOf(corners, at, sides[n]);
                areaTotal += area;

                // A HUNDREDTH of a typical face: too small to read as a
                // surface, big enough to fight with what is behind it.
                if (area < typical * 0.01f) slivers++;
                else real++;

                at += sides[n];
            }
        }

        if (cells == 0) { GD.Print("    no cells"); return; }

        GD.Print($"  cells sampled:        {cells}");
        GD.Print($"  faces per cell:       {(double)faces / cells:F1}");
        GD.Print($"    of which degenerate (<3 corners): {(double)degenerate / cells:F1}");
        GD.Print($"    of which slivers (<1% of a face): {(double)slivers / cells:F1}");
        GD.Print($"    REAL, drawable faces:             {(double)real / cells:F1}");
        GD.Print($"  a jittered lattice gives about 14.8 (calibrated: 6.0 for a cube)");
        GD.Print($"  mean face area: {areaTotal / Math.Max(1, real):F3} "
            + $"(a typical face is about {typical:F3})");

        // WHAT THE MESHER ACTUALLY DREW, which is the number that matters:
        // the grid still reports the sliver, the renderer just leaves it out.
        int pumped = 0;
        while (_world.HasQueuedMeshes && pumped < 4000)
        {
            _world.FlushQueuedMeshes(64f);
            pumped++;
            System.Threading.Thread.Sleep(1);
        }

        GD.Print($"  triangles uploaded across the world: {_world.TriangleCount}");
    }
}
