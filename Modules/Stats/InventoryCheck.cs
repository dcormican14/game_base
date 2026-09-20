using Godot;
using GameBase.Core;
using GameBase.Items;

namespace GameBase.Stats;

/// <summary>
/// Exercises the inventory's model and HUD wiring without a player driving it.
///
/// The parts worth checking are the ones that are invisible in a screenshot: a
/// swap that silently drops an item, a merge that exceeds the stack limit, a
/// slot index that stops matching its control after the grids are built in two
/// passes. Those all load fine and render fine — the panel simply holds the
/// wrong thing — so a smoke test that only asks whether the scene opened would
/// miss every one of them.
///
/// Run headless after any change to Inventory, InventorySlot or InventoryHud.
/// </summary>
public partial class InventoryCheck : Node
{
    /// <summary>Frames to wait before checking, so _Ready has run everywhere.</summary>
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
            GD.PrintErr("InventoryCheck: no InventoryHud in the scene.");
            GetTree().Quit(1);
            return;
        }

        Inventory inv = hud.Inventory;

        CheckLayout(inv);
        CheckStartingItem(inv);
        CheckSwap(inv);
        CheckStackMerge(inv);
        CheckOverflowIsReturned(inv);
        CheckSelectionWraps(inv);
        CheckTools();
        CheckHotbarMoves(hud, inv);

        GD.Print(_failures == 0
            ? "InventoryCheck: PASS"
            : $"InventoryCheck: FAIL ({_failures} problem(s))");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Fail(string message)
    {
        _failures++;
        GD.PrintErr($"  FAIL {message}");
    }

    private void CheckLayout(Inventory inv)
    {
        if (inv.SlotCount != inv.HotbarSize + inv.BackpackSize)
            Fail($"slot count {inv.SlotCount} != hotbar {inv.HotbarSize} + backpack {inv.BackpackSize}");

        if (!inv.IsHotbarIndex(0) || inv.IsHotbarIndex(inv.HotbarSize))
            Fail("hotbar/backpack boundary is wrong");

        GD.Print($"  layout: {inv.HotbarSize} hotbar + {inv.BackpackSize} backpack = {inv.SlotCount}");
    }

    private void CheckStartingItem(Inventory inv)
    {
        ItemStack first = inv.GetSlot(0);
        if (first.IsEmpty)
        {
            Fail("slot 0 is empty — the starting green cube did not arrive");
            return;
        }

        if (first.Type.ResolvedIconMesh == null)
            Fail($"'{first.Type.DisplayName}' has no icon mesh, so its slot renders blank");

        GD.Print($"  slot 0 holds {first} (mesh: {first.Type.ResolvedIconMesh?.GetType().Name ?? "none"})");
    }

    /// <summary>A swap must move both stacks, not overwrite one with the other.</summary>
    private void CheckSwap(Inventory inv)
    {
        ItemStack original = inv.GetSlot(0);
        if (original.IsEmpty)
            return;

        // Into the backpack and back again: the round trip is where an
        // off-by-one between grid position and slot index would show up.
        int target = inv.HotbarSize;
        inv.Swap(0, target);

        if (!inv.GetSlot(0).IsEmpty)
            Fail("source slot still holds an item after swapping into an empty slot");
        if (inv.GetSlot(target).Type != original.Type)
            Fail("item did not arrive in the target slot");

        inv.Swap(target, 0);
        if (inv.GetSlot(0).Type != original.Type)
            Fail("item did not come back after swapping twice");
        else
            GD.Print("  swap: hotbar -> backpack -> hotbar round trip intact");
    }

    /// <summary>
    /// First item in the inventory that stacks, for the checks that are about
    /// stacking behaviour rather than about any particular item.
    /// </summary>
    private static ItemType FindStackableType(Inventory inv)
    {
        for (int i = 0; i < inv.SlotCount; i++)
        {
            ItemType type = inv.GetSlot(i).Type;
            if (type != null && type.MaxStack >= 4)
                return type;
        }

        return null;
    }

    /// <summary>Dropping like onto like merges, and never exceeds MaxStack.</summary>
    private void CheckStackMerge(Inventory inv)
    {
        ItemType type = FindStackableType(inv);
        if (type == null)
            return;

        inv.SetSlot(0, new ItemStack(type, 2));
        inv.SetSlot(1, new ItemStack(type, 3));
        inv.Swap(0, 1);

        if (inv.GetSlot(1).Count != 5)
            Fail($"merging 2 onto 3 gave {inv.GetSlot(1).Count}, expected 5");
        if (!inv.GetSlot(0).IsEmpty)
            Fail("source slot kept items after a merge that had room for all of them");

        // A merge that cannot fit everything must leave the rest behind.
        inv.SetSlot(0, new ItemStack(type, type.MaxStack));
        inv.SetSlot(1, new ItemStack(type, type.MaxStack));
        inv.Swap(0, 1);
        int total = inv.GetSlot(0).Count + inv.GetSlot(1).Count;
        if (total != type.MaxStack * 2)
            Fail($"a full-to-full merge lost items: {total} of {type.MaxStack * 2} left");
        if (inv.GetSlot(1).Count > type.MaxStack)
            Fail("merge pushed a stack past MaxStack");
        else
            GD.Print($"  merge: stacks combine and cap at MaxStack ({type.MaxStack})");

        inv.SetSlot(0, new ItemStack(type, 1));
        inv.SetSlot(1, ItemStack.Empty);
    }

    /// <summary>A full inventory must hand back what it could not take.</summary>
    private void CheckOverflowIsReturned(Inventory inv)
    {
        // Must be an item that STACKS. A stack is clamped to its type's
        // MaxStack on construction, so asking a non-stacking tool for seven
        // yields a stack of one and the test would measure the clamp rather
        // than the overflow.
        ItemType type = FindStackableType(inv);
        if (type == null)
            return;

        inv.Clear();
        for (int i = 0; i < inv.SlotCount; i++)
            inv.SetSlot(i, new ItemStack(type, type.MaxStack));

        ItemStack overflow = inv.TryAdd(type, 7);
        if (overflow.Count != 7)
            Fail($"a full inventory swallowed items: {7 - overflow.Count} of 7 vanished");
        else
            GD.Print("  overflow: a full inventory returns what it cannot hold");

        inv.Clear();
        inv.TryAdd(type, 1);
    }

    /// <summary>
    /// The generated tool shapes produce real geometry, and each is distinct.
    ///
    /// A shape enum that silently falls through to the default would leave a
    /// tool with a null mesh and a blank slot; two shapes that accidentally
    /// resolve to the SAME cached mesh would draw a pickaxe as a shovel. Both
    /// look like an art problem rather than a code one, so they are worth
    /// asserting.
    /// </summary>
    private void CheckTools()
    {
        var shovel = ResourceLoader.Load<ItemType>("res://Modules/Inventory/Items/Shovel.tres");
        var pickaxe = ResourceLoader.Load<ItemType>("res://Modules/Inventory/Items/Pickaxe.tres");

        if (shovel == null || pickaxe == null)
        {
            Fail("shovel or pickaxe resource is missing");
            return;
        }

        foreach (ItemType tool in new[] { shovel, pickaxe })
        {
            Mesh mesh = tool.ResolvedIconMesh;
            if (mesh == null)
            {
                Fail($"'{tool.DisplayName}' resolves to no mesh, so its slot renders blank");
                continue;
            }

            if (mesh.GetSurfaceCount() == 0)
            {
                Fail($"'{tool.DisplayName}' built a mesh with no surfaces");
                continue;
            }

            // A tool that does not stack is the intent; a tool that stacks
            // would let the player merge two distinct ones into a pile.
            if (tool.MaxStack != 1)
                Fail($"'{tool.DisplayName}' stacks to {tool.MaxStack}; tools should not stack");

            Aabb bounds = mesh.GetAabb();
            GD.Print($"  {tool.DisplayName}: {mesh.GetSurfaceCount()} surface(s), "
                     + $"extent {bounds.Size.X:F2} x {bounds.Size.Y:F2}");
        }

        if (shovel.ResolvedIconMesh == pickaxe.ResolvedIconMesh)
            Fail("shovel and pickaxe resolve to the same mesh instance");
    }

    /// <summary>
    /// The hotbar moves into the panel when it opens and back out when it
    /// closes, and there is only ever ONE set of hotbar slots.
    ///
    /// The in-panel row exists so items can be dragged between backpack and
    /// bar. If opening duplicated the slots instead of moving them, a drag
    /// would carry the index of whichever copy it started from and the two
    /// would drift apart — which is invisible until a player loses an item.
    /// </summary>
    private void CheckHotbarMoves(InventoryHud hud, Inventory inv)
    {
        Node closedParent = FindSlotParent(hud, 0);
        if (closedParent == null)
        {
            Fail("hotbar slot 0 has no parent while closed");
            return;
        }

        // Opening normally pauses the tree; this check only cares where the
        // slots end up, and a paused tree would stop the very _Process that
        // has to reach the end and report.
        bool pausedWhileOpen = hud.PauseWhileOpen;
        bool capturedOnClose = hud.CaptureMouseOnClose;
        hud.PauseWhileOpen = false;
        hud.CaptureMouseOnClose = false;

        hud.Open();
        Node openParent = FindSlotParent(hud, 0);

        if (openParent == closedParent)
            Fail("hotbar did not move into the panel on open");

        // Every hotbar slot must travel, not just the first.
        for (int i = 0; i < inv.HotbarSize; i++)
        {
            if (FindSlotParent(hud, i) != openParent)
            {
                Fail($"hotbar slot {i} did not move into the panel with the rest");
                break;
            }
        }

        if (CountSlotsWithIndex(hud, 0) != 1)
            Fail("more than one control claims hotbar slot 0 — the bar was copied, not moved");

        hud.Close();
        hud.PauseWhileOpen = pausedWhileOpen;
        hud.CaptureMouseOnClose = capturedOnClose;

        if (FindSlotParent(hud, 0) != closedParent)
            Fail("hotbar did not return to the screen row on close");
        else
            GD.Print("  hotbar: moves into the panel on open and back out on close");
    }

    private static Node FindSlotParent(Node root, int index)
    {
        InventorySlot slot = FindSlot(root, index);
        return slot?.GetParent();
    }

    private static InventorySlot FindSlot(Node node, int index)
    {
        if (node is InventorySlot slot && slot.Index == index)
            return slot;

        foreach (Node child in node.GetChildren())
        {
            InventorySlot found = FindSlot(child, index);
            if (found != null)
                return found;
        }

        return null;
    }

    private static int CountSlotsWithIndex(Node node, int index)
    {
        int count = node is InventorySlot slot && slot.Index == index ? 1 : 0;
        foreach (Node child in node.GetChildren())
            count += CountSlotsWithIndex(child, index);
        return count;
    }

    /// <summary>Selection stays on the bar and wraps at both ends.</summary>
    private void CheckSelectionWraps(Inventory inv)
    {
        inv.SelectedIndex = 0;
        inv.SelectedIndex--;
        if (inv.SelectedIndex != inv.HotbarSize - 1)
            Fail($"scrolling below 0 gave {inv.SelectedIndex}, expected {inv.HotbarSize - 1}");

        inv.SelectedIndex = inv.HotbarSize - 1;
        inv.SelectedIndex++;
        if (inv.SelectedIndex != 0)
            Fail($"scrolling past the end gave {inv.SelectedIndex}, expected 0");
        else
            GD.Print("  selection: wraps at both ends, never leaves the hotbar");

        inv.SelectedIndex = 0;
    }
}
