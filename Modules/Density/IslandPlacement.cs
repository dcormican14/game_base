using Godot;
using System.Collections.Generic;

namespace GameBase.Density;

/// <summary>
/// One island's identity: where it is, how big, and the per-island random
/// numbers every other layer keys off so the same island always comes out the
/// same way.
/// </summary>
public readonly struct Island
{
    /// <summary>The lattice cell that spawned it — the island's stable id.</summary>
    public readonly Vector3I Cell;

    /// <summary>Centre in node coordinates. Not the cell centre: each island
    /// is jittered inside its cell so the field has no visible grid.</summary>
    public readonly Vector3 Centre;

    /// <summary>Horizontal radius of the island's top, in nodes.</summary>
    public readonly float Radius;

    /// <summary>How far the island's spike hangs below its top, in nodes.</summary>
    public readonly float Depth;

    /// <summary>Height of the flat top above the centre, in nodes. The top
    /// plane sits at Centre.Y + TopHeight.</summary>
    public readonly float TopHeight;

    /// <summary>0..1, unique per island — the general-purpose per-island roll
    /// layers use to vary themselves.</summary>
    public readonly float Variant;

    /// <summary>Which slot of its lattice cell this island occupies. A cell
    /// may hold several where islands cluster, and the slot is what makes two
    /// islands from the same cell distinct.</summary>
    public readonly int Slot;

    public Island(Vector3I cell, Vector3 centre, float radius, float depth,
        float topHeight, float variant, int slot = 0)
    {
        Cell = cell;
        Centre = centre;
        Radius = radius;
        Depth = depth;
        TopHeight = topHeight;
        Variant = variant;
        Slot = slot;
    }

    /// <summary>The y of this island's flat top.</summary>
    public float TopY => Centre.Y + TopHeight;

    /// <summary>
    /// A bound on everything this island can possibly fill, including the
    /// worst case of every erosion and relief layer pushing outward.
    ///
    /// Only used to skip work — it must never be too SMALL (that clips rock)
    /// but being generous only costs a few wasted samples.
    /// </summary>
    public float BoundingRadius => Mathf.Max(Radius * 2.1f, Depth * 1.2f) + 8f;
}

/// <summary>
/// LAYER 1 — placement.
///
/// Where islands are, and how big. This is the layer that makes the world
/// endless: islands are not a list but a function of space. Space is diced
/// into cells of <see cref="Spacing"/> nodes, each cell either holds one
/// island or does not, and which it is depends only on the cell's coordinates
/// and the seed. To find every island that could affect a point, only the
/// cells within reach need be visited — a couple of dozen lookups, whatever
/// the size of the world.
///
/// Sizes follow a power law, so large islands are rare and small ones common.
/// A uniform roll gives a world of interchangeable mid-size lumps; a power law
/// gives the read of a real archipelago, where a few landmasses dominate the
/// skyline and the gaps between are dusted with rocks.
/// </summary>
public sealed class IslandPlacement
{
    private readonly int _seed;

    /// <summary>Nodes per placement cell. One island at most per cell, so this
    /// sets how far apart islands can be.</summary>
    public float Spacing { get; }

    /// <summary>Fraction of cells that actually hold an island, before the
    /// clustering field modulates it.</summary>
    public float Density { get; }

    /// <summary>
    /// How strongly a large-scale field gathers islands into archipelagos.
    ///
    /// Without this, one roll per lattice cell against a constant threshold
    /// gives a blue-noise scatter: the most EVEN distribution there is, and
    /// the reason the sky reads as a grid of islands rather than as
    /// landscape. Modulating the threshold with a smooth low-frequency field
    /// makes whole regions generous and whole regions barren, so islands
    /// crowd together and real open sky opens up between the crowds.
    ///
    /// 0 restores the flat, even scatter.
    /// </summary>
    public float Clustering { get; }

    /// <summary>Nodes per lobe of the clustering field. This is the size of an
    /// archipelago, so it wants to be several times the island spacing —
    /// otherwise it modulates individual islands rather than groups.</summary>
    public float ClusterScale { get; }

    /// <summary>
    /// How sharply the clustering field separates crowded sky from empty sky.
    /// 1 is a plain gradient; higher pushes the field to the extremes, so
    /// archipelagos have edges rather than fading out gradually.
    /// </summary>
    public float ClusterContrast { get; }

    /// <summary>
    /// Extra islands allowed in one lattice cell, beyond the first.
    ///
    /// The lattice puts a floor under how close two islands can be, which is
    /// what stops them ever touching or overlapping into bigger forms. Letting
    /// a cell hold more than one — only where the clustering field is
    /// generous — lets islands genuinely huddle.
    /// </summary>
    public int MaxPerCell { get; }

    /// <summary>Smallest island radius, in nodes.</summary>
    public float MinRadius { get; }

    /// <summary>Largest island radius, in nodes.</summary>
    public float MaxRadius { get; }

    /// <summary>
    /// How sharply size is biased toward the small end. 1 is a uniform spread;
    /// higher makes big islands rarer. At 3 roughly two thirds of islands fall
    /// in the smallest third of the range.
    /// </summary>
    public float SizeBias { get; }

    /// <summary>How far an island's spike hangs below its top, as a multiple
    /// of its radius.</summary>
    public float DepthRatio { get; }

    /// <summary>
    /// Longest an island's spike may be, in nodes, whatever its radius implies.
    ///
    /// This is a ceiling rather than a target: it only bites on the largest
    /// islands, and it exists because depth scales with radius while the
    /// generated region does not. It also bounds how far a density query has
    /// to search for candidate islands, so raising it costs build time.
    /// </summary>
    public float MaxDepth { get; }

    /// <summary>
    /// How far islands may drift vertically out of their lattice cell, as a
    /// multiple of spacing. This is what makes the scatter read as fully 3D
    /// rather than as layers — but it is deliberately a separate dial from the
    /// horizontal jitter, because too much of it makes islands overlap
    /// vertically into columns.
    /// </summary>
    public float VerticalSpread { get; }

    /// <summary>
    /// Guarantee an island centred on the origin, whatever the seed would
    /// otherwise have rolled there.
    ///
    /// Placement is a hash over an infinite lattice, so at most seeds there is
    /// simply no rock above the origin — and a player spawned at 0,0,0 falls
    /// forever through empty sky. Rather than hunt for a spawn afterwards, the
    /// world is made to owe the player one island it can always be dropped
    /// onto. Every other cell is untouched, so the field stays a pure function
    /// of position and seed and the anchor island itself is shaped by exactly
    /// the same layers as any other.
    /// </summary>
    public bool AnchorAtOrigin { get; }

    /// <summary>Radius of the guaranteed origin island, in nodes.</summary>
    public float AnchorRadius { get; }

    /// <summary>The lattice cell the origin falls in — the one the anchor
    /// overrides.</summary>
    private Vector3I OriginCell => new(
        Mathf.FloorToInt(0f / Spacing),
        Mathf.FloorToInt(0f / Spacing),
        Mathf.FloorToInt(0f / Spacing));

    public IslandPlacement(int seed, float spacing = 190f, float density = 0.8f,
        float minRadius = 8f, float maxRadius = 135f, float sizeBias = 1.85f,
        float depthRatio = 2.2f, float verticalSpread = 1.15f,
        bool anchorAtOrigin = true, float anchorRadius = 30f,
        float clustering = 0.95f, float clusterScale = 620f, int maxPerCell = 5,
        float clusterContrast = 3.4f, float maxDepth = 230f)
    {
        AnchorAtOrigin = anchorAtOrigin;
        AnchorRadius = Mathf.Max(4f, anchorRadius);
        Clustering = Mathf.Clamp(clustering, 0f, 1f);
        ClusterScale = Mathf.Max(16f, clusterScale);
        MaxPerCell = Mathf.Clamp(maxPerCell, 1, 8);
        ClusterContrast = Mathf.Max(0.2f, clusterContrast);
        _seed = seed;
        Spacing = Mathf.Max(8f, spacing);
        Density = Mathf.Clamp(density, 0f, 1f);
        MinRadius = Mathf.Max(2f, minRadius);
        MaxRadius = Mathf.Max(MinRadius, maxRadius);
        SizeBias = Mathf.Max(0.1f, sizeBias);
        DepthRatio = Mathf.Max(0.1f, depthRatio);
        MaxDepth = Mathf.Max(8f, maxDepth);
        VerticalSpread = Mathf.Max(0f, verticalSpread);
    }

    /// <summary>The largest bounding radius any island can have — how far
    /// afield a query must look.</summary>
    public float MaxReach
    {
        get
        {
            var biggest = new Island(Vector3I.Zero, Vector3.Zero, MaxRadius,
                Mathf.Min(MaxRadius * DepthRatio * 2.3f, MaxDepth), 0f, 0f);
            return biggest.BoundingRadius;
        }
    }

    /// <summary>
    /// The island in a lattice cell, if any.
    ///
    /// Pure in the cell coordinates and the seed — no state, no order
    /// dependence — which is what lets two chunks generated years apart agree
    /// about the island straddling them.
    /// </summary>
    public bool TryIslandAt(Vector3I cell, out Island island)
    {
        return TrySlot(cell, 0, out island);
    }

    /// <summary>
    /// How generous this part of space is, 0..1 — the clustering field.
    ///
    /// Smooth and low-frequency, so it varies over archipelagos rather than
    /// over individual islands. This is what turns an even scatter into
    /// crowds and open sky.
    /// </summary>
    public float Abundance(Vector3 at)
    {
        if (Clustering <= 0f)
            return 1f;

        // Two octaves only, and gain-boosted. Many-octave fbm regresses to its
        // mean — the extremes need most octaves to agree — and a field that
        // hovers near the middle everywhere is an even scatter with extra
        // steps. Few octaves swing wide, which is exactly what is wanted here.
        float n = DensityNoise.Fbm(at * (1f / ClusterScale), _seed + 9137, 2, 0.5f);
        float t = Mathf.Clamp(n * 1.9f * 0.5f + 0.5f, 0f, 1f);

        // A hard contrast curve rather than a smoothstep: this drives the
        // field to spend real time at BOTH ends, so some regions are crowded
        // and others genuinely empty, instead of everywhere being lukewarm.
        t = t < 0.5f
            ? 0.5f * Mathf.Pow(t * 2f, ClusterContrast)
            : 1f - 0.5f * Mathf.Pow((1f - t) * 2f, ClusterContrast);

        return Mathf.Lerp(1f, t, Clustering);
    }

    /// <summary>
    /// One candidate island: lattice cell plus a slot index, so a cell can
    /// hold several. Slot 0 is the ordinary one; higher slots only appear
    /// where the clustering field is generous, which is what lets islands
    /// crowd rather than keeping a lattice-imposed distance apart.
    /// </summary>
    private bool TrySlot(Vector3I cell, int slot, out Island island)
    {
        island = default;

        // The spawn anchor: this one cell always holds an island, centred on
        // the origin rather than jittered, so the player has ground under it.
        //
        // It lives HERE rather than in TryIslandAt because every enumeration
        // path — Near, NearBox and TryIslandAt alike — funnels through this
        // method. Putting it anywhere else leaves paths that never see it, and
        // the player spawns into empty sky.
        if (AnchorAtOrigin && cell == OriginCell)
        {
            // Only slot 0 is the anchor; the cell's other slots stay ordinary,
            // so the spawn island can still have neighbours crowding it.
            if (slot != 0)
                return TryOrdinarySlot(cell, slot, out island);

            float anchorDepth = AnchorRadius * DepthRatio
                * (0.7f + DensityNoise.Hash01(cell.X, cell.Y, cell.Z, _seed, 6u) * 0.7f);

            island = new Island(cell, Vector3.Zero, AnchorRadius, anchorDepth,
                AnchorRadius * 0.18f,
                DensityNoise.Hash01(cell.X, cell.Y, cell.Z, _seed, 7u));
            return true;
        }

        return TryOrdinarySlot(cell, slot, out island);
    }

    /// <summary>One candidate island from the ordinary placement rules, with
    /// no anchor special case.</summary>
    private bool TryOrdinarySlot(Vector3I cell, int slot, out Island island)
    {
        island = default;

        // Salts are offset per slot so a cell's second island is independent
        // of its first rather than a copy of it.
        uint salt = (uint)slot * 64u;

        // The cell's nominal centre decides how generous this region is. Using
        // the cell rather than the jittered position keeps every slot in a
        // cell agreeing about its own abundance.
        var nominal = new Vector3(
            (cell.X + 0.5f) * Spacing,
            (cell.Y + 0.5f) * Spacing,
            (cell.Z + 0.5f) * Spacing);

        float abundance = Abundance(nominal);

        // Extra slots are progressively harder to fill, so crowding is a
        // feature of generous regions rather than a uniform doubling.
        //
        // The falloff is on ABUNDANCE rather than a flat penalty, and it is
        // deliberately gentle where abundance is high: raising a near-1 value
        // to a power barely reduces it, so a genuinely generous region fills
        // most of its slots and reads as a dense cluster, while a middling one
        // still thins out fast. A steeper falloff left the extra slots almost
        // never used — measured 10 of 736 cells reaching three islands.
        float crowding = Mathf.Pow(abundance, 1f + slot * 1.35f);
        float threshold = Density * crowding;

        if (DensityNoise.Hash01(cell.X, cell.Y, cell.Z, _seed, 1u + salt) >= threshold)
            return false;

        // Jitter inside the cell so no island sits on the lattice and the
        // spacing reads as natural. Held inside 0.5 so islands cannot swap
        // cells, which would break the "only nearby cells matter" bound.
        float jx = DensityNoise.Hash01(cell.X, cell.Y, cell.Z, _seed, 2u + salt) - 0.5f;
        float jy = DensityNoise.Hash01(cell.X, cell.Y, cell.Z, _seed, 3u + salt) - 0.5f;
        float jz = DensityNoise.Hash01(cell.X, cell.Y, cell.Z, _seed, 4u + salt) - 0.5f;

        // Slots of one cell are pushed onto different sides of it. Their
        // jitters are independent, so without this two islands sharing a cell
        // can land almost on the same spot and merge into one blob instead of
        // reading as a cluster of distinct masses.
        if (slot > 0)
        {
            float spin = slot * 2.399963f; // golden angle, so slots fan out
            jx = Mathf.Clamp(jx * 0.45f + Mathf.Cos(spin) * 0.42f, -0.5f, 0.5f);
            jz = Mathf.Clamp(jz * 0.45f + Mathf.Sin(spin) * 0.42f, -0.5f, 0.5f);
        }

        var centre = new Vector3(
            (cell.X + 0.5f + jx * 0.98f) * Spacing,
            (cell.Y + 0.5f + jy * 0.98f * VerticalSpread) * Spacing,
            (cell.Z + 0.5f + jz * 0.98f) * Spacing);

        // Size follows a power law: many small islands, few large ones, which
        // is how real archipelagos and rubble fields read.
        //
        // Interpolated GEOMETRICALLY rather than linearly. A linear lerp
        // spreads its values evenly in absolute terms, so with a wide range
        // most islands still land large — the difference between 60 and 70
        // nodes gets as much of the distribution as the difference between 10
        // and 20, though only the latter is a visible change in KIND. Going
        // through the ratio instead gives every doubling of size equal weight,
        // which is what makes the tail long and the small islands common.
        float roll = DensityNoise.Hash01(cell.X, cell.Y, cell.Z, _seed, 5u + salt);
        float size = Mathf.Pow(roll, SizeBias);
        float radius = MinRadius * Mathf.Pow(MaxRadius / Mathf.Max(MinRadius, 0.01f), size);

        // Depth is proportional to radius with a per-island wobble.
        //
        // The wobble is wide on purpose, and skewed: most islands sit near the
        // middle of the range while a few run to either extreme, so the sky
        // holds stubby plates AND deep spires rather than one uniform
        // proportion repeated at different sizes. A narrow wobble is what made
        // every island read as the same shape scaled up and down.
        float depthRoll = DensityNoise.Hash01(cell.X, cell.Y, cell.Z, _seed, 6u + salt);
        depthRoll = 0.4f + Mathf.Pow(depthRoll, 1.6f) * 1.9f;
        float depth = radius * DepthRatio * depthRoll;

        // Depth scales with radius, so without a ceiling the largest islands
        // grow spikes hundreds of nodes long — taller than the generated
        // region, which clips them top and bottom into walls rather than
        // spires. The cap also bounds BoundingRadius, and with it how many
        // lattice cells every density query has to search.
        depth = Mathf.Min(depth, MaxDepth);

        // Rock above the centre line, so the flat top is not exactly at the
        // island's midpoint. Varied per island: a low value gives a slab whose
        // top sits near its widest point, a high one a mass that keeps rising
        // above its waist before the top cut takes it.
        float crown = 0.10f + DensityNoise.Hash01(cell.X, cell.Y, cell.Z, _seed, 8u + salt) * 0.34f;
        float topHeight = radius * crown;

        float variant = DensityNoise.Hash01(cell.X, cell.Y, cell.Z, _seed, 7u + salt);

        island = new Island(cell, centre, radius, depth, topHeight, variant, slot);
        return true;
    }

    /// <summary>
    /// Every island whose influence could reach `point`, appended to `into`.
    ///
    /// The search radius is the largest bounding radius any island can have,
    /// so no island is ever missed — a missed island is a hole in the world,
    /// while an extra candidate merely costs one distance test.
    /// </summary>
    public void Near(Vector3 point, List<Island> into)
    {
        into.Clear();

        int reach = Mathf.CeilToInt(MaxReach / Spacing);
        var centre = new Vector3I(
            Mathf.FloorToInt(point.X / Spacing),
            Mathf.FloorToInt(point.Y / Spacing),
            Mathf.FloorToInt(point.Z / Spacing));

        for (int dx = -reach; dx <= reach; dx++)
        {
            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dz = -reach; dz <= reach; dz++)
                {
                    var cell = new Vector3I(centre.X + dx, centre.Y + dy, centre.Z + dz);
                    for (int slot = 0; slot < MaxPerCell; slot++)
                    {
                        if (!TrySlot(cell, slot, out Island island))
                            break;

                        if (point.DistanceSquaredTo(island.Centre)
                            <= island.BoundingRadius * island.BoundingRadius)
                            into.Add(island);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every island whose influence could reach anywhere inside an
    /// axis-aligned box, appended to `into`.
    ///
    /// Generation works a chunk at a time, and gathering the candidates once
    /// per chunk rather than once per cell is the difference between a
    /// tractable build and an untenable one — the per-cell field evaluation
    /// then only walks this short list.
    /// </summary>
    public void NearBox(Vector3 min, Vector3 max, List<Island> into)
    {
        into.Clear();

        float reach = MaxReach;
        var lo = new Vector3I(
            Mathf.FloorToInt((min.X - reach) / Spacing),
            Mathf.FloorToInt((min.Y - reach) / Spacing),
            Mathf.FloorToInt((min.Z - reach) / Spacing));
        var hi = new Vector3I(
            Mathf.FloorToInt((max.X + reach) / Spacing),
            Mathf.FloorToInt((max.Y + reach) / Spacing),
            Mathf.FloorToInt((max.Z + reach) / Spacing));

        for (int x = lo.X; x <= hi.X; x++)
        {
            for (int y = lo.Y; y <= hi.Y; y++)
            {
                for (int z = lo.Z; z <= hi.Z; z++)
                {
                    for (int slot = 0; slot < MaxPerCell; slot++)
                    {
                        if (!TrySlot(new Vector3I(x, y, z), slot, out Island island))
                            break;

                        // Distance from the island's centre to the box, zero
                        // when the centre is inside it.
                        var closest = new Vector3(
                            Mathf.Clamp(island.Centre.X, min.X, max.X),
                            Mathf.Clamp(island.Centre.Y, min.Y, max.Y),
                            Mathf.Clamp(island.Centre.Z, min.Z, max.Z));

                        if (island.Centre.DistanceSquaredTo(closest)
                            <= island.BoundingRadius * island.BoundingRadius)
                            into.Add(island);
                    }
                }
            }
        }
    }
}
