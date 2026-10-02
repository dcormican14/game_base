using Godot;

namespace GameBase.Nodes;

/// <summary>What a shaping stroke does to the ground inside its brush.</summary>
public enum SculptOperation
{
    /// <summary>Lifts the ground.</summary>
    Raise,

    /// <summary>Sinks the ground. Raw nodes are never touched, so it stops at rock.</summary>
    Lower,

    /// <summary>Moves the ground toward one height: cuts what is above, fills what is below.</summary>
    Level,

    /// <summary>Moves the ground toward the average around it, softening bumps and edges.</summary>
    Smooth,
}

/// <summary>
/// Where a brush works: at full strength inside <see cref="Inner"/>, fading
/// smoothly to nothing at <see cref="Outer"/>. A small inner circle
/// concentrates a change into a point; a large one spreads it evenly and only
/// fades it at the edge.
/// </summary>
public readonly struct SculptBrush
{
    public SculptBrush(float inner, float outer)
    {
        Outer = Mathf.Max(outer, 0.01f);
        Inner = Mathf.Clamp(inner, 0f, Outer);
    }

    public float Inner { get; }

    public float Outer { get; }

    /// <summary>How strongly the brush works at a distance from its centre, 0..1.</summary>
    public float Weight(float distance)
    {
        if (distance >= Outer)
            return 0f;

        if (distance <= Inner || Outer - Inner < 1e-4f)
            return 1f;

        float t = (Outer - distance) / (Outer - Inner);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>How much a shaping step changed, in cells, each way.</summary>
public readonly record struct SculptResult(float Added, float Removed)
{
    public bool Changed => Added > 0f || Removed > 0f;
}

/// <summary>
/// Shapes particle nodes gradually, the way terrain tools do: every step moves
/// the ground inside a brush a little, and holding the button keeps it moving.
///
/// ALONG THE SURFACE. The brush is a <see cref="SurfaceDisc"/> lying on the
/// ground, whichever way the ground faces, and every operation works on the
/// fill field itself rather than on heights: a fill is a distance to the
/// surface, so adding to it pushes the surface out along its own facing, and
/// taking from it pulls the surface in. Raise builds a wall out sideways and a
/// ceiling downward; over an edge, each side moves along its own facing.
///
/// Each cell near the disc is weighed by the brush at the nearest point of
/// the disc -- how far that is from the centre along the surface -- so the
/// change covers exactly the circle that is drawn, and nothing past it moves.
/// A raise lifts everything under its brush together and stops as a whole
/// when a slope under it reaches 45 degrees (see <see cref="RaiseFraction"/>),
/// so held, it makes a flat-topped mound and stops.
///
/// A fill holding a real distance moves by exactly the change. One clamped at
/// the edge of its range only says "at least this far", so it is placed by its
/// neighbours instead -- a distance can change by at most a node from one
/// lattice point to the next -- which keeps a far-off cell from being pulled
/// into a surface that has not reached it. Raw nodes are never written:
/// particles pile against rock, and digging stops at it.
/// </summary>
public static class ParticleSculpt
{
    /// <summary>
    /// Moves the ground under a disc (world-local, like everything here).
    /// <paramref name="field"/> is the patch the disc was walked over: its
    /// copy of the fills is the ground as it was before this step, which is
    /// what every cell's change is worked out from.
    /// </summary>
    /// <param name="amount">
    /// Raise, Lower, Level: world units the surface moves at full strength in
    /// this step. Smooth: the fraction of the way toward the average, 0..1.
    /// </param>
    /// <param name="planePoint">Level only: a point on the plane to level toward.</param>
    /// <param name="planeNormal">
    /// Level only: the way out of the ground from that plane. Ground further
    /// than <see cref="LevelReach"/> from the plane is left alone.
    /// </param>
    /// <param name="up">
    /// Which way is up where the brush is -- away from gravity. On the ground,
    /// the slopes a raise may make are measured against it (see
    /// <see cref="OnGround"/> and <see cref="RaiseFraction"/>).
    /// </param>
    public static SculptResult Apply(ParticleField field, ParticleNode type, SurfaceDisc disc, SculptBrush brush,
        SculptOperation operation, float amount, Vector3 planePoint = default, Vector3 planeNormal = default,
        Vector3 up = default)
    {
        NodeWorld world = field.World;
        VoronoiGrid grid = world.Grid;
        float node = grid.NodeSize;
        float range = NodeFill.Range;

        // Cells further from the surface than this cannot hold a distance a
        // step can change.
        float reach = (range + 0.5f) * node;

        var samples = new System.Collections.Generic.List<SurfaceDisc.Sample>(disc.Samples);
        Vector3 low = samples[0].Position, high = low;

        foreach (SurfaceDisc.Sample sample in samples)
        {
            low = low.Min(sample.Position);
            high = high.Max(sample.Position);
        }

        Vector3I first = grid.NearestLattice(low - Vector3.One * reach) - Vector3I.One;
        Vector3I last = grid.NearestLattice(high + Vector3.One * reach) + Vector3I.One;
        Vector3I size = last - first + Vector3I.One;

        // The nearest point of the disc to every cell, and how far along the
        // surface it lies: each point of the disc marks the cells around it.
        var nearest = new float[size.X * size.Y * size.Z];
        var arcs = new float[nearest.Length];
        var owners = new int[nearest.Length];
        System.Array.Fill(nearest, float.MaxValue);
        int around = Mathf.CeilToInt(reach / node);

        int Slot(Vector3I cell)
        {
            Vector3I local = cell - first;
            return (local.X * size.Y + local.Y) * size.Z + local.Z;
        }

        for (int index = 0; index < samples.Count; index++)
        {
            SurfaceDisc.Sample sample = samples[index];
            Vector3I middle = grid.NearestLattice(sample.Position);

            for (int dx = -around; dx <= around; dx++)
            for (int dy = -around; dy <= around; dy++)
            for (int dz = -around; dz <= around; dz++)
            {
                Vector3I cell = middle + new Vector3I(dx, dy, dz);
                Vector3I local = cell - first;
                if (local.X < 0 || local.Y < 0 || local.Z < 0 || local.X >= size.X || local.Y >= size.Y || local.Z >= size.Z)
                    continue;

                int slot = Slot(cell);
                float d = grid.LatticePoint(cell).DistanceSquaredTo(sample.Position);

                if (d < nearest[slot])
                {
                    nearest[slot] = d;
                    arcs[slot] = sample.Arc;
                    owners[slot] = index;
                }
            }
        }

        Vector3 plane = planeNormal.LengthSquared() > 1e-8f ? planeNormal.Normalized() : disc.Centre.Normal;
        bool onGround = OnGround(up, disc.Centre.Normal);
        up = onGround ? up.Normalized() : disc.Centre.Normal.Normalized();
        float step = amount / node;

        // The height of the ground where the brush is aimed, in nodes up, and
        // the lattice step most nearly down.
        float aimedAt = disc.Centre.Position.Dot(up) / node;
        Vector3I down = Downward(up, out float perStep);

        var edits = new System.Collections.Generic.List<(Vector3I Cell, float Was, float Now, float Weight)>();

        // A raise's cells are gathered first: how far each one may go depends
        // on how far all the others go (see RaiseFraction).
        var raises = new System.Collections.Generic.List<Rise>();

        for (int x = first.X; x <= last.X; x++)
        for (int y = first.Y; y <= last.Y; y++)
        for (int z = first.Z; z <= last.Z; z++)
        {
            var cell = new Vector3I(x, y, z);
            Vector3 point = grid.LatticePoint(cell);
            int slot = Slot(cell);

            if (nearest[slot] > reach * reach)
                continue;

            float weight = brush.Weight(arcs[slot]);
            if (weight <= 0f || !world.IsCellLoaded(cell) || world.TypeAt(cell) is RawNode)
                continue;

            float was = field.LevelOf(cell);
            bool clampedOut = was <= -range + 0.02f;
            bool clampedIn = was >= range - 0.02f;

            float now;

            switch (operation)
            {
                case SculptOperation.Raise:
                {
                    if (clampedIn)
                        continue;

                    float from = clampedOut ? LowestPossible(field, cell) : was;

                    // Where it stands, for slopes: a clamped cell's distance
                    // read the same way as its neighbours' (see OutsideLevel).
                    float standing = clampedOut ? OutsideLevel(field, cell, down, perStep) : was;

                    // Ground already standing above where the raise was aimed
                    // is left as it is (see RaiseAbove).
                    if (onGround && point.Dot(up) / node + standing > aimedAt + RaiseAbove)
                        continue;

                    raises.Add(new Rise(cell, was, from, standing, step * weight, weight));
                    continue;
                }

                case SculptOperation.Lower:
                    if (clampedOut)
                        continue;
                    now = (clampedIn ? HighestPossible(field, cell) : was) - step * weight;
                    break;

                case SculptOperation.Level:
                {
                    // Ground too far above or below the plane is left as it
                    // is, judged where the disc lies on it -- the ground's own
                    // surface -- so everything under one spot goes together.
                    float off = (samples[owners[slot]].Position - planePoint).Dot(plane) / node;
                    if (Mathf.Abs(off) > LevelReach)
                        continue;

                    // The plane's own distance field: positive below it.
                    float target = Mathf.Clamp((planePoint - point).Dot(plane) / node, -range, range);

                    if ((clampedOut && target <= was) || (clampedIn && target >= was))
                        continue;

                    float from = clampedOut ? LowestPossible(field, cell)
                        : clampedIn ? HighestPossible(field, cell)
                        : was;

                    now = from + Mathf.Clamp(target - from, -step, step) * weight;
                    break;
                }

                default:
                    // Only the surface itself is smoothed; a clamped cell is
                    // well away from it.
                    if (clampedOut || clampedIn)
                        continue;

                    now = was + (Average(field, cell) - was) * Mathf.Clamp(amount, 0f, 1f) * weight;
                    break;
            }

            edits.Add((cell, was, now, weight));
        }

        if (raises.Count > 0)
        {
            float fraction = onGround ? RaiseFraction(field, raises, up) : 1f;

            foreach (Rise rise in raises)
            {
                float now = rise.From + rise.By * fraction;
                if (now > rise.From)
                    edits.Add((rise.Cell, rise.Was, now, rise.Weight));
            }
        }

        // Smoothing moves ground around; it does not make or destroy it.
        // Whatever the step gained or lost overall is handed back, in
        // proportion to the brush's strength -- otherwise the untouched
        // ground past the edge pulls every average down and drains a bump
        // away instead of spreading it.
        if (operation == SculptOperation.Smooth)
        {
            float gained = 0f, weights = 0f;
            foreach (var edit in edits)
            {
                gained += edit.Now - edit.Was;
                weights += edit.Weight;
            }

            if (weights > 0f)
            {
                float back = gained / weights;
                for (int i = 0; i < edits.Count; i++)
                    edits[i] = edits[i] with { Now = edits[i].Now - back * edits[i].Weight };
            }
        }

        float added = 0f, removed = 0f;
        // One threshold for the whole step, not one per cell: cells standing
        // at the same height then round the same way on the same frame, so a
        // flat top crosses a lattice layer all at once -- rounded apart, the
        // first few to cross made a small jump -- and a slope the step brings
        // to its limit gets there together.
        float threshold = Dither(Time.GetTicksUsec());

        foreach ((Vector3I cell, float was, float now, _) in edits)
        {
            float change = now - was;
            if (Mathf.Abs(change) < 1e-5f)
                continue;

            byte fill = NodeFill.FromLevelDithered(now, threshold);
            byte oldFill = world.FillAt(cell);

            // A rising cell only ever gains, a sinking one only ever loses.
            if (change > 0f ? fill <= oldFill : fill >= oldFill)
                continue;

            if (!world.SetParticleFill(cell, type, fill))
                continue;

            float delta = (NodeFill.ToLevel(fill) - Mathf.Clamp(was, -range, range)) / (2f * range);

            if (delta > 0f)
                added += delta;
            else
                removed -= delta;
        }

        return new SculptResult(added, removed);
    }

    /// <summary>
    /// The steepest a raise may make the ground, as rise over run against the
    /// level: one node up for every node across, 45 degrees.
    /// </summary>
    public const float Repose = 1f;


    /// <summary>
    /// How far above or below its plane, in nodes, Level still works the
    /// ground. Past that it leaves it alone: dragged into a tall mound or
    /// across a deep pit, it stops at the edge rather than cutting the mound
    /// down or filling the pit in. A little over a node: one raise makes a
    /// node, and the bumps and dips a raise or a lower leaves are what Level
    /// is for.
    /// </summary>
    public const float LevelReach = 1.25f;

    /// <summary>
    /// How far above the ground where it was aimed a raise still lifts the
    /// ground, in nodes. Ground standing higher -- the flank of a mound the
    /// brush overlaps -- is left as it is, and the raise fills in below it
    /// instead: that is how a mound's base is widened. Held there, the new
    /// ground rises until it meets the old. Without this, the old mound's
    /// flank, already at 45 degrees and inside the brush's edge, would hold
    /// the whole raise at nothing.
    /// </summary>
    private const float RaiseAbove = 0.25f;

    /// <summary>
    /// One cell a raise would lift: from where (its real distance, for a
    /// clamped one), where it stands for measuring slopes, and by how much at
    /// most.
    /// </summary>
    private readonly record struct Rise(Vector3I Cell, float Was, float From, float Standing, float By, float Weight);

    /// <summary>
    /// How much of its step a raise may take, 0..1, the same for every cell
    /// under the brush.
    ///
    /// WHY A FLAT TOP THAT STOPS AS A WHOLE. The ground is drawn with one
    /// vertex in each lattice cube (2 units), at the average of where the
    /// surface crosses the cube's edges. A pointed top has no vertex of its
    /// own: the cubes around its tip average it with the lower slopes, so the
    /// drawn top sits well below the real one -- up to a node below -- until
    /// the tip passes the next lattice point, and a vertex appears right at it
    /// and the drawn top jumps up. Any raise that lifts its middle faster than
    /// its edge makes a pointed top, and capping each cell at 45 degrees on its
    /// own makes a cone, pointed at any size. A flat top crosses each lattice
    /// layer all at once, as flat ground does, and flat ground is drawn exactly.
    ///
    /// So a raise lifts every cell by the same share of its brush strength,
    /// and the share is cut back -- for all of them together -- so that no
    /// slope the raise makes steeper passes <see cref="Repose"/>. The top stays
    /// flat and the whole mound stops, about a node high above the ground
    /// around it; to build higher, widen its base. Only a pair that the step
    /// makes STEEPER counts -- the higher cell rising faster than the lower --
    /// so a raise beside an existing slope, filling in below it, is not held
    /// back by that slope.
    ///
    /// Slopes are measured against the level, square to gravity: neighbours
    /// are compared side by side across it (a step straight up is the
    /// ground's own depth, not a slope), and against where flat ground would
    /// put them. Measured against the brush's own surface instead, a brush on
    /// a 45 degree flank would count the flank as flat and stack another 45 on
    /// top. Only on the ground (see <see cref="OnGround"/>).
    /// </summary>
    private static float RaiseFraction(ParticleField field, System.Collections.Generic.List<Rise> raises, Vector3 up)
    {
        var index = new System.Collections.Generic.Dictionary<Vector3I, int>(raises.Count);
        for (int i = 0; i < raises.Count; i++)
            index[raises[i].Cell] = i;

        Vector3I down = Downward(up, out float perStep);
        float fraction = 1f;

        foreach (Rise rise in raises)
        {
            // Only the cells the surface passes near decide where it is drawn;
            // one well out in the open says nothing about its slope.
            if (rise.Standing < -1f)
                continue;

            foreach (Vector3I offset in Steps)
            {
                float lift = ((Vector3)offset).Dot(up);
                float across = Mathf.Sqrt(Mathf.Max(0f, 1f - lift * lift));

                if (across < 0.5f)
                    continue;

                Vector3I beside = rise.Cell + offset;
                bool rising = index.TryGetValue(beside, out int j);
                float besideStands = rising ? raises[j].Standing : OutsideLevel(field, beside, down, perStep);
                float besideBy = rising ? raises[j].By : 0f;

                // Only a pair this step makes steeper can stop it.
                float faster = rise.By - besideBy;
                if (faster <= 1e-7f)
                    continue;

                // A neighbour clamped deep in the ground only says "at least
                // this deep": its surface is higher still, so a raise beside
                // it cannot make a slope toward it. Taken at its word, it
                // reads as a cliff wherever the lattice leans against gravity
                // -- away from the planet's axes a lattice step "down" is
                // partly sideways -- and stopped every raise there dead.
                if (!rising && field.LevelOf(beside) >= NodeFill.Range - 0.02f)
                    continue;

                // On a flat surface the neighbour's fill is this cell's less
                // the lift; a slope of Repose lets this cell stand that much
                // more. How much of that is left is how far the step may go.
                float room = besideStands + lift + Repose * across - rise.Standing;
                fraction = Mathf.Min(fraction, Mathf.Max(0f, room) / faster);

                if (fraction <= 0f)
                    return 0f;
            }
        }

        return fraction;
    }

    /// <summary>
    /// Is a brush on the GROUND -- facing within 60 degrees of up -- where
    /// sand piles, and a raise makes a flat top and stops at 45 degrees? On a
    /// WALL or a CEILING, a raise just pushes the face out, flat, with no
    /// stop: a dug wall's edges are already far steeper than 45 degrees to it,
    /// and the whole raise would be held at nothing. Without a gravity, the
    /// brush's own surface is taken as level, and it is always on the ground.
    /// </summary>
    public static bool OnGround(Vector3 gravityUp, Vector3 surface) =>
        gravityUp.LengthSquared() < 1e-8f || surface.Normalized().Dot(gravityUp.Normalized()) >= WallFacing;

    /// <summary>
    /// How far from up a brush must face to be on a wall rather than ground:
    /// 60 degrees (as the cosine). Well past 45, because a mound's flank sits
    /// at 45 and a little over in places, and a brush there must still count
    /// as on the ground.
    /// </summary>
    private const float WallFacing = 0.5f;

    /// <summary>How far down the brush a cell can see to find ground: past this, the open counts as this far out.</summary>
    private const int LookDown = 6;

    /// <summary>
    /// A cell's distance to the surface, even out in the open. A fill only
    /// says so much -- anything far enough out reads the same -- and taken at
    /// its word, a column of open cells above ground would each seem only just
    /// out of it, and a pillar could climb one layer on top of the next without
    /// its sides ever looking steep. So from a cell out in the open, this walks
    /// down toward the ground until it finds a fill that holds a real distance,
    /// and counts the way back.
    /// </summary>
    private static float OutsideLevel(ParticleField field, Vector3I cell, Vector3I down, float perStep)
    {
        float level = field.LevelOf(cell);
        float edge = -NodeFill.Range + 0.02f;

        if (level > edge || perStep <= 0f)
            return level;

        for (int k = 1; k <= LookDown; k++)
        {
            float below = field.LevelOf(cell + down * k);
            if (below > edge)
                return below - k * perStep;
        }

        return -NodeFill.Range - LookDown * perStep;
    }

    /// <summary>The lattice step most nearly down the brush, and how much deeper into the ground each one goes, in nodes.</summary>
    private static Vector3I Downward(Vector3 up, out float perStep)
    {
        Vector3 a = up.Abs();
        Vector3I step = a.X >= a.Y && a.X >= a.Z ? new Vector3I(up.X > 0 ? -1 : 1, 0, 0)
            : a.Y >= a.Z ? new Vector3I(0, up.Y > 0 ? -1 : 1, 0)
            : new Vector3I(0, 0, up.Z > 0 ? -1 : 1);

        perStep = -((Vector3)step).Dot(up);
        return step;
    }

    /// <summary>
    /// The least a cell clamped outside the ground can really be: a node less
    /// than the most any of its neighbours says. Rock is left out -- its fill
    /// says "solid", not how far away its surface is.
    /// </summary>
    private static float LowestPossible(ParticleField field, Vector3I cell)
    {
        float best = -NodeFill.Range;

        foreach (Vector3I step in Steps)
        {
            Vector3I next = cell + step;
            if (field.World.TypeAt(next) is not RawNode)
                best = Mathf.Max(best, field.LevelOf(next));
        }

        return best - 1f;
    }

    /// <summary>The most a cell clamped inside the ground can really be: a node more than the least of its neighbours.</summary>
    private static float HighestPossible(ParticleField field, Vector3I cell)
    {
        float best = NodeFill.Range;

        foreach (Vector3I step in Steps)
            best = Mathf.Min(best, field.LevelOf(cell + step));

        return best + 1f;
    }

    /// <summary>The average fill of a cell's six neighbours.</summary>
    private static float Average(ParticleField field, Vector3I cell)
    {
        float total = 0f;

        foreach (Vector3I step in Steps)
            total += field.LevelOf(cell + step);

        return total / Steps.Length;
    }

    private static readonly Vector3I[] Steps =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    };

    /// <summary>A random number in 0..1, different every step.</summary>
    private static float Dither(ulong stamp)
    {
        uint h = (uint)stamp * 2654435761u ^ (uint)(stamp >> 32) * 73856093u;
        h ^= h >> 15;
        h *= 2246822519u;
        h ^= h >> 13;
        return (h & 0xFFFFFFu) / 16777216f;
    }
}
