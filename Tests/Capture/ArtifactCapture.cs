using Godot;
using System;
using System.Threading.Tasks;
using GameBase.Core;
using GameBase.Nodes;
using GameBase.Planets;
using GameBase.Tools;

namespace GameBase.Tests.Capture;

/// <summary>
/// Diagnostic: shoots the ground while turning in tiny steps, with a magenta
/// background (cracks show as magenta) and with the moonlight's shadows on and
/// off (acne comes and goes with them).
///
///   godot --path . res://Tests/Capture/ArtifactCapture.tscn -- --out=C:/some/folder
/// </summary>
public partial class ArtifactCapture : Node
{
    private string _out = "user://artifacts";

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--out="))
                _out = arg["--out=".Length..];
        }

        DirAccess.MakeDirRecursiveAbsolute(_out);
        _ = Run();
    }

    private async Task Run()
    {
        try
        {
            AddChild(GD.Load<PackedScene>("res://Game/PlanetLevel.tscn").Instantiate());
            await Shots();
        }
        catch (Exception error)
        {
            GD.PushError($"ArtifactCapture: {error}");
        }

        GetTree().Quit();
    }

    private async Task Shots()
    {
        var planet = NodeSearch.FindByType<Planet>(this);
        NodeWorld world = planet.World;

        foreach (Node node in GetTree().GetNodesInGroup(Groups.Player))
            NodeSearch.FindByType<ToolController>(node).ProcessMode = ProcessModeEnum.Disabled;

        var camera = new Camera3D { Fov = 75f, Far = 4000f };
        AddChild(camera);

        ulong start = Time.GetTicksMsec();
        while (!planet.Streamer.IsReady || world.HasPendingMeshes || planet.Streamer.PendingChunks > 0)
        {
            if (Time.GetTicksMsec() - start > 60000)
                break;
            await Frames(1);
        }

        camera.MakeCurrent();

        var filter = NodeSearch.FindByType<GameBase.Filters.StylizedFilter>(this);
        var rig = NodeSearch.FindByType<GameBase.Lighting.LightingRig>(this);
        Godot.Environment environment = NodeSearch.FindByType<GameBase.World.Skybox>(this)?.Environment;

        Vector3 up = Vector3.Up;
        Vector3 ground = planet.SurfacePoint(up);
        Vector3 eye = ground + up * 1.7f;

        foreach (string arg in OS.GetCmdlineUserArgs())
            if (arg == "--magenta" && environment != null)
            {
                environment.BackgroundMode = Godot.Environment.BGMode.Color;
                environment.BackgroundColor = new Color(1, 0, 1);
            }

        for (int pass = 0; pass < 4; pass++)
        {
            bool shadows = pass % 2 == 0;
            bool pixelate = pass >= 2;

            rig.Moonlight.ShadowEnabled = shadows;
            if (filter != null)
                filter.Visible = pixelate;

            for (int n = 0; n < 6; n++)
            {
                float yaw = Mathf.DegToRad(20f + n * 0.37f);
                Vector3 look = new Vector3(Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw)) * 10f - up * 3f;
                camera.GlobalPosition = eye;
                camera.LookAt(eye + look, Vector3.Up);
                await Frames(4);

                string name = $"{(shadows ? "shadow" : "noshadow")}_{(pixelate ? "px" : "raw")}_{n}";
                GetViewport().GetTexture().GetImage().SavePng($"{_out}/{name}.png");
            }
        }

        GD.Print("ArtifactCapture: done");
    }

    private async Task Frames(int count)
    {
        for (int n = 0; n < count; n++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
