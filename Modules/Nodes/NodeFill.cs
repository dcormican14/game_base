using Godot;

namespace GameBase.Nodes;

/// <summary>
/// How full a particle cell is, and how that is packed into a byte.
///
/// A fill is a SIGNED DISTANCE, in nodes, from the cell's lattice point to the
/// particle surface: positive inside the material, negative outside, zero on
/// the surface. Storing distance rather than a plain "how full" is what lets
/// the surface sit anywhere between two lattice points instead of snapping to
/// them -- the mesher puts the surface where the distance crosses zero, so a
/// flat bed of particle nodes is flat to the precision of this byte rather than to the
/// size of a node.
///
/// Only the band near the surface matters to the mesher, so the byte covers
/// <see cref="Range"/> nodes either side: the cell on each side of the surface
/// always holds its true distance, with room to spare for a surface that runs
/// diagonally across the lattice. A cell deeper inside is simply full; one
/// further out holds no particle at all and is stored as air.
///
/// Raw nodes and air never carry a meaningful fill: raw nodes read as
/// <see cref="Full"/> and air as <see cref="Empty"/>.
/// </summary>
public static class NodeFill
{
    /// <summary>A cell buried a node or more deep in its material.</summary>
    public const byte Full = 255;

    /// <summary>A cell a node or more clear of any surface.</summary>
    public const byte Empty = 0;

    /// <summary>The byte for a distance of zero: the cell sits on the surface.</summary>
    public const byte Surface = 128;

    /// <summary>How far from the surface a fill can say, in nodes.</summary>
    public const float Range = 1.5f;

    /// <summary>The signed distance a byte stores, in nodes, -Range..+Range.</summary>
    public static float ToLevel(byte fill) => fill * (2f * Range / 255f) - Range;

    /// <summary>The byte for a signed distance in nodes, clamped to -Range..+Range.</summary>
    public static byte FromLevel(float level)
    {
        float t = (Mathf.Clamp(level, -Range, Range) + Range) / (2f * Range);
        return (byte)Mathf.RoundToInt(t * 255f);
    }

    /// <summary>
    /// The byte for a signed distance, rounded up or down at random in
    /// proportion to how close it is to each: <paramref name="threshold"/> is a
    /// uniform random number in 0..1. A change smaller than one step -- a
    /// brush's faint edge on a single frame -- is kept as the odds of a whole
    /// step, so a slow, gradual change still arrives at the right rate on
    /// average instead of being rounded away every frame.
    /// </summary>
    public static byte FromLevelDithered(float level, float threshold)
    {
        float t = (Mathf.Clamp(level, -Range, Range) + Range) / (2f * Range);
        return (byte)Mathf.Clamp(Mathf.FloorToInt(t * 255f + threshold), 0, 255);
    }

    /// <summary>Is a cell with this fill inside its material?</summary>
    public static bool IsInside(byte fill) => fill >= Surface;
}
