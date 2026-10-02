using System;

namespace GameBase.Terrain;

/// <summary>
/// The planet split into regions, each flat, hills, mountains or a basin.
///
/// Every region is owned by one point on the unit sphere, and a spot belongs to
/// the region whose point is nearest. The points start on a Fibonacci sphere --
/// as evenly spread as points on a sphere get, so regions come out about the
/// same size -- then are jittered and the whole set turned at random by the
/// seed, so no two worlds share a layout.
///
/// Types are dealt from a deck holding exactly the planned share of each, not
/// rolled one by one: 100 rolls at 40% land anywhere from 30 to 50 flat
/// regions, a deck lands on 40 every time (see <see cref="DealTypes"/>).
/// </summary>
public sealed class RegionMap
{
    public RegionMap(ulong seed, int count, float flatShare, float hillsShare, float basinShare,
        double peakLow, double peakHigh, int oceans = 3)
    {
        Count = Math.Max(4, count);
        X = new double[Count];
        Y = new double[Count];
        Z = new double[Count];
        Types = new RegionType[Count];
        Characters = new MountainCharacter[Count];
        Peaks = new double[Count];

        PlacePoints(Noise.Derive(seed, 1));
        DealTypes(Noise.Derive(seed, 2), flatShare, hillsShare, basinShare, oceans);

        ulong peaks = Noise.Derive(seed, 3);
        for (int i = 0; i < Count; i++)
            Peaks[i] = peakLow + (peakHigh - peakLow) * Noise.Random(peaks, i);
    }

    public int Count { get; }

    /// <summary>Each region's point, a unit vector, split by axis.</summary>
    public double[] X { get; }
    public double[] Y { get; }
    public double[] Z { get; }

    public RegionType[] Types { get; }

    /// <summary>Each mountain region's character; meaningless for the rest.</summary>
    public MountainCharacter[] Characters { get; }

    /// <summary>
    /// Each region's size draw, between the peak heights: a mountain's peak,
    /// and rescaled, a towering range's horns and a basin's depth.
    /// </summary>
    public double[] Peaks { get; }

    /// <summary>The most regions a spot can blend between.</summary>
    public const int MaxBlend = 6;

    /// <summary>
    /// The regions a direction blends between, nearest first.
    ///
    /// Each region within <paramref name="blend"/> units (along the surface of
    /// a sphere of <paramref name="radius"/>) of being the nearest gets a
    /// weight, falling from the nearest to nothing at the blend's edge, and
    /// the weights sum to one. Each also gets its EDGE: for a mountain region,
    /// how far inside the range the spot is -- its distance to the nearest
    /// region that is not mountains, negative outside -- and for a basin, how
    /// far inside the basin, from the nearest region that is not a basin.
    /// Adjacent ranges run on into each other rather than dipping to the
    /// plains between them, and adjacent basins into one wider hollow.
    /// </summary>
    /// <returns>How many regions were written.</returns>
    public int Lookup(double ux, double uy, double uz, double radius, double blend,
        Span<int> ids, Span<double> weights, Span<double> edges)
    {
        Span<double> dots = stackalloc double[Count];

        int nearest = 0;
        double best = double.NegativeInfinity;
        double bestLowland = double.NegativeInfinity;
        double bestDry = double.NegativeInfinity;

        for (int i = 0; i < Count; i++)
        {
            double dot = X[i] * ux + Y[i] * uy + Z[i] * uz;
            dots[i] = dot;

            if (dot > best)
            {
                best = dot;
                nearest = i;
            }

            if (Types[i] != RegionType.Mountains && dot > bestLowland)
                bestLowland = dot;

            if (Types[i] != RegionType.Basin && dot > bestDry)
                bestDry = dot;
        }

        double nearestAngle = Angle(best);
        double lowlandAngle = bestLowland == double.NegativeInfinity ? Math.PI : Angle(bestLowland);
        double dryAngle = bestDry == double.NegativeInfinity ? Math.PI : Angle(bestDry);
        double reach = nearestAngle + blend / radius;
        double reachDot = Math.Cos(Math.Min(Math.PI, reach));

        // The nearest first, with full weight, then the rest in the blend.
        ids[0] = nearest;
        weights[0] = 1;
        edges[0] = Edge(nearest, nearestAngle, lowlandAngle, dryAngle, radius);

        int written = 1;
        double total = 1;

        for (int i = 0; i < Count && written < MaxBlend; i++)
        {
            if (i == nearest || dots[i] < reachDot)
                continue;

            double angle = Angle(dots[i]);
            double t = 1 - (angle - nearestAngle) * radius / blend;
            if (t <= 0)
                continue;

            double weight = t * t;

            ids[written] = i;
            weights[written] = weight;
            edges[written] = Edge(i, angle, lowlandAngle, dryAngle, radius);
            total += weight;
            written++;
        }

        for (int n = 0; n < written; n++)
            weights[n] /= total;

        return written;
    }

    /// <summary>How far inside its own kind of country a region's spot is (see <see cref="Lookup"/>).</summary>
    private double Edge(int id, double angle, double lowlandAngle, double dryAngle, double radius) => Types[id] switch
    {
        RegionType.Mountains => (lowlandAngle - angle) * radius * 0.5,
        RegionType.Basin => (dryAngle - angle) * radius * 0.5,
        _ => 0,
    };

    /// <summary>The nearest region to a direction.</summary>
    public int Nearest(double ux, double uy, double uz)
    {
        int nearest = 0;
        double best = double.NegativeInfinity;

        for (int i = 0; i < Count; i++)
        {
            double dot = X[i] * ux + Y[i] * uy + Z[i] * uz;
            if (dot > best)
            {
                best = dot;
                nearest = i;
            }
        }

        return nearest;
    }

    private static double Angle(double dot) => Math.Acos(Math.Clamp(dot, -1, 1));

    private void PlacePoints(ulong seed)
    {
        // A random turn of the whole sphere: three random axes, straightened.
        (double x, double y, double z) a = Unit(seed, 0), b = Unit(seed, 1);
        (double x, double y, double z) c = Cross(a, b);
        c = Normalise(c);
        b = Normalise(Cross(c, a));

        double golden = Math.PI * (3 - Math.Sqrt(5));
        double spacing = Math.Sqrt(4 * Math.PI / Count);

        for (int i = 0; i < Count; i++)
        {
            double y = 1 - 2 * (i + 0.5) / Count;
            double ring = Math.Sqrt(Math.Max(0, 1 - y * y));
            double theta = golden * i;
            double x = Math.Cos(theta) * ring;
            double z = Math.Sin(theta) * ring;

            // Jitter by up to about a third of the spacing, so the regions are
            // not a visible pattern.
            x += (Noise.Random(seed, i, 1, 0) - 0.5) * spacing * 0.7;
            y += (Noise.Random(seed, i, 2, 0) - 0.5) * spacing * 0.7;
            z += (Noise.Random(seed, i, 3, 0) - 0.5) * spacing * 0.7;

            (double px, double py, double pz) = Normalise((x, y, z));

            X[i] = a.x * px + b.x * py + c.x * pz;
            Y[i] = a.y * px + b.y * py + c.y * pz;
            Z[i] = a.z * px + b.z * py + c.z * pz;
        }
    }

    /// <summary>
    /// Deals each region its type. The deck holds exactly the planned count of
    /// each.
    ///
    /// Basins come first, gathered into a few OCEANS: each grows out from a
    /// seed region -- the lowest on a large ridged noise, so as far from the
    /// ranges as possible, and the seeds well apart -- one neighbouring region
    /// at a time, taking whichever lies nearest its seed with a little chance
    /// mixed in, so an ocean is one connected body with a ragged coast. The
    /// oceans grow in turn, so they come out about the same size.
    ///
    /// The rest follow the same ridged noise, with a good share of chance:
    /// mountains take the regions highest on it -- the ridges, so ranges run in
    /// chains -- hills the next, which puts foothills round most ranges, and
    /// plains the rest.
    /// </summary>
    private void DealTypes(ulong seed, float flatShare, float hillsShare, float basinShare, int oceans)
    {
        int flats = (int)Math.Round(Count * Math.Clamp(flatShare, 0, 1));
        int hills = Math.Min(Count - flats, (int)Math.Round(Count * Math.Clamp(hillsShare, 0, 1)));
        int basins = Math.Min(Count - flats - hills, (int)Math.Round(Count * Math.Clamp(basinShare, 0, 1)));
        int mountains = Count - flats - hills - basins;

        var score = new double[Count];
        ulong ridges = Noise.Derive(seed, 11);

        for (int i = 0; i < Count; i++)
        {
            score[i] = Noise.Ridged(ridges, X[i] * 1.6, Y[i] * 1.6, Z[i] * 1.6, 2)
                + 0.45 * Noise.Random(seed, i);
        }

        var ocean = new int[Count];
        Array.Fill(ocean, -1);
        GrowOceans(seed, score, basins, Math.Max(1, oceans), ocean);
        Ocean = ocean;

        var land = new System.Collections.Generic.List<int>();
        for (int i = 0; i < Count; i++)
        {
            if (ocean[i] >= 0)
                Types[i] = RegionType.Basin;
            else
                land.Add(i);
        }

        land.Sort((a, b) => score[b].CompareTo(score[a]));

        for (int n = 0; n < land.Count; n++)
        {
            Types[land[n]] = n < mountains ? RegionType.Mountains
                : n < mountains + hills ? RegionType.Hills
                : RegionType.Flat;
        }

        // Characters dealt the same way, so every world has some of each.
        var deck = new MountainCharacter[Count];
        for (int i = 0; i < Count; i++)
            deck[i] = (MountainCharacter)(i % 4);

        Shuffle(deck, Noise.Derive(seed, 7));

        int next = 0;
        for (int i = 0; i < Count; i++)
            Characters[i] = Types[i] == RegionType.Mountains ? deck[next++] : MountainCharacter.Jagged;
    }

    /// <summary>Which ocean each region belongs to; -1 for land.</summary>
    public int[] Ocean { get; private set; }

    /// <summary>How many oceans there are.</summary>
    public int Oceans { get; private set; }

    /// <summary>
    /// Grows the oceans (see <see cref="DealTypes"/>): seeds low on the noise
    /// and far apart, then region by region to the count wanted.
    /// </summary>
    private void GrowOceans(ulong seed, double[] score, int total, int count, int[] ocean)
    {
        if (total <= 0)
            return;

        // Seeds: the lowest-scoring regions, each at least this far (radians)
        // from those already chosen -- relaxed if none is left that far.
        var seeds = new System.Collections.Generic.List<int>();
        var byScore = new int[Count];
        for (int i = 0; i < Count; i++)
            byScore[i] = i;
        Array.Sort(byScore, (a, b) => score[a].CompareTo(score[b]));

        for (double apart = 1.2; seeds.Count < count && apart > 0.05; apart *= 0.8)
        {
            foreach (int i in byScore)
            {
                if (seeds.Count >= count)
                    break;

                bool far = !seeds.Contains(i);
                foreach (int s in seeds)
                    far &= Angle(X[i] * X[s] + Y[i] * Y[s] + Z[i] * Z[s]) >= apart;

                if (far)
                    seeds.Add(i);
            }
        }

        Oceans = seeds.Count;

        // Neighbours: the few nearest region points to each.
        const int Near = 7;
        var neighbours = new int[Count][];
        for (int i = 0; i < Count; i++)
        {
            var others = new System.Collections.Generic.List<int>();
            for (int j = 0; j < Count; j++)
            {
                if (j != i)
                    others.Add(j);
            }

            int self = i;
            others.Sort((a, b) =>
                (X[b] * X[self] + Y[b] * Y[self] + Z[b] * Z[self])
                .CompareTo(X[a] * X[self] + Y[a] * Y[self] + Z[a] * Z[self]));
            neighbours[i] = others.GetRange(0, Math.Min(Near, others.Count)).ToArray();
        }

        int taken = 0;
        for (int o = 0; o < seeds.Count && taken < total; o++)
        {
            ocean[seeds[o]] = o;
            taken++;
        }

        // Grow in turn: each ocean takes the free neighbour of its own
        // regions that lies nearest its seed, with some chance mixed in.
        bool grew = true;
        while (taken < total && grew)
        {
            grew = false;

            for (int o = 0; o < seeds.Count && taken < total; o++)
            {
                int seedRegion = seeds[o];
                int best = -1;
                double bestScore = double.NegativeInfinity;

                for (int i = 0; i < Count; i++)
                {
                    if (ocean[i] != o)
                        continue;

                    foreach (int j in neighbours[i])
                    {
                        if (ocean[j] >= 0)
                            continue;

                        double closeness = X[j] * X[seedRegion] + Y[j] * Y[seedRegion] + Z[j] * Z[seedRegion];
                        double pick = closeness + 0.15 * Noise.Random(seed, j, o, 17);
                        if (pick > bestScore)
                        {
                            bestScore = pick;
                            best = j;
                        }
                    }
                }

                if (best < 0)
                    continue;

                ocean[best] = o;
                taken++;
                grew = true;
            }
        }
    }

    private static void Shuffle<T>(T[] items, ulong seed)
    {
        for (int i = items.Length - 1; i > 0; i--)
        {
            int j = (int)(Noise.Random(seed, i) * (i + 1));
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static (double x, double y, double z) Unit(ulong seed, int which)
    {
        // Rejection-free: normalising a Gaussian-ish triple would need more
        // draws; a cube sample is uniform enough for a random turn.
        double x = Noise.Random(seed, which, 10, 0) * 2 - 1;
        double y = Noise.Random(seed, which, 11, 0) * 2 - 1;
        double z = Noise.Random(seed, which, 12, 0) * 2 - 1;
        return Normalise((x + 1e-9, y, z));
    }

    private static (double x, double y, double z) Cross((double x, double y, double z) a, (double x, double y, double z) b) =>
        (a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

    private static (double x, double y, double z) Normalise((double x, double y, double z) v)
    {
        double length = Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        return length < 1e-12 ? (0, 1, 0) : (v.x / length, v.y / length, v.z / length);
    }
}
