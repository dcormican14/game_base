using System;
using System.Collections.Generic;
using Godot;

namespace GameBase.Terrain;

/// <summary>
/// The planet's terrain: flat plains, hill country, mountain ranges and
/// sunken basins, laid out in large regions and blended where they meet; a
/// handful of canyons running into the basins; and a few zones where the
/// ground has shattered into sky islands over a chasm.
///
/// HOW A POINT IS MEASURED. <see cref="Distance"/> runs these steps:
///   1. REGIONS. The point's direction, wandered a little by noise so borders
///      are not straight, finds the regions it blends between
///      (<see cref="RegionMap"/>).
///   2. WARP. The point is pushed sideways by 3D noise, as far as the regions
///      there say: hard in jagged mountains, not at all on the plains. A
///      surface pushed sideways by different amounts at different heights
///      leans, and leaning far enough is an overhang -- this is where the
///      terrain's 3D comes from.
///   3. SHAPE. Each region measures its own distance at the warped point, and
///      the blend weights them together. Plains and hills are a height; each
///      mountain range is a height with its own character, and mesa caprock
///      is a true 3D shape added on top.
///   4. ARCHES. Tubes of noise are carved out of mountain rock, but only
///      enough to break through where a ridge is thin: through a fin they
///      leave an arch, inside a massif they leave nothing.
///   5. CANYONS are cut into the result (<see cref="Canyons"/>).
///   6. SKY ISLANDS: inside a zone, the ground gives way to a chasm and the
///      islands float over it (<see cref="SkyIslands"/>).
///
/// Everything is in doubles, in the world's local space, with the planet's
/// centre at the origin. Features are sampled on the BASE SPHERE -- the point's
/// direction times the base radius -- so they are the same size in world units
/// everywhere and have no seams or pinching at the poles.
/// </summary>
public sealed class PlanetTerrain : ITerrainShape
{
    public PlanetTerrain(double baseRadius, ulong seed, TerrainSettings settings)
    {
        settings ??= new TerrainSettings();

        BaseRadius = baseRadius;
        Seed = seed;

        _blend = settings.BlendWidth;
        _wander = settings.BorderWander;
        _rise = settings.MountainRise;
        _flatRoll = settings.FlatRoll;
        _hillMiddle = settings.HillMiddle;
        _hillSwing = settings.HillSwing;
        _hollowDepth = settings.HollowDepth;
        _basinLow = settings.BasinDepthLow;
        _basinHigh = settings.BasinDepthHigh;
        _basinRise = settings.BasinRise;
        _deepLow = settings.DeepLow;
        _deepHigh = settings.DeepHigh;
        _shelfWidth = settings.ShelfWidth;
        _deepWidth = settings.DeepWidth;
        _jaggedWarp = settings.JaggedWarp;
        _roundedWarp = settings.RoundedWarp;
        _towerWarp = settings.TowerWarp;
        _towerLow = settings.TowerPeakLow;
        _towerHigh = settings.TowerPeakHigh;
        _peakLow = settings.PeakLow;
        _peakHigh = settings.PeakHigh;
        _hillWarp = settings.HillWarp;
        _archRadius = settings.ArchRadius;
        _lip = settings.CaprockLip;
        SeaLevel = baseRadius + settings.SeaLevel;

        int oceansLow = Math.Min(settings.OceansLow, settings.OceansHigh);
        int oceans = oceansLow + (int)(Noise.Random(Noise.Derive(seed, 101), 0)
            * (Math.Max(settings.OceansLow, settings.OceansHigh) - oceansLow + 1));

        Regions = new RegionMap(Noise.Derive(seed, 100), settings.RegionCount,
            settings.FlatShare, settings.HillsShare, settings.BasinShare, settings.PeakLow, settings.PeakHigh, oceans);

        double warp = Math.Max(Math.Max(_jaggedWarp, _roundedWarp), Math.Max(_towerWarp, _hillWarp));
        double highest = Math.Max(Math.Max(settings.PeakHigh, settings.TowerPeakHigh), _hillMiddle + _hillSwing);
        double lowest = Math.Max(_hollowDepth + Math.Max(0, _hillSwing - _hillMiddle) + _flatRoll,
            Math.Max(_basinHigh, _deepHigh) + _flatRoll);
        double top = highest + warp + _lip + 16;
        double bottom = -(lowest + warp + settings.SandDepth + 24);

        _flatSeed = Noise.Derive(seed, 1);
        _hollowSeed = Noise.Derive(seed, 2);
        _hillSeed = Noise.Derive(seed, 3);
        _ridgeSeed = Noise.Derive(seed, 4);
        _roundSeed = Noise.Derive(seed, 5);
        _mesaSeed = Noise.Derive(seed, 6);
        _hornSeed = Noise.Derive(seed, 7);
        _massifSeed = Noise.Derive(seed, 8);
        _warpSeed = Noise.Derive(seed, 9);
        _borderSeed = Noise.Derive(seed, 10);
        _archSeed = Noise.Derive(seed, 11);
        _archGateSeed = Noise.Derive(seed, 12);

        // The islands first: canyons keep clear of them.
        Islands = new SkyIslands(Noise.Derive(seed, 200), baseRadius, Regions,
            u => Regional(u.X * baseRadius, u.Y * baseRadius, u.Z * baseRadius), settings);

        Canyons = new Canyons(Noise.Derive(seed, 300), baseRadius, Regions, RegionAt,
            (ux, uy, uz) => Islands.ZoneAt(ux, uy, uz, out double inside) >= 0 && inside > -0.4,
            settings);

        if (Islands.Zones.Count > 0)
        {
            top = Math.Max(top, Islands.Highest + 10);
            bottom = Math.Min(bottom, Islands.Lowest - settings.SandDepth - 24);
        }

        Top = baseRadius + top;
        Bottom = baseRadius + bottom;

        _bare = new Bare(this);
        _nearIslands = new NearIslands(this);
    }

    /// <summary>The canyons cut into the planet.</summary>
    public Canyons Canyons { get; }

    /// <summary>The zones where the ground has shattered into sky islands.</summary>
    public SkyIslands Islands { get; }

    public ulong Seed { get; }

    public RegionMap Regions { get; }

    public double BaseRadius { get; }

    /// <summary>Distance from the centre to sea level: basins dip below it, ordinary ground does not.</summary>
    public double SeaLevel { get; }

    public double Top { get; }

    public double Bottom { get; }

    /// <summary>
    /// Measured, with room to spare: mesa walls and warped crags are the
    /// steepest (see TerrainTests.SlopeBoundHolds). Near sky islands it holds
    /// only for the terrain inside a box (<see cref="Within"/>), which is what
    /// anything relying on it asks for.
    /// </summary>
    public double Slope => 8;

    private readonly double _blend, _wander, _rise, _flatRoll, _hillMiddle, _hillSwing, _hollowDepth;
    private readonly double _basinLow, _basinHigh, _basinRise, _deepLow, _deepHigh, _shelfWidth, _deepWidth;
    private readonly double _jaggedWarp, _roundedWarp, _towerWarp, _hillWarp, _archRadius, _lip;
    private readonly double _towerLow, _towerHigh, _peakLow, _peakHigh;

    private readonly ulong _flatSeed, _hollowSeed, _hillSeed, _ridgeSeed, _roundSeed, _mesaSeed;
    private readonly ulong _hornSeed, _massifSeed, _warpSeed, _borderSeed, _archSeed, _archGateSeed;

    // Feature sizes, in world units.
    private const double FlatScale = 160;
    private const double HollowScale = 1100;
    private const double HillScale = 280;
    private const double RidgeScale = 520;
    private const double RoundScale = 700;
    private const double MesaScale = 650;
    private const double HornCell = 400;

    /// <summary>How far from the sphere a horn cell's point may lie and still hold one.</summary>
    private const double HornBand = 100;

    /// <summary>The narrowest a horn's foot is, in radius, and how much wider the widest.</summary>
    private const double HornFoot = 160;
    private const double HornFootSpread = 80;

    /// <summary>How far a horn's ridges and gullies stretch or shrink its flanks, as a share.</summary>
    private const double HornCrags = 0.35;

    /// <summary>How far apart the gullies down a horn's faces are.</summary>
    private const double HornGullies = 45;
    private const double WarpScale = 90;
    private const double BorderScale = 1600;
    private const double ArchScale = 120;
    private const double ArchGateScale = 1600;
    private const int MesaSteps = 4;


    public double Distance(double x, double y, double z)
    {
        double r = Math.Sqrt(x * x + y * y + z * z);
        if (r < 1e-6)
            return BaseRadius;

        double distance = Regional(x, y, z);
        double ux = x / r, uy = y / r, uz = z / r, altitude = r - BaseRadius;

        // 5. Canyons.
        distance = Canyons.Carve(distance, ux, uy, uz, altitude);

        // 6. Sky islands.
        return Islands.Apply(distance, x, y, z, ux, uy, uz, altitude);
    }

    /// <summary>
    /// The terrain inside a box: without the sky islands where none can reach
    /// it, and with them -- the distance kept from dropping below
    /// -<see cref="SkyIslands.Plateau"/> anywhere -- where any can. Either way
    /// the slope bound holds inside the box: the distance changes smoothly
    /// throughout it.
    /// </summary>
    public ITerrainShape Within(Vector3 min, Vector3 max) =>
        Islands.Reaching(min, max) ? _nearIslands : _bare;

    private readonly Bare _bare;
    private readonly NearIslands _nearIslands;

    /// <summary>Boxes over a sky-island zone, at the islands' heights (see <see cref="SkyIslands.Touches"/>).</summary>
    public bool Intricate(Vector3 min, Vector3 max) => Islands.Touches(min, max);

    /// <summary>The terrain with the islands left out: exact wherever none can reach.</summary>
    private sealed class Bare : ITerrainShape
    {
        private readonly PlanetTerrain _terrain;

        public Bare(PlanetTerrain terrain) => _terrain = terrain;

        public double BaseRadius => _terrain.BaseRadius;
        public double Top => _terrain.Top;
        public double Bottom => _terrain.Bottom;
        public double Slope => _terrain.Slope;

        public double Distance(double x, double y, double z)
        {
            double r = Math.Sqrt(x * x + y * y + z * z);
            if (r < 1e-6)
                return _terrain.BaseRadius;

            double ux = x / r, uy = y / r, uz = z / r, altitude = r - _terrain.BaseRadius;
            double distance = _terrain.Canyons.Carve(_terrain.Regional(x, y, z), ux, uy, uz, altitude);
            return _terrain.Islands.Apply(distance, x, y, z, ux, uy, uz, altitude, islands: false);
        }

        public TerrainInfo Describe(Vector3 point) => _terrain.Describe(point);

        public ITerrainShape Within(Vector3 min, Vector3 max) => this;
    }

    /// <summary>The terrain in a box the islands reach: with them, and levelled off below the plateau.</summary>
    private sealed class NearIslands : ITerrainShape
    {
        private readonly PlanetTerrain _terrain;

        public NearIslands(PlanetTerrain terrain) => _terrain = terrain;

        public double BaseRadius => _terrain.BaseRadius;
        public double Top => _terrain.Top;
        public double Bottom => _terrain.Bottom;
        public double Slope => _terrain.Slope;

        public double Distance(double x, double y, double z)
        {
            double r = Math.Sqrt(x * x + y * y + z * z);
            if (r < 1e-6)
                return _terrain.BaseRadius;

            double ux = x / r, uy = y / r, uz = z / r, altitude = r - _terrain.BaseRadius;
            double distance = _terrain.Canyons.Carve(_terrain.Regional(x, y, z), ux, uy, uz, altitude);
            return _terrain.Islands.Apply(distance, x, y, z, ux, uy, uz, altitude, plateau: true);
        }

        public TerrainInfo Describe(Vector3 point) => _terrain.Describe(point);

        public ITerrainShape Within(Vector3 min, Vector3 max) => this;
    }

    /// <summary>Steps 1 to 4: the regions' own ground, before canyons and sky islands.</summary>
    private double Regional(double x, double y, double z)
    {
        double r = Math.Sqrt(x * x + y * y + z * z);
        if (r < 1e-6)
            return BaseRadius;

        // 1. Regions, at the wandered direction.
        Span<int> ids = stackalloc int[RegionMap.MaxBlend];
        Span<double> weights = stackalloc double[RegionMap.MaxBlend];
        Span<double> edges = stackalloc double[RegionMap.MaxBlend];

        double ux = x / r, uy = y / r, uz = z / r;
        Wander(ux, uy, uz, out double wx, out double wy, out double wz);
        int count = Regions.Lookup(wx, wy, wz, BaseRadius, _blend, ids, weights, edges);

        // 2. Warp, as strong as the regions here ask.
        double warp = 0, arches = 0;
        for (int n = 0; n < count; n++)
        {
            int id = ids[n];
            warp += weights[n] * WarpOf(id);

            if (Regions.Types[id] == RegionType.Mountains)
                arches += weights[n] * Mask(edges[n]);
        }

        double px = x, py = y, pz = z;
        if (warp > 0.01)
        {
            const double s = 1 / WarpScale;
            px += warp * Noise.Fbm(_warpSeed, x * s, y * s, z * s, 2);
            py += warp * Noise.Fbm(_warpSeed + 1, x * s, y * s, z * s, 2);
            pz += warp * Noise.Fbm(_warpSeed + 2, x * s, y * s, z * s, 2);
        }

        double pr = Math.Sqrt(px * px + py * py + pz * pz);
        double altitude = pr - BaseRadius;
        double sx = px / pr * BaseRadius, sy = py / pr * BaseRadius, sz = pz / pr * BaseRadius;

        // 3. Each region's own distance, blended.
        double hollow = double.NaN;
        double distance = 0;

        for (int n = 0; n < count; n++)
        {
            int id = ids[n];
            double own;

            switch (Regions.Types[id])
            {
                case RegionType.Flat:
                    hollow = double.IsNaN(hollow) ? Hollow(sx, sy, sz) : hollow;
                    own = FlatHeight(sx, sy, sz) - _hollowDepth * hollow - altitude;
                    break;

                case RegionType.Hills:
                    hollow = double.IsNaN(hollow) ? Hollow(sx, sy, sz) : hollow;
                    own = HillHeight(sx, sy, sz) * (1 - 0.6 * hollow) - _hollowDepth * hollow - altitude;
                    break;

                case RegionType.Basin:
                    own = OceanFloor(id, edges[n]) + FlatHeight(sx, sy, sz) - altitude;
                    break;

                default:
                    own = Mountain(id, Mask(edges[n]), sx, sy, sz, altitude);
                    break;
            }

            distance += weights[n] * own;
        }

        // 4. Arches through thin rock.
        if (arches > 0.01 && _archRadius > 0 && altitude > 10)
            distance = Arch(distance, arches, px, py, pz, sx, sy, sz, altitude);

        return distance;
    }

    /// <summary>What the terrain is like at a point: the regions there and the heights.</summary>
    public TerrainInfo Describe(Vector3 point)
    {
        double r = point.Length();
        double ux = point.X / r, uy = point.Y / r, uz = point.Z / r;

        Span<int> ids = stackalloc int[RegionMap.MaxBlend];
        Span<double> weights = stackalloc double[RegionMap.MaxBlend];
        Span<double> edges = stackalloc double[RegionMap.MaxBlend];

        Wander(ux, uy, uz, out double wx, out double wy, out double wz);
        int count = Regions.Lookup(wx, wy, wz, BaseRadius, _blend, ids, weights, edges);

        float flat = 0, hills = 0, mountains = 0, basins = 0;
        for (int n = 0; n < count; n++)
        {
            switch (Regions.Types[ids[n]])
            {
                case RegionType.Flat: flat += (float)weights[n]; break;
                case RegionType.Hills: hills += (float)weights[n]; break;
                case RegionType.Basin: basins += (float)weights[n]; break;
                default: mountains += (float)weights[n]; break;
            }
        }

        int nearest = ids[0];
        double surface = TerrainShapes.SurfaceRadius(this, new Vector3((float)ux, (float)uy, (float)uz));

        // Over a sky-island zone, or between a canyon's rims, that is what the
        // ground is, whatever region lies under it.
        float islands = Islands.ZoneAt(ux, uy, uz, out double inside) >= 0 ? (float)Math.Clamp(inside * 10 + 0.5, 0, 1) : 0f;
        RegionType type = islands >= 0.5f ? RegionType.SkyIslands
            : Canyons.Inside(ux, uy, uz) ? RegionType.Canyon
            : Regions.Types[nearest];

        return new TerrainInfo(nearest, type, Regions.Characters[nearest],
            flat, hills, mountains, basins, islands, (float)(r - SeaLevel), (float)(surface - BaseRadius));
    }

    // ---------------------------------------------------------- the regions

    /// <summary>The region a direction lies in, borders wandered as the terrain has them.</summary>
    public int RegionAt(double ux, double uy, double uz)
    {
        Wander(ux, uy, uz, out double wx, out double wy, out double wz);
        return Regions.Nearest(wx, wy, wz);
    }

    private void Wander(double ux, double uy, double uz, out double wx, out double wy, out double wz)
    {
        if (_wander <= 0)
        {
            wx = ux; wy = uy; wz = uz;
            return;
        }

        double s = BaseRadius / BorderScale;
        double amount = _wander / BaseRadius;

        wx = ux + amount * Noise.Fbm(_borderSeed, ux * s, uy * s, uz * s, 2);
        wy = uy + amount * Noise.Fbm(_borderSeed + 1, ux * s, uy * s, uz * s, 2);
        wz = uz + amount * Noise.Fbm(_borderSeed + 2, ux * s, uy * s, uz * s, 2);

        double length = Math.Sqrt(wx * wx + wy * wy + wz * wz);
        wx /= length; wy /= length; wz /= length;
    }

    private double WarpOf(int id) => Regions.Types[id] switch
    {
        RegionType.Flat or RegionType.Basin => 0,
        RegionType.Hills => _hillWarp,
        _ => Regions.Characters[id] switch
        {
            MountainCharacter.Jagged => _jaggedWarp,
            MountainCharacter.Rounded => _roundedWarp,
            MountainCharacter.Towering => _towerWarp,
            _ => 0,
        },
    };

    /// <summary>How far into its full height a mountain range is, from how far inside it a spot lies.</summary>
    private double Mask(double edge) => SmoothStep(0, _rise, edge);

    // ------------------------------------------------------------ lowlands

    private double FlatHeight(double x, double y, double z)
    {
        const double s = 1 / FlatScale;
        return _flatRoll * Noise.Fbm(_flatSeed, x * s, y * s, z * s, 2);
    }

    /// <summary>0 on ordinary ground, 1 at the bottom of a hollow: the small dips in flats and hills.</summary>
    private double Hollow(double x, double y, double z)
    {
        const double s = 1 / HollowScale;
        return SmoothStep(0.18, 0.45, Noise.Fbm(_hollowSeed, x * s, y * s, z * s, 3));
    }

    /// <summary>
    /// An ocean's floor under a basin region, against the base radius, at a
    /// distance from shore: a SHELF sloping gently down to its depth over the
    /// first stretch out from shore, then, past the shelf's width, a fall to
    /// the DEEP floor further out. Each region draws its own shelf and deep
    /// depths (from its size draw), and neighbouring regions blend, so the
    /// floor of one ocean varies. An ocean is many basin regions together:
    /// the distance from shore is to the nearest region that is not basin.
    /// </summary>
    private double OceanFloor(int id, double fromShore)
    {
        double t = _peakHigh > _peakLow ? Math.Clamp((Regions.Peaks[id] - _peakLow) / (_peakHigh - _peakLow), 0, 1) : 0.5;
        double shelf = _basinLow + (_basinHigh - _basinLow) * t;
        double deep = _deepLow + (_deepHigh - _deepLow) * (1 - t);

        return -shelf * SmoothStep(0, _basinRise, fromShore)
            - Math.Max(0, deep - shelf) * SmoothStep(_shelfWidth, _deepWidth, fromShore);
    }

    private double HillHeight(double x, double y, double z)
    {
        const double s = 1 / HillScale;
        return _hillMiddle + _hillSwing * Noise.Fbm(_hillSeed, x * s, y * s, z * s, 4);
    }

    // ----------------------------------------------------------- mountains

    /// <summary>One mountain region's distance at a warped point.</summary>
    private double Mountain(int id, double mask, double x, double y, double z, double altitude)
    {
        double peak = Regions.Peaks[id] * mask;

        switch (Regions.Characters[id])
        {
            case MountainCharacter.Jagged:
            {
                const double s = 1 / RidgeScale;
                double ridge = Math.Clamp(Noise.Ridged(_ridgeSeed, x * s, y * s, z * s, 5) / 0.7, 0, 1);
                return peak * (0.05 + 0.95 * Math.Pow(ridge, 1.5)) - altitude;
            }

            case MountainCharacter.Rounded:
            {
                const double s = 1 / RoundScale;
                double dome = Math.Clamp(0.5 + 0.9 * Noise.Fbm(_roundSeed, x * s, y * s, z * s, 4), 0, 1);
                return peak * Math.Pow(SmoothStep(0, 1, dome), 1.3) - altitude;
            }

            case MountainCharacter.Towering:
                return Towering(TowerPeak(Regions.Peaks[id]) * mask, x, y, z, altitude);

            default:
                return Mesa(Regions.Peaks[id], mask, x, y, z, altitude);
        }
    }

    /// <summary>
    /// Stepped plateaus: a smooth noise height pushed onto a staircase, with
    /// short steep risers between the treads. Each tread's rim carries a slab
    /// of caprock that hangs out over the riser below it.
    /// </summary>
    private double Mesa(double fullPeak, double mask, double x, double y, double z, double altitude)
    {
        const double s = 1 / MesaScale;
        double field = Math.Clamp(0.45 + Noise.Fbm(_mesaSeed, x * s, y * s, z * s, 4), 0, 1) * mask;

        double body = fullPeak * Terrace(field) - altitude;
        if (_lip <= 0)
            return body;

        // The caprock: a slab under each tread, reaching past the tread's
        // downhill edge by the lip. Measured in "steps" -- the staircase's own
        // coordinate -- and turned into world units by how fast it typically
        // changes. Every tread is tried, not just the nearest: picking one
        // would jump from slab to slab.
        const double Thickness = 5;
        double stepped = field * MesaSteps;
        double perUnit = MesaSteps * 1.8 / MesaScale;
        double lip = _lip * perUnit;
        double distance = body;

        for (int tread = 1; tread <= MesaSteps; tread++)
        {
            double top = fullPeak * tread / MesaSteps;
            double across = Math.Min(stepped - (tread - 0.38 - lip), tread + 0.38 - stepped) / perUnit;
            double upDown = Math.Min(top - altitude, altitude - (top - Thickness));
            distance = Math.Max(distance, Math.Min(across, upDown));
        }

        return distance;
    }

    /// <summary>A staircase from 0 to 1: flat treads, short smooth risers.</summary>
    private static double Terrace(double x)
    {
        double stepped = x * MesaSteps;
        double step = Math.Floor(stepped);
        double along = stepped - step;
        return Math.Min(1, (step + SmoothStep(0.38, 0.62, along)) / MesaSteps);
    }

    /// <summary>A towering range's horn height for a region, from its drawn peak.</summary>
    private double TowerPeak(double peak)
    {
        double t = _peakHigh > _peakLow ? (peak - _peakLow) / (_peakHigh - _peakLow) : 0.5;
        return _towerLow + (_towerHigh - _towerLow) * Math.Clamp(t, 0, 1);
    }

    /// <summary>
    /// Extra-tall jagged peaks: a craggy ridged massif, and standing out of it
    /// a few HORNS -- steep, sharp-topped peaks far taller than anything
    /// around them, like the great summits of a real range. Each horn is a
    /// cone, cut into ridges and gullies by a noise that runs round it (so its
    /// flanks are star-shaped from above), and the warp breaks its faces into
    /// crags. Horns are dealt one per cell of a coarse lattice, most cells
    /// holding one.
    /// </summary>
    private double Towering(double peak, double x, double y, double z, double altitude)
    {
        const double m = 1 / 420.0;
        double ridge = Math.Clamp(Noise.Ridged(_massifSeed, x * m, y * m, z * m, 5) / 0.7, 0, 1);
        double height = peak * 0.45 * (0.1 + 0.9 * Math.Pow(ridge, 1.3));

        if (peak < 1)
            return height - altitude;

        long cx = (long)Math.Floor(x / HornCell);
        long cy = (long)Math.Floor(y / HornCell);
        long cz = (long)Math.Floor(z / HornCell);

        for (long i = cx - 1; i <= cx + 1; i++)
        for (long j = cy - 1; j <= cy + 1; j++)
        for (long k = cz - 1; k <= cz + 1; k++)
        {
            if (Noise.Random(_hornSeed, i, j, k) > 0.7)
                continue;

            ulong own = Noise.Derive(_hornSeed, (ulong)(i * 73856093L ^ j * 19349663L ^ k * 83492791L));

            // The horn's summit, over a point of the cell near the sphere.
            // Only cells whose point lies near the sphere hold one: any other
            // cell's point would be pulled onto it from further than the
            // cells searched around a point.
            double fx = (i + 0.15 + 0.7 * Noise.Random(own, 0)) * HornCell;
            double fy = (j + 0.15 + 0.7 * Noise.Random(own, 1)) * HornCell;
            double fz = (k + 0.15 + 0.7 * Noise.Random(own, 2)) * HornCell;
            double fr = Math.Sqrt(fx * fx + fy * fy + fz * fz);

            if (Math.Abs(fr - BaseRadius) > HornBand)
                continue;

            fx *= BaseRadius / fr; fy *= BaseRadius / fr; fz *= BaseRadius / fr;

            double dx = x - fx, dy = y - fy, dz = z - fz;
            double across = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double foot = HornFoot + HornFootSpread * Noise.Random(own, 4);

            // Out of reach even with its widest ridge: skip the noise.
            if (across * (1 - HornCrags) >= foot)
                continue;

            // Ridges and gullies: the distance out stretched or shrunk by a
            // noise that depends only on which way round the horn a point
            // lies. Scaled by the distance itself, so it fades to nothing at
            // the summit and the summit stays a point.
            double around = 0;
            if (across > 1e-6)
            {
                double w = 3.5 / across;
                around = Noise.Gradient(own, dx * w + 11.3, dy * w + 7.1, dz * w + 3.7);
            }

            double stretched = across * (1 + HornCrags * around);
            double horn = peak * (0.65 + 0.35 * Noise.Random(own, 3)) * Math.Max(0, 1 - stretched / foot);

            // Gullies scored down the faces, deepest where the horn is tallest.
            if (horn > height)
            {
                const double g = 1 / HornGullies;
                horn *= 1 - 0.12 * Noise.Ridged(own + 5, x * g, y * g, z * g, 3);
            }

            height = Math.Max(height, horn);
        }

        return height - altitude;
    }

    // -------------------------------------------------------------- arches

    /// <summary>
    /// Carves tubes out of rock where two noise fields both cross zero -- a
    /// line, winding through space. The carve takes away twice what it cuts
    /// into, so it breaks through rock thinner than the tube's diameter and
    /// only dents anything thicker: fins become arches, massifs stay whole.
    /// </summary>
    private double Arch(double distance, double amount, double px, double py, double pz,
        double sx, double sy, double sz, double altitude)
    {
        const double g = 1 / ArchGateScale;
        double gate = SmoothStep(0.1, 0.35, Noise.Fbm(_archGateSeed, sx * g, sy * g, sz * g, 2));
        double radius = _archRadius * gate * amount * SmoothStep(10, 35, altitude);

        if (radius < 0.05)
            return distance;

        const double s = 1 / ArchScale;
        double a = Noise.Gradient(_archSeed, px * s, py * s, pz * s);
        double b = Noise.Gradient(_archSeed + 1, px * s, py * s, pz * s);

        // Gradient noise changes by about 1.3 per feature: this turns the two
        // values into an estimate of the distance to the line.
        double across = Math.Sqrt(a * a + b * b) * ArchScale / 1.3;

        return distance - 2 * Math.Max(0, radius - across);
    }

    private static double SmoothStep(double from, double to, double x)
    {
        double t = Math.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
