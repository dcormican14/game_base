using System;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace GameBase.Terrain;

/// <summary>
/// A few places where a piece of the planet is shattering and floating up: a
/// deep crater, filled with broken ground drifting up out of it.
///
/// ZONES. Three to five round zones, a kilometre or two across, centred on
/// region points far apart. A zone takes over whatever region was there: at
/// its rim the ground drops in a steep rock wall to a rubble floor about 250
/// down, as if the pieces tore out of it.
///
/// CRACKING. The land is only starting to go. Round the rim, and down the top
/// of the wall, the ground is split into plates by deep crevices, each plate
/// lifted a unit or two, more toward the edge. The floor is split the same
/// way, into wider plates still resting where they lie.
///
/// PIECES. The crater is filled with broken ground, frozen mid-drift, in a
/// heap that fills it low down and narrows to a point about
/// <see cref="TerrainSettings.IslandHeight"/> over the rim at its middle:
///   - FLOES at the rim, broken plates of the ground at its own height, close
///     enough at the edge to hop across, drifting apart and up further in;
///   - GREAT slabs filling the crater low down;
///   - MIDDLE and SMALL pieces everywhere, sparser the higher and further in,
///     so that toward the top only small pieces drift.
/// Each piece is one of three kinds, all upright: a SLAB (sand on a flat top,
/// rock beneath, tapering to hanging points), a CHUNK of bare rock (steep
/// faces a peak above and below, too steep for sand), or a SHARD (a long
/// splinter, leaning a little). Pieces do not overlap much, so each stands
/// clear.
///
/// A zone's pieces are laid out once, the first time anything asks about the
/// zone, and filed in a lattice of buckets, each listing the pieces that come
/// within <see cref="Plateau"/> of it. A point looks only at its bucket's
/// pieces, and anything further is certainly more than <see cref="Plateau"/>
/// away -- so the distance is exact down to -<see cref="Plateau"/>, and kept
/// from going lower there so it stays continuous. Pieces are joined as solids
/// are: the greatest distance of them.
///
/// Pure; any thread.
/// </summary>
public sealed class SkyIslands
{
    public sealed class Zone
    {
        public double X, Y, Z;

        /// <summary>From the centre to the rim, in world units, before the rim wanders.</summary>
        public double Radius;

        /// <summary>The height of the ground around the rim, against the base radius.</summary>
        public double Rim;

        public double CapCos;

        /// <summary>Directions across the zone at its centre.</summary>
        internal double Ex, Ey, Ez, Nx, Ny, Nz;

        internal Lazy<Filed> Pieces;

        /// <summary>How many pieces of each kind float in it: slabs, chunks, shards.</summary>
        public (int Slabs, int Chunks, int Shards) Counts
        {
            get
            {
                int slabs = 0, chunks = 0, shards = 0;
                foreach (Piece piece in Pieces.Value.All)
                {
                    switch (piece.Kind)
                    {
                        case PieceKind.Slab: slabs++; break;
                        case PieceKind.Chunk: chunks++; break;
                        default: shards++; break;
                    }
                }

                return (slabs, chunks, shards);
            }
        }
    }

    internal enum PieceKind : byte { Slab, Chunk, Shard }

    /// <summary>
    /// One floating piece. Its axis is up (a shard's leans a little); Across
    /// and Side are square to it. A slab's centre is the middle of its top.
    /// </summary>
    internal readonly struct Piece
    {
        public readonly double X, Y, Z;
        public readonly float Ax, Ay, Az, Ex, Ey, Ez, Sx, Sy, Sz;
        public readonly PieceKind Kind;

        /// <summary>Slab: its radius across. Chunk: its radius. Shard: its half width.</summary>
        public readonly float Size;

        /// <summary>Slab: its thickness. Chunk: how much taller than wide. Shard: its length above the middle.</summary>
        public readonly float Tall;

        /// <summary>Shard: its length below the middle.</summary>
        public readonly float Low;

        /// <summary>No part of it is further than this from its centre.</summary>
        public readonly float Bound;

        public readonly ulong Own;

        public Piece(Vector3 axis, Vector3 across, PieceKind kind, double size, double tall,
            double low, double bound, ulong own, double x, double y, double z)
        {
            X = x; Y = y; Z = z;
            Vector3 side = axis.Cross(across);
            Ax = axis.X; Ay = axis.Y; Az = axis.Z;
            Ex = across.X; Ey = across.Y; Ez = across.Z;
            Sx = side.X; Sy = side.Y; Sz = side.Z;
            Kind = kind;
            Size = (float)size;
            Tall = (float)tall;
            Low = (float)low;
            Bound = (float)bound;
            Own = own;
        }
    }

    /// <summary>A zone's pieces, and the buckets they are filed in (in the zone's own frame).</summary>
    internal sealed class Filed
    {
        public Piece[] All;
        public double OriginX, OriginY, OriginZ;
        public double MinX, MinY, MinZ;
        public int Nx, Ny, Nz;
        public int[] Start;
        public int[] Items;
    }

    private readonly List<Zone> _zones = new();
    private readonly double _radius, _depth, _height;
    private readonly ulong _seed;

    public IReadOnlyList<Zone> Zones => _zones;

    /// <summary>How far in from the rim the chasm wall takes to reach the floor.</summary>
    private const double Wall = 110;

    /// <summary>
    /// How far below zero the distance is kept exact near the pieces (and no
    /// lower). Further than this from every piece, a point may skip them.
    /// </summary>
    public const double Plateau = 12;

    /// <summary>The buckets' width.</summary>
    private const double Bucket = 32;

    // The cracked ground: plate size, crevice width and depth; how far out
    // past the rim the cracks reach, and how high the plates there lift.
    private const double RimCell = 36, RimWidth = 3.5, RimDepth = 25, RimReach = 180, RimLift = 2.5;
    private const double FloorCell = 55, FloorWidth = 2.5, FloorDepth = 12;

    /// <summary>The highest the rubble on a chasm floor stands.</summary>
    private const double Rubble = 20;

    /// <summary>One layer of pieces: its lattice and what it deals.</summary>
    private sealed record Layer(
        double Across, double Tier, double Slabs, double Chunks, double Shrink, bool Floes, ulong Seed,
        Func<double, double, double> Keep);

    private readonly Layer[] _layers;

    /// <param name="heightAt">The plain terrain's height at a direction, against the base radius.</param>
    public SkyIslands(ulong seed, double radius, RegionMap regions, Func<Vector3, double> heightAt,
        TerrainSettings settings)
    {
        _seed = seed;
        _radius = radius;
        _depth = settings.ChasmDepth;
        _height = settings.IslandHeight;

        // Keep: the chance a lattice point holds a piece, from how far in it
        // is (0 rim, 1 centre) and how far along the climb (see Progress).
        _layers = new Layer[]
        {
            // Floes at the rim, drifting apart and up further in.
            new(44, 0, 1, 0, 0.45, true, Noise.Derive(seed, 11), (inward, _) => 0.97 - 0.5 * Math.Min(1, inward / FloeBand)),
            // Great slabs filling the crater low down.
            new(110, 75, 0.85, 0.15, 0.5, false, Noise.Derive(seed, 12), (_, q) => 0.9 * (1 - SmoothStep(0.1, 0.5, q))),
            // Middle pieces everywhere, thinning as they climb.
            new(52, 40, 0.45, 0.37, 0.45, false, Noise.Derive(seed, 13), (_, q) => 0.8 - 0.55 * q),
            // Small pieces, most of what drifts at the top.
            new(26, 24, 0.22, 0.45, 0.3, false, Noise.Derive(seed, 14), (_, q) => 0.3 + 0.25 * q),
        };

        int low = Math.Min(settings.IslandZonesLow, settings.IslandZonesHigh);
        int count = low + (int)(Noise.Random(seed, 1) * (Math.Max(settings.IslandZonesLow, settings.IslandZonesHigh) - low + 1));

        // Region points in a shuffled order, taken while far from those
        // already taken.
        var order = new List<int>();
        for (int i = 0; i < regions.Count; i++)
            order.Add(i);

        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = (int)(Noise.Random(seed, 50 + i) * (i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }

        foreach (int id in order)
        {
            if (_zones.Count >= count)
                break;

            var centre = new Vector3((float)regions.X[id], (float)regions.Y[id], (float)regions.Z[id]);

            bool apart = true;
            foreach (Zone other in _zones)
            {
                double angle = centre.AngleTo(new Vector3((float)other.X, (float)other.Y, (float)other.Z));
                if (angle * radius < 4000)
                    apart = false;
            }

            if (!apart)
                continue;

            // As wide as the region allows: most of the way to its nearest neighbour.
            double nearest = double.MaxValue;
            for (int i = 0; i < regions.Count; i++)
            {
                if (i == id)
                    continue;

                double dot = regions.X[i] * centre.X + regions.Y[i] * centre.Y + regions.Z[i] * centre.Z;
                nearest = Math.Min(nearest, Math.Acos(Math.Clamp(dot, -1, 1)) * radius);
            }

            Vector3 east = centre.Cross(Mathf.Abs(centre.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            Vector3 north = centre.Cross(east);

            var zone = new Zone
            {
                X = centre.X,
                Y = centre.Y,
                Z = centre.Z,
                Radius = Math.Clamp(nearest * 0.55, 700, 1100),
                Ex = east.X, Ey = east.Y, Ez = east.Z,
                Nx = north.X, Ny = north.Y, Nz = north.Z,
            };

            // The rim's height: the ground round the edge, averaged.
            double rim = 0;
            const int Around = 24;
            for (int n = 0; n < Around; n++)
            {
                double a = Math.Tau * n / Around;
                Vector3 out1 = (east * (float)Math.Cos(a) + north * (float)Math.Sin(a)) * (float)(zone.Radius / radius);
                rim += heightAt((centre + out1).Normalized());
            }

            zone.Rim = rim / Around;
            // Out past the rim's wander and its cracks, so nothing the zone
            // changes stops short at the cap's edge.
            zone.CapCos = Math.Cos((zone.Radius * 1.1 + RimReach + 40) / radius);
            zone.Pieces = new Lazy<Filed>(() => Lay(zone), LazyThreadSafetyMode.ExecutionAndPublication);
            _zones.Add(zone);
        }
    }

    /// <summary>How far in (as a share of the radius) the rim's floes reach.</summary>
    private const double FloeBand = 0.3;

    /// <summary>The highest anything in a zone reaches, against the base radius.</summary>
    public double Highest
    {
        get
        {
            double top = double.NegativeInfinity;
            foreach (Zone zone in _zones)
                top = Math.Max(top, zone.Rim + _height + 60);
            return top;
        }
    }

    /// <summary>The lowest a chasm floor reaches (a crevice in it), against the base radius.</summary>
    public double Lowest
    {
        get
        {
            double bottom = double.PositiveInfinity;
            foreach (Zone zone in _zones)
                bottom = Math.Min(bottom, zone.Rim - _depth - FloorDepth - 30);
            return bottom;
        }
    }

    /// <summary>
    /// How far into a zone a direction is, 0 at the rim to 1 at the centre,
    /// and below 0 outside; and which zone. -1 when none is near.
    /// </summary>
    public int ZoneAt(double ux, double uy, double uz, out double inside)
    {
        inside = -1;

        for (int i = 0; i < _zones.Count; i++)
        {
            Zone zone = _zones[i];
            double dot = zone.X * ux + zone.Y * uy + zone.Z * uz;
            if (dot < zone.CapCos)
                continue;

            inside = Inside(zone, ux, uy, uz, dot) / zone.Radius;
            return i;
        }

        return -1;
    }

    /// <summary>Units in from a zone's wandering rim (negative outside).</summary>
    private double Inside(Zone zone, double ux, double uy, double uz, double dot)
    {
        double across = Math.Acos(Math.Clamp(dot, -1, 1)) * _radius;
        double s = _radius / 300;
        double wander = 1 + 0.08 * Noise.Fbm(_seed + 5, ux * s, uy * s, uz * s, 2);
        return zone.Radius * wander - across;
    }

    /// <summary>
    /// The terrain's distance with any zone here applied: the chasm and its
    /// cracks in place of the ground, and -- unless left out -- the pieces
    /// floating over it.
    /// </summary>
    /// <param name="plateau">
    /// Keep the distance from going below -<see cref="Plateau"/> everywhere,
    /// not only near the pieces: for the terrain inside a box the pieces
    /// reach, so it is continuous throughout the box.
    /// </param>
    public double Apply(double distance, double x, double y, double z, double ux, double uy, double uz,
        double altitude, bool islands = true, bool plateau = false)
    {
        foreach (Zone zone in _zones)
        {
            double dot = zone.X * ux + zone.Y * uy + zone.Z * uz;
            if (dot < zone.CapCos)
                continue;

            double inside = Inside(zone, ux, uy, uz, dot);

            // The chasm: the rim wall drops to a rubble floor.
            double sx = ux * _radius, sy = uy * _radius, sz = uz * _radius;
            double rubble = 14 * Noise.Ridged(_seed + 6, sx / 70, sy / 70, sz / 70, 3)
                + 6 * Noise.Fbm(_seed + 7, sx / 20, sy / 20, sz / 20, 2);
            double floor = zone.Rim - _depth + rubble;
            double sink = SmoothStep(-10, Wall, inside);
            distance = distance + sink * (floor - altitude - distance);

            distance = Crack(distance, inside, sx, sy, sz);

            if (!islands)
                return distance;

            double pieces = Pieces(zone, x, y, z, out bool near);
            if (plateau || near)
                distance = Math.Max(distance, -Plateau);

            return Math.Max(distance, pieces);
        }

        return plateau ? Math.Max(distance, -Plateau) : distance;
    }

    // ------------------------------------------------------------ cracking

    /// <summary>The rim's and the floor's crevices, and the rim's plates lifted a little.</summary>
    private double Crack(double distance, double inside, double sx, double sy, double sz)
    {
        // Round the rim and down the top of the wall.
        double rim = SmoothStep(-RimReach, -40, inside) * (1 - SmoothStep(Wall * 0.6, Wall, inside));
        if (rim > 0 && distance > -6 && distance < 5 * RimDepth)
        {
            double border = Border(_seed + 20, sx, sy, sz, RimCell, out ulong plate);

            // Lifted nearest the edge, and only near the surface -- enough
            // to raise it -- easing to nothing at each crevice.
            double lift = RimLift * SmoothStep(-RimReach, 0, inside) * (1 - SmoothStep(0, 60, inside))
                * Noise.Random(plate, 0)
                * SmoothStep(RimWidth / 2, RimWidth / 2 + 5, border)
                * (1 - SmoothStep(3, 5, Math.Abs(distance)));

            distance = Carve(distance + lift, border, RimWidth, RimDepth, rim);
        }

        // Across the floor.
        double floor = SmoothStep(Wall * 0.5, Wall, inside);
        if (floor > 0 && distance > -2 && distance < 5 * FloorDepth)
        {
            double border = Border(_seed + 21, sx, sy, sz, FloorCell, out _);
            distance = Carve(distance, border, FloorWidth, FloorDepth, floor);
        }

        return distance;
    }

    /// <summary>
    /// A crevice cut where a point is within half a width of a plate's
    /// border, as deep as given below the surface. Weaker, it narrows away to
    /// nothing. Leaves the distance alone half a width out into the air, and
    /// five depths down: there the cut cannot reach.
    /// </summary>
    private static double Carve(double distance, double border, double width, double depth, double strength)
    {
        double open = border - width * 0.5 + (1 - strength) * 12;
        return Math.Min(distance, Math.Max(open, 1.25 * (distance - depth)));
    }

    /// <summary>
    /// How far a point is from the nearest border between the cells of a
    /// jittered lattice -- plates, their edges wandering a little -- and a
    /// seed for the plate it is on.
    /// </summary>
    private static double Border(ulong seed, double x, double y, double z, double cell, out ulong plate)
    {
        // Ragged edges.
        x += 4 * Noise.Gradient(seed + 1, x / 35, y / 35, z / 35);
        y += 4 * Noise.Gradient(seed + 2, x / 35, y / 35, z / 35);
        z += 4 * Noise.Gradient(seed + 3, x / 35, y / 35, z / 35);

        double fx = x / cell, fy = y / cell, fz = z / cell;
        long bx = (long)Math.Floor(fx), by = (long)Math.Floor(fy), bz = (long)Math.Floor(fz);

        Span<double> px = stackalloc double[27];
        Span<double> py = stackalloc double[27];
        Span<double> pz = stackalloc double[27];

        int nearest = 0;
        double best = double.MaxValue;
        long nx = bx, ny = by, nz = bz;

        for (int n = 0; n < 27; n++)
        {
            long i = bx + n % 3 - 1, j = by + n / 3 % 3 - 1, k = bz + n / 9 - 1;
            px[n] = i + 0.1 + 0.8 * Noise.Random(seed, i, j, k);
            py[n] = j + 0.1 + 0.8 * Noise.Random(seed + 4, i, j, k);
            pz[n] = k + 0.1 + 0.8 * Noise.Random(seed + 5, i, j, k);

            double dx = fx - px[n], dy = fy - py[n], dz = fz - pz[n];
            double d = dx * dx + dy * dy + dz * dz;
            if (d < best)
            {
                best = d;
                nearest = n;
                nx = i; ny = j; nz = k;
            }
        }

        // To the plane halfway between the nearest site and each other one.
        double border = double.MaxValue;
        for (int n = 0; n < 27; n++)
        {
            if (n == nearest)
                continue;

            double ax = px[n] - px[nearest], ay = py[n] - py[nearest], az = pz[n] - pz[nearest];
            double apart = Math.Sqrt(ax * ax + ay * ay + az * az);
            double dx = fx - px[n], dy = fy - py[n], dz = fz - pz[n];
            double other = dx * dx + dy * dy + dz * dz;
            border = Math.Min(border, (other - best) / (2 * apart));
        }

        plate = Noise.Derive(seed, (ulong)(nx * 73856093L ^ ny * 19349663L ^ nz * 83492791L));
        return border * cell;
    }

    // -------------------------------------------------------------- pieces

    /// <summary>
    /// The greatest distance of the pieces near a point (negative infinity if
    /// none), and whether the point is among the zone's buckets at all.
    /// </summary>
    private static double Pieces(Zone zone, double x, double y, double z, out bool near)
    {
        Filed filed = zone.Pieces.Value;
        near = false;

        double rx = x - filed.OriginX, ry = y - filed.OriginY, rz = z - filed.OriginZ;
        int i = (int)Math.Floor((rx * zone.Ex + ry * zone.Ey + rz * zone.Ez - filed.MinX) / Bucket);
        int j = (int)Math.Floor((rx * zone.Nx + ry * zone.Ny + rz * zone.Nz - filed.MinY) / Bucket);
        int k = (int)Math.Floor((rx * zone.X + ry * zone.Y + rz * zone.Z - filed.MinZ) / Bucket);

        if (i < 0 || j < 0 || k < 0 || i >= filed.Nx || j >= filed.Ny || k >= filed.Nz)
            return double.NegativeInfinity;

        near = true;
        int b = (i * filed.Ny + j) * filed.Nz + k;
        double best = double.NegativeInfinity;

        for (int n = filed.Start[b]; n < filed.Start[b + 1]; n++)
            best = Math.Max(best, Measure(filed.All[filed.Items[n]], x, y, z));

        return best;
    }

    /// <summary>One piece's distance at a point: never more than its bound less how far off it is.</summary>
    private static double Measure(in Piece piece, double x, double y, double z)
    {
        double dx = x - piece.X, dy = y - piece.Y, dz = z - piece.Z;
        double off = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double bound = piece.Bound - off;
        if (bound < -Plateau)
            return bound;

        double h = dx * piece.Ax + dy * piece.Ay + dz * piece.Az;
        double a = dx * piece.Ex + dy * piece.Ey + dz * piece.Ez;
        double b = dx * piece.Sx + dy * piece.Sy + dz * piece.Sz;
        double shape;

        switch (piece.Kind)
        {
            case PieceKind.Slab:
            {
                // A near-flat top, gently rounded at the edge, over a taper to
                // hanging points; the outline pulled into bays and headlands.
                double size = piece.Size, thickness = piece.Tall;
                double across = Math.Sqrt(a * a + b * b);
                double around = 0;
                if (across > 1e-6)
                {
                    double w = 2.6 / across;
                    around = Noise.Gradient(piece.Own, a * w + 4.1, b * w + 9.7, 1.3);
                }

                double stretched = across * (1 + 0.2 * around);
                double r = Math.Min(1, stretched / size);
                double surface = 1.5 * Noise.Fbm(piece.Own, x / 25, y / 25, z / 25, 2) - 2.5 * r * r;
                double jag = Math.Min(0.25 * thickness, 10) * Noise.Ridged(piece.Own + 1, x / 18, y / 18, z / 18, 2) * (1 - r);
                double under = -thickness * (1 - r * r) - jag;
                shape = Math.Min(size - stretched, Math.Min(surface - h, h - under));
                break;
            }
            case PieceKind.Chunk:
            {
                // Peaked above and below, faces too steep for sand, sides
                // squared off; knobbly.
                double size = piece.Size, stretch = piece.Tall;
                double bump = 0.12 * size * Noise.Ridged(piece.Own, x / (0.7 * size), y / (0.7 * size), z / (0.7 * size), 2);
                double v = h / stretch;
                double peaks = (size + bump - (Math.Abs(a) + Math.Abs(b) + Math.Abs(v))) / Math.Sqrt(2 + 1 / (stretch * stretch));
                double sides = 0.78 * size + 0.5 * bump - Math.Max(Math.Abs(a), Math.Abs(b));
                shape = Math.Min(peaks, sides);
                break;
            }
            default:
            {
                // A splinter, diamond across, pointed at both ends.
                double width = piece.Size;
                double length = h >= 0 ? piece.Tall : piece.Low;
                double slope = width / length;
                double across = (Math.Abs(a) + Math.Abs(b)) / Math.Sqrt(2);
                shape = (width * (1 - Math.Abs(h) / length) - across) / Math.Sqrt(1 + slope * slope);
                break;
            }
        }

        return Math.Min(shape, bound);
    }

    /// <summary>
    /// How far along the climb a point is, 0..1: in from the rim, and up
    /// over it. Pieces get sparser and smaller along it.
    /// </summary>
    private double Progress(double inward, double above) =>
        Math.Clamp(0.55 * inward + 0.6 * Math.Max(0, above) / _height, 0, 1);

    /// <summary>The top of the heap, over the rim, that far in (0 rim, 1 centre).</summary>
    private double Crest(double inward) => 30 + (_height - 30) * Math.Pow(Math.Clamp(inward, 0, 1), 1.6);

    /// <summary>A piece being laid out: where it is in the zone's frame, and how far it reaches.</summary>
    private readonly record struct Placed(double X, double Y, double H, double Across, double Above, double Below);

    /// <summary>Lays out a zone's pieces, layer by layer, and files them.</summary>
    private Filed Lay(Zone zone)
    {
        var centre = new Vector3((float)zone.X, (float)zone.Y, (float)zone.Z);
        var east = new Vector3((float)zone.Ex, (float)zone.Ey, (float)zone.Ez);
        var north = new Vector3((float)zone.Nx, (float)zone.Ny, (float)zone.Nz);

        var pieces = new List<Piece>();
        var placed = new List<Placed>();
        var nearby = new Dictionary<(long, long, long), List<int>>();
        const double Near = 128;

        double floorTop = zone.Rim - _depth + Rubble;
        double reach = zone.Radius * 1.1;

        foreach (Layer layer in _layers)
        {
            double cell = layer.Across;
            int across = (int)Math.Ceiling(reach / cell) + 1;
            int tiers = layer.Floes ? 1 : (int)Math.Ceiling((zone.Rim + _height - floorTop) / layer.Tier);

            for (int i = -across; i <= across; i++)
            for (int j = -across; j <= across; j++)
            for (int t = 0; t < tiers; t++)
            {
                ulong own = Noise.Derive(layer.Seed, (ulong)(i * 73856093L ^ j * 19349663L ^ t * 83492791L));
                double px = (i + 0.2 + 0.6 * Noise.Random(own, 0)) * cell;
                double py = (j + 0.2 + 0.6 * Noise.Random(own, 1)) * cell;

                // Cheap first: far outside the rim, whatever it wanders.
                double flat = Math.Sqrt(px * px + py * py) / zone.Radius;
                if (flat > 1.1)
                    continue;

                Vector3 direction = (centre * (float)_radius + east * (float)px + north * (float)py).Normalized();
                double dot = direction.Dot(centre);
                if (dot < zone.CapCos)
                    continue;

                double inward = Inside(zone, direction.X, direction.Y, direction.Z, dot) / zone.Radius;
                if (inward <= 0)
                    continue;

                double h = layer.Floes
                    ? zone.Rim - 2 + 3 * Noise.Random(own, 2) + 140 * Math.Pow(Math.Min(1, inward / FloeBand), 2)
                    : floorTop + (t + 0.2 + 0.6 * Noise.Random(own, 2)) * layer.Tier;

                if (layer.Floes && inward > FloeBand)
                    continue;

                double q = Progress(inward, h - zone.Rim);
                if (Noise.Random(own, 3) > layer.Keep(inward, q))
                    continue;

                // What it is, and how big.
                double pick = Noise.Random(own, 4);
                PieceKind kind = pick < layer.Slabs ? PieceKind.Slab
                    : pick < layer.Slabs + layer.Chunks ? PieceKind.Chunk
                    : PieceKind.Shard;

                double shrink = 1 - layer.Shrink * (layer.Floes ? Math.Min(1, inward / FloeBand) : q);
                double r1 = Noise.Random(own, 5), r2 = Noise.Random(own, 6);
                Vector3 up = direction;
                Vector3 axis = up;
                double size, tall, low = 0, bound, spread, above, below;

                switch (kind)
                {
                    case PieceKind.Slab:
                        size = 0.5 * cell * (layer.Floes ? 1.05 : 0.75 + 0.35 * r1) * shrink;
                        tall = Math.Max(8, size * (0.45 + 0.35 * r2));
                        spread = size * 1.2;
                        above = 3;
                        below = tall + Math.Min(0.25 * tall, 10);

                        // Headlands reach a quarter past its size.
                        bound = Math.Sqrt(1.5625 * size * size + below * below) + 2;
                        break;
                    case PieceKind.Chunk:
                        size = Math.Max(2.5, 0.32 * cell * (0.7 + 0.5 * r1) * shrink);
                        tall = 1 + 0.6 * r2;
                        spread = size * 0.9;
                        above = below = size * tall * 1.12;

                        // Its squared-off sides' corners, and its peaks.
                        bound = Math.Sqrt(1.42 * size * size + above * above) + 1;
                        break;
                    default:
                    {
                        size = Math.Max(2, 0.07 * cell * (0.8 + 0.5 * r1) * shrink);
                        tall = size * (3.5 + 3 * r2);
                        low = tall * (0.4 + 0.3 * Noise.Random(own, 7));

                        // Leaning up to about 20 degrees.
                        double lean = 0.36 * Noise.Random(own, 8), turn = Math.Tau * Noise.Random(own, 9);
                        Vector3 side = east * (float)Math.Cos(turn) + north * (float)Math.Sin(turn);
                        axis = (up + side * (float)lean).Normalized();
                        spread = size + lean * tall;
                        above = tall;
                        below = low;
                        bound = Math.Max(tall, low) + size + 1;
                        break;
                    }
                }

                // Under the crest of the heap, over the floor, clear of the wall.
                if (h + above > zone.Rim + Crest(inward) || h - below < floorTop + 5)
                    continue;

                double clear = inward * zone.Radius;
                if (!layer.Floes && h - below < zone.Rim + 10 && clear < Wall + spread * 0.8)
                    continue;
                if (layer.Floes && clear < 12 + spread * 0.9)
                    continue;

                // Clear of the pieces already laid (floes may touch each other).
                var candidate = new Placed(px, py, h, spread, above, below);
                if (Crowded(candidate, placed, nearby, Near, layer.Floes))
                    continue;

                // Turned about its axis at random.
                double spin = Math.Tau * Noise.Random(own, 10);
                Vector3 flat1 = east * (float)Math.Cos(spin) + north * (float)Math.Sin(spin);
                Vector3 acrossAxis = (flat1 - axis * flat1.Dot(axis)).Normalized();

                pieces.Add(new Piece(axis, acrossAxis, kind, size, tall, low, bound, own,
                    direction.X * (_radius + h), direction.Y * (_radius + h), direction.Z * (_radius + h)));

                int index = placed.Count;
                placed.Add(candidate);
                var key = ((long)Math.Floor(px / Near), (long)Math.Floor(py / Near), (long)Math.Floor(h / Near));
                if (!nearby.TryGetValue(key, out List<int> list))
                    nearby[key] = list = new List<int>();
                list.Add(index);
            }
        }

        return File(zone, pieces.ToArray());
    }

    /// <summary>Would a piece overlap one already laid by more than a little?</summary>
    private static bool Crowded(Placed candidate, List<Placed> placed, Dictionary<(long, long, long), List<int>> nearby,
        double near, bool floe)
    {
        long ci = (long)Math.Floor(candidate.X / near), cj = (long)Math.Floor(candidate.Y / near), ck = (long)Math.Floor(candidate.H / near);

        for (long i = ci - 1; i <= ci + 1; i++)
        for (long j = cj - 1; j <= cj + 1; j++)
        for (long k = ck - 1; k <= ck + 1; k++)
        {
            if (!nearby.TryGetValue((i, j, k), out List<int> list))
                continue;

            foreach (int n in list)
            {
                Placed other = placed[n];
                double dx = candidate.X - other.X, dy = candidate.Y - other.Y;
                double apart = Math.Sqrt(dx * dx + dy * dy);
                double room = (candidate.Across + other.Across) * (floe ? 0.5 : 0.85);
                if (apart >= room)
                    continue;

                // Side by side at overlapping heights, or stacked too close.
                bool clearAbove = candidate.H - candidate.Below > other.H + other.Above + 2;
                bool clearBelow = candidate.H + candidate.Above < other.H - other.Below - 2;
                if (!clearAbove && !clearBelow)
                    return true;
            }
        }

        return false;
    }

    /// <summary>Files a zone's pieces into buckets, each listing those within <see cref="Plateau"/> of it.</summary>
    private Filed File(Zone zone, Piece[] pieces)
    {
        var filed = new Filed
        {
            All = pieces,
            OriginX = zone.X * _radius,
            OriginY = zone.Y * _radius,
            OriginZ = zone.Z * _radius,
        };

        // Each piece's reach in the zone's frame.
        var low = new (double X, double Y, double Z)[pieces.Length];
        var high = new (double X, double Y, double Z)[pieces.Length];
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

        for (int n = 0; n < pieces.Length; n++)
        {
            Piece piece = pieces[n];
            double rx = piece.X - filed.OriginX, ry = piece.Y - filed.OriginY, rz = piece.Z - filed.OriginZ;
            double lx = rx * zone.Ex + ry * zone.Ey + rz * zone.Ez;
            double ly = rx * zone.Nx + ry * zone.Ny + rz * zone.Nz;
            double lz = rx * zone.X + ry * zone.Y + rz * zone.Z;
            double r = piece.Bound + Plateau;

            low[n] = (lx - r, ly - r, lz - r);
            high[n] = (lx + r, ly + r, lz + r);
            minX = Math.Min(minX, lx - r); minY = Math.Min(minY, ly - r); minZ = Math.Min(minZ, lz - r);
            maxX = Math.Max(maxX, lx + r); maxY = Math.Max(maxY, ly + r); maxZ = Math.Max(maxZ, lz + r);
        }

        if (pieces.Length == 0)
        {
            filed.Start = new[] { 0, 0 };
            filed.Items = Array.Empty<int>();
            filed.Nx = filed.Ny = filed.Nz = 1;
            filed.MinX = filed.MinY = filed.MinZ = double.MaxValue / 4;
            return filed;
        }

        filed.MinX = minX; filed.MinY = minY; filed.MinZ = minZ;
        filed.Nx = (int)Math.Ceiling((maxX - minX) / Bucket) + 1;
        filed.Ny = (int)Math.Ceiling((maxY - minY) / Bucket) + 1;
        filed.Nz = (int)Math.Ceiling((maxZ - minZ) / Bucket) + 1;

        int buckets = filed.Nx * filed.Ny * filed.Nz;
        var counts = new int[buckets + 1];

        // Twice over the pieces: count each bucket's, then fill them in.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int n = 0; n < pieces.Length; n++)
            {
                int i0 = (int)Math.Floor((low[n].X - minX) / Bucket), i1 = (int)Math.Floor((high[n].X - minX) / Bucket);
                int j0 = (int)Math.Floor((low[n].Y - minY) / Bucket), j1 = (int)Math.Floor((high[n].Y - minY) / Bucket);
                int k0 = (int)Math.Floor((low[n].Z - minZ) / Bucket), k1 = (int)Math.Floor((high[n].Z - minZ) / Bucket);

                for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                for (int k = k0; k <= k1; k++)
                {
                    int b = (i * filed.Ny + j) * filed.Nz + k;
                    if (pass == 0)
                        counts[b + 1]++;
                    else
                        filed.Items[counts[b]++] = n;
                }
            }

            if (pass == 0)
            {
                for (int b = 0; b < buckets; b++)
                    counts[b + 1] += counts[b];

                filed.Start = (int[])counts.Clone();
                filed.Items = new int[counts[buckets]];
            }
        }

        return filed;
    }

    // ------------------------------------------------------------ reaching

    /// <summary>
    /// Could any piece reach into a box (world-local)? Worked out from the
    /// pieces themselves -- where each one is and how big -- not from
    /// samples, so a "no" is certain.
    /// </summary>
    public bool MayReach(Vector3 min, Vector3 max) => Reaching(min, max);

    /// <summary>
    /// Does a box overlap a zone's cap at the heights its pieces float at?
    /// Cheap -- no piece is looked at -- and generous.
    /// </summary>
    public bool Touches(Vector3 min, Vector3 max)
    {
        foreach (Zone zone in _zones)
        {
            if (Over(zone, min, max))
                return true;
        }

        return false;
    }

    private bool Over(Zone zone, Vector3 min, Vector3 max)
    {
        Vector3 centre = (min + max) * 0.5f;
        double half = (max - min).Length() * 0.5;
        double r = centre.Length();
        if (r < half + 1)
            return false;

        double ux = centre.X / r, uy = centre.Y / r, uz = centre.Z / r;
        double spread = Math.Asin(Math.Min(1, half / r));
        double dot = zone.X * ux + zone.Y * uy + zone.Z * uz;
        if (Math.Acos(Math.Clamp(dot, -1, 1)) - spread > Math.Acos(zone.CapCos))
            return false;

        double low = r - half - _radius, high = r + half - _radius;
        return high >= zone.Rim - _depth - 60 && low <= zone.Rim + _height + 60;
    }

    /// <summary>
    /// Does any piece reach into a box? Within a box none reaches, the
    /// terrain there is the ground alone.
    /// </summary>
    public bool Reaching(Vector3 min, Vector3 max)
    {
        foreach (Zone zone in _zones)
        {
            if (!Over(zone, min, max))
                continue;

            Filed filed = zone.Pieces.Value;
            if (filed.All.Length == 0)
                continue;

            // The box in the zone's frame, from its corners.
            double lowX = double.MaxValue, lowY = double.MaxValue, lowZ = double.MaxValue;
            double highX = double.MinValue, highY = double.MinValue, highZ = double.MinValue;
            for (int c = 0; c < 8; c++)
            {
                double cx = ((c & 1) == 0 ? min.X : max.X) - filed.OriginX;
                double cy = ((c & 2) == 0 ? min.Y : max.Y) - filed.OriginY;
                double cz = ((c & 4) == 0 ? min.Z : max.Z) - filed.OriginZ;
                double lx = cx * zone.Ex + cy * zone.Ey + cz * zone.Ez;
                double ly = cx * zone.Nx + cy * zone.Ny + cz * zone.Nz;
                double lz = cx * zone.X + cy * zone.Y + cz * zone.Z;
                lowX = Math.Min(lowX, lx); highX = Math.Max(highX, lx);
                lowY = Math.Min(lowY, ly); highY = Math.Max(highY, ly);
                lowZ = Math.Min(lowZ, lz); highZ = Math.Max(highZ, lz);
            }

            int i0 = Math.Max(0, (int)Math.Floor((lowX - filed.MinX) / Bucket));
            int i1 = Math.Min(filed.Nx - 1, (int)Math.Floor((highX - filed.MinX) / Bucket));
            int j0 = Math.Max(0, (int)Math.Floor((lowY - filed.MinY) / Bucket));
            int j1 = Math.Min(filed.Ny - 1, (int)Math.Floor((highY - filed.MinY) / Bucket));
            int k0 = Math.Max(0, (int)Math.Floor((lowZ - filed.MinZ) / Bucket));
            int k1 = Math.Min(filed.Nz - 1, (int)Math.Floor((highZ - filed.MinZ) / Bucket));
            if (i0 > i1 || j0 > j1 || k0 > k1)
                continue;

            long buckets = (long)(i1 - i0 + 1) * (j1 - j0 + 1) * (k1 - k0 + 1);

            // A big box: every piece is quicker than every bucket.
            if (buckets > filed.All.Length)
            {
                foreach (Piece piece in filed.All)
                {
                    if (Touching(piece, min, max))
                        return true;
                }

                continue;
            }

            for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
            for (int k = k0; k <= k1; k++)
            {
                int b = (i * filed.Ny + j) * filed.Nz + k;
                for (int n = filed.Start[b]; n < filed.Start[b + 1]; n++)
                {
                    if (Touching(filed.All[filed.Items[n]], min, max))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>Does a piece's bounding ball meet a box?</summary>
    private static bool Touching(in Piece piece, Vector3 min, Vector3 max)
    {
        double dx = Math.Max(0, Math.Max(min.X - piece.X, piece.X - max.X));
        double dy = Math.Max(0, Math.Max(min.Y - piece.Y, piece.Y - max.Y));
        double dz = Math.Max(0, Math.Max(min.Z - piece.Z, piece.Z - max.Z));
        return dx * dx + dy * dy + dz * dz <= (double)piece.Bound * piece.Bound;
    }

    private static double SmoothStep(double from, double to, double x)
    {
        double t = Math.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
