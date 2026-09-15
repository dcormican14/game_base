using Godot;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Fills the even-node planet: a solid ball of stone on the icosphere grid.
///
/// The counterpart to <see cref="CubePlanetSource"/>, and deliberately just as
/// plain. There is no density field and no noise, because the point of this
/// world is to show the GRID -- whether nodes come out even, and whether their
/// walls line up -- and relief hides exactly that under its own shading.
/// </summary>
public sealed class IcoPlanetSource
{
    private readonly IcoSphereGrid _grid;
    private readonly IcoSphereTables _tables;

    public IcoPlanetSource(IcoSphereGrid grid)
    {
        _grid = grid;
        _tables = grid.Tables;
    }

    public IcoSphereGrid Grid => _grid;

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

        if (origin.Z + Size <= 0 || origin.Z >= _tables.ShellCount)
            return 0;

        // The face and where this chunk sits on it are constant across the
        // whole chunk, so they are unpacked ONCE rather than per cell.
        _grid.Unpack(new Vector3I(origin.X, origin.Y, 0),
            out int face, out int baseI, out int baseJ, out _);

        if ((uint)face >= IcoSphere.FaceCount)
            return 0;

        int solid = 0;

        for (int ls = 0; ls < Size; ls++)
        {
            int shell = origin.Z + ls;
            if ((uint)shell >= (uint)_tables.ShellCount)
                continue;

            int divisions = _tables.Divisions(shell);

            for (int li = 0; li < Size; li++)
            {
                int i = baseI + li;
                if (i < 0 || i > divisions)
                    continue;

                // How much of this row falls inside the face, as a RANGE rather
                // than a test per cell: the triangle inequality i + j <= divisions
                // bounds j directly, so the loop simply stops there.
                int last = Mathf.Min(Size - 1, divisions - i - baseJ);

                for (int lj = 0; lj <= last; lj++)
                {
                    int j = baseJ + lj;
                    if (j < 0)
                        continue;

                    var cell = new Vector3I(origin.X + li, origin.Y + lj, shell);

                    // Only the address a site is actually stored under is
                    // filled. An edge site has several spellings, and filling
                    // all of them would mesh the same node more than once.
                    //
                    // Canonical is the expensive call here, so it runs last --
                    // and only for the sites on a face's boundary, since an
                    // interior address is its own canonical form and says so
                    // without searching.
                    if (i > 0 && j > 0 && i + j < divisions)
                    {
                        cells[NodeChunkStore.LocalIndex(li, lj, ls)] = (byte)NodeMaterial.Stone;
                        solid++;
                        continue;
                    }

                    if (_grid.Canonical(cell) != cell)
                        continue;

                    cells[NodeChunkStore.LocalIndex(li, lj, ls)] = (byte)NodeMaterial.Stone;
                    solid++;
                }
            }
        }

        return solid;
    }

    /// <summary>
    /// Could this chunk hold anything at all?
    ///
    /// Two tests, both cheap, and together they are what keeps the streamer's
    /// residency honest.
    ///
    /// FIRST, THE SHELLS. A chunk spans one range of shells, and shells outside
    /// 0..ShellCount hold nothing.
    ///
    /// SECOND, THE TRIANGLE. This is the one that matters here. A face's
    /// lattice is a triangle -- i and j non-negative with i + j at most the
    /// subdivision -- but chunks tile the (i, j) RECTANGLE that triangle sits
    /// in, so close to half of every face's chunks lie entirely outside it.
    /// Without this test they are generated, stored, meshed to nothing, and
    /// counted as resident: measured at 832 chunks and 16.8 MB where the cube
    /// planet holds 216 and 2.8 MB.
    ///
    /// A chunk is outside when its NEAREST corner already fails the triangle
    /// inequality, which is one comparison on the minimum i and j.
    /// </summary>
    public bool CouldHoldRock(Vector3I chunk)
    {
        const int Size = NodeChunkStore.ChunkSize;

        Vector3I origin = NodeChunkStore.OriginOf(chunk);

        if (origin.Z + Size <= 0 || origin.Z >= _tables.ShellCount)
            return false;

        _grid.Unpack(new Vector3I(origin.X, origin.Y, 0),
            out int face, out int baseI, out int baseJ, out _);

        if ((uint)face >= IcoSphere.FaceCount)
            return false;

        // Past the far edge of the face's own span.
        if (baseI >= _grid.Stride || baseJ >= _grid.Stride)
            return false;

        // The chunk's lowest corner, clamped into the lattice. The widest the
        // face ever is is its surface subdivision, so a chunk that fails there
        // fails at every shell it spans.
        int lowI = Mathf.Max(baseI, 0);
        int lowJ = Mathf.Max(baseJ, 0);

        return lowI + lowJ <= _tables.SurfaceDivisions;
    }
}
