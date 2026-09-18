using System;
using System.Collections.Generic;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Measures the bed a soil layer would have to cover.
///
/// The idea under test: stop making topsoil a Voronoi CELL and make it a layer
/// laid ON the rock instead. Every failure so far came from soil being a cell --
/// a cell must tile exactly with its neighbours, share walls and corners, never
/// overlap and never gap, and that is what five attempts kept breaking. A layer
/// that is not a cell has none of those constraints.
///
/// But a layer has to sit on something. This measures what the raw rock surface
/// is actually like: how far its top varies over a node, how much of it faces
/// upward at all, and how deep a layer would have to be to hide the steps
/// between cells. Those numbers decide whether "semi-level and flush" is
/// achievable and at what thickness.
/// </summary>
public partial class SoilBedCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Samples { get; set; } = 400;
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

    /// <summary>
    /// The highest point of a node, and how much of its surface faces outward.
    /// </summary>
    private bool TopOf(OrganicGrid grid, Vector3I cell,
        out float top, out float upwardArea, out float totalArea)
    {
        top = 0f;
        upwardArea = 0f;
        totalArea = 0f;

        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxFaceCorners];

        int count = grid.Faces(cell, walls, sides, corners);

        if (count == 0)
            return false;

        Vector3 up = grid.CentreOf(cell).Normalized();

        float highest = float.MinValue;
        int at = 0;

        for (int n = 0; n < count; n++)
        {
            if (sides[n] < 3)
            {
                at += sides[n];
                continue;
            }

            Vector3 total = Vector3.Zero;

            for (int c = 1; c + 1 < sides[n]; c++)
            {
                total += (corners[at + c] - corners[at])
                    .Cross(corners[at + c + 1] - corners[at]);
            }

            float area = total.Length() * 0.5f;
            totalArea += area;

            if (total.LengthSquared() > 0.000001f
                && total.Normalized().Dot(up) > 0.5f)
            {
                upwardArea += area;
            }

            for (int c = 0; c < sides[n]; c++)
            {
                float radius = corners[at + c].Length();

                if (radius > highest) highest = radius;
            }

            at += sides[n];
        }

        if (highest == float.MinValue)
            return false;

        top = highest;
        return true;
    }

    private void Run()
    {
        GD.Print("=== SOIL BED CHECK ===");

        if (_world?.Grid is not OrganicGrid grid)
        {
            GD.Print("not the organic planet");
            return;
        }

        float node = grid.NodeSize;
        float radius = grid.SurfaceRadius;

        var rng = new Random(8642);

        int sampled = 0;
        double upwardShare = 0;
        double stepTotal = 0, stepWorst = 0;
        int stepPairs = 0;

        // How high does the rock reach, and how much does that vary?
        double topLow = double.MaxValue, topHigh = double.MinValue;

        for (int i = 0; i < Samples * 40 && sampled < Samples; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I cell = grid.CellAt(dir * (radius - node * 0.25f));

            if (!grid.Contains(cell) || !_world.HasNode(cell)) continue;
            if (!TopOf(grid, cell, out float top, out float upward, out float all))
                continue;

            // ONLY CELLS WITH SKY ABOVE. A buried cell is not part of the bed.
            bool exposed = false;

            for (int dx = -1; dx <= 1 && !exposed; dx++)
            for (int dy = -1; dy <= 1 && !exposed; dy++)
            for (int dz = -1; dz <= 1 && !exposed; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;

                var at = new Vector3I(cell.X + dx, cell.Y + dy, cell.Z + dz);

                if (_world.HasNode(at)) continue;
                if (new Vector3(dx, dy, dz).Dot(grid.CentreOf(cell).Normalized()) > 0.3f)
                    exposed = true;
            }

            if (!exposed) continue;

            sampled++;

            if (all > 0.0001f)
                upwardShare += upward / all;

            if (top < topLow) topLow = top;
            if (top > topHigh) topHigh = top;

            // THE STEP between this node's top and its exposed neighbours'.
            // That is the height a layer has to bridge to read as flush.
            int wallCount = grid.WallCount(cell);

            for (int w = 0; w < wallCount; w++)
            {
                if (!grid.WallNeighbour(cell, w, out Vector3I nb)) continue;
                if (!grid.Contains(nb) || !_world.HasNode(nb)) continue;
                if (!TopOf(grid, nb, out float theirTop, out _, out _)) continue;

                double step = Math.Abs(top - theirTop);

                stepTotal += step;
                stepPairs++;

                if (step > stepWorst) stepWorst = step;
            }
        }

        if (sampled == 0) { GD.Print("  no exposed rock sampled"); return; }

        GD.Print($"  exposed rock nodes:          {sampled}");
        GD.Print($"  of each node's surface, the share facing UP: "
            + $"{100.0 * upwardShare / sampled:F1}%");
        GD.Print($"  their tops span r {topLow:F2} .. {topHigh:F2}"
            + $"  ({topHigh - topLow:F2} units, {(topHigh - topLow) / node:F2} nodes)");

        if (stepPairs > 0)
        {
            GD.Print($"  step between neighbouring tops ({stepPairs} pairs):");
            GD.Print($"    mean  {stepTotal / stepPairs:F3} units"
                + $"  ({stepTotal / stepPairs / node:F2} nodes)");
            GD.Print($"    worst {stepWorst:F3} units"
                + $"  ({stepWorst / node:F2} nodes)");
            GD.Print($"  => a layer must be about {stepWorst / node:F2} nodes deep");
            GD.Print($"     to hide the worst step, or {stepTotal / stepPairs / node:F2}");
            GD.Print($"     to hide the typical one");
        }

        GD.Print("=== END SOIL BED CHECK ===");
    }
}
