using Godot;
using GameBase.Density;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Fills chunks of the cubed-sphere grid, where a cell is (u, v, shell).
///
/// The counterpart to <see cref="PlanetChunkSource"/>, which does the same job
/// on a Cartesian lattice. Moving to cell space makes this dramatically
/// simpler, because the coordinate system already knows the planet's shape:
///
///   - a cell's DEPTH is its shell index, not a length to compute;
///   - the surface is shell 0 by definition, so there is no ground radius to
///     evaluate per cell and no cap walk to run;
///   - whether a chunk is sky, crust or core is decided by its shell range
///     alone, without touching a single cell.
///
/// The old source spent most of its time answering "how far below the ground
/// is this point" for every cell of every chunk. Here that is the z coordinate.
/// </summary>
public sealed class SphereChunkSource
{
    private readonly PlanetDensity _field;
    private readonly SphereGrid _grid;

    public SphereChunkSource(PlanetDensity field, SphereGrid grid)
    {
        _field = field;
        _grid = grid;
    }

    /// <summary>The field being sampled.</summary>
    public PlanetDensity Field => _field;

    /// <summary>The grid being filled.</summary>
    public SphereGrid Grid => _grid;

    /// <summary>
    /// How many shells of rock there are, from the surface to the core.
    ///
    /// Shell 0 is the surface, so this is the planet's radius in nodes: a cell
    /// deeper than it lies past the centre and does not exist.
    /// </summary>
    public int ShellCount => Mathf.Max(1, Mathf.RoundToInt(_field.Radius / _grid.NodeSize));

    /// <summary>
    /// Fills `cells` with one chunk's materials and returns how many are solid.
    ///
    /// Returning zero means the chunk holds nothing, which the streamer stores
    /// as a single byte.
    /// </summary>
    public int Generate(Vector3I chunk, byte[] cells)
    {
        System.Array.Fill(cells, NodeChunkStore.Air);

        const int Size = NodeChunkStore.ChunkSize;
        Vector3I origin = NodeChunkStore.OriginOf(chunk);

        // A chunk spans one range of shells, so its classification is decided
        // before any cell is looked at.
        int shellLo = origin.Z;
        int shellHi = origin.Z + Size - 1;

        // ABOVE THE SURFACE, or past the centre: nothing here.
        if (shellHi < 0 || shellLo >= ShellCount)
            return 0;

        int cap = Mathf.Max(0, Mathf.RoundToInt(_field.CapThickness / _grid.NodeSize));

        int solid = 0;

        for (int lu = 0; lu < Size; lu++)
        {
            for (int lv = 0; lv < Size; lv++)
            {
                for (int ls = 0; ls < Size; ls++)
                {
                    int shell = origin.Z + ls;
                    if (shell < 0 || shell >= ShellCount)
                        continue;

                    var cell = new Vector3I(origin.X + lu, origin.Y + lv, shell);

                    // Off the edge of a face: the packed u runs past its face's
                    // resolution where a chunk straddles the fold, and those
                    // addresses name no cell.
                    if (!_grid.Contains(cell))
                        continue;

                    if (!IsSolid(cell, shell))
                        continue;

                    // THE CAP IS THE OUTERMOST SHELLS, exactly.
                    //
                    // On the old lattice this needed a walk outward asking the
                    // density field where the sky was, because "how deep is
                    // this cell" had no cheap answer. Here it is the shell
                    // index, so the topsoil is shells 0..cap-1 by definition
                    // and the surface it caps is level by construction.
                    NodeMaterial material = shell < cap
                        ? NodeMaterial.Dark
                        : NodeMaterial.Raw;

                    cells[NodeChunkStore.LocalIndex(lu, lv, ls)] = (byte)material;
                    solid++;
                }
            }
        }

        return solid;
    }

    /// <summary>
    /// Could this chunk hold anything at all?
    ///
    /// Trivial in cell space: a chunk spans one range of shells, and shells
    /// outside 0..ShellCount hold nothing. The old lattice source needed the
    /// nearest and furthest corners of a box and a comparison against the
    /// planet's maximum radius; here it is two integers.
    /// </summary>
    public bool CouldHoldRock(Vector3I chunk)
    {
        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        int shellLo = origin.Z;
        int shellHi = origin.Z + NodeChunkStore.ChunkSize - 1;

        return shellHi >= 0 && shellLo < ShellCount;
    }

    /// <summary>
    /// Is there rock in this cell?
    ///
    /// The crust is solid outright. Below <see cref="PlanetDensity.SolidDepth"/>
    /// the cavern noise thins it, exactly as before -- but sampled at the
    /// cell's own centre, so the voids follow the shells rather than cutting
    /// across them.
    /// </summary>
    private bool IsSolid(Vector3I cell, int shell)
    {
        float depth = shell * _grid.NodeSize;

        // Solid crust, and the inner core, need no sampling at all.
        if (depth <= _field.SolidDepth)
            return true;

        Vector3 at = _grid.CentreOf(cell);
        return _field.At(at) > 0f;
    }
}
