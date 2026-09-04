using Godot;
using GameBase.Density;

namespace GameBase.Nodes;

/// <summary>
/// Topsoil: the transition node between raw rock and open sky.
///
/// The shape comes from a single FIELD LINE that always points INTO the
/// surface — down on a flat top, down and into the hill on a slope,
/// horizontally into the rock on a cliff face. The half of the node the line
/// points toward is grown by the raw rule so it interlocks with the crystal;
/// the half it points away from is cut back to make the surface. See
/// <see cref="TopsoilGeometry"/> for how that cut is applied; this class owns
/// the field the line is read from.
///
/// THE FIELD LINE
///
/// The line is the gradient of a smooth scalar field arranged so that higher
/// means "deeper into solid ground". Two terms make that true.
///
/// A BURIAL TERM gives the field a downward bias everywhere, so with nothing
/// else acting the line points straight down — which is what a flat plain
/// should produce, and what any surface reduces to when it is level.
///
/// A TERRAIN TERM adds smooth 3D noise, so where the ground swells the field
/// swells with it and the line tilts to point into the swell. On the side of a
/// hill that tilt is most of the vector, which is what makes the line point
/// into the mountain rather than merely downward.
///
/// The two are weighted so terrain can overcome burial on a steep face but
/// never on flat ground. That ratio is the one dial deciding whether the world
/// reads as rolling hills or as terraced steps.
///
/// WHY ITS OWN FIELD AND NOT THE ISLAND DENSITY
///
/// A node type may depend only on its cell and the seed. That is the
/// <see cref="INodeType"/> contract, it is what lets a node be shaped without
/// consulting neighbours, and it is what makes planet-scale generation
/// affordable. Reaching into the level generator would break it for every node
/// a player places away from an island — there would be no island surface to
/// follow — and would cost a full density evaluation per node at mesh time.
/// This field is defined everywhere and costs a few noise samples.
/// </summary>
public sealed class TopsoilNode : INodeType
{
    private readonly RawNodeField _raw;
    private readonly int _seed;
    private readonly float _frequency;
    private readonly float _terrainWeight;

    /// <param name="seed">Same seed, same world.</param>
    /// <param name="flowScale">Cells per lobe of the raw growth flow, passed
    /// straight through so the interlock half matches the rock exactly.</param>
    /// <param name="roughness">Raw contest jitter, as above.</param>
    /// <param name="growth">Raw contest count, as above.</param>
    /// <param name="terrainScale">Cells per lobe of the terrain term. Large,
    /// because the field line should follow the shape of a hillside rather
    /// than wobble per node — at 28 a slope holds its direction for tens of
    /// nodes.</param>
    /// <param name="terrainWeight">How strongly terrain tilts the line away
    /// from straight down. Around 1 lets a steep face turn it fully horizontal
    /// while leaving flat ground pointing down.</param>
    public TopsoilNode(int seed, float flowScale = 6f, float roughness = 0.35f,
        float growth = 0.7f, float terrainScale = 28f, float terrainWeight = 1.1f)
    {
        _raw = new RawNodeField(seed, flowScale, roughness, growth);
        _seed = seed;
        _frequency = 1f / Mathf.Max(terrainScale, 1f);
        _terrainWeight = Mathf.Max(terrainWeight, 0f);
    }

    public string Id => "topsoil";

    public int Subdivision => RawNodeGeometry.Sub;

    public NodeShape ShapeAt(Vector3I cell)
    {
        TopsoilGeometry.Mask mask = MaskFor(cell);

        // The handle packs the whole shape: the raw mask in one int, the
        // quantised field line in the other. Equal handles mean identical
        // geometry, which is what the mesher's per-shape cache requires.
        int a = mask.Raw.Corners << 12 | mask.Raw.Edges;
        int b = (mask.Flow.X + TopsoilGeometry.FlowSteps) * 25
            + (mask.Flow.Y + TopsoilGeometry.FlowSteps) * 5
            + mask.Flow.Z + TopsoilGeometry.FlowSteps;
        return new NodeShape(a, b);
    }

    public NodeMesh MeshFor(NodeShape shape) => TopsoilGeometry.Get(ToMask(shape));

    public int[] OccupiedCells(NodeShape shape) => TopsoilGeometry.OccupiedCells(ToMask(shape));

    public bool Occupies(NodeShape shape, int i, int j, int k) =>
        TopsoilGeometry.Occupies(ToMask(shape), i, j, k);

    private static TopsoilGeometry.Mask ToMask(NodeShape shape)
    {
        var raw = new RawNodeGeometry.Mask(shape.A >> 12, shape.A & 0xFFF);

        int b = shape.B;
        const int steps = TopsoilGeometry.FlowSteps;
        var flow = new Vector3I(
            b / 25 - steps,
            b / 5 % 5 - steps,
            b % 5 - steps);

        return new TopsoilGeometry.Mask(raw, flow);
    }

    /// <summary>
    /// The shape of the topsoil node at a cell: its raw interlock, and the
    /// field line that cuts its surface.
    /// </summary>
    private TopsoilGeometry.Mask MaskFor(Vector3I cell) =>
        new(_raw.MaskFor(cell), FlowAt(cell));

    /// <summary>
    /// The field line at a cell, quantised for the shape cache.
    ///
    /// The gradient is taken by central differences a cell apart, which is the
    /// right scale to measure at: closer would read noise rather than slope,
    /// and wider would smooth over the features the surface should follow.
    /// </summary>
    private Vector3I FlowAt(Vector3I cell)
    {
        float east = Buried(cell.X + 1, cell.Y, cell.Z);
        float west = Buried(cell.X - 1, cell.Y, cell.Z);
        float up = Buried(cell.X, cell.Y + 1, cell.Z);
        float down = Buried(cell.X, cell.Y - 1, cell.Z);
        float north = Buried(cell.X, cell.Y, cell.Z + 1);
        float south = Buried(cell.X, cell.Y, cell.Z - 1);

        // The gradient points toward MORE buried, which is into the surface —
        // so unlike an ordinary height gradient it is used as-is rather than
        // negated.
        var flow = new Vector3(east - west, up - down, north - south);

        float length = flow.Length();
        if (length < 0.0001f)
        {
            // Degenerate: no direction at all. Straight down is what flat
            // ground means.
            return new Vector3I(0, -TopsoilGeometry.FlowSteps, 0);
        }

        flow /= length;

        const int steps = TopsoilGeometry.FlowSteps;
        var quantised = new Vector3I(
            Mathf.Clamp(Mathf.RoundToInt(flow.X * steps), -steps, steps),
            Mathf.Clamp(Mathf.RoundToInt(flow.Y * steps), -steps, steps),
            Mathf.Clamp(Mathf.RoundToInt(flow.Z * steps), -steps, steps));

        // Quantising can round every component to zero for a direction sitting
        // between steps. Falling back to straight down keeps the node shaped
        // like flat ground rather than leaving it a bare cube.
        if (quantised == Vector3I.Zero)
            return new Vector3I(0, -steps, 0);

        return quantised;
    }

    /// <summary>
    /// How deeply buried a point is — the scalar whose gradient is the field
    /// line. Higher means further into solid ground.
    ///
    /// The downward bias is what makes the line point straight down on flat
    /// ground; the noise term is what tilts it into a hillside. Their ratio
    /// decides whether slopes roll or terrace.
    /// </summary>
    private float Buried(int x, int y, int z)
    {
        // Burial: lower is deeper, so descending y raises the value. Scaled by
        // the same frequency as the terrain term so the two are commensurate
        // and the weight below means what it says.
        float burial = -y * _frequency;

        var at = new Vector3(x * _frequency, y * _frequency, z * _frequency);
        float terrain = DensityNoise.Fbm(at, _seed + 9187, 3);

        return burial + terrain * _terrainWeight;
    }
}
