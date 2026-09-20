using Godot;
using GameBase.Core;
using System.Collections.Generic;

namespace GameBase.Items;

/// <summary>
/// The inventory's whole player-facing side: a hotbar that is always on
/// screen, a backpack panel that opens on the <see cref="ToggleAction"/> key,
/// and the input that drives both.
///
/// Instance InventoryHud.tscn into a gameplay scene and it works — it builds
/// its own slots from the <see cref="Inventory"/> it carries, so changing the
/// hotbar or backpack size needs no edits here or in the scene.
///
/// Opening pauses the tree exactly as PauseMenu does, and for the same reason:
/// the player is looking away from the world with the mouse free, and letting
/// the simulation run while they cannot respond to it is how people lose a
/// character to something that walked up behind the panel. The CanvasLayer
/// runs with ProcessMode.Always so the panel keeps working while paused.
///
/// Its layer sits below PauseMenu's so that Escape's menu draws over the
/// inventory rather than under it.
/// </summary>
public partial class InventoryHud : CanvasLayer
{
    /// <summary>InputMap action that opens and closes the backpack.</summary>
    [Export] public StringName ToggleAction { get; set; } = "toggle_inventory";

    /// <summary>
    /// Action that closes the backpack without opening it — Escape, shared
    /// with the pause menu. Which of the two acts is decided by
    /// <see cref="UiStateService"/> rather than by listener order.
    /// </summary>
    [Export] public StringName CloseAction { get; set; } = "pause";

    /// <summary>Pause the scene tree while the backpack is open.</summary>
    [Export] public bool PauseWhileOpen { get; set; } = true;

    /// <summary>Recapture the mouse on close, matching PauseMenu's behaviour.</summary>
    [Export] public bool CaptureMouseOnClose { get; set; } = true;

    /// <summary>Let the mouse wheel move the hotbar selection.</summary>
    [Export] public bool ScrollSelectsHotbar { get; set; } = true;

    /// <summary>Slot edge length in pixels, before the panel's own margins.</summary>
    [Export(PropertyHint.Range, "32,128,1")] public int SlotSize { get; set; } = 56;

    /// <summary>Gap between slots.</summary>
    [Export(PropertyHint.Range, "0,24,1")] public int SlotSeparation { get; set; } = 6;

    /// <summary>Backpack columns. The hotbar is always one row of its own size.</summary>
    [Export(PropertyHint.Range, "1,12,1")] public int BackpackColumns { get; set; } = 9;

    /// <summary>
    /// Items granted once on startup, for testing and for a game that starts
    /// the player with a kit. Each entry fills as many slots as it needs.
    /// </summary>
    [Export] public ItemType[] StartingItems { get; set; } = System.Array.Empty<ItemType>();

    /// <summary>How many of each starting item to grant.</summary>
    [Export(PropertyHint.Range, "1,999,1")] public int StartingItemCount { get; set; } = 1;

    /// <summary>
    /// Suppresses the panel while a level is still building, mirroring
    /// PauseMenu.Suspended — pausing the tree behind a loading screen stops
    /// the very work being waited on.
    /// </summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            _suspended = value;
            if (_suspended && IsOpen)
                Close();
        }
    }

    private bool _suspended;

    public Inventory Inventory { get; private set; }

    public bool IsOpen => _backpackRoot != null && _backpackRoot.Visible;

    private Control _backpackRoot;
    private GridContainer _backpackGrid;

    /// <summary>Where the bar lives while the inventory is closed: the bottom
    /// of the screen.</summary>
    private HBoxContainer _hotbarRow;

    /// <summary>Where the bar lives while the inventory is open: inside the
    /// panel, under the backpack grid.</summary>
    private HBoxContainer _panelHotbarRow;

    private readonly List<InventorySlot> _slots = new();

    public override void _Ready()
    {
        Inventory = GetNode<Inventory>("%Inventory");
        _backpackRoot = GetNode<Control>("%BackpackRoot");
        _backpackGrid = GetNode<GridContainer>("%BackpackGrid");
        _hotbarRow = GetNode<HBoxContainer>("%HotbarRow");
        _panelHotbarRow = GetNode<HBoxContainer>("%PanelHotbarRow");

        GetNode<ColorRect>("%Dim").Color = InventoryPalette.Dim;
        GetNode<Label>("%BackpackTitle").AddThemeColorOverride("font_color", InventoryPalette.Text);
        StylePanel(GetNode<PanelContainer>("%BackpackPanel"));
        StyleCloseButton(GetNode<Button>("%CloseButton"));

        Inventory.SlotChanged += OnSlotChanged;
        Inventory.SelectionChanged += OnSelectionChanged;

        BuildSlots();
        GrantStartingItems();

        _backpackRoot.Visible = false;
    }

    public override void _ExitTree()
    {
        if (Inventory == null)
            return;
        Inventory.SlotChanged -= OnSlotChanged;
        Inventory.SelectionChanged -= OnSelectionChanged;
    }

    /// <summary>
    /// Dresses the X in the panel's own palette.
    ///
    /// Dim at rest and full off-white under the cursor, so it reads as a way
    /// out without competing with the items for attention — the player's eye
    /// belongs on the grid, and a bright control in the corner pulls against
    /// that until the moment they actually want it.
    /// </summary>
    private void StyleCloseButton(Button close)
    {
        close.Pressed += Close;
        close.FocusMode = Control.FocusModeEnum.None;

        close.AddThemeColorOverride("font_color", InventoryPalette.TextDim);
        close.AddThemeColorOverride("font_hover_color", InventoryPalette.Text);
        close.AddThemeColorOverride("font_pressed_color", InventoryPalette.Text);
        close.AddThemeColorOverride("font_focus_color", InventoryPalette.Text);
        close.AddThemeFontSizeOverride("font_size", 18);

        var hover = new StyleBoxFlat { BgColor = InventoryPalette.SlotFill };
        hover.SetBorderWidthAll(1);
        hover.BorderColor = InventoryPalette.SlotBorderHover;
        close.AddThemeStyleboxOverride("hover", hover);
        close.AddThemeStyleboxOverride("pressed", hover);
    }

    /// <summary>Applies the plum window colour to the backpack's frame.</summary>
    private static void StylePanel(PanelContainer panel)
    {
        var style = new StyleBoxFlat
        {
            BgColor = InventoryPalette.Window,
            BorderColor = InventoryPalette.SlotBorder,
            ContentMarginLeft = 20,
            ContentMarginRight = 20,
            ContentMarginTop = 16,
            ContentMarginBottom = 16,
        };
        // Square corners: a rounded panel cannot be built out of whole pixels
        // at this size without the curve turning to mush.
        style.SetBorderWidthAll(2);
        panel.AddThemeStyleboxOverride("panel", style);
    }

    // -------------------------------------------------------------------- build

    private void BuildSlots()
    {
        foreach (InventorySlot slot in _slots)
            slot.QueueFree();
        _slots.Clear();

        _hotbarRow.AddThemeConstantOverride("separation", SlotSeparation);
        _panelHotbarRow.AddThemeConstantOverride("separation", SlotSeparation);
        _backpackGrid.Columns = Mathf.Max(1, BackpackColumns);
        _backpackGrid.AddThemeConstantOverride("h_separation", SlotSeparation);
        _backpackGrid.AddThemeConstantOverride("v_separation", SlotSeparation);

        // Backpack first, so the grid reads top-left to bottom-right in slot
        // order, then the hotbar below it — the same order the indices run in.
        for (int i = Inventory.HotbarSize; i < Inventory.SlotCount; i++)
            _backpackGrid.AddChild(MakeSlot(i));

        for (int i = 0; i < Inventory.HotbarSize; i++)
            _hotbarRow.AddChild(MakeSlot(i));

        RefreshAll();
    }

    private InventorySlot MakeSlot(int index)
    {
        var slot = new InventorySlot
        {
            Index = index,
            CustomMinimumSize = new Vector2(SlotSize, SlotSize),
            // Number keys only reach the first nine, so only those are labelled.
            HotkeyLabel = Inventory.IsHotbarIndex(index) && index < 9
                ? (index + 1).ToString()
                : "",
        };

        slot.SwapRequested += OnSwapRequested;
        slot.Activated += OnSlotActivated;

        // Insert into the flat list at its own index so _slots[i] is always
        // inventory slot i, whichever container it was parented to.
        while (_slots.Count <= index)
            _slots.Add(null);
        _slots[index] = slot;

        return slot;
    }

    private void GrantStartingItems()
    {
        if (StartingItems == null)
            return;

        foreach (ItemType item in StartingItems)
        {
            if (item == null)
                continue;

            ItemStack overflow = Inventory.TryAdd(item, StartingItemCount);
            if (!overflow.IsEmpty)
                GD.PushWarning($"InventoryHud: no room for {overflow} at startup.");
        }
    }

    // -------------------------------------------------------------------- input

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed(ToggleAction))
        {
            if (_suspended)
            {
                // Swallowed rather than ignored, so the key cannot fall
                // through to anything else while the level is still building.
                GetViewport().SetInputAsHandled();
                return;
            }

            // Tab opens the inventory from the world, and closes it again
            // while it is the screen on top. It does nothing underneath
            // another screen — Tab in the pause menu should not swap it for
            // the inventory.
            UiStateService toggleStates = UiStateService.Instance;
            bool mayToggle = toggleStates == null
                || toggleStates.IsCurrent(UiState.Gameplay)
                || toggleStates.IsCurrent(UiState.Inventory);

            if (mayToggle)
                Toggle();

            GetViewport().SetInputAsHandled();
            return;
        }

        // Escape closes the inventory and hands the player straight back to
        // the world, rather than falling through to the pause menu. The state
        // service arbitrates: this only fires while the inventory is the
        // screen on top, so Escape means the pause menu everywhere else.
        if (@event.IsActionPressed(CloseAction) && IsOpen)
        {
            UiStateService states = UiStateService.Instance;
            if (states == null || states.IsCurrent(UiState.Inventory))
            {
                Close();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        // Hotbar selection is a gameplay control: it belongs to the closed
        // state, where the mouse is captured and the player is looking at the
        // world. With the panel open the wheel and number keys should be free
        // for the panel itself.
        if (IsOpen || _suspended)
            return;

        if (ScrollSelectsHotbar && @event is InputEventMouseButton { Pressed: true } wheel)
        {
            if (wheel.ButtonIndex == MouseButton.WheelUp)
            {
                Inventory.SelectedIndex--;
                GetViewport().SetInputAsHandled();
            }
            else if (wheel.ButtonIndex == MouseButton.WheelDown)
            {
                Inventory.SelectedIndex++;
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            int number = (int)(key.Keycode - Key.Key1);
            if (number >= 0 && number < 9 && number < Inventory.HotbarSize)
            {
                Inventory.SelectedIndex = number;
                GetViewport().SetInputAsHandled();
            }
        }
    }

    // --------------------------------------------------------------- open/close

    public void Toggle()
    {
        if (IsOpen)
            Close();
        else
            Open();
    }

    public void Open()
    {
        if (_suspended || IsOpen)
            return;

        UiStateService.Instance?.Push(UiState.Inventory);
        MoveHotbar(_panelHotbarRow);
        _backpackRoot.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        if (PauseWhileOpen)
            GetTree().Paused = true;
    }

    public void Close()
    {
        if (!IsOpen)
            return;

        UiStateService.Instance?.Close(UiState.Inventory);
        MoveHotbar(_hotbarRow);
        _backpackRoot.Visible = false;
        if (PauseWhileOpen)
            GetTree().Paused = false;
        if (CaptureMouseOnClose)
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>
    /// Moves the hotbar slots into <paramref name="destination"/>, which is
    /// the panel's own row while the inventory is open and the screen-bottom
    /// row while it is closed.
    ///
    /// The slots are REPARENTED rather than mirrored by a second set of
    /// controls. Two sets would both have to be kept in step with the model
    /// and with each other, and — worse for the thing this row exists for —
    /// a drag would carry the index of whichever copy it started from, so
    /// dropping a backpack item onto the panel's bar would update the copy the
    /// player is not looking at. One set of slots cannot disagree with itself.
    /// </summary>
    private void MoveHotbar(Container destination)
    {
        if (destination == null)
            return;

        for (int i = 0; i < Inventory.HotbarSize && i < _slots.Count; i++)
        {
            InventorySlot slot = _slots[i];
            if (slot == null || slot.GetParent() == destination)
                continue;

            slot.GetParent()?.RemoveChild(slot);
            destination.AddChild(slot);
        }
    }

    // ------------------------------------------------------------------ signals

    private void OnSwapRequested(int from, int to) => Inventory.Swap(from, to);

    /// <summary>
    /// Clicking a hotbar slot selects it. Clicking a backpack slot does
    /// nothing yet — the click is what starts a drag, and acting on press as
    /// well would fire on every drag the player begins.
    /// </summary>
    private void OnSlotActivated(int index)
    {
        if (Inventory.IsHotbarIndex(index))
            Inventory.SelectedIndex = index;
    }

    private void OnSlotChanged(int index)
    {
        if (index >= 0 && index < _slots.Count && _slots[index] != null)
            _slots[index].Stack = Inventory.GetSlot(index);
    }

    private void OnSelectionChanged(int index)
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i] != null)
                _slots[i].IsSelected = Inventory.IsHotbarIndex(i) && i == index;
        }
    }

    private void RefreshAll()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i] == null)
                continue;
            _slots[i].Stack = Inventory.GetSlot(i);
            _slots[i].IsSelected = Inventory.IsHotbarIndex(i) && i == Inventory.SelectedIndex;
        }
    }
}
