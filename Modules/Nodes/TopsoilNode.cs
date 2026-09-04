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
    private readonly TerrainField _terrain;

    /// <param name="seed">Same seed, same world.</param>
    /// <param name="flowScale">Cells per lobe of the raw growth flow, passed
    /// straight through so the interlock half matches the rock exactly.</param>
    /// <param name="roughness">Raw contest jitter, as above.</param>
    /// <param name="growth">Raw contest count, as above.</param>
    /// <param name="terrain">The world's terrain field. Shared with the rock
    /// so both decide shared features the same way.</param>
    /// <param name="terrainBias">How strongly the growth contests bend toward
    /// the land. This is the dial that decides whether soil reads as soil or
    /// as another kind of crystal.</param>
    public TopsoilNode(int seed, float flowScale = 6f, float roughness = 0.35f,
        float growth = 0.7f, TerrainField terrain = null,
        float terrainBias = TerrainField.ContestBias)
    {
        _terrain = terrain ?? new TerrainField(seed);

        // The contests that shape this node are bent toward the land.
        //
        // Without this the winners follow the crystal's own noise currents,
        // and since space must be exactly tiled — a feature this node loses is
        // always filled by the neighbour that won it, verified exhaustively —
        // the soil has no way to keep that space back. Its rims therefore
        // march in the same currents the bismuth does, and it reads as another
        // kind of rock however its surface is cut. Measured, the contests were
        // removing 7770 sub-cells of the visible top layer against the field
        // line's 3984.
        //
        // Biasing the flow does not break the interlock, because the bias is a
        // property of the FEATURE rather than of the material: the rock beside
        // this node bends the same flow by the same amount at the same lattice
        // position and reaches the same verdict.
        _raw = new RawNodeField(seed, flowScale, roughness, growth, _terrain, terrainBias);
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
        int b = ((mask.Flow.X + TopsoilGeometry.FlowSteps) * 25
            + (mask.Flow.Y + TopsoilGeometry.FlowSteps) * 5
            + mask.Flow.Z + TopsoilGeometry.FlowSteps)
            * (TopsoilGeometry.MaxPhase * 2 + 1)
            + mask.Phase + TopsoilGeometry.MaxPhase;
        return new NodeShape(a, b);
    }

    public NodeMesh MeshFor(NodeShape shape) => TopsoilGeometry.Get(ToMask(shape));

    public int[] OccupiedCells(NodeShape shape) => TopsoilGeometry.OccupiedCells(ToMask(shape));

    public bool Occupies(NodeShape shape, int i, int j, int k) =>
        TopsoilGeometry.Occupies(ToMask(shape), i, j, k);

    private static TopsoilGeometry.Mask ToMask(NodeShape shape)
    {
        var raw = new RawNodeGeometry.Mask(shape.A >> 12, shape.A & 0xFFF);

        const int steps = TopsoilGeometry.FlowSteps;
        const int span = TopsoilGeometry.MaxPhase * 2 + 1;

        int phase = shape.B % span - TopsoilGeometry.MaxPhase;
        int f = shape.B / span;
        var flow = new Vector3I(
            f / 25 - steps,
            f / 5 % 5 - steps,
            f % 5 - steps);

        return new TopsoilGeometry.Mask(raw, flow, phase);
    }

    /// <summary>
    /// The shape of the topsoil node at a cell: its raw interlock, and the
    /// field line that cuts its surface.
    /// </summary>
    private TopsoilGeometry.Mask MaskFor(Vector3I cell) =>
        new(_raw.MaskFor(cell), FlowAt(cell), PhaseAt(cell));

    /// <summary>
    /// Where this node's cut plane sits, relative to its own centre, so that
    /// the plane lands at the same place in the WORLD as its neighbours'.
    ///
    /// The surface is one continuous sheet through the terrain: the level set
    /// where burial crosses a fixed value. A node cutting at a plane fixed to
    /// its own centre restarts that sheet in every cell, which is why the
    /// facets used to be flat within a node and step at every boundary. The
    /// burial value at the node's centre says how far this node sits from the
    /// sheet, and shifting the plane by that much puts every node's facet back
    /// on the one surface.
    ///
    /// Scaled into quarter-cells and rounded, because that is the resolution
    /// the geometry can express — and rounding is what keeps the number of
    /// distinct shapes small enough to cache.
    /// </summary>
    private int PhaseAt(Vector3I cell)
    {
        // How far this cell sits from the surface, in cells, along the field
        // line. Burial rises going down at one unit per cell of depth (the
        // field's own scaling), so the raw value is already a distance.
        float depth = _terrain.SurfaceOffset(cell);

        // Into quarter-cells, and clamped: beyond a node's own extent the
        // plane no longer intersects it and the shape saturates to solid or
        // empty, so there is nothing to gain by tracking it further.
        int phase = Mathf.RoundToInt(depth * TopsoilGeometry.Sub);
        return Mathf.Clamp(phase, -TopsoilGeometry.MaxPhase, TopsoilGeometry.MaxPhase);
    }

    /// <summary>
    /// The field line at a cell, quantised for the shape cache.
    ///
    /// The gradient is taken by central differences a cell apart, which is the
    /// right scale to measure at: closer would read noise rather than slope,
    /// and wider would smooth over the features the surface should follow.
    /// </summary>
    private Vector3I FlowAt(Vector3I cell)
    {
        Vector3 flow = _terrain.FlowAt(cell);

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

}
