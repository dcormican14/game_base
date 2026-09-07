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

        // ENTIRELY SOLID CRUST, with no noise evaluated at all.
        //
        // A chunk whose furthest corner is still inside the shallowest ground
        // the planet can have is completely underground, and if its nearest
        // corner is also above the depth where hollowing starts then nothing
        // in it can be anything but solid rock. That is the whole mantle
        // above the caverns, which is a large share of the chunks a player
        // digging down will ask for -- and it costs two lengths instead of
        // 32768 noise evaluations.
        float shallowest = _field.Radius - _field.TerrainHeight;
        if (far < shallowest && _field.Radius - near < _field.SolidDepth)
        {
            System.Array.Fill(cells, (byte)NodeMaterial.Raw);
            return cells.Length;
        }

        // How many cells the cap walk steps outward.
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

                    float distance = at.Length();
                    if (distance < 0.0001f)
                        continue;

                    // ONE ground-radius evaluation per cell, reused for both
                    // the density test and the cap.
                    //
                    // GroundRadius is a domain warp plus several octaves of
                    // noise and depends only on DIRECTION, so the old code
                    // paid for it up to five times per cell -- once inside
                    // IsSolid and once per step of the cap walk -- for what is
                    // the same answer along a ray. Measured, that was most of
                    // the 144ms a chunk cost to generate.
                    float ground = _field.GroundRadius(at / distance);

                    if (_field.AtWithGround(at, distance, ground) <= 0f)
                        continue;

                    // The cap WALKS outward, unchanged.
                    //
                    // Two shortcuts were tried and both were wrong. Deciding
                    // it from radial depth alone missed the topsoil on steep
                    // faces, where a cell sits well below its own ground
                    // radius and still has air a step sideways-and-out.
                    // Skipping the walk for deep cells missed it again: "air"
                    // to this test is not only sky, it is also a CAVERN, and
                    // the hollow core is full of them. Both left 93 cells of
                    // bare rock that should have been soil.
                    //
                    // The saving comes from the ground radius above being
                    // computed once instead of once per step, not from
                    // walking less.
                    NodeMaterial material = IsCapped(at, distance, ground, lookahead)
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
    /// Is this cell within the topsoil cap � close enough to open sky along
    /// the OUTWARD direction?
    ///
    /// Outward, not up. On a planet "up" is away from the centre, and a cell
    /// on the equator has its sky along +X. Walking world-up there would test
    /// sideways through the crust and cap the wrong faces.
    ///
    /// Only called for cells already known to be near the surface, so the
    /// steps it takes are paid on a thin shell rather than on every cell of
    /// the chunk.
    /// </summary>
    private bool IsCapped(Vector3 at, float distance, float ground, int limit)
    {
        Vector3 outward = at / distance;

        for (int d = 1; d <= limit; d++)
        {
            Vector3 above = at + outward * d;
            float aboveDistance = distance + d;

            // Past the highest ground this planet reaches: certainly sky, so
            // the cell is d-1 below the surface.
            if (aboveDistance > _field.MaxRadius)
                return d - 1 < _field.CapThickness;

            // The ground radius is the same along this ray, so the sample
            // reuses the caller's rather than recomputing the warp and noise
            // that produced it.
            if (_field.AtWithGround(above, aboveDistance, ground) <= 0f)
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
