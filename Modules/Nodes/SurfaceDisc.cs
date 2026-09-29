using Godot;

namespace GameBase.Nodes;

/// <summary>
/// A circle drawn ON the ground: every point of it is <see cref="Radius"/> from
/// its centre measured along the surface, not through the air. It lies flat on
/// flat ground, stands up on a wall, hangs upside down on a ceiling and folds
/// over an edge -- and whatever it lies on, it covers the same area.
///
/// Found by walking. From the centre, a path sets out in each of
/// <see cref="Directions"/> directions across the surface and takes small
/// steps, settling back onto the surface after each and turning to follow it,
/// until it has walked the radius. Where the surface bends, so does the path.
///
/// The brush and its highlight share one disc: the highlight draws its paths,
/// and the shaping weighs each cell by how far along them the nearest point is
/// (see <see cref="ParticleSculpt"/>). What is drawn is what changes.
///
/// Everything is in the world's LOCAL space.
/// </summary>
public sealed class SurfaceDisc
{
    /// <summary>One point on the disc: where it is, the way out of the ground there, and how far it is from the centre along the surface.</summary>
    public readonly record struct Sample(Vector3 Position, Vector3 Normal, float Arc);

    private SurfaceDisc(Sample centre, float radius, Sample[][] paths)
    {
        Centre = centre;
        Radius = radius;
        _paths = paths;
    }

    private readonly Sample[][] _paths;

    public Sample Centre { get; }

    public float Radius { get; }

    public int Directions => _paths.Length;

    /// <summary>Every point of every path, the centre first.</summary>
    public System.Collections.Generic.IEnumerable<Sample> Samples
    {
        get
        {
            yield return Centre;
            foreach (Sample[] path in _paths)
                foreach (Sample sample in path)
                    yield return sample;
        }
    }

    /// <summary>
    /// The point on a path at a distance along the surface from the centre. A
    /// path cut short -- off the edge of the loaded world, say -- ends where it
    /// stopped.
    /// </summary>
    public Sample At(int direction, float arc)
    {
        Sample[] path = _paths[Mathf.PosMod(direction, _paths.Length)];
        Sample previous = Centre;

        foreach (Sample sample in path)
        {
            if (sample.Arc >= arc)
            {
                float span = sample.Arc - previous.Arc;
                float t = span > 1e-5f ? (arc - previous.Arc) / span : 1f;

                return new Sample(
                    previous.Position.Lerp(sample.Position, t),
                    previous.Normal.Lerp(sample.Normal, t).Normalized(),
                    arc);
            }

            previous = sample;
        }

        return previous;
    }

    /// <summary>How many times more steps than a clean walk needs a path may take before it is cut off.</summary>
    private const int MaxStepsPerStride = 4;

    /// <summary>
    /// Walks a disc out from <paramref name="centre"/> (world-local, on or near
    /// the ground) over a patch of the field that covers it.
    /// <paramref name="normalHint"/> is used only where the field cannot say
    /// which way the ground faces.
    /// </summary>
    public static SurfaceDisc Build(ParticleField field, Vector3 centre, Vector3 normalHint, float radius,
        int directions = 40, float step = 0.4f)
    {
        Vector3 start = centre;
        if (!field.Project(ref start))
            start = centre;

        Vector3 up = field.Normal(start, normalHint.Normalized());
        var middle = new Sample(start, up, 0f);

        // Two directions across the ground at the centre.
        Vector3 axis = Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right;
        Vector3 east = axis.Cross(up).Normalized();
        Vector3 north = up.Cross(east).Normalized();

        var paths = new Sample[Mathf.Max(3, directions)][];
        var walked = new System.Collections.Generic.List<Sample>();

        for (int d = 0; d < paths.Length; d++)
        {
            float angle = Mathf.Tau * d / paths.Length;
            Vector3 heading = east * Mathf.Cos(angle) + north * Mathf.Sin(angle);
            Vector3 here = start;
            Vector3 normal = up;
            float arc = 0f;

            walked.Clear();

            // A path that catches on an edge can creep a hair at a time; it is
            // cut off rather than walked for ever. Everything after the disc
            // costs in proportion to its points, and a path of hundreds of
            // thousands of them would stall the game.
            int stepsLeft = MaxStepsPerStride * Mathf.CeilToInt(radius / step) + 8;

            while (arc < radius - 1e-4f && stepsLeft-- > 0)
            {
                float length = Mathf.Min(step, radius - arc);
                Vector3 next = here + heading * length;

                // Settle back onto the ground. A step off an edge lands in the
                // open, and settling pulls it round onto the face below.
                if (!field.Project(ref next))
                    break;

                Vector3 moved = next - here;
                float travelled = moved.Length();

                if (travelled < 1e-4f)
                    break;

                // Settling can swing a step round a sharp corner and make it
                // longer than asked; pull it back so the path measures true.
                if (travelled > length * 1.5f)
                {
                    next = here + moved * (length / travelled);
                    if (!field.Project(ref next))
                        break;

                    moved = next - here;
                    travelled = moved.Length();
                }

                normal = field.Normal(next, normal);

                // Carry on the way the path was actually going, turned to lie
                // along the ground where it now is.
                Vector3 along = moved - normal * moved.Dot(normal);
                if (along.LengthSquared() > 1e-8f)
                    heading = along.Normalized();

                arc += travelled;
                here = next;
                walked.Add(new Sample(here, normal, Mathf.Min(arc, radius)));
            }

            paths[d] = walked.ToArray();
        }

        return new SurfaceDisc(middle, radius, paths);
    }
}
