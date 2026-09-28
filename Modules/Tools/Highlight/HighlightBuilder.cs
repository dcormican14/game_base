using Godot;
using System;
using System.Collections.Generic;

namespace GameBase.Tools.Highlight;

/// <summary>
/// Collects the shapes a tool's highlight is made of, then turns them into the
/// two meshes <see cref="ToolHighlight"/> draws.
///
/// Two kinds of shape:
///   - EDGES become thin bars: the outline. Every tool draws the same line: a
///     bar is as wide as <see cref="BarWidthPerDistance"/> times its distance
///     from the <see cref="Eye"/>, so it stays a few pixels thick whether the
///     target is at arm's length or across the pit, and never fattens into a
///     band when the player stands over it.
///   - GLOW surfaces carry the gradient and the particles, and both work in
///     from the outline: the gradient is strongest at the outline and fades
///     toward the middle, its pulse travels inward, and particles are born at
///     the outline and drift in. Each vertex records, in world units:
///       UV   (distance along the outline, distance in from it) -- the grid
///            the particles live on;
///       UV2  (distance in from the outline, how far in the middle is) -- the
///            gradient.
///     Faces are fanned from their middle, one wedge per outline edge, so in
///     each wedge both coordinates are exact rather than approximated.
/// </summary>
public sealed class HighlightBuilder
{
    /// <summary>Where the outline is seen from; bars are sized by their distance to it.</summary>
    public Vector3 Eye { get; set; }

    /// <summary>Bar width per unit of distance from the eye: about three pixels at 720p.</summary>
    public float BarWidthPerDistance { get; set; } = 0.0085f;

    /// <summary>Bar width limits, in world units.</summary>
    public float MinBarWidth { get; set; } = 0.012f;

    public float MaxBarWidth { get; set; } = 0.08f;

    private readonly List<Vector3> _bars = new();
    private readonly List<Vector3> _glow = new();
    private readonly List<Vector2> _glowUv = new();
    private readonly List<Vector2> _glowInward = new();
    private readonly List<Vector3> _disc = new();
    private readonly List<Vector2> _discAt = new();

    public void Clear()
    {
        _bars.Clear();
        _glow.Clear();
        _glowUv.Clear();
        _glowInward.Clear();
        _disc.Clear();
        _discAt.Clear();
        DiscRadius = 0f;
        DiscInner = 0f;
        ShowInner = false;
    }

    // ------------------------------------------------------------------ disc

    /// <summary>A brush's outer circle, in the disc's own units.</summary>
    public float DiscRadius { get; set; }

    /// <summary>A brush's inner circle, drawn only if <see cref="ShowInner"/>.</summary>
    public float DiscInner { get; set; }

    public bool ShowInner { get; set; }

    /// <summary>
    /// A triangle of a brush's skin: a surface laid flush on the ground whose
    /// every point carries where it is IN the disc -- a flat circle's own x
    /// and y, wrapped onto the ground. The shader draws the circles, gradient
    /// and particles from that position, pixel by pixel.
    /// </summary>
    public void DiscTriangle(Vector3 a, Vector2 atA, Vector3 b, Vector2 atB, Vector3 c, Vector2 atC)
    {
        _disc.Add(a);
        _disc.Add(b);
        _disc.Add(c);
        _discAt.Add(atA);
        _discAt.Add(atB);
        _discAt.Add(atC);
    }

    // ------------------------------------------------------------------ bars

    /// <summary>
    /// A bar along one edge, pushed a hair away from <paramref name="centre"/> so
    /// it sits proud of both faces meeting there rather than inside either.
    /// </summary>
    public void Edge(Vector3 from, Vector3 to, Vector3 centre)
    {
        Vector3 along = to - from;
        float span = along.Length();
        if (span < 0.000001f)
            return;

        along /= span;

        Vector3 mid = (from + to) * 0.5f;
        Vector3 outward = mid - centre;
        outward -= along * outward.Dot(along);
        outward = outward.LengthSquared() < 0.000001f ? AnyPerpendicular(along) : outward.Normalized();

        Vector3 side = along.Cross(outward).Normalized();

        float width = Mathf.Clamp(mid.DistanceTo(Eye) * BarWidthPerDistance, MinBarWidth, MaxBarWidth);
        float half = width * 0.5f;

        // Lengthened by half a bar at each end so corners meet squarely.
        Box(mid + outward * half, along * (span * 0.5f + half), outward * half, side * half);
    }

    private void Box(Vector3 origin, Vector3 x, Vector3 y, Vector3 z)
    {
        Span<Vector3> c = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            c[i] = origin
                + x * ((i & 1) == 0 ? -1f : 1f)
                + y * (((i >> 1) & 1) == 0 ? -1f : 1f)
                + z * (((i >> 2) & 1) == 0 ? -1f : 1f);
        }

        ReadOnlySpan<int> quads = stackalloc int[]
        {
            1, 5, 7, 3, 4, 0, 2, 6,
            2, 3, 7, 6, 4, 5, 1, 0,
            4, 6, 7, 5, 0, 1, 3, 2,
        };

        for (int q = 0; q < quads.Length; q += 4)
        {
            Vector3 a = c[quads[q]], b = c[quads[q + 1]], cc = c[quads[q + 2]], d = c[quads[q + 3]];
            _bars.Add(a); _bars.Add(b); _bars.Add(cc);
            _bars.Add(a); _bars.Add(cc); _bars.Add(d);
        }
    }

    private static Vector3 AnyPerpendicular(Vector3 v)
    {
        Vector3 guess = Mathf.Abs(v.Dot(Vector3.Up)) < 0.9f ? Vector3.Up : Vector3.Right;
        return v.Cross(guess).Normalized();
    }

    // ------------------------------------------------------------------ glow

    /// <summary>
    /// A convex face that glows in from its edges: one wedge per edge, fanned
    /// from <paramref name="middle"/>, where the glow fades out.
    /// </summary>
    public void GlowFace(ReadOnlySpan<Vector3> corners, Vector3 middle)
    {
        float around = 0f;

        for (int n = 0; n < corners.Length; n++)
        {
            Vector3 a = corners[n];
            Vector3 b = corners[(n + 1) % corners.Length];

            Vector3 along = b - a;
            float length = along.Length();
            if (length < 0.000001f)
                continue;

            along /= length;

            // In from this edge, in the face's own plane, toward the middle.
            Vector3 toMiddle = middle - a;
            float alongToMiddle = toMiddle.Dot(along);
            float depth = (toMiddle - along * alongToMiddle).Length();

            if (depth < 0.000001f)
                continue;

            GlowTriangle(
                new GlowVertex(a, around, 0f, depth),
                new GlowVertex(b, around + length, 0f, depth),
                new GlowVertex(middle, around + alongToMiddle, depth, depth));

            around += length;
        }
    }

    /// <summary>One corner of a glow surface: where it is, and where it sits relative to the outline.</summary>
    public readonly record struct GlowVertex(Vector3 Position, float Along, float Inward, float Depth);

    /// <summary>A triangle of glow, for shapes the tools build themselves.</summary>
    public void GlowTriangle(GlowVertex a, GlowVertex b, GlowVertex c)
    {
        AddGlow(a);
        AddGlow(b);
        AddGlow(c);
    }

    private void AddGlow(GlowVertex vertex)
    {
        _glow.Add(vertex.Position);
        _glowUv.Add(new Vector2(vertex.Along, vertex.Inward));
        _glowInward.Add(new Vector2(vertex.Inward, vertex.Depth));
    }

    // ---------------------------------------------------------------- output

    public ArrayMesh BuildOutline() => _bars.Count == 0 ? null : Mesh(_bars, null, null);

    public ArrayMesh BuildGlow() => _glow.Count == 0 ? null : Mesh(_glow, _glowUv, _glowInward);

    public ArrayMesh BuildDisc() => _disc.Count == 0 ? null : Mesh(_disc, _discAt, null);

    private static ArrayMesh Mesh(List<Vector3> vertices, List<Vector2> uv, List<Vector2> uv2)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = vertices.ToArray();

        if (uv != null)
            arrays[(int)Godot.Mesh.ArrayType.TexUV] = uv.ToArray();

        if (uv2 != null)
            arrays[(int)Godot.Mesh.ArrayType.TexUV2] = uv2.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
