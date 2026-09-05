using Godot;
using GameBase.Density;

namespace GameBase.Nodes;

/// <summary>
/// Topsoil: the ground layer between rock and open sky.
///
/// Its shape is decided entirely by which of its six neighbours hold ground —
/// bevel the top edge where a side faces air, step up a quarter cell where a
/// side faces more soil. See <see cref="TopsoilGeometry"/> for the rule; this
/// class is only the <see cref="INodeType"/> face of it.
///
/// WHY NEIGHBOURS AND NOT A FIELD
///
/// Every other node type in this world is shaped by a function of its own
/// position, and that is what makes planet-scale generation affordable — a
/// node can be shaped without anyone looking around it. Topsoil is the one
/// type that cannot work that way, because it is not shaping ROCK, it is
/// shaping a SURFACE, and where the surface lies is the generator's decision
/// rather than something a node can independently evaluate.
///
/// A previous version tried anyway, sampling a smooth field of its own. The
/// field and the generator disagreed about where the ground was, and soil the
/// generator had placed came out as slivers: 387 of 2098 boundary nodes with
/// fewer than eight of their sixty-four sub-cells filled.
///
/// Taking the neighbour mask instead costs nothing. The mesher already reads
/// those six cells to cull buried faces, so it passes on what it has; nothing
/// searches the world, the shape is still a pure function of its inputs, and
/// with only 64 possible masks the geometry is a fixed table.
/// </summary>
public sealed class TopsoilNode : INodeType
{
    private readonly int _seed;

    /// <param name="seed">Picks which relief pattern each cell wears, so two
    /// worlds with different seeds dimple their flat ground differently. The
    /// bevel and step owe nothing to it.</param>
    public TopsoilNode(int seed = 0)
    {
        _seed = seed;
    }

    public string Id => "topsoil";

    public int Subdivision => RawNodeGeometry.Sub;

    /// <summary>
    /// Without a neighbour mask there is nothing to go on, so this assumes
    /// open sky on every side — the shape a lone node placed in mid-air should
    /// have. The mesher always calls the overload below.
    /// </summary>
    public NodeShape ShapeAt(Vector3I cell) => new(0, ReliefAt(cell));

    public NodeShape ShapeAt(Vector3I cell, int neighbours) =>
        new(neighbours & 63, ReliefAt(cell));

    /// <summary>
    /// Which relief pattern this cell wears.
    ///
    /// Hashed from the position, so it is fixed for a cell, varies from one
    /// cell to the next, and costs one multiply-and-shift with nothing looked
    /// up. This is the only part of a topsoil node's shape that comes from
    /// where it IS rather than from what is around it — and it has to, because
    /// every node in the middle of a plateau has identical neighbours and
    /// would otherwise take an identical shape.
    /// </summary>
    private int ReliefAt(Vector3I cell) =>
        (int)(DensityNoise.Hash(cell.X, cell.Y, cell.Z, _seed) % TopsoilGeometry.Variants);

    public NodeMesh MeshFor(NodeShape shape) => TopsoilGeometry.Get(ToMask(shape));

    public int[] OccupiedCells(NodeShape shape) => TopsoilGeometry.OccupiedCells(ToMask(shape));

    public bool Occupies(NodeShape shape, int i, int j, int k) =>
        TopsoilGeometry.Occupies(ToMask(shape), i, j, k);

    private static TopsoilGeometry.Mask ToMask(NodeShape shape) => new(shape.A, shape.B);
}
