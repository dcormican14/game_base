using Godot;

namespace GameBase.Items;

/// <summary>
/// The player's carried items: one flat array of slots plus a selected index.
///
/// The hotbar is not a separate container — it is the FIRST
/// <see cref="HotbarSize"/> slots of the same array. That choice is what keeps
/// the rest of the module simple: moving an item between the bar and the
/// backpack is an ordinary swap between two indices rather than a transfer
/// between two collections with their own bounds, their own change signals and
/// their own rules about what may sit where.
///
/// This is pure data with signals — no nodes, no drawing. The HUD
/// (<see cref="InventoryHud"/>) listens and redraws; anything else that wants
/// to give the player an item calls <see cref="TryAdd"/> without knowing a UI
/// exists at all.
/// </summary>
public partial class Inventory : Node
{
    /// <summary>Emitted when the contents of <paramref name="index"/> change.</summary>
    [Signal]
    public delegate void SlotChangedEventHandler(int index);

    /// <summary>Emitted when the highlighted hotbar slot changes.</summary>
    [Signal]
    public delegate void SelectionChangedEventHandler(int index);

    /// <summary>Slots shown on the always-visible bar. Must be at least 1.</summary>
    [Export(PropertyHint.Range, "1,12,1")] public int HotbarSize { get; set; } = 9;

    /// <summary>Slots in the backpack, shown only while the inventory is open.</summary>
    [Export(PropertyHint.Range, "0,54,1")] public int BackpackSize { get; set; } = 27;

    private ItemStack[] _slots;
    private int _selected;

    /// <summary>Total slot count: hotbar followed by backpack.</summary>
    public int SlotCount => _slots?.Length ?? 0;

    /// <summary>Index of the highlighted hotbar slot, always within the hotbar.</summary>
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            int clamped = Mathf.PosMod(value, Mathf.Max(1, HotbarSize));
            if (clamped == _selected)
                return;
            _selected = clamped;
            EmitSignal(SignalName.SelectionChanged, _selected);
        }
    }

    /// <summary>What the player is currently holding on the bar.</summary>
    public ItemStack SelectedStack => GetSlot(_selected);

    public override void _Ready() => EnsureSlots();

    private void EnsureSlots()
    {
        int total = Mathf.Max(1, HotbarSize) + Mathf.Max(0, BackpackSize);
        if (_slots != null && _slots.Length == total)
            return;

        var grown = new ItemStack[total];
        if (_slots != null)
        {
            int carried = Mathf.Min(_slots.Length, total);
            System.Array.Copy(_slots, grown, carried);
        }

        _slots = grown;
    }

    /// <summary>True when <paramref name="index"/> names a real slot.</summary>
    public bool IsValidIndex(int index)
    {
        EnsureSlots();
        return index >= 0 && index < _slots.Length;
    }

    /// <summary>True when <paramref name="index"/> is on the hotbar rather than in the backpack.</summary>
    public bool IsHotbarIndex(int index) => index >= 0 && index < HotbarSize;

    public ItemStack GetSlot(int index) => IsValidIndex(index) ? _slots[index] : ItemStack.Empty;

    /// <summary>Replaces a slot outright, announcing the change.</summary>
    public void SetSlot(int index, ItemStack stack)
    {
        if (!IsValidIndex(index))
            return;

        _slots[index] = stack;
        EmitSignal(SignalName.SlotChanged, index);
    }

    /// <summary>
    /// Exchanges two slots. This is the single operation behind every way the
    /// player rearranges their items — dragging in the grid, dropping onto the
    /// bar, or moving a stack with the number keys — so there is one place
    /// where a rearrangement can go wrong, and one place that announces it.
    ///
    /// Stacks of the same item merge instead of trading places, since a player
    /// dropping twenty cubes onto thirty cubes means to combine them. Whatever
    /// exceeds the stack limit stays behind in the source slot rather than
    /// vanishing.
    /// </summary>
    public void Swap(int a, int b)
    {
        if (a == b || !IsValidIndex(a) || !IsValidIndex(b))
            return;

        ItemStack from = _slots[a];
        ItemStack to = _slots[b];

        if (to.CanStackWith(from))
        {
            _slots[b] = to.Merge(from, out ItemStack remainder);
            _slots[a] = remainder;
        }
        else
        {
            _slots[a] = to;
            _slots[b] = from;
        }

        EmitSignal(SignalName.SlotChanged, a);
        EmitSignal(SignalName.SlotChanged, b);
    }

    /// <summary>
    /// Puts <paramref name="stack"/> into the inventory, topping up matching
    /// stacks before opening a new slot so a pickup does not fragment across
    /// the grid. Returns whatever did not fit; an empty return means all of it
    /// was taken.
    ///
    /// Callers should check the result rather than assume success — a full
    /// inventory is normal, and silently dropping the overflow would destroy
    /// the player's items.
    /// </summary>
    public ItemStack TryAdd(ItemStack stack)
    {
        EnsureSlots();
        if (stack.IsEmpty)
            return ItemStack.Empty;

        for (int i = 0; i < _slots.Length && !stack.IsEmpty; i++)
        {
            if (!_slots[i].CanStackWith(stack))
                continue;

            _slots[i] = _slots[i].Merge(stack, out ItemStack remainder);
            stack = remainder;
            EmitSignal(SignalName.SlotChanged, i);
        }

        for (int i = 0; i < _slots.Length && !stack.IsEmpty; i++)
        {
            if (!_slots[i].IsEmpty)
                continue;

            // Split through a temporary rather than passing `stack` as both
            // the receiver and the `out` target. Aliasing the two makes the
            // result depend on when the callee writes `left` relative to its
            // return, which is exactly the kind of detail that should not
            // decide whether the player keeps their items.
            ItemStack placed = stack.Take(stack.Type.MaxStack, out ItemStack rest);
            stack = rest;
            _slots[i] = placed;
            EmitSignal(SignalName.SlotChanged, i);
        }

        return stack;
    }

    /// <summary>Convenience overload for handing over a plain count of an item.</summary>
    public ItemStack TryAdd(ItemType type, int count = 1) => TryAdd(new ItemStack(type, count));

    /// <summary>Empties every slot.</summary>
    public void Clear()
    {
        EnsureSlots();
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].IsEmpty)
                continue;
            _slots[i] = ItemStack.Empty;
            EmitSignal(SignalName.SlotChanged, i);
        }
    }
}
