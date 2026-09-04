using Godot;
using GameBase.Density;

namespace GameBase.Nodes;

/// <summary>
/// The shape of the land, as a scalar that increases with depth and a vector
/// that points into the surface.
///
/// One field, shared by every node type in a world. That sharing is what makes
/// it usable: growth contests decide who owns each edge and corner of the
/// lattice, and every node touching a feature must reach the same verdict, so
/// anything that influences a contest has to be a property of the WORLD rather
/// than of the material asking.
///
/// TWO CONSUMERS
///
/// <see cref="TopsoilNode"/> reads the field line to decide which way to cut a
/// soil node's surface — the line points into the ground, so the half it
/// points away from is the half facing the sky.
///
/// <see cref="RawNodeField"/> reads the same line to bend its growth flow.
/// Left to noise alone the contests run in crystal currents, and any material
/// shaped by them reads as bismuth however its surface is finished. Bending
/// them toward the land makes the rims march along the slope instead, which is
/// what lets soil look like soil while still interlocking with the rock.
///
/// WHAT MAKES THE LINE POINT INWARD
///
/// A burial term gives the field a downward bias everywhere, so with nothing
/// else acting the line points straight down — a flat plain, and what any
/// surface reduces to when it is level. A terrain term adds smooth 3D noise,
/// so where the ground swells the field swells with it and the line tilts to
/// point into the swell. On a steep face the tilt dominates and the line runs
/// nearly horizontal, into the hillside.
///
/// Everything here is a pure function of position and seed: no state, no
/// allocation, no neighbour queries.
/// </summary>
public sealed class TerrainField : ITerrainFlow
{
    private readonly int _seed;
    private readonly float _frequency;
    private readonly float _weight;

    /// <summary>
    /// How strongly growth contests bend toward the land.
    ///
    /// One constant, not a per-type default, because it MUST be the same for
    /// every node type in a world. A lattice feature is shared between
    /// neighbouring nodes of possibly different materials, and they interlock
    /// only because each computes the same winner from the same flow — two
    /// types biasing differently would disagree about who owns the space
    /// between them and tear the seam open.
    ///
    /// 2.5 is enough to pull the contests into line with the slope without
    /// flattening the crystal: measured over a block of rock it takes the
    /// distinct-shape count from 3968 to 2818, so the bismuth keeps most of
    /// its variety while its rims start to follow the terrain — which is what
    /// strata do anyway.
    /// </summary>
    public const float ContestBias = 2.5f;

    /// <param name="seed">Same seed, same world.</param>
    /// <param name="scale">Cells per lobe.
    ///
    /// Large, because the field describes a hillside rather than a node. It is
    /// also the main dial on how smooth the soil surface is: the cut plane
    /// follows this field, so a short wavelength makes it turn within a node
    /// or two and the facets step. Measured over adjacent sub-columns, 14
    /// gives 1485 one-cell steps and 21 bigger ones; 90 gives 786 and 2.</param>
    /// <param name="weight">How strongly terrain tilts the line away from
    /// straight down. Around 1 lets a steep face turn it fully horizontal
    /// while leaving flat ground pointing down.</param>
    public TerrainField(int seed, float scale = 90f, float weight = 1.1f)
    {
        _seed = seed;
        _frequency = 1f / Mathf.Max(scale, 1f);
        _weight = Mathf.Max(weight, 0f);
    }

    /// <summary>
    /// How deeply buried a point is. Higher means further into solid ground.
    /// </summary>
    public float Buried(int x, int y, int z)
    {
        // Burial: lower is deeper, so descending y raises the value. Scaled by
        // the same frequency as the terrain term so the two are commensurate
        // and the weight means what it says.
        float burial = -y * _frequency;

        var at = new Vector3(x * _frequency, y * _frequency, z * _frequency);
        float terrain = DensityNoise.Fbm(at, _seed + 9187, 3);

        return burial + terrain * _weight;
    }

    /// <summary>
    /// How far this cell sits from the surface sheet, in cells, measured along
    /// the field line. Positive is below the surface, negative above.
    ///
    /// The surface is a level set of the burial field — the place where it
    /// crosses zero — and this is the signed distance to it. Burial is not a
    /// true distance function, so it is divided by the gradient's magnitude,
    /// which is the standard first-order correction and is accurate enough
    /// within the cell or two that matter here.
    ///
    /// Topsoil uses this to place its cut plane. Without it every node cuts at
    /// a plane fixed to its own centre and the surface restarts in each cell;
    /// with it, adjacent nodes put their facets at the same world height and
    /// the surface becomes one sheet.
    /// </summary>
    public float SurfaceOffset(Vector3I cell)
    {
        float here = Buried(cell.X, cell.Y, cell.Z);

        // The gradient magnitude, from the same central differences the field
        // line uses. Halved because those span two cells.
        float dx = Buried(cell.X + 1, cell.Y, cell.Z) - Buried(cell.X - 1, cell.Y, cell.Z);
        float dy = Buried(cell.X, cell.Y + 1, cell.Z) - Buried(cell.X, cell.Y - 1, cell.Z);
        float dz = Buried(cell.X, cell.Y, cell.Z + 1) - Buried(cell.X, cell.Y, cell.Z - 1);

        float grad = Mathf.Sqrt(dx * dx + dy * dy + dz * dz) * 0.5f;
        if (grad < 0.0001f)
            return 0f;

        return here / grad;
    }

    /// <summary>
    /// The field line at a cell: the direction the ground gets deeper, which
    /// is the direction that points INTO the surface.
    ///
    /// Central differences a cell apart — the right scale to measure at, since
    /// closer would read noise rather than slope and wider would smooth over
    /// the features the surface should follow.
    /// </summary>
    public Vector3 FlowAt(Vector3I cell)
    {
        float east = Buried(cell.X + 1, cell.Y, cell.Z);
        float west = Buried(cell.X - 1, cell.Y, cell.Z);
        float up = Buried(cell.X, cell.Y + 1, cell.Z);
        float down = Buried(cell.X, cell.Y - 1, cell.Z);
        float north = Buried(cell.X, cell.Y, cell.Z + 1);
        float south = Buried(cell.X, cell.Y, cell.Z - 1);

        // Points toward MORE buried, which is into the surface — so unlike an
        // ordinary height gradient it is used as-is rather than negated.
        var flow = new Vector3(east - west, up - down, north - south);

        float length = flow.Length();
        if (length < 0.0001f)
            return Vector3.Down;

        return flow / length;
    }
}
