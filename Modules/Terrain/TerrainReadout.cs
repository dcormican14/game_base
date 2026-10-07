using Godot;
using GameBase.Core;
using GameBase.Planets;
using GameBase.World;

namespace GameBase.Terrain;

/// <summary>
/// A debug readout of the terrain under the player, toggled with the
/// "debug_terrain" action (F3): the region and its blend, heights against the
/// base radius and sea level, the slope and the sand depth it gives, the seed,
/// and what the far terrain is doing. For tuning the terrain settings in play.
///
/// Drop-in: finds the planet and the player itself. Built in code, so it needs
/// no scene of its own.
/// </summary>
public partial class TerrainReadout : CanvasLayer
{
    [Export] public StringName ToggleAction { get; set; } = "debug_terrain";

    /// <summary>Seconds between refreshes.</summary>
    [Export] public float UpdateInterval { get; set; } = 0.25f;

    private Label _label;
    private Control _panel;
    private Planet _planet;
    private Node3D _player;
    private DayCycle _cycle;
    private double _since;

    public override void _Ready()
    {
        Layer = 21;
        ProcessMode = ProcessModeEnum.Always;

        var panel = new PanelContainer
        {
            SelfModulate = new Color(1f, 1f, 1f, 0.8f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        panel.GrowHorizontal = Control.GrowDirection.Begin;
        panel.OffsetRight = -8;
        panel.OffsetTop = 8;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);

        _label = new Label();
        _label.AddThemeFontSizeOverride("font_size", 13);

        margin.AddChild(_label);
        panel.AddChild(margin);
        AddChild(panel);

        _panel = panel;
        _panel.Visible = false;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!InputMap.HasAction(ToggleAction) || !@event.IsActionPressed(ToggleAction))
            return;

        _panel.Visible = !_panel.Visible;
        _since = UpdateInterval;
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (!_panel.Visible)
            return;

        _since += delta;
        if (_since < UpdateInterval)
            return;
        _since = 0;

        Node scene = GetTree().CurrentScene ?? GetParent();
        _planet ??= NodeSearch.FindByType<Planet>(scene);
        _player ??= GetTree().GetFirstNodeInGroup(Groups.Player) as Node3D;
        _cycle ??= NodeSearch.FindByType<DayCycle>(scene);

        if (_planet?.Shape == null || _player == null)
        {
            _label.Text = "No planet";
            return;
        }

        Vector3 feet = _planet.ToLocal(_player.GlobalPosition);
        TerrainInfo info = _planet.Shape.Describe(feet);

        // The slope where the ground is: the shape's gradient, by differences.
        Vector3 ground = TerrainShapes.SurfacePoint(_planet.Shape, feet);
        const float Step = 1f;
        Vector3 inward = new Vector3(
            Difference(ground, Vector3.Right * Step),
            Difference(ground, Vector3.Up * Step),
            Difference(ground, Vector3.Back * Step)) / (2f * Step);

        float slope = Mathf.RadToDeg((-inward).AngleTo(ground));
        float sand = _planet.Generator.Sand.DepthOn(-inward, ground);

        string region = info.Name;
        FarTerrain far = _planet.Far;

        _label.Text =
            $"Seed {_planet.SeedText}\n" +
            $"Region #{info.Region}: {region}\n" +
            $"Blend  {info.Blend}" + (info.IslandWeight > 0f ? $", sky islands {info.IslandWeight:P0}" : "") + "\n" +
            $"Ground {info.SurfaceHeight:0.0} above base\n" +
            $"You {info.HeightAboveSeaLevel:0.0} above sea level\n" +
            $"Slope {slope:0} deg, sand {sand:0.0} deep\n" +
            (far == null
                ? "Far terrain off"
                : $"Far {far.DrawnBlocks} drawn, {far.WantedBlocks} wanted ({far.LevelCounts}), {far.PendingBlocks} pending") +
            $"\nChunks {_planet.World.Store.ChunkCount}, sections pending {_planet.World.PendingSections}" +
            (_cycle == null ? "" : $"\n{Clock(_cycle.LocalHour)} local, sun {_cycle.SunElevation:0.0} deg, {_cycle.Phase} ([ and ] change the time)");
    }

    private static string Clock(float hour)
    {
        int minutes = (int)(SkyPalette.Wrap(hour) * 60) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    private float Difference(Vector3 at, Vector3 step)
    {
        Vector3 a = at + step, b = at - step;
        return (float)(_planet.Shape.Distance(a.X, a.Y, a.Z) - _planet.Shape.Distance(b.X, b.Y, b.Z));
    }
}
