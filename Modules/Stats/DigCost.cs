using System;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Measures what a dig COSTS the frame it happens on, and how long until the
/// hole is drawn.
///
/// Two numbers, because they trade against each other. The edit itself should
/// be near free -- it is a store write and a handful of dictionary adds. The
/// geometry then follows under the mesh budget, and what matters there is that
/// no single frame goes long, not that the work finishes in one.
///
/// Run against the streaming planet, in the shape the player pays it: one dig,
/// then frames pumped until the world is quiet again.
/// </summary>
public partial class DigCost : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Samples { get; set; } = 60;
    [Export] public float BudgetMs { get; set; } = 4f;
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

    private void Run()
    {
        GD.Print("=== DIG COST ===");

        if (_world == null || _player == null)
        {
            GD.Print("no world or player");
            return;
        }

        // The streamer owns meshing from here, exactly as in play.
        _world.DriveMeshingExternally();


        Vector3 eye = _player.GlobalPosition;
        var rng = new Random(20260917);

        var editMs = new double[Samples];
        var worstFrameMs = new double[Samples];
        var framesToDraw = new double[Samples];

        double editTotal = 0, frameTotal = 0, drawTotal = 0;
        double editWorst = 0, frameWorst = 0, drawWorst = 0;
        int digs = 0;

        // One warm dig before measuring: the first call through this path JITs
        // the mesher, which is a one-off at startup and not what a dig costs.
        Warm(eye, rng);

        for (int i = 0; i < Samples; i++)
        {
            float a = (float)(rng.NextDouble() * Math.PI * 2);
            float t = (float)(rng.NextDouble() * 0.5);
            Vector3 dir = Aim(eye, a, t);

            if (!_world.RayPick(eye, dir, 40f, out Vector3I hit, out _))
                continue;

            // SETTLE FIRST. A player does not mine sixty times in sixty
            // consecutive frames; digging into a queue this benchmark itself
            // created measures contention rather than what a dig costs.
            int quiet = 0;
            while (_world.HasQueuedMeshes && quiet < 2000)
            {
                _world.FlushQueuedMeshes(BudgetMs);
                quiet++;
                System.Threading.Thread.Sleep(1);
            }

            ulong t0 = Time.GetTicksUsec();
            bool removed = _world.RemoveNode(hit);
            ulong t1 = Time.GetTicksUsec();

            if (!removed) continue;

            double edit = (t1 - t0) / 1000.0;

            // Pump frames until the geometry has caught up, timing each.
            double worstFrame = 0;
            int pumped = 0;

            while (_world.EditPending && pumped < 600)
            {
                ulong f0 = Time.GetTicksUsec();
                _world.FlushEdits(BudgetMs);
                ulong f1 = Time.GetTicksUsec();

                double frame = (f1 - f0) / 1000.0;
                if (frame > worstFrame) worstFrame = frame;
                pumped++;

                // A frame is not a spin: the workers this dispatched run on the
                // thread pool, and hammering FlushEdits in a tight loop starves
                // the very tasks it is waiting for. Standing aside briefly is
                // what a real frame does for free.
                System.Threading.Thread.Sleep(1);
            }

            editMs[digs] = edit;
            worstFrameMs[digs] = worstFrame;
            framesToDraw[digs] = pumped;

            editTotal += edit; frameTotal += worstFrame; drawTotal += pumped;
            if (edit > editWorst) editWorst = edit;
            if (worstFrame > frameWorst) frameWorst = worstFrame;
            if (pumped > drawWorst) drawWorst = pumped;

            digs++;
        }

        if (digs == 0) { GD.Print("no digs landed"); return; }

        Array.Sort(editMs, 0, digs);
        Array.Sort(worstFrameMs, 0, digs);
        Array.Sort(framesToDraw, 0, digs);

        GD.Print($"  digs measured:            {digs}");
        GD.Print("  -- the edit itself, on the frame the player clicks --");
        GD.Print($"    mean:    {editTotal / digs:F3} ms");
        GD.Print($"    median:  {editMs[digs / 2]:F3} ms");
        GD.Print($"    p90:     {editMs[(int)(digs * 0.9)]:F3} ms");
        GD.Print($"    worst:   {editWorst:F3} ms");
        GD.Print($"  -- worst single meshing frame after that dig --");
        GD.Print($"    mean:    {frameTotal / digs:F2} ms   (budget {BudgetMs} ms)");
        GD.Print($"    median:  {worstFrameMs[digs / 2]:F2} ms");
        GD.Print($"    p90:     {worstFrameMs[(int)(digs * 0.9)]:F2} ms");
        GD.Print($"    worst:   {frameWorst:F2} ms");
        GD.Print("  -- frames until the hole is drawn --");
        GD.Print($"    median:  {framesToDraw[digs / 2]:F0}");
        GD.Print($"    p90:     {framesToDraw[(int)(digs * 0.9)]:F0}");
        GD.Print($"    mean:    {drawTotal / digs:F1}");
        GD.Print($"    worst:   {drawWorst:F0}");
        GD.Print("  60 fps frame is 16.67 ms");
    }

    private Vector3 Aim(Vector3 eye, float a, float t)
    {
        Vector3 down = -eye.Normalized();
        Vector3 side = down.Cross(Vector3.Right).Normalized();
        if (side.LengthSquared() < 0.1f) side = down.Cross(Vector3.Up).Normalized();
        Vector3 other = down.Cross(side);
        return (down + (side * Mathf.Cos(a) + other * Mathf.Sin(a)) * t).Normalized();
    }

    private void Warm(Vector3 eye, Random rng)
    {
        Vector3 dir = Aim(eye, 0.3f, 0.2f);

        if (!_world.RayPick(eye, dir, 40f, out Vector3I hit, out _))
            return;

        _world.RemoveNode(hit);

        int pumped = 0;
        while (_world.EditPending && pumped < 600)
        {
            _world.FlushEdits(BudgetMs);
            pumped++;
            System.Threading.Thread.Sleep(1);
        }
    }
}
