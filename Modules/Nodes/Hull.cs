using Godot;
using System;

namespace GameBase.Nodes;

/// <summary>
/// A convex solid built by cutting a box down with planes, reporting the
/// polygon each cut leaves behind.
///
/// This is how a raw node gets its shape. A Voronoi cell is the intersection
/// of half-spaces -- one per neighbour, bounded by the perpendicular bisector
/// between the two sites -- and intersecting half-spaces is exactly what
/// repeated clipping does: start with a box known to contain the cell, cut once
/// per neighbour, and what survives is the cell.
///
/// Faces are asked for only after every plane is in, because a face is the part
/// of its plane that survives ALL the other cuts. A plane that misses the solid
/// reports nothing, which is what reduces twenty-six candidate neighbours to the
/// thirteen or so walls a cell really has.
///
/// A ref struct over caller-provided storage, so building a cell allocates
/// nothing on the heap.
/// </summary>
internal ref struct Hull
{
    /// <summary>Six planes for the starting box plus one per lattice neighbour.</summary>
    public const int MaxPlanes = 6 + 26;

    /// <summary>The most corners one face may have.</summary>
    public const int MaxCorners = 12;

    public struct Plane
    {
        public Vector3 Normal;
        public float Offset;
    }

    private readonly Span<Plane> _planes;
    private int _count;

    /// <summary>Starts from a box of half-size <paramref name="reach"/> about the origin.</summary>
    public Hull(Span<Plane> storage, float reach)
    {
        _planes = storage;
        _count = 0;

        Add(Vector3.Right, reach);
        Add(Vector3.Left, reach);
        Add(Vector3.Up, reach);
        Add(Vector3.Down, reach);
        Add(Vector3.Back, reach);
        Add(Vector3.Forward, reach);
    }

    /// <summary>How many planes bound the solid, box included.</summary>
    public readonly int PlaneCount => _count;

    /// <summary>The planes of the starting box come first; these are their indices.</summary>
    public const int BoxPlanes = 6;

    /// <summary>Adds a cutting plane: a unit normal and a distance along it.</summary>
    public void Add(Vector3 normal, float offset)
    {
        if (_count < _planes.Length)
            _planes[_count++] = new Plane { Normal = normal, Offset = offset };
    }

    /// <summary>
    /// The polygon plane <paramref name="index"/> contributes to the finished
    /// solid, wound to face out of it, or false if the plane never touches it.
    /// </summary>
    public readonly bool Face(int index, out CellFace face)
    {
        face = default;

        if ((uint)index >= (uint)_count)
            return false;

        Plane plane = _planes[index];

        Span<Vector3> polygon = stackalloc Vector3[MaxCorners * 2];
        int count = Square(plane.Normal, plane.Offset, polygon);

        for (int n = 0; n < _count && count >= 3; n++)
        {
            if (n == index)
                continue;

            // A plane facing the same way and sitting further out cannot cut
            // this one: the half-spaces are nested.
            if (_planes[n].Normal.Dot(plane.Normal) > 0.999f
                && _planes[n].Offset >= plane.Offset)
                continue;

            count = ClipPolygon(polygon, count, _planes[n].Normal, _planes[n].Offset);
        }

        if (count < 3 || AreaOf(polygon, count) < 0.0001f)
            return false;

        face.Count = Mathf.Min(count, MaxCorners);

        // Clipping leaves the ring in whichever order the starting square was
        // in. Godot's front face winds CLOCKWISE seen from the front, so the ring
        // is reversed whenever its maths-convention normal agrees with the plane.
        Vector3 wound = Vector3.Zero;
        for (int n = 1; n + 1 < count; n++)
            wound += (polygon[n] - polygon[0]).Cross(polygon[n + 1] - polygon[0]);

        bool reversed = wound.Dot(plane.Normal) > 0f;

        for (int n = 0; n < face.Count; n++)
            face[n] = polygon[reversed ? face.Count - 1 - n : n];

        return true;
    }

    /// <summary>A square on the plane, large enough to cover any face of the solid.</summary>
    private readonly int Square(Vector3 normal, float offset, Span<Vector3> polygon)
    {
        Vector3 guess = Mathf.Abs(normal.X) < 0.9f ? Vector3.Right : Vector3.Up;
        Vector3 u = normal.Cross(guess).Normalized();
        Vector3 v = normal.Cross(u);

        float span = _planes[0].Offset * 4f;
        Vector3 at = normal * offset;

        polygon[0] = at - u * span - v * span;
        polygon[1] = at + u * span - v * span;
        polygon[2] = at + u * span + v * span;
        polygon[3] = at - u * span + v * span;

        return 4;
    }

    /// <summary>Sutherland-Hodgman: clips a convex polygon against one half-space.</summary>
    private static int ClipPolygon(Span<Vector3> polygon, int count, Vector3 normal, float offset)
    {
        Span<Vector3> result = stackalloc Vector3[MaxCorners * 2];
        int written = 0;

        // A point exactly on the plane -- every point of the face being built --
        // counts as inside.
        const float Epsilon = 0.000001f;

        for (int n = 0; n < count; n++)
        {
            Vector3 current = polygon[n];
            Vector3 next = polygon[(n + 1) % count];

            float here = normal.Dot(current) - offset;
            float there = normal.Dot(next) - offset;

            bool insideHere = here <= Epsilon;
            bool insideThere = there <= Epsilon;

            if (insideHere && written < result.Length)
                result[written++] = current;

            if (insideHere != insideThere && written < result.Length)
                result[written++] = current + (next - current) * (here / (here - there));
        }

        result[..written].CopyTo(polygon);
        return written;
    }

    private static float AreaOf(ReadOnlySpan<Vector3> polygon, int count)
    {
        Vector3 total = Vector3.Zero;
        for (int n = 1; n + 1 < count; n++)
            total += (polygon[n] - polygon[0]).Cross(polygon[n + 1] - polygon[0]);

        return total.Length() * 0.5f;
    }
}

/// <summary>One face of a Voronoi cell: up to <see cref="Hull.MaxCorners"/> corners.</summary>
[System.Runtime.CompilerServices.InlineArray(Hull.MaxCorners)]
internal struct CellCorners
{
    private Vector3 _first;
}

/// <summary>One face of a Voronoi cell, in cell-local space.</summary>
internal struct CellFace
{
    public int Count;
    public CellCorners Corners;

    public Vector3 this[int index]
    {
        readonly get => Corners[index];
        set => Corners[index] = value;
    }
}
