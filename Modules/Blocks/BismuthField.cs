using Godot;

namespace GameBase.Blocks;

/// <summary>
/// The flowing field that decides which block wins each edge and corner.
///
/// A lattice edge is shared by 4 blocks and a lattice corner by 8. This awards
/// each contested region to exactly one of them, as a pure function of the
/// feature's position and the seed. Every block touching the feature runs the
/// same computation on the same position and agrees on the winner, so no block
/// ever inspects a neighbour — which is what makes the shapes interlock and
/// what makes planet-scale generation affordable.
///
/// The winner is not uniformly random. A flowing 3D vector field is sampled at
/// the feature, and whichever contender lies furthest ALONG that flow takes
/// it. Because the flow varies smoothly, neighbouring features tend to favour
/// the same direction, so crystal growth reads as a current running through
/// the rock — long rims and terraces marching one way — rather than as
/// per-block static. `Roughness` mixes in per-feature jitter to break up that
/// coherence when a more granular, chaotic crystal is wanted.
///
/// Everything here is a pure function of position and seed: no state, no
/// allocation, no neighbour queries.
/// </summary>
public sealed class BismuthField
{
    private readonly int _seed;
    private readonly float _frequency;
    private readonly float _roughness;
    private readonly float _growth;

    /// <param name="seed">Deterministic seed — same seed, same planet.</param>
    /// <param name="scale">Cells per lobe of the flow. Larger means long, lazy
    /// currents; smaller means a busier, more granular crystal.</param>
    /// <param name="roughness">0..1. At 0 the flow decides every contest and
    /// growth is strongly directional; at 1 contests are essentially random
    /// and the crystal is chaotic.</param>
    /// <param name="growth">0..1, how many contests are awarded at all. At 0
    /// no edge or corner moves and blocks stay plain cubes; at 1 every one is
    /// claimed by somebody. Middling values leave some rims flat.</param>
    public BismuthField(int seed, float scale = 6f, float roughness = 0.35f, float growth = 0.7f)
    {
        _seed = seed;
        _frequency = 1f / Mathf.Max(scale, 0.5f);
        _roughness = Mathf.Clamp(roughness, 0f, 1f);
        _growth = Mathf.Clamp(growth, 0f, 1f);
    }

    // Winners memoized per lattice feature. Each corner is shared by 8 blocks
    // and each edge by 4, so meshing a region re-asks the same question 4-8
    // times; caching the verdict removes that redundancy outright. Purely an
    // optimisation — the answer is a pure function of position either way, so
    // dropping the cache changes nothing but speed.
    private readonly System.Collections.Generic.Dictionary<(int, int, int, int), Vector3I> _winners = new();

    /// <summary>
    /// The shape mask for a block: which of its 12 edges and 8 corners it won.
    /// This is the O(1) shape lookup — no neighbour queries and no dependence
    /// on what has been placed, so a block dropped into empty space mid-game
    /// gets exactly the shape it would have had if generated with the planet.
    /// </summary>
    public BismuthShape.Mask MaskFor(Vector3I cell)
    {
        int corners = 0;
        for (int c = 0; c < 8; c++)
        {
            bool xHigh = (c & 1) != 0, yHigh = (c & 2) != 0, zHigh = (c & 4) != 0;
            var lattice = new Vector3I(
                cell.X + (xHigh ? 1 : 0),
                cell.Y + (yHigh ? 1 : 0),
                cell.Z + (zHigh ? 1 : 0));

            if (WinsCorner(cell, lattice))
                corners |= 1 << BismuthShape.CornerIndex(xHigh, yHigh, zHigh);
        }

        int edges = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            int uAxis = axis == 0 ? 1 : 0;
            int vAxis = axis == 2 ? 1 : 2;
            for (int e = 0; e < 4; e++)
            {
                bool uHigh = (e & 1) != 0, vHigh = (e & 2) != 0;
                var lattice = cell;
                if (uHigh) lattice = Step(lattice, uAxis);
                if (vHigh) lattice = Step(lattice, vAxis);

                if (WinsEdge(cell, axis, lattice))
                    edges |= 1 << BismuthShape.EdgeIndex(axis, uHigh, vHigh);
            }
        }

        return new BismuthShape.Mask(corners, edges);
    }

    private static Vector3I Step(Vector3I v, int axis) => axis switch
    {
        0 => new Vector3I(v.X + 1, v.Y, v.Z),
        1 => new Vector3I(v.X, v.Y + 1, v.Z),
        _ => new Vector3I(v.X, v.Y, v.Z + 1),
    };

    /// <summary>
    /// Does `cell` win the lattice corner at `lattice`? The 8 contenders are
    /// lattice - {0,1}^3; the winner is whichever lies furthest along the flow
    /// sampled at the corner.
    /// </summary>
    private bool WinsCorner(Vector3I cell, Vector3I lattice) =>
        CornerWinner(lattice) == cell;

    /// <summary>Which block takes the region around this lattice corner.</summary>
    private Vector3I CornerWinner(Vector3I lattice)
    {
        var key = (0, lattice.X, lattice.Y, lattice.Z);
        if (_winners.TryGetValue(key, out Vector3I cached))
            return cached;
        Vector3I result = SolveCorner(lattice);
        _winners[key] = result;
        return result;
    }

    private Vector3I SolveCorner(Vector3I lattice)
    {
        // An uncontested corner still has to belong to somebody, or the
        // region it covers is claimed by nobody and leaves a hole. It falls
        // to the block that nominally contains it — the one whose own cell
        // the quarter-cells sit in — which is the block at lattice - (1,1,1).
        if (!Contested(0u, lattice))
            return new Vector3I(lattice.X - 1, lattice.Y - 1, lattice.Z - 1);

        Vector3 flow = Flow(lattice, 0u);
        int best = -1;
        float bestScore = float.NegativeInfinity;

        for (int s = 0; s < 8; s++)
        {
            var contender = new Vector3I(
                lattice.X - (s & 1),
                lattice.Y - ((s >> 1) & 1),
                lattice.Z - ((s >> 2) & 1));

            // Direction from the corner toward this contender's centre.
            var toCentre = new Vector3(
                contender.X + 0.5f - lattice.X,
                contender.Y + 0.5f - lattice.Y,
                contender.Z + 0.5f - lattice.Z);

            float score = flow.Dot(toCentre.Normalized())
                + (Hash01(contender, lattice, 17u) - 0.5f) * 2f * _roughness;
            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }

        return new Vector3I(
            lattice.X - (best & 1),
            lattice.Y - ((best >> 1) & 1),
            lattice.Z - ((best >> 2) & 1));
    }

    /// <summary>
    /// Does `cell` win the lattice edge running along `axis` at `lattice`?
    /// The 4 contenders vary in the two axes the edge does not run along.
    /// </summary>
    private bool WinsEdge(Vector3I cell, int axis, Vector3I lattice) =>
        EdgeWinner(axis, lattice) == cell;

    /// <summary>Which block takes the region around this lattice edge.</summary>
    private Vector3I EdgeWinner(int axis, Vector3I lattice)
    {
        var key = (axis + 1, lattice.X, lattice.Y, lattice.Z);
        if (_winners.TryGetValue(key, out Vector3I cached))
            return cached;
        Vector3I result = SolveEdge(axis, lattice);
        _winners[key] = result;
        return result;
    }

    private Vector3I SolveEdge(int axis, Vector3I lattice)
    {
        int uAxis = axis == 0 ? 1 : 0;
        int vAxis = axis == 2 ? 1 : 2;

        // As with corners: an unclaimed edge would leave its region ownerless,
        // so it defaults to the block that nominally contains it.
        if (!Contested((uint)(axis + 1), lattice))
            return Back(Back(lattice, uAxis), vAxis);

        Vector3 flow = Flow(lattice, (uint)(axis + 1));

        int best = -1;
        float bestScore = float.NegativeInfinity;
        for (int s = 0; s < 4; s++)
        {
            Vector3I contender = lattice;
            if ((s & 1) != 0) contender = Back(contender, uAxis);
            if ((s & 2) != 0) contender = Back(contender, vAxis);

            // The edge runs along `axis`, so only the cross-section matters:
            // zero that component so growth along the edge never decides it.
            var toCentre = new Vector3(
                contender.X + 0.5f - lattice.X,
                contender.Y + 0.5f - lattice.Y,
                contender.Z + 0.5f - lattice.Z);
            toCentre[axis] = 0f;

            float score = flow.Dot(toCentre.Normalized())
                + (Hash01(contender, lattice, (uint)(31 + axis)) - 0.5f) * 2f * _roughness;
            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }

        Vector3I winnerCell = lattice;
        if ((best & 1) != 0) winnerCell = Back(winnerCell, uAxis);
        if ((best & 2) != 0) winnerCell = Back(winnerCell, vAxis);
        return winnerCell;
    }

    private static Vector3I Back(Vector3I v, int axis) => axis switch
    {
        0 => new Vector3I(v.X - 1, v.Y, v.Z),
        1 => new Vector3I(v.X, v.Y - 1, v.Z),
        _ => new Vector3I(v.X, v.Y, v.Z - 1),
    };

    /// <summary>Whether this feature is awarded at all, or left flat.</summary>
    private bool Contested(uint kind, Vector3I lattice) =>
        Hash01(lattice, lattice, kind * 977u + 5u) < _growth;

    /// <summary>The flow vector at a lattice feature: smooth, so neighbouring
    /// features favour the same growth direction and rims run in currents.</summary>
    private Vector3 Flow(Vector3I lattice, uint kind)
    {
        float x = lattice.X * _frequency, y = lattice.Y * _frequency, z = lattice.Z * _frequency;
        var v = new Vector3(
            Value(x, y, z, kind * 7u + 1u),
            Value(x + 31.4f, y - 17.9f, z + 5.3f, kind * 7u + 2u),
            Value(x - 11.7f, y + 23.1f, z - 3.8f, kind * 7u + 3u));
        return v.LengthSquared() < 1e-8f ? Vector3.Up : v.Normalized();
    }

    /// <summary>Trilinear value noise, smootherstep-faded, in -1..1.</summary>
    private float Value(float x, float y, float z, uint salt)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y), z0 = Mathf.FloorToInt(z);
        float tx = Fade(x - x0), ty = Fade(y - y0), tz = Fade(z - z0);

        float c000 = Corner(x0, y0, z0, salt), c100 = Corner(x0 + 1, y0, z0, salt);
        float c010 = Corner(x0, y0 + 1, z0, salt), c110 = Corner(x0 + 1, y0 + 1, z0, salt);
        float c001 = Corner(x0, y0, z0 + 1, salt), c101 = Corner(x0 + 1, y0, z0 + 1, salt);
        float c011 = Corner(x0, y0 + 1, z0 + 1, salt), c111 = Corner(x0 + 1, y0 + 1, z0 + 1, salt);

        float x00 = Mathf.Lerp(c000, c100, tx), x10 = Mathf.Lerp(c010, c110, tx);
        float x01 = Mathf.Lerp(c001, c101, tx), x11 = Mathf.Lerp(c011, c111, tx);
        return Mathf.Lerp(Mathf.Lerp(x00, x10, ty), Mathf.Lerp(x01, x11, ty), tz);
    }

    /// <summary>Smootherstep — no visible creases where noise cells meet.</summary>
    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    private float Corner(int x, int y, int z, uint salt) =>
        Hash(unchecked((uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791)), salt) * 2f - 1f;

    private float Hash01(Vector3I a, Vector3I b, uint salt) =>
        Hash(unchecked((uint)(a.X * 73856093 ^ a.Y * 19349663 ^ a.Z * 83492791
            ^ b.X * 2654435761 ^ b.Y * 40503 ^ b.Z * 1073676287)), salt);

    /// <summary>Deterministic 0..1 hash.</summary>
    private float Hash(uint value, uint salt)
    {
        unchecked
        {
            uint x = value ^ (uint)_seed * 2654435761u ^ salt * 40503u;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0xFFFFFF) / 16777216f;
        }
    }
}
