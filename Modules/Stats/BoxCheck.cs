using System;
using System.Collections.Generic;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Asks which of a dug cell's 26 neighbours actually change shape.
///
/// The intent is that mining a node reshapes the whole 3x3x3 box around it --
/// faces, edges and corners alike. The enumeration already walks all 26, but
/// walking a neighbour is not the same as moving it: a corner neighbour's site
/// sits root-three, about 1.73 nodes, away from the dug one, so whether it
/// moves depends on the reach the depression is given.
///
/// Reported by the KIND of neighbour, because that is the question: a box that
/// reshapes its six faces and nothing else is a plus sign, not a box.
/// </summary>
public partial class BoxCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
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

    /// <summary>Face, edge or corner of the 3x3x3 box.</summary>
    private static int KindOf(int dx, int dy, int dz)
        => Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);

    private void Run()
    {
        GD.Print("=== BOX CHECK ===");

        if (_world?.Grid is not OrganicGrid grid)
        {
            GD.Print("not the organic planet");
            return;
        }

        var rng = new Random(8888);

        // Counted per kind: how many were solid surface nodes that COULD move,
        // and how many actually did.
        var couldMove = new int[4];
        var didMove = new int[4];
        var totalDrop = new double[4];

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
            if (CornersOf(grid, hit).Count == 0) continue;

            // Every neighbour's shape before the dig.
            var before = new Dictionary<Vector3I, List<Vector3>>();

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;

                var at = new Vector3I(hit.X + dx, hit.Y + dy, hit.Z + dz);

                if (!grid.Contains(at) || !_world.HasNode(at)) continue;

                List<Vector3> shape = CornersOf(grid, at);
                if (shape.Count == 0) continue;

                before[at] = shape;
            }

            if (before.Count == 0) continue;
            if (!_world.RemoveNode(hit)) continue;

            dug++;

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;

                var at = new Vector3I(hit.X + dx, hit.Y + dy, hit.Z + dz);
                if (!before.TryGetValue(at, out List<Vector3> was)) continue;

                int kind = KindOf(dx, dy, dz);
                couldMove[kind]++;

                List<Vector3> now = CornersOf(grid, at);

                double worst = 0;

                foreach (Vector3 old in was)
                {
                    float bestGap = float.MaxValue;
                    float bestDrop = 0;

                    foreach (Vector3 fresh in now)
                    {
                        float gap = old.Normalized().DistanceTo(fresh.Normalized())
                            * old.Length();

                        if (gap < bestGap)
                        {
                            bestGap = gap;
                            bestDrop = old.Length() - fresh.Length();
                        }
                    }

                    if (bestGap > grid.NodeSize * 0.4f) continue;
                    if (bestDrop > worst) worst = bestDrop;
                }

                if (worst > 0.001)
                {
                    didMove[kind]++;
                    totalDrop[kind] += worst;
                }
            }
        }

        GD.Print($"  digs: {dug}");
        GD.Print("  -- of the 26 neighbours, which reshape? --");

        string[] name = { "", "face  (6 of 26)", "edge  (12 of 26)", "corner (8 of 26)" };

        for (int kind = 1; kind <= 3; kind++)
        {
            if (couldMove[kind] == 0)
            {
                GD.Print($"    {name[kind],-18} none sampled");
                continue;
            }

            double share = 100.0 * didMove[kind] / couldMove[kind];
            double mean = didMove[kind] > 0 ? totalDrop[kind] / didMove[kind] : 0;

            GD.Print($"    {name[kind],-18} {didMove[kind],4} of {couldMove[kind],4} moved"
                + $"  ({share,5:F1}%)   mean drop {mean:F3}");
        }

        int anyCould = couldMove[1] + couldMove[2] + couldMove[3];
        int anyDid = didMove[1] + didMove[2] + didMove[3];

        GD.Print($"  overall: {anyDid} of {anyCould} "
            + $"({100.0 * anyDid / Math.Max(1, anyCould):F1}%)");

        // IS EVERY MOVED NEIGHBOUR ALSO REBUILT?
        //
        // MarkDirty filters the box down to cells that can really change, and
        // a wider reach can outrun that filter -- a cell that now slumps but
        // is not marked keeps its old geometry and its old collision.
        GD.Print("  -- and is each one marked for rebuild? --");
        Rebuilt(grid);

        bool box = couldMove[1] > 0 && couldMove[2] > 0 && couldMove[3] > 0
            && didMove[1] * 2 > couldMove[1]
            && didMove[2] * 2 > couldMove[2]
            && didMove[3] * 2 > couldMove[3];

        GD.Print(box
            ? "  VERDICT: faces, edges and corners all reshape -- it is a box"
            : "  VERDICT: not a box -- some kinds of neighbour never move");

        GD.Print("=== END BOX CHECK ===");
    }

    /// <summary>
    /// Does the world rebuild every neighbour the depression can move?
    /// </summary>
    private void Rebuilt(OrganicGrid grid)
    {
        var rng = new Random(4747);
        int moved = 0, missed = 0;

        for (int i = 0; i < 4000 && moved < 300; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I hit = grid.CellAt(dir * (grid.SurfaceRadius - grid.NodeSize * 0.25f));
            if (!grid.Contains(hit) || !_world.HasNode(hit)) continue;

            HashSet<Vector3I> dirty = _world.SectionsDirtiedBy(hit);

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;

                var at = new Vector3I(hit.X + dx, hit.Y + dy, hit.Z + dz);
                if (!grid.Contains(at) || !_world.HasNode(at)) continue;

                // Only a cell with a cap can slump.
                if (CornersOf(grid, at).Count == 0) continue;

                moved++;

                if (!dirty.Contains(_world.SectionOfCell(at)))
                    missed++;
            }
        }

        GD.Print($"    neighbours that can slump:  {moved}");
        GD.Print($"    NOT marked for rebuild:     {missed}");
        GD.Print(missed == 0
            ? "    every one is rebuilt"
            : "    FAILED -- some keep stale geometry and collision");
    }

    /// <summary>Every corner of every face of a node, in world space.</summary>
    private List<Vector3> CornersOf(OrganicGrid grid, Vector3I cell)
    {
        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxFaceCorners];

        var found = new List<Vector3>();

        int count = grid.Faces(cell, walls, sides, corners);
        int at = 0;

        for (int n = 0; n < count; n++)
        {
            // THE CAP ONLY, because that is the surface the divot moves and
            // the thing a player sees slump.
            if (sides[n] >= 3 && walls[n] == cell)
            {
                for (int c = 0; c < sides[n]; c++)
                    found.Add(corners[at + c]);
            }

            at += sides[n];
        }

        return found;
    }
}
