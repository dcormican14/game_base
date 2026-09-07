using Godot;

namespace GameBase.Density;

/// <summary>
/// A whole planet: a ball of rock with a terrained surface and a thinning core.
///
/// The field answers the same question <see cref="IslandDensity"/> does — how
/// solidly is this rock — so the same streamer, chunk source and mesher drive
/// it without changes. What differs is the shape being described: one sphere
/// centred on the origin rather than a scattering of islands.
///
/// SPACING IS ALREADY UNIFORM
///
/// Nodes sit on the world's cubic lattice, one per cell, wherever the field is
/// positive. That lattice has the same pitch everywhere, so every node is the
/// same distance from its neighbours whatever depth it is at — there is no
/// shell remapping to do and no convergence to correct for. A sphere carved
/// out of it is simply the set of cells whose centres fall inside the radius.
///
/// This matters because the obvious alternative — laying nodes out on
/// latitude/longitude shells — does NOT have uniform spacing. Shells converge
/// at the poles and crowd together as they descend, and every node would need
/// its own orientation. Carving a cubic lattice keeps nodes axis-aligned and
/// evenly spaced, which is what the whole node pipeline already assumes.
///
/// THE CORE THINS OUT
///
/// A solid ball is unaffordable and pointless. At radius 400 it is 268 million
/// cells, essentially all of them buried where no player will ever see them.
///
/// So density is REMOVED with depth rather than shells being rebuilt: below
/// <see cref="SolidDepth"/> the field starts dropping cells, and by the core it
/// keeps only <see cref="CoreDensity"/> of them. What survives is chosen by the
/// same kind of noise the rest of the world is shaped by, so the gaps read as
/// caverns and veins in the bismuth rather than as missing data — the raw node
/// type then grows its rims into those gaps exactly as it does at any other
/// surface, and the result is a spongy, crystalline interior.
///
/// The thinning is smooth in depth, so there is no visible boundary where it
/// begins.
///
/// Every layer is a pure function of position and seed. Nothing consults what
/// has already been generated, so the planet is identical however the world is
/// diced up, in whatever order, on whatever thread — the same property that
/// makes the field streamable.
/// </summary>
public sealed class PlanetDensity
{
    private readonly int _seed;

    // ------------------------------------------------------------------ shape

    /// <summary>
    /// Distance from the centre to mean sea level, in nodes.
    ///
    /// The planet's headline dial. Everything else is expressed relative to it
    /// or in absolute nodes, so this can be changed alone.
    /// </summary>
    public float Radius { get; set; } = 800f;

    /// <summary>
    /// How far terrain departs from the mean radius, in nodes — the height
    /// between the deepest basin and the highest peak.
    /// </summary>
    public float TerrainHeight { get; set; } = 0f;

    /// <summary>Nodes per lobe of the coarsest terrain octave. Large, because
    /// this is continents rather than boulders.</summary>
    public float TerrainScale { get; set; } = 840f;

    /// <summary>Octaves of surface terrain. Each costs a noise evaluation per
    /// sample; three gives continents, ranges and hills.</summary>
    public int TerrainOctaves { get; set; } = 4;

    /// <summary>
    /// How much the terrain is pushed toward ridges rather than rolling hills.
    /// 0 is smooth dunes; 1 is sharp mountain chains.
    /// </summary>
    public float Ridged { get; set; } = 0.55f;

    // ------------------------------------------------------------------- core

    /// <summary>
    /// How deep below the surface rock stays completely solid, in nodes.
    ///
    /// Thinning begins here rather than at the surface so that the crust a
    /// player digs through is dependable, and so caverns do not open onto open
    /// sky.
    /// </summary>
    public float SolidDepth { get; set; } = 48f;

    /// <summary>
    /// The fraction of cells still filled at the very centre.
    ///
    /// Not zero: a hollow planet would let a falling player through, and the
    /// core reads better as a dense crystalline sponge than as a void. Not
    /// high either — this is where the cell count would otherwise explode.
    /// </summary>
    public float CoreDensity { get; set; } = 0.25f;

    /// <summary>
    /// How abruptly the thinning arrives. 1 is linear in depth; higher keeps
    /// the upper mantle solid and concentrates the hollowing in the core.
    /// </summary>
    public float CoreFalloff { get; set; } = 1.6f;

    /// <summary>Nodes per lobe of the cavern noise that decides which cells
    /// survive. Sets how big the voids are.</summary>
    public float CoreScale { get; set; } = 38f;

    /// <summary>Octaves of cavern noise. More gives the voids ragged, branching
    /// edges rather than smooth blobs.</summary>
    public int CoreOctaves { get; set; } = 3;

    /// <summary>
    /// Radius of the solid inner core, in nodes — rock the hollowing never
    /// touches.
    ///
    /// The centre needs this. Cavern noise decides survival per lobe, and the
    /// innermost ball is SMALLER than one lobe of it (at CoreScale 38 the
    /// finest octave is about 9 nodes across, against a centre only a few
    /// nodes wide), so the whole middle of the planet gets a single verdict.
    /// Measured, that verdict came out "remove": 0 of 2176 cells inside r=8
    /// survived, leaving a hollow the player could fall into with no floor
    /// under it.
    ///
    /// Fading back to solid over the last stretch also reads correctly — a
    /// dense inner core under a spongy mantle is what a planet actually has.
    /// </summary>
    public float InnerCore { get; set; } = 16f;

    // ------------------------------------------------------------------ crust

    /// <summary>
    /// How thick the topsoil cap is, in nodes. The generator asks for this and
    /// materials the top of the crust with it.
    /// </summary>
    public float CapThickness { get; set; } = 3f;

    /// <summary>
    /// Strength of the domain warp applied to the surface, as a fraction of
    /// <see cref="TerrainScale"/>. Bends coastlines and ridges off the noise
    /// lattice so the planet does not read as axis-aligned.
    /// </summary>
    public float WarpStrength { get; set; } = 0.22f;

    /// <summary>Nodes per lobe of the warp.</summary>
    public float WarpScale { get; set; } = 160f;

    public PlanetDensity(int seed)
    {
        _seed = seed;
    }

    /// <summary>
    /// The outermost radius any rock can reach — the bound a generator needs
    /// to know when a region is entirely sky.
    /// </summary>
    public float MaxRadius => Radius + TerrainHeight;

    /// <summary>
    /// The density at a point, in node coordinates. Positive is rock.
    ///
    /// Two terms. The SURFACE term is the signed distance inward from the
    /// terrained ground, so it goes positive as soon as a point is underground
    /// and grows with depth. The CORE term subtracts from it, and below
    /// <see cref="SolidDepth"/> starts winning often enough to punch the
    /// cell out.
    /// </summary>
    public float At(Vector3 p)
    {
        float distance = p.Length();

        // The exact centre has no direction to sample terrain along, and
        // normalising it would divide by zero. It is solid regardless.
        if (distance < 0.0001f)
            return 1f;

        float ground = GroundRadius(p / distance);

        // Positive underground, negative in the air, measured in nodes.
        float depth = ground - distance;
        if (depth <= 0f)
            return depth;

        return depth - Hollow(p, depth) * depth;
    }

    /// <summary>Convenience for callers holding loose coordinates.</summary>
    public float At(float x, float y, float z) => At(new Vector3(x, y, z));

    /// <summary>
    /// The density at a point whose ground radius is already known.
    ///
    /// <see cref="GroundRadius"/> is the expensive half of the field -- a
    /// domain warp plus several octaves of noise -- and it depends only on
    /// DIRECTION. A generator walking a column therefore computes the same
    /// value over and over: measured, 32768 calls cost 48ms, and a chunk made
    /// up to five of them per cell, which was most of the 144ms it took to
    /// generate one.
    ///
    /// Passing the radius in makes the rest arithmetic. This is exact, not an
    /// approximation: `ground` is precisely what At would have computed for
    /// any point along the same ray.
    /// </summary>
    public float AtWithGround(Vector3 p, float distance, float ground)
    {
        float depth = ground - distance;
        if (depth <= 0f)
            return depth;

        return depth - Hollow(p, depth) * depth;
    }

    /// <summary>Is this point inside rock?</summary>
    public bool IsSolid(Vector3 p) => At(p) > 0f;

    /// <summary>
    /// How far the ground surface sits from the centre along a unit direction.
    ///
    /// This is the planet's topography: the mean radius plus terrain. Because
    /// it depends only on DIRECTION, it is the same however deep the point
    /// being tested is, which is what makes <see cref="At"/> a cheap signed
    /// depth rather than a search.
    /// </summary>
    public float GroundRadius(Vector3 direction)
    {
        // Sampled on the sphere of mean radius rather than in raw direction
        // units, so TerrainScale reads in nodes like every other scale dial
        // instead of in radians.
        Vector3 at = direction * Radius;

        // Domain warp first, so ridges and coastlines bend off the noise
        // lattice. Without it the continents carry a faint axis-aligned grain.
        if (WarpStrength > 0f && WarpScale > 0f)
        {
            float w = 1f / WarpScale;
            var offset = new Vector3(
                DensityNoise.Fbm(at * w, _seed + 4421, 2),
                DensityNoise.Fbm(at * w + new Vector3(31.7f, 0f, 0f), _seed + 4422, 2),
                DensityNoise.Fbm(at * w + new Vector3(0f, 0f, 57.3f), _seed + 4423, 2));

            at += offset * (WarpStrength * TerrainScale);
        }

        float frequency = 1f / Mathf.Max(TerrainScale, 1f);
        float rolling = DensityNoise.Fbm(at * frequency, _seed + 1201,
            Mathf.Max(TerrainOctaves, 1));

        // Ridged noise folds the field at zero, turning smooth lobes into
        // creases — mountain chains rather than dunes. Blended rather than
        // switched, so the dial is continuous.
        //
        // The fold is then RE-CENTRED. Folding maps a signal that averaged
        // zero onto one that averages about +0.39, because |n| for fractal
        // noise clusters near zero and so 1-|n| clusters near one. Left alone
        // that lifts the whole planet: measured at Ridged 0.55 the ground ran
        // 395..419 around a nominal 400, so "radius" no longer meant sea level
        // and the surface probe landed underground. Subtracting the fold's
        // mean puts the terrain back either side of Radius.
        const float FoldMean = 0.39f;
        float ridges = (1f - Mathf.Abs(rolling)) * 2f - 1f - FoldMean;

        float height = Mathf.Lerp(rolling, ridges, Mathf.Clamp(Ridged, 0f, 1f));

        return Radius + height * TerrainHeight;
    }

    /// <summary>
    /// How much of this cell's density the core steals, in 0..1.
    ///
    /// Zero above <see cref="SolidDepth"/>, so the crust is untouched. Below
    /// it, cavern noise is compared against a threshold that rises with depth
    /// until, at the centre, only <see cref="CoreDensity"/> of cells survive.
    ///
    /// The comparison is SOFT — the noise scales the density down rather than
    /// switching it off — so voids have graded edges and the raw node type
    /// finds a surface to grow rims into instead of a stair-step cliff.
    /// </summary>
    private float Hollow(Vector3 p, float depth)
    {
        if (depth <= SolidDepth || CoreDensity >= 1f)
            return 0f;

        // THE INNER CORE IS SOLID, fading in over its own radius so there is
        // no shell-shaped seam where the caverns stop. See InnerCore for why
        // the very centre cannot be left to the noise.
        float distance = p.Length();
        if (InnerCore > 0f)
        {
            if (distance <= InnerCore)
                return 0f;

            if (distance < InnerCore * 2f)
            {
                float blend = (distance - InnerCore) / InnerCore;
                return Hollowing(p, depth) * blend;
            }
        }

        return Hollowing(p, depth);
    }

    /// <summary>The cavern term proper, before the inner core is blended in.</summary>
    private float Hollowing(Vector3 p, float depth)
    {

        // 0 at the bottom of the solid crust, 1 at the centre.
        float span = Mathf.Max(Radius - SolidDepth, 1f);
        float t = Mathf.Clamp((depth - SolidDepth) / span, 0f, 1f);
        t = Mathf.Pow(t, Mathf.Max(CoreFalloff, 0.01f));

        // How much of the rock should be GONE at this depth.
        float removed = t * (1f - Mathf.Clamp(CoreDensity, 0f, 1f));
        if (removed <= 0f)
            return 0f;

        // Cavern noise. The cells it scores lowest are the ones that go first,
        // so raising the threshold with depth carves progressively more of the
        // rock away — and always the same cells, so a region generated twice
        // matches.
        float n = DensityNoise.Fbm(p / Mathf.Max(CoreScale, 1f), _seed + 7717,
            Mathf.Max(CoreOctaves, 1));

        // MAPPED THROUGH THE NOISE'S OWN SPREAD, not assumed to fill 0..1.
        //
        // Fbm normalises by total amplitude, so its range is ±1 in principle —
        // but gradient noise almost never reaches its extremes and the octaves
        // partly cancel, so the realised spread is far tighter. Measured over
        // 40000 samples at this scale: 0.198..0.822, with the middle 80%
        // inside 0.385..0.616.
        //
        // Comparing a 0..1 threshold against that was the bug. At the centre
        // the threshold reached 0.70, past the noise's 90th percentile, so
        // essentially every cell was removed and the core came out completely
        // hollow instead of the quarter-full CoreDensity asks for.
        //
        // Rescaling to the measured spread makes `removed` mean what it says:
        // remove that FRACTION of cells. The tanh-like squash below is not
        // needed — the distribution is near enough symmetric that a linear
        // remap tracks the intended proportion closely.
        const float NoiseLo = 0.20f;
        const float NoiseHi = 0.82f;
        n = Mathf.Clamp((n * 0.5f + 0.5f - NoiseLo) / (NoiseHi - NoiseLo), 0f, 1f);

        // A soft edge one tenth of the range wide. Wide enough that the
        // boundary is a slope the mesher can shape, narrow enough that the
        // proportion actually removed still tracks `removed`.
        return Mathf.Clamp((removed - n) / 0.1f + 0.5f, 0f, 1f);
    }
}
