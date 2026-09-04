using Godot;
using System.Collections.Generic;
using GameBase.Core;

namespace GameBase.UI;

/// <summary>
/// Pixel-art crosshair. The player picks how many dashes to show (0-4) and
/// they are spread equidistant around the centre, 360/N apart, each set back
/// from the middle so the centre of the screen stays clear. Two dashes give
/// the classic <c>--  --</c>; four add a matching vertical pair. Even counts
/// always include a level horizontal pair, and odd counts point their unpaired
/// dash straight down.
///
/// Everything is drawn as discrete blocks on a virtual low-resolution grid so
/// it reads as part of the pixelated look rather than as a crisp vector
/// overlay, and each block fades from near-opaque at the inner end to nearly
/// transparent at the outer tip. A dash is authored once as a horizontal run
/// and then rotated block by block into each position, so every dash is the
/// same piece of pixel art whatever angle it sits at.
///
/// Instance Crosshair.tscn into any scene; it tracks viewport resizing and
/// needs no other wiring. When SettingsService is present the line count
/// follows the user's saved HUD setting; the export below is the fallback.
/// </summary>
public partial class Crosshair : Control
{
    /// <summary>Dashes to draw when SettingsService is absent. 0 hides the crosshair.</summary>
    [Export(PropertyHint.Range, "0,4,1")] public int Lines { get; set; } = 2;

    /// <summary>
    /// Warm gold, picked to sit in the same family as the sky's plum and the
    /// nebulae's warm accent rather than cutting across them.
    /// </summary>
    [Export] public Color BarColor { get; set; } = new(0.913f, 0.546f, 0.058f);
    /// <summary>Alpha of the block nearest the centre gap.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float InnerAlpha { get; set; } = 0.95f;
    /// <summary>Alpha of the outermost block.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float OuterAlpha { get; set; } = 0.15f;
    /// <summary>Blocks per dash, measured in virtual pixels.</summary>
    [Export(PropertyHint.Range, "1,32,1")] public int BarLength { get; set; } = 7;
    /// <summary>Dash thickness in virtual pixels.</summary>
    [Export(PropertyHint.Range, "1,8,1")] public int BarThickness { get; set; } = 1;
    /// <summary>Empty virtual pixels between the centre and each dash.</summary>
    [Export(PropertyHint.Range, "0,32,1")] public int Gap { get; set; } = 6;

    /// <summary>Blocks behind the dashes, so they stay visible against terrain.</summary>
    [Export] public bool Outline { get; set; } = true;

    /// <summary>
    /// Warm off-white, matching the gold rather than fighting it.
    /// </summary>
    [Export] public Color OutlineColor { get; set; } = new(1f, 0.922f, 0.761f, 0.85f);

    /// <summary>
    /// Height of the virtual low-res buffer the blocks are sized against —
    /// the same convention as StylizedFilter's <c>pixel_resolution</c>, so at
    /// the matching value a crosshair block is exactly one filter pixel.
    /// </summary>
    [Export(PropertyHint.Range, "60,1080,1")] public int PixelResolution { get; set; } = 320;

    private static SettingsService Svc => SettingsService.Instance;

    // Cell -> alpha, accumulated per redraw. Fill wins over outline, and each
    // cell is drawn exactly once, so overlapping dashes (and the outlines of
    // neighbouring dashes) can never stack alpha into a dark blotch.
    private readonly Dictionary<Vector2I, float> _fill = new();
    private readonly Dictionary<Vector2I, float> _ink = new();

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        GetViewport().SizeChanged += QueueRedraw;

        if (Svc != null)
            Svc.SettingsChanged += QueueRedraw;
    }

    public override void _ExitTree()
    {
        if (Svc != null)
            Svc.SettingsChanged -= QueueRedraw;
    }

    public override void _Draw()
    {
        int lines = Mathf.Clamp(Svc?.CrosshairLines ?? Lines, 0, 4);
        if (lines == 0)
            return;

        Vector2 viewport = GetViewportRect().Size;
        if (viewport.Y <= 0f)
            return;

        _fill.Clear();
        _ink.Clear();

        // N dashes spread over the full circle, 360/N apart. The start angle
        // depends on parity: an even count starts pointing right so it always
        // contains a level horizontal pair, while an odd count starts pointing
        // straight down so its unpaired dash sits below the centre. (+Y is
        // down in screen space.) That gives right/left at 2, down alone at 1,
        // and right/down/left/up at 4.
        float start = lines % 2 == 0 ? 0f : Mathf.Pi * 0.5f;
        float step = Mathf.Tau / lines;
        for (int line = 0; line < lines; line++)
        {
            BuildDash(start + line * step);
        }

        Paint(viewport);
    }

    /// <summary>
    /// Stamps one dash into the cell maps, rotated to point along
    /// <paramref name="angle"/>.
    ///
    /// The dash is authored once as a horizontal run of blocks and each block
    /// is then rotated into place — the artwork is moved, not redrawn. Because
    /// the crosshair is its own canvas layer rather than something the
    /// screen-space filter pixelates, a rotated block is still a full-size
    /// square on the grid, so a diagonal dash carries exactly as many pixels,
    /// at exactly the same size, as a horizontal one. Re-rasterizing the line
    /// at an angle instead (a Bresenham walk) thins it into a staircase, which
    /// is what made the 3-dash diagonals read as faint and narrow.
    /// </summary>
    private void BuildDash(float angle)
    {
        float cos = Mathf.Cos(angle);
        float sin = Mathf.Sin(angle);
        // The dash's own axes, snapped to whole cells, for the outline.
        var dirCell = new Vector2I(Mathf.RoundToInt(cos), Mathf.RoundToInt(sin));
        var perpCell = new Vector2I(Mathf.RoundToInt(-sin), Mathf.RoundToInt(cos));

        var cells = new List<(Vector2I Cell, float Alpha)>();
        bool hasPrevious = false;
        Vector2I previous = default;

        for (int i = 0; i < BarLength; i++)
        {
            // 0 at the inner end, 1 at the tip; a single block stays opaque.
            float t = BarLength > 1 ? (float)i / (BarLength - 1) : 0f;
            float alpha = Mathf.Lerp(InnerAlpha, OuterAlpha, t);

            for (int w = 0; w < BarThickness; w++)
            {
                // Local space: whole-cell offsets from the CENTRE CELL, which
                // the crosshair is built around so that it has a true middle
                // and opposing dashes mirror about it exactly. (Anchoring on a
                // cell corner instead leaves no middle cell, and a 1px dash
                // then picks a side by its rotation sign — which is what put
                // opposing dashes one cell out of line.) Offsets stay whole so
                // rounding lands every block on its own cell and the run
                // cannot break into dots.
                int localAlong = Gap / 2 + 1 + i;
                float localAcross = w - (BarThickness - 1) * 0.5f;

                var cell = new Vector2I(
                    Mathf.RoundToInt(localAlong * cos - localAcross * sin),
                    Mathf.RoundToInt(localAlong * sin + localAcross * cos));

                // At angles off the axes consecutive blocks can land as
                // diagonal neighbours, touching only at a corner and leaving
                // the dash visibly perforated. Fill the cell that shares an
                // edge with both so it reads as one solid run.
                if (BarThickness == 1 && hasPrevious && cell.X != previous.X && cell.Y != previous.Y)
                {
                    var bridge = new Vector2I(previous.X, cell.Y);
                    if (_fill.TryAdd(bridge, alpha))
                        cells.Add((bridge, alpha));
                }

                if (!_fill.ContainsKey(cell))
                    cells.Add((cell, alpha));
                _fill[cell] = alpha;

                if (BarThickness == 1)
                {
                    previous = cell;
                    hasPrevious = true;
                }
            }
        }

        if (!Outline)
            return;

        for (int i = 0; i < cells.Count; i++)
        {
            (Vector2I cell, float alpha) = cells[i];
            _ink.TryAdd(cell + perpCell, alpha);
            _ink.TryAdd(cell - perpCell, alpha);
            if (i == 0)
                _ink.TryAdd(cell - dirCell, alpha);
            if (i == cells.Count - 1)
                _ink.TryAdd(cell + dirCell, alpha);
        }

    }

    /// <summary>Draws the accumulated cells, outline first so fill sits on top.</summary>
    private void Paint(Vector2 viewport)
    {
        // One virtual pixel in screen pixels. Snapped to a whole number so
        // every block lands on the same grid and none of them straddle a
        // screen pixel (which would reintroduce soft edges).
        float scale = Mathf.Max(1f, Mathf.Floor(viewport.Y / PixelResolution));
        // Cell (0,0) is the centre cell, so its block straddles the middle of
        // the screen: back the origin off by half a block. Round AFTER that
        // shift — at an odd scale half a block is a fractional pixel, and
        // leaving it there would put every block on a half-pixel boundary and
        // soften the very edges the grid exists to keep hard.
        Vector2 origin = (viewport * 0.5f - Vector2.One * (scale * 0.5f)).Round();

        foreach (var pair in _ink)
        {
            if (_fill.ContainsKey(pair.Key))
                continue;
            Color ink = OutlineColor;
            // The outline fades with the block it belongs to, so the tips do
            // not end in a smudge after the gold has faded out.
            ink.A *= pair.Value;
            DrawRect(new Rect2(origin + (Vector2)pair.Key * scale, scale, scale), ink);
        }

        foreach (var pair in _fill)
        {
            Color fill = BarColor;
            fill.A *= pair.Value;
            DrawRect(new Rect2(origin + (Vector2)pair.Key * scale, scale, scale), fill);
        }
    }
}
