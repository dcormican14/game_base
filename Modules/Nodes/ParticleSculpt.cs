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
/// Building straight up raises a column as wide as the brush, never a
/// mountain whose foot spreads as it grows.
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
    /// <param name="planeNormal">Level only: the way out of the ground from that plane.</param>
    public static SculptResult Apply(ParticleField field, ParticleNode type, SurfaceDisc disc, SculptBrush brush,
        SculptOperation operation, float amount, Vector3 planePoint = default, Vector3 planeNormal = default)
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
        System.Array.Fill(nearest, float.MaxValue);
        int around = Mathf.CeilToInt(reach / node);

        int Slot(Vector3I cell)
        {
            Vector3I local = cell - first;
            return (local.X * size.Y + local.Y) * size.Z + local.Z;
        }

        foreach (SurfaceDisc.Sample sample in samples)
        {
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
                }
            }
        }

        Vector3 plane = planeNormal.LengthSquared() > 1e-8f ? planeNormal.Normalized() : disc.Centre.Normal;
        float step = amount / node;

        var edits = new System.Collections.Generic.List<(Vector3I Cell, float Was, float Now, float Weight)>();

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
                    if (clampedIn)
                        continue;
                    now = (clampedOut ? LowestPossible(field, cell) : was) + step * weight;
                    break;

                case SculptOperation.Lower:
                    if (clampedOut)
                        continue;
                    now = (clampedIn ? HighestPossible(field, cell) : was) - step * weight;
                    break;

                case SculptOperation.Level:
                {
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
        ulong stamp = Time.GetTicksUsec();

        foreach ((Vector3I cell, float was, float now, _) in edits)
        {
            float change = now - was;
            if (Mathf.Abs(change) < 1e-5f)
                continue;

            byte fill = NodeFill.FromLevelDithered(now, Dither(cell, stamp));
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

    /// <summary>A random number in 0..1 for a cell, different every step.</summary>
    private static float Dither(Vector3I cell, ulong stamp)
    {
        uint h = (uint)cell.X * 73856093u ^ (uint)cell.Y * 19349663u ^ (uint)cell.Z * 83492791u ^ (uint)stamp * 2654435761u;
        h ^= h >> 15;
        h *= 2246822519u;
        h ^= h >> 13;
        return (h & 0xFFFFFFu) / 16777216f;
    }
}
