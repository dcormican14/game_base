using Godot;
using System;
using System.Threading.Tasks;
using GameBase.Core;
using GameBase.Nodes;
using GameBase.Planets;
using GameBase.Tools;

namespace GameBase.Tests.Capture;

/// <summary>
/// Diagnostic: plays the real level and holds the shovel's Raise for a long
/// time through the real input path -- turning and walking as it goes -- and
/// logs frame times, memory and pending meshing every second, so a stall shows
/// where it starts.
///
///   godot --path . res://Tests/Capture/StressCapture.tscn -- --seconds=90
/// </summary>
public partial class StressCapture : Node
{
    private int _seconds = 90;

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--seconds="))
                _seconds = int.Parse(arg["--seconds=".Length..]);

        _ = Run();
    }

    private async Task Run()
    {
        try
        {
            AddChild(GD.Load<PackedScene>("res://Game/PlanetLevel.tscn").Instantiate());
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
        while (!planet.Streamer.IsReady || world.HasPendingMeshes || planet.Streamer.PendingChunks > 0)
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

        GD.Print($"StressCapture: holding Raise with the {controller.Tool?.Id} for {_seconds}s");
        Input.ActionPress("mine");

        int frames = _seconds * 60;
        double worst = 0;
        ulong last = Time.GetTicksUsec();
        Vector3 origin = body.GlobalPosition;

        for (int frame = 0; frame < frames; frame++)
        {
            // Turn the whole time, look up and down, walk in bursts and jump
            // now and then: raising onto everything from every angle.
            body.RotateObjectLocal(Vector3.Up, 0.012f);
            pc.PitchDegrees = -35f + 25f * Mathf.Sin(frame * 0.013f);
            if (frame % 120 == 0)
                Input.ActionPress("move_forward");
            if (frame % 120 == 90)
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
                    + $"highlight {controller.Highlight.Showing}");
                worst = 0;
            }
        }

        Input.ActionRelease("mine");
        Input.ActionRelease("move_forward");
        GD.Print("StressCapture: done");
    }

    private async Task Frames(int count)
    {
        for (int n = 0; n < count; n++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
