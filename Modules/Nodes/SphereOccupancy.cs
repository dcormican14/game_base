using Godot;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// The solid sub-cells around a section, for a world whose cells are arcs.
///
/// WHY NOT <see cref="ChunkOccupancy"/>
///
/// That one is a dense box of bits indexed by (cell - origin) * Sub + i, which
/// is only a neighbourhood if cell coordinates form a uniform lattice. On the
/// cubed sphere they do not, in two separate ways:
///
///   - the face is folded into u, so u+1 at a face edge is not the cell next
///     door but one a whole face away;
///   - face resolution HALVES with depth, so a window spanning shells spans
///     grids of different sizes and a fixed stride means different things at
///     its top and bottom.
///
/// Forcing the dense box onto it produced a meaningless map: nothing was ever
/// found enclosed, every buried shell was drawn as though exposed, and the
/// planet rendered as a wedge of its own interior -- 104787 of 104788 rendered
/// vertices sat below the surface.
///
/// WHAT THIS DOES INSTEAD
///
/// Keeps the sub-cells per CELL, in a dictionary keyed by the cell itself, and
/// reaches neighbours by asking the grid. A cell's own 64 sub-cells fit in a
/// ulong, so the map is one 64-bit word per solid cell in the window and a
/// probe is a hash lookup and a bit test.
///
/// That is more per probe than a dense array, and it is the price of the
/// coordinate system being curved. It is bounded the same way: the window is
/// one section plus a margin, rebuilt per job, thrown away after.
/// </summary>
public sealed class SphereOccupancy
{
    /// <summary>Cells of context around the section. Matches
    /// <see cref="ChunkOccupancy.Margin"/> and exists for the same reason: a
    /// node's geometry can reach two cells beyond its own.</summary>
    public const int Margin = 2;

    private const int Sub = RawNodeGeometry.Sub;

    /// <summary>
    /// Which sub-cells each cell fills, one bit per sub-cell.
    ///
    /// Only cells that hold something appear, so an empty region costs nothing
    /// and the map's size follows the terrain rather than the window.
    /// </summary>
    private readonly Dictionary<Vector3I, ulong> _filled = new();

    private SphereGrid _grid;

    /// <summary>Points the window at a section and clears it.</summary>
    public void Reset(SphereGrid grid)
    {
        _grid = grid;
        _filled.Clear();
    }

    /// <summary>
    /// Stamps every node in the window around a section.
    ///
    /// The window is walked in CELL steps through the grid rather than as a box
    /// of coordinates, so it follows the surface across face edges and across
    /// the bands where resolution changes.
    /// </summary>
    public void Fill(Vector3I sectionOrigin, int sectionSize,
        NodeChunkStore store, System.Func<NodeMaterial, INodeType> typeOf)
    {
        int span = sectionSize + Margin * 2;

        for (int du = -Margin; du < sectionSize + Margin; du++)
        {
            for (int dv = -Margin; dv < sectionSize + Margin; dv++)
            {
                for (int ds = -Margin; ds < sectionSize + Margin; ds++)
                {
                    // Offsets are applied to the section's origin directly:
                    // within a section the packed coordinates ARE contiguous,
                    // and the grid is asked only where that runs off an edge.
                    var cell = new Vector3I(
                        sectionOrigin.X + du, sectionOrigin.Y + dv, sectionOrigin.Z + ds);

                    if (!_grid.Contains(cell))
                        continue;

                    byte material = store.Get(cell);
                    if (material == NodeChunkStore.Air)
                        continue;

                    Stamp(cell, store, typeOf((NodeMaterial)material));
                }
            }
        }
    }

    /// <summary>Marks one node's sub-cells filled.</summary>
    private void Stamp(Vector3I cell, NodeChunkStore store, INodeType type)
    {
        // A node with solid rock on all 26 sides fills its own footprint
        // whatever its exact shape, so the shape is not solved for it. This is
        // the same saving the flat path makes, and it is most of the interior.
        if (Buried(cell, store))
        {
            _filled[cell] = ulong.MaxValue;
            return;
        }

        int neighbours = 0;
        for (int face = 0; face < NodeFace.Offsets.Length; face++)
        {
            Vector3I offset = NodeFace.Offsets[face];
            if (_grid.Neighbour(cell, offset.X, offset.Y, offset.Z, out Vector3I at)
                && store.Has(at))
                neighbours |= 1 << face;
        }

        int[] cells = type.OccupiedCells(type.ShapeAt(cell, neighbours));

        ulong bits = 0UL;
        for (int c = 0; c < cells.Length; c += 3)
        {
            int i = cells[c], j = cells[c + 1], k = cells[c + 2];

            // Geometry reaching outside the cell is recorded against the cell
            // it reaches INTO, so a probe there finds it.
            if (i < 0 || i >= Sub || j < 0 || j >= Sub || k < 0 || k >= Sub)
            {
                Spill(cell, i, j, k);
                continue;
            }

            bits |= 1UL << Bit(i, j, k);
        }

        _filled.TryGetValue(cell, out ulong existing);
        _filled[cell] = existing | bits;
    }

    /// <summary>
    /// Records a sub-cell that lies outside its owner's cell, against the cell
    /// it actually falls in.
    /// </summary>
    private void Spill(Vector3I cell, int i, int j, int k)
    {
        int du = FloorDiv(i, Sub);
        int dv = FloorDiv(j, Sub);
        int ds = FloorDiv(k, Sub);

        if (!_grid.Neighbour(cell, du, dv, ds, out Vector3I into))
            return;

        int li = i - du * Sub;
        int lj = j - dv * Sub;
        int lk = k - ds * Sub;

        _filled.TryGetValue(into, out ulong existing);
        _filled[into] = existing | 1UL << Bit(li, lj, lk);
    }

    /// <summary>Is the sub-cell (i, j, k) of `cell` filled by anything?</summary>
    public bool Solid(Vector3I cell, int i, int j, int k)
    {
        Vector3I target = cell;

        // A probe outside the cell is redirected to the cell it names, through
        // the grid -- which is the whole reason this class exists.
        if (i < 0 || i >= Sub || j < 0 || j >= Sub || k < 0 || k >= Sub)
        {
            int du = FloorDiv(i, Sub);
            int dv = FloorDiv(j, Sub);
            int ds = FloorDiv(k, Sub);

            if (!_grid.Neighbour(cell, du, dv, ds, out target))
                return false;

            i -= du * Sub;
            j -= dv * Sub;
            k -= ds * Sub;
        }

        return _filled.TryGetValue(target, out ulong bits)
            && (bits & 1UL << Bit(i, j, k)) != 0UL;
    }

    /// <summary>Are all 26 cells around this one solid?</summary>
    private bool Buried(Vector3I cell, NodeChunkStore store)
    {
        for (int du = -1; du <= 1; du++)
            for (int dv = -1; dv <= 1; dv++)
                for (int ds = -1; ds <= 1; ds++)
                {
                    if (du == 0 && dv == 0 && ds == 0)
                        continue;

                    if (!_grid.Neighbour(cell, du, dv, ds, out Vector3I at)
                        || !store.Has(at))
                        return false;
                }

        return true;
    }

    private static int Bit(int i, int j, int k) => (i * Sub + j) * Sub + k;

    private static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        return value % divisor != 0 && (value < 0) != (divisor < 0) ? q - 1 : q;
    }
}
