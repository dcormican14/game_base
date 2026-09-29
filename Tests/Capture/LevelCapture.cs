using Godot;
using System;
using System.Threading.Tasks;
using GameBase.Core;
using GameBase.Nodes;
using GameBase.Planets;
using GameBase.Tools;
using GameBase.Tools.Highlight;

namespace GameBase.Tests.Capture;

/// <summary>
/// Loads the planet level, waits for the world, and saves screenshots from a
/// few fixed views -- including both tool highlights -- then quits. A visual
/// check to run by hand (it needs a window, so not headless):
///
///   godot --path . res://Tests/Capture/LevelCapture.tscn -- --out=C:/some/folder
/// </summary>
public partial class LevelCapture : Node
{
    [Export(PropertyHint.File, "*.tscn")]
    public string LevelPath { get; set; } = "res://Game/PlanetLevel.tscn";

    private string _out = "user://captures";

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
            await MenuShot();

            AddChild(GD.Load<PackedScene>(LevelPath).Instantiate());
            await Shots();
        }
        catch (Exception error)
        {
            GD.PushError($"LevelCapture: {error}");
        }

        GetTree().Quit();
    }

    /// <summary>The main menu, to check the interface theme.</summary>
    private async Task MenuShot()
    {
        Node menu = GD.Load<PackedScene>("res://Modules/MainMenu/MainMenu.tscn").Instantiate();
        AddChild(menu);
        await Frames(10);

        GetViewport().GetTexture().GetImage().SavePng($"{_out}/menu.png");
        menu.Free();
    }

    private async Task Shots()
    {
        var planet = NodeSearch.FindByType<Planet>(this);
        NodeWorld world = planet.World;

        // The player's own tools would draw their highlight over ours.
        foreach (Node node in GetTree().GetNodesInGroup(Groups.Player))
            NodeSearch.FindByType<ToolController>(node).ProcessMode = ProcessModeEnum.Disabled;

        // Our own camera, so the views do not depend on the player rig.
        var camera = new Camera3D { Fov = 75f, Far = 4000f };
        AddChild(camera);

        ulong start = Time.GetTicksMsec();
        bool announced = false;

        while (!planet.Streamer.IsReady || world.HasPendingMeshes || planet.Streamer.PendingChunks > 0)
        {
            if (planet.Streamer.IsReady && !announced)
            {
                announced = true;
                GD.Print($"LevelCapture: ready to play after {(Time.GetTicksMsec() - start) / 1000f:0.0}s");
            }

            if (Time.GetTicksMsec() - start > 60000)
            {
                GD.Print("LevelCapture: world not settled after 60s, shooting anyway");
                break;
            }

            await Frames(1);
        }

        GD.Print($"LevelCapture: world settled in {(Time.GetTicksMsec() - start) / 1000f:0.0}s, "
            + $"{world.Store.ChunkCount} chunks, {world.TriangleCount} triangles");

        Vector3 up = Vector3.Up;
        Vector3 ground = planet.SurfacePoint(up);
        Vector3 north = Vector3.Forward;

        camera.MakeCurrent();

        // Steady-state frame time, standing on the surface.
        camera.GlobalPosition = ground + up * 1.7f;
        camera.LookAt(ground + up * 1.7f + north * 100f, Vector3.Up);
        await Frames(30);

        ulong frameStart = Time.GetTicksUsec();
        await Frames(120);
        GD.Print($"LevelCapture: {(Time.GetTicksUsec() - frameStart) / 120000f:0.00} ms a frame over 120 frames");

        // 1. Standing, looking at the horizon.
        await Shoot(camera, ground + up * 1.7f, ground + up * 1.7f + north * 100f, "horizon");

        // 1b. Up at the moon, and a lower view with the moon and its light across the ground.
        var skybox = NodeSearch.FindByType<GameBase.World.Skybox>(this);
        if (skybox != null)
        {
            Vector3 eyeLevel = ground + up * 1.7f;
            await Shoot(camera, eyeLevel, eyeLevel + skybox.MoonDirection * 100f, "moon");

            Vector3 flat = (skybox.MoonDirection - up * skybox.MoonDirection.Dot(up)).Normalized();
            await Shoot(camera, eyeLevel, eyeLevel + flat * 100f + up * 40f, "moonlit");
        }

        // 2. Looking down at the sand, shovel in hand.
        Vector3 eye = ground + up * 1.7f;
        Vector3 aim = (north * 4f - up * 1.7f).Normalized();
        ShowTool(world, "shovel", camera, eye, eye + aim * 10f);
        await Shoot(camera, eye, eye + aim * 10f, "shovel");

        // 3. A pit dug to the rock, pickaxe in hand.
        Vector3 pit = ground + north * 6f;
        Dig(world, pit, 5f, 10f);
        world.MeshAllNow();
        await PhysicsFrames();

        Vector3 pitEye = pit + up * 2f - north * 2.5f;
        Vector3 pitAim = pit - up * 7f;
        ShowTool(world, "pickaxe", camera, pitEye, pitAim);
        await Shoot(camera, pitEye, pitAim, "pickaxe");

        // A strip of frames a tenth of a second apart, to see the particles move.
        for (int n = 1; n <= 8; n++)
            await Shoot(null, pitEye, pitAim, $"pickaxe_motion_{n}");

        // 4. From high above: the planet still curves for anyone who flies.
        await Shoot(camera, ground + up * 250f, ground + north * 600f, "altitude");

        await Gameplay(world);
    }

    /// <summary>
    /// The real input path, end to end: the player's own camera and tool
    /// controller, the inventory's selection, and the mine and place actions
    /// pressed through Godot's input system. Reports how far the sand moved.
    /// </summary>
    private async Task Gameplay(NodeWorld world)
    {
        Node player = GetTree().GetFirstNodeInGroup(Groups.Player);
        var controller = NodeSearch.FindByType<ToolController>(player);
        var inventory = NodeSearch.FindByType<GameBase.Items.Inventory>(this);

        controller.ProcessMode = ProcessModeEnum.Inherit;
        NodeSearch.FindByType<Camera3D>(player).MakeCurrent();
        Input.MouseMode = Input.MouseModeEnum.Captured;

        // Onto untouched sand well away from the pit dug earlier, looking down.
        var body = (CharacterBody3D)player;
        Vector3 spot = world.GlobalPosition + (body.GlobalPosition - world.GlobalPosition).Normalized()
            * body.GlobalPosition.DistanceTo(world.GlobalPosition) + Vector3.Right * 30f;
        body.GlobalPosition = spot + Vector3.Up * 0.5f;
        body.Velocity = Vector3.Zero;
        ((GameBase.Player.PlayerController)player).PitchDegrees = -40f;
        await PhysicsFrames();
        inventory.SelectedIndex = 1;
        await Frames(20);

        if (!controller.Highlight.Showing)
        {
            var cam = NodeSearch.FindByType<Camera3D>(player);
            Vector2 mid = cam.GetViewport().GetVisibleRect().Size * 0.5f;
            Vector3 o = cam.ProjectRayOrigin(mid), d = cam.ProjectRayNormal(mid);
            bool hit = world.Raycast(o, o + d * 40f, out NodeHit h);
            GD.Print($"LevelCapture: gameplay - the {controller.Tool?.Id ?? "no tool"} shows no highlight "
                + $"(mouse {Input.MouseMode}, state {UiStateService.Instance?.Current}) "
                + $"player {((Node3D)player).GlobalPosition} eye {o} aim {d} hit {hit} {h.Point} {h.Type} "
                + $"physics {((Node3D)player).IsPhysicsProcessing()}");
            return;
        }

        Vector3 at = controller.Highlight.Target.Point;
        Vector3 up = (at - world.GlobalPosition).Normalized();
        float before = Height(world, at, up);

        // A fixed side view of the spot, so what the shovel did is legible.
        Vector3 viewFrom = at + up * 3f + Vector3.Right * 10f;

        await SelectMode(controller, ShovelMode.Raise);
        await Hold("mine", 60);
        float raised = Height(world, at, up);
        await ShootFrom(viewFrom, at, "gameplay_raised");

        await SelectMode(controller, ShovelMode.Lower);
        await Hold("mine", 150);
        float dug = Height(world, at, up);
        await Frames(30);
        await ShootFrom(viewFrom, at, "gameplay_dug");
        await ShootFrom(at + up * 12f + Vector3.Right * 2f, at, "gameplay_dug_above");

        GD.Print($"LevelCapture: gameplay - the shovel raised the ground {raised - before:0.00} (raise), "
            + $"then lowered it to {dug - before:0.00} (lower), by the R key and the left button");

        await Level(world, body, controller);
        await MoundDig(world, body, controller);
    }

    /// <summary>
    /// Stands beside a column and looks straight at its side: the shovel's
    /// circle should stand up on the wall. Saves a shot and reports which way
    /// the circle faces.
    /// </summary>
    private async Task CircleOnTheWall(NodeWorld world, CharacterBody3D body, ToolController controller,
        Vector3 column, Vector3 up)
    {
        Vector3 side = Vector3.Back;
        Vector3 feet = column + side * 5.5f;

        if (ParticleField.GroundAt(world, feet, up, 20f, out Vector3 floor))
            feet = floor;

        body.GlobalPosition = feet + up * 0.3f;
        body.Velocity = Vector3.Zero;
        body.GlobalBasis = Basis.LookingAt(-side, up);
        ((GameBase.Player.PlayerController)body).PitchDegrees = 8f;
        await PhysicsFrames();
        await Frames(10);

        if (!controller.Highlight.Showing)
        {
            GD.Print("LevelCapture: circle on the wall - nothing under the crosshair");
            return;
        }

        NodeHit target = controller.Highlight.Target;
        var shovel = (ShovelTool)controller.Tool;
        SurfaceDisc disc = shovel.DiscAt(world, target);
        Vector3 facing = (world.GlobalBasis * disc.Centre.Normal).Normalized();

        await Frames(6);
        GetViewport().GetTexture().GetImage().SavePng($"{_out}/circle_wall.png");

        GD.Print($"LevelCapture: circle on the wall - faces {facing.Dot(up):0.00} up and "
            + $"{facing.Dot(side):0.00} out toward the player");
    }

    /// <summary>Presses the mode key until the held tool is in a mode.</summary>
    private async Task SelectMode(ToolController controller, ShovelMode mode)
    {
        for (int n = 0; n < 8 && controller.ModeOf(controller.Tool) != (int)mode; n++)
        {
            Input.ActionPress("tool_mode");
            await Frames(2);
            Input.ActionRelease("tool_mode");
            await Frames(2);
        }

        if (controller.ModeOf(controller.Tool) != (int)mode)
            GD.Print($"LevelCapture: the mode key never reached {mode}");
    }

    /// <summary>
    /// Raises a bump just ahead, then levels it the way a player would: turn
    /// aside and press on flat ground, then turn back onto the bump and hold.
    /// </summary>
    private async Task Level(NodeWorld world, CharacterBody3D body, ToolController controller)
    {
        var player = (GameBase.Player.PlayerController)body;
        var exclude = new Godot.Collections.Array<Rid> { body.GetRid() };

        Vector3 spot = body.GlobalPosition + Vector3.Right * 30f;
        Vector3 up = (spot - world.GlobalPosition).Normalized();

        if (world.Raycast(spot + up * 30f, spot - up * 30f, out NodeHit sand, exclude))
            spot = sand.Point;

        body.GlobalPosition = spot + up * 0.3f;
        body.Velocity = Vector3.Zero;
        player.PitchDegrees = -24f;
        await PhysicsFrames();
        await Frames(10);

        if (!controller.Highlight.Showing)
        {
            GD.Print("LevelCapture: level - nothing under the crosshair");
            return;
        }

        Vector3 bump = controller.Highlight.Target.Point;
        float ground = Height(world, bump, up);

        await SelectMode(controller, ShovelMode.Raise);
        await Hold("mine", 90);

        // The bump creeps toward the player as it grows -- the crosshair meets
        // its near side -- so it is measured by its highest point along the
        // line from where it began back toward the player.
        Vector3 toward = body.GlobalBasis.Z.Normalized();
        float Highest()
        {
            float best = float.MinValue;
            for (float d = -2f; d <= 5f; d += 0.5f)
                best = Mathf.Max(best, Height(world, bump + toward * d, up) - ground);
            return best;
        }

        float raised = Highest();
        await ShootFrom(bump + up * 3f + Vector3.Right * 10f, bump, "level_before");

        // Turn a quarter aside, onto the flat, and press there.
        await SelectMode(controller, ShovelMode.Level);
        const float Aside = Mathf.Pi * 0.5f;
        body.RotateObjectLocal(Vector3.Up, Aside);
        await Frames(5);

        Input.ActionPress("mine");
        await Frames(5);

        // Turn back onto the bump, slowly, and hold there while it is cut down.
        for (int n = 0; n < 60; n++)
        {
            body.RotateObjectLocal(Vector3.Up, -Aside / 60f);
            await Frames(1);
        }

        await Frames(300);
        Input.ActionRelease("mine");
        await Frames(10);

        float left = Highest();
        await ShootFrom(bump + up * 3f + Vector3.Right * 10f, bump, "level_after");

        GD.Print($"LevelCapture: level - a bump {raised:0.00} high was levelled to {left:0.00} "
            + "above the ground where the press began");
    }

    /// <summary>
    /// Raises the ground under the player's own feet -- climbing on it, with
    /// no height limit -- then lowers it straight back down underfoot. Every
    /// physics frame the player's feet are checked against the ground under
    /// them; reports how high the column got, how wide its base spread, and
    /// whether the player ever sank into the ground or fell through it.
    /// </summary>
    private async Task MoundDig(NodeWorld world, CharacterBody3D body, ToolController controller)
    {
        var exclude = new Godot.Collections.Array<Rid> { body.GetRid() };

        // Fresh sand, well away from everything shaped so far, found from above.
        Vector3 spot = body.GlobalPosition - Vector3.Right * 90f;
        Vector3 up = (spot - world.GlobalPosition).Normalized();

        if (world.Raycast(spot + up * 30f, spot - up * 30f, out NodeHit sand, exclude))
            spot = sand.Point;

        body.GlobalPosition = spot + up * 0.3f;
        body.Velocity = Vector3.Zero;
        ((GameBase.Player.PlayerController)body).PitchDegrees = -89f;
        await PhysicsFrames();
        await Frames(10);

        float start = Radial(world, body.GlobalPosition);
        float ground = Height(world, spot, up);

        await SelectMode(controller, ShovelMode.Raise);
        float pileSink = await Watch(world, body, "mine", 480, start, exclude);
        float pile = Radial(world, body.GlobalPosition) - start;
        float top = Height(world, spot, up) - ground;
        await ShootFrom(spot + up * (pile * 0.6f) + Vector3.Right * 16f, spot + up * (pile * 0.6f), "mound_built");

        await CircleOnTheWall(world, body, controller, spot, up);

        // How far out the ground was raised at all: the base's radius.
        float baseRadius = 0f;
        for (float r = 1f; r < 30f; r += 0.5f)
        {
            if (Height(world, spot + Vector3.Right * r, up) - ground > 0.1f)
                baseRadius = r;
        }

        // Back up onto the column to lower it from on top.
        if (ParticleField.GroundAt(world, spot, up, 60f, out Vector3 columnTop))
            body.GlobalPosition = columnTop + up * 0.3f;

        body.Velocity = Vector3.Zero;
        ((GameBase.Player.PlayerController)body).PitchDegrees = -89f;
        await PhysicsFrames();

        await SelectMode(controller, ShovelMode.Lower);
        float digSink = await Watch(world, body, "mine", 480, start, exclude);
        float dug = Radial(world, body.GlobalPosition) - start;
        await ShootFrom(spot + up * 6f + Vector3.Right * 10f, spot, "mound_dug");

        GD.Print($"LevelCapture: mound - raised a mound {top:0.00} high on a base {baseRadius:0.0} in radius, "
            + $"the player riding it to {pile:0.00} "
            + $"(feet sank at most {pileSink:0.00}), lowered back to {dug:0.00} (feet sank at most {digSink:0.00}); "
            + (Mathf.Max(pileSink, digSink) > 0.6f ? "FELL THROUGH" : "held"));
    }

    /// <summary>
    /// Holds an action, returning how far the player's feet ever sank below
    /// the ground under them. Prints the frame rate while it was held.
    /// </summary>
    private async Task<float> Watch(NodeWorld world, CharacterBody3D body, string action, int frames,
        float start, Godot.Collections.Array<Rid> exclude)
    {
        float worst = 0f;
        int missed = 0;
        int sinking = 0;
        ulong began = Time.GetTicksUsec();
        ulong firstFrame = Engine.GetProcessFrames();

        Input.ActionPress(action);

        for (int frame = 0; frame < frames; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

            Vector3 feet = body.GlobalPosition;
            Vector3 up = (feet - world.GlobalPosition).Normalized();

            // From well above, so a player already inside the ground still
            // finds its top. A frame with no ground at all is forgiven once or
            // twice -- the physics server takes a step to see a section's new
            // collision -- but not for longer: then the player is falling
            // through the world.
            float sink;

            if (world.Raycast(feet + up * 4f, feet - up * 30f, out NodeHit ground, exclude))
            {
                sink = Radial(world, ground.Point) - Radial(world, feet);
                missed = 0;
            }
            else
            {
                sink = ++missed >= 3 ? 30f : 0f;
            }

            // A click raises the ground in one step, and the player is lifted
            // onto it on the next physics frame, so one frame of sinking is
            // the lift catching up. Only sinking that lasts counts.
            sinking = sink > 0.3f ? sinking + 1 : 0;

            if (sinking >= 3)
            {
                worst = Mathf.Max(worst, sink);
                GD.Print($"LevelCapture: {action} frame {frame} SANK feet {Radial(world, feet) - start:0.00} "
                    + $"sink {sink:0.00} floor {body.IsOnFloor()}");
            }

            if (frame % 60 == 0)
                GD.Print($"LevelCapture: {action} frame {frame} feet {Radial(world, feet) - start:0.00} "
                    + $"sink {sink:0.00} pending {world.PendingSections}");
        }

        Input.ActionRelease(action);

        float seconds = (Time.GetTicksUsec() - began) / 1e6f;
        GD.Print($"LevelCapture: holding {action} ran at {(Engine.GetProcessFrames() - firstFrame) / seconds:0} fps");

        await Frames(10);
        return worst;
    }

    private static float Radial(NodeWorld world, Vector3 point) => point.DistanceTo(world.GlobalPosition);

    /// <summary>A shot from a throwaway camera, handing the view back afterwards.</summary>
    private async Task ShootFrom(Vector3 from, Vector3 to, string name)
    {
        Camera3D previous = GetViewport().GetCamera3D();
        var camera = new Camera3D { Fov = 75f };
        AddChild(camera);
        camera.MakeCurrent();

        await Shoot(camera, from, to, name);

        previous?.MakeCurrent();
        camera.QueueFree();
    }

    private static float Height(NodeWorld world, Vector3 at, Vector3 up) =>
        (ParticleField.GroundAt(world, at, up, 40f, out Vector3 ground) ? ground : at).DistanceTo(world.GlobalPosition);

    private async Task Hold(string action, int frames)
    {
        Input.ActionPress(action);
        await Frames(frames);
        Input.ActionRelease(action);
        await Frames(10);
    }

    private void ShowTool(NodeWorld world, string id, Camera3D camera, Vector3 eye, Vector3 at)
    {
        ITool tool = ToolRegistry.Find(id);
        var highlight = NodeSearch.FindByType<ToolHighlight>(this);
        // Reach as if the eye were a player standing at the pit's rim.
        var context = new ToolContext(world, eye, at - eye, UnlimitedLedger.Instance, null, 0f, 4f);

        highlight?.Clear();

        if (tool.TryTarget(context, out NodeHit target))
            highlight?.Show(tool, context, target);
        else
            GD.Print($"LevelCapture: the {id} found no target");
    }

    /// <summary>Clears everything but rock from a round pit.</summary>
    private static void Dig(NodeWorld world, Vector3 centre, float radius, float depth)
    {
        Vector3I low = world.CellAt(centre - new Vector3(radius, depth, radius));
        Vector3I high = world.CellAt(centre + new Vector3(radius, 2f, radius));

        for (int x = low.X; x <= high.X; x++)
        for (int y = low.Y; y <= high.Y; y++)
        for (int z = low.Z; z <= high.Z; z++)
        {
            var cell = new Vector3I(x, y, z);
            Vector3 at = world.LatticePoint(cell);
            var flat = new Vector2(at.X - centre.X, at.Z - centre.Z);

            if (flat.Length() <= radius && world.TypeAt(cell) is ParticleNode)
                world.ClearNode(cell);
        }
    }

    /// <summary>Saves a screenshot, from the given camera pose or, with no camera, from whatever is current.</summary>
    private async Task Shoot(Camera3D camera, Vector3 from, Vector3 to, string name)
    {
        if (camera != null)
        {
            camera.GlobalPosition = from;
            camera.LookAt(to, Vector3.Up);
        }

        await Frames(6);

        Image image = GetViewport().GetTexture().GetImage();
        string path = $"{_out}/{name}.png";
        image.SavePng(path);
        GD.Print($"LevelCapture: saved {path}");
    }

    private async Task Frames(int count)
    {
        for (int n = 0; n < count; n++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task PhysicsFrames()
    {
        for (int n = 0; n < 3; n++)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }
}
