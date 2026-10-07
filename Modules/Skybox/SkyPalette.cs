using System;
using Godot;

namespace GameBase.World;

/// <summary>
/// The day-night sky's colours and timing, from the Day-Night Sky Cycle spec,
/// with its colours adapted to the game's plum (see SKY_TODO.md, "Palette").
/// Pure: every value is a function of the spec's hour, 0-24.
///
/// THE SPEC'S HOUR. The spec times everything by a clock in which the sun rises
/// at 6:45 and sets at 18:15. On a turning planet the time of day depends on
/// where you stand, and near the poles the sun never climbs far; so the sky is
/// driven by the sun's ELEVATION over the local horizon, mapped back onto the
/// spec's hour through the elevations its keyframe table gives
/// (<see cref="HourFromElevation"/>). Every keyframe and curve then runs
/// exactly as the spec writes it.
///
/// DISPLAY COLOURS. Every colour here is what should appear on screen. The
/// renderer works before the Filmic tonemapper, so values handed to shaders
/// go through <see cref="BeforeTonemap"/> first.
///
/// NIGHT IS THE BACKDROP. The twilight does not end on a plum of its own: it
/// ends on the night sky's own space backdrop, as it appears on screen
/// (<see cref="Backdrop"/>), so the day's gradient lands exactly on what lies
/// behind it. The last twilight stop is blue hour carried most of the way
/// there in OKLab, where equal steps look equal, so the light falls evenly
/// from blue hour to night.
/// </summary>
public static class SkyPalette
{
    /// <summary>
    /// One keyframe: the spec's hour, the overhead and horizon colours then,
    /// and how far they are carried toward the night backdrop (1: they are the
    /// backdrop).
    /// </summary>
    public readonly record struct Keyframe(float Hour, Color Zenith, Color Horizon, float Night = 0)
    {
        public Keyframe(float hour, string zenith, string horizon, float night = 0)
            : this(hour, Color.FromHtml(zenith), Color.FromHtml(horizon), night) { }

        /// <summary>The colours with the night backdrop worked in.</summary>
        public (Color Zenith, Color Horizon) Against(Color backdrop) => Night <= 0
            ? (Zenith, Horizon)
            : (Oklab(Zenith, backdrop, Night), Oklab(Horizon, backdrop, Night));
    }

    /// <summary>
    /// The night sky's space backdrop as it appears on screen with the
    /// skybox's default settings: #28061e at a quarter, through the Filmic
    /// curve. <see cref="DayCycle"/> works out the live one.
    /// </summary>
    public static readonly Color Backdrop = Color.FromHtml("#060104");

    /// <summary>
    /// The spec's stops, adapted. Night (midnight, the start of dawn's
    /// twilight and the end of dusk's, the sun 18 degrees down) is the
    /// backdrop; the spec's -12 degree stop is blue hour half way to it
    /// (blue hour is at -6, night at -18). The first and last are the same, so
    /// the loop is seamless.
    /// </summary>
    public static readonly Keyframe[] Keyframes =
    {
        new(0.0f, "#060104", "#060104", 1),
        new(4.5f, "#060104", "#060104", 1),
        new(5.3f, "#361C50", "#583678"),
        new(5.8f, "#472869", "#986A88"),
        new(6.25f, "#68459A", "#E3B19E"),
        new(6.75f, "#8A6AC4", "#F6C9A8"),
        new(7.75f, "#9678D6", "#E6D4E5"),
        new(12.5f, "#9C7CE0", "#E1D4F4"),
        new(16.75f, "#9A78D8", "#E9D6EC"),
        new(17.6f, "#8E6ECC", "#F2CFA0"),
        new(18.25f, "#7D57AA", "#F0936E"),
        new(18.75f, "#583579", "#A96174"),
        new(19.25f, "#381D52", "#5B3A7A"),
        new(20.2f, "#381D52", "#5B3A7A", 0.5f),
        new(21.0f, "#060104", "#060104", 1),
        new(24.0f, "#060104", "#060104", 1),
    };

    // The spec's working colours, adapted.
    public static readonly Color SunriseApricot = Color.FromHtml("#F6C9A8");
    public static readonly Color SunsetCoral = Color.FromHtml("#F0936E");
    public static readonly Color PurpleGlow = Color.FromHtml("#C4889D");
    public static readonly Color BeltPink = Color.FromHtml("#E3A9AF");
    public static readonly Color EarthShadow = Color.FromHtml("#684A85");
    public static readonly Color SunLow = Color.FromHtml("#FFB98A");
    public static readonly Color SunHigh = Color.FromHtml("#FFF4DC");

    public const float Sunrise = 6.75f, Sunset = 18.25f;

    /// <summary>
    /// The overhead and horizon colours at an hour, between the stops either
    /// side, with night as <paramref name="backdrop"/> (by default the
    /// skybox's default backdrop).
    /// </summary>
    public static (Color Zenith, Color Horizon) SkyAt(float hour, Color? backdrop = null)
    {
        hour = Wrap(hour);
        Color night = backdrop ?? Backdrop;

        for (int i = 0; i < Keyframes.Length - 1; i++)
        {
            Keyframe a = Keyframes[i], b = Keyframes[i + 1];
            if (hour <= b.Hour)
            {
                float t = (hour - a.Hour) / (b.Hour - a.Hour);
                (Color za, Color ha) = a.Against(night);
                (Color zb, Color hb) = b.Against(night);
                return (za.Lerp(zb, t), ha.Lerp(hb, t));
            }
        }

        return Keyframes[^1].Against(night);
    }

    /// <summary>The strengths of the sky's effects at an hour (spec, "Calculations and formulas").</summary>
    public readonly record struct Effects(
        float GlowDawn, float GlowDusk, float PurpleDawn, float PurpleDusk, float BeltDawn, float BeltDusk,
        float Stars, float Daylight)
    {
        /// <summary>How warm the light is: the stronger of the two glows, dawn's at 80%.</summary>
        public float Warm => Math.Max(GlowDawn * 0.8f, GlowDusk);

        /// <summary>Between two sets of effects.</summary>
        public static Effects Lerp(Effects a, Effects b, float t) => new(
            Mathf.Lerp(a.GlowDawn, b.GlowDawn, t), Mathf.Lerp(a.GlowDusk, b.GlowDusk, t),
            Mathf.Lerp(a.PurpleDawn, b.PurpleDawn, t), Mathf.Lerp(a.PurpleDusk, b.PurpleDusk, t),
            Mathf.Lerp(a.BeltDawn, b.BeltDawn, t), Mathf.Lerp(a.BeltDusk, b.BeltDusk, t),
            Mathf.Lerp(a.Stars, b.Stars, t), Mathf.Lerp(a.Daylight, b.Daylight, t));
    }

    public static Effects EffectsAt(float hour)
    {
        hour = Wrap(hour);

        return new Effects(
            GlowDawn: Bump(hour, 6.5f, 0.6f) * 0.7f,
            GlowDusk: Bump(hour, 18.45f, 0.6f),
            PurpleDawn: Bump(hour, 5.95f, 0.28f) * 0.8f,
            PurpleDusk: Bump(hour, 18.9f, 0.3f),
            BeltDawn: Bump(hour, 6.4f, 0.45f) * 0.8f,
            BeltDusk: Bump(hour, 18.6f, 0.45f),
            Stars: Math.Clamp(1 - Smooth(4.6f, 5.5f, hour) + Smooth(19.5f, 20.4f, hour), 0, 1),
            Daylight: Smooth(5.4f, 7.6f, hour) * (1 - Smooth(17.4f, 19.6f, hour)));
    }

    /// <summary>The sun's own colour by its elevation: peach low, cream high (spec 7).</summary>
    public static Color SunColor(float elevationDegrees)
    {
        float altitude = Mathf.Sin(Mathf.DegToRad(Math.Max(elevationDegrees, 0)));
        return SunLow.Lerp(SunHigh, Smooth(0, 0.5f, altitude));
    }

    // The spec's keyframe table, as sun elevation (degrees) against hour: each
    // stop where its stage begins. The midday and midnight ends are filled in
    // with the day's own highest and lowest elevation.
    //
    // Through twilight the stops are spread as real twilight is, not at the
    // table's own labels: those put purple light at -3 and blue hour at -4, so
    // half an hour of the spec's clock passed in one degree of the sun, a drop
    // the eye catches. Its clock says the sun sinks about 7 degrees in that
    // half hour (15 an hour, at the equator), and real purple light runs to
    // about -6 and blue hour centres there.
    private static readonly (float Elevation, float Hour)[] Morning =
    {
        (-18, 4.5f), (-9, 5.3f), (-6, 5.8f), (-2.5f, 6.25f), (0, 6.75f), (6, 7.75f),
    };

    private static readonly (float Elevation, float Hour)[] Evening =
    {
        (6, 16.75f), (3, 17.6f), (0, 18.25f), (-2.5f, 18.75f), (-6, 19.25f), (-12, 20.2f), (-18, 21f),
    };

    /// <summary>
    /// The spec's hour for a sun at an elevation, rising (morning) or setting.
    /// <paramref name="highest"/> and <paramref name="lowest"/> are the most
    /// the sun climbs and sinks at this place: they mark midday (12.5) and
    /// midnight (0 or 24).
    /// </summary>
    public static float HourFromElevation(float elevation, bool morning, float highest = 90, float lowest = -90)
    {
        if (morning)
        {
            // Midnight up to the first stop, then stop to stop, then on to midday.
            if (elevation <= Morning[0].Elevation)
                return Lerp(lowest, Morning[0].Elevation, 0f, Morning[0].Hour, elevation);

            for (int i = 0; i < Morning.Length - 1; i++)
            {
                if (elevation <= Morning[i + 1].Elevation)
                    return Lerp(Morning[i].Elevation, Morning[i + 1].Elevation, Morning[i].Hour, Morning[i + 1].Hour, elevation);
            }

            return Lerp(Morning[^1].Elevation, Math.Max(highest, Morning[^1].Elevation + 1), Morning[^1].Hour, 12.5f, elevation);
        }

        if (elevation >= Evening[0].Elevation)
            return Lerp(Math.Max(highest, Evening[0].Elevation + 1), Evening[0].Elevation, 12.5f, Evening[0].Hour, elevation);

        for (int i = 0; i < Evening.Length - 1; i++)
        {
            if (elevation >= Evening[i + 1].Elevation)
                return Lerp(Evening[i].Elevation, Evening[i + 1].Elevation, Evening[i].Hour, Evening[i + 1].Hour, elevation);
        }

        return Lerp(Evening[^1].Elevation, Math.Min(lowest, Evening[^1].Elevation - 1), Evening[^1].Hour, 24f, elevation);
    }

    /// <summary>The spec's name for the stage of the day an hour falls in.</summary>
    public static string Phase(float hour)
    {
        hour = Wrap(hour);
        return hour switch
        {
            < 4.5f => "Night",
            < 5.3f => "Early twilight",
            < 5.8f => "Blue hour",
            < 6.25f => "Purple light",
            < 6.75f => "Dawn glow",
            < 7.75f => "Golden hour",
            < 16.75f => "Day",
            < 18.25f => "Golden hour",
            < 18.75f => "Sunset glow",
            < 19.25f => "Purple light",
            < 20.2f => "Blue hour",
            _ => "Night",
        };
    }

    // ------------------------------------------------------------ OKLab

    /// <summary>Mixes two display colours in OKLab, where equal steps look equal.</summary>
    public static Color Oklab(Color from, Color to, float t)
    {
        Vector3 a = ToOklab(from), b = ToOklab(to);
        return FromOklab(a.Lerp(b, t));
    }

    /// <summary>A display colour's OKLab lightness, a and b (Ottosson).</summary>
    public static Vector3 ToOklab(Color display)
    {
        Color c = display.SrgbToLinear();
        float l = 0.4122214708f * c.R + 0.5363325363f * c.G + 0.0514459929f * c.B;
        float m = 0.2119034982f * c.R + 0.6806995451f * c.G + 0.1073969566f * c.B;
        float s = 0.0883024619f * c.R + 0.2817188376f * c.G + 0.6299787005f * c.B;
        l = Mathf.Pow(Math.Max(l, 0), 1f / 3f);
        m = Mathf.Pow(Math.Max(m, 0), 1f / 3f);
        s = Mathf.Pow(Math.Max(s, 0), 1f / 3f);
        return new Vector3(
            0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
            1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
            0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    public static Color FromOklab(Vector3 lab)
    {
        float l = lab.X + 0.3963377774f * lab.Y + 0.2158037573f * lab.Z;
        float m = lab.X - 0.1055613458f * lab.Y - 0.0638541728f * lab.Z;
        float s = lab.X - 0.0894841775f * lab.Y - 1.2914855480f * lab.Z;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;
        var linear = new Color(
            Math.Clamp(4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s, 0, 1),
            Math.Clamp(-1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s, 0, 1),
            Math.Clamp(-0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s, 0, 1));
        return linear.LinearToSrgb();
    }

    // ------------------------------------------------------------ tonemap

    /// <summary>
    /// How a value from before the Filmic tonemapper appears on screen: the
    /// forward of <see cref="BeforeTonemap"/>.
    /// </summary>
    public static Color AfterTonemap(Vector3 linear, float white = 1)
    {
        var tonemapped = new Color(
            Math.Clamp(Filmic(linear.X, white), 0, 1),
            Math.Clamp(Filmic(linear.Y, white), 0, 1),
            Math.Clamp(Filmic(linear.Z, white), 0, 1));
        return tonemapped.LinearToSrgb();
    }

    /// <summary>
    /// The value, before the Filmic tonemapper, that lands on screen as a
    /// display colour: the inverse of Godot's Filmic curve, channel by channel,
    /// divided by whatever multiplies it on the way (a sky's energy, the
    /// exposure). Colours past the curve's reach come back at its top.
    /// </summary>
    public static Vector3 BeforeTonemap(Color display, float energy = 1, float white = 1, float exposure = 1)
    {
        Color linear = display.SrgbToLinear();
        float scale = 1f / Math.Max(energy * exposure, 1e-4f);
        return new Vector3(
            InverseFilmic(linear.R, white) * scale,
            InverseFilmic(linear.G, white) * scale,
            InverseFilmic(linear.B, white) * scale);
    }

    // Godot's Filmic curve (tonemap.glsl): Hable's, with an exposure bias of 2.
    private const float Bias = 2f;
    private const float A = 0.22f * Bias * Bias, B = 0.30f * Bias, C = 0.10f, D = 0.20f, E = 0.01f, F = 0.30f;

    private static float Curve(float x) => (x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F) - E / F;

    /// <summary>Godot's Filmic tonemapper on one linear channel.</summary>
    public static float Filmic(float x, float white = 1) => Curve(x) / Curve(white);

    /// <summary>The linear input the Filmic curve maps to <paramref name="y"/>.</summary>
    public static float InverseFilmic(float y, float white = 1)
    {
        // Solve Curve(x) = y * Curve(white) for x >= 0: a quadratic in x.
        float k = Math.Clamp(y, 0, 0.999f) * Curve(white) + E / F;
        float qa = A * (1 - k), qb = B * (C - k), qc = D * (E - k * F);

        if (Math.Abs(qa) < 1e-7f)
            return Math.Max(0, -qc / qb);

        float root = qb * qb - 4 * qa * qc;
        if (root < 0)
            return 0;

        float x = (-qb + Mathf.Sqrt(root)) / (2 * qa);
        return Math.Max(0, x);
    }

    // ------------------------------------------------------------ helpers

    public static float Wrap(float hour) => ((hour % 24f) + 24f) % 24f;

    public static float Bump(float x, float centre, float width)
    {
        float d = (x - centre) / width;
        return Mathf.Exp(-d * d);
    }

    public static float Smooth(float from, float to, float x)
    {
        float t = Math.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static float Lerp(float x0, float x1, float y0, float y1, float x) =>
        Math.Abs(x1 - x0) < 1e-6f ? y1 : y0 + (y1 - y0) * Math.Clamp((x - x0) / (x1 - x0), 0, 1);
}
