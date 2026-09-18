using System;
using System.Collections.Generic;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Asks whether one node's faces poke INTO a neighbour.
///
/// The seam check asks whether a node's own faces still meet each other, and
/// the divot check whether two neighbours agree about a shared corner. Neither
/// catches the failure reported in game: a face that has been pushed past the
/// wall it shares with the node next door, so the two solids overlap and their
/// surfaces cut through one another.
///
/// A Voronoi cell is exactly the set of points nearer its own site than any
/// other, so the test is simple and exact: no corner of a node may be nearer to
/// another site than to its own. A corner that is belongs to a node that has
/// grown into its neighbour.
/// </summary>
public partial class OverlapCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Cells { get; set; } = 400;
    [Export] public int Digs { get; set; } = 60;
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
        GD.Print("=== OVERLAP CHECK ===");

        if (_world?.Grid is not OrganicGrid grid)
        {
            GD.Print("not the organic planet");
            return;
        }

        GD.Print("-- before any digging --");
        Survey(grid);

        var rng = new Random(313);
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

        GD.Print("=== END OVERLAP CHECK ===");
    }

    private void Survey(OrganicGrid grid)
    {
        var rng = new Random(2024);

        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxFaceCorners];

        int cells = 0, intruding = 0, cornersSeen = 0, cornersOutside = 0;
        int capIntrusions = 0, wallIntrusions = 0;
        int noise = 0, mild = 0, gross = 0;
        float _nodeSize = grid.NodeSize;
        double worst = 0;

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

            cells++;

            Vector3 mine = grid.CentreOf(cell);
            bool bad = false;

            int at = 0;

            for (int n = 0; n < count; n++)
            {
                for (int c = 0; c < sides[n]; c++)
                {
                    Vector3 corner = corners[at + c];
                    cornersSeen++;

                    float toMine = corner.DistanceTo(mine);

                    // Is any neighbouring site nearer than this node's own?
                    float nearest = toMine;
                    bool foreign = false;

                    for (int dx = -2; dx <= 2; dx++)
                    for (int dy = -2; dy <= 2; dy++)
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;

                        var other = new Vector3I(
                            cell.X + dx, cell.Y + dy, cell.Z + dz);

                        if (!grid.Contains(other)) continue;

                        float d = corner.DistanceTo(grid.CentreOf(other));

                        if (d < nearest)
                        {
                            nearest = d;
                            foreign = true;
                        }
                    }

                    if (!foreign) continue;

                    // HOW FAR IN. A corner exactly on a shared wall is
                    // equidistant, so only a real excess counts.
                    float excess = toMine - nearest;

                    if (excess <= 0.001f) continue;

                    // BUCKETED BY SIZE, because the two failures look nothing
                    // alike. A corner a millimetre past a shared wall is the
                    // float noise of two cells solving the same crossing; one a
                    // whole node past it is a solid grown into its neighbour,
                    // which is what cuts through in game. Counting them
                    // together says 99% of cells are broken and means nothing.
                    if (excess > _nodeSize * 0.25f)
                    {
                        gross++;

                        if (gross <= 6)
                            GD.Print($"      GROSS {excess:F3} on face owned by "
                                + $"{walls[n]} (cell {cell}, cap={(walls[n] == cell)})");
                    }
                    else if (excess > _nodeSize * 0.05f) mild++;
                    else noise++;

                    cornersOutside++;
                    bad = true;

                    if (excess > worst) worst = excess;

                    // WHICH FACE is the intruding corner on? A wall shared
                    // with a neighbour, or the cell's own surface cap?
                    if (walls[n] == cell) capIntrusions++; else wallIntrusions++;
                }

                at += sides[n];
            }

            if (bad) intruding++;
        }

        if (cells == 0) { GD.Print("    no cells sampled"); return; }

        GD.Print($"    cells sampled:              {cells}");
        GD.Print($"    corners sampled:            {cornersSeen}");
        GD.Print($"    corners INSIDE another node: {cornersOutside}"
            + $"  ({100.0 * cornersOutside / Math.Max(1, cornersSeen):F2}%)");
        GD.Print($"    cells with any excess:      {intruding}"
            + $"  ({100.0 * intruding / cells:F1}%)");
        GD.Print("    by how far past the shared wall:");
        GD.Print($"      under 0.05 node (noise):  {noise}");
        GD.Print($"      0.05 to 0.25 node:        {mild}");
        GD.Print($"      OVER 0.25 node (visible): {gross}   <-- these cut through");
        GD.Print($"    worst intrusion:            {worst:F4} units");
        GD.Print($"      of which on a surface cap: {capIntrusions}");
        GD.Print($"      of which on a shared wall: {wallIntrusions}");
        GD.Print(gross == 0
            ? "    VERDICT: no node visibly reaches into another"
            : "    VERDICT: FAILED -- nodes overlap, so their faces cut through");
    }
}
