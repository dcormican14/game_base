using Godot;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// The geometry of a TOPSOIL node: raw crystal below, a rounded contour above.
///
/// Topsoil is the skin between rock and sky, and it is built to read as a
/// transition rather than as a separate material laid on top. That means it
/// has to do two different things in the same cell:
///
/// THE LOWER HALF — INTERLOCK
///
/// The bottom two quarter-cell layers use exactly the raw node's rule, via the
/// same <see cref="RawNodeGeometry.Occupies"/> the rock below consults. Its
/// edge and corner contests are decided by the same field, so a topsoil node
/// and the raw node beneath it interlock like the puzzle pieces they are —
/// every rim that grows out of the rock is met by the matching recess in the
/// soil, with no seam and no z-fighting. This half is not an approximation of
/// the raw shape; it IS the raw shape, evaluated for this cell.
///
/// THE UPPER HALF — CONTOUR
///
/// The top two layers follow a smooth field instead, so the surface rounds
/// over the way soil settles rather than continuing the crystal's facets. The
/// field is sampled once per node and its GRADIENT quantised into a direction:
/// the soil is thickest where the field is high and thins toward where it is
/// low, which puts a one-sub-cell step running across the node perpendicular
/// to the gradient. Because the field is smooth and shared, that step lines up
/// with the step on the next node along, and a slope reads as a band of
/// contours marching across it — a topographic map drawn in quarter-cells.
///
/// WHY THIS NEEDS NO NEIGHBOUR QUERIES
///
/// Both halves are pure functions of the cell and the seed, which is the
/// <see cref="INodeType"/> contract and the thing that makes planet-scale
/// generation possible. The interlock half agrees with its neighbours because
/// every node touching a lattice feature runs the same contest and reaches the
/// same verdict. The contour half agrees because the field is continuous —
/// adjacent nodes sample points a cell apart and get almost the same gradient,
/// so their contours meet without either node having looked at the other.
///
/// THE SEAM WITH THE NODE ABOVE
///
/// One layer is genuinely shared: the top quarter-cell row of this node is
/// where the rims of the node above reach down. Both filling it is an overlap
/// that z-fights, and neither filling it is a hole.
///
/// It is arbitrated by computing what the cells above WOULD be shaped like —
/// the raw mask is a pure function of position, so that costs nothing but
/// arithmetic and requires no lookup of whether anything is actually there —
/// and ceding exactly the sub-cells they would claim. NINE cells, not one: a
/// corner win straddles a lattice corner, so the diagonal neighbours at y+1
/// reach into this layer too. This still satisfies the no-neighbour-queries
/// contract: nothing is read, only re-derived.
///
/// That is what makes stacking work. Soil under soil has the upper node's
/// rims reaching down to fill the seam, so the pair reads as continuous
/// ground; soil under air has nothing reaching down, so the contour is the
/// surface.
/// </summary>
public static class TopsoilGeometry
{
    /// <summary>Quarter-cells per node edge — the shared lattice.</summary>
    public const int Sub = RawNodeGeometry.Sub;

    /// <summary>
    /// How many quarter-cell layers at the bottom follow the raw interlock.
    /// Half the node, so the transition is genuinely half rock and half soil.
    /// </summary>
    public const int InterlockLayers = Sub / 2;

    /// <summary>
    /// One topsoil node's shape: the raw mask it interlocks with, plus which
    /// way its contour falls.
    /// </summary>
    public readonly struct Mask
    {
        /// <summary>The raw shape this node's lower half wears, so it meets
        /// the rock below with the same rims and recesses.</summary>
        public readonly RawNodeGeometry.Mask Raw;

        /// <summary>
        /// Which columns of the TOP quarter-cell layer are already claimed
        /// from above, as a 16-bit mask indexed i * Sub + k.
        ///
        /// The top layer is where the rims of the cells above reach down, and
        /// it is not only the cell directly above that reaches: a raw corner
        /// win takes the 2x2x2 straddling a lattice corner, so the nine cells
        /// at y+1 spanning dx,dz in -1..1 can all extend into it. Measured,
        /// arbitrating against only the cell directly above still collided on
        /// 272 sub-cells, every one of them with a DIAGONAL neighbour.
        ///
        /// Precomputed by the node type, which evaluates all nine raw masks as
        /// pure functions of position — no neighbour is looked up, only
        /// re-derived — and folded to a bitmask so the geometry cache stays
        /// keyed on something small.
        /// </summary>
        public readonly int CededTop;

        /// <summary>
        /// Which way the ground falls, as one of 8 compass directions, or -1
        /// where the field is flat enough that the top stays level.
        ///
        /// Quantised rather than kept as an angle so that shapes repeat: a
        /// handful of distinct contours covers a whole hillside, and each is
        /// meshed once and reused for every node wearing it.
        /// </summary>
        public readonly int Fall;

        /// <summary>
        /// How far through the contour band this node sits, 0 or 1.
        ///
        /// The step has to land somewhere, and putting it at the same height
        /// on every node of a slope would draw one long terrace. This is the
        /// field's own value quantised to the two sub-cell heights the top
        /// half can take, so the step walks up the hill as the field rises —
        /// which is what turns a set of steps into contour lines.
        /// </summary>
        public readonly int Band;

        public Mask(RawNodeGeometry.Mask raw, int cededTop, int fall, int band)
        {
            Raw = raw;
            CededTop = cededTop;
            Fall = fall;
            Band = band;
        }
    }

    /// <summary>The eight compass directions a slope can fall in, as
    /// quarter-cell steps across the node.</summary>
    private static readonly (int X, int Z)[] Compass =
    {
        (1, 0), (1, 1), (0, 1), (-1, 1),
        (-1, 0), (-1, -1), (0, -1), (1, -1),
    };

    // Shapes are cached per distinct mask, exactly as the raw type does: a
    // hillside uses a few contours over and over, so the mesh for each is
    // built once and reused for every node on it.
    private static readonly Dictionary<(int, int, int), NodeMesh> _cache = new();
    private static readonly Dictionary<(int, int, int), int[]> _occupancyCache = new();
    private static readonly object _lock = new();

    /// <summary>
    /// A cache key covering everything the shape depends on.
    ///
    /// Both raw masks are 20 bits, so the pair plus the contour needs more
    /// than an int; the two are hashed together instead. Collisions would
    /// hand back the wrong mesh, so the full masks are compared on lookup.
    /// </summary>
    private static (int Self, int Ceded, int Contour) KeyOf(in Mask mask) => (
        mask.Raw.Corners << 12 | mask.Raw.Edges,
        mask.CededTop,
        (mask.Fall + 1) << 1 | mask.Band);

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
    /// place the two halves are actually joined.
    /// </summary>
    public static bool Occupies(in Mask mask, int i, int j, int k)
    {
        if (i < RawNodeGeometry.Lo || i >= RawNodeGeometry.Hi
            || j < RawNodeGeometry.Lo || j >= RawNodeGeometry.Hi
            || k < RawNodeGeometry.Lo || k >= RawNodeGeometry.Hi)
            return false;

        // LOWER HALF — defer entirely to the raw rule, including the rims it
        // grows into neighbouring cells. Anything at or below the interlock
        // line is rock as far as shape is concerned.
        if (j < InterlockLayers)
            return RawNodeGeometry.Occupies(mask.Raw, i, j, k);

        // UPPER HALF — the contour. Only this node's own footprint: soil does
        // not grow rims, so it never reaches outside its cell here.
        if (i < 0 || i >= Sub || k < 0 || k >= Sub)
            return false;

        // THE TOP LAYER IS CONTESTED, AND THE CONTOUR CEDES IT.
        //
        // A node's raw interlock half reaches one quarter-cell BELOW its own
        // cell — that is what a corner or edge win straddling a lattice
        // feature means. So this node's top layer is exactly where the node
        // ABOVE reaches down into. Measured, letting both fill it collided on
        // 470 sub-cells in a two-deep stack, every one of them in this layer.
        //
        // The arbitration has to be decidable by each node alone, so it is the
        // raw contest again, asked about the lattice plane between the two
        // cells: whatever the node above would claim there, this node leaves
        // alone. Both nodes hash the same feature position and reach the same
        // verdict, so the layer is filled exactly once without either having
        // looked at the other.
        //
        // The result is what stacking should look like. Where soil sits under
        // soil, the upper node's rims reach down and fill the seam, so the
        // pair reads as continuous ground; where soil sits under air, nothing
        // reaches down and the contour is the surface, as it should be.
        if (j == Sub - 1 && CededToAbove(mask, i, k))
            return false;

        return j < TopHeight(mask, i, k);
    }

    /// <summary>
    /// Would the node directly above claim this node's top quarter-cell at
    /// column (i,k)?
    ///
    /// The node above wears its own raw mask, whose downward reach into this
    /// cell is decided by the four lattice edges and four lattice corners on
    /// the plane between them. This node cannot see that mask — it may not
    /// look at neighbours — but it does not need to: the reach only ever
    /// happens at the OUTER columns of the cell, since an edge or corner win
    /// straddles a boundary, and whether it happens at all is decided by a
    /// contest both nodes can evaluate.
    ///
    /// Rather than re-derive the neighbour's contest here (which would need
    /// the field, not just the mask), this yields the boundary columns
    /// unconditionally. That is the conservative direction: ceding a sub-cell
    /// the node above turns out not to claim leaves a one-quarter-cell notch
    /// that the node above's own flat underside covers anyway, whereas filling
    /// one it does claim is an overlap that z-fights.
    /// </summary>
    private static bool CededToAbove(in Mask mask, int i, int k) =>
        (mask.CededTop & 1 << (i * Sub + k)) != 0;

    /// <summary>
    /// How many quarter-cell layers of soil stand over column (i,k) of this
    /// node.
    ///
    /// Between InterlockLayers and Sub: the soil always covers the interlock
    /// half, and never exceeds the cell. The contour is the one-layer step
    /// between those two, placed by how far along the fall direction the
    /// column sits.
    /// </summary>
    private static int TopHeight(in Mask mask, int i, int k)
    {
        // Flat ground: the whole node is filled to the top, so a plateau reads
        // as a plateau rather than as noise.
        if (mask.Fall < 0)
            return Sub;

        (int fx, int fz) = Compass[mask.Fall];

        // How far this column lies down-slope, in quarter-cells from the
        // node's centre. Positive means downhill, where the soil thins.
        //
        // Measured from the centre rather than from an edge so the step falls
        // INSIDE the node: a step measured from the edge would put the whole
        // node at one height and turn the contour into a staircase of whole
        // cells rather than a line running through them.
        float centre = (Sub - 1) * 0.5f;
        float along = (i - centre) * fx + (k - centre) * fz;

        // Diagonal falls cover more ground per step, so their along-slope
        // distance is normalised — otherwise a diagonal contour would be
        // roughly 1.4x steeper than an axis-aligned one and the banding would
        // visibly change character with direction.
        if (fx != 0 && fz != 0)
            along *= 0.7071f;

        // The band offsets where the step sits, so successive nodes up a slope
        // put it at different heights and the steps join into a continuous
        // contour rather than repeating identically in every cell.
        float threshold = mask.Band == 0 ? 0f : -1f;

        // One sub-cell of relief: full height up-slope, one layer less down.
        return along > threshold ? Sub - 1 : Sub;
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
