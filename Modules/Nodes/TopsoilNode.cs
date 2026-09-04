using Godot;
using System.Collections.Generic;
using GameBase.Density;

namespace GameBase.Nodes;

/// <summary>
/// Topsoil: the transition node between raw rock and open sky.
///
/// Its lower half is raw crystal — the same contest, the same rims — so it
/// interlocks with the rock beneath it exactly. Its upper half follows a
/// smooth contour field instead, so the ground rounds over the way soil
/// settles. See <see cref="TopsoilGeometry"/> for how the two halves are
/// joined; this class is the <see cref="INodeType"/> face of it, and owns the
/// contour field.
///
/// THE CONTOUR FIELD
///
/// A single smooth 3D noise field stands in for altitude. What matters is not
/// its absolute value but its GRADIENT: the direction the field falls is the
/// direction the soil thins, so the one-sub-cell step in each node sits
/// perpendicular to it. Because the field varies smoothly over many cells, the
/// gradient barely changes from one node to the next, and their steps line up
/// into contour bands running across a slope.
///
/// The gradient is taken by finite differences a cell apart, which is the
/// right scale: sampled any closer it would measure noise rather than slope,
/// and any further it would smooth over the terrain features the contours are
/// meant to trace.
///
/// It is deliberately its own field rather than the island density it sits on.
/// A node type may only depend on its cell and the seed — that is what lets a
/// node be shaped without consulting its neighbours and what makes it
/// affordable at planet scale — and reaching into the level generator would
/// break that for every node the player places away from an island, which has
/// no island surface to follow. This field is defined everywhere.
/// </summary>
public sealed class TopsoilNode : INodeType
{
    private readonly RawNodeField _raw;
    private readonly int _seed;
    private readonly float _frequency;

    /// <param name="seed">Same seed, same world.</param>
    /// <param name="flowScale">Cells per lobe of the raw growth flow, passed
    /// straight through so the interlock half matches the rock exactly.</param>
    /// <param name="roughness">Raw contest jitter, as above.</param>
    /// <param name="growth">Raw contest count, as above.</param>
    /// <param name="contourScale">Cells per lobe of the contour field. Large,
    /// because contours should read as the shape of a hillside rather than as
    /// per-node variation — at 40 a band runs for tens of nodes before it
    /// turns.</param>
    public TopsoilNode(int seed, float flowScale = 6f, float roughness = 0.35f,
        float growth = 0.7f, float contourScale = 40f)
    {
        _raw = new RawNodeField(seed, flowScale, roughness, growth);
        _seed = seed;
        _frequency = 1f / Mathf.Max(contourScale, 1f);
    }

    public string Id => "topsoil";

    public int Subdivision => RawNodeGeometry.Sub;

    public NodeShape ShapeAt(Vector3I cell) => Intern(MaskFor(cell));

    // A topsoil shape depends on three things — this node's raw mask, the mask
    // the node above would wear, and the contour — which is more than the two
    // ints a NodeShape carries. Rather than widen the interface for one type,
    // distinct masks are interned and the handle is an index into them.
    //
    // The contract is unchanged and is what matters: equal handles still mean
    // identical geometry, which is all the mesher's per-shape cache requires.
    // The table stays small because a world reuses a few hundred distinct
    // shapes over millions of nodes.
    private readonly Dictionary<(int, int, int), int> _shapeIds = new();
    private readonly List<TopsoilGeometry.Mask> _shapes = new();
    private readonly object _shapeLock = new();

    /// <summary>The handle for a mask, assigning a new one if unseen.</summary>
    private NodeShape Intern(in TopsoilGeometry.Mask mask)
    {
        var key = (
            mask.Raw.Corners << 12 | mask.Raw.Edges,
            mask.CededTop,
            (mask.Fall + 1) << 1 | mask.Band);

        lock (_shapeLock)
        {
            if (_shapeIds.TryGetValue(key, out int existing))
                return new NodeShape(existing);

            int id = _shapes.Count;
            _shapes.Add(mask);
            _shapeIds[key] = id;
            return new NodeShape(id);
        }
    }

    /// <summary>The mask a handle names.</summary>
    private TopsoilGeometry.Mask FromHandle(NodeShape shape)
    {
        lock (_shapeLock)
            return _shapes[shape.A];
    }

    public NodeMesh MeshFor(NodeShape shape) => TopsoilGeometry.Get(FromHandle(shape));

    public int[] OccupiedCells(NodeShape shape) =>
        TopsoilGeometry.OccupiedCells(FromHandle(shape));

    public bool Occupies(NodeShape shape, int i, int j, int k) =>
        TopsoilGeometry.Occupies(FromHandle(shape), i, j, k);

    /// <summary>
    /// The shape of the topsoil node at a cell: its raw interlock, and which
    /// way its contour falls.
    /// </summary>
    private TopsoilGeometry.Mask MaskFor(Vector3I cell)
    {
        RawNodeGeometry.Mask raw = _raw.MaskFor(cell);

        int ceded = CededTop(cell);

        // The contour field, sampled at the node and one cell either side on
        // each horizontal axis. Two octaves: the first gives the hillside its
        // overall lie, the second enough variation that contours meander
        // rather than running dead straight.
        float here = Contour(cell.X, cell.Y, cell.Z);
        float east = Contour(cell.X + 1, cell.Y, cell.Z);
        float west = Contour(cell.X - 1, cell.Y, cell.Z);
        float north = Contour(cell.X, cell.Y, cell.Z + 1);
        float south = Contour(cell.X, cell.Y, cell.Z - 1);

        // Central differences: the direction the field falls fastest.
        float dx = east - west;
        float dz = north - south;

        float slope = Mathf.Sqrt(dx * dx + dz * dz);

        // Flat enough that no direction is meaningful. Left level rather than
        // given an arbitrary fall, so plateaus read flat instead of picking up
        // a spurious step from noise in the gradient.
        const float FlatThreshold = 0.01f;
        if (slope < FlatThreshold)
            return new TopsoilGeometry.Mask(raw, ceded, -1, 0);

        // Quantise the downhill direction to one of eight compass points.
        // Negated because the gradient points UPhill and the soil thins going
        // down.
        float angle = Mathf.Atan2(-dz, -dx);
        int fall = Mathf.PosMod(Mathf.RoundToInt(angle / (Mathf.Pi / 4f)), 8);

        // Which half of the contour band this node sits in. Taken from the
        // field's own value, so the step climbs with the terrain and
        // neighbouring nodes on a slope place it at successive heights —
        // which is what joins their individual steps into a running contour.
        int band = Mathf.PosMod(Mathf.FloorToInt(here * BandsPerUnit), 2);

        return new TopsoilGeometry.Mask(raw, ceded, fall, band);
    }

    /// <summary>
    /// Which columns of this node's top quarter-cell layer the cells above
    /// already claim, as a bitmask indexed i * Sub + k.
    ///
    /// Nine cells are consulted, not one. A raw corner win takes the whole
    /// 2x2x2 straddling a lattice corner, so a node diagonally above reaches
    /// down AND sideways into this layer — arbitrating against only the cell
    /// directly overhead left 272 sub-cells doubly claimed, every one of them
    /// against a diagonal neighbour.
    ///
    /// None of this reads the world. Each mask is recomputed from its cell
    /// position, which is the same pure function the node up there will run,
    /// so both sides reach the same verdict about who owns the space without
    /// either having asked what is actually placed.
    /// </summary>
    private int CededTop(Vector3I cell)
    {
        int sub = RawNodeGeometry.Sub;
        int ceded = 0;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                var neighbour = new Vector3I(cell.X + dx, cell.Y + 1, cell.Z + dz);
                RawNodeGeometry.Mask mask = _raw.MaskFor(neighbour);

                for (int i = 0; i < sub; i++)
                {
                    for (int k = 0; k < sub; k++)
                    {
                        // This node's (i, top, k) in the neighbour's own
                        // coordinates: one cell down in y, and offset by
                        // however far the neighbour sits in x and z.
                        int ni = i - dx * sub;
                        int nk = k - dz * sub;

                        if (RawNodeGeometry.Occupies(mask, ni, -1, nk))
                            ceded |= 1 << (i * sub + k);
                    }
                }
            }
        }

        return ceded;
    }

    /// <summary>
    /// How many contour bands the field's full range is divided into.
    ///
    /// Sets how tightly the banding follows the terrain: higher packs the
    /// contours closer together. Tuned so a typical slope shows a band every
    /// couple of nodes, which is fine enough to read as topography and coarse
    /// enough that each band survives being drawn in quarter-cells.
    /// </summary>
    private const float BandsPerUnit = 6f;

    /// <summary>
    /// The contour field at a cell — a stand-in for altitude.
    ///
    /// Includes Y, so soil on the underside of an overhang and soil on the top
    /// of the island above it get different contours rather than sharing one
    /// pattern smeared vertically through the world.
    /// </summary>
    private float Contour(int x, int y, int z)
    {
        var at = new Vector3(x * _frequency, y * _frequency * 0.5f, z * _frequency);
        return DensityNoise.Fbm(at, _seed + 9187, 2);
    }
}
