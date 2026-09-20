using Godot;
using GameBase.Core;
using GameBase.Nodes;
using GameBase.Items;

namespace GameBase.Stats;

/// <summary>
/// Opens the inventory and saves a screenshot, so the panel's look can be
/// reviewed without playing the game.
///
/// Deliberately separate from InventoryCheck: that one asserts behaviour and
/// belongs in a headless run, this one needs a real window and a few frames
/// for the icon viewports to render. Point it at a scene, run it, look at the
/// PNG.
/// </summary>
public partial class InventoryShot : Node
{
    /// <summary>Where the PNG is written.</summary>
    [Export] public string OutputPath { get; set; } = "user://inventory_shot.png";

    /// <summary>Frames to let the icons render before capturing.</summary>
    [Export] public int WarmupFrames { get; set; } = 30;

    /// <summary>
    /// Wait for the chunk streamer to finish before counting warmup frames.
    /// Without this the shot lands on the loading screen in any scene that
    /// builds a world, and the warmup is spent watching a progress bar.
    /// </summary>
    [Export] public bool WaitForWorld { get; set; } = true;

    /// <summary>Give up waiting after this many frames and shoot anyway, so a
    /// world that never finishes cannot hang the capture forever.</summary>
    [Export] public int MaxWaitFrames { get; set; } = 3000;

    /// <summary>Frames to let the loading screen fade out before opening.</summary>
    [Export] public int FadeFrames { get; set; } = 90;

    /// <summary>Open the backpack before shooting. False captures the bar alone.</summary>
    [Export] public bool OpenBackpack { get; set; } = true;

    private int _frames;
    private int _waited;
    private int _readyFrame;
    private bool _worldReady;
    private ChunkStreamer _streamer;
    private InventoryHud _hud;

    public override void _Ready()
    {
        // Opening the inventory pauses the tree. Without this the capture
        // would never run, because _Process stops with everything else.
        ProcessMode = ProcessModeEnum.Always;

        _hud = NodeSearch.FindByType<InventoryHud>(GetTree().CurrentScene);
        if (_hud == null)
        {
            GD.PrintErr("InventoryShot: no InventoryHud in the scene.");
            GetTree().Quit(1);
            return;
        }

        // Spread the items out so the screenshot shows every icon shape, a
        // stacked count and an unstacked tool, on the bar and in the backpack.
        // The starting kit already occupies the first slots; these fill in
        // around it rather than replacing it.
        var cube = ResourceLoader.Load<ItemType>("res://Modules/Inventory/Items/GreenCube.tres");
        var shovel = ResourceLoader.Load<ItemType>("res://Modules/Inventory/Items/Shovel.tres");
        var pickaxe = ResourceLoader.Load<ItemType>("res://Modules/Inventory/Items/Pickaxe.tres");

        if (cube != null)
        {
            _hud.Inventory.SetSlot(4, new ItemStack(cube, 12));
            _hud.Inventory.SetSlot(6, new ItemStack(cube, 64));
            _hud.Inventory.SetSlot(_hud.Inventory.HotbarSize + 3, new ItemStack(cube, 7));
        }

        if (shovel != null)
            _hud.Inventory.SetSlot(_hud.Inventory.HotbarSize + 1, new ItemStack(shovel));

        if (pickaxe != null)
            _hud.Inventory.SetSlot(_hud.Inventory.HotbarSize + 11, new ItemStack(pickaxe));

        _hud.Inventory.SelectedIndex = 0;
    }

    public override void _Process(double delta)
    {
        // Hold everything until the world exists. Opening the inventory pauses
        // the tree, and pausing while chunks are still streaming stops the very
        // work being waited on — the same reason PauseMenu has Suspended.
        if (WaitForWorld && !_worldReady)
        {
            _waited++;
            _streamer ??= NodeSearch.FindByType<ChunkStreamer>(GetTree().CurrentScene);

            bool ready = _streamer?.IsReady ?? true;
            if (!ready && _waited < MaxWaitFrames)
                return;

            if (!ready)
                GD.Print($"InventoryShot: world not ready after {_waited} frames, shooting anyway.");

            _worldReady = true;
            _readyFrame = _waited;
        }

        // Let the loading screen finish fading before opening. Opening pauses
        // the tree, and the fade runs on _Process like everything else, so
        // opening the instant the world is ready freezes the screen mid-fade
        // and the shot comes out under a grey sheet.
        if (_waited++ < _readyFrame + FadeFrames)
            return;

        // Opening pauses the tree, so this must run before the panel is shown
        // or _Process stops being called and the shot is never taken.
        if (_frames == 1 && OpenBackpack)
            _hud.Open();

        if (++_frames < WarmupFrames)
            return;

        Image image = GetViewport().GetTexture().GetImage();
        Error err = image.SavePng(OutputPath);
        GD.Print(err == Error.Ok
            ? $"InventoryShot: wrote {ProjectSettings.GlobalizePath(OutputPath)}"
            : $"InventoryShot: could not write {OutputPath} ({err})");

        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }
}
