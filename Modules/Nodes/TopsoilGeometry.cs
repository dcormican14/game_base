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
/// THE CORE
///
/// The central 2x2x2 is never touched. It is the node's guarantee that it
/// exists at all — whatever the field line says, a topsoil node can never be
/// carved away to nothing, and the surface can never open a hole where one
/// node happened to lose every contest. Exactly the role the core plays in
/// <see cref="RawNodeGeometry"/>, and for the same reason.
///
/// THE TWO HALVES, SPLIT BY THE FIELD LINE
///
/// The field line divides the node, rather than a fixed horizontal plane. The
/// half it points TOWARD faces into the rock, so that half is grown by the raw
/// rule — the same contests and the same rims, driven by the RAW field rather
/// than this one — and interlocks with the crystal it meets like a puzzle
/// piece.
///
/// The half it points AWAY from faces open air, and that is where the surface
/// is shaped. A sub-cell there is omitted once it lies far enough opposite the
/// field line: project its offset from the node's centre onto the reversed
/// field line and drop it past a threshold. A field line pointing straight
/// down removes the whole top layer; one pointing down and to the left removes
/// the top layer and a further step off the opposite corner. The cut is a
/// plane perpendicular to the field line, so what it leaves is a facet lying
/// across the slope.
///
/// WHY THIS ROLLS RATHER THAN STEPS
///
/// The field line comes from a smooth field, so it turns gradually across the
/// terrain. Neighbouring nodes cut at almost the same angle, and their facets
/// line up into a continuous surface following the hill — rather than the
/// staircase of independent per-node heights a per-column height rule gives.
///
/// NO NEIGHBOUR QUERIES
///
/// Every part of this is a pure function of the cell and the seed, which is
/// the <see cref="INodeType"/> contract. The rock-facing half agrees with its
/// neighbours because all of them run the same raw contest on the same lattice
/// features. The air-facing half agrees because the field is continuous and
/// shared. Nothing is ever read from the world.
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

    /// <summary>How finely each axis of the field line is quantised.</summary>
    public const int FlowSteps = 2;

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
        /// The field line, quantised to small integer components.
        ///
        /// Quantised rather than kept as a float vector so shapes repeat: a
        /// hillside uses a handful of distinct directions, each meshed once
        /// and reused for every node wearing it. Components run
        /// -FlowSteps..FlowSteps, fine enough that facets turn smoothly and
        /// coarse enough that the cache stays small.
        /// </summary>
        public readonly Vector3I Flow;

        /// <summary>
        /// Where the cut plane sits, in quarter-cells from this node's centre
        /// along the field line. Positive pushes it deeper, negative raises it.
        ///
        /// This is what makes the surface span nodes instead of restarting in
        /// each one. Without it every node cuts at the same place relative to
        /// ITSELF, so the facets are flat within a node and step at every
        /// boundary — measured, 1665 of 3944 adjacent sub-columns stepped.
        /// Offsetting by how much deeper the field says this node sits lets
        /// neighbouring facets meet at the same world height and join into one
        /// continuous surface.
        ///
        /// Quantised to whole quarter-cells because that is the resolution the
        /// geometry has; the shape cache keys on it, so a hillside still reuses
        /// a handful of meshes.
        /// </summary>
        public readonly int Phase;

        public Mask(RawNodeGeometry.Mask raw, Vector3I flow, int phase)
        {
            Raw = raw;
            Flow = flow;
            Phase = phase;
        }
    }

    // Shapes are cached per distinct mask, exactly as the raw type does.
    private static readonly Dictionary<(int, int), NodeMesh> _cache = new();
    private static readonly Dictionary<(int, int), int[]> _occupancyCache = new();
    private static readonly object _lock = new();

    /// <summary>How far either way the cut plane may be shifted.</summary>
    public const int MaxPhase = Sub;

    private static (int Raw, int FlowAndPhase) KeyOf(in Mask mask) => (
        mask.Raw.Corners << 12 | mask.Raw.Edges,
        ((mask.Flow.X + FlowSteps) * 25 + (mask.Flow.Y + FlowSteps) * 5
            + mask.Flow.Z + FlowSteps) * (MaxPhase * 2 + 1)
            + mask.Phase + MaxPhase);

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

        // THE CORE — the central 2x2x2, never contested and never carved. The
        // node's guarantee that it exists whatever the field line says.
        if (IsCore(i, j, k))
            return true;

        // Which side of the node this sub-cell is on, relative to the field
        // line. Positive lies along it, toward the rock.
        float along = Along(mask.Flow, i, j, k);

        // THE ROCK-FACING HALF — grown by the raw rule, including the rims it
        // pushes into neighbouring cells. Not an approximation of the crystal
        // it meets but the same shape function, so the two interlock exactly.
        if (along >= 0f)
            return RawNodeGeometry.Occupies(mask.Raw, i, j, k);

        // THE AIR-FACING HALF — the surface. Soil grows no rims, so it never
        // reaches outside its own cell here.
        if (i < 0 || i >= Sub || j < 0 || j >= Sub || k < 0 || k >= Sub)
            return false;

        // Space this node LOST is not its to keep, even on the surface side.
        //
        // A neighbouring raw node that wins an edge or corner grows its rim
        // into this cell, and it does so on the strength of this node having
        // vacated exactly that space. The raw rule encodes both halves of that
        // bargain: it fills what a node won and leaves empty what it lost.
        //
        // Applying only the cut here would honour the first half and ignore
        // the second — the soil would fill its whole footprint regardless of
        // contests, colliding with every rim that reaches in. Measured at 457
        // doubly-claimed sub-cells against plain rock, and 958 in a stack.
        //
        // So the surface half is the INTERSECTION of two rules: the space the
        // raw contests leave to this node, minus what the field line cuts
        // away. Tiling is preserved because the first rule is the same one
        // every neighbour runs.
        if (!RawNodeGeometry.Occupies(mask.Raw, i, j, k))
            return false;

        // Omit what lies far enough opposite the field line, with the plane
        // shifted by this node's phase so it lands at the same WORLD height as
        // its neighbours'. `along` is the signed projection already, so this is
        // a plane cut perpendicular to the field line — a facet lying across
        // the slope, joined to the facets either side of it.
        return -along + mask.Phase <= CutThreshold;
    }

    /// <summary>Is this the untouchable central 2x2x2?</summary>
    private static bool IsCore(int i, int j, int k)
    {
        const int lo = Sub / 2 - 1;
        const int hi = Sub / 2;
        return i >= lo && i <= hi && j >= lo && j <= hi && k >= lo && k <= hi;
    }

    /// <summary>
    /// How far a sub-cell lies along the field line, from the node's centre in
    /// quarter-cells.
    ///
    /// Positive is toward the rock the field line points into; negative toward
    /// the open air it points away from, which is the magnitude the cut
    /// threshold is compared against.
    /// </summary>
    private static float Along(Vector3I flow, int i, int j, int k)
    {
        // The node's centre sits BETWEEN sub-cells, so offsets are
        // half-integers and no sub-cell ever projects to exactly zero — there
        // is no cell sitting on the dividing plane whose side would have to be
        // broken arbitrarily.
        const float centre = (Sub - 1) * 0.5f;

        float fx = flow.X;
        float fy = flow.Y;
        float fz = flow.Z;

        float length = Mathf.Sqrt(fx * fx + fy * fy + fz * fz);
        if (length < 0.0001f)
        {
            // No direction at all. Treated as pointing straight down, which is
            // what flat ground means and what a node with no gradient should
            // look like.
            fy = -1f;
            length = 1f;
        }

        return ((i - centre) * fx + (j - centre) * fy + (k - centre) * fz) / length;
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
