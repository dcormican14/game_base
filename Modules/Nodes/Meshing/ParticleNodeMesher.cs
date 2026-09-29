using Godot;
using System;

namespace GameBase.Nodes.Meshing;

/// <summary>
/// Draws particle nodes as one smooth surface through their fill levels.
///
/// SURFACE NETS over the lattice. Each cell's fill is a signed distance to the
/// surface at its lattice point (<see cref="NodeFill"/>). Wherever the distance
/// changes sign along a lattice edge, the surface passes through that edge, and
/// the four lattice cubes sharing the edge are joined by a quad. Each cube gets
/// one vertex, placed at the average of the crossings on its twelve edges, so
/// neighbouring quads share vertices and the result is a closed, smooth sheet
/// that sits wherever the fills put it -- between lattice points, not on them.
///
/// Particles meet the other forms like this:
///   - AIR is outside, so a particle-air edge is surface and gets a quad;
///   - RAW is JUST outside (<see cref="SectionSample.RawLevel"/>). The particle
///     nodes are a closed shell of their own, like a cloud around them, whose
///     surface sinks part of the way into the rock beside them but never to a
///     raw node's core. Both are drawn and the rock wins where they overlap:
///     rock stands through a thin shell, and a thick one hides the join. Mine
///     the rock away and the shell is left as it was, rounded underneath;
///   - UNKNOWN is inside and never gets a quad, for the reason given on
///     <see cref="SectionSample"/>.
///
/// A section owns the edges leaving its own lattice points in +x, +y and +z,
/// so every edge in the world is meshed by exactly one section and neighbouring
/// sections never draw the same quad twice. The cubes along a section's edge
/// are shared, though, and each section builds their vertices for itself --
/// identically, bit for bit, or the seam between the two opens up.
/// </summary>
internal sealed class ParticleNodeMesher : INodeMesher
{
    public NodeForm Form => NodeForm.Particle;


    private const int Size = SectionSample.Size;

    /// <summary>Cubes along an edge: min corners from -1 to Size - 1.</summary>
    private const int CubeSpan = Size + 1;

    /// <summary>The three axes, and for each the two that complete a right-handed frame.</summary>
    private static readonly Vector3I[] Axes = { new(1, 0, 0), new(0, 1, 0), new(0, 0, 1) };

    // Per-thread scratch: the levels of every sample, and the vertex each cube
    // was given in each particle material's buffers.
    [ThreadStatic] private static float[] _levels;
    [ThreadStatic] private static int[] _cubeVertex;
    [ThreadStatic] private static byte[] _cubeMaterial;

    public void Build(SectionSample sample, VoronoiGrid grid, SectionGeometry output)
    {
        if (!sample.HasParticles)
            return;

        float[] levels = _levels ??= new float[SectionSample.Span * SectionSample.Span * SectionSample.Span];
        int[] cubeVertex = _cubeVertex ??= new int[CubeSpan * CubeSpan * CubeSpan];
        byte[] cubeMaterial = _cubeMaterial ??= new byte[CubeSpan * CubeSpan * CubeSpan];

        for (int i = 0; i < levels.Length; i++)
            levels[i] = sample.Level(i);

        // A cube's vertex is made on first use, into the buffers of whichever
        // particle material first asked for it. Air marks "not made yet".
        Array.Fill(cubeMaterial, NodeChunkStore.Air);

        for (int x = 0; x < Size; x++)
        for (int y = 0; y < Size; y++)
        for (int z = 0; z < Size; z++)
        {
            int here = SectionSample.Index(x, y, z);

            for (int a = 0; a < 3; a++)
            {
                Vector3I step = Axes[a];
                int there = SectionSample.Index(x + step.X, y + step.Y, z + step.Z);

                bool insideHere = levels[here] > 0f;
                bool insideThere = levels[there] > 0f;

                if (insideHere == insideThere)
                    continue;

                int inner = insideHere ? here : there;
                int outer = insideHere ? there : here;

                // Only a particle cell makes particle surface -- against the
                // open or into the rock -- and never against the unknown.
                byte material = sample.Materials[inner];

                byte beyond = sample.Materials[outer];

                if (!NodeTypes.IsParticle(material) || beyond == SectionSample.Unknown)
                    continue;

                // Into rock from a particle cell buried deep enough to hide the
                // rock's face: that stretch of shell is buried as well, and
                // drawing it would put the whole planet's rock top in the mesh.
                if (NodeTypes.IsRaw(beyond) && sample.Covers(inner))
                    continue;

                EmitQuad(sample, levels, cubeVertex, cubeMaterial, output.Particle(material),
                    material, grid, x, y, z, a, outwardPositive: insideHere);
            }
        }
    }

    /// <summary>
    /// Joins the four cubes around the edge leaving (x, y, z) along axis
    /// <paramref name="a"/>.
    /// </summary>
    private static void EmitQuad(SectionSample sample, float[] levels, int[] cubeVertex,
        byte[] cubeMaterial, MeshBuffers buffers, byte material, VoronoiGrid grid,
        int x, int y, int z, int a, bool outwardPositive)
    {
        Vector3I eb = Axes[(a + 1) % 3];
        Vector3I ec = Axes[(a + 2) % 3];
        var p = new Vector3I(x, y, z);

        int v0 = CubeVertex(sample, levels, cubeVertex, cubeMaterial, buffers, material, grid, p - eb - ec);
        int v1 = CubeVertex(sample, levels, cubeVertex, cubeMaterial, buffers, material, grid, p - ec);
        int v2 = CubeVertex(sample, levels, cubeVertex, cubeMaterial, buffers, material, grid, p);
        int v3 = CubeVertex(sample, levels, cubeVertex, cubeMaterial, buffers, material, grid, p - eb);

        // v0..v3 run counter-clockwise seen from +a. Godot's front face is
        // clockwise, so a surface facing +a takes them in reverse.
        Vector3 outward = (Vector3)Axes[a] * (outwardPositive ? 1f : -1f);

        if (outwardPositive)
            AddQuad(buffers, v0, v3, v2, v1, outward);
        else
            AddQuad(buffers, v0, v1, v2, v3, outward);
    }

    /// <summary>
    /// Two triangles for a quad already wound for Godot's front face.
    ///
    /// Sites are jittered, so a quad is not a square: it can be bent, or even
    /// slightly concave. Of its two diagonals, the one whose triangles both face
    /// outward is used; when both do, the shorter, so the fold follows the
    /// surface.
    /// </summary>
    private static void AddQuad(MeshBuffers buffers, int a, int b, int c, int d, Vector3 outward)
    {
        Vector3 pa = buffers.Vertices[a], pb = buffers.Vertices[b];
        Vector3 pc = buffers.Vertices[c], pd = buffers.Vertices[d];

        bool acWorks = Faces(pa, pb, pc, outward) && Faces(pa, pc, pd, outward);
        bool bdWorks = Faces(pb, pc, pd, outward) && Faces(pb, pd, pa, outward);

        if (acWorks && (!bdWorks || pa.DistanceSquaredTo(pc) <= pb.DistanceSquaredTo(pd)))
        {
            buffers.AddTriangle(a, b, c);
            buffers.AddTriangle(a, c, d);
        }
        else
        {
            buffers.AddTriangle(b, c, d);
            buffers.AddTriangle(b, d, a);
        }
    }

    /// <summary>Does a clockwise triangle face along a direction?</summary>
    private static bool Faces(Vector3 a, Vector3 b, Vector3 c, Vector3 direction) =>
        (c - a).Cross(b - a).Dot(direction) > 0f;

    /// <summary>
    /// The vertex of the lattice cube whose lowest corner is <paramref name="min"/>
    /// (section-local), made on first use.
    /// </summary>
    private static int CubeVertex(SectionSample sample, float[] levels, int[] cubeVertex,
        byte[] cubeMaterial, MeshBuffers buffers, byte material, VoronoiGrid grid, Vector3I min)
    {
        float node = grid.NodeSize;
        int key = ((min.X + 1) * CubeSpan + (min.Y + 1)) * CubeSpan + (min.Z + 1);

        if (cubeMaterial[key] == material)
            return cubeVertex[key];

        // Everything is worked out relative to the cube's own lowest corner,
        // and only then placed in the world. A cube on a section's edge is
        // built by both sections that share it, and this way both do exactly
        // the same arithmetic and get exactly the same vertex -- to the last
        // bit, which is what keeps the seam between them closed (see
        // NodeWorld's section nodes).
        Span<float> level = stackalloc float[8];
        Span<Vector3> lattice = stackalloc Vector3[8];
        Span<Vector3> site = stackalloc Vector3[8];
        Span<bool> rock = stackalloc bool[8];

        for (int i = 0; i < 8; i++)
        {
            int dx = i & 1, dy = (i >> 1) & 1, dz = (i >> 2) & 1;
            int index = SectionSample.Index(min.X + dx, min.Y + dy, min.Z + dz);

            level[i] = levels[index];
            lattice[i] = new Vector3(dx, dy, dz) * node;
            site[i] = lattice[i] + sample.Jitters[index];
            rock[i] = SectionSample.IsRawOrUnknown(sample.Materials[index]);
        }

        // Average of the crossings on the cube's twelve edges.
        //
        // Between particles and air the crossing is interpolated along the
        // LATTICE, which keeps the sheet's topology regular and a level surface
        // exactly level. Where either end is rock it is interpolated between
        // the two SITES instead: the midpoint of two sites lies on the wall
        // between their nodes, which is the rock's own face, so how far the
        // shell sinks into the rock is measured against the rock itself -- and
        // it can never reach the rock's site, its core.
        Vector3 sum = Vector3.Zero;
        int crossings = 0;

        for (int e = 0; e < 12; e++)
        {
            int from = EdgeFrom[e];
            int to = EdgeTo[e];

            if ((level[from] > 0f) == (level[to] > 0f))
                continue;

            float t = level[from] / (level[from] - level[to]);

            sum += rock[from] || rock[to]
                ? site[from].Lerp(site[to], t)
                : lattice[from].Lerp(lattice[to], t);

            crossings++;
        }

        Vector3 offset = crossings > 0
            ? sum / crossings
            : (lattice[0] + lattice[7]) * 0.5f;

        // The corner is a whole number of nodes from the world's middle, so
        // it is exact, and one addition places the vertex.
        Vector3 corner = grid.LatticePoint(sample.Origin + min);

        var kind = (ParticleNode)NodeTypes.Of(material);
        int vertex = buffers.AddVertex(corner + offset, Normal(level, lattice), kind.Colour);

        cubeMaterial[key] = material;
        cubeVertex[key] = vertex;
        return vertex;
    }

    /// <summary>
    /// The surface normal through a cube: the gradient of the levels, fitted by
    /// least squares to the eight corners. Levels fall going outward, so the
    /// normal is the gradient reversed.
    /// </summary>
    private static Vector3 Normal(ReadOnlySpan<float> level, ReadOnlySpan<Vector3> site)
    {
        Vector3 meanSite = Vector3.Zero;
        float meanLevel = 0f;

        for (int i = 0; i < 8; i++)
        {
            meanSite += site[i];
            meanLevel += level[i];
        }

        meanSite /= 8f;
        meanLevel /= 8f;

        // Normal equations: (sum q q^T) g = sum q (l - mean).
        Vector3 xx = Vector3.Zero, xy = Vector3.Zero, b = Vector3.Zero;

        for (int i = 0; i < 8; i++)
        {
            Vector3 q = site[i] - meanSite;
            float d = level[i] - meanLevel;

            xx += new Vector3(q.X * q.X, q.Y * q.Y, q.Z * q.Z);
            xy += new Vector3(q.X * q.Y, q.X * q.Z, q.Y * q.Z);
            b += q * d;
        }

        var m = new Basis(
            new Vector3(xx.X, xy.X, xy.Y),
            new Vector3(xy.X, xx.Y, xy.Z),
            new Vector3(xy.Y, xy.Z, xx.Z));

        float det = m.Determinant();
        Vector3 gradient = Mathf.Abs(det) > 1e-9f ? m.Inverse() * b : b;

        return gradient.LengthSquared() > 1e-12f ? -gradient.Normalized() : Vector3.Up;
    }

    private static Vector3 CornerOffset(int corner) =>
        new(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);

    // The twelve edges of a cube, as pairs of corner indices (bit 0 = x,
    // bit 1 = y, bit 2 = z).
    private static readonly int[] EdgeFrom = { 0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3 };
    private static readonly int[] EdgeTo = { 1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7 };
}
