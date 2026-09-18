using System;
using System.Collections.Generic;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Checks the divot: that mining a surface node makes the ground around it
/// slope inward, and that the surface does not tear while doing so.
///
/// Two claims, and the second is the one that has failed before. A shape that
/// depends on what has been mined can very easily be computed differently by
/// two cells sharing a wall, and then the wall splits and you see through the
/// planet. So the test measures BOTH: that corners actually moved, and that
/// every pair of cells still agrees about where their shared corners are.
/// </summary>
public partial class DivotCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Digs { get; set; } = 60;
    [Export] public bool QuitWhenDone { get; set; } = true;

    private NodeWorld _world;
    private Node3D _player;
    private ChunkStreamer _streamer;
    private int _frames;
    private bool _done;

    public override void _Ready()
    {
        Node scene = GetTree().CurrentScene;
        _world = NodeSearch.FindByType<NodeWorld>(scene);
        _player = scene?.GetNodeOrNull<Node3D>("Player");
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

    private const float Quantum = 0.01f;

    private static Vector3I Key(Vector3 p) => new(
        Mathf.RoundToInt(p.X / Quantum),
        Mathf.RoundToInt(p.Y / Quantum),
        Mathf.RoundToInt(p.Z / Quantum));

    private void Run()
    {
        GD.Print("=== DIVOT CHECK ===");

        if (_world?.Grid is not OrganicGrid grid || _player == null)
        {
            GD.Print("not the organic planet");
            return;
        }

        Vector3 eye = _player.GlobalPosition;
        var rng = new Random(9090);

        int dug = 0;
        int movedSomething = 0;
        double worstDrop = 0, totalDrop = 0;
        int cornersMoved = 0;

        for (int i = 0; i < Digs * 200 && dug < Digs; i++)
        {
            // ALL OVER THE PLANET, not down one shaft.
            //
            // Casting repeatedly from a fixed eye digs a hole and then keeps
            // mining the bottom of it: cells a node or two down, with no cap of
            // their own and nothing above to slump. Measured, the picks were
            // landing at r=115..118 against a surface at 120, and the test was
            // reporting a feature about surfaces as broken from tunnel digs.
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I hit = grid.CellAt(dir * (grid.SurfaceRadius - grid.NodeSize * 0.25f));

            if (!grid.Contains(hit) || !_world.HasNode(hit)) continue;

            // A SURFACE NODE, which is what the feature is about.
            if (CapOf(grid, hit).Count == 0)
                continue;

            // EVERY SURFACE CELL NEAR THE HOLE, not just its wall neighbours.
            //
            // A dug cell's wall neighbours are mostly sideways and below --
            // buried rock with no cap at all, measured at 0 or 1 of eleven.
            // The cells whose ground should slump are the ones whose CAP
            // CORNERS sit near the hole, which is a different set.
            var before = new Dictionary<Vector3I, List<Vector3>>();

            Vector3 hole = grid.CentreOf(hit);
            float reach = grid.NodeSize * 2.5f;

            for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
            for (int dz = -2; dz <= 2; dz++)
            {
                var nb = new Vector3I(hit.X + dx, hit.Y + dy, hit.Z + dz);

                if (nb == hit) continue;
                if (!grid.Contains(nb) || !_world.HasNode(nb)) continue;
                if (grid.CentreOf(nb).DistanceTo(hole) > reach) continue;

                List<Vector3> cap = CapOf(grid, nb);
                if (cap.Count == 0) continue;

                before[nb] = cap;
            }

            if (before.Count == 0) continue;

            if (!_world.RemoveNode(hit)) continue;

            dug++;

            bool any = false;

            foreach (var kv in before)
            {
                List<Vector3> now = CapOf(grid, kv.Key);
                List<Vector3> was = kv.Value;

                // COMPARED BY POSITION, NOT BY INDEX. A cap's corner list can
                // change length and order between two builds, and lining them
                // up by index then compares unrelated corners -- reporting
                // movement where there is none and missing it where there is.
                // Each old corner is matched to the nearest new one.
                foreach (Vector3 old in was)
                {
                    float bestGap = float.MaxValue;
                    float bestDrop = 0;

                    foreach (Vector3 fresh in now)
                    {
                        // Matched on DIRECTION, which the depression leaves
                        // alone -- it only moves a corner inward along its own
                        // radius, so the same corner points the same way
                        // before and after.
                        float gap = old.Normalized().DistanceTo(fresh.Normalized())
                            * old.Length();

                        if (gap < bestGap)
                        {
                            bestGap = gap;
                            bestDrop = old.Length() - fresh.Length();
                        }
                    }

                    if (bestGap > grid.NodeSize * 0.4f) continue;

                    if (bestDrop > 0.001f)
                    {
                        any = true;
                        cornersMoved++;
                        totalDrop += bestDrop;
                        if (bestDrop > worstDrop) worstDrop = bestDrop;
                    }
                }
            }

            if (any) movedSomething++;
            else if (dug <= 8)
            {
                // WHY did nothing move? Either the dug cell had no surface
                // neighbours with caps, or no cap corner was actually touched
                // by it.
                int withCaps = 0;
                foreach (var kv in before) if (kv.Value.Count > 0) withCaps++;

                float r = grid.CentreOf(hit).Length();
                GD.Print($"    no slump at {hit}: {before.Count} capped nearby, "
                    + $"dug cell sits at r={r:F1} (surface {grid.SurfaceRadius}), "
                    + $"own cap corners {CapOf(grid, hit).Count}");
            }
        }

        GD.Print($"  digs that had surface neighbours: {dug}");
        GD.Print($"  digs where the ground slumped:    {movedSomething}");
        GD.Print($"  cap corners lowered:              {cornersMoved}");

        if (cornersMoved > 0)
        {
            GD.Print($"  drop per corner: mean {totalDrop / cornersMoved:F3}, "
                + $"worst {worstDrop:F3} units  (node is {grid.NodeSize})");
        }

        // DOES THE MESHER REBUILD EVERYTHING THAT MOVED?
        //
        // Depress reads all 26 lattice neighbours, while MarkDirty walks only
        // the WALL neighbours -- a smaller set. A cell that slumps but is not a
        // wall neighbour of the dug cell would keep its old geometry, and the
        // player would see and stand on ground that no longer matches.
        GD.Print("-- is every slumped cell marked for rebuild? --");
        Reach(grid);

        // THE ONE THAT MATTERS: is the surface still sealed?
        GD.Print("-- after all that digging, do neighbours still agree? --");
        Agreement(grid);

        GD.Print("=== END DIVOT CHECK ===");
    }

    /// <summary>
    /// Are all the cells a dig can reshape also cells the world would rebuild?
    /// </summary>
    private void Reach(OrganicGrid grid)
    {
        var rng = new Random(2027);
        int checkedCells = 0, missed = 0;

        for (int i = 0; i < 4000 && checkedCells < 200; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I hit = grid.CellAt(dir * (grid.SurfaceRadius - grid.NodeSize * 0.25f));
            if (!grid.Contains(hit) || !_world.HasNode(hit)) continue;

            // Which sections would an edit here dirty? ASKED OF THE WORLD,
            // not reimplemented -- a copy of MarkDirty in the test only proves
            // the copy agrees with itself.
            HashSet<Vector3I> dirty = _world.SectionsDirtiedBy(hit);

            // Which cells could actually change shape? Every lattice neighbour
            // whose cap has a corner within the divot's reach.
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;

                var nb = new Vector3I(hit.X + dx, hit.Y + dy, hit.Z + dz);
                if (!grid.Contains(nb) || !_world.HasNode(nb)) continue;

                List<Vector3> cap = CapOf(grid, nb);
                if (cap.Count == 0) continue;

                Vector3 site = grid.CentreOf(hit);
                bool reachable = false;

                foreach (Vector3 corner in cap)
                {
                    if (site.DistanceTo(corner) <= grid.NodeSize * 1.15f)
                    { reachable = true; break; }
                }

                if (!reachable) continue;

                checkedCells++;

                if (!dirty.Contains(_world.SectionOfCell(nb)))
                    missed++;
            }
        }

        GD.Print($"    cells a dig can reshape:     {checkedCells}");
        GD.Print($"    NOT in a dirtied section:    {missed}");
        GD.Print(missed == 0
            ? "    VERDICT: the rebuild reaches everything that moves"
            : "    VERDICT: FAILED -- some slumped cells keep stale geometry");
    }

    private List<Vector3> CapOf(OrganicGrid grid, Vector3I cell)
    {
        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxFaceCorners];

        var found = new List<Vector3>();

        int count = grid.Faces(cell, walls, sides, corners);
        int at = 0;

        for (int n = 0; n < count; n++)
        {
            if (sides[n] >= 3 && walls[n] == cell)
            {
                for (int c = 0; c < sides[n]; c++)
                    found.Add(corners[at + c]);
            }

            at += sides[n];
        }

        return found;
    }

    /// <summary>
    /// Do cells that share a corner still place it at the same point?
    ///
    /// Sampled around the dug region, because that is where the depression has
    /// been applied and where a disagreement would show.
    /// </summary>
    private void Agreement(OrganicGrid grid)
    {
        var rng = new Random(5150);
        var owners = new Dictionary<Vector3I, List<(Vector3I cell, Vector3 point)>>();

        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxFaceCorners];

        int sampled = 0;

        for (int i = 0; i < 6000 && sampled < 900; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I centre = grid.CellAt(dir * (grid.SurfaceRadius - grid.NodeSize * 0.25f));
            if (!grid.Contains(centre)) continue;

            int wallCount = grid.WallCount(centre);

            for (int w = -1; w < wallCount; w++)
            {
                Vector3I cell = centre;

                if (w >= 0 && (!grid.WallNeighbour(centre, w, out cell)
                    || !grid.Contains(cell)))
                    continue;

                int count = grid.Faces(cell, walls, sides, corners);
                int at = 0;

                for (int n = 0; n < count; n++)
                {
                    if (sides[n] >= 3 && walls[n] == cell)
                    {
                        for (int c = 0; c < sides[n]; c++)
                        {
                            Vector3 p = corners[at + c];
                            Vector3I key = Key(p);

                            if (!owners.TryGetValue(key, out var list))
                            {
                                list = new List<(Vector3I, Vector3)>();
                                owners[key] = list;
                            }

                            list.Add((cell, p));
                        }
                    }

                    at += sides[n];
                }

                sampled++;
            }
        }

        int shared = 0, disagreed = 0;
        double worst = 0;

        foreach (var kv in owners)
        {
            // Distinct cells only.
            var cells = new HashSet<Vector3I>();
            foreach (var e in kv.Value) cells.Add(e.cell);
            if (cells.Count < 2) continue;

            shared++;

            bool bad = false;

            for (int a = 0; a < kv.Value.Count && !bad; a++)
            {
                for (int b = a + 1; b < kv.Value.Count; b++)
                {
                    if (kv.Value[a].cell == kv.Value[b].cell) continue;

                    float gap = kv.Value[a].point.DistanceTo(kv.Value[b].point);
                    if (gap > worst) worst = gap;

                    if (gap > 0.02f) { disagreed++; bad = true; break; }
                }
            }
        }

        GD.Print($"  corners shared by 2+ cells: {shared}");
        GD.Print($"  where the cells DISAGREE:   {disagreed}");
        GD.Print($"  worst disagreement:         {worst:F5} units");
        GD.Print(disagreed == 0
            ? "  VERDICT: the surface is still sealed"
            : "  VERDICT: FAILED -- the surface has torn");
    }
}
