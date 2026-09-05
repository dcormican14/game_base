using Godot;

namespace GameBase.Nodes;

/// <summary>
/// The solid sub-cells around one chunk, built fresh for each meshing job.
///
/// WHY THIS REPLACES A WORLD-WIDE MAP
///
/// Face culling has to ask "is this quarter-cell filled by anything?", and the
/// old answer was a single <see cref="SubCellSet"/> covering the entire world,
/// maintained incrementally by every add and remove. That map was the second
/// unbounded allocation in the engine — 64 sub-cells stamped per node, over
/// every node that had ever been generated, with no way to drop the part of it
/// belonging to terrain the player had long since flown away from.
///
/// It never needed to be global. Occupancy is a PURE FUNCTION of the node data
/// and the node types: a node's shape depends only on its cell and the type's
/// seed (the <see cref="INodeType"/> contract), so the solid sub-cells around
/// a chunk can be re-derived from the chunk and its neighbours whenever that
/// chunk is meshed. Deriving costs one stamp per node in a small
/// neighbourhood; storing cost a permanent share of the world.
///
/// That trade is what makes the mesher THREADABLE as well as bounded. This
/// object is owned by one job, written by one thread, thrown away when the
/// mesh is done — so meshing reads the store and touches no shared mutable
/// state at all. The old incrementally-maintained map was shared by every
/// edit and every mesh, which is precisely why meshing had to stay on the main
/// thread.
///
/// THE MARGIN
///
/// The window covers one MESH SECTION plus <see cref="Margin"/> cells on every
/// side.
/// Two, because a quad's occlusion cells can lie one cell outside the node
/// that owns them, and the node filling that space can itself be one cell
/// further out — the same radius <c>RestampAround</c> used, and for the same
/// reason.
/// </summary>
public sealed class ChunkOccupancy
{
    /// <summary>Cells of context around the region. See the class remarks.</summary>
    public const int Margin = 2;

    /// <summary>
    /// Cells per edge of the largest window this can cover: one mesh section
    /// plus both margins.
    ///
    /// Sized to a SECTION rather than a whole chunk. Meshing a 32-cell chunk
    /// needed a 36-cell window — 46656 cells stamped to redraw one edit — and
    /// that volume was the single largest cost of mining a node. A section is
    /// small enough that the window around it is a fraction of the size.
    /// </summary>
    private const int Span = NodeWorld.SectionSize + Margin * 2;

    /// <summary>Sub-cells per edge of the window.</summary>
    private const int SubSpan = Span * RawNodeGeometry.Sub;

    /// <summary>
    /// One bit per sub-cell in the window, as 64-bit words.
    ///
    /// Dense rather than sparse: the window is a fixed size and a dense array
    /// is both smaller and far faster to probe than the hashed blocks a sparse
    /// set would need. It is REUSED between jobs (see <see cref="Reset"/>), so
    /// a worker allocates one for its lifetime rather than one per region.
    /// </summary>
    private readonly ulong[] _bits = new ulong[SubSpan * SubSpan * SubSpan / 64];

    /// <summary>The cell the window's min corner sits at.</summary>
    private Vector3I _origin;

    /// <summary>
    /// Points the window at a region of cells and clears it.
    ///
    /// `origin` is the region's min corner; the window covers it plus a margin
    /// on every side. Clearing is a memset the CPU does at cache-fill speed,
    /// and it buys a dense array's O(1) unhashed probe on every one of the
    /// culling queries meshing makes.
    /// </summary>
    public void Reset(Vector3I origin)
    {
        _origin = origin - Vector3I.One * Margin;
        System.Array.Clear(_bits, 0, _bits.Length);
    }

    /// <summary>
    /// Stamps every node in the window, reading through to neighbouring
    /// chunks for the margin.
    ///
    /// Nodes outside the window are not consulted and do not need to be: a
    /// node's geometry never reaches more than one cell beyond its own, so
    /// nothing outside the margin can fill a sub-cell any quad of this chunk
    /// tests.
    /// </summary>
    public void Fill(NodeChunkStore store, System.Func<NodeMaterial, INodeType> typeOf)
    {
        for (int x = 0; x < Span; x++)
            for (int y = 0; y < Span; y++)
                for (int z = 0; z < Span; z++)
                {
                    var cell = new Vector3I(_origin.X + x, _origin.Y + y, _origin.Z + z);
                    byte material = store.Get(cell);
                    if (material == NodeChunkStore.Air)
                        continue;

                    Stamp(store, cell, typeOf((NodeMaterial)material));
                }
    }

    /// <summary>Marks one node's sub-cells filled.</summary>
    private void Stamp(NodeChunkStore store, Vector3I cell, INodeType type)
    {
        // The neighbour mask must match what the MESHER will use, or culling
        // and geometry disagree: a face would be tested against occupancy that
        // describes a different shape than the one actually drawn.
        int neighbours = 0;
        for (int face = 0; face < NodeFace.Offsets.Length; face++)
        {
            if (store.Has(cell + NodeFace.Offsets[face]))
                neighbours |= 1 << face;
        }

        int[] cells = type.OccupiedCells(type.ShapeAt(cell, neighbours));

        // The node's min sub-cell, relative to the window.
        int baseX = (cell.X - _origin.X) * RawNodeGeometry.Sub;
        int baseY = (cell.Y - _origin.Y) * RawNodeGeometry.Sub;
        int baseZ = (cell.Z - _origin.Z) * RawNodeGeometry.Sub;

        for (int c = 0; c < cells.Length; c += 3)
        {
            int i = baseX + cells[c];
            int j = baseY + cells[c + 1];
            int k = baseZ + cells[c + 2];

            // A shape may reach outside its own cell, so the outermost nodes
            // of the window can stamp past its edge. Those sub-cells belong to
            // the next chunk's window and are simply dropped here.
            if ((uint)i >= SubSpan || (uint)j >= SubSpan || (uint)k >= SubSpan)
                continue;

            int bit = (i * SubSpan + j) * SubSpan + k;
            _bits[bit >> 6] |= 1UL << (bit & 63);
        }
    }

    /// <summary>
    /// Is the sub-cell (i, j, k) of `cell` filled by anything?
    ///
    /// Sub-cell coordinates are node-local and may reach outside
    /// 0..Sub-1 where a shape grows into a neighbour's vacated space, exactly
    /// as <see cref="INodeType.OccupiedCells"/> allows.
    /// </summary>
    public bool Solid(Vector3I cell, int i, int j, int k)
    {
        int x = (cell.X - _origin.X) * RawNodeGeometry.Sub + i;
        int y = (cell.Y - _origin.Y) * RawNodeGeometry.Sub + j;
        int z = (cell.Z - _origin.Z) * RawNodeGeometry.Sub + k;

        // Outside the window there is no information. Reporting "not solid"
        // draws the quad, which is the safe direction to be wrong in: a
        // surplus triangle is invisible, a missing one is a hole.
        if ((uint)x >= SubSpan || (uint)y >= SubSpan || (uint)z >= SubSpan)
            return false;

        int bit = (x * SubSpan + y) * SubSpan + z;
        return (_bits[bit >> 6] & (1UL << (bit & 63))) != 0UL;
    }
}
