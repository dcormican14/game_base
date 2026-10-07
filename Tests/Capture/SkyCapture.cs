using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using GameBase.Core;
using GameBase.Planets;
using GameBase.Terrain;
using GameBase.World;

namespace GameBase.Tests.Capture;

/// <summary>
/// Pictures of the day-night sky at the Day-Night spec's checkpoints. It plays
/// the real level at a spot, stops the clock, and for each of the spec's hours
/// turns the planet until the sky is drawn at that hour where the player
/// stands. It then photographs toward the sun's side of the horizon, away from
/// it, and overhead.
///
///   godot --path . res://Tests/Capture/SkyCapture.tscn -- --out=C:/shots [--spot=flat] [--hours=5.5,12.5]
///   godot --path . res://Tests/Capture/SkyCapture.tscn -- --out=C:/shots --sweep=6.9
///
/// With --sweep=HOUR it instead faces the player toward the sun at that hour
/// and tilts the player's own camera from 20 degrees above the horizon to 45
/// below, a degree a frame, logging the camera, the time of day and the
/// sky's colour at every step, and saving each frame: for finding anything
/// that changes as the view tilts.
///
/// Checkpoints by default:
///   5:30 blue hour; 6:30 dawn glow and the belt opposite; 12:30 midday;
///   18:15 sunset; 18:55 purple light; 22:00 night; 23:59 and 0:00 across
///   the loop.
/// Not headless: it needs the renderer.
/// </summary>
public partial class SkyCapture : Node
{
    private string _out = "user://sky";
    private string _spot = "flat";
    private string _seed = WorldSeed.Default;
    private float[] _hours = { 5.5f, 6.5f, 12.5f, 18.25f, 18.92f, 22f, 23.98f, 0.02f };
    private float _sweep = float.NaN;
    private float _watch = float.NaN;
    private float _pitch = -10f;
    private bool _turn;

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--out="))
                _out = arg["--out=".Length..];
            else if (arg.StartsWith("--spot="))
                _spot = arg["--spot=".Length..];
            else if (arg.StartsWith("--watch="))
                _watch = float.Parse(arg["--watch=".Length..], CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--seed="))
                _seed = arg["--seed=".Length..];
            else if (arg == "--turn")
                _turn = true;
            else if (arg.StartsWith("--pitch="))
                _pitch = float.Parse(arg["--pitch=".Length..], CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--sweep="))
                _sweep = float.Parse(arg["--sweep=".Length..], CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--hours="))
                _hours = Array.ConvertAll(arg["--hours=".Length..].Split(','), h => float.Parse(h, CultureInfo.InvariantCulture));
        }

        _ = Run();
    }

    private async Task Run()
    {
        int code = 0;

        try
        {
            DirAccess.MakeDirRecursiveAbsolute(_out);
            WorldSeed.Chosen = _seed;

            // "spawn" keeps the level's own spawn point.
            Node level = GD.Load<PackedScene>("res://Game/PlanetLevel.tscn").Instantiate();
            if (_spot != "spawn")
            {
                var terrain = new PlanetTerrain(6000f, WorldSeed.Parse(_seed), new TerrainSettings());
                level.GetNode<PlanetSpawn>("PlanetSpawn").Direction = TerrainCapture.Find(terrain, _spot);
            }
            AddChild(level);

            var planet = NodeSearch.FindByType<Planet>(level);
            var cycle = NodeSearch.FindByType<DayCycle>(level);
            ulong start = Time.GetTicksMsec();
            while (!planet.IsReady && Time.GetTicksMsec() - start < 180_000)
                await Frames(1);

            await Frames(40);

            if (_turn)
            {
                await Turn(cycle);
                GetTree().Quit(0);
                return;
            }

            cycle.Running = false;

            if (!float.IsNaN(_watch))
            {
                await Watch(cycle, level);
                GetTree().Quit(0);
                return;
            }

            if (!float.IsNaN(_sweep))
            {
                await Sweep(cycle);
                GetTree().Quit(0);
                return;
            }

            Node3D player = (Node3D)GetTree().GetFirstNodeInGroup(Groups.Player);
            var camera = new Camera3D { Far = 4500f };
            level.AddChild(camera);
            Vector3 eye = player.GlobalPosition + planet.UpAt(player.GlobalPosition) * 1.6f;
            camera.LookAtFromPosition(eye, eye + Vector3.Forward, planet.UpAt(player.GlobalPosition));
            camera.MakeCurrent();
            await Frames(2);

            foreach (float hour in _hours)
            {
                SetSkyHour(cycle, hour);
                await Frames(20);

                Vector3 up = cycle.LocalUp;
                Vector3 toward = cycle.SunDirection - up * cycle.SunDirection.Dot(up);
                toward = toward.LengthSquared() > 1e-6f ? toward.Normalized() : up.Cross(Vector3.Right).Normalized();

                GD.Print($"SkyCapture: {Clock(hour)} -> local {Clock(cycle.LocalHour)}, sun {cycle.SunElevation:0.0} deg, "
                    + $"sky hour {Clock(cycle.SkyHour)} ({cycle.Phase}), daylight {cycle.Effects.Daylight:0.00}, "
                    + $"stars {cycle.Effects.Stars:0.00}");

                var shots = new (string Name, Vector3 Look)[]
                {
                    ("sun", toward + up * 0.18f),
                    ("away", -toward + up * 0.18f),
                    ("up", toward * 0.5f + up),
                };

                foreach ((string name, Vector3 look) in shots)
                {
                    camera.LookAtFromPosition(eye, eye + look, up);
                    await Frames(10);
                    string label = Clock(hour).Replace(":", "");
                    GetViewport().GetTexture().GetImage().SavePng($"{_out}/{label}_{name}.png");
                }
            }
        }
        catch (Exception error)
        {
            GD.PushError($"SkyCapture: {error}");
            code = 1;
        }

        GetTree().Quit(code);
    }

    /// <summary>Tilts the player's own camera down through the horizon, facing the sun.</summary>
    private async Task Sweep(DayCycle cycle)
    {
        var player = (Node3D)GetTree().GetFirstNodeInGroup(Groups.Player);
        var controller = player as GameBase.Player.PlayerController ?? NodeSearch.FindByType<GameBase.Player.PlayerController>(player);

        SetSkyHour(cycle, _sweep);
        Vector3 up = cycle.LocalUp;
        Vector3 toward = (cycle.SunDirection - up * cycle.SunDirection.Dot(up)).Normalized();
        player.LookAt(player.GlobalPosition + toward, up);
        await Frames(10);

        for (float pitch = 20; pitch >= -45; pitch -= 1)
        {
            controller.PitchDegrees = pitch;
            await Frames(3);

            Camera3D camera = GetViewport().GetCamera3D();
            Image image = GetViewport().GetTexture().GetImage();
            Color top = Average(image, 0, 40);
            GD.Print($"Sweep: pitch {pitch:0} camera {camera.GlobalPosition} up {cycle.LocalUp} "
                + $"hour {cycle.LocalHour:0.000} sky {cycle.SkyHour:0.000} sun {cycle.SunElevation:0.000} "
                + $"top {top.ToHtml(false)}");
            image.SavePng($"{_out}/sweep_{(int)(pitch + 100):000}.png");
        }
    }

    /// <summary>
    /// Lets the clock run through an hour in real time, facing the sun with
    /// the camera tilted down, and logs every frame whose sky or ground jumps
    /// in colour from the frame before -- with what the lights were doing.
    /// </summary>
    private async Task Watch(DayCycle cycle, Node level)
    {
        var player = (Node3D)GetTree().GetFirstNodeInGroup(Groups.Player);
        var controller = player as GameBase.Player.PlayerController ?? NodeSearch.FindByType<GameBase.Player.PlayerController>(player);
        var rig = NodeSearch.FindByType<GameBase.Lighting.LightingRig>(level);

        SetSkyHour(cycle, _watch);
        Vector3 up = cycle.LocalUp;
        Vector3 toward = (cycle.SunDirection - up * cycle.SunDirection.Dot(up)).Normalized();
        player.LookAt(player.GlobalPosition + toward, up);
        controller.PitchDegrees = _pitch;
        cycle.Running = true;
        await Frames(5);

        Color lastSky = default, lastGround = default;
        int jumps = 0;
        for (int frame = 0; frame < 900; frame++)
        {
            // A little sway, as a player's look does.
            controller.PitchDegrees = _pitch + 3f * Mathf.Sin(frame * 0.2f);
            await Frames(1);

            Image image = GetViewport().GetTexture().GetImage();
            Color sky = Average(image, 0, 30);
            Color ground = Average(image, image.GetHeight() - 160, image.GetHeight() - 100);

            if (frame > 0)
            {
                float skyJump = Math.Max(Math.Abs(sky.R - lastSky.R), Math.Max(Math.Abs(sky.G - lastSky.G), Math.Abs(sky.B - lastSky.B)));
                float groundJump = Math.Max(Math.Abs(ground.R - lastGround.R), Math.Max(Math.Abs(ground.G - lastGround.G), Math.Abs(ground.B - lastGround.B)));
                if (skyJump > 0.03f || groundJump > 0.03f)
                {
                    jumps++;
                    GD.Print($"Watch: frame {frame} sun {cycle.SunElevation:0.000} sky {cycle.SkyHour:0.000} "
                        + $"sky jump {skyJump:0.000} ground jump {groundJump:0.000} "
                        + $"sun light {rig.Sunlight.LightEnergy:0.000} shadows {rig.Sunlight.ShadowEnabled}, "
                        + $"moon light {rig.Moonlight.LightEnergy:0.000} shadows {rig.Moonlight.ShadowEnabled}");
                    if (jumps <= 6)
                    {
                        image.SavePng($"{_out}/jump_{frame:000}.png");
                        _lastFrame?.SavePng($"{_out}/jump_{frame:000}_before.png");
                    }
                }
            }

            if (frame % 60 == 0)
                GD.Print($"Watch: frame {frame} sun {cycle.SunElevation:0.000} sky {sky.ToHtml(false)} ground {ground.ToHtml(false)}");

            _lastFrame = image;
            lastSky = sky;
            lastGround = ground;
        }

        GD.Print($"Watch: {jumps} jumps in 900 frames");
    }

    private Image _lastFrame;

    /// <summary>
    /// As spawned -- the clock untouched -- turns the player round on the
    /// spot, logging the time of day the sky is drawn at and the camera.
    /// </summary>
    private async Task Turn(DayCycle cycle)
    {
        var player = (Node3D)GetTree().GetFirstNodeInGroup(Groups.Player);
        Vector3 up = cycle.LocalUp;
        Vector3 start = (-player.GlobalBasis.Z - up * (-player.GlobalBasis.Z).Dot(up)).Normalized();
        cycle.Running = false;

        for (int step = 0; step <= 72; step++)
        {
            Vector3 facing = start.Rotated(up, Mathf.DegToRad(step * 5f));
            player.LookAt(player.GlobalPosition + facing, up);
            await Frames(3);

            Camera3D camera = GetViewport().GetCamera3D();
            Image image = GetViewport().GetTexture().GetImage();
            Color top = Average(image, 0, 30);
            GD.Print($"Turn: {step * 5,3} deg  camera {camera.Name} at {camera.GlobalPosition} "
                + $"(from player {(camera.GlobalPosition - player.GlobalPosition).Length():0.00}) "
                + $"up {cycle.LocalUp} local {cycle.LocalHour:0.000} sun {cycle.SunElevation:0.000} "
                + $"sky {cycle.SkyHour:0.000} {cycle.Phase} top {top.ToHtml(false)}");
            if (step % 6 == 0)
                image.SavePng($"{_out}/turn_{step * 5:000}.png");
        }
    }

    private static Color Average(Image image, int fromRow, int toRow)
    {
        float r = 0, g = 0, b = 0;
        int n = 0;
        for (int y = fromRow; y < toRow; y++)
        for (int x = 0; x < image.GetWidth(); x += 4)
        {
            Color c = image.GetPixel(x, y);
            r += c.R; g += c.G; b += c.B;
            n++;
        }

        return new Color(r / n, g / n, b / n);
    }

    /// <summary>
    /// Turns the planet until the sky is drawn at the spec's hour where the
    /// camera is: the local hour whose sky hour comes closest, on the same
    /// side of noon.
    /// </summary>
    private static void SetSkyHour(DayCycle cycle, float target)
    {
        float best = 0, error = float.MaxValue;
        for (float local = 0; local < 24; local += 0.01f)
        {
            cycle.SetLocalHour(local);
            float apart = Math.Abs(cycle.SkyHour - target);
            apart = Math.Min(apart, 24 - apart);
            if (apart < error)
            {
                error = apart;
                best = local;
            }
        }

        cycle.SetLocalHour(best);
    }

    private static string Clock(float hour)
    {
        int minutes = (int)Math.Round(SkyPalette.Wrap(hour) * 60) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    private async Task Frames(int count)
    {
        for (int n = 0; n < count; n++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
