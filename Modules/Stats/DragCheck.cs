using Godot;
using GameBase.Core;
using GameBase.Items;

namespace GameBase.Stats;

/// <summary>
/// Checks that a dragged item is only ever in ONE place, and that the model
/// survives every way a drag can end.
///
/// The visible half matters as much as the model half here. The inventory
/// never actually duplicated anything — the stack only moved on drop — but
/// during a drag the item was drawn both in its slot and under the cursor,
/// and an item visibly in two places is indistinguishable from a duplication
/// bug. So this asserts what is DRAWN, not just what is stored.
///
/// The cancelled drag is the case worth guarding hardest: hiding the source
/// slot's item is easy, and putting it back when the player releases over
/// nothing is what gets forgotten — and that failure DELETES an item from the
/// player's view while leaving it in the model, which is the worst of both.
/// </summary>
public partial class DragCheck : Node
{
    [Export] public int Frames { get; set; } = 5;

    private int _frames;
    private int _failures;

    public override void _Process(double delta)
    {
        if (++_frames < Frames)
            return;

        var hud = NodeSearch.FindByType<InventoryHud>(GetTree().CurrentScene);
        if (hud == null)
        {
            GD.PrintErr("DragCheck: no InventoryHud in the scene.");
            GetTree().Quit(1);
            return;
        }

        // Open it: the backpack slots only exist in the panel.
        hud.PauseWhileOpen = false;
        hud.CaptureMouseOnClose = false;
        hud.Open();

        CheckSourceHidesWhileDragging(hud);
        CheckCancelledDragRestores(hud);
        CheckPreviewIsBareIcon(hud);
        CheckCompletedDragMovesOnce(hud);

        hud.Close();

        GD.Print(_failures == 0
            ? "DragCheck: PASS"
            : $"DragCheck: FAIL ({_failures} problem(s))");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Fail(string message)
    {
        _failures++;
        GD.PrintErr($"  FAIL {message}");
    }

    /// <summary>The item is drawn under the cursor, so not also in its slot.</summary>
    private void CheckSourceHidesWhileDragging(InventoryHud hud)
    {
        InventorySlot slot = FindSlotWithItem(hud);
        if (slot == null)
        {
            Fail("no slot holds an item to drag");
            return;
        }

        ItemStack before = slot.Stack;
        slot._GetDragData(Vector2.Zero);

        if (!slot.IsDragging)
            Fail("starting a drag did not mark the source slot as dragging");

        if (!slot.VisibleStack.IsEmpty)
            Fail("the source slot still draws its item during the drag — it looks copied");

        // The MODEL must not move yet: the drag can still be cancelled.
        if (slot.Stack.Type != before.Type || slot.Stack.Count != before.Count)
            Fail("starting a drag changed the stack before anything was dropped");
        else
            GD.Print("  drag start: item is drawn once, and the stack has not moved yet");

        slot._Notification((int)Control.NotificationDragEnd);
    }

    /// <summary>A drag released over nothing puts the item back.</summary>
    private void CheckCancelledDragRestores(InventoryHud hud)
    {
        InventorySlot slot = FindSlotWithItem(hud);
        if (slot == null)
            return;

        ItemStack before = slot.Stack;
        slot._GetDragData(Vector2.Zero);

        // What Godot sends every Control when a drag ends, whether or not it
        // was dropped on anything.
        slot._Notification((int)Control.NotificationDragEnd);

        if (slot.IsDragging)
            Fail("the slot is still marked as dragging after the drag ended");

        if (slot.VisibleStack.IsEmpty)
            Fail("a cancelled drag left the slot looking empty — the item vanished from view");

        if (slot.Stack.Type != before.Type || slot.Stack.Count != before.Count)
            Fail("a cancelled drag altered the stack");
        else
            GD.Print("  drag cancelled: the item comes back, unchanged");
    }

    /// <summary>
    /// The thing following the cursor is the item and nothing else: no slot
    /// frame, no background, and exactly one icon.
    /// </summary>
    private void CheckPreviewIsBareIcon(InventoryHud hud)
    {
        InventorySlot slot = FindSlotWithItem(hud);
        if (slot == null)
            return;

        slot._GetDragData(Vector2.Zero);

        Control preview = slot.LastDragPreview;
        if (preview == null)
        {
            Fail("no drag preview was created");
            slot._Notification((int)Control.NotificationDragEnd);
            return;
        }

        int panels = CountOfType<Panel>(preview);
        int icons = CountOfType<ItemIcon>(preview);

        if (panels > 0)
            Fail($"the drag preview draws {panels} panel(s) — it should be the bare item");

        if (icons != 1)
            Fail($"the drag preview holds {icons} icons, expected exactly 1");
        else if (panels == 0)
            GD.Print("  drag preview: one bare icon, no frame and no extra items");

        slot._Notification((int)Control.NotificationDragEnd);
    }

    /// <summary>A completed drag moves the item once — it does not leave a copy.</summary>
    private void CheckCompletedDragMovesOnce(InventoryHud hud)
    {
        Inventory inv = hud.Inventory;
        InventorySlot source = FindSlotWithItem(hud);
        if (source == null)
            return;

        int target = -1;
        for (int i = 0; i < inv.SlotCount; i++)
        {
            if (inv.GetSlot(i).IsEmpty)
            {
                target = i;
                break;
            }
        }

        if (target < 0)
        {
            Fail("no empty slot to drag into");
            return;
        }

        ItemType moved = source.Stack.Type;
        int sourceIndex = source.Index;
        int countBefore = CountOf(inv, moved);

        Variant data = source._GetDragData(Vector2.Zero);
        InventorySlot targetSlot = FindSlot(hud, target);
        targetSlot?._DropData(Vector2.Zero, data);
        source._Notification((int)Control.NotificationDragEnd);

        if (!inv.GetSlot(sourceIndex).IsEmpty)
            Fail("the source slot kept the item after it was dropped elsewhere");

        if (inv.GetSlot(target).Type != moved)
            Fail("the item did not arrive in the slot it was dropped on");

        int countAfter = CountOf(inv, moved);
        if (countAfter != countBefore)
            Fail($"the item count changed across the drag: {countBefore} -> {countAfter}");
        else
            GD.Print($"  drag completed: item moved, total unchanged ({countAfter})");
    }

    // ------------------------------------------------------------------ helpers

    private static int CountOf(Inventory inv, ItemType type)
    {
        int total = 0;
        for (int i = 0; i < inv.SlotCount; i++)
        {
            ItemStack stack = inv.GetSlot(i);
            if (stack.Type == type)
                total += stack.Count;
        }

        return total;
    }

    private static InventorySlot FindSlotWithItem(Node root)
    {
        if (root is InventorySlot slot && !slot.Stack.IsEmpty && !slot.IsDragging)
            return slot;

        foreach (Node child in root.GetChildren())
        {
            InventorySlot found = FindSlotWithItem(child);
            if (found != null)
                return found;
        }

        return null;
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

    private static int CountOfType<T>(Node node) where T : Node
    {
        int count = node is T ? 1 : 0;
        foreach (Node child in node.GetChildren())
            count += CountOfType<T>(child);
        return count;
    }
}
