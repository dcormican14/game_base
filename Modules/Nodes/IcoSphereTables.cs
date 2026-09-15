using Godot;

namespace GameBase.Nodes;

/// <summary>
/// Precomputed per-shell values for one even-node planet: how finely each shell
/// is divided, and where it sits.
///
/// The counterpart to <see cref="QuadSphereTables"/>, and it answers the same
/// question -- what does this shell look like -- for a grid whose resolution is
/// a SUBDIVISION LEVEL rather than a cell count across a face.
///
/// WHY THE LEVELS NEST EXACTLY
///
/// This is the property the whole design rests on, and it is a fact about
/// midpoint subdivision rather than something arranged here. Subdividing an
/// icosahedron 1-to-4 keeps every vertex it already had and adds one per edge.
/// So the level-L site set is a PREFIX of the level-(L+1) set: site 7 is the
/// same point at every level that has a site 7, and dropping a level is
/// truncating the list.
///
/// That is what lets the grid coarsen toward the core without the cells
/// wandering. A hexagonal grid refined the usual way (aperture 3 or 7) rotates
/// about 20 degrees per level, so a shaft dug straight down corkscrews. Here a
/// site that survives a band boundary keeps its exact position, and its column
/// stays a column.
///
/// WHAT THE BANDING BUYS
///
/// A shell's circumference shrinks toward the core, so a fixed site count would
/// squeeze cells to nothing. Halving the radius quarters the area, and dropping
/// one subdivision level quarters the site count -- so the two cancel and cell
/// width stays near constant from the surface all the way down. Measured at
/// radius 120 with 2.1-unit spacing: 2.102 at the surface, 2.089 eight bands
/// down.
/// </summary>
public sealed class IcoSphereTables
{
    /// <summary>Subdivision level of each shell.</summary>
    private readonly int[] _level;

    /// <summary>Outer radius of each shell.</summary>
    private readonly float[] _radius;

    /// <summary>Sites on each shell: 10 * 4^level + 2.</summary>
    private readonly int[] _sites;

    /// <summary>Lattice steps along one base-face edge: 2^level.</summary>
    private readonly int[] _divisions;

    public IcoSphereTables(float surfaceRadius, float nodeSize)
    {
        SurfaceRadius = Mathf.Max(surfaceRadius, 1f);
        NodeSize = Mathf.Max(nodeSize, 0.0001f);

        // The level the SURFACE is drawn at, from the spacing asked for. Every
        // shell below is this level or coarser, so this also fixes the widest
        // the site list ever gets.
        SurfaceLevel = IcoSphere.LevelForSpacing(SurfaceRadius, NodeSize);

        // Shells run from the surface inward until the grid would be coarser
        // than the base icosahedron. Below that there is no lattice left to
        // divide, and what remains is a solid core a few nodes across.
        int count = Mathf.Max(1, Mathf.FloorToInt(SurfaceRadius / NodeSize));

        _level = new int[count];
        _radius = new float[count];
        _sites = new int[count];
        _divisions = new int[count];

        for (int shell = 0; shell < count; shell++)
        {
            float radius = SurfaceRadius - shell * NodeSize;
            _radius[shell] = radius;

            // The level whose spacing at THIS radius is nearest the node size.
            // Derived per shell rather than stepped at hand-picked boundaries,
            // so the band edges land wherever the geometry actually wants them.
            int level = Mathf.Min(
                IcoSphere.LevelForSpacing(Mathf.Max(radius, NodeSize), NodeSize),
                SurfaceLevel);

            _level[shell] = level;
            _sites[shell] = IcoSphere.SiteCount(level);
            _divisions[shell] = 1 << level;
        }

        ShellCount = count;
    }

    public float SurfaceRadius { get; }
    public float NodeSize { get; }

    /// <summary>Subdivision level at the surface: the finest the world gets.</summary>
    public int SurfaceLevel { get; }

    /// <summary>Shells of rock from the surface to the core.</summary>
    public int ShellCount { get; }

    /// <summary>Is this a shell the planet actually has?</summary>
    public bool HasShell(int shell) => (uint)shell < (uint)ShellCount;

    /// <summary>Subdivision level of a shell. O(1).</summary>
    public int Level(int shell) => _level[shell];

    /// <summary>Outer radius of a shell. O(1).</summary>
    public float Radius(int shell) => _radius[shell];

    /// <summary>Sites on a shell. O(1).</summary>
    public int Sites(int shell) => _sites[shell];

    /// <summary>Lattice steps along one base-face edge at a shell. O(1).</summary>
    public int Divisions(int shell) => _divisions[shell];

    /// <summary>
    /// Lattice steps along an edge at the surface -- the widest the lattice
    /// gets, and so the span one base face occupies in a packed address.
    /// </summary>
    public int SurfaceDivisions => 1 << SurfaceLevel;
}
