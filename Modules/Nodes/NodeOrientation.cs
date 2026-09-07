using Godot;

namespace GameBase.Nodes;

/// <summary>
/// Which way is UP for a node, and how to talk to a shape rule that assumes
/// it is +Y.
///
/// THE PROBLEM
///
/// Node geometry has a top. Topsoil bevels the edges that face open sky,
/// builds its back up toward rising ground, and fills solid when something
/// sits above it — all of which are statements about a direction. Written for
/// a flat world that direction is world +Y, and on a planet that is right only
/// at the north pole. Everywhere else the soil caps the wrong face: on the
/// equator it grows its surface out of a cliff wall, and underneath the planet
/// it caps the ceiling.
///
/// The player already turns to face the local vertical, so the ground has to
/// turn with them or walking round the globe feels like walking round the
/// OUTSIDE of a boulder.
///
/// THE FIX, AND WHY IT KEEPS O(1)
///
/// A cubic lattice offers six axis directions, and the outward direction at
/// any cell is nearest to exactly one of them. So rather than rewriting the
/// shape rules to take a vector — which would make every one of them do
/// trigonometry per node — the neighbour mask is RELABELLED: the axis that
/// points away from the core is renamed +Y before the rule sees it, and the
/// rule runs unchanged.
///
/// That costs one comparison of three magnitudes and a table lookup. The shape
/// is still a pure function of the cell and the seed, still has no neighbour
/// queries beyond the mask the mesher already had, and still keys a geometry
/// cache on a small integer.
///
/// WHAT IT LOOKS LIKE
///
/// The six orientations partition the sphere into six faces, the way a cube
/// map does. Inside a face every node agrees about up, so the surface reads as
/// continuous ground. The boundaries sit 45 degrees from each axis, where the
/// outward direction is equally close to two of them — and there the soil
/// changes which face it caps. That is a real seam, and it is the price of
/// keeping the shape a table lookup rather than a rotation per node.
/// </summary>
public static class NodeOrientation
{
    /// <summary>The six axis directions a node's up can take.</summary>
    public const int PosY = 0;
    public const int NegY = 1;
    public const int PosX = 2;
    public const int NegX = 3;
    public const int PosZ = 4;
    public const int NegZ = 5;

    /// <summary>How many distinct orientations there are.</summary>
    public const int Count = 6;

    /// <summary>
    /// Which axis points most directly away from `centre` at this cell.
    ///
    /// Ties are broken in a fixed order, so two nodes on a boundary always
    /// agree about which face they belong to — a tie broken differently either
    /// side would tear the seam open rather than merely bending it.
    /// </summary>
    public static int Facing(Vector3I cell, Vector3 centre)
    {
        float x = cell.X + 0.5f - centre.X;
        float y = cell.Y + 0.5f - centre.Y;
        float z = cell.Z + 0.5f - centre.Z;

        float ax = Mathf.Abs(x);
        float ay = Mathf.Abs(y);
        float az = Mathf.Abs(z);

        if (ay >= ax && ay >= az)
            return y >= 0f ? PosY : NegY;

        if (ax >= az)
            return x >= 0f ? PosX : NegX;

        return z >= 0f ? PosZ : NegZ;
    }

    /// <summary>
    /// The world direction one of the six orientations calls "up".
    /// </summary>
    public static Vector3I UpOf(int orientation) => orientation switch
    {
        PosY => new Vector3I(0, 1, 0),
        NegY => new Vector3I(0, -1, 0),
        PosX => new Vector3I(1, 0, 0),
        NegX => new Vector3I(-1, 0, 0),
        PosZ => new Vector3I(0, 0, 1),
        _ => new Vector3I(0, 0, -1),
    };

    /// <summary>
    /// The world direction a rule's local face bit refers to, for a given
    /// orientation.
    ///
    /// The inverse of what <see cref="Rebase"/> does to a whole mask, and what
    /// lets a caller SAMPLE the world along the rule's own axes rather than
    /// relabelling what it already sampled along the world's.
    ///
    /// That distinction matters on a curved surface. A sphere carved from
    /// cubes is a staircase: walk round it and the ground steps down about once
    /// per node, purely from curvature. Sampled along world axes, a soil node
    /// on that staircase sees a solid uphill neighbour with another solid cell
    /// above it -- which is exactly what the rule reads as "the ground rises
    /// here" -- so it adds a lip and bevels the downhill side. Measured on a
    /// perfectly smooth sphere, that fired on 7% of surface nodes near an axis
    /// and 84% of them at 40 degrees away, which is the tilted, stepped look a
    /// flat planet should not have.
    ///
    /// Sampling along the LOCAL frame instead follows the curve: the cell the
    /// rule calls "above" is the one further from the core, so a smooth sphere
    /// reads as smooth ground and only real terrain makes it step.
    /// </summary>
    public static Vector3I FaceOffset(int orientation, int face)
    {
        if (orientation == PosY)
            return NodeFace.Offsets[face];

        Basis(orientation, out Vector3I right, out Vector3I up, out Vector3I forward);
        Vector3I local = NodeFace.Offsets[face];

        return right * local.X + up * local.Y + forward * local.Z;
    }

    /// <summary>
    /// Rewrites a neighbour mask into the frame a shape rule expects.
    ///
    /// The rule believes +Y is up and reasons about its four horizontal sides
    /// and their diagonals. This maps each of those directions onto the world
    /// direction that plays the same role for this orientation, so the rule's
    /// own vocabulary is preserved and only the meaning of its axes changes.
    ///
    /// Built by rotating the direction each <see cref="NodeFace"/> bit stands
    /// for, so faces, horizontal diagonals and upper diagonals all move
    /// together and stay consistent with one another.
    /// </summary>
    public static int Rebase(int mask, int orientation)
    {
        if (orientation == PosY)
            return mask;

        int[] map = Maps[orientation];
        int result = 0;

        for (int face = 0; face < NodeFace.Count; face++)
        {
            if ((mask & 1 << map[face]) != 0)
                result |= 1 << face;
        }

        return result;
    }

    /// <summary>
    /// For each orientation, which WORLD face bit stands in for each LOCAL
    /// face bit. Built once, because the mesher asks per node.
    /// </summary>
    private static readonly int[][] Maps = BuildMaps();

    private static int[][] BuildMaps()
    {
        var maps = new int[Count][];

        for (int orientation = 0; orientation < Count; orientation++)
        {
            var map = new int[NodeFace.Count];
            Basis(orientation, out Vector3I right, out Vector3I up, out Vector3I forward);

            for (int face = 0; face < NodeFace.Count; face++)
            {
                Vector3I local = NodeFace.Offsets[face];

                // The local direction expressed in world axes.
                Vector3I world = right * local.X + up * local.Y + forward * local.Z;
                map[face] = IndexOf(world);
            }

            maps[orientation] = map;
        }

        return maps;
    }

    /// <summary>
    /// A right-handed frame for an orientation: which world directions play
    /// the parts of local +X, +Y and +Z.
    ///
    /// The choice of right and forward within a face is arbitrary — the shape
    /// rules are symmetric under a quarter turn about up — so it only has to be
    /// consistent, which a fixed table guarantees.
    /// </summary>
    private static void Basis(int orientation,
        out Vector3I right, out Vector3I up, out Vector3I forward)
    {
        switch (orientation)
        {
            case PosY:
                right = new Vector3I(1, 0, 0);
                up = new Vector3I(0, 1, 0);
                forward = new Vector3I(0, 0, 1);
                return;

            case NegY:
                right = new Vector3I(1, 0, 0);
                up = new Vector3I(0, -1, 0);
                forward = new Vector3I(0, 0, -1);
                return;

            case PosX:
                right = new Vector3I(0, 0, -1);
                up = new Vector3I(1, 0, 0);
                forward = new Vector3I(0, 1, 0);
                return;

            case NegX:
                right = new Vector3I(0, 0, 1);
                up = new Vector3I(-1, 0, 0);
                forward = new Vector3I(0, 1, 0);
                return;

            case PosZ:
                right = new Vector3I(1, 0, 0);
                up = new Vector3I(0, 0, 1);
                forward = new Vector3I(0, -1, 0);
                return;

            default:
                right = new Vector3I(1, 0, 0);
                up = new Vector3I(0, 0, -1);
                forward = new Vector3I(0, 1, 0);
                return;
        }
    }

    /// <summary>
    /// Turns a sub-cell coordinate from a shape rule's own frame into the
    /// world's.
    ///
    /// The rule solves its shape believing +Y is up, so its vertices and its
    /// occlusion cells come out in that frame. Rotating them here is what
    /// actually tilts the geometry to face away from the core -- rebasing the
    /// mask alone only changes which shape is CHOSEN, not which way it points,
    /// and soil would still bevel toward world up while deciding as though it
    /// were on a wall.
    ///
    /// Sub-cell coordinates run 0..Sub-1 inside the node and may reach one
    /// beyond either end, so the rotation is about the node's CENTRE rather
    /// than a corner. Working in doubled coordinates keeps that centre on an
    /// integer and the arithmetic exact.
    /// </summary>
    public static void ToWorld(int orientation, int sub, int i, int j, int k,
        out int wi, out int wj, out int wk)
    {
        if (orientation == PosY)
        {
            wi = i; wj = j; wk = k;
            return;
        }

        Basis(orientation, out Vector3I right, out Vector3I up, out Vector3I forward);

        // Doubled and centred, so the half-cell offset never rounds.
        int cx = 2 * i - (sub - 1);
        int cy = 2 * j - (sub - 1);
        int cz = 2 * k - (sub - 1);

        int dx = right.X * cx + up.X * cy + forward.X * cz;
        int dy = right.Y * cx + up.Y * cy + forward.Y * cz;
        int dz = right.Z * cx + up.Z * cy + forward.Z * cz;

        wi = (dx + sub - 1) / 2;
        wj = (dy + sub - 1) / 2;
        wk = (dz + sub - 1) / 2;
    }

    /// <summary>The same rotation for a continuous point, in sub-cell units.</summary>
    public static Vector3 ToWorld(int orientation, int sub, Vector3 p)
    {
        if (orientation == PosY)
            return p;

        Basis(orientation, out Vector3I right, out Vector3I up, out Vector3I forward);

        float half = sub * 0.5f;
        Vector3 c = p - new Vector3(half, half, half);

        Vector3 r = new Vector3(right.X, right.Y, right.Z) * c.X
                  + new Vector3(up.X, up.Y, up.Z) * c.Y
                  + new Vector3(forward.X, forward.Y, forward.Z) * c.Z;

        return r + new Vector3(half, half, half);
    }

    /// <summary>Rotates a direction (a normal) out of a rule's frame.</summary>
    public static Vector3 DirectionToWorld(int orientation, Vector3 d)
    {
        if (orientation == PosY)
            return d;

        Basis(orientation, out Vector3I right, out Vector3I up, out Vector3I forward);

        return new Vector3(right.X, right.Y, right.Z) * d.X
             + new Vector3(up.X, up.Y, up.Z) * d.Y
             + new Vector3(forward.X, forward.Y, forward.Z) * d.Z;
    }

    /// <summary>The <see cref="NodeFace"/> bit for a direction, or PosY's bit
    /// for one that names no face.</summary>
    private static int IndexOf(Vector3I direction)
    {
        for (int face = 0; face < NodeFace.Count; face++)
        {
            if (NodeFace.Offsets[face] == direction)
                return face;
        }

        return NodeFace.PosY;
    }
}
