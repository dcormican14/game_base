using Godot;
using System;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// The shape of space: a Voronoi diagram over a jittered cubic lattice.
///
/// Every integer lattice cell <c>c</c> owns one SITE, sitting near
/// <c>c * NodeSize</c> but pushed off it by a hash of the address. A node is the
/// region of space closer to its own site than to any other, so its walls are
/// the perpendicular bisectors between neighbouring sites and two nodes always
/// agree exactly on the wall they share. The result reads as shattered rock
/// rather than as masonry, with no grid and no preferred direction.
///
/// Pure geometry: this knows nothing about planets, materials or storage. The
/// same grid serves the raw mesher (cell faces), the particle mesher (lattice
/// points) and every lookup from a world position to a node.
///
/// THE SITES ARE NOT STORED. A site is a pure function of its address, so any
/// thread can compute any site and all of them agree. Finding the node owning
/// a point tests the twenty-seven lattice cells around it and no more: with the
/// jitter capped at 0.5 a site strays at most a quarter of a cell, so the owner
/// is never further than one cell away.
/// </summary>
public sealed class VoronoiGrid
{
    public VoronoiGrid(float nodeSize, float jitter = 0.5f)
    {
        NodeSize = Mathf.Max(nodeSize, 0.01f);

        // Capped at 0.5 because the 27-cell lookup is only exact while a site
        // stays within a quarter cell of its lattice point.
        Jitter = Mathf.Clamp(jitter, 0f, 0.5f);
    }

    /// <summary>How wide one node is, in world units.</summary>
    public float NodeSize { get; }

    /// <summary>How far a site may sit from its lattice point, as a fraction of a cell.</summary>
    public float Jitter { get; }

    /// <summary>The most faces a cell can have; size face scratch by this.</summary>
    public const int MaxFaces = 26;

    /// <summary>The most corners all of a cell's faces can total.</summary>
    public const int MaxFaceCorners = MaxFaces * Hull.MaxCorners;

    // ------------------------------------------------------------ positions

    /// <summary>A cell's lattice point: where its site would sit with no jitter.</summary>
    public Vector3 LatticePoint(Vector3I cell) =>
        new(cell.X * NodeSize, cell.Y * NodeSize, cell.Z * NodeSize);

    /// <summary>A site's offset from its lattice point.</summary>
    public Vector3 JitterOf(Vector3I cell)
    {
        float spread = Jitter * NodeSize;

        return new Vector3(
            (Hash(cell, 1) - 0.5f) * spread,
            (Hash(cell, 2) - 0.5f) * spread,
            (Hash(cell, 3) - 0.5f) * spread);
    }

    /// <summary>Where a cell's site sits.</summary>
    public Vector3 SiteOf(Vector3I cell) => LatticePoint(cell) + JitterOf(cell);

    /// <summary>The lattice cell nearest a point, before jitter is considered.</summary>
    public Vector3I NearestLattice(Vector3 point) => new(
        Mathf.RoundToInt(point.X / NodeSize),
        Mathf.RoundToInt(point.Y / NodeSize),
        Mathf.RoundToInt(point.Z / NodeSize));

    /// <summary>The node whose region contains a point.</summary>
    public Vector3I CellAt(Vector3 point)
    {
        Vector3I basis = NearestLattice(point);
        Vector3 local = point - LatticePoint(basis);

        Vector3I best = basis;
        float bestDistance = float.MaxValue;

        // Measured relative to the basis lattice point, not in absolute
        // coordinates, so the comparison keeps its precision far from the
        // origin where the planet's surface is.
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            var step = new Vector3I(dx, dy, dz);
            Vector3I at = basis + step;

            Vector3 site = new Vector3(dx, dy, dz) * NodeSize + JitterOf(at);
            float distance = (site - local).LengthSquared();

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = at;
            }
        }

        return best;
    }

    // ---------------------------------------------------------------- faces

    /// <summary>
    /// Every face of a node.
    ///
    /// Fills <paramref name="neighbours"/> with the node behind each face,
    /// <paramref name="sides"/> with how many corners that face has, and
    /// <paramref name="corners"/> with the corners in world space, face after
    /// face. Returns how many faces were written. Faces are wound so Godot
    /// treats their outside as the front.
    /// </summary>
    public int Faces(Vector3I cell, Span<Vector3I> neighbours, Span<int> sides,
        Span<Vector3> corners)
    {
        CellGeometry geometry = Geometry(cell);
        Vector3 site = SiteOf(cell);

        int written = 0;

        for (int n = 0; n < geometry.Count; n++)
        {
            ref readonly CellFace face = ref geometry.Faces[n];

            neighbours[n] = geometry.Neighbours[n];
            sides[n] = face.Count;

            for (int c = 0; c < face.Count; c++)
                corners[written++] = site + face[c];
        }

        return geometry.Count;
    }

    /// <summary>The nodes sharing a face with this one. Returns how many.</summary>
    public int Neighbours(Vector3I cell, Span<Vector3I> neighbours)
    {
        CellGeometry geometry = Geometry(cell);

        for (int n = 0; n < geometry.Count; n++)
            neighbours[n] = geometry.Neighbours[n];

        return geometry.Count;
    }

    // ---------------------------------------------------------- the geometry

    /// <summary>One cell's faces, in site-local space. Immutable once built.</summary>
    private sealed class CellGeometry
    {
        public int Count;
        public Vector3I[] Neighbours;
        public CellFace[] Faces;
    }

    /// <summary>
    /// A cell's geometry, from a per-thread cache.
    ///
    /// Building a cell clips thirty-odd planes against each other and is the
    /// most expensive thing in the grid. A cell's shape depends only on its
    /// address, so a built cell is good for the life of the grid and the cache
    /// never needs invalidating. Per thread, because sections mesh on workers
    /// and a shared cache would need a lock costing more than a hit saves.
    /// Cleared wholesale when full: meshing walks a section at a time, so the
    /// working set is one section and its shell.
    /// </summary>
    private CellGeometry Geometry(Vector3I cell)
    {
        CellCache cache = _cache;

        if (cache == null || cache.Owner != this)
        {
            cache = new CellCache(this);
            _cache = cache;
        }

        if (cache.Entries.TryGetValue(cell, out CellGeometry hit))
            return hit;

        CellGeometry built = Build(cell);

        if (cache.Entries.Count >= CacheCapacity)
            cache.Entries.Clear();

        cache.Entries[cell] = built;
        return built;
    }

    /// <summary>A section (512 cells) plus the shell of neighbours around it, with room.</summary>
    private const int CacheCapacity = 4096;

    private sealed class CellCache
    {
        public CellCache(VoronoiGrid owner) => Owner = owner;

        public readonly VoronoiGrid Owner;
        public readonly Dictionary<Vector3I, CellGeometry> Entries = new();
    }

    [ThreadStatic] private static CellCache _cache;

    /// <summary>The 26 lattice offsets whose bisectors can bound a cell.</summary>
    private static readonly Vector3I[] Window = BuildWindow();

    private static Vector3I[] BuildWindow()
    {
        var window = new Vector3I[26];
        int count = 0;

        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (dx != 0 || dy != 0 || dz != 0)
                window[count++] = new Vector3I(dx, dy, dz);
        }

        return window;
    }

    /// <summary>Clips a box down by every neighbour's bisector.</summary>
    private CellGeometry Build(Vector3I cell)
    {
        // Every site sits within a quarter cell of its lattice point, so no
        // wall is further than one and a half cells out.
        Span<Hull.Plane> planes = stackalloc Hull.Plane[Hull.MaxPlanes];
        var hull = new Hull(planes, NodeSize * 1.5f);

        Span<Vector3I> owner = stackalloc Vector3I[Hull.MaxPlanes];
        Vector3 mine = JitterOf(cell);

        foreach (Vector3I step in Window)
        {
            Vector3I at = cell + step;

            // Relative to this site, computed from small numbers: the lattice
            // step and the two jitters. Subtracting two absolute site positions
            // far from the origin would throw away most of the precision.
            Vector3 toward = new Vector3(step.X, step.Y, step.Z) * NodeSize
                + JitterOf(at) - mine;

            float length = toward.Length();
            if (length < 0.000001f)
                continue;

            owner[hull.PlaneCount] = at;
            hull.Add(toward / length, length * 0.5f);
        }

        Span<CellFace> faces = stackalloc CellFace[MaxFaces];
        Span<Vector3I> walls = stackalloc Vector3I[MaxFaces];
        int count = 0;

        for (int n = Hull.BoxPlanes; n < hull.PlaneCount && count < MaxFaces; n++)
        {
            if (!hull.Face(n, out CellFace face))
                continue;

            walls[count] = owner[n];
            faces[count] = face;
            count++;
        }

        return new CellGeometry
        {
            Count = count,
            Neighbours = walls[..count].ToArray(),
            Faces = faces[..count].ToArray(),
        };
    }

    // ------------------------------------------------------------------ hash

    /// <summary>
    /// One deterministic value in 0..1 per cell and stream: the whole site
    /// table, in a few integer operations.
    /// </summary>
    private static float Hash(Vector3I cell, int stream)
    {
        unchecked
        {
            int n = cell.X * 73856093
                ^ cell.Y * 19349663
                ^ cell.Z * 83492791
                ^ stream * -1640531527;

            n ^= n >> 13;
            n *= 1274126177;
            n ^= n >> 16;

            return ((n & 0xFFFF) + 0.5f) / 65536f;
        }
    }

    /// <summary>A stable bit per cell, for alternating shades between neighbours.</summary>
    public static bool Parity(Vector3I cell) => Hash(cell, 7) < 0.5f;
}
