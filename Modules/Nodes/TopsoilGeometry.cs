using Godot;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// The geometry of a TOPSOIL node: crystal where it meets rock, a rolling
/// surface where it meets air.
///
/// Topsoil is the skin between rock and sky, and it is shaped by one idea: a
/// FIELD LINE that always points INTO the surface. On a flat top it points
/// straight down; on a slope it points down and into the hill; on the side of
/// a mountain it points horizontally into the rock. Everything else follows
/// from which way that vector faces.
///
/// THE SURFACE IS SAMPLED, NOT CARVED
///
/// The soil's outer shape is the level set of the world's terrain field: each
/// sub-cell is filled when the field says that point is inside the ground.
/// Every node samples the SAME function at the SAME world positions, so a
/// sub-cell has exactly one answer whichever node asks — it cannot be claimed
/// twice, and it cannot be left unclaimed.
///
/// That is what an earlier version got wrong. It cut each node with a plane of
/// its own, which is a one-sided operation: carving a face left space the
/// neighbour had no reason to fill, and the joins between nodes opened into
/// holes — 565 of them between two soil layers. Material has to be DISPLACED
/// rather than removed. Sampling a shared field does that implicitly, because
/// the solid region is one sheet that happens to be divided among cells rather
/// than a set of cubes each carving itself.
///
/// THE RIMS ARE A PROMISE
///
/// The growth contests still run, and they are honoured in both directions: a
/// feature this node won is filled wherever its rim reaches, INCLUDING outside
/// its own cell and regardless of the surface, and a feature it lost is left
/// empty. The loser vacates precisely because the winner will fill it, so a
/// winner that declined on account of its own surface would leave a hole
/// nobody else could close — which is exactly what the stacked-soil voids
/// were.
///
/// NO CORE
///
/// Unlike <see cref="RawNodeGeometry"/> this keeps no untouchable centre. A
/// core exists there to stop contests carving a node away to nothing, and
/// contests do not shape soil. Forcing one here does the opposite of its job:
/// where the terrain field says a cell is entirely above ground, a mandatory
/// core leaves a 2x2x2 nub floating in the air with nothing attached to it.
///
/// Nothing is lost by dropping it. Which cells hold soil at all is the
/// generator's decision, and a cell the surface says is empty simply meshes to
/// nothing.
///
/// NO NEIGHBOUR QUERIES
///
/// Every part of this is a pure function of the cell and the seed, which is
/// the <see cref="INodeType"/> contract. Neighbours agree about contests
/// because all of them run the same contest on the same lattice features, and
/// about the surface because all of them sample the same field. Nothing is
/// ever read from the world.
/// </summary>
public static class TopsoilGeometry
{
    /// <summary>Quarter-cells per node edge — the shared lattice.</summary>
    public const int Sub = RawNodeGeometry.Sub;

    /// <summary>
    /// How far a sub-cell must lie opposite the field line before it is
    /// omitted, in quarter-cells along the reversed field line.
    ///
    /// 0.9 puts the cut just inside the layer of sub-cells whose centres sit
    /// 1.5 quarter-cells off the node's middle, so a field line pointing
    /// straight down removes exactly the top layer and no more. Lower carves
    /// the node away aggressively; higher leaves it nearly a full cube.
    /// </summary>
    public const float CutThreshold = 0.9f;

    /// <summary>
    /// One topsoil node's shape: the raw mask its rock-facing half wears, and
    /// the quantised field line that decides the rest.
    /// </summary>
    public readonly struct Mask
    {
        /// <summary>The raw shape the rock-facing half wears, so it meets the
        /// crystal with the same rims and recesses.</summary>
        public readonly RawNodeGeometry.Mask Raw;

        /// <summary>
        /// The surface, sampled: one bit per sub-cell of this node's own cell,
        /// set where the shared terrain field says the point is inside ground.
        /// Indexed i * Sub * Sub + j * Sub + k.
        ///
        /// This replaces cutting the node with a plane of its own. A plane
        /// anchored to each node re-centres the surface in every cell, so
        /// carving one node opened space its neighbours had no reason to fill
        /// — the voids. Sampling one WORLD field instead means every sub-cell
        /// has exactly one answer, whichever node asks: no two nodes can both
        /// claim it, and none can be left unclaimed. The solid region becomes
        /// a single connected sheet through the terrain rather than a set of
        /// independently carved cubes.
        ///
        /// 64 bits, so the whole node fits one ulong and the shape cache keys
        /// on it directly.
        /// </summary>
        public readonly ulong Surface;

        public Mask(RawNodeGeometry.Mask raw, ulong surface)
        {
            Raw = raw;
            Surface = surface;
        }
    }

    // Shapes are cached per distinct mask, exactly as the raw type does.
    private static readonly Dictionary<(int, ulong), NodeMesh> _cache = new();
    private static readonly Dictionary<(int, ulong), int[]> _occupancyCache = new();
    private static readonly object _lock = new();

    /// <summary>The bit index of a sub-cell within <see cref="Mask.Surface"/>.</summary>
    public static int SurfaceBit(int i, int j, int k) => (i * Sub + j) * Sub + k;

    private static (int Raw, ulong Surface) KeyOf(in Mask mask) => (
        mask.Raw.Corners << 12 | mask.Raw.Edges,
        mask.Surface);

    /// <summary>The meshed shape for a mask, built once and cached.</summary>
    public static NodeMesh Get(in Mask mask)
    {
        var key = KeyOf(mask);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out NodeMesh cached))
                return cached;

            NodeMesh built = Build(mask);
            _cache[key] = built;
            return built;
        }
    }

    /// <summary>The quarter-cells this shape fills, as flat (i,j,k) triples in
    /// node-local coordinates.</summary>
    public static int[] OccupiedCells(in Mask mask)
    {
        var key = KeyOf(mask);
        lock (_lock)
        {
            if (_occupancyCache.TryGetValue(key, out int[] cached))
                return cached;

            var list = new List<int>(96);
            for (int i = RawNodeGeometry.Lo; i < RawNodeGeometry.Hi; i++)
                for (int j = RawNodeGeometry.Lo; j < RawNodeGeometry.Hi; j++)
                    for (int k = RawNodeGeometry.Lo; k < RawNodeGeometry.Hi; k++)
                        if (Occupies(mask, i, j, k))
                        {
                            list.Add(i);
                            list.Add(j);
                            list.Add(k);
                        }

            int[] built = list.ToArray();
            _occupancyCache[key] = built;
            return built;
        }
    }

    /// <summary>
    /// Does this shape fill the quarter-cell at node-local (i,j,k)?
    ///
    /// The authority both the mesher and the world's culling consult, and the
    /// single place the field line's two consequences are applied.
    /// </summary>
    public static bool Occupies(in Mask mask, int i, int j, int k)
    {
        if (i < RawNodeGeometry.Lo || i >= RawNodeGeometry.Hi
            || j < RawNodeGeometry.Lo || j >= RawNodeGeometry.Hi
            || k < RawNodeGeometry.Lo || k >= RawNodeGeometry.Hi)
            return false;

        // SOIL GROWS NO RIMS, AND YIELDS NONE.
        //
        // It fills exactly its own cell, wherever the surface says solid, and
        // never reaches outside it. That is the contract PlainNode has always
        // kept: a type confined to its own footprint tiles space by
        // construction, and a raw neighbour's rim simply overhangs it. The
        // seam is honest — crystal grows over soil, which is what it should
        // look like.
        //
        // Entering the crystal contests was the mistake behind the divots.
        // Running the raw rule made every soil node wear a bismuth shape:
        // dumping the actual occupancy showed it matching the raw mask
        // exactly, hollowed to a 2x2 nub along every border it lost. The
        // surface field was deciding nothing, because a node below ground has
        // every surface bit set and the contests then had the last word.
        //
        // Soil is not crystal. It should not grow like it.
        if (i < 0 || i >= Sub || j < 0 || j >= Sub || k < 0 || k >= Sub)
            return false;

        return (mask.Surface & 1UL << SurfaceBit(i, j, k)) != 0UL;
    }

    /// <summary>Builds the mesh for one shape.</summary>
    private static NodeMesh Build(in Mask mask)
    {
        const int lo = RawNodeGeometry.Lo;
        const int span = RawNodeGeometry.Span;

        var solid = new bool[span, span, span];
        Mask local = mask;

        for (int i = lo; i < RawNodeGeometry.Hi; i++)
            for (int j = lo; j < RawNodeGeometry.Hi; j++)
                for (int k = lo; k < RawNodeGeometry.Hi; k++)
                    if (Occupies(local, i, j, k))
                        solid[i - lo, j - lo, k - lo] = true;

        // The same greedy merge the raw type uses, so both agree about
        // winding, merging and the per-quad occlusion lists culling reads.
        return RawNodeGeometry.MeshGrid(solid, (i, j, k) => Occupies(local, i, j, k));
    }
}
