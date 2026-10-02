using Godot;
using System;
using System.Collections.Generic;

namespace GameBase.Terrain;

/// <summary>A far-terrain block's triangles, ready to hand to the renderer.</summary>
public sealed class BlockMesh
{
    public static readonly BlockMesh Empty = new(Array.Empty<Vector3>(), Array.Empty<Vector3>(),
        Array.Empty<Color>(), Array.Empty<int>());

    public BlockMesh(Vector3[] vertices, Vector3[] normals, Color[] colors, int[] indices, int surfaceVertices = -1,
        Vector3[] morphOffsets = null, Vector3[] morphNormals = null)
    {
        Vertices = vertices;
        Normals = normals;
        Colors = colors;
        Indices = indices;
        SurfaceVertices = surfaceVertices < 0 ? vertices.Length : surfaceVertices;
        MorphOffsets = morphOffsets;
        MorphNormals = morphNormals;
    }

    public Vector3[] Vertices { get; }

    /// <summary>How many of <see cref="Vertices"/> lie on the ground; the rest are the skirt's, inside it.</summary>
    public int SurfaceVertices { get; }
    public Vector3[] Normals { get; }

    /// <summary>Red is 1 for rock, 0 for sand.</summary>
    public Color[] Colors { get; }

    public int[] Indices { get; }

    /// <summary>
    /// Where each vertex moves to lie on the next level's coarser ground, and
    /// the normal it has there (see FarMesher, MORPH). Null when not worked out.
    /// </summary>
    public Vector3[] MorphOffsets { get; }
    public Vector3[] MorphNormals { get; }

    public bool IsEmpty => Indices.Length == 0;

    /// <summary>
    /// Main thread only: the mesh as the renderer takes it, with the
    /// distances from the camera over which it morphs (see FarMesher, MORPH).
    /// </summary>
    public ArrayMesh ToArrayMesh(float morphStart = 1e9f, float morphEnd = 2e9f)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = Normals;
        arrays[(int)Mesh.ArrayType.Color] = Colors;
        arrays[(int)Mesh.ArrayType.Index] = Indices;

        Mesh.ArrayFormat flags = 0;
        if (MorphOffsets != null)
        {
            arrays[(int)Mesh.ArrayType.Custom0] = Flatten(MorphOffsets, morphStart);
            arrays[(int)Mesh.ArrayType.Custom1] = Flatten(MorphNormals, morphEnd);
            flags = (Mesh.ArrayFormat)(((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
                | ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift));
        }

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
        return mesh;
    }

    /// <summary>Four floats a vertex: the vector, then a value the same for every vertex.</summary>
    private static float[] Flatten(Vector3[] vectors, float fourth)
    {
        var floats = new float[vectors.Length * 4];
        for (int i = 0; i < vectors.Length; i++)
        {
            floats[i * 4] = vectors[i].X;
            floats[i * 4 + 1] = vectors[i].Y;
            floats[i * 4 + 2] = vectors[i].Z;
            floats[i * 4 + 3] = fourth;
        }

        return floats;
    }
}

/// <summary>
/// Meshes one far-terrain block with SURFACE NETS: one vertex in every cell the
/// surface passes through, at the average of where it crosses the cell's
/// edges, joined into a quad across every edge it crosses. The same method as
/// the particle mesher, so the far ground has the same soft character as the
/// sand, and it keeps whatever the field holds -- an arch is two surfaces
/// with air between, and comes out as two surfaces with air between.
///
/// The block meshes one cell past each side, with a skirt hanging into the
/// ground from the rim (see FarTerrain, SEAMS). Pure; any thread.
///
/// MORPH. Each vertex also carries where it would lie on the NEXT level's
/// ground. The next level samples every other one of these samples, so its
/// mesh over this block can be built here exactly as it builds it
/// (<see cref="CoarseGround"/>), and each vertex is given the nearest point on
/// it. The shader slides it there as the block nears the distance its parent
/// takes over at, so where a block meets a coarser one the two stand at the
/// same height -- no ledge between the levels -- and a block swapping for its
/// parent barely moves.
/// </summary>
public static class FarMesher
{
    [ThreadStatic] private static DistanceBlock _field;
    [ThreadStatic] private static int[] _vertexOf;

    /// <summary>Cells meshed along each side: the block's own, and one past each end.</summary>
    private const int Span = FarTerrain.Cells + 2;

    /// <summary>
    /// Is a whole block (with its overlap) nothing but air or rock? +1 rock,
    /// -1 air, 0 either. A coarse look first, which settles open sky and deep
    /// rock cheaply, then a finer one for blocks nearer the ground. Most
    /// blocks in the view are one or the other.
    /// </summary>
    public static int Probe(ITerrainShape shape, Vector3I coord, float cell)
    {
        float size = cell * FarTerrain.Cells;
        var min = new Vector3(coord.X, coord.Y, coord.Z) * size - Vector3.One * cell * 2;
        Vector3 max = min + Vector3.One * (size + 4 * cell);

        int side = DistanceBlock.Uniform(shape, min, max, cell * 0.5, cell * 0.5, 4);
        return side != 0 ? side : DistanceBlock.Uniform(shape, min, max, cell * 0.5, cell * 0.5, 8);
    }

    /// <param name="coord">The block's position, in blocks of its level.</param>
    /// <param name="cell">The width of one of its cells.</param>
    /// <param name="probed">True when <see cref="Probe"/> already found the block not uniform.</param>
    public static BlockMesh Build(ITerrainShape shape, SandRules sand, Vector3I coord, float cell, bool probed = false)
    {
        if (!probed && Probe(shape, coord, cell) != 0)
            return BlockMesh.Empty;

        // Samples at whole multiples of the cell width, so blocks of a level
        // share theirs, and level 1 shares the chunks'.
        Vector3I first = coord * FarTerrain.Cells - Vector3I.One;
        DistanceBlock field = _field ??= new DistanceBlock();
        field.Fill(shape, cell, first, first + Vector3I.One * Span);

        int[] vertexOf = _vertexOf ??= new int[Span * Span * Span];
        Array.Fill(vertexOf, -1);

        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Color>();
        var indices = new List<int>();
        var morphOffsets = new List<Vector3>();
        var morphNormals = new List<Vector3>();

        Span<float> corner = stackalloc float[8];
        var coarseGround = new CoarseGround(field, cell);

        // One vertex per cell the surface crosses.
        for (int x = 0; x < Span; x++)
        for (int y = 0; y < Span; y++)
        for (int z = 0; z < Span; z++)
        {
            Vector3I s = first + new Vector3I(x, y, z);
            int inside = 0;

            for (int c = 0; c < 8; c++)
            {
                corner[c] = field.Value(s.X + (c & 1), s.Y + ((c >> 1) & 1), s.Z + ((c >> 2) & 1));
                if (corner[c] > 0f)
                    inside++;
            }

            if (inside == 0 || inside == 8)
                continue;

            Vector3 sum = Vector3.Zero;
            int crossings = 0;

            for (int e = 0; e < 12; e++)
            {
                int a = EdgeA[e], b = EdgeB[e];
                if ((corner[a] > 0f) == (corner[b] > 0f))
                    continue;

                float t = corner[a] / (corner[a] - corner[b]);
                sum += Offset(a).Lerp(Offset(b), t);
                crossings++;
            }

            Vector3 position = (new Vector3(s.X, s.Y, s.Z) + sum / crossings) * cell;
            Vector3 inward = field.GradientAt(position);
            float strength = inward.Length();
            inward = strength > 1e-6f ? inward / strength : position.Normalized() * -1f;

            float rock = sand.DepthOn(-inward, position) <= 0f ? 1f : 0f;

            Vector3 coarse = coarseGround.Project(position, out Vector3 coarseNormal);

            vertexOf[(x * Span + y) * Span + z] = vertices.Count;
            vertices.Add(position);
            normals.Add(-inward);
            colors.Add(new Color(rock, 0f, 0f));
            morphOffsets.Add(coarse - position);
            morphNormals.Add(coarseNormal);
        }

        // One quad across every sample edge the surface crosses, joining the
        // four cells around the edge.
        for (int axis = 0; axis < 3; axis++)
        {
            int u = (axis + 1) % 3, v = (axis + 2) % 3;

            for (int x = 0; x < Span; x++)
            for (int y = 0; y < Span; y++)
            for (int z = 0; z < Span; z++)
            {
                var local = new Vector3I(x, y, z);

                // The cells around this edge are local - u and local - v:
                // both must be in the block.
                if (local[u] == 0 || local[v] == 0)
                    continue;

                Vector3I s = first + local;
                Vector3I along = Unit(axis);
                float here = field.Value(s.X, s.Y, s.Z);
                float next = field.Value(s.X + along.X, s.Y + along.Y, s.Z + along.Z);

                if ((here > 0f) == (next > 0f))
                    continue;

                Vector3I du = Unit(u), dv = Unit(v);
                int q0 = VertexAt(vertexOf, local);
                int q1 = VertexAt(vertexOf, local - du);
                int q2 = VertexAt(vertexOf, local - du - dv);
                int q3 = VertexAt(vertexOf, local - dv);

                if (q0 < 0 || q1 < 0 || q2 < 0 || q3 < 0)
                    continue;

                // Facing out of the ground: along the axis if the ground is
                // on this side of the edge.
                Vector3 outward = new Vector3(along.X, along.Y, along.Z) * (here > 0f ? 1f : -1f);
                AddQuad(vertices, indices, q0, q1, q2, q3, outward);
            }
        }

        int surface = vertices.Count;
        AddSkirts(field, vertices, normals, colors, morphOffsets, morphNormals, indices, cell * 2f);

        return new BlockMesh(vertices.ToArray(), normals.ToArray(), colors.ToArray(), indices.ToArray(), surface,
            morphOffsets.ToArray(), morphNormals.ToArray());
    }

    /// <summary>
    /// The next level's mesh over a block, built from every other one of the
    /// block's samples just as that level builds it from its own -- the same
    /// vertices, the same triangles -- to find the nearest point on it.
    /// </summary>
    private sealed class CoarseGround
    {
        private readonly DistanceBlock _field;
        private readonly float _cell;
        private readonly float _coarse;
        private readonly Vector3I _low;
        private readonly int _n;

        private readonly int[] _vertexOf;
        private readonly List<Vector3> _vertices = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<int> _triangles = new();

        /// <summary>Per coarse cell, the triangles that come within a fine cell of it.</summary>
        private readonly List<int>[] _near;

        public CoarseGround(DistanceBlock field, float cell)
        {
            _field = field;
            _cell = cell;
            _coarse = cell * 2f;

            // Coarse cells whose corners the field holds: even samples from
            // one before Low to one past High.
            _low = new Vector3I(First(field.Low.X), First(field.Low.Y), First(field.Low.Z));
            _n = (int)Math.Floor((field.High.X - 1) / 2.0) - _low.X + 1;
            _vertexOf = new int[_n * _n * _n];
            _near = new List<int>[_n * _n * _n];
            Array.Fill(_vertexOf, -1);

            Span<float> corner = stackalloc float[8];

            for (int x = 0; x < _n; x++)
            for (int y = 0; y < _n; y++)
            for (int z = 0; z < _n; z++)
            {
                Vector3I c = _low + new Vector3I(x, y, z);
                int inside = 0;

                for (int k = 0; k < 8; k++)
                {
                    corner[k] = field.Value(2 * (c.X + (k & 1)), 2 * (c.Y + ((k >> 1) & 1)), 2 * (c.Z + ((k >> 2) & 1)));
                    if (corner[k] > 0f)
                        inside++;
                }

                if (inside == 0 || inside == 8)
                    continue;

                Vector3 sum = Vector3.Zero;
                int crossings = 0;

                for (int e = 0; e < 12; e++)
                {
                    int a = EdgeA[e], b = EdgeB[e];
                    if ((corner[a] > 0f) == (corner[b] > 0f))
                        continue;

                    float t = corner[a] / (corner[a] - corner[b]);
                    sum += Offset(a).Lerp(Offset(b), t);
                    crossings++;
                }

                Vector3 position = (new Vector3(c.X, c.Y, c.Z) + sum / crossings) * _coarse;
                Coarse(field, position, out Vector3 gradient);
                float strength = gradient.Length();

                _vertexOf[(x * _n + y) * _n + z] = _vertices.Count;
                _vertices.Add(position);
                _normals.Add(strength > 1e-6f ? -gradient / strength : position.Normalized());
            }

            // The quads, as Build joins them: across every coarse sample edge
            // the surface crosses, between the four cells around it.
            for (int axis = 0; axis < 3; axis++)
            {
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                Vector3I along = Unit(axis), du = Unit(u), dv = Unit(v);

                for (int x = 0; x < _n; x++)
                for (int y = 0; y < _n; y++)
                for (int z = 0; z < _n; z++)
                {
                    var local = new Vector3I(x, y, z);
                    if (local[u] == 0 || local[v] == 0 || local[axis] >= _n)
                        continue;

                    Vector3I c = _low + local;
                    float here = field.Value(2 * c.X, 2 * c.Y, 2 * c.Z);
                    float next = field.Value(2 * (c.X + along.X), 2 * (c.Y + along.Y), 2 * (c.Z + along.Z));
                    if ((here > 0f) == (next > 0f))
                        continue;

                    int q0 = At(local), q1 = At(local - du), q2 = At(local - du - dv), q3 = At(local - dv);
                    if (q0 < 0 || q1 < 0 || q2 < 0 || q3 < 0)
                        continue;

                    // Split on the same diagonal Build's quads are.
                    AddTriangle(q0, q1, q2);
                    AddTriangle(q0, q2, q3);
                }
            }
        }

        /// <summary>
        /// The nearest point on the coarse mesh to a point, and the coarse
        /// normal there. A point with no coarse triangle near keeps its place.
        /// </summary>
        public Vector3 Project(Vector3 point, out Vector3 normal)
        {
            normal = point.Normalized();
            List<int> near = _near[Index(CellOf(point))];
            if (near == null)
                return point;

            float best = float.MaxValue;
            Vector3 found = point;

            foreach (int t in near)
            {
                int a = _triangles[t], b = _triangles[t + 1], c = _triangles[t + 2];
                Vector3 on = Closest(point, _vertices[a], _vertices[b], _vertices[c], out Vector3 weights);
                float distance = on.DistanceSquaredTo(point);
                if (distance >= best)
                    continue;

                best = distance;
                found = on;
                Vector3 blended = _normals[a] * weights.X + _normals[b] * weights.Y + _normals[c] * weights.Z;
                normal = blended.LengthSquared() > 1e-12f ? blended.Normalized() : normal;
            }

            // Never slid further than two cells: past that, it is not this ground.
            Vector3 offset = found - point;
            if (offset.Length() > 2f * _cell)
            {
                normal = point.Normalized();
                return point;
            }

            return found;
        }

        private void AddTriangle(int a, int b, int c)
        {
            int t = _triangles.Count;
            _triangles.Add(a);
            _triangles.Add(b);
            _triangles.Add(c);

            // Filed under every coarse cell within a fine cell of it.
            Vector3 pa = _vertices[a], pb = _vertices[b], pc = _vertices[c];
            Vector3 min = pa.Min(pb).Min(pc) - Vector3.One * _cell;
            Vector3 max = pa.Max(pb).Max(pc) + Vector3.One * _cell;
            Vector3I from = CellOf(min), to = CellOf(max);

            for (int x = from.X; x <= to.X; x++)
            for (int y = from.Y; y <= to.Y; y++)
            for (int z = from.Z; z <= to.Z; z++)
            {
                int i = Index(new Vector3I(x, y, z));
                (_near[i] ??= new List<int>()).Add(t);
            }
        }

        private int At(Vector3I local) => _vertexOf[Index(local)];

        private int Index(Vector3I local) => (local.X * _n + local.Y) * _n + local.Z;

        /// <summary>The coarse cell a point is in, as a local index, kept inside the block.</summary>
        private Vector3I CellOf(Vector3 point) => new(
            Math.Clamp((int)Math.Floor(point.X / _coarse) - _low.X, 0, _n - 1),
            Math.Clamp((int)Math.Floor(point.Y / _coarse) - _low.Y, 0, _n - 1),
            Math.Clamp((int)Math.Floor(point.Z / _coarse) - _low.Z, 0, _n - 1));

        private static int First(int low) => (int)Math.Ceiling((low - 1) / 2.0);

        /// <summary>The nearest point of a triangle, with its barycentric weights (Ericson, 5.1.5).</summary>
        private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 weights)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = ab.Dot(ap), d2 = ac.Dot(ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                weights = new Vector3(1, 0, 0);
                return a;
            }

            Vector3 bp = p - b;
            float d3 = ab.Dot(bp), d4 = ac.Dot(bp);
            if (d3 >= 0f && d4 <= d3)
            {
                weights = new Vector3(0, 1, 0);
                return b;
            }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                weights = new Vector3(1 - v, v, 0);
                return a + ab * v;
            }

            Vector3 cp = p - c;
            float d5 = ab.Dot(cp), d6 = ac.Dot(cp);
            if (d6 >= 0f && d5 <= d6)
            {
                weights = new Vector3(0, 0, 1);
                return c;
            }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                weights = new Vector3(1 - w, 0, w);
                return a + ac * w;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                float w = (d4 - d3) / (d4 - d3 + (d5 - d6));
                weights = new Vector3(0, 1 - w, w);
                return b + (c - b) * w;
            }

            float denominator = 1f / (va + vb + vc);
            float bv = vb * denominator, cw = vc * denominator;
            weights = new Vector3(1 - bv - cw, bv, cw);
            return a + ab * bv + ac * cw;
        }
    }

    /// <summary>
    /// The field as the next level holds it: interpolated between every other
    /// sample. With its gradient, exact for that interpolation.
    /// </summary>
    private static float Coarse(DistanceBlock field, Vector3 point, out Vector3 gradient)
    {
        double spacing = field.Spacing * 2;

        int x = CoarseCorner(point.X / spacing, field.Low.X, field.High.X, out float tx);
        int y = CoarseCorner(point.Y / spacing, field.Low.Y, field.High.Y, out float ty);
        int z = CoarseCorner(point.Z / spacing, field.Low.Z, field.High.Z, out float tz);

        float v000 = field.Value(x, y, z), v100 = field.Value(x + 2, y, z);
        float v010 = field.Value(x, y + 2, z), v110 = field.Value(x + 2, y + 2, z);
        float v001 = field.Value(x, y, z + 2), v101 = field.Value(x + 2, y, z + 2);
        float v011 = field.Value(x, y + 2, z + 2), v111 = field.Value(x + 2, y + 2, z + 2);

        float sx = 1f - tx, sy = 1f - ty, sz = 1f - tz;
        float scale = (float)(1 / spacing);

        gradient = new Vector3(
            ((v100 - v000) * sy * sz + (v110 - v010) * ty * sz + (v101 - v001) * sy * tz + (v111 - v011) * ty * tz) * scale,
            ((v010 - v000) * sx * sz + (v110 - v100) * tx * sz + (v011 - v001) * sx * tz + (v111 - v101) * tx * tz) * scale,
            ((v001 - v000) * sx * sy + (v101 - v100) * tx * sy + (v011 - v010) * sx * ty + (v111 - v110) * tx * ty) * scale);

        return (v000 * sx + v100 * tx) * sy * sz + (v010 * sx + v110 * tx) * ty * sz
            + (v001 * sx + v101 * tx) * sy * tz + (v011 * sx + v111 * tx) * ty * tz;
    }

    /// <summary>
    /// The first sample (always even) of the coarse cell a point falls in,
    /// along one axis, kept to cells whose corners the field holds: even
    /// samples from one before Low to one past High.
    /// </summary>
    private static int CoarseCorner(double at, int low, int high, out float t)
    {
        int first = (int)Math.Ceiling((low - 1) / 2.0), last = (int)Math.Floor((high - 1) / 2.0);
        int c = Math.Clamp((int)Math.Floor(at), first, last);
        t = (float)Math.Clamp(at - c, 0, 1);
        return c * 2;
    }

    /// <summary>
    /// A curtain hanging straight down (toward the planet's centre) from every
    /// open edge of the mesh: the rim of the block's overlap. Where the next
    /// block's ground meets this one exactly -- the same level -- the curtain
    /// is buried and never seen. Where it cannot meet exactly -- a coarser or
    /// finer block -- the curtain fills the crack between the two from below,
    /// so no line of sight gets through to the sky. Hanging under an edge that
    /// lies on the ground, it stays under the ground.
    ///
    /// It is shortened wherever the rock below is thinner than it -- an arch,
    /// an overhang, a floating island -- and left off under their undersides,
    /// so it never hangs into an opening. (Hung along the surface's normal
    /// instead, two edges of a valley hang apart and the curtain between them
    /// stood up out of the valley floor.)
    /// </summary>
    private static void AddSkirts(DistanceBlock field, List<Vector3> vertices, List<Vector3> normals,
        List<Color> colors, List<Vector3> morphOffsets, List<Vector3> morphNormals, List<int> indices, float depth)
    {
        // An open edge belongs to one triangle only.
        var uses = new Dictionary<long, int>();
        int triangles = indices.Count;

        static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        for (int i = 0; i < triangles; i += 3)
        {
            for (int e = 0; e < 3; e++)
            {
                long key = Key(indices[i + e], indices[i + (e + 1) % 3]);
                uses[key] = uses.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }

        var hung = new Dictionary<int, int>();
        int Hang(int vertex)
        {
            if (hung.TryGetValue(vertex, out int below))
                return below;

            // The longest hang, of four, that stays in the ground all the way;
            // none at all (-1) if even the shortest would come out.
            Vector3 p = vertices[vertex];
            Vector3 down = -p.Normalized();
            below = -1;
            for (float length = depth; length >= depth / 8f; length *= 0.5f)
            {
                if (field.Distance(p + down * length) > 0f && field.Distance(p + down * (length * 0.5f)) > 0f
                    && field.Distance(p + down * (length * 0.25f)) > 0f)
                {
                    below = vertices.Count;
                    p += down * length;
                    break;
                }
            }

            hung[vertex] = below;
            if (below < 0)
                return below;

            // Moving with the vertex it hangs from.
            vertices.Add(p);
            normals.Add(normals[vertex]);
            colors.Add(colors[vertex]);
            morphOffsets.Add(morphOffsets[vertex]);
            morphNormals.Add(morphNormals[vertex]);
            return below;
        }

        for (int i = 0; i < triangles; i += 3)
        {
            for (int e = 0; e < 3; e++)
            {
                int a = indices[i + e], b = indices[i + (e + 1) % 3];
                if (uses[Key(a, b)] != 1)
                    continue;

                int a2 = Hang(a), b2 = Hang(b);
                if (a2 < 0 || b2 < 0)
                    continue;

                // Both faces: the curtain is seen from whichever side a crack
                // opens on.
                indices.Add(a); indices.Add(b); indices.Add(b2);
                indices.Add(a); indices.Add(b2); indices.Add(a2);
                indices.Add(a); indices.Add(b2); indices.Add(b);
                indices.Add(a); indices.Add(a2); indices.Add(b2);
            }
        }
    }

    private static int VertexAt(int[] vertexOf, Vector3I local)
    {
        if (local.X < 0 || local.Y < 0 || local.Z < 0 || local.X >= Span || local.Y >= Span || local.Z >= Span)
            return -1;

        return vertexOf[(local.X * Span + local.Y) * Span + local.Z];
    }

    /// <summary>Two triangles, wound so Godot's front face looks along <paramref name="outward"/>.</summary>
    private static void AddQuad(List<Vector3> vertices, List<int> indices, int a, int b, int c, int d, Vector3 outward)
    {
        // Godot's front face is clockwise; its normal is (C - A) x (B - A).
        Vector3 facing = (vertices[c] - vertices[a]).Cross(vertices[b] - vertices[a]);
        if (facing.Dot(outward) < 0f)
            (b, d) = (d, b);

        indices.Add(a); indices.Add(b); indices.Add(c);
        indices.Add(a); indices.Add(c); indices.Add(d);
    }

    private static Vector3I Unit(int axis) => axis switch
    {
        0 => Vector3I.Right,
        1 => Vector3I.Up,
        _ => Vector3I.Back,
    };

    private static Vector3 Offset(int corner) => new(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);

    // The twelve edges of a cube, by corner (bit 0 x, bit 1 y, bit 2 z).
    private static readonly int[] EdgeA = { 0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3 };
    private static readonly int[] EdgeB = { 1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7 };
}
