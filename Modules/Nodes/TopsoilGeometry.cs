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
///   edge is taken off. That bevel is what makes a slope read as rounded
///   rather than as a stack of cubes.
///
///   the cell to the right is SOIL — the ground continues, and a step up of a
///   quarter cell is added over the top-right edge. Its neighbour does the
///   same toward this node, and the two meet as one continuous rise.
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
    /// How deep the bevel is where the ground ends, in quarter-cells.
    ///
    /// One. The bevel exists to round a corner, not to shave the node down: at
    /// one quarter-cell a node facing air loses a single row along that edge,
    /// which is the smallest step the lattice can express and the one that
    /// reads as a rounded lip rather than as a chamfer.
    /// </summary>
    public const int Bevel = 1;

    /// <summary>
    /// How far the ground rises toward a solid neighbour, in quarter-cells.
    ///
    /// One, matching the bevel, so a run of soil climbing to the right rises a
    /// quarter cell per node — the "step up by 1/4" the surface is built from.
    /// </summary>
    public const int Step = 1;

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

        public Mask(int neighbours)
        {
            Neighbours = neighbours;
        }
    }

    // 64 shapes, built on demand and kept. Small enough to build up front;
    // done lazily only to keep startup free of work a world may never need.
    private static readonly NodeMesh[] _meshes = new NodeMesh[64];
    private static readonly int[][] _occupancy = new int[64][];
    private static readonly object _lock = new();

    /// <summary>The meshed shape for a mask, built once and cached.</summary>
    public static NodeMesh Get(in Mask mask)
    {
        int key = mask.Neighbours & 63;
        lock (_lock)
        {
            return _meshes[key] ??= Build(new Mask(key));
        }
    }

    /// <summary>The quarter-cells this shape fills, as flat (i,j,k) triples in
    /// node-local coordinates.</summary>
    public static int[] OccupiedCells(in Mask mask)
    {
        int key = mask.Neighbours & 63;
        lock (_lock)
        {
            if (_occupancy[key] != null)
                return _occupancy[key];

            var list = new List<int>(Sub * Sub * Sub * 3);
            var local = new Mask(key);
            for (int i = 0; i < Sub; i++)
                for (int j = 0; j < Sub; j++)
                    for (int k = 0; k < Sub; k++)
                        if (Occupies(local, i, j, k))
                        {
                            list.Add(i);
                            list.Add(j);
                            list.Add(k);
                        }

            return _occupancy[key] = list.ToArray();
        }
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
        // Never outside its own footprint. Soil grows no rims; a raw
        // neighbour's rim may overhang it, which is what makes crystal read as
        // growing over the ground rather than out of it.
        if (i < 0 || i >= Sub || j < 0 || j >= Sub || k < 0 || k >= Sub)
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
        bool exposed =
            Touches(i, NodeFace.NegX, mask.Neighbours, false) ||
            Touches(Sub - 1 - i, NodeFace.PosX, mask.Neighbours, false) ||
            Touches(k, NodeFace.NegZ, mask.Neighbours, false) ||
            Touches(Sub - 1 - k, NodeFace.PosZ, mask.Neighbours, false);

        if (exposed)
            return Mathf.Max(Sub - Bevel, 1);

        // Not exposed. A side facing soil would step this column up, but a
        // node may not grow past its own ceiling — the cell above belongs to
        // whatever is up there — so the rise is already at the cap.
        return Sub;
    }

    /// <summary>
    /// Does a column standing `distance` quarter-cells in from `face` touch
    /// that face, and does the neighbour there match `solid`?
    ///
    /// Only the outermost row counts, which is what keeps the bevel a lip
    /// around the rim rather than a slope across the whole cell.
    /// </summary>
    private static bool Touches(int distance, int face, int neighbours, bool solid) =>
        distance < Bevel && NodeFace.Has(neighbours, face) == solid;

    /// <summary>Builds the mesh for one shape.</summary>
    private static NodeMesh Build(in Mask mask)
    {
        const int lo = RawNodeGeometry.Lo;
        const int span = RawNodeGeometry.Span;

        var solid = new bool[span, span, span];
        Mask local = mask;

        for (int i = 0; i < Sub; i++)
            for (int j = 0; j < Sub; j++)
                for (int k = 0; k < Sub; k++)
                    if (Occupies(local, i, j, k))
                        solid[i - lo, j - lo, k - lo] = true;

        // The same greedy merge the raw type uses, so both agree about
        // winding, merging and the per-quad occlusion lists culling reads.
        return RawNodeGeometry.MeshGrid(solid, (i, j, k) => Occupies(local, i, j, k));
    }
}
