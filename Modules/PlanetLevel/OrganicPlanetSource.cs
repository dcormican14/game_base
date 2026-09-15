using Godot;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Fills the organic planet: solid rock everywhere inside the ball.
///
/// The counterpart to <see cref="CubePlanetSource"/> and
/// <see cref="IcoPlanetSource"/>, and the simplest of the three. There are no
/// shells and no faces to clip against -- a cell is rock when its site lies
/// inside the planet -- so filling a chunk is one test per cell with nothing to
/// unpack first.
/// </summary>
public sealed class OrganicPlanetSource
{
    private readonly OrganicGrid _grid;

    public OrganicPlanetSource(OrganicGrid grid)
    {
        _grid = grid;
    }

    /// <summary>What this planet's skin is made of.</summary>
    private readonly NodeType _skin = NodeTypes.Of(NodeMaterial.Soil);

    /// <summary>What its body is made of.</summary>
    private readonly NodeType _body = NodeTypes.Of(NodeMaterial.Stone);

    public OrganicGrid Grid => _grid;

    /// <summary>Fills one chunk with stone and returns how many cells are
    /// solid.</summary>
    public int Generate(Vector3I chunk, byte[] cells)
    {
        System.Array.Fill(cells, NodeChunkStore.Air);

        const int Size = NodeChunkStore.ChunkSize;
        Vector3I origin = NodeChunkStore.OriginOf(chunk);

        if (!CouldHoldRock(chunk))
            return 0;

        int solid = 0;

        for (int lx = 0; lx < Size; lx++)
        {
            for (int ly = 0; ly < Size; ly++)
            {
                for (int lz = 0; lz < Size; lz++)
                {
                    var cell = new Vector3I(origin.X + lx, origin.Y + ly, origin.Z + lz);

                    // IsGround, not Contains: the world now extends well past
                    // the planet so there is somewhere to build, and filling
                    // all of that with rock would bury the sky.
                    if (!_grid.IsGround(cell))
                        continue;

                    // The planet's skin is soil, everything under it is rock.
                    // Which one a node is comes from where its SITE sits, so a
                    // node is wholly one or the other and the boundary between
                    // them is a face the Voronoi diagram already draws.
                    //
                    // The KINDS come from the node type table rather than being
                    // named here, so a planet that wants ice or clay for its
                    // skin changes which type it asks for and nothing else.
                    NodeType kind = _grid.IsSurfaceNode(cell) ? _skin : _body;

                    cells[NodeChunkStore.LocalIndex(lx, ly, lz)] = (byte)kind.Material;
                    solid++;
                }
            }
        }

        return solid;
    }

    /// <summary>
    /// Could this chunk hold anything at all?
    ///
    /// The chunk is a box in the same space the planet is a ball in, so this is
    /// a box-versus-sphere test: the nearest point of the box to the centre,
    /// against the radius. Rejecting a chunk here saves generating, storing and
    /// meshing it, and most of the residency ball around a surface player is
    /// open sky.
    /// </summary>
    public bool CouldHoldRock(Vector3I chunk)
    {
        const int Size = NodeChunkStore.ChunkSize;

        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        float node = _grid.NodeSize;

        // The box in world units, grown by the jitter since a site may sit up
        // to half a cell outside its own lattice point.
        float slack = node * (0.5f + _grid.Jitter);

        float lowX = origin.X * node - slack;
        float lowY = origin.Y * node - slack;
        float lowZ = origin.Z * node - slack;

        float highX = (origin.X + Size - 1) * node + slack;
        float highY = (origin.Y + Size - 1) * node + slack;
        float highZ = (origin.Z + Size - 1) * node + slack;

        // Nearest point of the box to the planet's centre.
        float nearX = Mathf.Max(lowX, Mathf.Min(0f, highX));
        float nearY = Mathf.Max(lowY, Mathf.Min(0f, highY));
        float nearZ = Mathf.Max(lowZ, Mathf.Min(0f, highZ));

        float distance = nearX * nearX + nearY * nearY + nearZ * nearZ;
        float radius = _grid.SurfaceRadius;

        return distance <= radius * radius;
    }
}
