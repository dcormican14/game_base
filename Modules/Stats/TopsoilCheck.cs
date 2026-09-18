using System;
using Godot;
using GameBase.Nodes;
using GameBase.Levels;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Checks the topsoil shell: that it exists, that it is smooth, and that it
/// covers every bit of rock rather than letting cells poke through.
///
/// The node checks cannot see any of this -- the shell is not made of nodes, so
/// as far as they are concerned it is not there. This is the only thing that
/// looks at it.
/// </summary>
public partial class TopsoilCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Samples { get; set; } = 600;
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
        GD.Print("=== TOPSOIL CHECK ===");

        if (_world?.Grid is not OrganicGrid grid)
        {
            GD.Print("not the organic planet");
            return;
        }

        var cap = NodeSearch.FindByType<TopsoilCap>(GetTree().CurrentScene);

        if (cap == null)
        {
            GD.Print("  [FAIL] there is no topsoil shell in the scene");
            return;
        }

        var mesh = cap.GetNodeOrNull<MeshInstance3D>("Soil");

        if (mesh?.Mesh == null)
        {
            GD.Print("  [FAIL] the shell exists but has no mesh");
            return;
        }

        var arrays = mesh.Mesh.SurfaceGetArrays(0);
        var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();

        GD.Print($"  shell: {verts.Length} vertices, {idx.Length / 3} triangles");

        // HOW SMOOTH? The radius of every vertex, against the radius it aims at.
        float low = float.MaxValue, high = float.MinValue;
        double total = 0;

        foreach (Vector3 v in verts)
        {
            float r = v.Length();

            if (r < low) low = r;
            if (r > high) high = r;

            total += r;
        }

        double mean = total / Math.Max(1, verts.Length);

        GD.Print($"  radius: {low:F3} .. {high:F3}, mean {mean:F3}");
        GD.Print($"  roughness (the band it varies in): {high - low:F3} units"
            + $"  ({(high - low) / grid.NodeSize:F3} nodes)");

        // DOES IT COVER THE ROCK? Every exposed node's highest corner must sit
        // below the shell, or that cell pokes through the dirt.
        var rng = new Random(9753);

        Span<Vector3I> walls = stackalloc Vector3I[grid.MaxWalls];
        Span<int> sides = stackalloc int[grid.MaxWalls];
        Span<Vector3> corners = stackalloc Vector3[grid.MaxFaceCorners];

        int checkedCells = 0, poking = 0;
        double worstPoke = 0, clearanceTotal = 0;

        for (int i = 0; i < Samples * 40 && checkedCells < Samples; i++)
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

            float highest = 0;
            int at = 0;

            for (int n = 0; n < count; n++)
            {
                for (int c = 0; c < sides[n]; c++)
                {
                    float r = corners[at + c].Length();
                    if (r > highest) highest = r;
                }

                at += sides[n];
            }

            checkedCells++;

            // The shell's own radius in this direction, taken as its lowest
            // vertex radius -- the worst case for covering.
            float clearance = low - highest;

            clearanceTotal += clearance;

            if (clearance < 0)
            {
                poking++;
                if (-clearance > worstPoke) worstPoke = -clearance;
            }
        }

        if (checkedCells == 0) { GD.Print("  no rock sampled"); return; }

        GD.Print($"  rock nodes checked:       {checkedCells}");
        GD.Print($"  mean clearance under the shell: "
            + $"{clearanceTotal / checkedCells:F3} units");
        GD.Print($"  nodes POKING THROUGH:     {poking}"
            + (poking > 0 ? $"  (worst by {worstPoke:F3} units)" : ""));

        GD.Print(poking == 0
            ? "  [PASS] the shell covers every rock node sampled"
            : "  [FAIL] rock pokes through the topsoil");

        // WHERE DOES THE PLAYER STAND RELATIVE TO IT?
        //
        // The shell has no collision -- deliberately, since that is what stops a
        // decorative layer opening a hole you fall through. But it means the
        // player rests on ROCK while the dirt they see is higher up, and if the
        // gap is more than a shoe's worth they are standing buried in it.
        var player = GetTree().CurrentScene?.GetNodeOrNull<Node3D>("Player");

        if (player != null)
        {
            float feet = player.GlobalPosition.Length();

            GD.Print("-- the player against the soil --");
            GD.Print($"    feet at radius        {feet:F3}");
            GD.Print($"    soil surface at       {low:F3} .. {high:F3}");
            GD.Print($"    buried by             {low - feet:F3} units"
                + $"  ({(low - feet) / grid.NodeSize:F2} nodes)");

            GD.Print(low - feet <= 0.05f
                ? "    [PASS] the player stands on top of the soil"
                : "    [FAIL] the player stands inside the soil");
        }

        GD.Print("=== END TOPSOIL CHECK ===");
    }
}
