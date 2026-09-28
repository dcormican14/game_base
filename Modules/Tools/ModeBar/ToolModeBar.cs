using Godot;
using System.Collections.Generic;
using GameBase.Core;
using GameBase.Items;

namespace GameBase.Tools;

/// <summary>
/// The held tool's modes, as a row of text labels just above the hotbar: the
/// selected one in gold, the rest in cream. Shown only while the tool in hand
/// has modes and the player is in the world; the mode key steps through them
/// (see <see cref="ToolController"/>).
///
/// Instance into a level with a player; it finds the player's
/// <see cref="ToolController"/> itself.
/// </summary>
public partial class ToolModeBar : CanvasLayer
{
    /// <summary>How far above the bottom of the screen the bar sits: clear of the hotbar.</summary>
    [Export(PropertyHint.Range, "0,400,1")] public int BottomMargin { get; set; } = 86;

    [Export(PropertyHint.Range, "8,48,1")] public int FontSize { get; set; } = 13;

    private ToolController _controller;
    private HBoxContainer _row;
    private PanelContainer _panel;
    private readonly List<Label> _labels = new();

    private ITool _shownTool;
    private int _shownMode = -1;

    public override void _Ready()
    {
        Layer = 6;

        var anchor = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        anchor.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        anchor.GrowVertical = Control.GrowDirection.Begin;
        anchor.AddThemeConstantOverride("margin_bottom", BottomMargin);
        AddChild(anchor);

        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        anchor.AddChild(centre);

        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat
        {
            BgColor = InventoryPalette.SlotFill,
            BorderColor = InventoryPalette.SlotBorder,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 5,
            ContentMarginBottom = 5,
        };
        style.SetBorderWidthAll(2);
        _panel.AddThemeStyleboxOverride("panel", style);
        centre.AddChild(_panel);

        _row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _row.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(_row);

        _panel.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_controller == null || !IsInstanceValid(_controller))
            _controller = FindController();

        ITool tool = _controller?.Tool;
        bool show = tool != null && tool.Modes.Count > 0
            && UiStateService.Instance is not { GameplayHasInput: false };

        _panel.Visible = show;

        if (!show)
            return;

        int mode = _controller.ModeOf(tool);

        if (tool != _shownTool)
            Rebuild(tool);

        if (tool != _shownTool || mode != _shownMode)
            Select(mode);

        _shownTool = tool;
        _shownMode = mode;
    }

    private ToolController FindController()
    {
        foreach (Node player in GetTree().GetNodesInGroup(Groups.Player))
        {
            var found = NodeSearch.FindByType<ToolController>(player);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>One label per mode, with a dot between each.</summary>
    private void Rebuild(ITool tool)
    {
        foreach (Node child in _row.GetChildren())
            child.QueueFree();

        _labels.Clear();

        for (int i = 0; i < tool.Modes.Count; i++)
        {
            if (i > 0)
                _row.AddChild(MakeLabel("·", InventoryPalette.TextDim));

            Label label = MakeLabel(tool.Modes[i].ToUpperInvariant(), InventoryPalette.TextDim);
            _labels.Add(label);
            _row.AddChild(label);
        }
    }

    private void Select(int mode)
    {
        for (int i = 0; i < _labels.Count; i++)
        {
            _labels[i].AddThemeColorOverride("font_color",
                i == mode ? Palette.Gold : InventoryPalette.TextDim);
        }
    }

    private Label MakeLabel(string text, Color colour)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", FontSize);
        label.AddThemeColorOverride("font_color", colour);
        label.AddThemeColorOverride("font_shadow_color", InventoryPalette.TextShadow);
        label.AddThemeConstantOverride("shadow_offset_x", 1);
        label.AddThemeConstantOverride("shadow_offset_y", 1);
        return label;
    }
}
