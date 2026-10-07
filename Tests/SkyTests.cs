using Godot;
using System;
using GameBase.World;

namespace GameBase.Tests;

/// <summary>The day-night sky: the spec's keyframes and curves, the elevation-to-hour mapping, and the turning planet.</summary>
public sealed class SkyTests : TestSuite
{
    private static float Apart(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    /// <summary>At each stop the sky is exactly that stop's colours (night worked in).</summary>
    [Test]
    public void KeyframesHitTheirStops()
    {
        foreach (SkyPalette.Keyframe stop in SkyPalette.Keyframes)
        {
            (Color z, Color h) = stop.Against(SkyPalette.Backdrop);
            (Color zenith, Color horizon) = SkyPalette.SkyAt(stop.Hour == 24f ? 23.9999f : stop.Hour);
            Check(Apart(zenith, z) < 0.002f, $"the zenith at {stop.Hour} is {zenith.ToHtml(false)}, not {z.ToHtml(false)}");
            Check(Apart(horizon, h) < 0.002f, $"the horizon at {stop.Hour} is {horizon.ToHtml(false)}, not {h.ToHtml(false)}");
        }
    }

    /// <summary>
    /// Night is the space backdrop itself, whatever it is set to; and from
    /// sunset into night, and from night to sunrise, the sky's lightness only
    /// ever moves one way, in small steps -- no sudden drop at the end of
    /// twilight.
    /// </summary>
    [Test]
    public void TwilightFallsEvenlyIntoTheBackdrop()
    {
        Color backdrop = Color.FromHtml("#0a0310");
        (Color z, Color h) = SkyPalette.SkyAt(22f, backdrop);
        Check(Apart(z, backdrop) < 0.004f && Apart(h, backdrop) < 0.004f, "night is not the backdrop it was given");

        // Evening, by the sun's elevation from 0 to 18 degrees down; then
        // morning back up.
        foreach (bool morning in new[] { false, true })
        {
            float last = morning ? -1f : 2f, biggest = 0;
            for (float e = morning ? -18f : 0f; morning ? e <= 0f : e >= -18f; e += morning ? 0.1f : -0.1f)
            {
                (Color zenith, _) = SkyPalette.SkyAt(SkyPalette.HourFromElevation(e, morning));
                float lightness = SkyPalette.ToOklab(zenith).X;
                if (last >= 0 && last <= 1)
                {
                    Check(morning ? lightness >= last - 1e-4f : lightness <= last + 1e-4f,
                        $"the overhead sky turns back at {e:0.0} degrees ({(morning ? "morning" : "evening")})");
                    biggest = Math.Max(biggest, Math.Abs(lightness - last));
                }

                last = lightness;
            }

            Check(biggest < 0.02f, $"the {(morning ? "dawn" : "dusk")} sky's lightness jumps {biggest:0.000} in a tenth of a degree");
        }
    }

    /// <summary>Across 23:59 to 0:00 nothing jumps: colours, glows, stars, daylight.</summary>
    [Test]
    public void TheLoopHasNoJump()
    {
        (Color z1, Color h1) = SkyPalette.SkyAt(23.999f);
        (Color z2, Color h2) = SkyPalette.SkyAt(0.001f);
        Check(Apart(z1, z2) < 0.002f && Apart(h1, h2) < 0.002f, "the sky's colours jump at midnight");

        SkyPalette.Effects a = SkyPalette.EffectsAt(23.999f), b = SkyPalette.EffectsAt(0.001f);
        Near(a.Stars, b.Stars, 0.001f, "stars across midnight");
        Near(a.Daylight, b.Daylight, 0.001f, "daylight across midnight");
        Near(a.GlowDusk + a.GlowDawn, b.GlowDusk + b.GlowDawn, 0.001f, "glow across midnight");
    }

    /// <summary>
    /// The spec's stages land where its table puts them: sunrise at 6:45 and
    /// sunset at 18:15 with the sun on the horizon, midday at the sun's
    /// highest, midnight at its lowest -- and the hour only ever moves one
    /// way as the sun climbs or sinks.
    /// </summary>
    [Test]
    public void ElevationMapsOntoTheSpecsHours()
    {
        Near(SkyPalette.HourFromElevation(0, true), SkyPalette.Sunrise, 0.001f, "the hour at sunrise");
        Near(SkyPalette.HourFromElevation(0, false), SkyPalette.Sunset, 0.001f, "the hour at sunset");
        Near(SkyPalette.HourFromElevation(40, true, 40, -40), 12.5f, 0.001f, "the hour at the sun's highest");
        Near(SkyPalette.HourFromElevation(-40, true, 40, -40), 0f, 0.001f, "the hour at the sun's lowest, rising");
        Near(SkyPalette.HourFromElevation(-40, false, 40, -40), 24f, 0.001f, "the hour at the sun's lowest, setting");
        Equal(SkyPalette.Phase(SkyPalette.HourFromElevation(-7.5f, true)), "Blue hour", "the stage with the sun 7.5 degrees down at dawn");
        Equal(SkyPalette.Phase(SkyPalette.HourFromElevation(-3.5f, false)), "Purple light", "the stage 3.5 degrees down at dusk");

        float last = -1;
        for (float e = -90; e <= 90; e += 0.5f)
        {
            float hour = SkyPalette.HourFromElevation(e, true);
            Check(hour >= last, $"the morning hour goes back at {e} degrees");
            last = hour;
        }

        last = 25;
        for (float e = -90; e <= 90; e += 0.5f)
        {
            float hour = SkyPalette.HourFromElevation(e, false);
            Check(hour <= last, $"the evening hour goes back at {e} degrees");
            last = hour;
        }
    }

    /// <summary>A colour handed to the sky lands on screen as itself, through the tonemapper.</summary>
    [Test]
    public void TheTonemapInverseRoundTrips()
    {
        for (float y = 0.01f; y < 0.96f; y += 0.01f)
        {
            float x = SkyPalette.InverseFilmic(y);
            Near(SkyPalette.Filmic(x), y, 0.001f, $"the Filmic curve after its inverse, at {y:0.00}");
        }
    }

    /// <summary>The sun's colour runs from peach on the horizon to cream overhead.</summary>
    [Test]
    public void TheSunWarmsAsItSinks()
    {
        Check(Apart(SkyPalette.SunColor(0), SkyPalette.SunLow) < 0.002f, "the sun on the horizon is not the low colour");
        Check(Apart(SkyPalette.SunColor(60), SkyPalette.SunHigh) < 0.002f, "the sun overhead is not the high colour");
    }

    private DayCycle Cycle(Vector3 standing, out Node3D observer)
    {
        observer = Add(new Node3D());
        observer.GlobalPosition = standing;
        DayCycle cycle = Add(new DayCycle { Observer = observer, Running = false });
        return cycle;
    }

    /// <summary>Setting the local time and reading it back agree, all round the clock.</summary>
    [Test]
    public void TheLocalHourRoundTrips()
    {
        DayCycle cycle = Cycle(new Vector3(6000, 0, 0), out _);

        for (float hour = 0.25f; hour < 24; hour += 1.5f)
        {
            cycle.SetLocalHour(hour);
            float apart = Math.Abs(cycle.LocalHour - hour);
            Check(Math.Min(apart, 24 - apart) < 0.01f, $"set to {hour:0.00}, the local hour reads {cycle.LocalHour:0.00}");
        }
    }

    /// <summary>
    /// On the equator the sun is overhead at noon, under the feet at midnight,
    /// on the horizon at six -- in the east in the morning -- and the moon is
    /// always opposite it.
    /// </summary>
    [Test]
    public void TheSunCrossesTheSky()
    {
        DayCycle cycle = Cycle(new Vector3(6000, 0, 0), out _);

        cycle.SetLocalHour(12);
        Check(cycle.SunElevation > 85, $"the sun is only {cycle.SunElevation:0} degrees up at noon");
        cycle.SetLocalHour(0);
        Check(cycle.SunElevation < -85, $"the sun is {cycle.SunElevation:0} degrees at midnight");
        cycle.SetLocalHour(6);
        Check(Math.Abs(cycle.SunElevation) < 1, $"the sun is {cycle.SunElevation:0} degrees at six");

        cycle.SetLocalHour(9);
        Vector3 east = Vector3.Up.Cross(cycle.LocalUp).Normalized();
        Check(cycle.SunDirection.Dot(east) > 0.5f, "the morning sun is not in the east");
        Check(cycle.MoonDirection.IsEqualApprox(-cycle.SunDirection), "the moon is not opposite the sun");
    }

    /// <summary>
    /// At a pole the sky holds still as the camera moves round it. There the
    /// meridian is undefined, and deciding morning or evening by it flipped
    /// the sky between dawn and sunset as the camera swung round the player.
    /// </summary>
    [Test]
    public void TheSkyHoldsStillAtThePole()
    {
        DayCycle cycle = Cycle(new Vector3(0.4f, 6000, 0), out Node3D observer);
        cycle.SetLocalHour(9);
        Color zenith = cycle.Zenith, horizon = cycle.Horizon;

        for (int step = 0; step < 72; step++)
        {
            float angle = Mathf.DegToRad(step * 5);
            observer.GlobalPosition = new Vector3(0.4f * Mathf.Cos(angle), 6000, 0.4f * Mathf.Sin(angle));
            cycle.Refresh();
            Check(Apart(cycle.Zenith, zenith) < 0.01f && Apart(cycle.Horizon, horizon) < 0.01f,
                $"the sky changed from {horizon.ToHtml(false)} to {cycle.Horizon.ToHtml(false)} as the camera moved round the pole");
        }
    }

    /// <summary>Walking over a pole, the sky's colours change only a little at each step: no flip between dawn and dusk.</summary>
    [Test]
    public void CrossingThePoleIsSmooth()
    {
        DayCycle cycle = Cycle(new Vector3(0, 6000, 0), out Node3D observer);
        cycle.SetLocalHour(7);
        float turn = cycle.Turn;
        Color last = default;

        for (float x = -300; x <= 300; x += 2)
        {
            observer.GlobalPosition = new Vector3(x, Mathf.Sqrt(6000f * 6000f - x * x), 37);
            cycle.Refresh();
            if (x > -300)
                Check(Apart(cycle.Horizon, last) < 0.02f, $"the horizon jumped at {x} units across the pole");
            last = cycle.Horizon;
        }
    }

    /// <summary>A quarter of the day's length later, six hours have passed; and the far side of the planet is the other half of the day.</summary>
    [Test]
    public void TimePassesAndIsLocal()
    {
        DayCycle cycle = Cycle(new Vector3(6000, 0, 0), out Node3D observer);
        cycle.DayLengthMinutes = 1;
        cycle._Process(0);
        cycle.SetLocalHour(8);
        cycle.Running = true;
        cycle._Process(15);
        Near(cycle.LocalHour, 14, 0.01f, "the local hour after a quarter of the day");

        observer.GlobalPosition = new Vector3(-6000, 0, 0);
        cycle.Refresh();
        Near(cycle.LocalHour, 2, 0.01f, "the local hour on the far side of the planet");
        Check(cycle.Effects.Stars > 0.99f, "it is not night on the far side");
    }
}
