using Godot;
using System;

namespace GameBase.Nodes;

/// <summary>
/// A convex solid built by cutting a box down with planes, reporting the
/// polygon each cut leaves behind.
///
/// This is how an organic node gets its shape. A Voronoi cell is the
/// intersection of half-spaces -- one per neighbour, bounded by the
/// perpendicular bisector between the two sites -- and intersecting
/// half-spaces is exactly what repeated clipping does. Start with a box known
/// to contain the cell, cut once per neighbour, and what survives IS the cell.
///
/// WHY THE CUT REPORTS ITS POLYGON
///
/// The mesher needs the faces, not just the solid, and the face a plane
/// contributes is precisely the cross-section it cuts. Returning it from the
/// cut avoids a second pass to recover which parts of the surface belong to
/// which neighbour -- and avoids the two disagreeing, which is the class of bug
/// that opens holes between adjacent nodes.
///
/// A plane that misses the solid reports nothing, which is what reduces
/// twenty-six candidate neighbours to the thirteen or so walls a cell has.
/// </summary>
/// <remarks>
/// Faces are kept as polygons rather than as a half-edge mesh. A cell has a
/// dozen-odd faces of a handful of corners each, so the simple representation
/// is both faster and far easier to keep correct than a topological one.
/// </remarks>
internal sealed class Hull
{
    /// <summary>
    /// The most planes a cell may be bounded by.
    ///
    /// Six for the starting box, twenty-six for the neighbours in the lattice
    /// window, and one for the planet's surface. Sized at exactly that, because
    /// a budget one short silently drops the LAST plane added -- which was the
    /// surface cap, so every node at the boundary went unclipped and the planet
    /// grew a crust of half-cells poking through it.
    /// </summary>
    public const int MaxFaces = 6 + 26 + 1;

    /// <summary>The most corners one face may have.</summary>
    public const int MaxCorners = 12;

    private Plane[] _planes;
    private int _count;

    public Hull(float reach)
    {
        _planes = new Plane[MaxFaces];
        _count = 0;

        // The starting box, in cell-local space. Big enough that no bisector
        // cut can leave the cell touching it, so the box itself never shows up
        // as a face of the finished solid -- if it did, a node would have a
        // flat side that belongs to nothing.
        AddBox(reach);
    }

    private void AddBox(float reach)
    {
        Span<Vector3> normals = stackalloc Vector3[6]
        {
            Vector3.Right, Vector3.Left, Vector3.Up,
            Vector3.Down, Vector3.Back, Vector3.Forward,
        };

        for (int n = 0; n < 6; n++)
            _planes[_count++] = new Plane { Normal = normals[n], Offset = reach };
    }

    private struct Plane
    {
        public Vector3 Normal;
        public float Offset;
    }

    /// <summary>
    /// Cuts the solid with a plane and reports the face it leaves.
    ///
    /// The plane is given in cell-local space as a unit normal and a distance
    /// along it. Everything on the far side is removed.
    ///
    /// Returns false when the plane does not touch the solid, which means this
    /// neighbour is not actually adjacent and contributes no wall.
    /// </summary>
    public void Add(Vector3 normal, float offset)
    {
        if (_count < MaxFaces)
            _planes[_count++] = new Plane { Normal = normal, Offset = offset };
    }

    /// <summary>
    /// The polygon plane <paramref name="index"/> contributes to the finished
    /// solid, or nothing if that plane never touches it.
    ///
    /// MUST BE ASKED ONLY AFTER EVERY PLANE IS IN. A face is the part of its
    /// plane that survives clipping against ALL the others, so a face computed
    /// while planes are still arriving is too big -- later cuts would have
    /// trimmed it. Building faces as the planes were added left every node
    /// carrying its early faces at full size: 23 faces per cell against the 13
    /// a jittered lattice really has, and surface cells whose corners stuck out
    /// past the planet because the clip that should have removed them came too
    /// late.
    /// </summary>
    public bool Face(int index, out OrganicFace face)
    {
        face = default;

        if ((uint)index >= (uint)_count)
            return false;

        Plane plane = _planes[index];

        // Start with a square lying on the plane, large enough to cover any
        // face the solid could have, then cut it down with every other plane.
        Span<Vector3> polygon = stackalloc Vector3[MaxCorners * 2];
        int count = Square(plane.Normal, plane.Offset, polygon);

        for (int n = 0; n < _count && count >= 3; n++)
        {
            if (n == index)
                continue;

            // A plane facing the same way as this one and sitting further out
            // cannot cut it: the two half-spaces are nested. Skipping those is
            // free and removes most of the pairs on a cell whose neighbours are
            // spread evenly around it.
            if (_planes[n].Normal.Dot(plane.Normal) > 0.999f
                && _planes[n].Offset >= plane.Offset)
                continue;

            count = ClipPolygon(polygon, count, _planes[n].Normal, _planes[n].Offset);
        }

        if (count < 3)
            return false;

        // A sliver with no meaningful area is not a wall. Left in, it would
        // emit degenerate triangles whose normals are noise.
        if (AreaOf(polygon, count) < 0.0001f)
            return false;

        face = new OrganicFace { Count = Mathf.Min(count, MaxCorners) };

        // WOUND TO FACE OUT OF THE SOLID.
        //
        // Clipping produces a ring in whichever order the starting square
        // happened to be in, and that order depends on the arbitrary axes
        // Square picked -- so half the faces come out backwards. Godot's front
        // face is the CLOCKWISE one seen from the front, so the winding is
        // reversed whenever the polygon's own normal disagrees with the plane
        // it lies on.
        //
        // Left unchecked this is invisible in every self-consistent test: the
        // normals and the winding agree with each other, and only a comparison
        // against an outside reference catches it. MeshAudit's outward count
        // measured 16841 faces pointing into the planet, which is a world you
        // can stand on and cannot see.
        Vector3 wound = Vector3.Zero;

        for (int n = 1; n + 1 < count; n++)
            wound += (polygon[n] - polygon[0]).Cross(polygon[n + 1] - polygon[0]);

        bool reversed = wound.Dot(plane.Normal) > 0f;

        for (int n = 0; n < face.Count; n++)
            face[n] = polygon[reversed ? face.Count - 1 - n : n];

        return true;
    }

    /// <summary>How many planes bound this solid.</summary>
    public int PlaneCount => _count;

    /// <summary>
    /// A square lying on a plane, large enough to cover any face of the solid.
    ///
    /// Clipped down to the real face by the other planes; starting oversized is
    /// what lets the clip do all the work without knowing the answer first.
    /// </summary>
    private int Square(Vector3 normal, float offset, Span<Vector3> polygon)
    {
        // Any two axes perpendicular to the normal will do.
        Vector3 guess = Mathf.Abs(normal.X) < 0.9f ? Vector3.Right : Vector3.Up;

        Vector3 u = normal.Cross(guess).Normalized();
        Vector3 v = normal.Cross(u);

        // Sized from the box the solid started in, so it always overhangs.
        float span = _planes[0].Offset * 4f;
        Vector3 at = normal * offset;

        polygon[0] = at - u * span - v * span;
        polygon[1] = at + u * span - v * span;
        polygon[2] = at + u * span + v * span;
        polygon[3] = at - u * span + v * span;

        return 4;
    }

    /// <summary>
    /// Sutherland-Hodgman: clips a polygon against one half-space.
    ///
    /// Walks the edges, keeping points inside and inserting a point wherever an
    /// edge crosses the plane. Convex in, convex out, which is all a Voronoi
    /// cell ever needs.
    /// </summary>
    private static int ClipPolygon(Span<Vector3> polygon, int count,
        Vector3 normal, float offset)
    {
        Span<Vector3> result = stackalloc Vector3[MaxCorners * 2];
        int written = 0;

        for (int n = 0; n < count; n++)
        {
            Vector3 current = polygon[n];
            Vector3 next = polygon[(n + 1) % count];

            float here = normal.Dot(current) - offset;
            float there = normal.Dot(next) - offset;

            // A small tolerance, so a point sitting exactly on the plane -- which
            // is every point of the face being built -- counts as inside.
            const float Epsilon = 0.000001f;

            bool insideHere = here <= Epsilon;
            bool insideThere = there <= Epsilon;

            if (insideHere && written < result.Length)
                result[written++] = current;

            if (insideHere != insideThere && written < result.Length)
            {
                float t = here / (here - there);
                result[written++] = current + (next - current) * t;
            }
        }

        for (int n = 0; n < written; n++)
            polygon[n] = result[n];

        return written;
    }

    /// <summary>Twice the area of a polygon, for rejecting slivers.</summary>
    private static float AreaOf(ReadOnlySpan<Vector3> polygon, int count)
    {
        Vector3 total = Vector3.Zero;

        for (int n = 1; n + 1 < count; n++)
            total += (polygon[n] - polygon[0]).Cross(polygon[n + 1] - polygon[0]);

        return total.Length() * 0.5f;
    }
}

/// <summary>One face of an organic node, in cell-local space.</summary>
internal struct OrganicFace
{
    public int Count;

    private Vector3 _a, _b, _c, _d, _e, _f, _g, _h, _i, _j, _k, _l;

    public Vector3 this[int index]
    {
        get => index switch
        {
            0 => _a, 1 => _b, 2 => _c, 3 => _d, 4 => _e, 5 => _f,
            6 => _g, 7 => _h, 8 => _i, 9 => _j, 10 => _k, _ => _l,
        };

        set
        {
            switch (index)
            {
                case 0: _a = value; break;
                case 1: _b = value; break;
                case 2: _c = value; break;
                case 3: _d = value; break;
                case 4: _e = value; break;
                case 5: _f = value; break;
                case 6: _g = value; break;
                case 7: _h = value; break;
                case 8: _i = value; break;
                case 9: _j = value; break;
                case 10: _k = value; break;
                default: _l = value; break;
            }
        }
    }
}
