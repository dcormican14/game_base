using Godot;
using System;
using System.Collections.Generic;
using GameBase.Nodes;

namespace GameBase.Tests;

/// <summary>The Voronoi grid: lookups round-trip and cells tile space exactly.</summary>
public sealed class GridTests : TestSuite
{
    private readonly VoronoiGrid _grid = new(2f);

    /// <summary>Cells scattered near the origin and out where a planet's surface is.</summary>
    private static IEnumerable<Vector3I> Sample(int count)
    {
        var random = new Random(1234);

        for (int n = 0; n < count; n++)
        {
            int spread = n % 2 == 0 ? 50 : 4000;
            yield return new Vector3I(
                random.Next(-spread, spread), random.Next(-spread, spread), random.Next(-spread, spread));
        }
    }

    [Test]
    public void SiteLiesInItsOwnCell()
    {
        foreach (Vector3I cell in Sample(2000))
            Equal(_grid.CellAt(_grid.SiteOf(cell)), cell, $"cell owning the site of {cell}");
    }

    [Test]
    public void WallsAreBisectors()
    {
        Span<Vector3I> neighbours = stackalloc Vector3I[VoronoiGrid.MaxFaces];
        Span<int> sides = stackalloc int[VoronoiGrid.MaxFaces];
        Span<Vector3> corners = stackalloc Vector3[VoronoiGrid.MaxFaceCorners];

        foreach (Vector3I cell in Sample(200))
        {
            int faces = _grid.Faces(cell, neighbours, sides, corners);
            Check(faces >= 8, $"{cell} has only {faces} faces");

            Vector3 mine = _grid.SiteOf(cell);
            int at = 0;

            for (int f = 0; f < faces; f++)
            {
                Vector3 theirs = _grid.SiteOf(neighbours[f]);

                for (int c = 0; c < sides[f]; c++)
                {
                    Vector3 corner = corners[at + c];
                    Near(corner.DistanceTo(mine), corner.DistanceTo(theirs), 0.01f,
                        $"corner of the wall between {cell} and {neighbours[f]}");
                }

                at += sides[f];
            }
        }
    }

    [Test]
    public void FacesWindOutward()
    {
        Span<Vector3I> neighbours = stackalloc Vector3I[VoronoiGrid.MaxFaces];
        Span<int> sides = stackalloc int[VoronoiGrid.MaxFaces];
        Span<Vector3> corners = stackalloc Vector3[VoronoiGrid.MaxFaceCorners];

        foreach (Vector3I cell in Sample(200))
        {
            int faces = _grid.Faces(cell, neighbours, sides, corners);
            Vector3 site = _grid.SiteOf(cell);
            int at = 0;

            for (int f = 0; f < faces; f++)
            {
                ReadOnlySpan<Vector3> face = corners.Slice(at, sides[f]);
                at += sides[f];

                // Godot's front face is clockwise, so this is the outward normal.
                Vector3 facing = Vector3.Zero;
                Vector3 middle = Vector3.Zero;

                for (int c = 1; c + 1 < face.Length; c++)
                    facing += (face[c + 1] - face[0]).Cross(face[c] - face[0]);

                foreach (Vector3 corner in face)
                    middle += corner;

                middle /= face.Length;
                Check(facing.Dot(middle - site) > 0f, $"a face of {cell} is wound inward");
            }
        }
    }

    [Test]
    public void NeighboursAreMutual()
    {
        Span<Vector3I> mine = stackalloc Vector3I[VoronoiGrid.MaxFaces];
        Span<Vector3I> theirs = stackalloc Vector3I[VoronoiGrid.MaxFaces];

        foreach (Vector3I cell in Sample(100))
        {
            int count = _grid.Neighbours(cell, mine);

            for (int n = 0; n < count; n++)
            {
                int back = _grid.Neighbours(mine[n], theirs);
                Check(theirs[..back].Contains(cell), $"{mine[n]} does not list {cell} back");
            }
        }
    }
}
