using Godot;

namespace GameBase.Nodes;

/// <summary>
/// The planet's coordinate system: cells are ARCS on spherical shells rather
/// than cubes on a Cartesian lattice.
///
/// WHY NOT A CUBIC LATTICE
///
/// A sphere carved out of cubes has a stepped surface, and no amount of
/// reshaping the nodes hides it: measured on a perfectly smooth planet, the
/// cells making up one surface layer straddle two different radii everywhere,
/// so the ground rises and falls by a whole node as you walk. That step is in
/// the coordinate system, not the geometry drawn inside it.
///
/// WHY NOT LATITUDE AND LONGITUDE
///
/// The obvious spherical grid has cells that converge at the poles — at 89
/// degrees a cell is 1.7% of its equatorial width — which is worse
/// non-uniformity than the problem being solved.
///
/// THE CUBED SPHERE
///
/// Six square grids, one per face of a cube, each projected onto the sphere.
/// A cell is (face, u, v, shell): which face, where on it, and how far out.
///
/// With the standard tangent warp the cells stay within about 1.4x of each
/// other's area across a whole face, against 60x for latitude/longitude. There
/// are no poles, no convergence, and the six faces are the same six
/// orientations node geometry already uses — so a node's local "up" is exactly
/// its face's outward normal, everywhere, by construction.
///
/// RESOLUTION FOLLOWS RADIUS
///
/// A face grid of fixed resolution would make cells shrink with depth, which
/// is the pole problem again in the radial direction: at radius 50 a cell
/// sized for radius 800 is a sixteenth of a node across. So the resolution
/// HALVES each time the radius halves, which holds cell width at 1.00 nodes
/// from the crust to the core.
///
/// That is also what the planet wanted anyway. Deep shells hold proportionally
/// fewer cells, so the core costs a fraction of what a uniform grid would —
/// the same thinning that was previously done by carving caverns out of a
/// dense lattice, now falling out of the coordinate system.
/// </summary>
public static class CubedSphere
{
    /// <summary>
    /// Cells across one face at the reference radius.
    ///
    /// Chosen so a cell is one node wide there: a face spans a quarter turn,
    /// so its arc is (pi/2) * radius and the count is that divided by the node
    /// size. Everything else is derived from it.
    /// </summary>
    /// <remarks>
    /// ROUNDED UP TO A WHOLE NUMBER OF CHUNKS, so a face begins and ends on a
    /// chunk boundary.
    ///
    /// Faces are stacked along the packed u axis, so a resolution that is not
    /// a multiple of the chunk size leaves one chunk per face straddling the
    /// fold -- half its cells on one face, half on the next. Measured, that
    /// made the chunk at the end of a face report itself as belonging to the
    /// NEXT face, and residency never crossed a seam: 265 resident chunks, all
    /// on face 0 of 6.
    ///
    /// Rounding up makes cells very slightly smaller than one node rather than
    /// slightly larger, which is the harmless direction.
    /// </remarks>
    public static int FaceResolution(float radius, float nodeSize)
    {
        int ideal = Mathf.Max(1,
            Mathf.RoundToInt(Mathf.Pi * 0.5f * radius / Mathf.Max(nodeSize, 0.0001f)));

        const int Chunk = NodeChunkStore.ChunkSize;
        return (ideal + Chunk - 1) / Chunk * Chunk;
    }

    /// <summary>
    /// The resolution of the face grid at a given shell.
    ///
    /// Halves with each halving of the radius, so cells stay one node wide at
    /// every depth rather than converging on the core.
    /// </summary>
    public static int ResolutionAt(int shell, float surfaceRadius, float nodeSize)
    {
        int full = FaceResolution(surfaceRadius, nodeSize);
        int level = LevelOf(shell, surfaceRadius, nodeSize);
        return Mathf.Max(1, full >> level);
    }

    /// <summary>
    /// How many times the resolution has halved by this shell.
    ///
    /// Zero in the outermost octave, one in the next, and so on. Bands rather
    /// than a continuous function, because the grid has to be a grid: cells
    /// must line up with their neighbours, which they only do if the
    /// resolution is constant across a band and changes by exactly a factor of
    /// two between bands.
    /// </summary>
    public static int LevelOf(int shell, float surfaceRadius, float nodeSize)
    {
        float radius = RadiusOf(shell, surfaceRadius, nodeSize);
        if (radius >= surfaceRadius * 0.5f)
            return 0;

        int level = 0;
        float bound = surfaceRadius * 0.5f;

        while (radius < bound && level < 30)
        {
            level++;
            bound *= 0.5f;
        }

        return level;
    }

    /// <summary>
    /// The radius of a shell. Shell 0 is the surface and they count inward, so
    /// a shell is one node thick everywhere.
    /// </summary>
    public static float RadiusOf(int shell, float surfaceRadius, float nodeSize) =>
        surfaceRadius - shell * nodeSize;

    /// <summary>
    /// The world-space direction of a cell's centre.
    ///
    /// `u` and `v` index the face grid; the tangent warp spreads them evenly
    /// over the face's quarter turn, which is what keeps cell size uniform
    /// rather than bunching them at the face centre.
    /// </summary>
    public static Vector3 DirectionOf(int face, float u, float v, int resolution)
    {
        // Grid index to the face's [-1, 1] square, at cell centres.
        float su = (u + 0.5f) / resolution * 2f - 1f;
        float sv = (v + 0.5f) / resolution * 2f - 1f;

        // The tangent warp. Without it cells crowd toward the face centre and
        // stretch at its edges by the same factor lat/long stretches at the
        // poles, only milder.
        float au = Mathf.Tan(su * Mathf.Pi * 0.25f);
        float av = Mathf.Tan(sv * Mathf.Pi * 0.25f);

        return FaceDirection(face, au, av).Normalized();
    }

    /// <summary>
    /// The un-normalised direction for a point on a face, in world axes.
    ///
    /// The faces are laid out so each one's outward normal is one of the six
    /// axis directions, matching <see cref="NodeOrientation"/> exactly — so a
    /// cell's face IS its orientation and the two never have to be reconciled.
    /// </summary>
    public static Vector3 FaceDirection(int face, float au, float av) => face switch
    {
        NodeOrientation.PosY => new Vector3(au, 1f, av),
        NodeOrientation.NegY => new Vector3(au, -1f, -av),
        NodeOrientation.PosX => new Vector3(1f, au, av),
        NodeOrientation.NegX => new Vector3(-1f, au, -av),
        NodeOrientation.PosZ => new Vector3(au, av, 1f),
        _ => new Vector3(-au, av, -1f),
    };

    /// <summary>
    /// The world position of a cell's centre.
    /// </summary>
    public static Vector3 CentreOf(int face, int u, int v, int shell,
        float surfaceRadius, float nodeSize, Vector3 origin)
    {
        int resolution = ResolutionAt(shell, surfaceRadius, nodeSize);
        float radius = RadiusOf(shell, surfaceRadius, nodeSize);

        // Half a node inward, so the cell's centre sits between its inner and
        // outer faces rather than on the outer one.
        return origin + DirectionOf(face, u, v, resolution) * (radius - nodeSize * 0.5f);
    }

    /// <summary>
    /// Which cell a world point falls in.
    ///
    /// The inverse of <see cref="CentreOf"/>, and the bridge every part of the
    /// engine that still thinks in world space crosses: the player's position,
    /// a ray hit, an edit.
    /// </summary>
    public static void CellAt(Vector3 point, float surfaceRadius, float nodeSize,
        Vector3 origin, out int face, out int u, out int v, out int shell)
    {
        Vector3 d = point - origin;
        float distance = d.Length();

        if (distance < 0.0001f)
        {
            face = NodeOrientation.PosY;
            u = v = 0;
            shell = Mathf.RoundToInt(surfaceRadius / nodeSize);
            return;
        }

        // The shell first, since resolution depends on it. Cell centres sit
        // half a node inside their shell radius, so the floor lands on the
        // right shell without an offset.
        shell = Mathf.Max(0, Mathf.FloorToInt((surfaceRadius - distance) / nodeSize));

        int resolution = ResolutionAt(shell, surfaceRadius, nodeSize);

        // Which face: the dominant axis, exactly as NodeOrientation decides it,
        // so a cell's face and its geometry's orientation always agree.
        Vector3 n = d / distance;
        face = FaceOf(n);

        // Undo the face layout to recover the face-local coordinates.
        FaceLocal(face, n, out float au, out float av);

        // Undo the tangent warp.
        float su = Mathf.Atan(au) * 4f / Mathf.Pi;
        float sv = Mathf.Atan(av) * 4f / Mathf.Pi;

        u = Mathf.Clamp(Mathf.FloorToInt((su + 1f) * 0.5f * resolution), 0, resolution - 1);
        v = Mathf.Clamp(Mathf.FloorToInt((sv + 1f) * 0.5f * resolution), 0, resolution - 1);
    }

    /// <summary>Which face a direction belongs to.</summary>
    public static int FaceOf(Vector3 n)
    {
        float ax = Mathf.Abs(n.X);
        float ay = Mathf.Abs(n.Y);
        float az = Mathf.Abs(n.Z);

        if (ay >= ax && ay >= az)
            return n.Y >= 0f ? NodeOrientation.PosY : NodeOrientation.NegY;

        if (ax >= az)
            return n.X >= 0f ? NodeOrientation.PosX : NodeOrientation.NegX;

        return n.Z >= 0f ? NodeOrientation.PosZ : NodeOrientation.NegZ;
    }

    /// <summary>The face-local tangent coordinates of a direction.</summary>
    private static void FaceLocal(int face, Vector3 n, out float au, out float av)
    {
        switch (face)
        {
            case NodeOrientation.PosY: au = n.X / n.Y; av = n.Z / n.Y; return;
            case NodeOrientation.NegY: au = n.X / -n.Y; av = -n.Z / -n.Y; return;
            case NodeOrientation.PosX: au = n.Y / n.X; av = n.Z / n.X; return;
            case NodeOrientation.NegX: au = n.Y / -n.X; av = -n.Z / -n.X; return;
            case NodeOrientation.PosZ: au = n.X / n.Z; av = n.Y / n.Z; return;
            default: au = -n.X / -n.Z; av = n.Y / -n.Z; return;
        }
    }
}
