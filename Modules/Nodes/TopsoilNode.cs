using Godot;
using System.Collections.Generic;

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
/// It also yields its floor to the crystal beneath it. A raw rim grows UP out
/// of the node below into this one's bottom row, and soil filling its own
/// footprint regardless put two materials in the same sub-cell — measured at
/// 1884 of them over a small block of soil on rock — each drawing a face there
/// and z-fighting with the other. The rock keeps that space, so the crystal
/// shows through and the soil breaks around it.
///
/// Taking the neighbour mask instead costs nothing. The mesher already reads
/// those six cells to cull buried faces, so it passes on what it has; nothing
/// searches the world, the shape is still a pure function of its inputs, and
/// the geometry is a fixed table — one entry per neighbour mask.
/// </summary>
public sealed class TopsoilNode : INodeType
{
    /// <summary>
    /// The crystal field, used only to work out what the rock BELOW a soil
    /// node looks like — specifically whether its rims grow up into the soil's
    /// floor, which the soil then leaves alone.
    /// </summary>
    private readonly RawNodeField _raw;

    /// <param name="seed">Same seed, same world. The bevel and the rise owe
    /// nothing to it; it decides only where the crystal beneath pushes up.</param>
    /// <param name="terrain">The world's terrain field, shared with the rock
    /// so both decide the contests beneath them the same way.</param>
    public TopsoilNode(int seed = 0, TerrainField terrain = null)
    {
        _raw = new RawNodeField(seed, terrain: terrain ?? new TerrainField(seed),
            terrainBias: TerrainField.ContestBias);
    }

    public string Id => "topsoil";

    public int Subdivision => RawNodeGeometry.Sub;

    /// <summary>
    /// Without a neighbour mask there is nothing to go on, so this assumes
    /// open sky on every side — the shape a lone node placed in mid-air should
    /// have. The mesher always calls the overload below.
    /// </summary>
    public NodeShape ShapeAt(Vector3I cell) => ShapeAt(cell, 0);

    public NodeShape ShapeAt(Vector3I cell, int neighbours)
    {
        return new NodeShape(
            neighbours & (TopsoilGeometry.Masks - 1),
            InternTaken(FloorTaken(cell), FloorTaken(cell + Vector3I.Up)));
    }

    /// <summary>
    /// Which sub-cells of this node the surrounding crystal already fills.
    ///
    /// All 26 neighbours are consulted. A rim reaches in from whichever
    /// direction won the feature, and arbitrating against fewer left sub-cells
    /// contested every time: 1059 against the cell directly below alone, 255
    /// against the nine below.
    ///
    /// Nothing is read from the world. Each mask is recomputed from its cell
    /// position, which is the same pure function the rock down there runs, so
    /// both sides agree about who owns the space without either having asked
    /// what is actually placed.
    /// </summary>
    private ulong FloorTaken(Vector3I cell)
    {
        const int sub = RawNodeGeometry.Sub;
        ulong taken = 0UL;

        // All 26 surrounding cells, not just the nine below.
        //
        // A rim reaches in from whatever direction won the feature: upward out
        // of the rock beneath, sideways out of the rock alongside, diagonally
        // out of a corner. Arbitrating against only the cells below left the
        // floor clean but still collided 255 times on a slope, where soil and
        // rock sit side by side at the same height.
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0)
                        continue;

                    var at = new Vector3I(cell.X + dx, cell.Y + dy, cell.Z + dz);
                    RawNodeGeometry.Mask mask = _raw.MaskFor(at);

                    for (int i = 0; i < sub; i++)
                        for (int j = 0; j < sub; j++)
                            for (int k = 0; k < sub; k++)
                            {
                                // This cell in that neighbour own frame.
                                if (RawNodeGeometry.Occupies(mask,
                                        i - dx * sub, j - dy * sub, k - dz * sub))
                                    taken |= 1UL << TopsoilGeometry.CellBit(i, j, k);
                            }
                }
            }
        }

        return taken;
    }

    public NodeMesh MeshFor(NodeShape shape) => TopsoilGeometry.Get(ToMask(shape));

    public int[] OccupiedCells(NodeShape shape) => TopsoilGeometry.OccupiedCells(ToMask(shape));

    public bool Occupies(NodeShape shape, int i, int j, int k) =>
        TopsoilGeometry.Occupies(ToMask(shape), i, j, k);

    // The taken-mask is 64 bits and will not fit in a NodeShape int, so
    // distinct ones are interned and the handle carries an index. Equal
    // handles still mean identical geometry, which is what the per-shape mesh
    // cache requires; the table stays small because a world reuses far fewer
    // distinct rim patterns than it has nodes.
    private readonly Dictionary<(ulong, ulong), int> _takenIds = new();
    private readonly List<(ulong Own, ulong Above)> _taken = new();
    private readonly object _takenLock = new();

    private int InternTaken(ulong taken, ulong above)
    {
        lock (_takenLock)
        {
            if (_takenIds.TryGetValue((taken, above), out int existing))
                return existing;

            int id = _taken.Count;
            _taken.Add((taken, above));
            _takenIds[(taken, above)] = id;
            return id;
        }
    }

    private TopsoilGeometry.Mask ToMask(NodeShape shape)
    {
        lock (_takenLock)
        {
            (ulong own, ulong above) = _taken[shape.B];
            return new TopsoilGeometry.Mask(shape.A, own, above);
        }
    }
}
