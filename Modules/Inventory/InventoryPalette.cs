using Godot;

namespace GameBase.Items;

/// <summary>
/// The colours the inventory is drawn in, in one place.
///
/// They are taken from what is already on screen rather than invented: the
/// panel plum is Skybox's <c>SpaceColor</c> (#130218 territory) lifted just
/// far enough off the background to read as a surface in front of it, and the
/// slot ink is Crosshair's <c>OutlineColor</c> — the warm off-white the player
/// is already looking at in the middle of the screen. Borrowing the crosshair's
/// exact value is what makes the panel look like part of the same HUD instead
/// of a window pasted over it.
///
/// Held as static readonly fields rather than exports because these are the
/// scheme, not per-instance decoration: a second inventory panel tinted
/// differently would be a bug, not a feature.
/// </summary>
public static class InventoryPalette
{
    /// <summary>
    /// Backing plum for the open inventory window, deliberately translucent so
    /// the world stays legible behind it. Alpha is high enough that item icons
    /// still read against a bright horizon.
    /// </summary>
    public static readonly Color Window = new(0.055f, 0.012f, 0.047f, 0.88f);

    /// <summary>
    /// The dim wash over the rest of the screen while the inventory is open.
    /// Near-black plum rather than neutral black, so the dimming reads as the
    /// scene falling into shadow rather than as a grey sheet.
    /// </summary>
    public static readonly Color Dim = new(0.035f, 0.006f, 0.030f, 0.55f);

    /// <summary>Empty slot interior: a shade deeper than the window behind it.</summary>
    public static readonly Color SlotFill = new(0.030f, 0.008f, 0.032f, 0.72f);

    /// <summary>Slot border at rest — off-white, well back so the grid reads as quiet.</summary>
    public static readonly Color SlotBorder = new(1f, 0.922f, 0.761f, 0.32f);

    /// <summary>Slot border under the cursor.</summary>
    public static readonly Color SlotBorderHover = new(1f, 0.922f, 0.761f, 0.70f);

    /// <summary>
    /// Border of the selected hotbar slot: the crosshair's off-white at nearly
    /// full strength, so the bar's selection and the crosshair are obviously
    /// the same UI speaking.
    /// </summary>
    public static readonly Color SlotBorderSelected = new(1f, 0.922f, 0.761f, 0.95f);

    /// <summary>Labels, counts and headings.</summary>
    public static readonly Color Text = new(1f, 0.922f, 0.761f, 0.92f);

    /// <summary>Slot numerals under the hotbar — present, but not competing with the count.</summary>
    public static readonly Color TextDim = new(1f, 0.922f, 0.761f, 0.45f);

    /// <summary>Drop shadow behind text, so counts stay readable over a bright icon.</summary>
    public static readonly Color TextShadow = new(0.02f, 0.004f, 0.018f, 0.85f);
}
