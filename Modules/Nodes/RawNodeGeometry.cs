using Godot;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// The geometry of a RAW node: a tiered bismuth crystal, solved without ever
/// looking at a neighbouring node.
///
/// This is the shape solver behind <see cref="RawNode"/>. It knows nothing
/// about the world grid or meshing — it turns a shape mask into occupancy and
/// quads, and caches both per distinct mask.
///
/// WHAT MOVES: EDGES AND CORNERS, NOT FACES
///
/// Real bismuth grows fastest where the most free space meets — along edges
/// and especially at corners — which is why a hopper crystal has raised rims
/// around recessed faces. So the displacement here lives on the 12 edges and
/// 8 corners of a node. Flat faces stay put; the rim around them steps out or
/// pulls back. A corner reaches twice as far as an edge, because three
/// directions of free space meet there rather than two.
///
/// THE INTERLOCK RULE — why nodes fit like puzzle pieces
///
/// A lattice edge is shared by 4 nodes; a lattice corner by 8. Rather than
/// each node deciding independently how far to grow (which would collide or
/// leave gaps), the contested region around each lattice feature is awarded
/// WHOLE to a single owner, chosen by hashing that feature's position. The
/// winner fills it; the losers vacate it. Nothing is created and nothing is
/// destroyed, so space stays exactly tiled — gaplessness is structural, not
/// something the mesher checks for.
///
/// Every node touching a feature computes the same hash from the same
/// position and reaches the same verdict, so a node never has to ask what is
/// next to it. That is the property that makes whole planets affordable: a
/// node dropped into empty space mid-game gets exactly the shape it would
/// have had if the planet had generated it.
///
/// THE SUBDIVISION
///
/// Each cell is 4x4x4 quarter-cells, classified by how many coordinates sit on
/// a border (0 or 3):
///   - 0 borders — the central 2x2x2 CORE. Always solid, never contested. It
///     is the "present at the very least" region, and it is what guarantees a
///     node can never be carved away to nothing.
///   - 1 border — FACE cells. Left alone, so flat faces stay flat.
///   - 2 borders — EDGE cells, awarded per lattice edge.
///   - 3 borders — CORNER cells, awarded per lattice corner.
///
/// A winner's territory extends OUTSIDE its own cell, into the space the
/// losers vacated: an edge win takes a 2x2 run straddling the lattice edge, a
/// corner win takes the whole 2x2x2 node straddling the lattice corner. That
/// straddling is what gives corners their double reach.
///
/// COST
///
/// A node's shape is 12 edge bits plus 8 corner bits — 2^20 combinations, far
/// too many to tabulate. But the regions are independent and additive, so the
/// occupancy is built from the bitmask directly (a few dozen writes into a
/// 6x6x6 grid) and meshed with a greedy merge. Small, allocation-free per
/// node, and no dependence on neighbours.
/// </summary>
public static class RawNodeGeometry
{
    /// <summary>Quarter-cells per node edge. The 4x4x4 subdivision.</summary>
    public const int Sub = 4;

    /// <summary>A raw node's shape: which contests it won.</summary>
    public readonly struct Mask
    {
        /// <summary>Bit c set = this node won its local corner c, where c
        /// packs (xHigh, yHigh, zHigh) as bits 0,1,2.</summary>
        public readonly int Corners;

        /// <summary>Bit e set = this node won its local edge e. Edges are
        /// indexed axis * 4 + (uHigh | vHigh &lt;&lt; 1), where axis is the
        /// direction the edge runs along.</summary>
        public readonly int Edges;

        public Mask(int corners, int edges)
        {
            Corners = corners;
            Edges = edges;
        }

        public override int GetHashCode() => Corners * 4096 ^ Edges;
    }

    // Shapes are cached on demand rather than enumerated: 2^20 combinations
    // exist in principle, but a given world uses a small, repeating subset.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, NodeMesh>
        _cache = new();

    // Occupied quarter-cells per shape, cached alongside the meshes so the
    // world's occupancy set can be stamped in without re-deriving ownership.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int[]>
        _occupancyCache = new();

    /// <summary>
    /// The quarter-cells this shape occupies, as flat (i,j,k) triples in
    /// node-local coordinates spanning -1..Sub. Built once per distinct
    /// shape and cached.
    /// </summary>
    public static int[] OccupiedCells(Mask mask)
    {
        int key = mask.Corners << 12 | mask.Edges;

        // Read without a lock. This is asked once per node of every chunk
        // meshed — 46656 times for one chunk's occupancy window alone — and
        // taking a lock on each was a measurable share of the cost of a single
        // mined node. Two threads racing to build the same shape both get the
        // right answer and one of them wins the write, which is harmless: the
        // value is a pure function of the key.
        if (_occupancyCache.TryGetValue(key, out int[] cached))
            return cached;

        var list = new List<int>(96);
        for (int i = Lo; i < Hi; i++)
            for (int j = Lo; j < Hi; j++)
                for (int k = Lo; k < Hi; k++)
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

    /// <summary>The meshed shape for a mask, built once and cached.</summary>
    public static NodeMesh Get(Mask mask)
    {
        int key = mask.Corners << 12 | mask.Edges;
        if (_cache.TryGetValue(key, out NodeMesh cached))
            return cached;

        NodeMesh built = Build(mask);
        _cache[key] = built;
        return built;
    }

    /// <summary>Local corner index from its three high/low bits.</summary>
    public static int CornerIndex(bool xHigh, bool yHigh, bool zHigh) =>
        (xHigh ? 1 : 0) | (yHigh ? 2 : 0) | (zHigh ? 4 : 0);

    /// <summary>Floor division, so negative coordinates map to the node below
    /// rather than truncating toward zero.</summary>
    public static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        return value % divisor != 0 && (value < 0) != (divisor < 0) ? q - 1 : q;
    }

    /// <summary>
    /// Does the node wearing `mask` occupy the quarter-cell at node-local
    /// (i,j,k)? Coordinates outside -1..Sub are never occupied, since a win
    /// reaches at most one quarter-cell beyond the node.
    ///
    /// This is the authority the mesher consults to decide whether a quad is
    /// really buried. It re-derives the same ownership rule Build() uses.
    /// </summary>
    public static bool Occupies(Mask mask, int i, int j, int k)
    {
        if (i < Lo || i >= Hi || j < Lo || j >= Hi || k < Lo || k >= Hi)
            return false;

        bool inside = i >= 0 && i < Sub && j >= 0 && j < Sub && k >= 0 && k < Sub;
        if (inside && BorderCount(i, j, k) <= 1)
            return true; // core and faces, never contested

        // Edge wins: the middle run straddling a lattice edge.
        for (int axis = 0; axis < 3; axis++)
        {
            int uAxis = axis == 0 ? 1 : 0;
            int vAxis = axis == 2 ? 1 : 2;
            int t = axis == 0 ? i : axis == 1 ? j : k;
            if (t < 1 || t > Sub - 2)
                continue;

            int u = uAxis == 0 ? i : uAxis == 1 ? j : k;
            int v = vAxis == 0 ? i : vAxis == 1 ? j : k;
            for (int e = 0; e < 4; e++)
            {
                bool uHigh = (e & 1) != 0, vHigh = (e & 2) != 0;
                if ((mask.Edges & 1 << EdgeIndex(axis, uHigh, vHigh)) == 0)
                    continue;
                int uBase = uHigh ? Sub - 1 : 0, vBase = vHigh ? Sub - 1 : 0;
                bool uHit = uHigh ? u == uBase || u == uBase + 1 : u == uBase || u == uBase - 1;
                bool vHit = vHigh ? v == vBase || v == vBase + 1 : v == vBase || v == vBase - 1;
                if (uHit && vHit)
                    return true;
            }
        }

        // Corner wins: the 2x2x2 straddling a lattice corner.
        for (int c = 0; c < 8; c++)
        {
            if ((mask.Corners & 1 << c) == 0)
                continue;
            bool xHigh = (c & 1) != 0, yHigh = (c & 2) != 0, zHigh = (c & 4) != 0;
            int xBase = xHigh ? Sub - 1 : 0, yBase = yHigh ? Sub - 1 : 0, zBase = zHigh ? Sub - 1 : 0;
            bool xHit = xHigh ? i == xBase || i == xBase + 1 : i == xBase || i == xBase - 1;
            bool yHit = yHigh ? j == yBase || j == yBase + 1 : j == yBase || j == yBase - 1;
            bool zHit = zHigh ? k == zBase || k == zBase + 1 : k == zBase || k == zBase - 1;
            if (xHit && yHit && zHit)
                return true;
        }

        return false;
    }

    /// <summary>Local edge index: the axis it runs along, plus which side it
    /// sits on in the other two axes.</summary>
    public static int EdgeIndex(int axis, bool uHigh, bool vHigh) =>
        axis * 4 + ((uHigh ? 1 : 0) | (vHigh ? 2 : 0));

    // Occupancy grid spans -1..Sub on each axis, so wins reaching one
    // quarter-cell outside the node have somewhere to land.

    /// <summary>Lowest quarter-cell index a shape may fill.</summary>
    public const int Lo = -1;

    /// <summary>One past the highest quarter-cell index a shape may fill.</summary>
    public const int Hi = Sub + 1;

    /// <summary>Width of the occupancy grid, in quarter-cells.</summary>
    public const int Span = Hi - Lo;

    private static NodeMesh Build(Mask mask)
    {
        var solid = new bool[Span, Span, Span];

        void Set(int i, int j, int k)
        {
            if (i < Lo || i >= Hi || j < Lo || j >= Hi || k < Lo || k >= Hi)
                return;
            solid[i - Lo, j - Lo, k - Lo] = true;
        }

        bool At(int i, int j, int k)
        {
            if (i < Lo || i >= Hi || j < Lo || j >= Hi || k < Lo || k >= Hi)
                return false;
            return solid[i - Lo, j - Lo, k - Lo];
        }

        // --- Core and faces: everything with at most one border coordinate.
        // Never contested, so it is unconditionally the node's own.
        for (int i = 0; i < Sub; i++)
        {
            for (int j = 0; j < Sub; j++)
            {
                for (int k = 0; k < Sub; k++)
                {
                    if (BorderCount(i, j, k) <= 1)
                        Set(i, j, k);
                }
            }
        }

        // --- Edge wins: the winner takes the full 2x2 run straddling the
        // lattice edge, half of which lies outside its own cell.
        for (int axis = 0; axis < 3; axis++)
        {
            for (int corner = 0; corner < 4; corner++)
            {
                if ((mask.Edges & 1 << EdgeIndex(axis, (corner & 1) != 0, (corner & 2) != 0)) == 0)
                    continue;

                bool uHigh = (corner & 1) != 0;
                bool vHigh = (corner & 2) != 0;
                int uAxis = axis == 0 ? 1 : 0;
                int vAxis = axis == 2 ? 1 : 2;

                // Straddle: the two quarter-cells either side of the boundary
                // on each of the two cross axes.
                //
                // The run covers only the MIDDLE of the edge (t = 1..Sub-2).
                // The cells at each end belong to the two lattice corners that
                // terminate this edge, and those are decided by their own
                // contests. Letting the edge run the full length double-claims
                // them, which is an overlap wherever a node wins an edge but
                // loses the corner beside it.
                int uBase = uHigh ? Sub - 1 : 0;
                int vBase = vHigh ? Sub - 1 : 0;
                for (int du = 0; du < 2; du++)
                {
                    for (int dv = 0; dv < 2; dv++)
                    {
                        for (int t = 1; t < Sub - 1; t++)
                        {
                            var c = new int[3];
                            c[axis] = t;
                            c[uAxis] = uBase + (uHigh ? du : -du);
                            c[vAxis] = vBase + (vHigh ? dv : -dv);
                            Set(c[0], c[1], c[2]);
                        }
                    }
                }
            }
        }

        // --- Corner wins: the winner takes the whole 2x2x2 straddling the
        // lattice corner — seven of its eight quarter-cells lie outside the
        // node, which is the double reach corners are meant to have.
        for (int corner = 0; corner < 8; corner++)
        {
            if ((mask.Corners & 1 << corner) == 0)
                continue;

            bool xHigh = (corner & 1) != 0;
            bool yHigh = (corner & 2) != 0;
            bool zHigh = (corner & 4) != 0;
            int xBase = xHigh ? Sub - 1 : 0;
            int yBase = yHigh ? Sub - 1 : 0;
            int zBase = zHigh ? Sub - 1 : 0;

            for (int dx = 0; dx < 2; dx++)
            {
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dz = 0; dz < 2; dz++)
                    {
                        Set(xBase + (xHigh ? dx : -dx),
                            yBase + (yHigh ? dy : -dy),
                            zBase + (zHigh ? dz : -dz));
                    }
                }
            }
        }

        return MeshGrid(solid, At);
    }

    /// <summary>How many of a quarter-cell's coordinates sit on a border.</summary>
    private static int BorderCount(int i, int j, int k)
    {
        int n = 0;
        if (i == 0 || i == Sub - 1) n++;
        if (j == 0 || j == Sub - 1) n++;
        if (k == 0 || k == Sub - 1) n++;
        return n;
    }

    /// <summary>Whether a shape fills one quarter-cell, in node-local
    /// coordinates. What the greedy mesher walks.</summary>
    public delegate bool Occupancy(int i, int j, int k);

    /// <summary>
    /// Greedy-merges coplanar quarter-faces into the largest rectangles that
    /// share a direction, a slice and a hidden-by tag. A flat face is 4x4
    /// quarter-cells that would otherwise ship as 16 quads; merged it is one.
    /// Paid once per distinct shape, then reused for every node wearing it.
    /// </summary>
    /// <summary>
    /// Greedy-meshes any occupancy grid on the shared quarter-cell lattice.
    ///
    /// Public because every node type that grows outside its own cell needs
    /// exactly this: the merge, the outward winding, and the per-quad
    /// occlusion lists the world's culling reads. A type that re-implemented
    /// it would drift from the raw type's conventions and tear seams where the
    /// two meet.
    /// </summary>
    public static NodeMesh MeshGrid(bool[,,] solid, Occupancy At)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<int>();
        var occludedCells = new List<int>();
        var occludedStart = new List<int>();

        for (int d = 0; d < 6; d++)
        {
            int di = d == 0 ? -1 : d == 1 ? 1 : 0;
            int dj = d == 2 ? -1 : d == 3 ? 1 : 0;
            int dk = d == 4 ? -1 : d == 5 ? 1 : 0;
            int axis = di != 0 ? 0 : dj != 0 ? 1 : 2;
            int step = di != 0 ? di : dj != 0 ? dj : dk;
            int uAxis = axis == 0 ? 1 : 0;
            int vAxis = axis == 2 ? 1 : 2;

            for (int slice = Lo; slice < Hi; slice++)
            {
                var mask = new int[Span, Span];
                bool any = false;
                for (int u = 0; u < Span; u++)
                {
                    for (int v = 0; v < Span; v++)
                    {
                        mask[u, v] = int.MinValue;
                        var cell = new int[3];
                        cell[axis] = slice;
                        cell[uAxis] = u + Lo;
                        cell[vAxis] = v + Lo;
                        if (!At(cell[0], cell[1], cell[2]))
                            continue;
                        if (At(cell[0] + di, cell[1] + dj, cell[2] + dk))
                            continue;

                        // Every quad in a slice merges with its neighbours:
                        // occlusion is resolved per quarter-cell at mesh time
                        // against real world occupancy, so nothing here needs
                        // to be kept separate for culling purposes. Splitting
                        // the mask on a bake-time occluder guess (as an
                        // earlier version did) only fragments the merge and
                        // multiplies the triangle count.
                        mask[u, v] = 0;
                        any = true;
                    }
                }

                if (!any)
                    continue;

                for (int u = 0; u < Span; u++)
                {
                    for (int v = 0; v < Span; v++)
                    {
                        int tag = mask[u, v];
                        if (tag == int.MinValue)
                            continue;

                        int height = 1;
                        while (v + height < Span && mask[u, v + height] == tag)
                            height++;

                        int width = 1;
                        while (u + width < Span)
                        {
                            bool wholeRow = true;
                            for (int h = 0; h < height; h++)
                            {
                                if (mask[u + width, v + h] != tag)
                                {
                                    wholeRow = false;
                                    break;
                                }
                            }

                            if (!wholeRow)
                                break;
                            width++;
                        }

                        for (int w = 0; w < width; w++)
                            for (int h = 0; h < height; h++)
                                mask[u + w, v + h] = int.MinValue;

                        // Record every quarter-cell in front of this merged
                        // rectangle. Recorded for EVERY quad, not only ones a
                        // bake-time tag guessed were occludable: a rim from an
                        // edge or corner win reaches diagonally, so the node
                        // that ends up burying a quad is often not the face
                        // neighbour such a tag would name. The mesher asks the
                        // world what is actually solid there instead.
                        occludedStart.Add(occludedCells.Count);
                        for (int w = 0; w < width; w++)
                        {
                            for (int h = 0; h < height; h++)
                            {
                                var oc = new int[3];
                                oc[axis] = slice + (di != 0 ? di : dj != 0 ? dj : dk);
                                oc[uAxis] = u + Lo + w;
                                oc[vAxis] = v + Lo + h;
                                occludedCells.Add(oc[0]);
                                occludedCells.Add(oc[1]);
                                occludedCells.Add(oc[2]);
                            }
                        }

                        AddMergedFace(vertices, normals, indices,
                            axis, uAxis, vAxis, slice, u + Lo, v + Lo, width, height,
                            di, dj, dk);
                    }
                }
            }
        }

        occludedStart.Add(occludedCells.Count);

        return new NodeMesh
        {
            Vertices = vertices.ToArray(),
            Normals = normals.ToArray(),
            Indices = indices.ToArray(),
            OccludedCells = occludedCells.ToArray(),
            OccludedStart = occludedStart.ToArray(),
        };
    }

    /// <summary>
    /// One merged rectangle spanning `width` x `height` quarter-cells in the
    /// face's own plane, wound to point outward along (di,dj,dk).
    /// </summary>
    private static void AddMergedFace(List<Vector3> vertices, List<Vector3> normals,
        List<int> indices,
        int axis, int uAxis, int vAxis, int slice, int u, int v, int width, int height,
        int di, int dj, int dk)
    {
        var normal = new Vector3(di, dj, dk);
        int step = di != 0 ? di : dj != 0 ? dj : dk;
        float plane = slice + (step > 0 ? 1 : 0);

        Vector3 Corner(int du, int dv)
        {
            var c = new float[3];
            c[axis] = plane;
            c[uAxis] = u + du;
            c[vAxis] = v + dv;
            return new Vector3(c[0], c[1], c[2]);
        }

        Vector3 v0 = Corner(0, 0);
        Vector3 v1 = Corner(width, 0);
        Vector3 v2 = Corner(width, height);
        Vector3 v3 = Corner(0, height);

        // Godot treats clockwise winding as front-facing, so the test is
        // (v2-v0) x (v1-v0) — matching AddFace here and the bismuth blob.
        // Flipping this culls every face and nodes render inside-out.
        if ((v2 - v0).Cross(v1 - v0).Dot(normal) < 0f)
            (v1, v3) = (v3, v1);

        int start = vertices.Count;
        vertices.Add(v0);
        vertices.Add(v1);
        vertices.Add(v2);
        vertices.Add(v3);
        for (int n = 0; n < 4; n++)
        {
            normals.Add(normal);
        }

        indices.Add(start);
        indices.Add(start + 1);
        indices.Add(start + 2);
        indices.Add(start);
        indices.Add(start + 2);
        indices.Add(start + 3);
    }
}
