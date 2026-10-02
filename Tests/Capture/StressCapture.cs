using Godot;
using System;
using System.Threading.Tasks;
using GameBase.Core;
using GameBase.Nodes;
using GameBase.Planets;
using GameBase.Terrain;
using GameBase.Tools;

namespace GameBase.Tests.Capture;

/// <summary>
/// Diagnostic: plays the real level and holds the shovel's Raise for a long
/// time through the real input path -- turning and walking as it goes -- and
/// logs frame times, memory and pending meshing every second, so a stall shows
/// where it starts.
///
///   godot --path . res://Tests/Capture/StressCapture.tscn -- --seconds=90 [--seed=1] [--spot=jagged]
///
/// --spot picks where on the planet to stand, as TerrainCapture names them
/// (flat, hills, jagged, rounded, towering, mesa, border). --sprint runs
/// flat out the whole time instead of walking in bursts, with the shovel
/// idle, to see streaming keep up. --fly=SPEED carries the player along the
/// ground at SPEED units a second, 30 units up, with physics off: faster than
/// any run, to see the far terrain and streaming keep up, and whether
/// anything piles up the further it goes.
/// </summary>
public partial class StressCapture : Node
{
    private int _seconds = 90;
    private string _seed = WorldSeed.Default;
    private string _spot;
    private bool _sprint;
    private float _fly;
    private string _shots;
    private int _load;
    private float _height = 30f;
    private int _shotEvery = 300;

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--seconds="))
                _seconds = int.Parse(arg["--seconds=".Length..]);
            else if (arg.StartsWith("--seed="))
                _seed = arg["--seed=".Length..];
            else if (arg.StartsWith("--spot="))
                _spot = arg["--spot=".Length..];
            else if (arg == "--sprint")
                _sprint = true;
            else if (arg.StartsWith("--height="))
                _height = float.Parse(arg["--height=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--shot-every="))
                _shotEvery = int.Parse(arg["--shot-every=".Length..]);
            else if (arg.StartsWith("--load="))
                _load = int.Parse(arg["--load=".Length..]);
            else if (arg.StartsWith("--shots="))
                _shots = arg["--shots=".Length..];
            else if (arg.StartsWith("--fly="))
                _fly = float.Parse(arg["--fly=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        }

        _ = Run();
    }

    private async Task Run()
    {
        try
        {
            WorldSeed.Chosen = _seed;
            Node level = GD.Load<PackedScene>("res://Game/PlanetLevel.tscn").Instantiate();

            if (_spot != null)
            {
                var terrain = new PlanetTerrain(6000f, WorldSeed.Parse(_seed), new TerrainSettings());
                level.GetNode<PlanetSpawn>("PlanetSpawn").Direction = TerrainCapture.Find(terrain, _spot);
            }

            // --load=N: stream N chunks round the player instead (and unload
            // two further out), to compare.
            if (_load > 0)
            {
                var streamer = level.GetNode<Planet>("Planet").GetNode<ChunkStreamer>("Streamer");
                streamer.LoadRadius = _load;
                streamer.UnloadRadius = _load + 2;
            }

            AddChild(level);
            await Stress();
        }
        catch (Exception error)
        {
            GD.PushError($"StressCapture: {error}");
        }

        GetTree().Quit();
    }

    private async Task Stress()
    {
        var planet = NodeSearch.FindByType<Planet>(this);
        NodeWorld world = planet.World;

        ulong start = Time.GetTicksMsec();
        while (!planet.IsReady || world.HasPendingMeshes || planet.Streamer.PendingChunks > 0)
        {
            if (Time.GetTicksMsec() - start > 60000)
                break;
            await Frames(1);
        }

        Node player = GetTree().GetFirstNodeInGroup(Groups.Player);
        var body = (CharacterBody3D)player;
        var controller = NodeSearch.FindByType<ToolController>(player);
        var inventory = NodeSearch.FindByType<GameBase.Items.Inventory>(this);
        var pc = (GameBase.Player.PlayerController)player;

        NodeSearch.FindByType<Camera3D>(player).MakeCurrent();
        Input.MouseMode = Input.MouseModeEnum.Captured;

        if (_fly > 0)
        {
            await Fly(planet, body, (GameBase.Player.PlayerController)player);
            return;
        }
        pc.PitchDegrees = -35f;
        inventory.SelectedIndex = 1;
        await Frames(20);

        for (int n = 0; n < 8 && controller.ModeOf(controller.Tool) != (int)ShovelMode.Raise; n++)
        {
            Input.ActionPress("tool_mode");
            await Frames(2);
            Input.ActionRelease("tool_mode");
            await Frames(2);
        }

        // Sprinting is about streaming keeping up, so the shovel stays idle:
        // its own cost would muddle the frame times.
        if (_sprint)
        {
            GD.Print($"StressCapture: sprinting for {_seconds}s");
        }
        else
        {
            GD.Print($"StressCapture: holding Raise with the {controller.Tool?.Id} for {_seconds}s");
            Input.ActionPress("mine");
        }

        int frames = _seconds * 60;
        double worst = 0;
        ulong last = Time.GetTicksUsec();
        Vector3 origin = body.GlobalPosition;

        for (int frame = 0; frame < frames; frame++)
        {
            // Turn the whole time, look up and down, walk in bursts and jump
            // now and then: raising onto everything from every angle.
            body.RotateObjectLocal(Vector3.Up, _sprint ? 0.002f : 0.012f);
            pc.PitchDegrees = -35f + 25f * Mathf.Sin(frame * 0.013f);
            if (_sprint && frame == 0)
            {
                Input.ActionPress("move_forward");
                Input.ActionPress("sprint");
            }
            else if (!_sprint && frame % 120 == 0)
                Input.ActionPress("move_forward");
            else if (!_sprint && frame % 120 == 90)
                Input.ActionRelease("move_forward");
            if (frame % 300 == 150)
                Input.ActionPress("jump");
            if (frame % 300 == 155)
                Input.ActionRelease("jump");

            await Frames(1);

            ulong now = Time.GetTicksUsec();
            double ms = (now - last) / 1000.0;
            last = now;
            worst = Math.Max(worst, ms);

            if (ms > 200)
                GD.Print($"StressCapture: frame {frame} took {ms:0} ms");

            if (frame % 60 == 0)
            {
                GD.Print($"StressCapture: {frame / 60}s worst {worst:0.0} ms, "
                    + $"mem {OS.GetStaticMemoryUsage() / 1048576.0:0} MB, "
                    + $"pending {world.PendingSections} group {(world.HasPendingMeshes ? "yes" : "no")}, "
                    + $"tris {world.TriangleCount}, chunks {world.Store.ChunkCount}, "
                    + $"height {body.GlobalPosition.DistanceTo(world.GlobalPosition) - origin.DistanceTo(world.GlobalPosition):0.0}, "
                    + $"moved {body.GlobalPosition.DistanceTo(origin):0.0}, "
                    + $"highlight {controller.Highlight.Showing}, "
                    + $"far {planet.Far?.DrawnBlocks}/{planet.Far?.WantedBlocks} pending {planet.Far?.PendingBlocks}");
                worst = 0;
            }
        }

        Input.ActionRelease("mine");
        Input.ActionRelease("move_forward");
        Input.ActionRelease("sprint");
        GD.Print("StressCapture: done");
    }

    /// <summary>Carries the player round the planet at a steady speed, logging every second.</summary>
    private async Task Fly(Planet planet, CharacterBody3D body, GameBase.Player.PlayerController pc)
    {
        GD.Print($"StressCapture: flying at {_fly} units a second for {_seconds}s");
        body.SetPhysicsProcess(false);
        pc.PitchDegrees = -10f;

        NodeWorld world = planet.World;
        Vector3 up = planet.UpAt(body.GlobalPosition);
        Vector3 heading = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();

        int frames = _seconds * 60;
        double worst = 0, total = 0;
        int count = 0;
        ulong last = Time.GetTicksUsec();
        float travelled = 0;

        for (int frame = 0; frame < frames; frame++)
        {
            // A steady pace per frame, whatever the frame took: the ground
            // covered is what matters, not how long it took.
            float step = _fly / 60f;
            up = planet.UpAt(body.GlobalPosition);
            heading = (heading - up * heading.Dot(up)).Normalized();
            Vector3 along = planet.GlobalPosition + (body.GlobalPosition - planet.GlobalPosition + heading * step).Normalized()
                * (body.GlobalPosition - planet.GlobalPosition).Length();
            Vector3 newUp = planet.UpAt(along);
            Vector3 ground = planet.SurfacePoint(newUp);
            body.GlobalPosition = ground + newUp * _height;
            body.LookAt(body.GlobalPosition + heading, newUp);
            body.RotateObjectLocal(Vector3.Up, Mathf.Pi);
            travelled += step;

            await Frames(1);

            // A picture every five seconds, looking down the way ahead.
            if (_shots != null && frame % _shotEvery == _shotEvery - 1)
            {
                DirAccess.MakeDirRecursiveAbsolute(_shots);
                GetViewport().GetTexture().GetImage().SavePng($"{_shots}/fly_{frame + 1:0000}.png");
            }

            ulong now = Time.GetTicksUsec();
            double ms = (now - last) / 1000.0;
            last = now;
            worst = Math.Max(worst, ms);
            total += ms;
            count++;

            if (frame % 60 == 59)
            {
                FarTerrain far = planet.Far;
                GD.Print($"StressCapture: {(frame + 1) / 60}s at {travelled:0} units: "
                    + $"avg {total / count:0.0} ms, worst {worst:0.0} ms, "
                    + $"draws {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame)}, "
                    + $"tris {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame) / 1000}K, "
                    + $"mem {OS.GetStaticMemoryUsage() / 1048576.0:0} MB, "
                    + $"far {far?.DrawnBlocks} drawn/{far?.WantedBlocks} wanted/{far?.HeldBlocks} held, {far?.PendingBlocks} pending, "
                    + $"chunks {world.Store.ChunkCount} ({planet.Streamer.PendingChunks} pending), sections {world.PendingSections}, "
                    + $"nodes {GetTree().GetNodeCount()}");
                worst = 0;
                total = 0;
                count = 0;
            }
        }

        GD.Print("StressCapture: done");
    }

    private async Task Frames(int count)
    {
        for (int n = 0; n < count; n++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
