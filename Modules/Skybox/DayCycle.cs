using System;
using Godot;
using GameBase.Core;
using GameBase.Planets;
using GameBase.Terrain;

namespace GameBase.World;

/// <summary>
/// Day and night on a turning planet. Drop it into a level beside a
/// <see cref="Skybox"/> and a <c>LightingRig</c>; it finds the planet itself.
///
/// THE PLANET TURNS. The sun circles the planet's axis once every
/// <see cref="DayLengthMinutes"/>, with the moon opposite it. The time of day is
/// local: where the camera stands, the sun's height over the local horizon gives
/// its elevation, and its side of the local meridian gives morning or
/// afternoon. One side of the planet is in daylight while the far side is at
/// night, and travelling far enough changes the time.
///
/// THE SKY FOLLOWS THE SPEC. The sun's elevation is mapped onto the
/// Day-Night spec's hour (<see cref="SkyPalette.HourFromElevation"/>), and its
/// keyframes and effects at that hour are handed to the sky shader, ready to
/// draw. The stars and nebulae turn with the planet.
///
/// The lights and the far terrain's haze follow <see cref="Updated"/>.
/// </summary>
public partial class DayCycle : Node
{
    /// <summary>Real minutes a full day takes.</summary>
    [Export(PropertyHint.Range, "0.5,240,0.5")] public float DayLengthMinutes { get; set; } = 20f;

    /// <summary>The local time where the camera first stands.</summary>
    [Export(PropertyHint.Range, "0,24,0.25")] public float StartHour { get; set; } = 9f;

    /// <summary>Whether the clock runs.</summary>
    [Export] public bool Running { get; set; } = true;

    /// <summary>Hours a press of the time keys moves the clock.</summary>
    [Export] public float ScrubHours { get; set; } = 1f;

    /// <summary>
    /// Where the time of day is measured from. Unset, it is the current
    /// camera, which is what the sky is drawn for.
    /// </summary>
    public Node3D Observer { get; set; }

    [ExportGroup("Glow")]
    /// <summary>The sun's forward-scattered glow: how strong, and how tightly it hugs the sun.</summary>
    [Export(PropertyHint.Range, "0,2,0.01")] public float SunGlow { get; set; } = 0.08f;
    [Export(PropertyHint.Range, "0,0.99,0.01")] public float SunScatter { get; set; } = 0.76f;

    /// <summary>The wide haze round the sun as it nears the horizon.</summary>
    [Export(PropertyHint.Range, "0,2,0.01")] public float SunHaze { get; set; } = 0.35f;

    /// <summary>How tightly the moon's glow hugs it.</summary>
    [Export(PropertyHint.Range, "0,0.99,0.01")] public float MoonScatter { get; set; } = 0.85f;

    [ExportGroup("")]
    [Export] public StringName ForwardAction { get; set; } = "time_forward";
    [Export] public StringName BackAction { get; set; } = "time_back";

    /// <summary>Raised once a frame, after the sky has been brought up to date.</summary>
    public event Action<DayCycle> Updated;

    // --------------------------------------------------------------- state

    /// <summary>Direction toward the sun, in world space.</summary>
    public Vector3 SunDirection { get; private set; } = Vector3.Up;

    /// <summary>Direction toward the moon: opposite the sun.</summary>
    public Vector3 MoonDirection => -SunDirection;

    /// <summary>Straight up where the camera is.</summary>
    public Vector3 LocalUp { get; private set; } = Vector3.Up;

    /// <summary>The local solar time, 0-24: noon when the sun crosses the local meridian.</summary>
    public float LocalHour { get; private set; }

    /// <summary>The sun's height over the local horizon, in degrees.</summary>
    public float SunElevation { get; private set; }

    /// <summary>The spec's hour the sky is drawn at (see <see cref="SkyPalette"/>); the nearer of the two near the poles (see <see cref="Rising"/>).</summary>
    public float SkyHour { get; private set; }

    /// <summary>
    /// How much the sky is drawn as morning rather than evening, 0..1: by how
    /// fast the sun is climbing here, so it changes smoothly everywhere. Near
    /// noon and midnight the two look the same anyway; at a pole, where the
    /// sun circles the horizon neither rising nor setting, it is an even
    /// half of each and holds still however the camera moves.
    /// </summary>
    public float Rising { get; private set; }

    public SkyPalette.Effects Effects { get; private set; }

    /// <summary>The sky's overhead and horizon colours now, as they should appear on screen.</summary>
    public Color Zenith { get; private set; }
    public Color Horizon { get; private set; }

    /// <summary>The sun's own colour now (display).</summary>
    public Color SunColor { get; private set; }

    /// <summary>The spec's name for the stage of the day.</summary>
    public string Phase => SkyPalette.Phase(SkyHour);

    /// <summary>How far the planet has turned, in radians.</summary>
    public float Turn { get; private set; }

    private Skybox _skybox;
    private Planet _planet;
    private bool _started;

    // -------------------------------------------------------------- frame

    public override void _Ready()
    {
        _skybox = NodeSearch.FindByType<Skybox>(GetTree().CurrentScene ?? GetParent());
        _planet = NodeSearch.FindByType<Planet>(GetTree().CurrentScene ?? GetParent());
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (InputMap.HasAction(ForwardAction) && @event.IsActionPressed(ForwardAction, true))
            SetLocalHour(LocalHour + ScrubHours);
        else if (InputMap.HasAction(BackAction) && @event.IsActionPressed(BackAction, true))
            SetLocalHour(LocalHour - ScrubHours);
    }

    public override void _Process(double delta)
    {
        if (!_started)
        {
            _started = true;
            SetLocalHour(StartHour);
        }
        else if (Running && DayLengthMinutes > 0)
        {
            Turn += (float)(Math.Tau * delta / (DayLengthMinutes * 60));
        }

        Refresh();
    }

    /// <summary>Turns the planet so that it is <paramref name="hour"/> o'clock where the camera stands.</summary>
    public void SetLocalHour(float hour)
    {
        Frame(out Vector3 axis, out Vector3 up, out Vector3 reference);

        // At that hour the sun lies this far east of the local meridian
        // (west after noon), square to the axis.
        Vector3 meridian = (up - axis * up.Dot(axis));
        meridian = meridian.LengthSquared() > 1e-8f ? meridian.Normalized() : reference;
        Vector3 east = axis.Cross(meridian).Normalized();
        float toward = (12f - SkyPalette.Wrap(hour)) / 12f * Mathf.Pi;
        Vector3 sun = meridian * Mathf.Cos(toward) + east * Mathf.Sin(toward);

        // The turn that brings the reference direction round to it.
        Turn = -Mathf.Atan2(axis.Dot(reference.Cross(sun)), reference.Dot(sun));
        Refresh();
    }

    /// <summary>Works out the sun, the local time and the sky, and hands them on.</summary>
    public void Refresh()
    {
        Frame(out Vector3 axis, out Vector3 up, out Vector3 reference);
        LocalUp = up;

        // The planet turns east, so the sun goes round the other way.
        SunDirection = reference.Rotated(axis, -Turn).Normalized();

        float sine = Math.Clamp(SunDirection.Dot(up), -1f, 1f);
        SunElevation = Mathf.RadToDeg(Mathf.Asin(sine));

        // The local time, for reading out: by the sun's side of the meridian.
        // It is undefined at a pole, and swings wildly near one -- so the sky
        // is not drawn from it.
        Vector3 meridian = up - axis * up.Dot(axis);
        if (meridian.LengthSquared() > 1e-8f)
        {
            meridian = meridian.Normalized();
            Vector3 east = axis.Cross(meridian).Normalized();
            float toward = Mathf.Atan2(SunDirection.Dot(east), SunDirection.Dot(meridian));
            LocalHour = SkyPalette.Wrap(12f - toward / Mathf.Pi * 12f);
        }
        else
        {
            LocalHour = 12f;
        }

        // Morning or evening by how fast the sun climbs here: the planet
        // turns the sun along -(axis x sun), so its height changes at
        // -(axis x sun) . up. That is smooth everywhere, unlike which side of
        // a meridian the sun is on; it is zero at noon and midnight, where
        // morning and evening look alike, and at the poles.
        float climb = -axis.Cross(SunDirection).Dot(up);
        Rising = SkyPalette.Smooth(-0.05f, 0.05f, climb);

        // How high and low the sun goes here: 90 degrees less the latitude.
        float latitude = Mathf.RadToDeg(Mathf.Asin(Math.Clamp(Math.Abs(up.Dot(axis)), 0f, 1f)));
        float morningHour = SkyPalette.HourFromElevation(SunElevation, true, 90f - latitude, latitude - 90f);
        float eveningHour = SkyPalette.HourFromElevation(SunElevation, false, 90f - latitude, latitude - 90f);
        SkyHour = Rising >= 0.5f ? morningHour : eveningHour;

        Color backdrop = Backdrop();
        (Color morningZenith, Color morningHorizon) = SkyPalette.SkyAt(morningHour, backdrop);
        (Color eveningZenith, Color eveningHorizon) = SkyPalette.SkyAt(eveningHour, backdrop);
        Zenith = eveningZenith.Lerp(morningZenith, Rising);
        Horizon = eveningHorizon.Lerp(morningHorizon, Rising);
        Effects = SkyPalette.Effects.Lerp(SkyPalette.EffectsAt(eveningHour), SkyPalette.EffectsAt(morningHour), Rising);
        SunColor = SkyPalette.SunColor(SunElevation);

        PushSky(axis);
        PushHaze();
        Updated?.Invoke(this);
    }

    /// <summary>
    /// The sine of the planet's horizon below the level, from the camera: the
    /// higher the camera, the further it dips. The base sphere's, so the
    /// planet hides the sun no later than its real ground does.
    /// </summary>
    private float HorizonSine()
    {
        if (_planet == null || !IsInstanceValid(_planet))
            return 0f;

        Vector3? eye = GetViewport()?.GetCamera3D()?.GlobalPosition;
        if (eye == null)
            return 0f;

        float height = (eye.Value - _planet.GlobalPosition).Length();
        float radius = _planet.Radius;
        if (height <= radius)
            return 0f;

        float ratio = radius / height;
        return -Mathf.Sqrt(1f - ratio * ratio);
    }

    /// <summary>
    /// The night sky's space backdrop as it appears on screen: the skybox's
    /// space colour at its brightness and the sky's energy, through the
    /// tonemapper. The twilight ends on exactly this.
    /// </summary>
    private Color Backdrop()
    {
        if (_skybox == null || !IsInstanceValid(_skybox))
            return SkyPalette.Backdrop;

        Color space = _skybox.SpaceColor.SrgbToLinear();
        float scale = _skybox.SpaceBrightness * _skybox.SkyEnergy;
        return SkyPalette.AfterTonemap(new Vector3(space.R, space.G, space.B) * scale);
    }

    /// <summary>The planet's axis, straight up at the camera, and the direction the sun starts from.</summary>
    private void Frame(out Vector3 axis, out Vector3 up, out Vector3 reference)
    {
        Vector3 centre = _planet != null && IsInstanceValid(_planet) ? _planet.GlobalPosition : Vector3.Zero;
        Basis basis = _planet != null && IsInstanceValid(_planet) ? _planet.GlobalBasis.Orthonormalized() : Basis.Identity;
        axis = basis.Y;
        reference = basis.X;

        Vector3 eye = Observer != null && IsInstanceValid(Observer) ? Observer.GlobalPosition
            : GetViewport()?.GetCamera3D()?.GlobalPosition ?? centre + axis;
        Vector3 offset = eye - centre;
        up = offset.LengthSquared() > 1e-6f ? offset.Normalized() : axis;
    }

    // --------------------------------------------------------------- haze

    /// <summary>
    /// The far terrain's haze takes the sky's colour: the night's own wine,
    /// moving to the day's horizon as the light comes (aerial perspective:
    /// distance reads as the sky's colour). The sun and the moon light it
    /// with exactly the glow they give the sky.
    /// </summary>
    private void PushHaze()
    {
        FarTerrain far = _planet != null && IsInstanceValid(_planet) ? _planet.Far : null;
        if (far == null)
            return;

        Color wine = Palette.Wine.SrgbToLinear();
        Vector3 night = new(wine.R, wine.G, wine.B);
        Vector3 haze = night.Lerp(SkyPalette.BeforeTonemap(Horizon), Effects.Daylight);

        // The sky's sun glow (SpaceSky.gdshader, sun()): its colour, faded
        // as it sets, the haze part growing as it gets low.
        float sine = Mathf.Sin(Mathf.DegToRad(SunElevation));
        float fade = SkyPalette.Smooth(-0.1f, 0.02f, sine);
        float low = 1f - SkyPalette.Smooth(0f, 0.35f, sine);
        Vector3 sun = SkyPalette.BeforeTonemap(SunColor);

        // The sky's moon glow: its colours are given as display colours and
        // multiplied by the sky's energy on the way.
        Vector3 moon = Vector3.Zero;
        if (_skybox != null && IsInstanceValid(_skybox) && _skybox.MoonEnabled)
        {
            Color light = _skybox.MoonLight.SrgbToLinear(), halo = _skybox.MoonHaloColor.SrgbToLinear();
            Color tint = (light * 0.6f + halo) * (_skybox.MoonBrightness * _skybox.MoonHalo * 0.25f * _skybox.SkyEnergy);
            moon = new Vector3(tint.R, tint.G, tint.B);
        }

        far.SetSky(new FarTerrain.SkyLight(
            haze, SunDirection, sun * SunGlow * fade, sun * SunHaze * low * fade, SunScatter,
            MoonDirection, moon, MoonScatter));
    }

    // ---------------------------------------------------------------- sky

    private void PushSky(Vector3 axis)
    {
        ShaderMaterial sky = _skybox != null && IsInstanceValid(_skybox) ? _skybox.ProceduralMaterial : null;
        if (sky == null)
            return;

        float energy = _skybox.SkyEnergy;
        SkyPalette.Effects e = Effects;

        sky.SetShaderParameter("local_up", LocalUp);
        sky.SetShaderParameter("horizon_sine", HorizonSine());
        sky.SetShaderParameter("sun_direction", SunDirection);
        sky.SetShaderParameter("moon_direction", MoonDirection);
        sky.SetShaderParameter("sky_axis", axis);
        sky.SetShaderParameter("sky_turn", Turn);

        // The day's gradient covers the night's layers through twilight: the
        // nebulae come through as the sun sinks from 6 to 18 degrees down,
        // while the gradient itself darkens onto the backdrop behind them.
        float day = SkyPalette.Smooth(-18f, -6f, SunElevation);
        sky.SetShaderParameter("day_amount", day);
        sky.SetShaderParameter("day_zenith", SkyPalette.BeforeTonemap(Zenith, energy));
        sky.SetShaderParameter("day_horizon", SkyPalette.BeforeTonemap(Horizon, energy));
        sky.SetShaderParameter("star_visibility", e.Stars);

        // Dawn's glow is apricot, dusk's coral; where both show (near a pole)
        // their colours mix by strength.
        float glow = e.GlowDawn + e.GlowDusk;
        Color warm = SkyPalette.SunriseApricot.Lerp(SkyPalette.SunsetCoral, glow > 1e-5f ? e.GlowDusk / glow : 1f);
        sky.SetShaderParameter("glow_color", SkyPalette.BeforeTonemap(warm, energy));
        sky.SetShaderParameter("glow_strength", Math.Min(glow, 1f));
        sky.SetShaderParameter("purple_color", SkyPalette.BeforeTonemap(SkyPalette.PurpleGlow, energy));
        sky.SetShaderParameter("purple_strength", Math.Max(e.PurpleDawn, e.PurpleDusk));
        sky.SetShaderParameter("belt_color", SkyPalette.BeforeTonemap(SkyPalette.BeltPink, energy));
        sky.SetShaderParameter("shadow_color", SkyPalette.BeforeTonemap(SkyPalette.EarthShadow, energy));
        sky.SetShaderParameter("belt_strength", Math.Max(e.BeltDawn, e.BeltDusk));

        // The sun: its colour by elevation, and squashed as it nears the
        // horizon, as refraction does (about 5/6 tall as wide when setting).
        sky.SetShaderParameter("sun_color", SkyPalette.BeforeTonemap(SunColor, energy));
        sky.SetShaderParameter("sun_flatten", Mathf.Lerp(0.83f, 1f, SkyPalette.Smooth(0f, 8f, SunElevation)));
        sky.SetShaderParameter("sun_glow", SunGlow);
        sky.SetShaderParameter("sun_haze", SunHaze);
        sky.SetShaderParameter("sun_scatter", SunScatter);
        sky.SetShaderParameter("moon_scatter", MoonScatter);
    }
}
