using System;
using System.Collections.Generic;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Asks the plainest question there is: when a node is mined, do its neighbours
/// CHANGE SHAPE?
///
/// Every other check in this folder asks whether the geometry is correct --
/// closed, not overlapping, not buried, not full of holes. A world that never
/// changes at all passes all of them, which is how a surface that does not
/// respond to mining went unnoticed.
///
/// Measured on the corners themselves, before and after, so it cannot be
/// fooled by a counter that fires while the geometry stays put.
/// </summary>
public partial class ReshapeCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
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

    /// <summary>Every corner of every face of a node, in world space.</summary>
    private List<Vector3> ShapeOf(OrganicGrid grid, Vector3I cell)
    {
        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxFaceCorners];

        var found = new List<Vector3>();

        int count = grid.Faces(cell, walls, sides, corners);
        int at = 0;

        for (int n = 0; n < count; n++)
        {
            for (int c = 0; c < sides[n]; c++)
                found.Add(corners[at + c]);

            at += sides[n];
        }

        return found;
    }

    /// <summary>How far the shape moved, as the largest corner displacement.</summary>
    private static double Moved(List<Vector3> before, List<Vector3> after)
    {
        if (before.Count == 0 || after.Count == 0)
            return 0;

        // Corner counts can change when a face appears or vanishes, which is
        // itself a reshape -- so that counts as movement rather than being
        // compared corner by corner.
        if (before.Count != after.Count)
            return double.MaxValue;

        double worst = 0;

        foreach (Vector3 old in before)
        {
            float nearest = float.MaxValue;

            foreach (Vector3 now in after)
            {
                float gap = old.DistanceTo(now);
                if (gap < nearest) nearest = gap;
            }

            if (nearest > worst) worst = nearest;
        }

        return worst;
    }

    private void Settle()
    {
        int pumped = 0;

        while (_world.HasQueuedMeshes && pumped < 4000)
        {
            _world.FlushQueuedMeshes(64f);
            pumped++;
            System.Threading.Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Does a dig change the TRIANGLES, not just the grid's idea of the shape?
    /// </summary>
    private void Drawn(OrganicGrid grid)
    {
        var rng = new Random(1357);
        float node = grid.NodeSize;
        float radius = grid.SurfaceRadius;

        int digs = 0, changed = 0;
        long totalDelta = 0;

        for (int i = 0; i < Digs * 200 && digs < 20; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I hit = grid.CellAt(dir * (radius - node * 0.25f));

            if (!grid.Contains(hit) || !_world.HasNode(hit)) continue;
            if (!_world.HasChunkMesh(NodeChunkStore.ChunkOf(hit))) continue;

            Settle();
            ulong before = _world.GeometryHash;
            int beforeTris = _world.TriangleCount;

            if (!_world.RemoveNode(hit)) continue;

            Settle();
            ulong after = _world.GeometryHash;
            int afterTris = _world.TriangleCount;

            digs++;

            if (before != after)
            {
                changed++;
                totalDelta += Math.Abs(afterTris - beforeTris);
            }
        }

        if (digs == 0) { GD.Print("    no digs in a meshed chunk"); return; }

        GD.Print($"    digs in meshed chunks:   {digs}");
        GD.Print($"    that changed the mesh:   {changed}"
            + $"  ({100.0 * changed / digs:F1}%)");

        if (changed > 0)
            GD.Print($"    mean triangles changed:  {totalDelta / changed}");

        GD.Print(changed == digs
            ? "    [PASS] every dig redrew"
            : "    [FAIL] some digs did not reach the drawn mesh");
    }

    private void Run()
    {
        GD.Print("=== RESHAPE CHECK ===");

        if (_world?.Grid is not OrganicGrid grid)
        {
            GD.Print("not the organic planet");
            return;
        }

        var rng = new Random(2468);
        float node = grid.NodeSize;
        float radius = grid.SurfaceRadius;

        int digs = 0;
        int withNeighbours = 0;
        int anyMoved = 0;
        int neighboursSeen = 0, neighboursMoved = 0;
        double worstMove = 0, totalMove = 0;

        for (int i = 0; i < Digs * 200 && digs < Digs; i++)
        {
            Vector3 dir = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            if (dir.LengthSquared() < 0.01f) continue;
            dir = dir.Normalized();

            Vector3I hit = grid.CellAt(dir * (radius - node * 0.25f));

            if (!grid.Contains(hit) || !_world.HasNode(hit)) continue;

            // The shapes of everything around it, before.
            var before = new Dictionary<Vector3I, List<Vector3>>();

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;

                var at = new Vector3I(hit.X + dx, hit.Y + dy, hit.Z + dz);

                if (!grid.Contains(at) || !_world.HasNode(at)) continue;

                before[at] = ShapeOf(grid, at);
            }

            if (before.Count == 0) continue;

            if (!_world.RemoveNode(hit)) continue;

            digs++;
            withNeighbours++;

            bool moved = false;

            foreach (var kv in before)
            {
                neighboursSeen++;

                double how = Moved(kv.Value, ShapeOf(grid, kv.Key));

                if (how > 0.001)
                {
                    neighboursMoved++;
                    moved = true;

                    if (how != double.MaxValue)
                    {
                        totalMove += how;
                        if (how > worstMove) worstMove = how;
                    }
                }
            }

            if (moved) anyMoved++;
        }

        if (digs == 0) { GD.Print("  no digs landed"); return; }

        GD.Print($"  digs with solid neighbours:  {digs}");
        GD.Print($"  digs where ANY neighbour changed shape: {anyMoved}"
            + $"  ({100.0 * anyMoved / digs:F1}%)");
        GD.Print($"  neighbours examined:         {neighboursSeen}");
        GD.Print($"  neighbours that changed:     {neighboursMoved}"
            + $"  ({100.0 * neighboursMoved / Math.Max(1, neighboursSeen):F1}%)");

        if (neighboursMoved > 0)
        {
            GD.Print($"  corner movement: mean {totalMove / neighboursMoved:F3}, "
                + $"worst {worstMove:F3} units  (node is {node})");
        }

        GD.Print(anyMoved > 0
            ? "  [PASS] the GRID reshapes the nodes around a dig"
            : "  [FAIL] nothing changes shape -- the surface is inert");

        // AND DOES THAT REACH THE SCREEN?
        //
        // The grid reshaping is only half of it. The mesher has to rebuild the
        // sections holding those nodes, or the new shape exists in the grid and
        // the player still sees the old one. Measured on the uploaded
        // triangles, which is what is actually drawn.
        GD.Print("-- does the drawn mesh follow? --");
        Drawn(grid);

        GD.Print("=== END RESHAPE CHECK ===");
    }
}
