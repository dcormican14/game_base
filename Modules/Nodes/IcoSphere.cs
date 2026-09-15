using Godot;

namespace GameBase.Nodes;

/// <summary>
/// The icosahedral geometry the even-node planet is built on: twenty triangular
/// faces, and the lattice of sites laid over them.
///
/// The counterpart to <see cref="QuadSphere"/>, and it exists because that one
/// cannot produce even nodes. A cube has eight corners where three quads meet,
/// and a quad forced to share a vertex with only two others opens to 120
/// degrees. That defect does not stay at the corner: measured across one face
/// of the cubed sphere, cells run from 90 degrees at the centre to 120 at the
/// corners, averaging 8.3 degrees of skew, so most of the planet reads as
/// rhombuses rather than squares.
///
/// An icosahedron spreads the same unavoidable defect over TWELVE points
/// instead of eight, and -- the part that matters -- its faces are triangles, so
/// the defect does not bleed. Every site away from the twelve has exactly six
/// neighbours at near-equal spacing, and its Voronoi cell is a near-regular
/// hexagon.
///
/// WHY TWELVE IS NOT NEGOTIABLE
///
/// Euler's formula fixes V - E + F = 2 for any tiling of a sphere. A tiling of
/// hexagons alone gives 0, so no subdivision, projection or relaxation can
/// remove the twelve pentagons. What can be chosen is whether the defect is
/// localised, and here it is: twelve cells out of tens of thousands.
/// </summary>
/// <remarks>
/// Sites are the icosahedron's VERTICES under repeated 1-to-4 subdivision, not
/// its face centres. That choice is what makes shells nest exactly -- see
/// <see cref="IcoSphereTables"/>.
/// </remarks>
public static class IcoSphere
{
    /// <summary>Triangular faces of the base icosahedron.</summary>
    public const int FaceCount = 20;

    /// <summary>
    /// Vertices of the base icosahedron, on the unit sphere.
    ///
    /// The twelve points where only five triangles meet -- the pentagons of the
    /// dual, and the only sites in the whole world that are not hexagonal.
    /// Ordered so index doubles as identity: site 0..11 are these, and every
    /// subdivision only ever APPENDS.
    /// </summary>
    public static readonly Vector3[] BaseVertices = BuildBaseVertices();

    /// <summary>
    /// The twenty base faces, as triples of vertex indices.
    ///
    /// Wound consistently counter-clockwise seen from outside, so a face's
    /// normal can be taken from its own corners rather than from a table.
    /// </summary>
    public static readonly int[][] BaseFaces =
    {
        new[] { 0, 11, 5 }, new[] { 0, 5, 1 }, new[] { 0, 1, 7 },
        new[] { 0, 7, 10 }, new[] { 0, 10, 11 }, new[] { 1, 5, 9 },
        new[] { 5, 11, 4 }, new[] { 11, 10, 2 }, new[] { 10, 7, 6 },
        new[] { 7, 1, 8 }, new[] { 3, 9, 4 }, new[] { 3, 4, 2 },
        new[] { 3, 2, 6 }, new[] { 3, 6, 8 }, new[] { 3, 8, 9 },
        new[] { 4, 9, 5 }, new[] { 2, 4, 11 }, new[] { 6, 2, 10 },
        new[] { 8, 6, 7 }, new[] { 9, 8, 1 },
    };

    private static Vector3[] BuildBaseVertices()
    {
        // The golden ratio: an icosahedron's twelve vertices are the corners of
        // three mutually perpendicular golden rectangles.
        float t = (1f + Mathf.Sqrt(5f)) * 0.5f;

        var raw = new Vector3[]
        {
            new(-1f, t, 0f), new(1f, t, 0f), new(-1f, -t, 0f), new(1f, -t, 0f),
            new(0f, -1f, t), new(0f, 1f, t), new(0f, -1f, -t), new(0f, 1f, -t),
            new(t, 0f, -1f), new(t, 0f, 1f), new(-t, 0f, -1f), new(-t, 0f, 1f),
        };

        var result = new Vector3[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            result[i] = raw[i].Normalized();

        return result;
    }

    /// <summary>
    /// How many sites a subdivision level carries: 10 * 4^level + 2.
    ///
    /// The +2 is the reason the twelve pentagons can never be subdivided away:
    /// it is Euler's characteristic showing up as a constant that no amount of
    /// refinement divides.
    /// </summary>
    public static int SiteCount(int level)
    {
        long count = 10L * (1L << (2 * Mathf.Clamp(level, 0, 14))) + 2L;
        return (int)Mathf.Min(count, int.MaxValue);
    }

    /// <summary>
    /// The subdivision level whose sites sit closest to a target spacing at a
    /// given radius.
    ///
    /// Spacing is derived from area per site rather than from an edge length,
    /// because a site's cell is a hexagon whose edge is not its spacing.
    /// </summary>
    public static int LevelForSpacing(float radius, float spacing, int maxLevel = 10)
    {
        if (spacing <= 0.0001f)
            return 0;

        float area = 4f * Mathf.Pi * radius * radius;
        int best = 0;
        float bestError = float.MaxValue;

        for (int level = 0; level <= maxLevel; level++)
        {
            float width = Mathf.Sqrt(area / SiteCount(level));
            float error = Mathf.Abs(width - spacing);

            if (error >= bestError)
                continue;

            bestError = error;
            best = level;
        }

        return best;
    }

    /// <summary>
    /// Which base face a direction falls on.
    ///
    /// Brute force over twenty faces, which is a handful of dot products and no
    /// branching worth avoiding -- the alternative, a lookup by octant, has to
    /// fall back to this near face boundaries anyway.
    /// </summary>
    public static int FaceOf(Vector3 direction)
    {
        int best = 0;
        float bestDot = float.MinValue;

        for (int face = 0; face < FaceCount; face++)
        {
            // The face's centroid direction. A direction belongs to the face
            // whose centroid it is nearest, which for a convex polyhedron of
            // equal faces is the same as being inside its solid angle.
            Vector3 centre = FaceCentre(face);
            float d = direction.Dot(centre);

            if (d <= bestDot)
                continue;

            bestDot = d;
            best = face;
        }

        return best;
    }

    /// <summary>
    /// Do two base faces share an edge or a vertex?
    ///
    /// Used to bound the search when a direction sits near a face boundary and
    /// the owning face is ambiguous. Sharing a VERTEX counts, not just an edge:
    /// around the twelve icosahedral vertices five faces meet, and a site there
    /// is reachable from any of them.
    /// </summary>
    public static bool FacesAdjacent(int a, int b)
    {
        if (a == b)
            return true;

        int[] first = BaseFaces[a];
        int[] second = BaseFaces[b];

        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (first[i] == second[j])
                    return true;
            }
        }

        return false;
    }

    /// <summary>The unit direction at the centre of a base face.</summary>
    public static Vector3 FaceCentre(int face)
    {
        int[] tri = BaseFaces[face];
        return (BaseVertices[tri[0]] + BaseVertices[tri[1]] + BaseVertices[tri[2]])
            .Normalized();
    }

    /// <summary>
    /// Barycentric coordinates of a direction on the FLAT triangle of a face.
    ///
    /// A FIRST GUESS, NOT AN INVERSE. <see cref="PointOn"/> places sites along
    /// great circles, and this undoes the flat placement instead, so scaling
    /// these by the subdivision lands near the right site rather than on it --
    /// close enough to start from, never close enough to trust.
    ///
    /// There was an analytic spherical inverse here for a while and it was
    /// quietly wrong: the arc from a face corner to the opposite edge is a
    /// different length in every direction, so recovering the lattice index
    /// from an angle ratio is not linear and came out a full step low in both
    /// axes. Callers now take this guess and walk to the true nearest site.
    /// </summary>
    public static void FlatBarycentric(int face, Vector3 direction,
        out float a, out float b, out float c)
    {
        int[] tri = BaseFaces[face];
        Vector3 v0 = BaseVertices[tri[0]];
        Vector3 v1 = BaseVertices[tri[1]];
        Vector3 v2 = BaseVertices[tri[2]];

        Vector3 normal = (v1 - v0).Cross(v2 - v0);
        float denominator = direction.Dot(normal);

        if (Mathf.Abs(denominator) < 0.000001f)
        {
            a = 1f; b = 0f; c = 0f;
            return;
        }

        // Where the ray along `direction` meets the triangle's plane.
        Vector3 at = direction * (v0.Dot(normal) / denominator);

        // Areas of the three sub-triangles, as a fraction of the whole.
        float total = normal.Dot(normal);
        a = (v1 - at).Cross(v2 - at).Dot(normal) / total;
        b = (v2 - at).Cross(v0 - at).Dot(normal) / total;
        c = 1f - a - b;
    }

    /// <summary>
    /// The direction of a lattice point on a face, given in lattice steps along
    /// two of the triangle's edges.
    ///
    /// `divisions` is 2^level: the number of lattice steps along one edge.
    ///
    /// PLACED BY ANGLE, NOT BY CHORD. The obvious construction -- interpolate
    /// across the flat triangle, then normalise -- is what repeated midpoint
    /// subdivision produces, and it bunches sites badly: equal steps along a
    /// straight chord are not equal steps along the arc it subtends, so cells
    /// near a face's corners come out markedly smaller than cells at its
    /// centre. Measured over a whole face, that placement spreads spacing by
    /// 1.38 at 8 divisions and gets WORSE with resolution, reaching 1.45 at 32
    /// and 1.47 at the level a real planet uses -- worse than the cubed sphere
    /// this grid exists to replace.
    ///
    /// Interpolating along the great circles instead holds the spread at about
    /// 1.22 and, unlike the flat version, does not degrade as the lattice gets
    /// finer.
    /// </summary>
    public static Vector3 PointOn(int face, int i, int j, int divisions)
    {
        // SERVED FROM A TABLE WHEREVER ONE EXISTS.
        //
        // This is the single hottest call in the whole grid -- the mesher
        // reaches it through every ring, every corner and every lookup -- and
        // computing it costs three slerps, which is three acos, six sin and
        // three square roots. Measured against a real streamed world, that put
        // one WallCount at 60 microseconds where the cubed sphere's was 0.03.
        //
        // The positions depend only on (face, i, j, divisions), so they are a
        // pure function of a small dense index: exactly the thing to compute
        // once and look up. The table is built per subdivision level the first
        // time that level is asked for, and after that this is an array index.
        DirectionTable table = TableFor(divisions);

        if (table != null)
        {
            int index = table.IndexOf(face, i, j);
            if (index >= 0)
                return table.Directions[index];
        }

        return Compute(face, i, j, divisions);
    }

    /// <summary>The uncached construction, for addresses outside a table.</summary>
    private static Vector3 Compute(int face, int i, int j, int divisions)
    {
        int[] tri = BaseFaces[face];
        Vector3 v0 = BaseVertices[tri[0]];
        Vector3 v1 = BaseVertices[tri[1]];
        Vector3 v2 = BaseVertices[tri[2]];

        int span = i + j;
        if (span <= 0)
            return v0;

        int steps = Mathf.Max(1, divisions);

        // How far from v0 toward the opposite edge, then how far along that
        // edge. Two slerps rather than three weights, because a spherical
        // triangle has no linear barycentric form.
        Vector3 toward1 = Slerp(v0, v1, (float)span / steps);
        Vector3 toward2 = Slerp(v0, v2, (float)span / steps);

        return Slerp(toward1, toward2, (float)j / span);
    }

    /// <summary>
    /// Every site direction at one subdivision level, laid out flat.
    ///
    /// One face's lattice is a triangle of (divisions + 1)(divisions + 2)/2
    /// points, and the twenty faces are stored end to end. At the surface level
    /// of a radius-120 planet that is about 170k directions, or 2 MB -- built
    /// once, shared by every thread, and never written again.
    /// </summary>
    private sealed class DirectionTable
    {
        public readonly int Divisions;
        public readonly int PerFace;
        public readonly Vector3[] Directions;

        public DirectionTable(int divisions)
        {
            Divisions = divisions;
            PerFace = (divisions + 1) * (divisions + 2) / 2;
            Directions = new Vector3[PerFace * FaceCount];

            for (int face = 0; face < FaceCount; face++)
            {
                for (int i = 0; i <= divisions; i++)
                {
                    for (int j = 0; i + j <= divisions; j++)
                        Directions[IndexOf(face, i, j)] = Compute(face, i, j, divisions);
                }
            }
        }

        /// <summary>
        /// Where a lattice point sits in the flat array, or -1 if it is outside
        /// the face's triangle.
        ///
        /// Rows shorten as i grows, so the offset of row i is the triangular
        /// number of the rows above it.
        /// </summary>
        public int IndexOf(int face, int i, int j)
        {
            if ((uint)face >= FaceCount || i < 0 || j < 0 || i + j > Divisions)
                return -1;

            int row = i * (Divisions + 1) - i * (i - 1) / 2;
            return face * PerFace + row + j;
        }
    }

    /// <summary>
    /// Tables by subdivision level, built on demand.
    ///
    /// Indexed by level rather than held in a dictionary, since levels are
    /// small dense integers and the lookup is on the hot path. Written once
    /// under a lock and read without one: a reference assignment is atomic, so
    /// a reader either sees a fully built table or none at all.
    /// </summary>
    private static readonly DirectionTable[] Tables = new DirectionTable[21];

    private static readonly object TableLock = new();

    /// <summary>
    /// The table for a subdivision, building it if this is the first ask.
    ///
    /// Returns null for a subdivision too fine to be worth the memory, where
    /// the caller falls back to computing the position.
    /// </summary>
    private static DirectionTable TableFor(int divisions)
    {
        if (divisions < 1 || divisions > (1 << 12))
            return null;

        // Only exact powers of two are levels this grid uses; anything else is
        // a caller asking about a subdivision no shell is drawn at.
        if ((divisions & (divisions - 1)) != 0)
            return null;

        int level = System.Numerics.BitOperations.Log2((uint)divisions);

        DirectionTable existing = Tables[level];
        if (existing != null)
            return existing;

        lock (TableLock)
        {
            // Re-checked inside the lock: two threads can reach here together
            // and only one should pay for the build.
            existing = Tables[level];
            if (existing != null)
                return existing;

            var built = new DirectionTable(divisions);
            Tables[level] = built;
            return built;
        }
    }

    /// <summary>
    /// Interpolation along the great circle between two unit directions.
    ///
    /// Falls back to the straight blend when the two are nearly parallel, where
    /// the angle is too small to divide by and the difference is below what a
    /// float can carry anyway.
    /// </summary>
    private static Vector3 Slerp(Vector3 from, Vector3 to, float amount)
    {
        float cos = Mathf.Clamp(from.Dot(to), -1f, 1f);
        float angle = Mathf.Acos(cos);

        if (angle < 0.000001f)
            return from;

        float sin = Mathf.Sin(angle);

        return (from * Mathf.Sin((1f - amount) * angle) / sin
            + to * Mathf.Sin(amount * angle) / sin).Normalized();
    }
}
