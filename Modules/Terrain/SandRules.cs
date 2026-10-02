using Godot;

namespace GameBase.Terrain;

/// <summary>
/// Where sand lies on the terrain: how deep on gentle ground, and between which
/// slopes it thins to nothing. Shared by the chunk generator and the far
/// terrain, so the two agree on where rock shows.
/// </summary>
public readonly record struct SandRules(float Depth, float FullSlope, float NoneSlope, float Minimum)
{
    /// <summary>The same depth everywhere, whatever the slope: the plain round planet's rule.</summary>
    public static SandRules Uniform(float depth) => new(depth, 90f, 90f, 0f);

    public static SandRules From(TerrainSettings settings) => new(
        settings.SandDepth, settings.SandFullSlope, settings.SandNoneSlope, settings.SandMinimum);

    /// <summary>
    /// How deep the sand is on ground facing <paramref name="outward"/> (need
    /// not be normalised) at a point, with up away from the centre.
    /// </summary>
    public float DepthOn(Vector3 outward, Vector3 point)
    {
        if (FullSlope >= 90f)
            return Depth;

        float length = outward.Length() * point.Length();
        if (length < 1e-6f)
            return Depth;

        // Cosine of the slope: 1 on level ground, 0 on a wall, negative under
        // an overhang.
        float facing = outward.Dot(point) / length;

        float full = Mathf.Cos(Mathf.DegToRad(FullSlope));
        float none = Mathf.Cos(Mathf.DegToRad(NoneSlope));
        float t = Mathf.SmoothStep(none, full, facing);

        float depth = Depth * t;
        return depth < Minimum ? 0f : depth;
    }
}
