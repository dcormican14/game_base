using System;
using Godot;
using GameBase.Nodes;
using GameBase.Levels;
using GameBase.Core;
using GameBase.Player;

namespace GameBase.Stats;

/// <summary>
/// Checks that the shovel moves sand, that the ground the player stands on
/// moves with it, and that a dig cannot reach the rock underneath.
///
/// THE COLLIDER IS THE POINT. A height field that changed only the mesh would
/// pass every look at the geometry while the player walked on the shape of the
/// ground as it was before they dug -- a dig you can see and not stand in. So
/// this asks the PHYSICS SERVER where the surface is, before and after, rather
/// than asking the mesh.
/// </summary>
public partial class ShovelCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public bool QuitWhenDone { get; set; } = true;

    private ChunkStreamer _streamer;
    private int _frames;
    private bool _done;

    public override void _Ready()
    {
        _streamer = NodeSearch.FindByType<ChunkStreamer>(GetTree().CurrentScene);
    }

    public override void _Process(double delta)
    {
        if (_done) return;
        _frames++;
        bool ready = _streamer == null || _streamer.IsReady;
        if (!ready && _frames < WaitFrames) return;
        _done = true;
        Run();
    }

    /// <summary>Where the collider's surface is along a direction, or -1.</summary>
    private float SurfaceAt(Vector3 dir, float from, float to)
    {
        var query = PhysicsRayQueryParameters3D.Create(dir * from, dir * to);

        Godot.Collections.Dictionary hit =
            GetViewport().World3D.DirectSpaceState.IntersectRay(query);

        return hit.Count == 0 ? -1f : ((Vector3)hit["position"]).Length();
    }

    /// <summary>
    /// Lets a physics frame pass.
    ///
    /// A COLLIDER ADDED THIS FRAME IS NOT YET REGISTERED with the physics
    /// server, so a ray cast straight after a dig reports empty space where
    /// the new ground is. In the game the dig and the next step are frames
    /// apart and this never arises; in a check that does both in one call it
    /// has to be waited for explicitly.
    /// </summary>
    private Godot.SignalAwaiter Step()
        => ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

    private async void Run()
    {
        GD.Print("=== SHOVEL CHECK ===");

        var cap = NodeSearch.FindByType<TopsoilCap>(GetTree().CurrentScene);

        if (cap == null)
        {
            GD.Print("  [FAIL] no topsoil in the scene");
            return;
        }

        var dir = new Vector3(0.31f, 0.78f, -0.54f).Normalized();

        float rock = 120f;
        float high = rock + cap.Depth + cap.MaxHeight + 6f;
        float low = rock - 4f;

        await Step();

        float before = SurfaceAt(dir, high, low);

        GD.Print($"  surface before digging: {before:F3}");

        if (before < 0f)
        {
            GD.Print("  [FAIL] nothing solid to dig");
            return;
        }

        // -- LOWERING ------------------------------------------------------
        bool moved = cap.Sculpt(dir * before, 4f, -2f);

        await Step();

        GD.Print($"  Sculpt(-2) reported movement: {moved}");

        float dug = SurfaceAt(dir, high, low);

        GD.Print($"  surface after lowering:  {dug:F3}"
            + $"  (moved {dug - before:F3})");

        GD.Print(dug >= 0f && dug < before - 0.25f
            ? "  [PASS] the ground you stand on dropped"
            : "  [FAIL] the collider did not follow the dig");

        // -- RAISING -------------------------------------------------------
        cap.Sculpt(dir * before, 4f, 4f);

        await Step();

        float piled = SurfaceAt(dir, high, low);

        GD.Print($"  surface after raising:   {piled:F3}"
            + $"  (moved {piled - dug:F3})");

        GD.Print(piled > dug + 0.25f
            ? "  [PASS] the ground rose"
            : "  [FAIL] raising did not lift the collider");

        // -- THE FALLOFF ---------------------------------------------------
        //
        // A dig has to fade to nothing at its rim, or it leaves a cylinder
        // punched into the sand with walls no sand could hold.
        var axis = Mathf.Abs(dir.Y) < 0.9f ? Vector3.Up : Vector3.Right;
        Vector3 east = axis.Cross(dir).Normalized();

        GD.Print("-- how the dig falls off from its centre --");

        float last = float.MaxValue;
        bool monotonic = true;

        for (int i = 0; i <= 4; i++)
        {
            float outAt = i * 1.5f;
            float arc = outAt / before;

            Vector3 sample =
                (dir * Mathf.Cos(arc) + east * Mathf.Sin(arc)).Normalized();

            float here = SurfaceAt(sample, high, low);

            GD.Print($"    {outAt:F1} units out: surface {here:F3}");

            if (here > last + 0.05f) monotonic = false;

            last = here;
        }

        GD.Print(monotonic
            ? "  [PASS] the mound falls away smoothly from its centre"
            : "  [FAIL] the dig has a lip or a wall at its rim");

        // -- THE FLOOR -----------------------------------------------------
        //
        // Digging must stop before it reaches the rock. Past that the stone
        // pokes through the sand and the player stands on rock while looking
        // into a hole.
        for (int i = 0; i < 12; i++)
            cap.Sculpt(dir * before, 4f, -cap.Depth);

        await Step();

        float floor = SurfaceAt(dir, high, low);

        GD.Print($"  after digging hard {12} times: surface {floor:F3}");
        GD.Print($"    the rock is at {rock:F3}, the cap allows {cap.MaxDepth:F3} down");

        GD.Print(floor > rock + 0.05f
            ? "  [PASS] the dig stopped above the rock"
            : "  [FAIL] the dig cut through to the stone");

        GD.Print("=== END SHOVEL CHECK ===");

        if (QuitWhenDone) GetTree().Quit(0);
    }
}
