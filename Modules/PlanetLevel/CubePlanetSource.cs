using Godot;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Fills the cube planet: a solid ball of stone on the quad sphere.
///
/// There is no density field and no noise. The point of this world is that it
/// reads as a lattice of BLOCKS, and relief works against that twice -- noise
/// on the surface hides the checkerboard under its own shading, and caverns
/// underneath mean a mined block can open onto a void rather than onto more
/// blocks. So every cell from the surface to the core holds stone, and
/// everything the player sees comes from the GRID: the arc of each shell, the
/// checkerboard, the way resolution halves with depth.
///
/// That also makes the source nearly free. With nothing to evaluate per cell, a
/// chunk is filled at memory speed and the streamer's cost is meshing alone.
/// </summary>
public sealed class CubePlanetSource
{
    private readonly QuadSphereGrid _grid;
    private readonly QuadSphereTables _tables;

    public CubePlanetSource(QuadSphereGrid grid)
    {
        _grid = grid;
        _tables = grid.Tables;
    }

    public QuadSphereGrid Grid => _grid;

    /// <summary>Shells of rock from the surface to the solid core.</summary>
    public int ShellCount => _tables.ShellCount;

    /// <summary>
    /// Fills one chunk with stone and returns how many cells are solid.
    ///
    /// Zero means the chunk holds nothing, which the streamer stores as a
    /// single byte rather than an array.
    /// </summary>
    public int Generate(Vector3I chunk, byte[] cells)
    {
        System.Array.Fill(cells, NodeChunkStore.Air);

        const int Size = NodeChunkStore.ChunkSize;
        Vector3I origin = NodeChunkStore.OriginOf(chunk);

        // A chunk spans one range of shells, so whether it can hold anything is
        // settled before a single cell is touched.
        if (origin.Z + Size <= 0 || origin.Z >= _tables.ShellCount)
            return 0;

        // The face this chunk lies on, and where it sits on that face. Both are
        // constant across the chunk, so they are unpacked ONCE rather than per
        // cell -- which is what turns the inner loop into two comparisons and a
        // store.
        _grid.Unpack(new Vector3I(origin.X, origin.Y, 0),
            out int face, out int baseU, out int baseV, out _);

        if ((uint)face >= NodeOrientation.Count)
            return 0;

        int solid = 0;

        for (int ls = 0; ls < Size; ls++)
        {
            int shell = origin.Z + ls;
            if ((uint)shell >= (uint)_tables.ShellCount)
                continue;

            // The shell's face resolution bounds u and v. Read once per shell.
            int resolution = _tables.Resolution(shell);

            // How much of this chunk falls inside the face, as a range rather
            // than a test per cell. A chunk near a fold is partly outside the
            // face; clipping the loop is cheaper than rejecting cell by cell.
            int uEnd = Mathf.Min(Size, resolution - baseU);
            int vEnd = Mathf.Min(Size, resolution - baseV);

            if (uEnd <= 0 || vEnd <= 0)
                continue;

            for (int lu = 0; lu < uEnd; lu++)
            {
                for (int lv = 0; lv < vEnd; lv++)
                {
                    cells[NodeChunkStore.LocalIndex(lu, lv, ls)] = (byte)NodeMaterial.Stone;
                    solid++;
                }
            }
        }

        return solid;
    }

    /// <summary>
    /// Could this chunk hold anything at all?
    ///
    /// Two integers: a chunk spans one range of shells, and shells outside
    /// 0..ShellCount hold nothing. This is what lets the view radius reach far
    /// enough to see the planet from a distance without the residency scan
    /// walking the sky.
    /// </summary>
    public bool CouldHoldRock(Vector3I chunk)
    {
        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        return origin.Z + NodeChunkStore.ChunkSize > 0 && origin.Z < _tables.ShellCount;
    }
}
