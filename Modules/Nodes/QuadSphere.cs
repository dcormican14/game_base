using Godot;

namespace GameBase.Nodes;

/// <summary>
/// The planet's coordinate system: six square grids, one per cube face,
/// projected onto concentric spherical shells.
///
/// A cell is (face, u, v, shell) -- which of the six faces, where on it, and
/// how far down. Shell 0 is the surface and they count INWARD.
///
/// RESOLUTION HALVES AT POWER-OF-TWO BANDS
///
/// This is the one rule the whole system rests on, and getting it wrong is what
/// breaks a cubed sphere into floating ribbons.
///
/// A shell nearer the core has a smaller circumference, so a face grid of fixed
/// resolution would squeeze its cells to nothing. The count therefore has to
/// fall with depth. The temptation is to make it fall SMOOTHLY -- track the
/// circumference exactly, dropping a couple of cells per layer -- and that is
/// precisely what cannot work: a layer of 1278 cells does not tile against a
/// layer of 1280. Each cell's neighbour below is a fraction of a cell, nothing
/// lines up, and every shell drifts against the one above it.
///
/// So the resolution changes only by FACTORS OF TWO, and only at fixed
/// boundaries. Then one cell below sits under exactly one cell above, and one
/// cell above spans exactly 2x2 cells below. Edges meet, walls stack, and a
/// column dug straight down stays a column.
///
/// The price is that cells are not all the same size -- within a band a cell
/// shrinks as the radius does, by up to a factor of two before the next halving
/// catches it. Bounded distortion in exchange for exact alignment is the trade
/// every working implementation of this makes.
/// </summary>
/// <remarks>
/// Replaces an earlier attempt that tapered the resolution linearly (two cells
/// per shell). It rendered as detached curved sheets floating in space, because
/// no two adjacent layers shared an edge.
///
/// Approach follows the standard cubed-sphere/"quad sphere" construction as
/// described by Bowerbyte's Blocky Planet write-up, which arrives at the same
/// power-of-two shell banding for the same reason.
/// </remarks>
public static class QuadSphere
{
    /// <summary>Cells across one face at the surface, per face edge.
    /// A power of two, so halving stays exact all the way down.</summary>
    public static int SurfaceResolution(float radius, float nodeSize)
    {
        // The arc across one face is a quarter turn.
        float ideal = Mathf.Pi * 0.5f * radius / Mathf.Max(nodeSize, 0.0001f);

        // Round DOWN to a power of two.
        //
        // A power of two is required -- it is what makes every later halving
        // land on a whole number -- but which one is a free choice, and the two
        // candidates behave differently over a band.
        //
        // A band runs from its top radius down to half of it, so a cell's width
        // halves across it. Rounding DOWN starts the surface a little above one
        // node and ends the band a little below it, straddling node size where
        // the player actually is. Rounding UP starts at 0.74 and ends at 0.37 --
        // every block in the world smaller than a node, and the surface, which
        // is the part anyone sees, worst affected.
        int power = 1;
        while (power * 2 <= ideal && power < (1 << 20))
            power <<= 1;

        // Never finer than one chunk, so a face is always a whole number of
        // chunks across and no chunk straddles a face fold.
        return Mathf.Max(NodeChunkStore.ChunkSize, power);
    }

    /// <summary>
    /// How many times the face grid has halved by this shell.
    ///
    /// A band ends when its cells have shrunk to half a node, which -- since
    /// the radius falls linearly and the count is fixed within a band -- is
    /// exactly when the radius has halved. So the bands are the octaves of the
    /// radius, and the level is how many the shell has descended through.
    /// </summary>
    public static int LevelAt(int shell, float surfaceRadius, float nodeSize)
    {
        float radius = RadiusOf(shell, surfaceRadius, nodeSize);
        if (radius <= 0f)
            return 30;

        int level = 0;
        float bound = surfaceRadius * 0.5f;

        while (radius < bound && level < 30)
        {
            level++;
            bound *= 0.5f;
        }

        return level;
    }

    /// <summary>The face resolution at a given shell.</summary>
    public static int ResolutionAt(int shell, float surfaceRadius, float nodeSize)
    {
        int full = SurfaceResolution(surfaceRadius, nodeSize);
        int level = LevelAt(shell, surfaceRadius, nodeSize);

        // Floored at one chunk: below that a face is no longer addressable by
        // the chunk grid, and the innermost core is not worth the complexity.
        return Mathf.Max(NodeChunkStore.ChunkSize, full >> Mathf.Min(level, 20));
    }

    /// <summary>
    /// The OUTER radius of a shell. Shell 0's outer face is the planet's
    /// surface, and each shell is one node thick.
    /// </summary>
    public static float RadiusOf(int shell, float surfaceRadius, float nodeSize) =>
        surfaceRadius - shell * nodeSize;

    /// <summary>
    /// The radius below which the planet is not divided into cells at all.
    ///
    /// A face is floored at one chunk across, so past a certain depth the
    /// resolution stops halving while the radius keeps shrinking -- and the
    /// cells shrink with it, to a twentieth of a node at the very centre. That
    /// is a coordinate system degenerating, and nothing useful lives there:
    /// it is a handful of shells at the core of the planet.
    ///
    /// So the grid simply stops. Everything inside is solid core, addressed by
    /// nothing and reachable by no cell -- which is also the honest model of a
    /// planet's centre.
    /// </summary>
    public static float CoreRadius(float surfaceRadius, float nodeSize)
    {
        // Where a floor-resolution face still gives cells about a node wide.
        float minimum = NodeChunkStore.ChunkSize * nodeSize / (Mathf.Pi * 0.5f);

        // Never more than a quarter of the planet, so a small world still has
        // a sensible depth to dig through.
        return Mathf.Min(minimum, surfaceRadius * 0.25f);
    }

    /// <summary>How many shells of rock there are before the solid core.</summary>
    public static int ShellCount(float surfaceRadius, float nodeSize)
    {
        float size = Mathf.Max(nodeSize, 0.0001f);
        float depth = surfaceRadius - CoreRadius(surfaceRadius, size);

        return Mathf.Max(1, Mathf.FloorToInt(depth / size));
    }

    // ------------------------------------------------------------ projection

    /// <summary>
    /// A face-local coordinate in [-1, 1] mapped to a direction on the sphere.
    ///
    /// The tangent warp spreads cells evenly across the face's quarter turn.
    /// Without it they crowd toward the face centre and stretch at its edges;
    /// with it the area ratio across a face is about 1.4x rather than 5x.
    /// </summary>
    public static Vector3 Direction(int face, float s, float t)
    {
        float a = Mathf.Tan(Mathf.Clamp(s, -1.2f, 1.2f) * Mathf.Pi * 0.25f);
        float b = Mathf.Tan(Mathf.Clamp(t, -1.2f, 1.2f) * Mathf.Pi * 0.25f);

        return FaceDirection(face, a, b).Normalized();
    }

    /// <summary>
    /// The un-normalised direction for a point on a face.
    ///
    /// Laid out so each face's outward normal is one of the six axis
    /// directions, matching <see cref="NodeOrientation"/> -- so a cell's face
    /// IS its orientation and the two never need reconciling.
    ///
    /// The handedness is consistent across all six: (s, t) always runs
    /// right-handed about the outward normal, which is what lets neighbour
    /// finding across a fold be a coordinate permutation rather than a table of
    /// special cases.
    /// </summary>
    public static Vector3 FaceDirection(int face, float a, float b) => face switch
    {
        NodeOrientation.PosY => new Vector3(a, 1f, -b),
        NodeOrientation.NegY => new Vector3(a, -1f, b),
        NodeOrientation.PosX => new Vector3(1f, b, -a),
        NodeOrientation.NegX => new Vector3(-1f, b, a),
        NodeOrientation.PosZ => new Vector3(a, b, 1f),
        _ => new Vector3(-a, b, -1f),
    };

    /// <summary>Which face a direction belongs to: its dominant axis.</summary>
    public static int FaceOf(Vector3 n)
    {
        float ax = Mathf.Abs(n.X), ay = Mathf.Abs(n.Y), az = Mathf.Abs(n.Z);

        if (ay >= ax && ay >= az)
            return n.Y >= 0f ? NodeOrientation.PosY : NodeOrientation.NegY;

        if (ax >= az)
            return n.X >= 0f ? NodeOrientation.PosX : NodeOrientation.NegX;

        return n.Z >= 0f ? NodeOrientation.PosZ : NodeOrientation.NegZ;
    }

    /// <summary>
    /// The face-local (a, b) of a direction, before the tangent warp is undone.
    /// The exact inverse of <see cref="FaceDirection"/>.
    /// </summary>
    public static void FaceLocal(int face, Vector3 n, out float a, out float b)
    {
        switch (face)
        {
            case NodeOrientation.PosY: a = n.X / n.Y; b = -n.Z / n.Y; return;
            case NodeOrientation.NegY: a = n.X / -n.Y; b = n.Z / -n.Y; return;
            case NodeOrientation.PosX: a = -n.Z / n.X; b = n.Y / n.X; return;
            case NodeOrientation.NegX: a = n.Z / -n.X; b = n.Y / -n.X; return;
            case NodeOrientation.PosZ: a = n.X / n.Z; b = n.Y / n.Z; return;
            default: a = -n.X / -n.Z; b = n.Y / -n.Z; return;
        }
    }

    /// <summary>Undoes the tangent warp: a face-local axis back to [-1, 1].</summary>
    public static float Unwarp(float a) => Mathf.Atan(a) * 4f / Mathf.Pi;
}
