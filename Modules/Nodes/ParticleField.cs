using Godot;

namespace GameBase.Nodes;

/// <summary>
/// The ground as one continuous field over a patch of the world: the fills of
/// the lattice points around any point, blended. Positive inside the ground,
/// negative outside, zero on its surface, in nodes. Raw nodes count as solid
/// ground, so the field's surface runs over particles and bare rock alike.
///
/// A patch COPIES the fills it covers when it is made, so every read after
/// that is an array lookup rather than a trip to the store -- walking a brush
/// over the ground reads the field thousands of times a frame -- and so it is
/// a steady picture of the ground as it was, even while the ground is being
/// written. Reads outside the patch fall through to the world.
///
/// Everything here takes and returns the world's LOCAL space.
/// </summary>
public sealed class ParticleField
{
    private readonly NodeWorld _world;
    private readonly float _node;
    private readonly Vector3I _low;
    private readonly Vector3I _size;
    private readonly float[] _levels;

    /// <summary>A patch covering every lattice point within a box (plus one).</summary>
    public ParticleField(NodeWorld world, Vector3 min, Vector3 max)
    {
        _world = world;
        _node = world.Grid.NodeSize;
        _low = world.Grid.NearestLattice(min) - Vector3I.One * 2;
        Vector3I high = world.Grid.NearestLattice(max) + Vector3I.One * 2;
        _size = high - _low + Vector3I.One;
        _levels = new float[_size.X * _size.Y * _size.Z];

        for (int x = 0; x < _size.X; x++)
        for (int y = 0; y < _size.Y; y++)
        for (int z = 0; z < _size.Z; z++)
            _levels[(x * _size.Y + y) * _size.Z + z] = world.ParticleLevel(_low + new Vector3I(x, y, z));
    }

    /// <summary>A patch covering a ball around a point.</summary>
    public static ParticleField Around(NodeWorld world, Vector3 centre, float radius) =>
        new(world, centre - Vector3.One * radius, centre + Vector3.One * radius);

    public NodeWorld World => _world;

    /// <summary>A lattice point's fill, as the patch copied it.</summary>
    public float LevelOf(Vector3I cell)
    {
        Vector3I local = cell - _low;

        if (local.X < 0 || local.Y < 0 || local.Z < 0 || local.X >= _size.X || local.Y >= _size.Y || local.Z >= _size.Z)
            return _world.ParticleLevel(cell);

        return _levels[(local.X * _size.Y + local.Y) * _size.Z + local.Z];
    }

    /// <summary>The field at a point.</summary>
    public float At(Vector3 point)
    {
        Vector3 g = point / _node;
        var b = new Vector3I(Mathf.FloorToInt(g.X), Mathf.FloorToInt(g.Y), Mathf.FloorToInt(g.Z));
        Vector3 f = g - (Vector3)b;

        float x00 = Mathf.Lerp(LevelOf(b), LevelOf(b + new Vector3I(1, 0, 0)), f.X);
        float x10 = Mathf.Lerp(LevelOf(b + new Vector3I(0, 1, 0)), LevelOf(b + new Vector3I(1, 1, 0)), f.X);
        float x01 = Mathf.Lerp(LevelOf(b + new Vector3I(0, 0, 1)), LevelOf(b + new Vector3I(1, 0, 1)), f.X);
        float x11 = Mathf.Lerp(LevelOf(b + new Vector3I(0, 1, 1)), LevelOf(b + new Vector3I(1, 1, 1)), f.X);

        return Mathf.Lerp(Mathf.Lerp(x00, x10, f.Y), Mathf.Lerp(x01, x11, f.Y), f.Z);
    }

    /// <summary>How the field rises at a point, in nodes per world unit: it points into the ground.</summary>
    public Vector3 Gradient(Vector3 point)
    {
        float h = _node * 0.5f;

        return new Vector3(
            At(point + new Vector3(h, 0, 0)) - At(point - new Vector3(h, 0, 0)),
            At(point + new Vector3(0, h, 0)) - At(point - new Vector3(0, h, 0)),
            At(point + new Vector3(0, 0, h)) - At(point - new Vector3(0, 0, h))) / (2f * h);
    }

    /// <summary>The way out of the ground at a point, or <paramref name="fallback"/> where the field is flat.</summary>
    public Vector3 Normal(Vector3 point, Vector3 fallback)
    {
        Vector3 gradient = Gradient(point);
        return gradient.LengthSquared() > 1e-8f ? -gradient.Normalized() : fallback;
    }

    /// <summary>
    /// Moves a point onto the surface, straight across the field toward it.
    /// False when there is no surface to reach from there -- deep inside the
    /// ground or far out in the open, where the field is flat.
    /// </summary>
    public bool Project(ref Vector3 point, int iterations = 3)
    {
        for (int n = 0; n < iterations; n++)
        {
            float level = At(point);
            Vector3 gradient = Gradient(point);
            float slope = gradient.Length();

            if (slope < 1e-4f)
                return false;

            // Level is distance to the surface in nodes; slope turns it to units.
            point -= gradient / slope * (level / slope);

            if (Mathf.Abs(level) < 0.005f)
                break;
        }

        return Mathf.Abs(At(point)) < 0.05f;
    }

    /// <summary>
    /// The ground under (or over) a global point: the first solid found walking
    /// down along <paramref name="up"/> from <paramref name="span"/> above it.
    /// Read from the fills, so it works on shapes too narrow for any blended
    /// estimate, and at any angle on a planet.
    /// </summary>
    public static bool GroundAt(NodeWorld world, Vector3 point, Vector3 up, float span, out Vector3 ground)
    {
        Vector3 localUp = (world.GlobalBasis.Inverse() * up).Normalized();
        Vector3 foot = world.ToLocal(point);
        var field = new ParticleField(world, foot - Vector3.One * 0.5f - localUp.Abs() * span,
            foot + Vector3.One * 0.5f + localUp.Abs() * span);
        float step = world.Grid.NodeSize * 0.5f;
        float above = field.At(foot + localUp * span);

        ground = point;

        if (above > 0f)
            return false;

        for (float h = span - step; h >= -span; h -= step)
        {
            float here = field.At(foot + localUp * h);

            if (here > 0f)
            {
                // Between the empty sample a step above and this solid one.
                float height = h + step * (here / (here - above));
                ground = world.ToGlobal(foot + localUp * height);
                return true;
            }

            above = here;
        }

        return false;
    }
}
