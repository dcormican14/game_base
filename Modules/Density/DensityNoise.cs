using Godot;
using System.Runtime.CompilerServices;

namespace GameBase.Density;

/// <summary>
/// The noise primitives every density layer is built from.
///
/// All of it is a pure function of position and seed — no state, no
/// allocation, no lookup tables to initialise. That is what lets the field be
/// evaluated for any cell in any order on any thread, which is in turn what
/// makes an endless world possible: a chunk generated an hour from now, ten
/// thousand nodes away, computes exactly the rock it would have had if it were
/// generated first.
///
/// Gradient noise rather than value noise. Value noise (what
/// <see cref="GameBase.Nodes.RawNodeField"/> uses for its flow) has visible
/// axis-aligned structure at low frequencies, which reads as a grid in an
/// island's silhouette; gradient noise does not, and the silhouette is
/// exactly what these layers shape.
/// </summary>
public static class DensityNoise
{
    /// <summary>
    /// Gradient (Perlin-style) noise in roughly -1..1.
    ///
    /// The gradient at each lattice point is picked from the 12 edge-midpoint
    /// directions of a cube, hashed from the point's coordinates — the
    /// standard improved-Perlin set, which avoids the axis clumping a fully
    /// random direction produces.
    /// </summary>
    public static float Gradient(float x, float y, float z, int seed)
    {
        int x0 = Floor(x), y0 = Floor(y), z0 = Floor(z);
        float fx = x - x0, fy = y - y0, fz = z - z0;
        float u = Fade(fx), v = Fade(fy), w = Fade(fz);

        float n000 = Dot(x0, y0, z0, fx, fy, fz, seed);
        float n100 = Dot(x0 + 1, y0, z0, fx - 1f, fy, fz, seed);
        float n010 = Dot(x0, y0 + 1, z0, fx, fy - 1f, fz, seed);
        float n110 = Dot(x0 + 1, y0 + 1, z0, fx - 1f, fy - 1f, fz, seed);
        float n001 = Dot(x0, y0, z0 + 1, fx, fy, fz - 1f, seed);
        float n101 = Dot(x0 + 1, y0, z0 + 1, fx - 1f, fy, fz - 1f, seed);
        float n011 = Dot(x0, y0 + 1, z0 + 1, fx, fy - 1f, fz - 1f, seed);
        float n111 = Dot(x0 + 1, y0 + 1, z0 + 1, fx - 1f, fy - 1f, fz - 1f, seed);

        float a = Mathf.Lerp(Mathf.Lerp(n000, n100, u), Mathf.Lerp(n010, n110, u), v);
        float b = Mathf.Lerp(Mathf.Lerp(n001, n101, u), Mathf.Lerp(n011, n111, u), v);
        return Mathf.Lerp(a, b, w);
    }

    /// <summary>Gradient noise at a point.</summary>
    public static float Gradient(Vector3 p, int seed) => Gradient(p.X, p.Y, p.Z, seed);

    /// <summary>
    /// Fractal Brownian motion: octaves of gradient noise at doubling
    /// frequency and halving amplitude, normalised to roughly -1..1.
    ///
    /// This is the workhorse for anything that should look eroded — each
    /// octave adds detail an order of magnitude finer than the last, which is
    /// how real rock reads at every distance.
    /// </summary>
    /// <param name="octaves">How many layers of detail. Each one doubles the
    /// cost, and octaves finer than a node are wasted work.</param>
    /// <param name="persistence">How much each octave contributes relative to
    /// the one before. 0.5 is the neutral choice; higher is rougher.</param>
    /// <param name="lacunarity">Frequency step per octave. 2 is the neutral
    /// choice; values slightly off 2 avoid octaves reinforcing each other into
    /// visible repeats.</param>
    public static float Fbm(Vector3 p, int seed, int octaves,
        float persistence = 0.5f, float lacunarity = 2.03f)
    {
        float sum = 0f, amplitude = 1f, range = 0f;
        Vector3 at = p;

        for (int i = 0; i < octaves; i++)
        {
            sum += Gradient(at, seed + i * 1013) * amplitude;
            range += amplitude;
            amplitude *= persistence;

            // Each octave is offset as well as scaled, so the zero crossings
            // of different octaves do not stack at the origin.
            at = at * lacunarity + new Vector3(31.416f, -17.9f, 5.303f);
        }

        return range > 0f ? sum / range : 0f;
    }

    /// <summary>
    /// Ridged multifractal noise, in roughly 0..1 with sharp ridges at 1.
    ///
    /// Folding the noise about zero and inverting turns smooth hills into
    /// creases. This is what gives the island sides their spikes and spurs:
    /// fbm alone produces rounded lumps, which read as clay rather than rock.
    /// </summary>
    public static float Ridged(Vector3 p, int seed, int octaves,
        float persistence = 0.5f, float lacunarity = 2.03f)
    {
        float sum = 0f, amplitude = 1f, range = 0f;
        Vector3 at = p;

        for (int i = 0; i < octaves; i++)
        {
            // 1 - |noise| creases the field where the noise crosses zero;
            // squaring sharpens the crease into a ridge rather than a fold.
            float n = 1f - Mathf.Abs(Gradient(at, seed + i * 2027));
            sum += n * n * amplitude;
            range += amplitude;
            amplitude *= persistence;
            at = at * lacunarity + new Vector3(-11.7f, 23.1f, -3.8f);
        }

        return range > 0f ? sum / range : 0f;
    }

    /// <summary>Smootherstep — zero first and second derivative at both ends,
    /// so no creases show where noise cells meet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    /// <summary>
    /// Floor as an int, without the round trip through MathF.Floor.
    ///
    /// Called three times per gradient evaluation and the field runs dozens of
    /// those per sample, so this sits squarely in the innermost loop of world
    /// generation. The cast truncates toward zero, which is floor for
    /// non-negative values and one too high for negatives; the correction is a
    /// branchless compare.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Floor(float v)
    {
        int i = (int)v;
        return v < i ? i - 1 : i;
    }

    /// <summary>
    /// The gradient at a lattice point, dotted with the offset to the sample.
    /// Gradients come from the 12 cube-edge directions, selected by hash.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dot(int ix, int iy, int iz, float dx, float dy, float dz, int seed)
    {
        uint h = Hash(ix, iy, iz, seed) & 15u;

        // The classic improved-Perlin gradient table, expressed as arithmetic
        // rather than a lookup: the low bits pick which two axes contribute
        // and with which signs.
        float u = h < 8u ? dx : dy;
        float v = h < 4u ? dy : (h == 12u || h == 14u ? dx : dz);
        return ((h & 1u) == 0u ? u : -u) + ((h & 2u) == 0u ? v : -v);
    }

    /// <summary>Deterministic integer hash. Same coordinates and seed, same
    /// value, on every machine and every run.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(int x, int y, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791)
                ^ ((uint)seed * 2654435761u);
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }
    }

    /// <summary>Deterministic 0..1 from a lattice point and a salt.</summary>
    public static float Hash01(int x, int y, int z, int seed, uint salt = 0u) =>
        (Hash(x, y, z, unchecked(seed + (int)salt * 7919)) & 0xFFFFFFu) / 16777216f;

    /// <summary>A deterministic unit vector from a lattice point — used to
    /// give each island its own orientation for cleave planes and the like.</summary>
    public static Vector3 UnitVector(int x, int y, int z, int seed, uint salt = 0u)
    {
        // Sampling z uniformly and the angle uniformly gives points evenly
        // spread over the sphere; naive per-axis randomness clusters at the
        // corners of the cube.
        float cosTheta = Hash01(x, y, z, seed, salt) * 2f - 1f;
        float phi = Hash01(x, y, z, seed, salt + 101u) * Mathf.Tau;
        float sinTheta = Mathf.Sqrt(Mathf.Max(0f, 1f - cosTheta * cosTheta));
        return new Vector3(sinTheta * Mathf.Cos(phi), cosTheta, sinTheta * Mathf.Sin(phi));
    }
}
