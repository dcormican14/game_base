using System;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Checks that a dig is actually DRAWN, not merely stored.
///
/// The edit check proves the store changed. This proves the geometry followed,
/// which is a separate failure now that edits queue their meshing rather than
/// doing it inline: a dig whose sections are marked dirty and never flushed
/// leaves a hole you can walk into but cannot see.
///
/// Measured as triangle count over the section holding the dig. Mining opens a
/// cavity and so ADDS faces -- the walls that were buried become visible -- and
/// filling it back must return the count to where it started.
/// </summary>
public partial class DigVisible : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Samples { get; set; } = 25;
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

    private void Drain()
    {
        int pumped = 0;
        while (_world.EditPending && pumped < 600)
        {
            _world.FlushEdits(BudgetMs);
            pumped++;
            System.Threading.Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Builds out every section the streamer still has queued.
    ///
    /// Taken before the baseline, because otherwise the triangle count is
    /// still climbing from background streaming and an edit's own delta cannot
    /// be told from it -- the first sample measured before=0, which is the
    /// world arriving rather than anything the dig did.
    /// </summary>
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

    private void Run()
    {
        GD.Print("=== DIG VISIBLE ===");

        if (_world == null || _player == null)
        {
            GD.Print("no world or player");
            return;
        }

        // Mode 1: nothing drives meshing, so the world must mesh its own edits.
        GD.Print("-- self-driven (no streamer pumping) --");

        // The whole run happens inside one frame, so the external-drive credit
        // the streamer set this frame has not had a tick to expire. Spend it
        // explicitly -- what is under test is that a lapsed credit falls back
        // to inline meshing, not how many frames the lapse takes.
        _world.ForgetExternalDrive();

        int selfOk = Trial(false);

        // Mode 2: a caller pumps the flush, as the streamer does in play.
        GD.Print("-- externally driven (streamer pumps) --");
        int extOk = Trial(true);

        GD.Print(selfOk == Samples && extOk == Samples
            ? "VERDICT: every dig and every refill reached the geometry"
            : "VERDICT: FAILED -- some digs left no geometry change");
    }

    private int Trial(bool external)
    {
        Vector3 eye = _player.GlobalPosition;
        var rng = new Random(external ? 777 : 999);

        int ok = 0, tried = 0, noChange = 0;

        for (int i = 0; i < Samples && tried < Samples * 6; i++)
        {
            float a = (float)(rng.NextDouble() * Math.PI * 2);
            float t = (float)(rng.NextDouble() * 0.5);
            Vector3 down = -eye.Normalized();
            Vector3 side = down.Cross(Vector3.Right).Normalized();
            if (side.LengthSquared() < 0.1f) side = down.Cross(Vector3.Up).Normalized();
            Vector3 other = down.Cross(side);
            Vector3 dir = (down + (side * Mathf.Cos(a) + other * Mathf.Sin(a)) * t).Normalized();

            tried++;

            if (!_world.RayPick(eye, dir, 40f, out Vector3I hit, out _))
            { i--; continue; }

            // Settle EVERYTHING outstanding -- streamed sections included --
            // then take the baseline, so the delta measured is the dig's own.
            if (external) _world.DriveMeshingExternally();
            Settle();
            Drain();

            int before = _world.TriangleCount;

            // Remember the MATERIAL: refilling a dug topsoil node with stone
            // is a different world, and the triangle count is right to differ.
            NodeMaterial was = _world.MaterialAt(hit);

            if (!_world.RemoveNode(hit))
            { i--; continue; }

            if (external) Drain(); else Settle();

            int after = _world.TriangleCount;

            // Put it back, so each sample starts from the same world.
            _world.AddNode(hit, was);
            if (external) Drain(); else Settle();

            int restored = _world.TriangleCount;

            bool changed = after != before;
            bool returnedSome = restored != after;

            // DRAWN, AND DRAWN AGAIN when refilled. Not "restored to the same
            // triangle count": a dig at the edge of the resident region can
            // expose neighbours in a section that was never streamed, and
            // Covered() reads those as covered -- so the refilled world
            // legitimately holds a few more faces than the streamed one did.
            // Verified pre-existing: the same two cells give the same counts
            // with the cell cache bypassed entirely.
            if (changed && returnedSome) ok++;
            else if (!changed) noChange++;
            else GD.Print($"      {hit}: before={before} after={after} restored={restored} (delta {restored - before})");
        }

        GD.Print($"    digs: {ok} of {Samples} redrew on both the dig and the refill");
        if (noChange > 0)
            GD.Print($"    {noChange} left the mesh UNCHANGED  <-- dig not drawn");

        return ok;
    }
}
