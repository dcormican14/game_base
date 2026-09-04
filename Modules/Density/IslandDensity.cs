using Godot;
using System.Collections.Generic;

namespace GameBase.Density;

/// <summary>
/// The layered density field that floating islands are carved out of.
///
/// A density field answers one question at any point in space: how solidly is
/// this rock? Positive is inside, negative is outside, and the surface is
/// wherever it crosses zero. That is all a generator needs — walk the cells of
/// a region, ask, and fill the ones that come back positive.
///
/// Building the answer out of LAYERS is what makes the result look designed
/// rather than noisy. Each layer does one job and is tunable alone:
///
///   1. PLACEMENT  (<see cref="IslandPlacement"/>) — where islands are and how
///      big. Large islands rare, small ones common.
///   2. BODY       — the base solid: a radial falloff crossed with a vertical
///      profile that is flat on top and tapers to a spike below.
///   3. EROSION    — multi-octave ridged noise on the sides, weighted to zero
///      at the top rim and full strength below, cutting the jagged spurs and
///      overhangs. Fully 3D, so it genuinely undercuts.
///   4. RELIEF     — gentle rolling displacement of the top plane, and caves
///      bored through the body.
///
/// Then two PLANE operations finish the silhouette:
///
///   - the TOP CUT, a horizontal plane per island that shears the top dead
///     flat, which is what makes an island read as cleaved from something
///     larger rather than merely eroded;
///   - the CLEAVE, an occasional tilted plane that splits an island into
///     shards with matching flat faces.
///
/// Every layer is a pure function of position and seed. Nothing here consults
/// what has already been generated, so the field is identical however the
/// world is diced up, in whatever order, on whatever thread.
/// </summary>
public sealed class IslandDensity
{
    private readonly int _seed;

    /// <summary>Where islands are and how big — layer 1.</summary>
    public IslandPlacement Placement { get; }

    // ------------------------------------------------------------- layer dials

    /// <summary>Octaves of side erosion. More gives finer spurs; each costs a
    /// noise evaluation per sample.</summary>
    public int ErosionOctaves { get; set; } = 3;

    /// <summary>How deeply erosion bites into the sides, as a fraction of the
    /// island's radius. This is the main "how jagged" dial.</summary>
    public float ErosionStrength { get; set; } = 0.5f;

    /// <summary>Nodes per lobe of the coarsest erosion octave. Smaller makes
    /// busier, more granular rock.</summary>
    public float ErosionScale { get; set; } = 34f;

    /// <summary>
    /// How strongly erosion is biased downward. At 0 the sides are eroded
    /// evenly top to bottom; higher concentrates the damage low, leaving the
    /// rim under the flat top comparatively clean — which is what reads as an
    /// island rather than a lump.
    /// </summary>
    public float ErosionBias { get; set; } = 1.35f;

    /// <summary>Amplitude of the rolling relief on the flat top, in nodes.
    /// Kept small so the top stays walkable everywhere.</summary>
    public float TopRelief { get; set; } = 2.6f;

    /// <summary>Nodes per lobe of the top's rolling relief.</summary>
    public float TopReliefScale { get; set; } = 48f;

    /// <summary>
    /// How much the SHAPE of an island varies from island to island — the
    /// taper of its profile and how hard erosion wears it.
    ///
    /// Size variance alone is not enough: a field of islands that are all the
    /// same silhouette at different scales still reads as repetitive. This is
    /// what makes one a broad plateau, the next a plunging spire, the next a
    /// gnawed stub. 0 makes every island the same shape.
    /// </summary>
    public float ProfileVariance { get; set; } = 0.45f;

    /// <summary>
    /// How far the island's outline is stretched and pinched out of round, as
    /// a fraction of its radius. This is what stops islands reading as discs;
    /// 0 leaves the base shape a circle for erosion to fray.
    /// </summary>
    public float OutlineStrength { get; set; } = 0.62f;

    /// <summary>
    /// Lobe size of the outline distortion, as a MULTIPLE of the island's own
    /// radius. Around 1 gives a few big lobes — peninsulas and bays; much
    /// smaller just makes a frilly circle, which erosion already does better.
    /// Relative rather than absolute so islands of every size are distorted
    /// alike.
    /// </summary>
    public float OutlineScale { get; set; } = 0.85f;

    /// <summary>
    /// Shapes the vertical profile. Above 1 the island narrows steadily from
    /// the top down, reading as a cone; below 1 it holds its width further
    /// down and then pinches hard into a spike.
    /// </summary>
    public float TaperExponent { get; set; } = 1.15f;

    /// <summary>
    /// How abruptly erosion fades in below the rim. Higher brings the jagged
    /// sides right up under the flat top; lower leaves a clean band of rim
    /// below it.
    /// </summary>
    public float ErosionRimFade { get; set; } = 6f;

    /// <summary>
    /// How far space is bent before the island's body is measured in it, as a
    /// fraction of the island's radius. This is what produces genuine
    /// overhangs and undercuts — see the warp in
    /// <see cref="DensityFor"/>. 0 disables it, leaving sides that are rough
    /// but never fold back.
    /// </summary>
    public float WarpStrength { get; set; } = 0.28f;

    /// <summary>Nodes per lobe of the domain warp. Large relative to the
    /// erosion scale, so the warp bends whole flanks rather than fraying
    /// them.</summary>
    public float WarpScale { get; set; } = 60f;

    /// <summary>Thickness of the dark capping layer on the tops of the
    /// LARGEST islands, in nodes. Smaller islands get proportionally less —
    /// see <see cref="CapNodesFor"/>.</summary>
    public float CapThickness { get; set; } = 2f;

    /// <summary>
    /// Whether the cap thins on smaller islands instead of staying a fixed
    /// number of nodes.
    ///
    /// A fixed thickness is a different fraction of the rock depending on the
    /// island: a couple of nodes over a fifty-node island is a skin, while the
    /// same couple over a nine-node one is most of its volume. Scaling keeps
    /// it reading as a skin at every size; leaving it off keeps the cap an
    /// exact, predictable depth everywhere. Off by default — a fixed cap is
    /// the simpler behaviour and the one to judge first.
    /// </summary>
    public bool ScaleCapWithIsland { get; set; }

    /// <summary>
    /// How thick the dark cap is on one island, in nodes.
    /// </summary>
    public int CapNodesFor(in Island island)
    {
        if (CapThickness <= 0f)
            return 0;

        if (!ScaleCapWithIsland)
            return Mathf.Max(1, Mathf.RoundToInt(CapThickness));

        // Referenced against a large island's depth, so CapThickness reads as
        // "nodes on a big island" and everything smaller scales down from it.
        float fraction = Mathf.Clamp(island.Depth / 55f, 0.34f, 1f);
        return Mathf.Max(1, Mathf.RoundToInt(CapThickness * fraction));
    }

    public IslandDensity(int seed, IslandPlacement placement = null)
    {
        _seed = seed;
        Placement = placement ?? new IslandPlacement(seed);
    }

    // ------------------------------------------------------------------ field

    /// <summary>
    /// The density at a point, in node coordinates. Positive is rock.
    ///
    /// Islands are combined by taking the strongest — a max rather than a sum
    /// — so two islands that overlap do not build a density ridge between
    /// them where neither alone had rock. Each island's shape stays its own.
    /// </summary>
    public float At(Vector3 point, List<Island> candidates)
    {
        float best = float.NegativeInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            float d = DensityFor(candidates[i], point);
            if (d > best)
                best = d;
        }

        return best == float.NegativeInfinity ? -1f : best;
    }

    /// <summary>
    /// Is this point inside rock? The same question <see cref="At"/> answers,
    /// when only the sign of the answer is wanted.
    ///
    /// Islands are combined with a max, so <see cref="At"/> has to test the
    /// point against every candidate to find the largest. A caller that only
    /// needs to know whether ANY island claims the point can stop at the first
    /// one that does — and because islands are placed on a lattice that keeps
    /// them apart, a point inside one is almost never inside another.
    ///
    /// Measured, generation was running 3.7 island tests per cell; this is
    /// what removes the ones after the first hit. Cells in open sky still test
    /// every candidate, since none of them claims the point — but those are
    /// rejected by the cheap bounding checks at the top of
    /// <see cref="DensityFor"/> rather than by the noise stack.
    /// </summary>
    public bool IsSolid(Vector3 point, List<Island> candidates)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            if (DensityFor(candidates[i], point) > 0f)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The vertical span an island's rock can possibly occupy at a given
    /// column, as node y values. Rock outside it is impossible, so a caller
    /// walking a column can skip straight to the span rather than asking the
    /// field at every empty cell above and below.
    ///
    /// Both ends are deliberate over-estimates — every displacing layer's full
    /// amplitude is allowed for. Too generous only wastes a few samples; too
    /// tight would clip real rock.
    ///
    /// Returns false when the column cannot contain any of this island's rock
    /// at all, which is the common case and the whole point.
    /// </summary>
    public bool VerticalSpan(in Island island, float x, float z, out float minY, out float maxY)
    {
        minY = maxY = 0f;

        float dx = x - island.Centre.X;
        float dz = z - island.Centre.Z;

        // How far the warp can drag a sample horizontally, which widens the
        // footprint a column can be inside.
        float slack = SurfaceSlack(island);
        float reach = island.Radius + slack;
        if (dx * dx + dz * dz > reach * reach)
            return false;

        // The warp displaces vertically too, so the top and bottom both move.
        float vertical = island.Radius * WarpStrength * 1.6f + TopRelief + 2f;

        maxY = island.TopY + vertical;
        minY = island.Centre.Y + island.TopHeight - island.Depth - vertical;
        return true;
    }

    /// <summary>
    /// The vertical span any of `candidates` could fill at a column. False
    /// when no island reaches the column at all — which is most of the sky,
    /// and skipping it outright is what keeps a region build affordable.
    /// </summary>
    public bool VerticalSpan(List<Island> candidates, float x, float z,
        out int minY, out int maxY, List<Island> reaching = null)
    {
        reaching?.Clear();

        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!VerticalSpan(candidates[i], x, z, out float a, out float b))
                continue;

            // The islands that actually reach this column, which is usually
            // one or two of the tile's several. Handing that shorter list back
            // lets the caller test each cell against only those, rather than
            // re-rejecting the same distant islands once per cell of the
            // column — the reject is cheap, but at a hundred-odd cells per
            // column it is not free.
            reaching?.Add(candidates[i]);

            if (a < lo) lo = a;
            if (b > hi) hi = b;
        }

        if (lo > hi)
        {
            minY = maxY = 0;
            return false;
        }

        minY = Mathf.FloorToInt(lo);
        maxY = Mathf.CeilToInt(hi);
        return true;
    }

    /// <summary>Density at a point, gathering candidates itself. Convenient
    /// for one-off queries; a bulk walk should gather per region instead.</summary>
    public float At(Vector3 point)
    {
        var scratch = new List<Island>();
        Placement.Near(point, scratch);
        return At(point, scratch);
    }

    /// <summary>
    /// How far every displacing layer together could move this island's
    /// surface, in nodes.
    ///
    /// Used to skip work: a sample further outside the plain body than this
    /// can never be brought back in, and one further INSIDE than this can
    /// never be carved out (except by caves and cleaves, which are applied
    /// separately). Both shortcuts are worth a great deal, so this wants to be
    /// a true bound but a TIGHT one — an inflated slack silently disables the
    /// deep-interior path, which is what turned a 20-second region build into
    /// a 74-second one.
    ///
    /// The terms, from the layers below:
    ///   erosion  — amplitude * (1.5 for the displacing fbm + 0.85 for the
    ///              centred ridged term), amplitude itself being
    ///              radius * ErosionStrength * wear, wear at most
    ///              1 + ProfileVariance * 0.8;
    ///   warp     — moves the sample up to radius * WarpStrength horizontally
    ///              (1.6x that vertically, handled by the caller);
    ///   outline  — scales the half-width by up to 1 +/- OutlineStrength.
    ///
    /// fbm and ridged are normalised to their octave sums and in practice stay
    /// well inside +/-1, so this remains conservative.
    /// </summary>
    private float SurfaceSlack(in Island island)
    {
        float erosion = ErosionStrength * (1f + ProfileVariance * 0.8f) * 2.35f;
        return island.Radius * (erosion + WarpStrength + OutlineStrength) + 2f;
    }

    /// <summary>
    /// The same bound, but for a sample whose depth fraction is already known.
    ///
    /// <see cref="SurfaceSlack"/> is the WORST case over the whole island, and
    /// that worst case is about two and a half times the radius — larger than
    /// the island itself, which is why the deep-interior shortcut never fired
    /// and every sample paid for the full layer stack.
    ///
    /// But erosion's amplitude is scaled by a weight derived from `t`, and `t`
    /// is known before any noise is touched. Feeding it in turns a constant
    /// worst case into a bound that is tight where it matters: near the top,
    /// where the erosion weight is almost zero, the slack collapses to little
    /// more than the warp and outline terms, and the interior test starts
    /// passing. This is the whole reason a large island's core is cheap.
    /// </summary>
    private float SurfaceSlackAt(in Island island, float t)
    {
        float ramp = Mathf.Min(1f, t * ErosionRimFade);
        float weight = ramp * Mathf.Pow(t + 0.15f, ErosionBias);

        float erosion = ErosionStrength * (1f + ProfileVariance * 0.8f) * 2.35f * weight;
        return island.Radius * (erosion + WarpStrength + OutlineStrength) + 2f;
    }

    /// <summary>
    /// One island's contribution at a point — layers 2 through 4 plus the
    /// plane cuts, in order.
    /// </summary>
    public float DensityFor(in Island island, Vector3 point)
    {
        Vector3 local = point - island.Centre;

        // Cheap reject before any noise is touched. The vast majority of
        // samples in a region are nowhere near this island, and every one of
        // them can be answered with a subtraction and a compare.
        float bound = island.BoundingRadius;
        if (Mathf.Abs(local.X) > bound || Mathf.Abs(local.Y) > bound || Mathf.Abs(local.Z) > bound)
            return -1f;

        if (local.X * local.X + local.Z * local.Z > bound * bound)
            return -1f;

        // DEEP INTERIOR — well inside the unwarped body and well below its
        // top, no layer can reach far enough to carve this sample out except
        // the caves, so the warp and the erosion stack are pure waste here.
        //
        // This is the single biggest saving in a region build: an island is
        // mostly interior, and the expensive layers only matter near a
        // surface.
        //
        // The bound is computed for THIS SAMPLE's depth rather than as the
        // island's global worst case. That distinction decides whether the
        // shortcut is usable at all: the worst case runs to about two and a
        // half times the radius — wider than the island — so the test could
        // never pass, and every interior sample paid for the full stack. The
        // erosion weight that makes it so large is itself a function of depth,
        // and depth is known here for free.
        float flatHorizontal = Mathf.Sqrt(local.X * local.X + local.Z * local.Z);
        float depthBelowTop = island.TopHeight - local.Y;
        // The warp can push this sample DOWN the island by up to its vertical
        // amount, landing it at a greater depth where erosion is stronger. The
        // bound must be for the deepest place the sample could end up, not
        // where it started, or the shortcut would fire on samples the warp
        // carries into a heavily eroded band.
        float warpY = island.Radius * WarpStrength * 1.6f;
        float tRaw = Mathf.Clamp((depthBelowTop + warpY) / Mathf.Max(island.Depth, 0.001f), 0f, 1f);
        float slack = SurfaceSlackAt(island, tRaw);

        if (flatHorizontal < island.Radius - slack
            && depthBelowTop > slack + TopRelief
            && depthBelowTop < island.Depth - slack)
        {
            return island.Radius - flatHorizontal;
        }

        // DOMAIN WARP — bend space before the body shape is measured in it.
        //
        // This is what makes overhangs possible at all. The body below is a
        // radius compared against a horizontal distance, which defines exactly
        // one surface per direction: displacing that radius can roughen the
        // silhouette but can never fold it back over itself, because the
        // surface is a function of the direction. Warping the POSITION instead
        // bends the whole field, so a column of space can leave the rock,
        // re-enter it, and leave again — which is an overhang.
        //
        // The vertical component is the one that undercuts, so it gets its own
        // stronger weight; warping horizontally alone just wobbles the
        // silhouette the erosion layer already handles.
        // SKY CUT — reject before the warp, not after.
        //
        // The warp is nine gradient-noise evaluations, and it used to run for
        // every sample that reached here, including the great majority that
        // sit in open sky above an island and are rejected a few lines later
        // by the top cut. Measured on a chunk of pure sky that was 1.0M noise
        // evaluations for zero solid cells — the single largest waste in the
        // build.
        //
        // Both displacements that could carry a sky sample down into rock are
        // BOUNDED and known without sampling anything: the warp moves a point
        // by at most its own amplitude, and the relief lifts the top by at
        // most TopRelief. A sample above the sum of the two cannot be inside
        // this island whatever the noise says, so it can be rejected on
        // arithmetic alone.
        float skyCut = island.TopHeight + TopRelief
            + (WarpStrength > 0f ? island.Radius * WarpStrength * 1.6f : 0f);
        if (local.Y > skyCut)
            return -1f;

        if (WarpStrength > 0f)
        {
            // The warp field does not depend on the island — only on the point
            // — so for a sample tested against several candidate islands it is
            // the same three vectors every time. Memoised on the last point
            // asked about, which turns N islands' worth of warp noise (nine
            // gradient-noise evaluations each) into one.
            Warp(point, out float wx, out float wy, out float wz);

            float amount = island.Radius * WarpStrength;
            local.X += wx * amount;
            local.Y += wy * amount * 1.6f;
            local.Z += wz * amount;
        }

        float horizontal = Mathf.Sqrt(local.X * local.X + local.Z * local.Z);
        if (horizontal > bound)
            return -1f;

        // ---------------------------------------------------- 2. body shape

        float topY = island.TopHeight;

        // Above the top plane there is nothing at all — the top cut. Doing it
        // here rather than at the end also means the whole erosion and cave
        // stack is skipped for every sample in the sky above an island.
        float relief = TopReliefAt(island, point);
        if (local.Y > topY + relief)
            return -1f;

        // Below the top, the island's half-width tapers from full radius to
        // nothing over its depth. The exponent shapes the profile: above 1 the
        // sides stay wide near the top and pinch late, which is the classic
        // floating-island silhouette rather than a plain cone.
        float below = topY - local.Y;
        float t = Mathf.Clamp(below / Mathf.Max(island.Depth, 0.001f), 0f, 1f);
        // The exponent shapes the profile: above 1 the sides stay wide near
        // the top and pinch late, which is the classic floating-island
        // silhouette rather than a plain cone. Lower it below 1 to hold the
        // width further down and pinch harder into a spike.
        //
        // Varied per island by ProfileVariance, so the sky holds broad
        // shallow-tapered plateaus alongside masses that hold their width and
        // then plunge into a spire. Without this every island is the same
        // silhouette at a different scale, which is what makes a field of them
        // read as repetitive however much the sizes differ.
        float exponent = TaperExponent
            * Mathf.Lerp(1f - ProfileVariance, 1f + ProfileVariance, island.Variant);
        float taper = 1f - Mathf.Pow(t, Mathf.Max(0.15f, exponent));
        if (taper <= 0f)
            return -1f;

        float halfWidth = island.Radius * taper;

        // ---------------------------------------------- 2b. outline shaping
        //
        // The body above is a radius compared against a distance, which is a
        // CIRCLE by construction — and no amount of erosion on top hides that,
        // because erosion perturbs a fundamentally round outline rather than
        // replacing it. Islands come out as discs with frayed edges.
        //
        // This bends the outline itself: a low-frequency field sampled around
        // the island stretches the radius in some directions and pinches it in
        // others, so the base shape is lobed, peninsular, sometimes almost
        // dumbbell — before a single grain of erosion is applied.
        if (OutlineStrength > 0f)
        {
            // Sampled on a horizontal ring around the island's own centre, so
            // the distortion belongs to the island and turns with it. The
            // island's variant offsets the sample so no two share an outline.
            //
            // The scale is RELATIVE to the island's radius, not an absolute
            // node count. Islands span a wide range of sizes, and a fixed
            // wavelength that gives a big island a few broad lobes gives a
            // small one less than half of one — which is no distortion at all,
            // and why the small islands stayed perfectly round. Tying it to
            // the radius gives every island the same number of lobes and so
            // the same character at any size.
            float wavelength = Mathf.Max(island.Radius * OutlineScale, 2f);
            float os = 1f / wavelength;
            var at = new Vector3(
                local.X * os,
                island.Variant * 128f,
                local.Z * os);

            float lobes = DensityNoise.Fbm(at, _seed + 3313, 3, 0.6f);

            // Multiplicative, so the distortion scales with the island rather
            // than swamping a small one and barely marking a large one.
            halfWidth *= 1f + lobes * OutlineStrength;
        }

        // Density as a signed distance in the horizontal: positive inside.
        float density = halfWidth - horizontal;

        // A generous early out. Erosion displaces the surface both ways, by at
        // most the sum of the two terms' amplitudes, so a sample further
        // outside than that can never be brought back in and the expensive
        // layers can be skipped. Deliberately an over-estimate: too large only
        // wastes a few samples, while too small would clip real rock.
        // Tight rather than worst-case, for the same reason as the interior
        // test above: this sample's own depth already bounds how far erosion
        // can move the surface here.
        float maxErosion = SurfaceSlackAt(island, t);
        if (density < -maxErosion)
            return -1f;

        // -------------------------------------------------- 3. side erosion

        if (ErosionStrength > 0f && ErosionOctaves > 0)
        {
            float scale = 1f / Mathf.Max(ErosionScale, 1f);

            // Weight: zero at the very top so the rim under the flat top stays
            // clean, ramping in over the first stretch below it, then biased
            // further downward so the deep sides are the most savaged.
            float ramp = Mathf.Min(1f, t * ErosionRimFade);
            float weight = ramp * Mathf.Pow(t + 0.15f, ErosionBias);

            // Erosion strength varies per island on the same variance dial, so
            // some masses are gnawed to spurs and others stay comparatively
            // sheer — another axis on which no two islands match.
            float wear = Mathf.Lerp(1f - ProfileVariance * 0.8f, 1f + ProfileVariance * 0.8f,
                Mathf.Abs(island.Variant * 2f - 1f));
            float amplitude = island.Radius * ErosionStrength * wear * weight;

            // Two terms, doing two different jobs.
            //
            // The first DISPLACES the silhouette, in and out, symmetrically
            // about zero. This is what makes overhangs: rock has to bulge as
            // well as recede to undercut, and a term that only ever subtracts
            // can never produce one — it just shrinks the island.
            float displace = DensityNoise.Fbm(point * scale, _seed + 1607, ErosionOctaves, 0.55f);
            density += displace * amplitude * 1.5f;

            // The second CREASES it. Ridged noise sits in 0..1 with a mean
            // near a half, so subtracting it raw is mostly a constant shrink
            // with a little texture on top — the variance is the whole point,
            // so it is centred before use. What survives is the sharp part:
            // spurs where the ridges run, notches between them.
            float ridged = DensityNoise.Ridged(point * (scale * 2.1f), _seed + 811,
                Mathf.Max(2, ErosionOctaves - 1));
            density += (ridged - 0.5f) * amplitude * 1.7f;
        }

        return density;
    }


    // The last point the warp was evaluated at, and its result. A sample is
    // tested against every candidate island in turn, and the warp is identical
    // for all of them, so this collapses that repeat.
    // [ThreadStatic] rather than a plain field: the memo is a cache, and a
    // cache shared between threads is a race — two workers evaluating
    // different points would overwrite each other's entry and hand back the
    // wrong warp, silently corrupting terrain in a way that varies run to run.
    // One cache per thread keeps the saving and removes the hazard.
    //
    // Static, because [ThreadStatic] only applies to statics; the seed is
    // folded into the key so two differently seeded fields on one thread
    // cannot read each other's entry.
    [System.ThreadStatic] private static Vector3 _warpPoint;
    [System.ThreadStatic] private static int _warpSeed;
    [System.ThreadStatic] private static bool _warpValid;
    [System.ThreadStatic] private static float _warpX;
    [System.ThreadStatic] private static float _warpY;
    [System.ThreadStatic] private static float _warpZ;

    /// <summary>The domain warp at a point — the same for every island, so it
    /// is computed once per point rather than once per island.</summary>
    private void Warp(Vector3 point, out float wx, out float wy, out float wz)
    {
        if (_warpValid && _warpSeed == _seed
            && point.X == _warpPoint.X && point.Y == _warpPoint.Y && point.Z == _warpPoint.Z)
        {
            wx = _warpX;
            wy = _warpY;
            wz = _warpZ;
            return;
        }

        // Two octaves rather than three.
        //
        // The warp DISPLACES space; it does not draw a surface. Its job is to
        // bend the body enough to fold overhangs out of it, which is a
        // low-frequency effect — the third octave moves each sample by a few
        // hundredths of a radius, which is finer than the lattice the result
        // is rasterised onto. Measured at 9% of the whole field's cost for a
        // 1.3% change in solid volume.
        float ws = 1f / Mathf.Max(WarpScale, 1f);
        wx = DensityNoise.Fbm(point * ws, _seed + 5077, 2);
        wy = DensityNoise.Fbm(point * ws + new Vector3(19.3f, -7.1f, 43.9f), _seed + 6151, 2);
        wz = DensityNoise.Fbm(point * ws + new Vector3(-33.7f, 61.2f, 11.4f), _seed + 7213, 2);

        _warpPoint = point;
        _warpSeed = _seed;
        _warpValid = true;
        _warpX = wx;
        _warpY = wy;
        _warpZ = wz;
    }

    /// <summary>
    /// The rolling displacement of this island's top plane at a point, in
    /// nodes. Broad and low-amplitude: the top must read as terrain without
    /// ever becoming a cliff the player cannot walk up.
    /// </summary>
    private float TopReliefAt(in Island island, Vector3 point)
    {
        if (TopRelief <= 0f)
            return 0f;

        // Sampled on the horizontal plane only — the relief is a height over
        // the top, so it must not vary with the y being tested, or the top
        // surface would not be a single well-defined height. The island's own
        // centre enters as a fixed offset so neighbouring islands get
        // different relief rather than sharing one continuous landscape.
        float scale = 1f / Mathf.Max(TopReliefScale, 1f);
        var at = new Vector3(
            point.X * scale + island.Centre.Y * 0.37f,
            island.Variant * 64f,
            point.Z * scale - island.Centre.Y * 0.21f);

        return DensityNoise.Fbm(at, _seed + 4231, 3) * TopRelief;
    }


    // ---------------------------------------------------------------- surface

    /// <summary>
    /// Whether a point is within the capping layer — the top
    /// <see cref="CapThickness"/> nodes of rock, which the generator surfaces
    /// in the dark capping material.
    ///
    /// Asked as "is there sky not far above me" rather than by comparing to
    /// the island's top plane, so the cap follows the rolling relief and drapes
    /// over outcrops instead of cutting through them at a fixed height.
    /// </summary>
    public bool IsCap(Vector3 point, List<Island> candidates)
    {
        if (CapThickness <= 0f)
            return false;

        // One node above the cap's thickness: if THAT is empty, this point is
        // within the top layer of its island.
        var above = new Vector3(point.X, point.Y + CapThickness, point.Z);
        return At(above, candidates) <= 0f;
    }
}
