using Godot;
using System;

namespace GameBase.Nodes.Meshing;

/// <summary>
/// Draws raw nodes as the polyhedra the Voronoi diagram gives them.
///
/// The rule is one line: a face is drawn when the node behind it does not
/// cover it (see <see cref="SectionSample.Covers"/>). Where particle nodes lie
/// against rock, both surfaces are drawn: the particle shell sinks a little way
/// into the rock, and whichever stands further out is what is seen. Everything else is making
/// that cheap. A node whose 26 lattice neighbours all cover it cannot show a
/// single face -- that is every node inside the planet -- so it is skipped
/// before its geometry is ever built, which is the expensive part.
/// </summary>
internal sealed class RawNodeMesher : INodeMesher
{
    public NodeForm Form => NodeForm.Raw;

    public void Build(SectionSample sample, VoronoiGrid grid, SectionGeometry output)
    {
        if (!sample.HasOpenings)
            return;

        MeshBuffers buffers = output.Raw;
        Vector3 origin = grid.LatticePoint(sample.Origin);

        Span<Vector3I> neighbours = stackalloc Vector3I[VoronoiGrid.MaxFaces];
        Span<int> sides = stackalloc int[VoronoiGrid.MaxFaces];
        Span<Vector3> corners = stackalloc Vector3[VoronoiGrid.MaxFaceCorners];

        const int Size = SectionSample.Size;

        for (int x = 0; x < Size; x++)
        for (int y = 0; y < Size; y++)
        for (int z = 0; z < Size; z++)
        {
            byte material = sample.Materials[SectionSample.Index(x, y, z)];

            if (material == SectionSample.Unknown || !NodeTypes.IsRaw(material))
                continue;

            if (Enclosed(sample, x, y, z))
                continue;

            var cell = new Vector3I(sample.Origin.X + x, sample.Origin.Y + y, sample.Origin.Z + z);
            var kind = (RawNode)NodeTypes.Of(material);
            Color color = VoronoiGrid.Parity(cell) ? kind.ShadeA : kind.ShadeB;

            int faces = grid.Faces(cell, neighbours, sides, corners);
            int at = 0;

            for (int n = 0; n < faces; n++)
            {
                int count = sides[n];
                Vector3I step = neighbours[n] - cell;

                bool hidden = sample.Covers(SectionSample.Index(x + step.X, y + step.Y, z + step.Z));

                if (!hidden)
                    AddPolygon(buffers, corners.Slice(at, count), origin, color);

                at += count;
            }
        }
    }

    /// <summary>Do all 26 lattice neighbours cover this cell?</summary>
    private static bool Enclosed(SectionSample sample, int x, int y, int z)
    {
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if ((dx | dy | dz) != 0 && !sample.Covers(SectionSample.Index(x + dx, y + dy, z + dz)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Emits one convex face as a fan with a flat normal. The corners arrive
    /// already wound for Godot's front face (see <see cref="Hull.Face"/>).
    ///
    /// EVERY face, however small. A sliver between rock and air is still the
    /// only thing between them: leaving it out once seemed free, and made
    /// pinholes a ray could pass straight through the planet by.
    /// </summary>
    private static void AddPolygon(MeshBuffers buffers, ReadOnlySpan<Vector3> corners,
        Vector3 origin, Color color)
    {
        if (corners.Length < 3)
            return;

        // Godot's front face is clockwise, so the outward normal is the
        // reversed cross product of the fan.
        Vector3 sum = Vector3.Zero;
        for (int c = 1; c + 1 < corners.Length; c++)
            sum += (corners[c + 1] - corners[0]).Cross(corners[c] - corners[0]);

        float twiceArea = sum.Length();
        if (twiceArea < 1e-8f)
            return;

        Vector3 normal = sum / twiceArea;
        int first = buffers.Vertices.Count;

        foreach (Vector3 corner in corners)
            buffers.AddVertex(corner - origin, normal, color);

        for (int c = 1; c + 1 < corners.Length; c++)
            buffers.AddTriangle(first, first + c, first + c + 1);
    }
}
