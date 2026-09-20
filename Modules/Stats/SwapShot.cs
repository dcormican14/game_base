using Godot;
using GameBase.Core;
using GameBase.Items;

namespace GameBase.Stats;

/// <summary>
/// Swaps two occupied slots and screenshots the result, so the frames right
/// after a swap can be looked at.
///
/// Both of the faults this covers are one-frame rendering artefacts that no
/// assertion about the model can see: two items drawn on top of each other
/// because a viewport composited a new render over the old one, and a patch of
/// UI baked into an icon because the viewport's environment pulled in the
/// canvas behind it. The only way to know they are gone is to look.
///
/// Captures on the frame immediately after the swap by default — the worst
/// case, and the one where a late reveal would show the previous item.
/// </summary>
public partial class SwapShot : Node
{
    [Export] public string OutputPath { get; set; } = "user://swap_shot.png";

    /// <summary>Frames to let the panel and its icons settle before swapping.</summary>
    [Export] public int WarmupFrames { get; set; } = 40;

    /// <summary>Frames to wait after the swap before capturing.</summary>
    [Export] public int FramesAfterSwap { get; set; } = 1;

    private int _frames;
    private InventoryHud _hud;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;

        _hud = NodeSearch.FindByType<InventoryHud>(GetTree().CurrentScene);
        if (_hud == null)
        {
            GD.PrintErr("SwapShot: no InventoryHud in the scene.");
            GetTree().Quit(1);
            return;
        }

        _hud.PauseWhileOpen = false;
        _hud.CaptureMouseOnClose = false;

        // Two clearly different silhouettes in adjacent slots, so an overlap
        // between them would be unmistakable rather than subtle.
        var cube = ResourceLoader.Load<ItemType>("res://Modules/Inventory/Items/GreenCube.tres");
        var pick = ResourceLoader.Load<ItemType>("res://Modules/Inventory/Items/Pickaxe.tres");
        var shovel = ResourceLoader.Load<ItemType>("res://Modules/Inventory/Items/Shovel.tres");

        _hud.Inventory.Clear();
        if (pick != null) _hud.Inventory.SetSlot(0, new ItemStack(pick));
        if (cube != null) _hud.Inventory.SetSlot(1, new ItemStack(cube, 12));
        if (shovel != null) _hud.Inventory.SetSlot(2, new ItemStack(shovel));
        if (cube != null) _hud.Inventory.SetSlot(3, new ItemStack(cube, 5));

        _hud.Open();
    }

    public override void _Process(double delta)
    {
        _frames++;

        if (_frames == WarmupFrames)
        {
            // Two swaps at once, between items of different shapes: the
            // pickaxe and cube trade places, as do the shovel and the other
            // cube. Every one of the four slots changes on the same frame.
            _hud.Inventory.Swap(0, 1);
            _hud.Inventory.Swap(2, 3);
            return;
        }

        if (_frames == WarmupFrames + FramesAfterSwap)
        {
            Image image = GetViewport().GetTexture().GetImage();
            Error err = image.SavePng(OutputPath);
            GD.Print(err == Error.Ok
                ? $"SwapShot: wrote {ProjectSettings.GlobalizePath(OutputPath)}"
                : $"SwapShot: could not write {OutputPath} ({err})");

            GetTree().Quit(err == Error.Ok ? 0 : 1);
        }
    }
}
