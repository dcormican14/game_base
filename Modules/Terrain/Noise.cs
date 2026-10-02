using System;
using System.Runtime.CompilerServices;

namespace GameBase.Terrain;

/// <summary>
/// Seeded 3D noise, in doubles, with no state at all.
///
/// Why not Godot's FastNoiseLite: it is a Resource (an engine object, not
/// something to share between worker threads), it samples in floats (a planet's
/// surface is thousands of units out, where a float has lost the fine digits),
/// and its output is only as deterministic as the engine build. Every function
/// here is a pure function of its seed and coordinates, so chunks generated on
/// any thread, in any order, on any machine, agree.
///
/// Gradient noise is Perlin's improved noise with the permutation table
/// replaced by a hash of the lattice point and the seed: no table to build per
/// seed, and a seed changes every value.
/// </summary>
public static class Noise
{
    /// <summary>
    /// Gradient noise at a point, about -1..1 (rarely past ±0.9). Features are
    /// about one unit across: scale the point to set their size.
    /// </summary>
    public static double Gradient(ulong seed, double x, double y, double z)
    {
        double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
        long ix = (long)fx, iy = (long)fy, iz = (long)fz;
        double tx = x - fx, ty = y - fy, tz = z - fz;

        double u = Fade(tx), v = Fade(ty), w = Fade(tz);

        double n000 = Corner(seed, ix, iy, iz, tx, ty, tz);
        double n100 = Corner(seed, ix + 1, iy, iz, tx - 1, ty, tz);
        double n010 = Corner(seed, ix, iy + 1, iz, tx, ty - 1, tz);
        double n110 = Corner(seed, ix + 1, iy + 1, iz, tx - 1, ty - 1, tz);
        double n001 = Corner(seed, ix, iy, iz + 1, tx, ty, tz - 1);
        double n101 = Corner(seed, ix + 1, iy, iz + 1, tx - 1, ty, tz - 1);
        double n011 = Corner(seed, ix, iy + 1, iz + 1, tx, ty - 1, tz - 1);
        double n111 = Corner(seed, ix + 1, iy + 1, iz + 1, tx - 1, ty - 1, tz - 1);

        double x00 = n000 + u * (n100 - n000);
        double x10 = n010 + u * (n110 - n010);
        double x01 = n001 + u * (n101 - n001);
        double x11 = n011 + u * (n111 - n011);
        double y0 = x00 + v * (x10 - x00);
        double y1 = x01 + v * (x11 - x01);

        return y0 + w * (y1 - y0);
    }

    /// <summary>
    /// Fractal noise: <paramref name="octaves"/> layers of gradient noise, each
    /// twice as fine and half as strong as the last, normalised back to about
    /// -1..1.
    /// </summary>
    public static double Fbm(ulong seed, double x, double y, double z, int octaves)
    {
        double total = 0, weight = 0, amplitude = 1;

        for (int o = 0; o < octaves; o++)
        {
            total += Gradient(seed + (ulong)o * 0x9E37u, x, y, z) * amplitude;
            weight += amplitude;
            x *= 2.03; y *= 2.03; z *= 2.03;
            amplitude *= 0.5;
        }

        return total / weight;
    }

    /// <summary>
    /// Ridged fractal noise, 0..1: sharp crests where the noise crosses zero,
    /// each octave weighted by the one before so fine detail gathers on the
    /// ridges rather than filling the valleys. The classic mountain-chain
    /// noise.
    /// </summary>
    public static double Ridged(ulong seed, double x, double y, double z, int octaves)
    {
        double total = 0, weight = 0, amplitude = 1, previous = 1;

        for (int o = 0; o < octaves; o++)
        {
            double n = 1 - Math.Abs(Gradient(seed + (ulong)o * 0x7F4Bu, x, y, z));
            n *= n;
            n *= previous;
            previous = Math.Clamp(n * 1.6, 0, 1);

            total += n * amplitude;
            weight += amplitude;
            x *= 2.07; y *= 2.07; z *= 2.07;
            amplitude *= 0.5;
        }

        return total / weight;
    }

    /// <summary>A uniform random value 0..1 for an integer point and a seed.</summary>
    public static double Random(ulong seed, long x, long y, long z) =>
        (Hash(seed, x, y, z) >> 11) * (1.0 / (1UL << 53));

    /// <summary>A uniform random value 0..1 for an index and a seed.</summary>
    public static double Random(ulong seed, long index) => Random(seed, index, 0, 0);

    /// <summary>Mixes a seed with a label, so each use of the seed gets its own stream.</summary>
    public static ulong Derive(ulong seed, ulong label) => Mix(seed ^ Mix(label + 0x632BE59BD9B4E019UL));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    /// <summary>The dot product of a corner's hashed gradient with the offset from it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Corner(ulong seed, long ix, long iy, long iz, double x, double y, double z)
    {
        // Perlin's twelve edge directions of a cube, picked by the hash's top
        // four bits; the last four repeat four of them, as in Perlin's own
        // table, so no division is needed to pick one.
        switch ((int)(Hash(seed, ix, iy, iz) >> 60))
        {
            case 0: return x + y;
            case 1: return -x + y;
            case 2: return x - y;
            case 3: return -x - y;
            case 4: return x + z;
            case 5: return -x + z;
            case 6: return x - z;
            case 7: return -x - z;
            case 8: return y + z;
            case 9: return -y + z;
            case 10: return y - z;
            case 11: return -y - z;
            case 12: return x + y;
            case 13: return -x + y;
            case 14: return y - z;
            default: return -y - z;
        }
    }

    /// <summary>
    /// A 64-bit hash of a lattice point: the coordinates spread by large odd
    /// multipliers, then one strong mix. Cheap enough to run eight times a
    /// noise sample, and every bit of the result depends on every input.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Hash(ulong seed, long x, long y, long z)
    {
        unchecked
        {
            ulong h = seed
                + (ulong)x * 0x9E3779B97F4A7C15UL
                + (ulong)y * 0xC2B2AE3D27D4EB4FUL
                + (ulong)z * 0x165667B19E3779F9UL;
            return Mix(h);
        }
    }

    /// <summary>SplitMix64's finaliser: every input bit reaches every output bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong h)
    {
        unchecked
        {
            h ^= h >> 30;
            h *= 0xBF58476D1CE4E5B9UL;
            h ^= h >> 27;
            h *= 0x94D049BB133111EBUL;
            h ^= h >> 31;
            return h;
        }
    }
}
