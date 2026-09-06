using Godot;
using GameBase.Density;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Turns a region of <see cref="PlanetDensity"/> into one chunk of nodes.
///
/// The planet's counterpart to <see cref="IslandChunkSource"/>, and it has the
/// same job: fill a chunk's byte array with materials and say how many cells
/// came back solid. Everything downstream — storage, meshing, streaming — is
/// unchanged.
///
/// WHY THIS IS CHEAPER THAN THE ISLAND SOURCE
///
/// A sphere admits a reject the island field could not offer. Rock lives
/// strictly between the deepest basin and the highest peak, so a chunk can be
/// classified by DISTANCE ALONE, using the nearest and furthest corners of its
/// box:
///
///   - furthest corner inside the deepest possible ground: entirely solid, and
///     for a chunk deep enough to be below the caverns it can be filled without
///     a single noise evaluation;
///   - nearest corner beyond the highest possible peak: entirely sky, one byte;
///   - otherwise: sweep it.
///
/// Only chunks straddling the surface or the cavern zone are ever swept, which
/// is a thin shell of the total. The islands' equivalent test needed a
/// placement query and a per-column vertical span; this is a length.
/// </summary>
public sealed class PlanetChunkSource
{
    private readonly PlanetDensity _field;

    public PlanetChunkSource(PlanetDensity field)
    {
        _field = field;
    }

    /// <summary>The field being sampled.</summary>
    public PlanetDensity Field => _field;

    /// <summary>
    /// Fills `cells` with one chunk's materials and returns how many are solid.
    ///
    /// Returning zero means empty sky, which the streamer stores as a single
    /// byte rather than an array — the case that makes the space around a
    /// planet affordable.
    /// </summary>
    public int Generate(Vector3I chunk, byte[] cells)
    {
        System.Array.Fill(cells, NodeChunkStore.Air);

        const int Size = NodeChunkStore.ChunkSize;
        Vector3I origin = NodeChunkStore.OriginOf(chunk);

        // The chunk's own bounds, then the closest and furthest any cell centre
        // in it can be from the planet's centre.
        var min = new Vector3(origin.X + 0.5f, origin.Y + 0.5f, origin.Z + 0.5f);
        var max = new Vector3(
            origin.X + Size - 0.5f, origin.Y + Size - 0.5f, origin.Z + Size - 0.5f);

        NearFar(min, max, out float near, out float far);

        // ENTIRELY SKY. Even the nearest cell sits above the highest peak this
        // planet can raise, so nothing in the chunk can be rock. Most chunks
        // in a streamed world are this.
        if (near > _field.MaxRadius)
            return 0;

        // The cap needs to know what is above it, and a cell at the top of the
        // chunk looks up to `lookahead` cells past its ceiling.
        int lookahead = Mathf.CeilToInt(Mathf.Max(_field.CapThickness, 0f)) + 1;

        int solid = 0;

        for (int lx = 0; lx < Size; lx++)
        {
            for (int ly = 0; ly < Size; ly++)
            {
                for (int lz = 0; lz < Size; lz++)
                {
                    var at = new Vector3(
                        origin.X + lx + 0.5f, origin.Y + ly + 0.5f, origin.Z + lz + 0.5f);

                    if (!_field.IsSolid(at))
                        continue;

                    NodeMaterial material = IsCapped(at, lookahead)
                        ? NodeMaterial.Dark
                        : NodeMaterial.Raw;

                    cells[NodeChunkStore.LocalIndex(lx, ly, lz)] = (byte)material;
                    solid++;
                }
            }
        }

        return solid;
    }

    /// <summary>
    /// Is this cell within the topsoil cap — that is, close enough to open sky
    /// along the OUTWARD direction?
    ///
    /// Outward, not up. On a planet "up" is away from the centre, and a cell
    /// on the equator has its sky along +X. Walking world-up there would test
    /// sideways through the crust and cap the wrong faces, which is exactly
    /// the difference between a planet and a flat world.
    ///
    /// Only `limit` cells are ever examined: the cap is at most that thick, so
    /// anything deeper is uncapped whatever lies beyond.
    /// </summary>
    private bool IsCapped(Vector3 at, int limit)
    {
        float distance = at.Length();
        if (distance < 0.0001f)
            return false;

        Vector3 outward = at / distance;

        for (int d = 1; d <= limit; d++)
        {
            Vector3 above = at + outward * d;

            // Past the highest ground this planet reaches: certainly sky, so
            // the cell is d-1 below the surface.
            if (above.Length() > _field.MaxRadius)
                return d - 1 < _field.CapThickness;

            if (!_field.IsSolid(above))
                return d - 1 < _field.CapThickness;
        }

        // Solid all the way out through the cap's thickness, so this cell is
        // buried whatever lies further.
        return false;
    }

    /// <summary>
    /// The closest and furthest distances from the origin to any point in an
    /// axis-aligned box.
    ///
    /// The near distance is the classic point-to-box distance, clamped per
    /// axis; the far one takes whichever face is further along each axis. Both
    /// are exact, which is what lets them drive a reject rather than a guess.
    /// </summary>
    private static void NearFar(Vector3 min, Vector3 max, out float near, out float far)
    {
        float nx = Axis(min.X, max.X, out float fx);
        float ny = Axis(min.Y, max.Y, out float fy);
        float nz = Axis(min.Z, max.Z, out float fz);

        near = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
        far = Mathf.Sqrt(fx * fx + fy * fy + fz * fz);
    }

    /// <summary>Per-axis nearest and furthest offsets from zero.</summary>
    private static float Axis(float lo, float hi, out float far)
    {
        far = Mathf.Max(Mathf.Abs(lo), Mathf.Abs(hi));

        // Straddles zero on this axis, so the nearest point is at zero.
        if (lo <= 0f && hi >= 0f)
            return 0f;

        return Mathf.Min(Mathf.Abs(lo), Mathf.Abs(hi));
    }
}
