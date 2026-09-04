using Godot;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// The set of filled sub-cells, as a sparse grid of bitsets.
///
/// This answers one question — "is this sub-cell solid?" — for face culling
/// and picking, and it is asked more than anything else in the engine: once
/// per sub-cell in front of every quad the mesher considers, which at region
/// scale is tens of millions of times per build.
///
/// It was a <c>Dictionary&lt;Vector3I, int&gt;</c>, one entry per filled
/// sub-cell holding a reference count. That cost about 32 bytes and roughly a
/// microsecond per insertion, which at 64 sub-cells per node and a million
/// nodes came to gigabytes of dictionary and half a minute of stalls. A bit
/// per sub-cell is 1/256th the memory and measured ten times faster to fill.
///
/// SPARSE, because the world is mostly empty sky: bits live in fixed-size
/// blocks allocated on first write, so an empty region costs nothing and a
/// solid one costs one bit per sub-cell.
///
/// The reference counts are gone deliberately. Nothing ever read them — only
/// membership was ever asked — and they existed so that removing one node
/// would not clear sub-cells a neighbour still filled. Callers handle that by
/// re-stamping the affected neighbourhood after an edit, which is a couple of
/// dozen nodes rather than a per-sub-cell tally over the whole world.
/// </summary>
public sealed class SubCellSet
{
    /// <summary>
    /// Sub-cells per block edge. 32 gives 32768 bits — a 4 KB block — which is
    /// large enough that the dictionary of blocks stays small and small enough
    /// that a sparse world does not pay for sky it never fills.
    /// </summary>
    private const int BlockSize = 32;
    private const int BlockMask = BlockSize - 1;
    private const int BlockShift = 5;

    /// <summary>Words of 64 bits per block.</summary>
    private const int WordsPerBlock = BlockSize * BlockSize * BlockSize / 64;

    private readonly Dictionary<Vector3I, ulong[]> _blocks = new();

    /// <summary>Forgets every sub-cell.</summary>
    public void Clear() => _blocks.Clear();

    /// <summary>Blocks currently allocated — a rough measure of how much of
    /// the world holds rock.</summary>
    public int BlockCount => _blocks.Count;

    /// <summary>Marks a sub-cell filled.</summary>
    public void Add(int x, int y, int z)
    {
        Vector3I block = BlockOf(x, y, z);
        if (!_blocks.TryGetValue(block, out ulong[] words))
        {
            words = new ulong[WordsPerBlock];
            _blocks[block] = words;
        }

        int bit = BitIn(x, y, z);
        words[bit >> 6] |= 1UL << (bit & 63);
    }

    /// <summary>Marks a sub-cell empty. The block is kept even if it empties —
    /// a cleared block is usually about to be refilled by the re-stamp that
    /// follows an edit.</summary>
    public void Remove(int x, int y, int z)
    {
        if (!_blocks.TryGetValue(BlockOf(x, y, z), out ulong[] words))
            return;

        int bit = BitIn(x, y, z);
        words[bit >> 6] &= ~(1UL << (bit & 63));
    }

    /// <summary>Is this sub-cell filled?</summary>
    public bool Contains(int x, int y, int z)
    {
        if (!_blocks.TryGetValue(BlockOf(x, y, z), out ulong[] words))
            return false;

        int bit = BitIn(x, y, z);
        return (words[bit >> 6] & (1UL << (bit & 63))) != 0UL;
    }

    /// <summary>Which block a sub-cell lives in. Arithmetic shift, so negative
    /// coordinates floor rather than truncating toward zero — otherwise the
    /// blocks either side of the origin would overlap.</summary>
    private static Vector3I BlockOf(int x, int y, int z) =>
        new(x >> BlockShift, y >> BlockShift, z >> BlockShift);

    /// <summary>The bit index within a block. The mask handles negatives
    /// correctly because it keeps only the low bits.</summary>
    private static int BitIn(int x, int y, int z) =>
        ((x & BlockMask) << (BlockShift * 2)) | ((y & BlockMask) << BlockShift) | (z & BlockMask);
}
