using Godot;

namespace GameBase.Nodes;

/// <summary>
/// Precomputed per-shell tables for one planet.
///
/// Everything the mesher asks about a shell -- its resolution, its radius, how
/// far its grid is inset from the base grid -- is a pure function of the shell
/// index, and the mesher asks per CELL. Computing them on the fly means a
/// division and a loop (LevelAt walks octaves) for every one of millions of
/// lookups.
///
/// A shell index is a small dense integer, so the answers fit in flat arrays
/// and every lookup becomes one bounds-checked index. That is the whole idea:
/// turn repeated arithmetic into a table built once when the planet is made.
/// </summary>
/// <remarks>
/// Built eagerly rather than memoised on demand. A planet has a few hundred
/// shells, so the table is a few kilobytes and costs microseconds to fill --
/// far cheaper than the branch a lazy cache would put in the hot path, and it
/// makes the arrays immutable once published, which is what lets worker threads
/// read them without a lock.
/// </remarks>
public sealed class QuadSphereTables
{
    /// <summary>Cells across a face, per shell.</summary>
    private readonly int[] _resolution;

    /// <summary>Outer radius of each shell.</summary>
    private readonly float[] _radius;

    /// <summary>
    /// Log2 of how many base cells one cell of this shell spans.
    ///
    /// Stored as a SHIFT rather than a ratio because every use of it is a
    /// multiply or divide by a power of two, and a shift is exact where float
    /// division is not.
    /// </summary>
    private readonly int[] _shift;

    public QuadSphereTables(float surfaceRadius, float nodeSize)
    {
        SurfaceRadius = surfaceRadius;
        NodeSize = Mathf.Max(nodeSize, 0.0001f);

        BaseResolution = QuadSphere.SurfaceResolution(SurfaceRadius, NodeSize);
        ShellCount = QuadSphere.ShellCount(SurfaceRadius, NodeSize);

        _resolution = new int[ShellCount];
        _radius = new float[ShellCount];
        _shift = new int[ShellCount];

        for (int shell = 0; shell < ShellCount; shell++)
        {
            int res = QuadSphere.ResolutionAt(shell, SurfaceRadius, NodeSize);

            _resolution[shell] = res;
            _radius[shell] = QuadSphere.RadiusOf(shell, SurfaceRadius, NodeSize);

            // BaseResolution / res, as a shift. Both are powers of two, so the
            // ratio is exact and the loop runs at most about twenty times.
            int shift = 0;
            for (int r = res; r < BaseResolution; r <<= 1)
                shift++;

            _shift[shell] = shift;
        }
    }

    public float SurfaceRadius { get; }
    public float NodeSize { get; }

    /// <summary>Cells across a face at the surface.</summary>
    public int BaseResolution { get; }

    /// <summary>Shells of rock from the surface to the solid core.</summary>
    public int ShellCount { get; }

    /// <summary>Is this a shell the planet actually has?</summary>
    public bool HasShell(int shell) => (uint)shell < (uint)ShellCount;

    /// <summary>Cells across a face at this shell. O(1).</summary>
    public int Resolution(int shell) => _resolution[shell];

    /// <summary>Outer radius of this shell. O(1).</summary>
    public float Radius(int shell) => _radius[shell];

    /// <summary>
    /// How many times to halve a base-grid coordinate to reach this shell's
    /// grid. O(1).
    /// </summary>
    public int Shift(int shell) => _shift[shell];
}
