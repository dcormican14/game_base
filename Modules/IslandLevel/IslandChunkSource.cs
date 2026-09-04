using Godot;
using System.Collections.Generic;
using GameBase.Density;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Turns one chunk of the island density field into node materials.
///
/// The generator's entire job, reduced to something a chunk can do alone. It
/// is handed a chunk coordinate and a byte array, and fills the array — no
/// reference to any other chunk, no shared scratch state beyond what it is
/// given, no dependence on the order chunks are asked for. That independence
/// is what lets the streamer generate chunks in whatever order the player's
/// movement demands, and what will let the work move onto worker threads.
///
/// WHY THE CAP NO LONGER NEEDS A COLUMN
///
/// The old generator walked each column of the world from the ceiling down,
/// counting how far below open sky every cell sat, because that is what the
/// dark capping layer is defined by. Cheap, but it made a cell's material
/// depend on every cell above it — which is exactly the dependency that forced
/// generation into full-height columns and made 3D chunks impossible. A chunk
/// halfway up an island had no way to know whether its top row was buried or
/// exposed without generating everything above it first.
///
/// So depth is measured LOCALLY instead: a cell is capped if the field turns
/// to air within <see cref="IslandDensity.CapNodesFor"/> cells straight up. It
/// asks the field a few times per solid cell rather than reusing a running
/// count, which costs more per cell — but it depends only on cells within a
/// bounded distance, so a chunk can answer for its own cells by looking a
/// little way past its own ceiling. Bounded lookahead instead of unbounded
/// history. That is the whole trade, and it buys 3D chunking.
///
/// It is also more CORRECT than the column walk was. The old counter reset at
/// every gap in a column, so a cave roof got a cap and the underside of an
/// overhang did not, depending on nothing but which cells happened to be
/// stacked above. The local test asks the same question everywhere.
/// </summary>
public sealed class IslandChunkSource
{
    private readonly IslandDensity _field;

    /// <summary>
    /// Scratch reused across cells of a chunk. Per-source rather than static
    /// so two threads generating different chunks never share one; the
    /// streamer owns one source per worker.
    /// </summary>
    private readonly List<Island> _candidates = new();
    private readonly List<Island> _reaching = new();

    public IslandChunkSource(IslandDensity field)
    {
        _field = field;
    }

    /// <summary>The field being sampled.</summary>
    public IslandDensity Field => _field;

    /// <summary>
    /// Fills `cells` with one chunk's materials and returns how many are
    /// solid.
    ///
    /// Returning zero means empty sky, which the streamer stores as a single
    /// byte rather than an array — the case that makes an endless world of
    /// mostly-empty space affordable, and the reason the early-out below
    /// matters so much.
    /// </summary>
    public int Generate(Vector3I chunk, byte[] cells)
    {
        System.Array.Fill(cells, NodeChunkStore.Air);

        const int Size = NodeChunkStore.ChunkSize;
        Vector3I origin = NodeChunkStore.OriginOf(chunk);

        // Which islands could possibly reach this chunk. One placement query
        // for the whole chunk rather than one per column: the lattice of
        // island slots is far coarser than a chunk, so the answer is the same
        // for every cell in it.
        //
        // The box is grown by the cap lookahead, because a cell at the top of
        // the chunk tests cells above it, and those may belong to an island
        // whose rock never enters this chunk at all.
        int lookahead = MaxCapLookahead();
        var min = new Vector3(origin.X + 0.5f, origin.Y + 0.5f, origin.Z + 0.5f);
        var max = new Vector3(
            origin.X + Size - 0.5f,
            origin.Y + Size - 0.5f + lookahead,
            origin.Z + Size - 0.5f);

        _field.Placement.NearBox(min, max, _candidates);

        // No island within reach: the whole chunk is sky. This is the majority
        // of chunks in a world of floating islands, and it costs one placement
        // query.
        if (_candidates.Count == 0)
            return 0;

        int solid = 0;

        for (int lx = 0; lx < Size; lx++)
        {
            for (int lz = 0; lz < Size; lz++)
            {
                float x = origin.X + lx + 0.5f;
                float z = origin.Z + lz + 0.5f;

                // The vertical extent any of these islands could fill at this
                // column. Most columns of a chunk miss every island's
                // footprint entirely, and this skips them for the price of a
                // few multiplies — the same bound the old column walk used,
                // which is where most of its speed came from.
                if (!_field.VerticalSpan(_candidates, x, z,
                        out int spanMin, out int spanMax, _reaching))
                    continue;

                if (_reaching.Count == 0)
                    continue;

                int yLo = Mathf.Max(origin.Y, spanMin);
                int yHi = Mathf.Min(origin.Y + Size - 1, spanMax);
                if (yLo > yHi)
                    continue;

                int capNodes = CapNodesAt(_reaching, x, z);

                for (int y = yLo; y <= yHi; y++)
                {
                    if (_field.At(new Vector3(x, y + 0.5f, z), _reaching) <= 0f)
                        continue;

                    NodeMaterial material = capNodes > 0
                            && DepthBelowSky(x, z, y, capNodes, spanMax) < capNodes
                        ? NodeMaterial.Dark
                        : NodeMaterial.Raw;

                    cells[NodeChunkStore.LocalIndex(lx, y - origin.Y, lz)] = (byte)material;
                    solid++;
                }
            }
        }

        return solid;
    }

    /// <summary>
    /// How many cells of air sit directly above this one, up to `limit`.
    ///
    /// The local replacement for the column walk's running counter. Only
    /// `limit` cells are ever examined — the cap is at most that thick, so a
    /// cell deeper than it is capped-or-not by the same answer either way, and
    /// there is nothing to gain by looking further.
    ///
    /// `spanMax` bounds it further: above the highest rock this column's
    /// islands can reach there is certainly sky, so a cell near the top needs
    /// no samples at all.
    /// </summary>
    private int DepthBelowSky(float x, float z, int y, int limit, int spanMax)
    {
        for (int d = 1; d <= limit; d++)
        {
            int above = y + d;

            // Past every island's reach: guaranteed air, so the cell is d-1
            // below open sky.
            if (above > spanMax)
                return d - 1;

            if (_field.At(new Vector3(x, above + 0.5f, z), _reaching) <= 0f)
                return d - 1;
        }

        // Solid all the way up through the cap's thickness, so this cell is
        // buried whatever lies further above.
        return limit;
    }

    /// <summary>
    /// The cap thickness that applies at a column: the nearest candidate
    /// island's.
    ///
    /// Nearest in the horizontal only — islands stack vertically, and the one
    /// whose rock fills a column is the one it sits under, which vertical
    /// distance would get wrong.
    /// </summary>
    private int CapNodesAt(List<Island> candidates, float x, float z)
    {
        int capNodes = 0;
        float best = float.MaxValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            Island island = candidates[i];
            float dx = x - island.Centre.X;
            float dz = z - island.Centre.Z;

            // Relative to the island's own radius, so a big island a little
            // further off does not lose to a pebble underfoot.
            float distance = (dx * dx + dz * dz) / Mathf.Max(island.Radius * island.Radius, 1f);
            if (distance < best)
            {
                best = distance;
                capNodes = _field.CapNodesFor(island);
            }
        }

        return capNodes;
    }

    /// <summary>
    /// The furthest above a cell the cap test can ever look.
    ///
    /// Used to grow the placement query box, so an island that only clips the
    /// airspace above the chunk is still considered — otherwise the topmost
    /// row of a chunk could see false sky and lay a band of cap through solid
    /// rock at every chunk boundary.
    /// </summary>
    private int MaxCapLookahead()
    {
        if (_field.CapThickness <= 0f)
            return 0;

        // The unscaled thickness is the ceiling: scaling only ever thins it.
        return Mathf.Max(1, Mathf.CeilToInt(_field.CapThickness));
    }
}
