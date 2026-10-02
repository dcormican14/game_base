using System;
using System.Collections.Generic;
using Godot;

namespace GameBase.Terrain;

/// <summary>
/// A handful of canyons winding across the planet, each running downhill into
/// an ocean basin like an old riverbed.
///
/// Each canyon is a PATH laid out once, when the planet is made: it starts on
/// an ocean's shelf near the coast, heads inland toward the nearest plains or
/// hills, and meanders on across them for a kilometre or three. Mouths are
/// spread along the coasts. It stops early rather than cut into mountains,
/// run back into an ocean, or come near a sky-island chasm. Its FLOOR sits at
/// the sea floor at the mouth and climbs gently upstream, faster toward the
/// head, until it rises out of the ground and the canyon closes.
///
/// Across, a canyon is a flat sandy floor between stepped rock walls: ledges
/// and short cliffs, each step about 20 units high. Where the ground stands
/// higher the walls simply climb further before meeting it, so a canyon is
/// deeper and wider through hills than across a plain.
///
/// Carving is a height: the ground is the lower of the terrain and the
/// canyon's own profile. Pure; any thread.
/// </summary>
public sealed class Canyons
{
    /// <summary>One canyon: its centre line on the unit sphere, mouth first.</summary>
    public sealed class Path
    {
        public double[] X, Y, Z;

        /// <summary>Distance along the line from the mouth to each point, in world units.</summary>
        public double[] Along;

        public double Length;

        /// <summary>The floor's height at the mouth, against the base radius.</summary>
        public double MouthFloor;

        /// <summary>How far the floor climbs from mouth to head.</summary>
        public double Climb;

        // A cap on the sphere holding the whole canyon, walls and all.
        public double CapX, CapY, CapZ, CapCos;

        public int Count => X.Length;
    }

    private readonly List<Path> _paths = new();
    private readonly double _radius, _mouthWidth, _headWidth;
    private readonly ulong _seed;

    /// <summary>World units between the points of a canyon's line.</summary>
    private const double Stride = 40;

    // The walls: each step rises this far, over a tread and a short riser.
    private const double StepRise = 22;
    private const double StepTread = 9;
    private const double StepRiser = 6;

    public IReadOnlyList<Path> Paths => _paths;

    /// <param name="regionAt">The region a direction lies in, as the terrain has it.</param>
    /// <param name="avoid">Is a direction near somewhere canyons keep out of?</param>
    public Canyons(ulong seed, double radius, RegionMap regions, Func<double, double, double, int> regionAt,
        Func<double, double, double, bool> avoid, TerrainSettings settings)
    {
        _seed = seed;
        _radius = radius;
        _mouthWidth = settings.CanyonWidth;
        _headWidth = settings.CanyonHeadWidth;

        int low = Math.Min(settings.CanyonsLow, settings.CanyonsHigh);
        int wanted = low + (int)(Noise.Random(seed, 1) * (Math.Max(settings.CanyonsLow, settings.CanyonsHigh) - low + 1));

        // Mouths: basin regions on an ocean's shore -- with plains or hills
        // among their nearest neighbours -- in a shuffled order.
        var shores = new List<int>();
        for (int i = 0; i < regions.Count; i++)
        {
            if (regions.Types[i] == RegionType.Basin && Coastal(regions, i))
                shores.Add(i);
        }

        for (int i = shores.Count - 1; i > 0; i--)
        {
            int j = (int)(Noise.Random(seed, 100 + i) * (i + 1));
            (shores[i], shores[j]) = (shores[j], shores[i]);
        }

        var mouths = new List<Vector3>();
        foreach (int shore in shores)
        {
            if (_paths.Count >= wanted)
                break;

            // Spread along the coasts, not bunched.
            var at = new Vector3((float)regions.X[shore], (float)regions.Y[shore], (float)regions.Z[shore]);
            if (mouths.Exists(m => m.AngleTo(at) * radius < 1500))
                continue;

            double length = settings.CanyonLengthLow
                + (settings.CanyonLengthHigh - settings.CanyonLengthLow) * Noise.Random(seed, 200 + shore);

            Path path = Lay(regions, shore, length, regionAt, avoid);
            if (path == null)
                continue;

            // The floor starts as deep as an ocean's shelf -- a notch cut deep
            // into the coast, carrying on as a trench across the shallows --
            // and climbs until it rises out of the land at the head.
            double mouth = -(settings.BasinDepthLow
                + (settings.BasinDepthHigh - settings.BasinDepthLow) * Noise.Random(seed, 300 + shore));
            path.MouthFloor = mouth;
            path.Climb = -mouth + 25;
            _paths.Add(path);
            mouths.Add(at);
        }
    }

    /// <summary>Does a basin region have plains or hills among its nearest neighbours?</summary>
    private static bool Coastal(RegionMap regions, int id)
    {
        var nearest = new List<(double Dot, int Id)>();
        for (int i = 0; i < regions.Count; i++)
        {
            if (i != id)
                nearest.Add((regions.X[i] * regions.X[id] + regions.Y[i] * regions.Y[id] + regions.Z[i] * regions.Z[id], i));
        }

        nearest.Sort((a, b) => b.Dot.CompareTo(a.Dot));
        for (int n = 0; n < Math.Min(6, nearest.Count); n++)
        {
            if (regions.Types[nearest[n].Id] is RegionType.Flat or RegionType.Hills)
                return true;
        }

        return false;
    }

    /// <summary>Is a direction inside a canyon -- between its rims?</summary>
    public bool Inside(double ux, double uy, double uz)
    {
        foreach (Path path in _paths)
        {
            if (path.CapX * ux + path.CapY * uy + path.CapZ * uz < path.CapCos)
                continue;

            Nearest(path, ux, uy, uz, out double across, out double along);
            if (across < WidthAt(path, along) * 0.5)
                return true;
        }

        return false;
    }

    /// <summary>A canyon's width, rim to rim, a distance up from its mouth.</summary>
    private double WidthAt(Path path, double along)
    {
        double t = Math.Clamp(along / path.Length, 0, 1);
        double width = _mouthWidth + (_headWidth - _mouthWidth) * t;
        return width * (1 + 0.15 * Noise.Fbm(_seed + 3, along / 200, 0.5, 0.5, 2));
    }

    /// <summary>Walks a canyon's line out of a basin; null when it cannot get far enough.</summary>
    private Path Lay(RegionMap regions, int basin, double length, Func<double, double, double, int> regionAt,
        Func<double, double, double, bool> avoid)
    {
        var u = new Vector3((float)regions.X[basin], (float)regions.Y[basin], (float)regions.Z[basin]);

        // Toward the nearest plains or hills.
        int toward = -1;
        double best = -2;
        for (int i = 0; i < regions.Count; i++)
        {
            if (regions.Types[i] is not (RegionType.Flat or RegionType.Hills))
                continue;

            double dot = regions.X[i] * u.X + regions.Y[i] * u.Y + regions.Z[i] * u.Z;
            if (dot > best)
            {
                best = dot;
                toward = i;
            }
        }

        if (toward < 0)
            return null;

        var target = new Vector3((float)regions.X[toward], (float)regions.Y[toward], (float)regions.Z[toward]);
        Vector3 heading = (target - u * u.Dot(target)).Normalized();

        var points = new List<Vector3> { u };
        double step = Stride / _radius;
        bool leftBasin = false;
        ulong wander = Noise.Derive(_seed, (ulong)basin + 7);

        for (int n = 1; n * Stride <= length; n++)
        {
            // Meander: turn a little each step, by a slowly changing amount,
            // so the line swings back and forth without doubling back.
            double turn = 0.3 * Noise.Fbm(wander, n * 0.16, 0.5, 0.5, 2);
            Vector3 side = u.Cross(heading).Normalized();
            heading = (heading * (float)Math.Cos(turn) + side * (float)Math.Sin(turn)).Normalized();

            Vector3 next = (u + heading * (float)step).Normalized();
            int region = regionAt(next.X, next.Y, next.Z);
            RegionType type = regions.Types[region];

            // Out of the ocean and into plains or hills; never mountains, back
            // into an ocean, or a sky island.
            if (type == RegionType.Mountains || avoid(next.X, next.Y, next.Z))
                break;
            if (type != RegionType.Basin)
                leftBasin = true;
            else if (leftBasin)
                break;

            heading = (heading - next * next.Dot(heading)).Normalized();
            u = next;
            points.Add(u);
        }

        if (!leftBasin)
            return null;

        // Start just off the coast: the walk began out on the shelf, and a
        // canyon across the sea floor would be no canyon at all.
        int coast = points.FindIndex(p => regions.Types[regionAt(p.X, p.Y, p.Z)] != RegionType.Basin);
        points.RemoveRange(0, Math.Max(0, coast - 4));

        if (points.Count * Stride < 600)
            return null;

        var path = new Path
        {
            X = new double[points.Count],
            Y = new double[points.Count],
            Z = new double[points.Count],
            Along = new double[points.Count],
        };

        Vector3 middle = Vector3.Zero;
        for (int i = 0; i < points.Count; i++)
        {
            path.X[i] = points[i].X;
            path.Y[i] = points[i].Y;
            path.Z[i] = points[i].Z;
            path.Along[i] = i == 0 ? 0 : path.Along[i - 1] + points[i].DistanceTo(points[i - 1]) * _radius;
            middle += points[i];
        }

        path.Length = path.Along[^1];
        middle = middle.Normalized();

        double reach = 0;
        foreach (Vector3 point in points)
            reach = Math.Max(reach, middle.AngleTo(point));

        path.CapX = middle.X;
        path.CapY = middle.Y;
        path.CapZ = middle.Z;
        path.CapCos = Math.Cos(reach + (Math.Max(_mouthWidth, _headWidth) * 2 + 60) / _radius);
        return path;
    }

    /// <summary>
    /// The ground's distance with every canyon near a direction carved out of
    /// it: the lower of the two, as heights.
    /// </summary>
    /// <param name="altitude">How far above the base radius the point is.</param>
    public double Carve(double distance, double ux, double uy, double uz, double altitude)
    {
        foreach (Path path in _paths)
        {
            if (path.CapX * ux + path.CapY * uy + path.CapZ * uz < path.CapCos)
                continue;

            Nearest(path, ux, uy, uz, out double across, out double along);

            double t = Math.Clamp(along / path.Length, 0, 1);
            double floor = path.MouthFloor + path.Climb * t * t;
            double bed = WidthAt(path, along) * 0.5 * 0.35;
            double height = floor + Walls(across - bed);

            distance = Math.Min(distance, height - altitude);
        }

        return distance;
    }

    /// <summary>Is a direction on or near any canyon (within its walls)? For tools and tests.</summary>
    public bool Near(Vector3 direction, out double across, out double along, out Path which)
    {
        across = double.MaxValue;
        along = 0;
        which = null;

        foreach (Path path in _paths)
        {
            Nearest(path, direction.X, direction.Y, direction.Z, out double a, out double l);
            if (a < across)
            {
                across = a;
                along = l;
                which = path;
            }
        }

        return which != null && across < _mouthWidth;
    }

    /// <summary>The stepped rise of a canyon's wall, from the edge of its floor out.</summary>
    private static double Walls(double outward)
    {
        if (outward <= 0)
            return 0;

        const double period = StepTread + StepRiser;
        double steps = outward / period;
        double whole = Math.Floor(steps);
        double part = steps - whole;
        double t = Math.Clamp((part - StepTread / period) / (StepRiser / period), 0, 1);

        return StepRise * (whole + t * t * (3 - 2 * t));
    }

    /// <summary>How far a direction is from a canyon's line, and how far along it the nearest point is.</summary>
    private void Nearest(Path path, double ux, double uy, double uz, out double across, out double along)
    {
        double best = double.MaxValue;
        along = 0;

        for (int i = 0; i + 1 < path.Count; i++)
        {
            double ax = path.X[i], ay = path.Y[i], az = path.Z[i];
            double bx = path.X[i + 1] - ax, by = path.Y[i + 1] - ay, bz = path.Z[i + 1] - az;
            double px = ux - ax, py = uy - ay, pz = uz - az;

            double lengthSq = bx * bx + by * by + bz * bz;
            double t = lengthSq > 0 ? Math.Clamp((px * bx + py * by + pz * bz) / lengthSq, 0, 1) : 0;
            double dx = px - bx * t, dy = py - by * t, dz = pz - bz * t;
            double d = dx * dx + dy * dy + dz * dz;

            if (d < best)
            {
                best = d;
                along = path.Along[i] + t * (path.Along[i + 1] - path.Along[i]);
            }
        }

        across = Math.Sqrt(best) * _radius;
    }
}
