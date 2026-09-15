using Godot;
using System;

namespace GameBase.Nodes;

/// <summary>
/// The quad sphere as an addressable grid: where cells are, and how to get from
/// one to the next.
///
/// <see cref="QuadSphere"/> defines the projection; this puts a lattice on it.
/// A cell keeps the Vector3I the rest of the engine passes around, read as
/// (u, v, shell) with the face folded into u -- so storage, chunking and
/// streaming work unchanged on a coordinate that means something new.
///
/// THE ONE STRUCTURAL CHANGE
///
/// On a flat lattice a neighbour is `cell + offset`. Here it cannot be: step
/// off a face edge and you land on another face where the axes may be swapped
/// or reversed; step inward across a band boundary and the grid beneath you is
/// half as fine. So neighbour lookup is a call, and that call is the only place
/// either fact is known.
///
/// EVERY LOOKUP IS O(1)
///
/// Per-shell values come from <see cref="QuadSphereTables"/> -- one array index
/// each, no octave walk, no division. The only branch on the hot path is
/// whether a lateral step stayed on its face, which is true for all but the
/// outermost ring of cells.
/// </summary>
public sealed class QuadSphereGrid : INodeGrid
{
    private readonly QuadSphereTables _tables;
    private readonly Vector3 _origin;
    private readonly int _baseResolution;

    public QuadSphereGrid(float surfaceRadius, float nodeSize, Vector3 origin)
        : this(new QuadSphereTables(surfaceRadius, nodeSize), origin)
    {
    }

    public QuadSphereGrid(QuadSphereTables tables, Vector3 origin)
    {
        _tables = tables;
        _origin = origin;
        _baseResolution = tables.BaseResolution;
    }

    /// <summary>The per-shell tables, for anything meshing in bulk.</summary>
    public QuadSphereTables Tables => _tables;

    public int BaseResolution => _baseResolution;
    public Vector3 Origin => _origin;
    public float SurfaceRadius => _tables.SurfaceRadius;
    public float NodeSize => _tables.NodeSize;
    public int ShellCount => _tables.ShellCount;

    // ------------------------------------------------------------ addressing

    /// <summary>
    /// Packs a cell address into the Vector3I the engine passes around.
    ///
    /// The face is folded into u at the BASE resolution -- not the shell's own
    /// -- so a face always occupies the same span of u whatever depth it is at,
    /// and a deeper face simply uses fewer of its slots. That keeps unpacking a
    /// division by a constant and keeps a chunk from ever straddling a fold.
    /// </summary>
    public Vector3I Pack(int face, int u, int v, int shell) =>
        new(face * _baseResolution + u, v, shell);

    /// <summary>Recovers a cell address from its packed form.</summary>
    public void Unpack(Vector3I cell, out int face, out int u, out int v, out int shell)
    {
        // Floor division, so a negative u does not fold onto the wrong face the
        // way truncation would.
        face = cell.X >= 0
            ? cell.X / _baseResolution
            : (cell.X - _baseResolution + 1) / _baseResolution;

        u = cell.X - face * _baseResolution;
        v = cell.Y;
        shell = cell.Z;
    }

    /// <summary>The face resolution the cell's shell is drawn on.</summary>
    public int ResolutionOf(Vector3I cell) =>
        _tables.HasShell(cell.Z) ? _tables.Resolution(cell.Z) : _baseResolution;

    /// <summary>
    /// Is this an address the grid actually contains?
    ///
    /// The bound that matters most is the shell: everything above the surface
    /// is sky and everything below the deepest shell is solid core, and the
    /// chunk store cannot say so for itself -- a negative index there wraps into
    /// a neighbouring chunk and reports open air as rock.
    /// </summary>
    public bool Contains(Vector3I cell)
    {
        int shell = cell.Z;
        if ((uint)shell >= (uint)_tables.ShellCount)
            return false;

        Unpack(cell, out int face, out int u, out int v, out _);

        if ((uint)face >= NodeOrientation.Count)
            return false;

        int resolution = _tables.Resolution(shell);
        return (uint)u < (uint)resolution && (uint)v < (uint)resolution;
    }

    // -------------------------------------------------------------- geometry

    /// <summary>
    /// Where a point inside a cell lands in world space.
    ///
    /// `local` runs 0..1 across the cell: u and v across the face, and w
    /// OUTWARD through the shell, so local.Z = 1 is the face nearest the sky.
    /// This is what makes a node an ARC rather than a cube -- its eight corners
    /// sit on two concentric spherical caps and its sides follow the curve.
    ///
    /// Coordinates outside 0..1 are meaningful, since geometry may reach past
    /// its own cell.
    /// </summary>
    public Vector3 PointIn(Vector3I cell, Vector3 local)
    {
        Unpack(cell, out int face, out int u, out int v, out int shell);

        int clamped = Mathf.Clamp(shell, 0, _tables.ShellCount - 1);
        int resolution = _tables.Resolution(clamped);

        float s = (u + local.X) / resolution * 2f - 1f;
        float t = (v + local.Y) / resolution * 2f - 1f;

        // Outward is +Z: the shell's outer radius at local.Z = 1, one node
        // further in at 0.
        float radius = _tables.Radius(clamped) - _tables.NodeSize * (1f - local.Z);

        return _origin + QuadSphere.Direction(face, s, t) * radius;
    }

    /// <summary>The world position of a cell's centre.</summary>
    public Vector3 CentreOf(Vector3I cell) => PointIn(cell, Half);

    private static readonly Vector3 Half = new(0.5f, 0.5f, 0.5f);

    /// <summary>The direction a cell calls UP: away from the centre.</summary>
    public Vector3 UpAt(Vector3I cell)
    {
        Vector3 d = CentreOf(cell) - _origin;
        float length = d.Length();
        return length < 0.0001f ? Vector3.Up : d / length;
    }

    /// <summary>
    /// The cell containing a world point.
    ///
    /// Returns a NEGATIVE shell for a point above the surface rather than
    /// clamping. A point in the sky belongs to no cell, and saying so is the
    /// whole job here -- clamping answers "the block at the surface" for every
    /// point in the sky, which makes a ray from the player's eye report a hit
    /// before it has travelled anywhere.
    /// </summary>
    public Vector3I CellAt(Vector3 point)
    {
        Vector3 d = point - _origin;
        float distance = d.Length();

        if (distance < 0.0001f)
            return Pack(NodeOrientation.PosY, 0, 0, _tables.ShellCount);

        int shell = Mathf.FloorToInt((_tables.SurfaceRadius - distance) / _tables.NodeSize);

        Vector3 n = d / distance;
        int face = QuadSphere.FaceOf(n);

        QuadSphere.FaceLocal(face, n, out float a, out float b);

        // Resolution is only defined on the grid; a sky point is measured
        // against the surface grid so the caller still gets a sensible u and v
        // beside the shell that tells it there is nothing there.
        int resolution = _tables.Resolution(
            Mathf.Clamp(shell, 0, _tables.ShellCount - 1));

        int u = ToCell(QuadSphere.Unwarp(a), resolution);
        int v = ToCell(QuadSphere.Unwarp(b), resolution);

        return Pack(face, u, v, shell);
    }

    /// <summary>A face coordinate in [-1, 1] to a cell index on that face.</summary>
    private static int ToCell(float s, int resolution) =>
        Mathf.Clamp(Mathf.FloorToInt((s + 1f) * 0.5f * resolution), 0, resolution - 1);

    // ------------------------------------------------------------ neighbours

    /// <summary>
    /// The cell one step away, in the cell's own frame.
    ///
    /// `du` and `dv` step across the face; `dOut` steps RADIALLY, positive
    /// meaning OUTWARD toward the sky. Outward is positive even though shells
    /// are numbered inward, because every caller thinks in a local frame where
    /// +Z is up -- the negation happens once, here, rather than in each caller.
    ///
    /// Returns false when there is no such cell: past the surface, or inside
    /// the solid core.
    /// </summary>
    public bool Neighbour(Vector3I cell, int du, int dv, int dOut, out Vector3I result)
    {
        Unpack(cell, out int face, out int u, out int v, out int shell);
        result = default;

        // Shells count inward, so stepping outward DECREASES the index.
        int target = shell - dOut;

        if ((uint)target >= (uint)_tables.ShellCount)
            return false;

        if ((uint)shell >= (uint)_tables.ShellCount)
            return false;

        int here = _tables.Resolution(shell);
        int there = _tables.Resolution(target);

        int nu = u + du;
        int nv = v + dv;

        // STILL ON THIS FACE: the common case, and pure arithmetic.
        if ((uint)nu < (uint)here && (uint)nv < (uint)here)
        {
            if (here != there)
                Rescale(ref nu, ref nv, _tables.Shift(shell), _tables.Shift(target));

            result = Pack(face, nu, nv, target);
            return true;
        }

        // OFF THE EDGE. The neighbouring face is found GEOMETRICALLY rather
        // than from a hand-written adjacency table: take the direction just
        // past this face's edge and ask which face owns it. That cannot
        // disagree with the projection's own layout, which a table could.
        float s = (nu + 0.5f) / here * 2f - 1f;
        float t = (nv + 0.5f) / here * 2f - 1f;

        Vector3 direction = QuadSphere.Direction(face, s, t);

        int nf = QuadSphere.FaceOf(direction);
        QuadSphere.FaceLocal(nf, direction, out float a, out float b);

        result = Pack(nf,
            ToCell(QuadSphere.Unwarp(a), there),
            ToCell(QuadSphere.Unwarp(b), there),
            target);

        return true;
    }

    // ------------------------------------------------------------- INodeGrid

    /// <summary>
    /// A cubed-sphere node is the four-walled case of a prism.
    ///
    /// Its walls run -u, +u, -v, +v, in that order, which is the order the
    /// corners below are written in -- so wall `w` spans corner `w` to corner
    /// `w + 1` exactly as the interface promises.
    /// </summary>
    public int MaxWalls => 4;

    /// <summary>Always four: every cell on this grid is a quad.</summary>
    public int WallCount(Vector3I cell) => 4;

    /// <summary>
    /// Lateral steps in wall order.
    ///
    /// Derived FROM the corner order below, not chosen beside it. Wall w spans
    /// corner w to corner w+1, and those two corners share one coordinate --
    /// which is the direction the wall faces. With corners wound
    /// (0,0) (0,1) (1,1) (1,0) that gives -u, +v, +u, -v.
    ///
    /// Writing the steps in the obvious -u,-v,+u,+v order instead swaps the
    /// second and fourth, so half of every node's walls named the wrong
    /// neighbour -- and the contract check caught it as exactly 8000 of 16000
    /// walls not shared.
    /// </summary>
    private static readonly Vector2I[] WallSteps =
    {
        new(-1, 0), new(0, 1), new(1, 0), new(0, -1),
    };

    /// <summary>
    /// The corners of a cell's outer face, counter-clockwise seen from outside.
    ///
    /// Wound so the wall between consecutive corners is the wall whose step
    /// shares its index.
    /// </summary>
    public int TopCorners(Vector3I cell, Span<Vector3> corners)
    {
        corners[0] = PointIn(cell, new Vector3(0f, 0f, 1f));
        corners[1] = PointIn(cell, new Vector3(0f, 1f, 1f));
        corners[2] = PointIn(cell, new Vector3(1f, 1f, 1f));
        corners[3] = PointIn(cell, new Vector3(1f, 0f, 1f));
        return 4;
    }

    /// <summary>The corners of a cell's inner face, in the same order.</summary>
    public int BottomCorners(Vector3I cell, Span<Vector3> corners)
    {
        corners[0] = PointIn(cell, new Vector3(0f, 0f, 0f));
        corners[1] = PointIn(cell, new Vector3(0f, 1f, 0f));
        corners[2] = PointIn(cell, new Vector3(1f, 1f, 0f));
        corners[3] = PointIn(cell, new Vector3(1f, 0f, 0f));
        return 4;
    }

    /// <summary>The cell past one of this cell's four side walls.</summary>
    public bool WallNeighbour(Vector3I cell, int wall, out Vector3I result)
    {
        Vector2I step = WallSteps[((wall % 4) + 4) % 4];
        return Neighbour(cell, step.X, step.Y, 0, out result);
    }

    /// <summary>The cell one shell outward (positive) or inward.</summary>
    public bool RadialNeighbour(Vector3I cell, int dOut, out Vector3I result) =>
        Neighbour(cell, 0, 0, dOut, out result);

    /// <summary>
    /// Addresses here are already unambiguous.
    ///
    /// A face is folded into u at the base resolution and a chunk never
    /// straddles a fold, so no cell has a second spelling the way an icosphere
    /// seam site does.
    /// </summary>
    public Vector3I Canonical(Vector3I cell) => cell;

    /// <summary>The chunk one step away, for the streamer's flood fill.</summary>
    public bool NeighbourChunk(Vector3I chunk, Vector3I direction, out Vector3I result)
    {
        const int Size = NodeChunkStore.ChunkSize;
        const int Half = Size / 2;

        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        var from = new Vector3I(origin.X + Half, origin.Y + Half, origin.Z + Half);

        if (!Neighbour(from,
                direction.X * Size, direction.Y * Size, direction.Z * Size,
                out Vector3I cell))
        {
            result = default;
            return false;
        }

        result = NodeChunkStore.ChunkOf(cell);
        return true;
    }

    /// <summary>
    /// Maps face coordinates between two resolutions across a band boundary.
    ///
    /// Both are powers of two, so this is a shift: going inward, a cell's 2x2
    /// group collapses to the one cell it sits under; going outward, it maps to
    /// the first of the four above it. Exact in both directions, which is the
    /// entire reason the resolution may only change by a factor of two.
    /// </summary>
    private static void Rescale(ref int u, ref int v, int shiftHere, int shiftThere)
    {
        int delta = shiftThere - shiftHere;

        if (delta > 0)
        {
            // Coarser: fewer cells, so shift down.
            u >>= delta;
            v >>= delta;
        }
        else
        {
            u <<= -delta;
            v <<= -delta;
        }
    }
}
