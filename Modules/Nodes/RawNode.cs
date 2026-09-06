using Godot;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// Raw nodes: unworked bismuth crystal, the world's default rock.
///
/// Growth lives on the 12 edges and 8 corners of each node rather than on its
/// faces, because real bismuth grows fastest where the most free space meets.
/// Flat faces stay flat; the rim around them steps out or pulls back, and a
/// corner reaches twice as far as an edge.
///
/// Each contested edge and corner is awarded whole to a single owner, chosen
/// by hashing that lattice feature's position, so neighbouring nodes interlock
/// exactly — see <see cref="RawNodeField"/> for the contest and
/// <see cref="RawNodeGeometry"/> for the geometry it produces.
///
/// This class is the <see cref="INodeType"/> face of those two: it holds the
/// dials, owns the field instance, and translates between the interface's
/// opaque <see cref="NodeShape"/> and the raw solver's mask.
/// </summary>
public sealed class RawNode : INodeType
{
    private readonly RawNodeField _field;

    /// <param name="seed">Same seed, same world.</param>
    /// <param name="flowScale">Cells per lobe of the growth flow. Larger gives
    /// long, lazy currents; smaller a busier, more granular crystal.</param>
    /// <param name="roughness">0 lets the flow decide every contest, so growth
    /// runs directionally; 1 makes contests random and the crystal chaotic.</param>
    /// <param name="growth">How many edge and corner contests are awarded at
    /// all. 0 leaves every rim flat (plain cubes); 1 claims every one.</param>
    /// <param name="terrain">The world's terrain field, bending the growth
    /// contests toward the shape of the land.</param>
    /// <param name="terrainBias">How strongly to bend. MUST match what every
    /// other type in the world uses: a lattice feature is shared between
    /// neighbouring nodes, and the two only interlock because they compute the
    /// same winner from the same flow. A rock node biasing differently from
    /// the soil node beside it would have them disagree about who owns the
    /// space between them.</param>
    public RawNode(int seed, float flowScale = 6f, float roughness = 0.35f,
        float growth = 0.7f, TerrainField terrain = null,
        float terrainBias = TerrainField.ContestBias)
    {
        _field = new RawNodeField(seed, flowScale, roughness, growth,
            terrain ?? new TerrainField(seed), terrainBias);
    }

    public string Id => "raw";

    public int Subdivision => RawNodeGeometry.Sub;

    public NodeShape ShapeAt(Vector3I cell)
    {
        RawNodeGeometry.Mask mask = _field.MaskFor(cell);
        return new NodeShape(mask.Corners, mask.Edges);
    }

    public NodeMesh MeshFor(NodeShape shape) => RawNodeGeometry.Get(ToMask(shape));

    public int[] OccupiedCells(NodeShape shape) => RawNodeGeometry.OccupiedCells(ToMask(shape));

    public bool Occupies(NodeShape shape, int i, int j, int k) =>
        RawNodeGeometry.Occupies(ToMask(shape), i, j, k);

    private static RawNodeGeometry.Mask ToMask(NodeShape shape) => new(shape.A, shape.B);
}

/// <summary>
/// The node types a world can be built from, by <see cref="INodeType.Id"/>.
/// New types register here and become selectable without the mesher changing.
/// </summary>
public static class NodeTypes
{
    /// <summary>Every type this build knows how to construct, keyed by id.</summary>
    private static readonly Dictionary<string, System.Func<int, float, float, float, INodeType>> Factories = new()
    {
        ["raw"] = (seed, scale, roughness, growth) =>
            new RawNode(seed, scale, roughness, growth, TerrainFor(seed)),

        // Plain cubes ignore every growth dial — the shape has no freedom to
        // spend them on.
        ["plain"] = (_, _, _, _) => new PlainNode(),

        // Topsoil ignores every growth dial. Its shape comes from which of
        // its neighbours hold ground, not from the crystal contests — soil is
        // a surface, not a mineral.
        //
        // It takes the world's terrain field all the same, because it has to
        // work out what the rock BENEATH it looks like in order to leave that
        // rock's rims alone, and it must reach the same verdict the rock does.
        ["topsoil"] = (seed, _, _, _) => new TopsoilNode(seed, TerrainFor(seed)),
    };

    /// <summary>
    /// The world's terrain field, one per seed.
    ///
    /// Shared deliberately. The field bends every growth contest toward the
    /// shape of the land, and a lattice feature is shared between neighbouring
    /// nodes of possibly different materials — they interlock only because
    /// each computes the same winner from the same flow. Two types holding
    /// separate fields with the same seed would agree by luck; holding the
    /// same one, they agree by construction.
    /// </summary>
    private static readonly Dictionary<int, TerrainField> Terrains = new();
    private static readonly object TerrainLock = new();

    private static TerrainField TerrainFor(int seed)
    {
        lock (TerrainLock)
        {
            if (Terrains.TryGetValue(seed, out TerrainField found))
                return found;

            var built = new TerrainField(seed);
            Terrains[seed] = built;
            return built;
        }
    }

    /// <summary>Ids in registration order, for an editor dropdown.</summary>
    public static IEnumerable<string> Ids => Factories.Keys;

    /// <summary>Godot enum-property hint listing every registered id.</summary>
    public static string PropertyHint => string.Join(',', Factories.Keys);

    /// <summary>
    /// Builds a node type by id, falling back to raw for an unknown one so a
    /// world saved with a type this build lacks still loads.
    /// </summary>
    public static INodeType Create(string id, int seed, float flowScale, float roughness, float growth)
    {
        if (Factories.TryGetValue(id ?? "raw", out var factory))
            return factory(seed, flowScale, roughness, growth);

        // Reported through Known/IsKnown rather than GD.PushWarning, so the
        // registry stays a plain lookup that tools and tests can call without
        // a live engine.
        return Factories["raw"](seed, flowScale, roughness, growth);
    }

    /// <summary>Whether an id names a type this build can construct.</summary>
    public static bool IsKnown(string id) => id != null && Factories.ContainsKey(id);
}
