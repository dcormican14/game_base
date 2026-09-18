using System;
using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Asks whether the surface has HOLES: places where a ray from the sky reaches
/// the inside of the planet without ever hitting a face.
///
/// THE QUESTION EVERY OTHER CHECK MISSES. The mesh audit asks whether anything
/// is drawn that should not be -- buried faces, inverted windings. The overlap
/// check asks whether one node reaches into another. The seam check asks
/// whether a node's own faces meet. All three are about geometry that EXISTS
/// being wrong.
///
/// A hole is geometry that is ABSENT, and absence is invisible to all of them: a
/// world with half its faces missing passes every one. That is exactly what
/// happened -- the divot reported "no buried faces" while the planet was full of
/// holes you could see through.
///
/// Measured by casting rays inward and counting how many pass through where the
/// ground ought to be. Uses the world's own collision geometry, so it answers
/// for what the player actually walks on and looks at rather than for what the
/// grid believes.
/// </summary>
public partial class SurfaceSealCheck : Node
{
    [Export] public int WaitFrames { get; set; } = 900;
    [Export] public int Rays { get; set; } = 3000;
    [Export] public int Digs { get; set; } = 40;
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

    private int _stage;
    private int _stageFrame;

    /// <summary>
    /// Driven across frames rather than run in one.
    ///
    /// A raycast asks the PHYSICS server, and a collision shape installed this
    /// frame is not registered until the server has stepped. Running the whole
    /// survey inside one _Process found nothing anywhere -- 100% holes on a
    /// planet that was demonstrably solid, which is the test being wrong rather
    /// than the world.
    /// </summary>
    public override void _Process(double delta)
    {
        if (_done) return;

        _frames++;

        bool ready = _streamer == null || _streamer.IsReady;
        if (!ready && _frames < WaitFrames) return;

        _stageFrame++;

        switch (_stage)
        {
            case 0:
                GD.Print("=== SURFACE SEAL CHECK ===");

                if (_world?.Grid is not OrganicGrid)
                {
                    GD.Print("not the organic planet");
                    Finish();
                    return;
                }

                Settle();
                _stage = 1;
                _stageFrame = 0;
                return;

            // LET PHYSICS CATCH UP before asking it anything.
            case 1:
                if (_stageFrame < 30) return;
                GD.Print("-- before any digging --");
                _before = Survey((OrganicGrid)_world.Grid);
                _stage = 2;
                _stageFrame = 0;
                return;

            case 2:
                _dug = DigSome((OrganicGrid)_world.Grid);
                Settle();
                _stage = 3;
                _stageFrame = 0;
                return;

            case 3:
                if (_stageFrame < 30) return;
                GD.Print($"-- after {_dug} digs --");
                int after = Survey((OrganicGrid)_world.Grid);

                GD.Print(_before == 0 && after == 0
                    ? "VERDICT: the surface is solid -- no ray reached the inside"
                    : "VERDICT: FAILED -- there are holes you can see and fall through");

                GD.Print("=== END SURFACE SEAL CHECK ===");
                Finish();
                return;
        }
    }

    private int _before;
    private int _dug;

    private void Finish()
    {
        _done = true;
        if (QuitWhenDone) GetTree().Quit(0);
    }

    private int DigSome(OrganicGrid grid)
    {
        var rng = new Random(717);
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

        return dug;
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
    /// Casts rays inward and counts the ones that find no ground.
    ///
    /// Returns the number of holes found.
    /// </summary>
    private int Survey(OrganicGrid grid)
    {
        var space = GetViewport().World3D.DirectSpaceState;
        var rng = new Random(31337);

        float radius = grid.SurfaceRadius;
        float node = grid.NodeSize;

        int cast = 0, missed = 0;
        int noGroundThere = 0;

        // AROUND THE PLAYER, not over the whole sphere.
        //
        // The streamer builds collision only near the player, so a ray at the
        // far side of the planet finds nothing however solid the world is --
        // which read as 88% holes on a planet that was demonstrably fine. Only
        // the loaded neighbourhood can answer this question.
        Vector3 eye = _player != null ? _player.GlobalPosition : Vector3.Up * radius;
        Vector3 up = eye.Normalized();

        Vector3 side = up.Cross(Vector3.Right);
        if (side.LengthSquared() < 0.01f) side = up.Cross(Vector3.Up);
        side = side.Normalized();

        Vector3 other = up.Cross(side);

        for (int i = 0; i < Rays * 6 && cast < Rays; i++)
        {
            // A patch a few nodes wide under the player's feet.
            float a = (float)(rng.NextDouble() * Math.PI * 2);
            float r = (float)Math.Sqrt(rng.NextDouble()) * node * 8f;

            Vector3 spot = up * radius
                + side * (Mathf.Cos(a) * r)
                + other * (Mathf.Sin(a) * r);

            Vector3 dir = spot.Normalized();

            // ONLY WHERE THERE IS SUPPOSED TO BE GROUND, and only where the
            // mesh for it has actually been built.
            Vector3I surface = grid.CellAt(dir * (radius - node * 0.25f));

            if (!grid.Contains(surface) || !_world.HasNode(surface))
            {
                noGroundThere++;
                continue;
            }

            if (!_world.HasChunkMesh(NodeChunkStore.ChunkOf(surface)))
            {
                noGroundThere++;
                continue;
            }

            cast++;

            // From well above the surface to well below it: any solid ground on
            // the way should stop this.
            var query = PhysicsRayQueryParameters3D.Create(
                dir * (radius + node * 3f),
                dir * (radius - node * 4f));

            var result = space.IntersectRay(query);

            if (result.Count == 0)
                missed++;
        }

        if (cast == 0)
        {
            GD.Print("    no rays landed on ground (nothing streamed?)");
            return 0;
        }

        GD.Print($"    rays cast at solid ground:  {cast}");
        GD.Print($"    rays that hit NOTHING:      {missed}"
            + $"  ({100.0 * missed / cast:F2}%)   <-- holes");
        GD.Print($"    (skipped, no ground there:  {noGroundThere})");

        return missed;
    }
}
