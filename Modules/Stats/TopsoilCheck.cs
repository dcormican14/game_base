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

    /// <summary>
    /// Extra frames to let the player FALL AND SETTLE before measuring them.
    ///
    /// The world being ready is not the same as the player being at rest. They
    /// spawn with clearance above the ground and are still falling through it
    /// for a good fraction of a second afterwards, so a measurement taken the
    /// instant the streamer reports ready reads whatever radius they happened
    /// to be passing through -- which is how this check once reported the
    /// player standing 2.9 units ABOVE soil they had not landed on yet.
    /// </summary>
    [Export] public int SettleFrames { get; set; } = 180;

    public override void _Process(double delta)
    {
        if (_done) return;
        _frames++;
        bool ready = _streamer == null || _streamer.IsReady;
        if (!ready && _frames < WaitFrames) return;

        // Ready: now give them time to land.
        if (_settling < SettleFrames) { _settling++; return; }

        _done = true;
        Run();
        if (QuitWhenDone) GetTree().Quit(0);
    }

    private int _settling;

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
        // The shell carries its own collider now, so the dirt they see IS the
        // ground they are on and this measures whether they came to rest on top
        // of it. While the shell was decorative they rested on the rock instead
        // and this measured how deeply the dirt buried them.
        GD.Print($"  soil thickness over the rock: {mean - grid.SurfaceRadius:F3} units"
            + $"  ({(mean - grid.SurfaceRadius) / grid.NodeSize:F2} nodes)");

        var player = GetTree().CurrentScene?.GetNodeOrNull<Node3D>("Player");

        if (player is GameBase.Player.PlayerController body)
        {
            float feet = body.GlobalPosition.Length();

            // HOW FAR OFF THE SOIL, not how far off the rock. The shell is what
            // carries collision now, so the surface they should be resting on
            // is the shell's, and the rock is simply what is under it.
            //
            // Measured against the shell's HIGH radius: the player stands on
            // whatever the trimesh presents beneath them, and the lowest vertex
            // anywhere on the planet is not it.
            float standing = feet - high;

            GD.Print("-- the player against the soil --");
            GD.Print($"    feet at radius        {feet:F3}");
            GD.Print($"    soil surface at       {low:F3} .. {high:F3}");
            GD.Print($"    rock surface at       {grid.SurfaceRadius:F3}");
            GD.Print($"    standing proud by     {standing:F3} units"
                + $"  ({standing / grid.NodeSize:F2} nodes)");
            GD.Print($"    still moving at       {body.Velocity.Length():F3} units/s");
            GD.Print($"    on floor:             {body.IsOnFloor()}");

            // On the soil, not in it and not hovering: a body resting on a
            // trimesh sits a hair above it, so a small positive band is right.
            bool resting = body.IsOnFloor() && body.Velocity.Length() < 1f;
            bool onSoil = standing > -0.30f && standing < 1.50f;

            GD.Print(resting && onSoil
                ? "    [PASS] the player stands on top of the soil"
                : standing < -0.30f
                    ? "    [FAIL] the player is sunk into the soil"
                    : !resting
                        ? "    [FAIL] the player never came to rest"
                        : "    [FAIL] the player floats above the soil");
        }

        // IS THE COLLIDER ACTUALLY REGISTERED?
        //
        // Worth asking separately, because the way this fails is silent: a
        // CollisionShape3D parented under the cap instead of under the body
        // sits in the tree looking correct, draws its outline in the editor,
        // and collides with nothing at all. Only the physics server knows, so
        // the physics server is what gets asked.
        if (cap.Collide)
        {
            var space = GetViewport().World3D.DirectSpaceState;
            var rng2 = new Random(2468);

            int cast = 0, hitSoil = 0, hitNothing = 0;
            double deepest = 0;

            for (int i = 0; i < 400 && cast < 200; i++)
            {
                Vector3 dir = new Vector3(
                    (float)(rng2.NextDouble() * 2 - 1),
                    (float)(rng2.NextDouble() * 2 - 1),
                    (float)(rng2.NextDouble() * 2 - 1));

                if (dir.LengthSquared() < 0.01f) continue;
                dir = dir.Normalized();

                // Straight down at the planet, from clear of the shell.
                var query = PhysicsRayQueryParameters3D.Create(
                    dir * (high + 8f), dir * (grid.SurfaceRadius - 6f));

                var hit = space.IntersectRay(query);

                cast++;

                if (hit.Count == 0) { hitNothing++; continue; }

                float r = ((Vector3)hit["position"]).Length();

                // Landing on the shell rather than on the rock under it.
                if (r > grid.SurfaceRadius + 0.5f) hitSoil++;

                double under = high - r;
                if (under > deepest) deepest = under;
            }

            GD.Print("-- the soil as the physics server sees it --");
            GD.Print($"    rays cast:            {cast}");
            GD.Print($"    stopped by the SOIL:  {hitSoil}");
            GD.Print($"    hit nothing at all:   {hitNothing}");
            GD.Print($"    deepest first hit:    {deepest:F3} under the shell top");

            GD.Print(hitNothing == 0 && hitSoil > cast * 0.9
                ? "    [PASS] the shell is solid to the physics server"
                : "    [FAIL] the shell is not collidable where it should be");
        }

        GD.Print("=== END TOPSOIL CHECK ===");
    }
}
