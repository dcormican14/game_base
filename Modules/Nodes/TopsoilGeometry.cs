using Godot;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// The geometry of a TOPSOIL node: ground that rounds off where it meets the
/// sky and steps up where it meets more ground.
///
/// THE RULE, PER EDGE
///
/// Take the right-hand side of a node. Two cases, and only two:
///
///   the cell to the right is AIR — the ground ends here, so the top-right
///   edge falls away. Not as a single chamfered row: the bevel is GRADED,
///   dropping furthest against the face and stepping back up over
///   <see cref="BevelReach"/> rows. One row alone reads as a rounded corner,
///   which is the shape this had before; a graded one reads as a slope.
///
///   the cell to the right is SOIL — the ground continues, so that edge is
///   left alone at full height. Its neighbour does the same toward this node,
///   and the two meet flush. The step UP a slope is what the bevel on the
///   exposed side leaves behind: a node whose downhill side falls away and
///   whose uphill side does not is a stair tread.
///
/// The same applies on all four horizontal sides, so a node's shape is decided
/// by which of its neighbours hold ground.
///
/// WHY THIS AND NOT A FIELD
///
/// An earlier version sampled a smooth noise field and filled the sub-cells it
/// called solid. That field had no idea where the generator had actually put
/// the ground: measured over 2098 boundary nodes it left 387 of them with
/// fewer than eight of their sixty-four sub-cells filled — soil the generator
/// had placed, rendering as slivers or vanishing outright. The shape of a
/// surface has to follow the surface that exists, and the only thing that
/// knows where that is, is what the generator placed.
///
/// O(1), AND NO SEARCHING
///
/// The shape is a pure function of a six-bit neighbour mask, so it is one
/// array index per node and there is nothing to walk. The mesher already reads
/// those six neighbours to cull buried faces, so it hands over what it has and
/// nothing extra is fetched. Sixty-four masks is the entire geometry set.
///
/// THE CORNERS CARRY THE LINES ON
///
/// Faces alone are not enough. Where two soil nodes meet a diagonal that is
/// air, each of them bevels the edge running toward the node between them —
/// and that node has soil on all four faces, so a face-only rule leaves it a
/// full cube and the two cut lines stop dead at its boundary. That is the
/// clunky join.
///
/// So the mask carries the four horizontal diagonals as well, and a node cuts
/// the corner facing any diagonal that is air. The lines continue through it
/// instead of ending, and the nodes around a corner read as one surface.
///
/// A node with ground on every side, diagonals included, is genuinely interior
/// and stays a full cube — which is right. Ground that continues in all
/// directions has no feature to show, and an earlier attempt to give it one by
/// dimpling the top from a position hash just put a hole in every cube.
///
/// TILING
///
/// A node never reaches outside its own cell, which tiles by construction —
/// the contract <see cref="PlainNode"/> keeps. The step a node adds toward a
/// solid neighbour is added INSIDE its own footprint, and the neighbour adds
/// the matching step inside its own, so the two meet at the shared face
/// without either claiming the other's space.
/// </summary>
public static class TopsoilGeometry
{
    /// <summary>Quarter-cells per node edge — the shared lattice.</summary>
    public const int Sub = RawNodeGeometry.Sub;

    /// <summary>
    /// How far the bevel reaches into the node from an exposed side, in
    /// quarter-cells — and, because it is graded, how deep it cuts at the very
    /// edge.
    ///
    /// The bevel steps DOWN as it goes out: at reach 2 the row against the
    /// face drops two quarter-cells and the row behind it drops one, leaving
    /// the far half of the node at full height. That gradient is what reads as
    /// a slope.
    ///
    /// One was the first attempt and it only ever removed the outermost row,
    /// which on a four-tall node is a single chamfered edge — the shape looked
    /// rounded rather than sloped, because one step is not a gradient. Three
    /// grades the whole node and leaves nothing flat, so the terrain loses its
    /// terraces; two is the value that shows a slope and keeps a top.
    /// </summary>
    public const int BevelReach = 2;

    /// <summary>
    /// One topsoil node's shape: which of its six neighbours hold ground.
    ///
    /// That is the whole of it. Six bits, indexed by <see cref="NodeFace"/>,
    /// so there are 64 distinct topsoil shapes in a world and every node wears
    /// one of them.
    /// </summary>
    public readonly struct Mask
    {
        public readonly int Neighbours;

        /// <summary>
        /// Which sub-cells of this node the surrounding crystal already
        /// fills, one bit each.
        ///
        /// A raw rim grows UP out of the rock below into this node's floor.
        /// Soil filling its own footprint regardless puts two materials in the
        /// same sub-cell — measured at 1884 of them over a small block of soil
        /// on rock, every one in this row — and each then draws an outward face
        /// there, coplanar and facing the same way with air in front of both,
        /// so neither is culled and the two z-fight.
        ///
        /// NINE cells are consulted, not one. A corner win takes the 2x2x2
        /// straddling a lattice corner, so a rock node sitting diagonally below
        /// reaches up AND sideways into this floor; arbitrating against only
        /// the cell directly beneath still left 1059 sub-cells contested.
        ///
        /// Gathered by the node type, which recomputes each mask from its cell
        /// position — no neighbour is read, only re-derived — and folded to a
        /// bitmask so the geometry cache keys on something small.
        /// </summary>
        public readonly ulong Taken;

        /// <summary>
        /// The same, for the cell directly ABOVE — where a raised back grows.
        /// That row is outside this node own cell, so it needs its own
        /// arbitration or the bridging row collides with a rim up there.
        /// </summary>
        public readonly ulong TakenAbove;

        public Mask(int neighbours, ulong taken = 0UL, ulong takenAbove = 0UL)
        {
            Neighbours = neighbours;
            Taken = taken;
            TakenAbove = takenAbove;
        }
    }

    /// <summary>
    /// How far the back of a node is built up toward rising ground, in
    /// quarter-cells.
    ///
    /// One, deliberately less than <see cref="BevelReach"/>. Two was tried and
    /// overshoots: the node climbs as fast as the terrain does and the step
    /// simply moves rather than closing, leaving the same two-sub-cell jump a
    /// column further along. At one the profile across a staircase slope runs
    /// evenly — every adjacent pair of sub-columns differs by at most one.
    /// </summary>
    public const int RiseReach = 1;

    /// <summary>How many distinct neighbour masks there are: six faces, four
    /// horizontal diagonals, and four cells above the horizontal faces.</summary>
    public const int Masks = 1 << NodeFace.Count;

    // One shape per (neighbour mask, rock below) pair, built on demand.
    //
    // A dictionary rather than a flat array now: the pair spans far more
    // combinations than a world uses, and only the ones that actually occur
    // are ever built. In practice a world reuses a modest set, since the rock
    // below only matters through the handful of top features it can win.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (int, ulong, ulong), NodeMesh> _meshes = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (int, ulong, ulong), int[]> _occupancy = new();

    /// <summary>The meshed shape for a mask, built once and cached.</summary>
    public static NodeMesh Get(in Mask mask)
    {
        var key = KeyOf(mask);
        if (_meshes.TryGetValue(key, out NodeMesh cached))
            return cached;

        NodeMesh built = Build(mask);
        _meshes[key] = built;
        return built;
    }

    /// <summary>The bit index of a sub-cell within <see cref="Mask.Taken"/>.</summary>
    public static int CellBit(int i, int j, int k) => (i * Sub + j) * Sub + k;

    private static (int Neighbours, ulong Taken, ulong Above) KeyOf(in Mask mask) => (
        mask.Neighbours & (Masks - 1), mask.Taken, mask.TakenAbove);

    /// <summary>The quarter-cells this shape fills, as flat (i,j,k) triples in
    /// node-local coordinates.</summary>
    public static int[] OccupiedCells(in Mask mask)
    {
        var key = KeyOf(mask);
        if (_occupancy.TryGetValue(key, out int[] cached))
            return cached;

        var list = new List<int>(Sub * Sub * Sub * 3);
        Mask local = mask;
        for (int i = 0; i < Sub; i++)
            for (int j = 0; j < Sub + RiseReach; j++)
                for (int k = 0; k < Sub; k++)
                    if (Occupies(local, i, j, k))
                    {
                        list.Add(i);
                        list.Add(j);
                        list.Add(k);
                    }

        int[] built = list.ToArray();
        _occupancy[key] = built;
        return built;
    }

    /// <summary>
    /// Does this shape fill the quarter-cell at node-local (i,j,k)?
    ///
    /// The whole rule lives here: a column of the node is solid up to a height
    /// decided by which sides face ground, and nothing ever reaches outside the
    /// node's own cell.
    /// </summary>
    public static bool Occupies(in Mask mask, int i, int j, int k)
    {
        // Horizontally the node never leaves its own footprint. Soil grows no
        // rims sideways; a raw neighbour's rim may overhang it, which is what
        // makes crystal read as growing over the ground rather than out of it.
        if (i < 0 || i >= Sub || k < 0 || k >= Sub)
            return false;

        // Vertically it may reach a little ABOVE its cell, into the space a
        // raised back needs. That space is air — the node above is not soil,
        // or this one would be buried — so nothing else claims it.
        if (j < 0 || j >= Sub + RiseReach)
            return false;

        // THE FLOOR YIELDS TO THE ROCK BELOW.
        //
        // A crystal rim grows UP out of the node beneath, into this node's
        // bottom row. Soil filling its own footprint regardless puts two
        // materials in the same sub-cell, and each draws an outward face there
        // — coplanar, facing the same way, air in front of both — so neither
        // is culled and the two z-fight.
        //
        // The rock wins that space. It grew into it under the interlock rule,
        // which the whole world tiles by; the soil is the newcomer and simply
        // steps aside, so the crystal shows through and the soil silhouette
        // breaks at every raw boundary rather than hovering over it.
        //
        // Anywhere in the node: a rim reaches in from whichever direction
        // won the feature, not only from below.
        if (j < Sub && (mask.Taken & 1UL << CellBit(i, j, k)) != 0UL)
            return false;

        // The raised back sits ABOVE this node's own cell, outside the mask
        // above, and a rim from the rock beside the cell up there can reach
        // into it just the same. Left unchecked that collided 83 times on a
        // slope. Its own bit lives in TakenAbove.
        if (j >= Sub && (mask.TakenAbove & 1UL << CellBit(i, j - Sub, k)) != 0UL)
            return false;

        // THE RAISED BACK NEVER LEAVES AN OCCUPIED CELL.
        //
        // Rows at or above Sub sit in the cell ABOVE this one, which the node
        // may only borrow while that cell is empty. With ground up there the
        // space is already owned, and taking it anyway had every buried soil
        // node overflow a row into its neighbour � 60252 doubly-claimed
        // sub-cells over a block of real terrain, every one of them a node's
        // floor row shared with the node beneath it.
        //
        // Checked before the buried short-circuit below, which returns solid
        // for the whole range and would otherwise fill this row regardless.
        if (j >= Sub && NodeFace.Has(mask.Neighbours, NodeFace.PosY))
            return false;

        // With ground above, this node is buried and has no surface to shape.
        // Filling solid is what makes a stack of soil read as one mass rather
        // than as layers with a lid on each.
        if (NodeFace.Has(mask.Neighbours, NodeFace.PosY))
            return true;

        return j < TopHeight(mask, i, k);
    }

    /// <summary>
    /// How many quarter-cell layers of soil stand over column (i,k).
    ///
    /// A full node, minus a bevel wherever the column touches a side that
    /// faces open air. A column in a corner touches two sides, and one of them
    /// facing air is enough to bevel it — see below for why that asymmetry is
    /// the point.
    /// </summary>
    private static int TopHeight(in Mask mask, int i, int k)
    {
        // EXPOSURE WINS. A column is bevelled if ANY side it touches faces
        // open air, whatever the other sides say.
        //
        // Applying the sides in turn, each nudging the height, was the bug
        // behind the row of small divots. Along a hill edge every node has air
        // in front and soil to both sides, so its front-middle columns
        // bevelled to 3 while its front-CORNER columns bevelled to 3 and were
        // then stepped straight back to 4 by the soil beside them. The lip
        // came out 4-3-3-4 instead of running level, and a row of those reads
        // as a line of dimples rather than one continuous edge.
        //
        // A bevel marks an edge that is open to the sky. Nothing on another
        // side can close it, so the two effects are decided independently and
        // exposure takes precedence.
        int drop = 0;
        drop = Mathf.Max(drop, DropFrom(i, NodeFace.NegX, mask.Neighbours));
        drop = Mathf.Max(drop, DropFrom(Sub - 1 - i, NodeFace.PosX, mask.Neighbours));
        drop = Mathf.Max(drop, DropFrom(k, NodeFace.NegZ, mask.Neighbours));
        drop = Mathf.Max(drop, DropFrom(Sub - 1 - k, NodeFace.PosZ, mask.Neighbours));

        // THE CORNERS, so a neighbour's cut line does not stop at this node.
        //
        // Where two soil nodes meet a diagonal that is air, each of them
        // bevels the edge running toward the node between them. That node has
        // soil on all four FACES, so the face rule above leaves it a full cube
        // and the two lines arriving at its corner stop dead — which is the
        // clunky join.
        //
        // Cutting the corner continues them. The distance used is the LARGER
        // of the two axis distances, so what comes off is a corner rather than
        // a stripe across the node: only columns near both faces are touched,
        // and the cut shrinks inward exactly as the face bevels do.
        drop = Mathf.Max(drop,
            DropFrom(Mathf.Max(i, k), NodeFace.NegXNegZ, mask.Neighbours));
        drop = Mathf.Max(drop,
            DropFrom(Mathf.Max(i, Sub - 1 - k), NodeFace.NegXPosZ, mask.Neighbours));
        drop = Mathf.Max(drop,
            DropFrom(Mathf.Max(Sub - 1 - i, k), NodeFace.PosXNegZ, mask.Neighbours));
        drop = Mathf.Max(drop,
            DropFrom(Mathf.Max(Sub - 1 - i, Sub - 1 - k), NodeFace.PosXPosZ, mask.Neighbours));

        // The deepest cut wins, so a column in an outer corner falls away in
        // both directions at once rather than the two sides fighting over it.
        //
        // Never below one layer while the node holds soil: the generator
        // placed it because there is ground here, and a node bevelled away on
        // every side would leave a hole in the surface.
        //
        // Not exposed at all means full height. A side facing soil would step
        // the column up, but a node may not grow past its own ceiling — the
        // cell above belongs to whatever is up there — so the rise is already
        // at the cap.
        int height = Mathf.Max(Sub - drop, 1);

        // THE RAISED BACK, bridging the step up a slope.
        //
        // Bevelling alone makes a staircase of cliffs. On ground climbing to
        // the +X, every node bevels its downhill edge and stops flat at its
        // own ceiling, so the profile runs 2,3,4,4 and then jumps to 6 at the
        // next node — a two-sub-cell cliff at every boundary.
        //
        // The material the bevel took off has to go somewhere, and uphill is
        // where it belongs. A side whose ground rises builds its back up
        // instead of stopping flat, so the profile becomes 2,3,4,5 and meets
        // the neighbour's 6 with a single step. Measured across three nodes,
        // the largest step between adjacent sub-columns falls from 2 to 1.
        //
        // A side RISES when its neighbour is solid AND the cell above that
        // neighbour is solid too. The neighbour alone only says the ground
        // continues; the cell above it says the ground continues higher.
        int rise = 0;
        rise = Mathf.Max(rise, RiseFrom(i, NodeFace.NegX, NodeFace.UpNegX, mask.Neighbours));
        rise = Mathf.Max(rise,
            RiseFrom(Sub - 1 - i, NodeFace.PosX, NodeFace.UpPosX, mask.Neighbours));
        rise = Mathf.Max(rise, RiseFrom(k, NodeFace.NegZ, NodeFace.UpNegZ, mask.Neighbours));
        rise = Mathf.Max(rise,
            RiseFrom(Sub - 1 - k, NodeFace.PosZ, NodeFace.UpPosZ, mask.Neighbours));

        // Only where the bevel has not already cut this column. A column that
        // is falling away toward air is not also climbing toward a rise, and
        // adding to it would fill in the very lip the bevel just opened.
        //
        // And never with ground directly overhead. The rise reaches one
        // quarter-cell above this node's own cell, which is free only while
        // that cell is air; growing into a node that is actually there put 255
        // sub-cells in contention on a slope. A node with something above it
        // is buried anyway and has no surface to bridge.
        if (drop == 0 && !NodeFace.Has(mask.Neighbours, NodeFace.PosY))
            height += rise;

        return height;
    }

    /// <summary>
    /// How far one side lifts a column standing `distance` quarter-cells in
    /// from that face.
    ///
    /// Only when the ground that way genuinely steps UP — the neighbour solid
    /// and the cell above it solid as well. Zero otherwise, and zero beyond
    /// <see cref="RiseReach"/>, so what is added is a lip along the uphill
    /// edge rather than a ramp across the whole node.
    /// </summary>
    private static int RiseFrom(int distance, int face, int above, int neighbours)
    {
        if (distance >= RiseReach)
            return 0;

        if (!NodeFace.Has(neighbours, face) || !NodeFace.Has(neighbours, above))
            return 0;

        return RiseReach - distance;
    }

    /// <summary>
    /// How far one side pulls a column down, for a column standing `distance`
    /// quarter-cells in from that face.
    ///
    /// Zero when the side faces ground, or when the column is further in than
    /// the bevel reaches. Otherwise it grades: deepest against the face and
    /// one quarter-cell shallower per row inward, which is the gradient that
    /// makes the edge read as a slope instead of a chamfer.
    /// </summary>
    private static int DropFrom(int distance, int face, int neighbours)
    {
        if (distance >= BevelReach || NodeFace.Has(neighbours, face))
            return 0;

        return BevelReach - distance;
    }

    /// <summary>Builds the mesh for one shape.</summary>
    private static NodeMesh Build(in Mask mask)
    {
        const int lo = RawNodeGeometry.Lo;
        const int span = RawNodeGeometry.Span;

        var solid = new bool[span, span, span];
        Mask local = mask;

        // Up to Sub + RiseReach in y: a raised back reaches above the node's
        // own cell, and stopping at Sub would drop exactly the row that
        // bridges the step.
        for (int i = 0; i < Sub; i++)
            for (int j = 0; j < Sub + RiseReach; j++)
                for (int k = 0; k < Sub; k++)
                    if (Occupies(local, i, j, k))
                        solid[i - lo, j - lo, k - lo] = true;

        // The same greedy merge the raw type uses, so both agree about
        // winding, merging and the per-quad occlusion lists culling reads.
        return RawNodeGeometry.MeshGrid(solid, (i, j, k) => Occupies(local, i, j, k));
    }
}
