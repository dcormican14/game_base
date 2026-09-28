using Godot;

namespace GameBase.Core;

/// <summary>
/// The interface theme, built from <see cref="Palette"/>: plum panels, cream
/// text, gold for whatever is hovered, pressed or focused. Merged into the engine's default theme at
/// start-up (see <see cref="UiStateService"/>), so every menu, button and bar in
/// the game is drawn in the palette without each scene styling itself.
/// </summary>
public static class PaletteTheme
{
    public static Theme Create()
    {
        var theme = new Theme();

        Color text = Palette.Cream;
        Color muted = Palette.Cream.WithAlpha(0.45f);

        foreach (string type in new[] { "Button", "OptionButton", "CheckBox", "CheckButton", "MenuButton" })
        {
            theme.SetStylebox("normal", type, Box(Palette.Wine, Palette.Mauve));
            theme.SetStylebox("hover", type, Box(Palette.Mauve, Palette.Gold));
            theme.SetStylebox("pressed", type, Box(Palette.Dusk, Palette.Gold));
            theme.SetStylebox("focus", type, Outline(Palette.Gold));
            theme.SetStylebox("disabled", type, Box(Palette.Plum, Palette.Wine));

            theme.SetColor("font_color", type, text);
            theme.SetColor("font_hover_color", type, text);
            theme.SetColor("font_pressed_color", type, text);
            theme.SetColor("font_focus_color", type, text);
            theme.SetColor("font_disabled_color", type, muted);
        }

        theme.SetColor("font_color", "Label", text);

        theme.SetStylebox("panel", "Panel", Box(Palette.Plum, Palette.Wine));
        theme.SetStylebox("panel", "PanelContainer", Box(Palette.Plum, Palette.Wine));
        theme.SetStylebox("panel", "PopupMenu", Box(Palette.Plum, Palette.Mauve));
        theme.SetColor("font_color", "PopupMenu", text);
        theme.SetColor("font_hover_color", "PopupMenu", text);
        theme.SetStylebox("hover", "PopupMenu", Box(Palette.Mauve, Palette.Mauve));

        theme.SetStylebox("background", "ProgressBar", Box(Palette.Plum, Palette.Wine));
        theme.SetStylebox("fill", "ProgressBar", Box(Palette.Gold, Palette.Gold));
        theme.SetColor("font_color", "ProgressBar", text);

        foreach (string type in new[] { "HSlider", "VSlider" })
        {
            theme.SetStylebox("slider", type, Box(Palette.Wine, Palette.Wine));
            theme.SetStylebox("grabber_area", type, Box(Palette.Gold, Palette.Gold));
            theme.SetStylebox("grabber_area_highlight", type, Box(Palette.Cream, Palette.Cream));
        }

        theme.SetStylebox("panel", "TabContainer", Box(Palette.Plum, Palette.Wine));
        theme.SetStylebox("tab_selected", "TabContainer", Box(Palette.Mauve, Palette.Gold));
        theme.SetStylebox("tab_unselected", "TabContainer", Box(Palette.Wine, Palette.Wine));
        theme.SetStylebox("tab_hovered", "TabContainer", Box(Palette.Dusk, Palette.Gold));
        theme.SetColor("font_selected_color", "TabContainer", text);
        theme.SetColor("font_unselected_color", "TabContainer", muted);
        theme.SetColor("font_hovered_color", "TabContainer", text);

        theme.SetStylebox("normal", "LineEdit", Box(Palette.Void, Palette.Mauve));
        theme.SetStylebox("focus", "LineEdit", Outline(Palette.Gold));
        theme.SetColor("font_color", "LineEdit", text);

        return theme;
    }

    private static StyleBoxFlat Box(Color fill, Color border)
    {
        var box = new StyleBoxFlat { BgColor = fill, BorderColor = border };
        box.SetBorderWidthAll(2);
        box.SetContentMarginAll(8);
        return box;
    }

    private static StyleBoxFlat Outline(Color border)
    {
        var box = new StyleBoxFlat { DrawCenter = false, BorderColor = border };
        box.SetBorderWidthAll(2);
        return box;
    }
}
