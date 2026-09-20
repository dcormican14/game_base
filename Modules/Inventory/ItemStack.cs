namespace GameBase.Items;

/// <summary>
/// What one slot holds: a type and a count, or nothing.
///
/// A readonly struct rather than a class so an empty slot is a value
/// (<see cref="Empty"/>) rather than a null that every caller has to test for,
/// and so copying a stack out of a slot cannot alias the slot's contents —
/// swapping two slots is then plain assignment rather than a dance of
/// temporaries and defensive clones.
/// </summary>
public readonly struct ItemStack
{
    /// <summary>An empty slot.</summary>
    public static readonly ItemStack Empty = default;

    public ItemType Type { get; }

    /// <summary>How many are held. Always 0 when <see cref="Type"/> is null.</summary>
    public int Count { get; }

    public ItemStack(ItemType type, int count = 1)
    {
        // A stack of zero and a stack of nothing are the same thing; collapsing
        // them here means IsEmpty is the only emptiness test anywhere else.
        if (type == null || count <= 0)
        {
            Type = null;
            Count = 0;
            return;
        }

        Type = type;
        Count = count < type.MaxStack ? count : type.MaxStack;
    }

    public bool IsEmpty => Type == null;

    /// <summary>Room left before this stack hits its type's limit.</summary>
    public int SpaceLeft => IsEmpty ? 0 : Type.MaxStack - Count;

    /// <summary>True when <paramref name="other"/> could merge into this stack.</summary>
    public bool CanStackWith(ItemStack other) =>
        !IsEmpty && !other.IsEmpty && other.Type == Type && SpaceLeft > 0;

    /// <summary>
    /// Merges as much of <paramref name="incoming"/> into this stack as fits.
    /// Returns the combined stack and, via <paramref name="remainder"/>,
    /// whatever did not fit — so a caller can put the leftovers back rather
    /// than losing them.
    /// </summary>
    public ItemStack Merge(ItemStack incoming, out ItemStack remainder)
    {
        if (!CanStackWith(incoming))
        {
            remainder = incoming;
            return this;
        }

        int moved = incoming.Count < SpaceLeft ? incoming.Count : SpaceLeft;
        remainder = new ItemStack(incoming.Type, incoming.Count - moved);
        return new ItemStack(Type, Count + moved);
    }

    /// <summary>Splits <paramref name="amount"/> off, leaving the rest behind.</summary>
    public ItemStack Take(int amount, out ItemStack left)
    {
        if (IsEmpty || amount <= 0)
        {
            left = this;
            return Empty;
        }

        int taken = amount < Count ? amount : Count;
        left = new ItemStack(Type, Count - taken);
        return new ItemStack(Type, taken);
    }

    public override string ToString() => IsEmpty ? "(empty)" : $"{Type.DisplayName} x{Count}";
}
