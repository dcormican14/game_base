using Godot;

namespace GameBase.Nodes;

/// <summary>
/// The cubed sphere as a cell grid the rest of the engine can address.
///
/// <see cref="CubedSphere"/> defines where cells ARE; this defines how they
/// are named and how you get from one to the next. A cell keeps the Vector3I
/// the whole engine already uses, read as (u, v, shell) with the face folded
/// into u — so storage, chunking, streaming and the dirty-set all work
/// unchanged on a coordinate that now means something different.
///
/// THE ONE STRUCTURAL CHANGE
///
/// On a Cartesian lattice a neighbour is `cell + offset`. Here it cannot be:
/// step off the edge of a face and you land on a different face, where the
/// axes may be swapped or reversed. So neighbour lookup becomes a call.
///
/// That is the whole cost of the move, and it is confined to this file. Every
/// caller that adds an offset to a cell asks <see cref="Neighbour"/> instead,
/// and everything downstream — shape rules, the interlock, culling — carries
/// on unchanged, because they only ever needed "the cell that way", not the
/// arithmetic that found it.
///
/// RESOLUTION BANDS
///
/// Face resolution halves with depth, so a cell's neighbours in u and v are
/// straightforward but its neighbour in SHELL may live on a coarser or finer
/// grid. Crossing a band boundary maps the coordinate accordingly; the cell
/// returned is the one containing the same direction.
/// </summary>
public sealed class SphereGrid
{
    private readonly float _surfaceRadius;
    private readonly float _nodeSize;
    private readonly Vector3 _origin;
    private readonly int _baseResolution;

    public SphereGrid(float surfaceRadius, float nodeSize, Vector3 origin)
    {
        _surfaceRadius = surfaceRadius;
        _nodeSize = Mathf.Max(nodeSize, 0.0001f);
        _origin = origin;
        _baseResolution = CubedSphere.FaceResolution(surfaceRadius, _nodeSize);
    }

    /// <summary>Cells across a face at the surface.</summary>
    public int BaseResolution => _baseResolution;

    /// <summary>Where the planet's centre is, in world space.</summary>
    public Vector3 Origin => _origin;

    /// <summary>Distance from the centre to the outermost shell.</summary>
    public float SurfaceRadius => _surfaceRadius;

    /// <summary>One node's size, in world units.</summary>
    public float NodeSize => _nodeSize;

    /// <summary>
    /// Packs a cell address into the Vector3I the engine passes around.
    ///
    /// The face is folded into u because u is already bounded by the face
    /// resolution, so faces stack without overlapping. Keeping the same type
    /// is what lets storage and streaming stay untouched.
    /// </summary>
    public Vector3I Pack(int face, int u, int v, int shell) =>
        new(face * _baseResolution + u, v, shell);

    /// <summary>Recovers a cell address from its packed form.</summary>
    public void Unpack(Vector3I cell, out int face, out int u, out int v, out int shell)
    {
        // Floor division, so cells below the origin do not fold to the wrong
        // face the way truncation would.
        face = cell.X >= 0
            ? cell.X / _baseResolution
            : (cell.X - _baseResolution + 1) / _baseResolution;

        u = cell.X - face * _baseResolution;
        v = cell.Y;
        shell = cell.Z;
    }

    /// <summary>The resolution of the face grid a cell lives on.</summary>
    public int ResolutionOf(Vector3I cell)
    {
        Unpack(cell, out _, out _, out _, out int shell);
        return CubedSphere.ResolutionAt(shell, _surfaceRadius, _nodeSize);
    }

    /// <summary>Is this a cell the grid actually contains?</summary>
    public bool Contains(Vector3I cell)
    {
        Unpack(cell, out int face, out int u, out int v, out int shell);
        if (face < 0 || face >= NodeOrientation.Count || shell < 0)
            return false;

        int resolution = CubedSphere.ResolutionAt(shell, _surfaceRadius, _nodeSize);
        return u >= 0 && u < resolution && v >= 0 && v < resolution;
    }

    /// <summary>The world position of a cell's centre.</summary>
    public Vector3 CentreOf(Vector3I cell)
    {
        Unpack(cell, out int face, out int u, out int v, out int shell);
        return CubedSphere.CentreOf(face, u, v, shell, _surfaceRadius, _nodeSize, _origin);
    }

    /// <summary>The cell containing a world point.</summary>
    public Vector3I CellAt(Vector3 point)
    {
        CubedSphere.CellAt(point, _surfaceRadius, _nodeSize, _origin,
            out int face, out int u, out int v, out int shell);

        return Pack(face, u, v, shell);
    }

    /// <summary>
    /// The direction a cell calls UP: away from the planet's centre.
    ///
    /// Exact rather than snapped to an axis, because on this grid a cell's
    /// outward direction is a property of where it is, not of which axis
    /// happens to be nearest.
    /// </summary>
    public Vector3 UpAt(Vector3I cell)
    {
        Vector3 centre = CentreOf(cell) - _origin;
        float length = centre.Length();
        return length < 0.0001f ? Vector3.Up : centre / length;
    }

    /// <summary>
    /// Where a point inside a cell lands in world space.
    ///
    /// `local` runs 0..1 across the cell in each axis: u across the face, v
    /// across it the other way, and w outward through the shell. This is what
    /// makes a node an ARC rather than a cube -- its eight corners land on two
    /// concentric spherical caps, and the faces between them curve with the
    /// planet.
    ///
    /// Coordinates outside 0..1 are allowed and meaningful, because node
    /// geometry reaches past its own cell: a crystal rim spans the lattice
    /// edge it won, and the topsoil's raised back stands a quarter-cell above
    /// its ceiling.
    /// </summary>
    public Vector3 PointIn(Vector3I cell, Vector3 local)
    {
        Unpack(cell, out int face, out int u, out int v, out int shell);

        int resolution = CubedSphere.ResolutionAt(shell, _surfaceRadius, _nodeSize);

        // Fractional grid position, so a point halfway across a cell is
        // halfway across its arc.
        float fu = u + local.X;
        float fv = v + local.Y;

        // Outward is -w: shells count inward, so a point at the top of a cell
        // (local.Z = 1) is one node further out than its base.
        float radius = CubedSphere.RadiusOf(shell, _surfaceRadius, _nodeSize)
            - _nodeSize * (1f - local.Z);

        // The same tangent warp CubedSphere uses, evaluated off the grid.
        float su = fu / resolution * 2f - 1f;
        float sv = fv / resolution * 2f - 1f;

        float au = Mathf.Tan(Mathf.Clamp(su, -1.5f, 1.5f) * Mathf.Pi * 0.25f);
        float av = Mathf.Tan(Mathf.Clamp(sv, -1.5f, 1.5f) * Mathf.Pi * 0.25f);

        return _origin + CubedSphere.FaceDirection(face, au, av).Normalized() * radius;
    }

    /// <summary>
    /// The cell one step from this one, in the local frame.
    ///
    /// `du` and `dv` step across the face, `dShell` steps radially (positive
    /// is INWARD, matching how shells are numbered). Stepping off a face edge
    /// lands on the adjoining face, which is why this cannot be an addition.
    ///
    /// Returns false when there is no such cell — past the outermost shell,
    /// or through the centre of the planet.
    /// </summary>
    public bool Neighbour(Vector3I cell, int du, int dv, int dShell, out Vector3I result)
    {
        Unpack(cell, out int face, out int u, out int v, out int shell);

        int targetShell = shell + dShell;
        result = default;

        if (targetShell < 0)
            return false;

        int here = CubedSphere.ResolutionAt(shell, _surfaceRadius, _nodeSize);
        int there = CubedSphere.ResolutionAt(targetShell, _surfaceRadius, _nodeSize);

        int nu = u + du;
        int nv = v + dv;

        // Still on this face: the common case, and pure arithmetic.
        if (nu >= 0 && nu < here && nv >= 0 && nv < here)
        {
            Rescale(ref nu, ref nv, here, there);
            result = Pack(face, nu, nv, targetShell);
            return true;
        }

        // Off the edge. The neighbouring face is found geometrically rather
        // than from a hand-written adjacency table: take the direction just
        // past this face's edge and ask which face owns it. That cannot
        // disagree with CubedSphere's own layout, which a table could.
        Vector3 direction = EdgeDirection(face, nu, nv, here);
        CubedSphere.CellAt(direction * CubedSphere.RadiusOf(targetShell, _surfaceRadius, _nodeSize)
                + _origin - direction * (_nodeSize * 0.5f),
            _surfaceRadius, _nodeSize, _origin,
            out int nf, out int nu2, out int nv2, out int ns);

        result = Pack(nf, nu2, nv2, ns);
        return true;
    }

    /// <summary>
    /// A direction just outside a face's grid, used to find what is over the
    /// edge. The tangent warp is applied the same way
    /// <see cref="CubedSphere.DirectionOf"/> does, extended past the face's
    /// bounds — which is exactly where the next face begins.
    /// </summary>
    private static Vector3 EdgeDirection(int face, int u, int v, int resolution)
    {
        float su = (u + 0.5f) / resolution * 2f - 1f;
        float sv = (v + 0.5f) / resolution * 2f - 1f;

        float au = Mathf.Tan(Mathf.Clamp(su, -1.4f, 1.4f) * Mathf.Pi * 0.25f);
        float av = Mathf.Tan(Mathf.Clamp(sv, -1.4f, 1.4f) * Mathf.Pi * 0.25f);

        return CubedSphere.FaceDirection(face, au, av).Normalized();
    }

    /// <summary>
    /// Maps face coordinates between two resolutions, for a step that crosses
    /// a band boundary.
    /// </summary>
    private static void Rescale(ref int u, ref int v, int from, int to)
    {
        if (from == to)
            return;

        // Scaled about the face's centre so the mapping is symmetric and a
        // cell maps to the one covering the same direction.
        u = Mathf.Clamp(u * to / from, 0, to - 1);
        v = Mathf.Clamp(v * to / from, 0, to - 1);
    }
}
