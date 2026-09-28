using Godot;

namespace GameBase.Core;

/// <summary>
/// Every colour the game draws with, in one place.
///
/// The scheme is taken from the space background (space_pixel_art.jpg): a dark
/// plum ground with stars ramping through dusty rose to a pink-white core. Each
/// entry below is either one of those measured colours or a step between two of
/// them, so anything drawn from this list sits inside the same palette as the
/// sky behind it.
///
/// Values are sRGB, as authored. Anything feeding them to a renderer that
/// expects linear colour (vertex colours, for instance) converts at the point
/// of use rather than here, so the list reads the same as the reference art.
/// </summary>
public static class Palette
{
    // ------------------------------------------------------------ the ramp

    /// <summary>Deepest plum: the reference background halved toward black.</summary>
    public static readonly Color Void = Color.FromHtml("#14030f");

    /// <summary>The reference background itself.</summary>
    public static readonly Color Plum = Color.FromHtml("#28061e");

    /// <summary>One step up from the background: panels and shadowed stone.</summary>
    public static readonly Color Wine = Color.FromHtml("#3d1030");

    /// <summary>Nebula body.</summary>
    public static readonly Color Mauve = Color.FromHtml("#5a2244");

    /// <summary>Faint stars and nebula edges.</summary>
    public static readonly Color Dusk = Color.FromHtml("#8c445c");

    /// <summary>Mid stars.</summary>
    public static readonly Color Rose = Color.FromHtml("#ba6976");

    /// <summary>Between rose and the star core: accents that must read on dark.</summary>
    public static readonly Color Blush = Color.FromHtml("#e3a6ae");

    /// <summary>Star cores: the palette's off-white, used for ink and outlines.</summary>
    public static readonly Color Bone = Color.FromHtml("#fee1ea");

    // ------------------------------------------------------------ materials

    /// <summary>Sand, lit side. A light off-white leaning toward the bone ink.</summary>
    public static readonly Color Sand = Color.FromHtml("#efe3df");

    /// <summary>Sand grain and ripple shade: the same sand, one step toward rose.</summary>
    public static readonly Color SandShade = Color.FromHtml("#d2bdbd");

    /// <summary>Stone, lighter of the two shades that alternate between nodes.</summary>
    public static readonly Color StoneLight = Color.FromHtml("#5b4454");

    /// <summary>Stone, darker shade.</summary>
    public static readonly Color StoneDark = Color.FromHtml("#45313f");

    // ------------------------------------------------------------ interface

    /// <summary>
    /// Warm gold: the crosshair's dashes and the interface's accent -- hover,
    /// focus, progress. The one saturated colour set against the plum, which
    /// is what lets it read at a glance.
    /// </summary>
    public static readonly Color Gold = new(0.913f, 0.546f, 0.058f);

    /// <summary>Warm off-white: interface text, the crosshair's ring, slot borders.</summary>
    public static readonly Color Cream = new(1f, 0.922f, 0.761f);

    /// <summary>Screen-space outlines drawn by the stylised filter.</summary>
    public static readonly Color Ink = Void;

    /// <summary>
    /// Tool highlights -- outline, gradient and particles alike -- for every
    /// tool on every material: the original warm off-white gold of the node
    /// outline, the same cream as the crosshair's ring.
    /// </summary>
    public static readonly Color Highlight = Cream;

    /// <summary>The interface's accent.</summary>
    public static readonly Color Accent = Gold;

    /// <summary>A colour with its alpha replaced.</summary>
    public static Color WithAlpha(this Color color, float alpha) =>
        new(color.R, color.G, color.B, alpha);
}
