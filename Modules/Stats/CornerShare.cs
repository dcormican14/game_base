using System;
using System.Collections.Generic;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Asks whether a surface CAP CORNER can carry a height that every cell
/// touching it agrees on.
///
/// This is the question the "slope inward when mined" feature turns on. A cap
/// corner is where the planet's surface meets a Voronoi edge, and an edge is
/// shared by several cells. If those cells all place that corner at the same
/// point, then a rule keyed on the POINT -- rather than on any one cell --
/// gives them all the same answer, and lowering it cannot split the surface.
///
/// If they do not, no per-corner rule can work and the feature needs another
/// construction entirely.
/// </summary>
public partial class CornerShare : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Patches { get; set; } = 120;
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

    // A centimetre: far below a 2-unit node, far above float noise at r=120.
    private const float Quantum = 0.01f;

    private static Vector3I Key(Vector3 p) => new(
        Mathf.RoundToInt(p.X / Quantum),
        Mathf.RoundToInt(p.Y / Quantum),
        Mathf.RoundToInt(p.Z / Quantum));

    private void Run()
    {
        GD.Print("=== CORNER SHARING ===");

        if (_world?.Grid is not OrganicGrid grid)
        {
            GD.Print("not the organic planet");
            return;
        }

        float node = grid.NodeSize;
        var rng = new Random(31337);

        // Corner point -> the distinct cells that reported it.
        var owners = new Dictionary<Vector3I, HashSet<Vector3I>>();

        int cellsSampled = 0;

        for (int i = 0; i < Patches * 80 && cellsSampled < Patches * 8; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I centre = grid.CellAt(dir * (grid.SurfaceRadius - node * 0.25f));
            if (!grid.Contains(centre)) continue;

            // A CELL TOGETHER WITH ITS NEIGHBOURS.
            //
            // Picking cells by random ray almost never picks two that touch, so
            // no corner gets reported twice and the test reads "not shared"
            // whatever the truth is -- which is exactly what it did at 0.3%.
            // The question is whether cells that DO meet agree about the corner
            // where they meet, so the neighbourhood is the thing to walk.
            cellsSampled += Sample(grid, centre, owners);

            int wallCount = grid.WallCount(centre);

            for (int w = 0; w < wallCount; w++)
            {
                if (!grid.WallNeighbour(centre, w, out Vector3I nb)) continue;
                if (!grid.Contains(nb)) continue;

                cellsSampled += Sample(grid, nb, owners);
            }
        }

        if (owners.Count == 0) { GD.Print("no cap corners found"); return; }

        var byShare = new SortedDictionary<int, int>();

        foreach (var kv in owners)
        {
            byShare.TryGetValue(kv.Value.Count, out int had);
            byShare[kv.Value.Count] = had + 1;
        }

        int shared = 0;
        foreach (var kv in owners) if (kv.Value.Count > 1) shared++;

        GD.Print($"  surface cells sampled:   {cellsSampled}");
        GD.Print($"  distinct cap corners:    {owners.Count}");
        GD.Print("  -- how many DISTINCT cells placed a corner at the same point --");

        foreach (var kv in byShare)
            GD.Print($"    {kv.Key} cell(s): {kv.Value,5} corners");

        GD.Print($"  corners shared by 2+ cells: {shared} of {owners.Count} "
            + $"({100.0 * shared / owners.Count:F1}%)");

        GD.Print(shared * 4 > owners.Count
            ? "  => cap corners ARE shared: a per-corner height is well defined"
            : "  => corners are mostly NOT shared: no per-corner rule can work");

        GD.Print("=== END CORNER SHARING ===");
    }

    /// <summary>Records one cell's cap corners. Returns 1 if it had a cap.</summary>
    private int Sample(OrganicGrid grid, Vector3I cell,
        Dictionary<Vector3I, HashSet<Vector3I>> owners)
    {
        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxFaceCorners];

        int count = grid.Faces(cell, walls, sides, corners);
        if (count == 0) return 0;

        int at = 0;
        bool hasCap = false;

        for (int n = 0; n < count; n++)
        {
            // THE CAP ONLY. A wall is shared by construction; the question is
            // about the surface.
            if (sides[n] >= 3 && walls[n] == cell)
            {
                hasCap = true;

                for (int c = 0; c < sides[n]; c++)
                {
                    Vector3I key = Key(corners[at + c]);

                    if (!owners.TryGetValue(key, out HashSet<Vector3I> set))
                    {
                        set = new HashSet<Vector3I>();
                        owners[key] = set;
                    }

                    set.Add(cell);
                }
            }

            at += sides[n];
        }

        return hasCap ? 1 : 0;
    }
}
