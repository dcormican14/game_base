using Godot;
using GameBase.Items;
using GameBase.Tools;

namespace GameBase.Tests;

/// <summary>The inventory's stacking rules, and which items are tools.</summary>
public sealed class InventoryTests : TestSuite
{
    private static ItemType Item(string id, int maxStack) =>
        new() { Id = id, DisplayName = id, MaxStack = maxStack };

    private Inventory NewInventory() => Add(new Inventory { HotbarSize = 3, BackpackSize = 2 });

    [Test]
    public void AddingTopsUpBeforeOpeningSlots()
    {
        Inventory inventory = NewInventory();
        ItemType cube = Item("cube", 10);

        Check(inventory.TryAdd(cube, 7).IsEmpty, "seven cubes did not fit");
        Check(inventory.TryAdd(cube, 7).IsEmpty, "seven more did not fit");

        Equal(inventory.GetSlot(0).Count, 10, "first slot");
        Equal(inventory.GetSlot(1).Count, 4, "second slot");
    }

    [Test]
    public void OverflowIsReturned()
    {
        Inventory inventory = NewInventory();
        ItemType rock = Item("rock", 2);

        // A stack never holds more than its item's limit, so fill slot by slot.
        for (int n = 0; n < 5; n++)
            Check(inventory.TryAdd(rock, 2).IsEmpty, $"stack {n} did not fit in five free slots");

        Equal(inventory.TryAdd(rock, 1).Count, 1, "what a full inventory hands back");
    }

    [Test]
    public void SwappingLikeStacksMerges()
    {
        Inventory inventory = NewInventory();
        ItemType cube = Item("cube", 10);

        inventory.SetSlot(0, new ItemStack(cube, 6));
        inventory.SetSlot(1, new ItemStack(cube, 6));
        inventory.Swap(0, 1);

        Equal(inventory.GetSlot(1).Count, 10, "merged stack");
        Equal(inventory.GetSlot(0).Count, 2, "remainder");
    }

    [Test]
    public void SelectionWrapsWithinTheHotbar()
    {
        Inventory inventory = NewInventory();

        inventory.SelectedIndex = 4;
        Equal(inventory.SelectedIndex, 1, "index 4 on a bar of 3");

        inventory.SelectedIndex = -1;
        Equal(inventory.SelectedIndex, 2, "index -1 on a bar of 3");
    }

    [Test]
    public void ToolItemsResolveToTools()
    {
        Check(ToolRegistry.For(Item("pickaxe", 1)) is PickaxeTool, "the pickaxe item is not the pickaxe");
        Check(ToolRegistry.For(Item("shovel", 1)) is ShovelTool, "the shovel item is not the shovel");
        Check(ToolRegistry.For(Item("stone", 64)) == null, "a stone block is a tool");
    }
}
