using Godot;
using GameBase.Core;
using GameBase.Items;

namespace GameBase.Stats;

/// <summary>
/// Stages a drag and screenshots it mid-flight, so the thing that follows the
/// cursor can be looked at rather than inferred.
///
/// DragCheck asserts the drag's invariants, but the two faults being chased
/// here are purely visual — an item drawn in two places, and a preview showing
/// a previously dragged item — and neither is visible to an assertion about
/// the model.
///
/// The drag is staged rather than performed with synthetic mouse events:
/// Godot's drag is driven by real pointer state, which ParseInputEvent does
/// not reproduce, so a simulated press-and-move leaves the engine's drag
/// machinery untouched and nothing is picked up. Calling the slot's own
/// _GetDragData runs the same code a real drag runs — hiding the source,
/// building the preview — and the preview is then parented and positioned by
/// hand. What this cannot cover is Godot's dispatch, which neither fault was
/// ever about.
/// </summary>
public partial class DragShot : Node
{
    [Export] public string OutputPath { get; set; } = "user://drag_shot.png";

    /// <summary>Frames to let the panel and its icons settle before starting.</summary>
    [Export] public int WarmupFrames { get; set; } = 40;

    /// <summary>Frames to drag for before the screenshot is taken.</summary>
    [Export] public int DragFrames { get; set; } = 25;

    private int _frames;
    private InventoryHud _hud;
    private Vector2 _from;
    private Vector2 _to;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;

        _hud = NodeSearch.FindByType<InventoryHud>(GetTree().CurrentScene);
        if (_hud == null)
        {
            GD.PrintErr("DragShot: no InventoryHud in the scene.");
            GetTree().Quit(1);
            return;
        }

        // Pausing would stop the drag from being processed at all.
        _hud.PauseWhileOpen = false;
        _hud.CaptureMouseOnClose = false;

        // Several items, so a preview showing the WRONG one would be obvious.
        var cube = ResourceLoader.Load<ItemType>("res://Modules/Inventory/Items/GreenCube.tres");
        var shovel = ResourceLoader.Load<ItemType>("res://Modules/Inventory/Items/Shovel.tres");
        var pick = ResourceLoader.Load<ItemType>("res://Modules/Inventory/Items/Pickaxe.tres");

        _hud.Inventory.Clear();
        if (pick != null) _hud.Inventory.SetSlot(0, new ItemStack(pick));
        if (shovel != null) _hud.Inventory.SetSlot(1, new ItemStack(shovel));
        if (cube != null) _hud.Inventory.SetSlot(2, new ItemStack(cube, 12));
        if (cube != null) _hud.Inventory.SetSlot(_hud.Inventory.HotbarSize + 4, new ItemStack(cube, 3));

        _hud.Open();
    }

    public override void _Process(double delta)
    {
        _frames++;

        if (_frames == WarmupFrames)
        {
            // Godot's drag is driven by real pointer state, which synthetic
            // events do not reliably reproduce — a simulated press-and-move
            // leaves the engine's drag machinery untouched. So the drag is
            // staged directly instead: ask the slot for its drag data (which
            // hides the source and builds the preview, exactly as a real drag
            // would) and park the preview under a chosen point.
            //
            // This still exercises the code under test. What it cannot cover
            // is Godot's own dispatch, which is not what these two faults were
            // ever about.
            InventorySlot source = FindSlot(_hud, 1);
            InventorySlot target = FindSlot(_hud, _hud.Inventory.HotbarSize + 2);

            if (source == null || target == null)
            {
                GD.PrintErr("DragShot: could not find the slots to drag between.");
                GetTree().Quit(1);
                return;
            }

            _from = source.GlobalPosition + source.Size * 0.5f;
            _to = target.GlobalPosition + target.Size * 0.5f;

            source._GetDragData(Vector2.Zero);

            Control preview = source.LastDragPreview;
            if (preview == null)
            {
                GD.PrintErr("DragShot: the slot produced no drag preview.");
                GetTree().Quit(1);
                return;
            }

            // Halfway between the two slots, where a real cursor would be.
            // Offset from the source slot, where a cursor mid-drag would be.
            // Parked well clear of the panel: the preview is parented to the
            // viewport, below the inventory's CanvasLayer, so anywhere over
            // the panel it would simply be hidden behind it.
            preview.Position = new Vector2(1000f, 620f);
            _hud.GetViewport().AddChild(preview);
            return;
        }

        if (_frames == WarmupFrames + DragFrames)
        {
            // Captured mid-drag, with the pointer between the two slots.
            Image image = GetViewport().GetTexture().GetImage();
            Error err = image.SavePng(OutputPath);
            GD.Print(err == Error.Ok
                ? $"DragShot: wrote {ProjectSettings.GlobalizePath(OutputPath)}"
                : $"DragShot: could not write {OutputPath} ({err})");

            GetTree().Quit(err == Error.Ok ? 0 : 1);
        }
    }

    private static InventorySlot FindSlot(Node root, int index)
    {
        if (root is InventorySlot slot && slot.Index == index)
            return slot;

        foreach (Node child in root.GetChildren())
        {
            InventorySlot found = FindSlot(child, index);
            if (found != null)
                return found;
        }

        return null;
    }
}
