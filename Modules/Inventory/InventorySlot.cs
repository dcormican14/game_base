using Godot;

namespace GameBase.Items;

/// <summary>
/// One square of the grid: draws its stack, and acts as both the source and
/// the target of a drag.
///
/// Swapping runs on Godot's built-in drag-and-drop (_GetDragData /
/// _CanDropData / _DropData) rather than on hand-rolled mouse tracking. That
/// gets the awkward parts right for free — a drag that leaves the window, a
/// button released over nothing, the preview following the cursor — and, more
/// usefully here, it means a slot never has to know which grid it belongs to.
/// The drag carries the source INDEX, both grids address the same
/// <see cref="Inventory"/>, and so dragging from the backpack to the hotbar is
/// the same code path as dragging within either one.
///
/// The slot draws itself rather than wearing a StyleBox so its border is built
/// from whole blocks on a pixel grid, matching the crosshair's construction.
/// </summary>
public partial class InventorySlot : Panel
{
    /// <summary>Emitted when the player drops <paramref name="fromIndex"/> onto this slot.</summary>
    [Signal]
    public delegate void SwapRequestedEventHandler(int fromIndex, int toIndex);

    /// <summary>Emitted when the slot is clicked without being dragged.</summary>
    [Signal]
    public delegate void ActivatedEventHandler(int index);

    /// <summary>Border thickness in screen pixels. Kept whole so it snaps to the grid.</summary>
    [Export(PropertyHint.Range, "1,8,1")] public int BorderWidth { get; set; } = 2;

    /// <summary>Index of this slot in the owning <see cref="Inventory"/>.</summary>
    public int Index { get; set; }

    /// <summary>Draws the selection border. Set by the hotbar, never by the backpack.</summary>
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; QueueRedraw(); } }
    }

    private bool _selected;
    private bool _hovered;

    /// <summary>True while this slot is the source of a drag in progress, so
    /// its item is showing under the cursor instead of here.</summary>
    private bool _dragging;
    private ItemStack _stack = ItemStack.Empty;
    private ItemIcon _icon;
    private Label _count;

    /// <summary>
    /// The number shown beneath a hotbar slot. Empty for backpack slots, which
    /// have no key of their own.
    /// </summary>
    public string HotkeyLabel { get; set; } = "";

    public ItemStack Stack
    {
        get => _stack;
        set
        {
            _stack = value;
            RefreshContents();
        }
    }

    /// <summary>
    /// True while this slot is the source of a drag, so its item is drawn
    /// under the cursor rather than here.
    ///
    /// The stack itself is unchanged — a cancelled drag has to leave the item
    /// where it was — so this is about what is DRAWN, which is the thing the
    /// player reads as "the item moved" or "the item got copied".
    /// </summary>
    public bool IsDragging => _dragging;

    /// <summary>What this slot is currently drawing: nothing while its item is
    /// out on a drag, its stack otherwise.</summary>
    public ItemStack VisibleStack => _dragging ? ItemStack.Empty : _stack;

    /// <summary>
    /// The preview control handed to the last drag started from this slot.
    ///
    /// Exposed so what follows the cursor can be inspected. Godot's
    /// SetDragPreview only works inside a drag it is itself running, so a test
    /// that calls _GetDragData directly cannot find the preview through the
    /// viewport — it has to be handed the same object the engine was.
    /// </summary>
    public Control LastDragPreview { get; private set; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;

        _icon = new ItemIcon();
        _icon.SetAnchorsPreset(LayoutPreset.FullRect);
        // Inset so the icon never touches the border blocks.
        _icon.OffsetLeft = 6;
        _icon.OffsetTop = 6;
        _icon.OffsetRight = -6;
        _icon.OffsetBottom = -6;
        AddChild(_icon);

        _count = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        // Anchored to the bottom-right corner rather than stretched over the
        // whole slot, so the numerals sit in the corner instead of floating
        // across the middle of the icon.
        _count.SetAnchorsPreset(LayoutPreset.BottomRight);
        _count.GrowHorizontal = GrowDirection.Begin;
        _count.GrowVertical = GrowDirection.Begin;
        // Clear of the border blocks on both edges, so a three-digit count
        // cannot ride over the frame.
        _count.OffsetLeft = -34;
        _count.OffsetTop = -21;
        _count.OffsetRight = -6;
        _count.OffsetBottom = -5;
        _count.AddThemeColorOverride("font_color", InventoryPalette.Text);
        _count.AddThemeColorOverride("font_shadow_color", InventoryPalette.TextShadow);
        _count.AddThemeConstantOverride("shadow_offset_x", 1);
        _count.AddThemeConstantOverride("shadow_offset_y", 1);
        _count.AddThemeFontSizeOverride("font_size", 13);
        AddChild(_count);

        MouseEntered += () => { _hovered = true; QueueRedraw(); };
        MouseExited += () => { _hovered = false; QueueRedraw(); };

        RefreshContents();
    }

    private void RefreshContents()
    {
        if (_icon == null)
            return;

        // While this slot is the source of a drag its item is under the
        // cursor, so the slot shows empty — the item is in one place, not two.
        ItemStack shown = VisibleStack;

        _icon.Item = shown.Type;
        // A count of one is noise — the icon already says there is one.
        _count.Text = shown.Count > 1 ? shown.Count.ToString() : "";
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            EmitSignal(SignalName.Activated, Index);
    }

    // ------------------------------------------------------------- drag & drop

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (_stack.IsEmpty)
            return default;

        // The item leaves this slot for the duration of the drag, so it is
        // only ever in ONE place: under the cursor. Drawing it here as well
        // reads as the item having been copied — the model was always right,
        // but "it is in two places" is what the player sees, and that is the
        // thing being fixed.
        //
        // Hidden rather than cleared: the stack itself must stay put, because
        // a drag that ends over nothing is cancelled and the item has to still
        // be here. _Notification restores it however the drag ends.
        SetDragging(true);

        // Just the item, on nothing. A framed miniature reads as a second slot
        // being dragged around rather than as the item itself, and it is the
        // frame that made stale contents so obvious when one appeared.
        float side = Mathf.Max(32f, Mathf.Min(Size.X, Size.Y) - 8f);
        var previewIcon = new ItemIcon
        {
            Item = _stack.Type,
            CustomMinimumSize = new Vector2(side, side),
            Size = new Vector2(side, side),
            // Centred on the cursor rather than hanging below-right of it.
            Position = new Vector2(-side * 0.5f, -side * 0.5f),
        };

        // SetDragPreview positions what it is given at the cursor, so the
        // offset above has to live on a child of a plain wrapper rather than
        // on the previewed node itself.
        var wrapper = new Control();
        wrapper.AddChild(previewIcon);
        LastDragPreview = wrapper;
        SetDragPreview(wrapper);

        return Variant.From(Index);
    }

    /// <summary>
    /// Godot does not tell a drag's SOURCE that the drag ended, only its
    /// target — and a drag dropped on empty space has no target at all. This
    /// notification fires either way, so it is the one place that can reliably
    /// put a hidden item back.
    /// </summary>
    public override void _Notification(int what)
    {
        if (what == NotificationDragEnd)
            SetDragging(false);
    }

    /// <summary>Hides or restores this slot's contents while it is the source
    /// of a drag.</summary>
    private void SetDragging(bool dragging)
    {
        if (_dragging == dragging)
            return;

        _dragging = dragging;
        RefreshContents();
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) =>
        data.VariantType == Variant.Type.Int;

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        int from = data.AsInt32();
        if (from != Index)
            EmitSignal(SignalName.SwapRequested, from, Index);
    }

    // ------------------------------------------------------------------ drawing

    public override void _Draw()
    {
        Vector2 size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), InventoryPalette.SlotFill);

        Color border = _selected
            ? InventoryPalette.SlotBorderSelected
            : _hovered ? InventoryPalette.SlotBorderHover : InventoryPalette.SlotBorder;

        // Four rects rather than DrawRect's own outline mode: drawn this way
        // the corners are square blocks that meet exactly, which is what makes
        // the frame read as placed pixels rather than as a stroked rectangle.
        float w = BorderWidth;
        DrawRect(new Rect2(0, 0, size.X, w), border);
        DrawRect(new Rect2(0, size.Y - w, size.X, w), border);
        DrawRect(new Rect2(0, w, w, size.Y - w * 2f), border);
        DrawRect(new Rect2(size.X - w, w, w, size.Y - w * 2f), border);

        // The selected hotbar slot gets a second, inset frame. A thicker
        // single border would change the slot's apparent size as the player
        // scrolls along the bar, which reads as the bar jittering.
        if (_selected)
        {
            float inset = w + 2f;
            Color inner = InventoryPalette.SlotBorderSelected;
            inner.A *= 0.45f;
            DrawRect(new Rect2(inset, inset, size.X - inset * 2f, w), inner);
            DrawRect(new Rect2(inset, size.Y - inset - w, size.X - inset * 2f, w), inner);
            DrawRect(new Rect2(inset, inset + w, w, size.Y - (inset + w) * 2f), inner);
            DrawRect(new Rect2(size.X - inset - w, inset + w, w, size.Y - (inset + w) * 2f), inner);
        }
    }
}
