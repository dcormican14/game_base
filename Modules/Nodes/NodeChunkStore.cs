using Godot;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// The world's node data, owned one chunk at a time.
///
/// This replaces the single <c>Dictionary&lt;Vector3I, NodeMaterial&gt;</c>
/// that used to hold every node in the world. That dictionary was what capped
/// the world's size: at roughly 32 bytes of bucket, key and value per node it
/// cost about 2 GB at a million nodes, which is why the region radius had to
/// be chosen small enough to fit in RAM rather than large enough to be
/// interesting.
///
/// WHY THIS IS SO MUCH SMALLER
///
/// A chunk is a dense byte array: one material per cell, no key, no hash
/// bucket, no per-entry overhead. At <see cref="ChunkSize"/> 32 that is 32 KB
/// for 32768 cells — about 1/32nd of what the dictionary charged for the same
/// nodes, and the saving grows with how full the chunk is.
///
/// Better still, most chunks are not mixed. A world of floating islands is
/// overwhelmingly empty sky, and the inside of an island is overwhelmingly
/// solid rock. Both are UNIFORM: every cell the same material (or all air), so
/// the chunk stores one byte and no array at all. The array is allocated
/// lazily, on the first write that actually breaks uniformity, and a chunk
/// that is filled solid and then never carved never allocates one.
///
/// WHY IT IS A STORE AND NOT JUST A DICTIONARY OF ARRAYS
///
/// Because the mesher's queries do not respect chunk boundaries. Face culling
/// asks about cells up to two steps outside the chunk being meshed, so every
/// read has to resolve "which chunk holds this cell" and cope with the answer
/// being a chunk that is not loaded. Putting that lookup here — with a
/// one-entry cache for the overwhelmingly common case of consecutive reads
/// landing in the same chunk — keeps it off every call site.
///
/// THREADING
///
/// Reads are safe from any thread once the chunks they touch are loaded and no
/// writer is running: a uniform chunk is an immutable byte, and a mixed chunk
/// is an array nobody resizes. Writes are main-thread only, exactly as before,
/// because they can allocate a chunk and rehash the dictionary. The one shared
/// mutable thing is <see cref="_lastChunk"/>, the read cache, which is
/// <c>[ThreadStatic]</c> so worker threads cannot tear each other's.
/// </summary>
public sealed class NodeChunkStore
{
    /// <summary>
    /// Cells per chunk edge.
    ///
    /// 32 rather than the 8 the old mesher used. Eight made sense when a chunk
    /// was only a meshing unit and an edit had to re-mesh the 3x3x3 around
    /// itself; it makes no sense as a STREAMING unit, where every chunk is a
    /// dictionary entry, a scene node, a mesh, and a collision shape, and the
    /// per-chunk overhead is what dominates. At 32 a given volume needs 1/64th
    /// as many chunks, and a chunk still meshes fast enough to fit in a frame
    /// budget.
    /// </summary>
    public const int ChunkSize = 32;

    /// <summary>Cells in one chunk.</summary>
    public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

    /// <summary>Shift and mask for the power-of-two chunk size, so the hot
    /// path divides and modulos with bit operations.</summary>
    private const int ChunkShift = 5;
    private const int ChunkMask = ChunkSize - 1;

    /// <summary>
    /// The material stored in an empty cell.
    ///
    /// Air needs its own value distinct from every real material, because a
    /// dense array has an entry for every cell whether or not a node is there
    /// — unlike the dictionary, where absence was the encoding. It is 255
    /// rather than 0 so that <see cref="NodeMaterial.Raw"/> keeps id 0 and
    /// existing save data stays valid.
    /// </summary>
    public const byte Air = 255;

    /// <summary>
    /// One chunk's cells.
    ///
    /// Either UNIFORM — every cell is <see cref="Uniform"/> and
    /// <see cref="Cells"/> is null — or MIXED, where Cells holds one byte per
    /// cell. The uniform form is what makes an endless world affordable: sky
    /// and deep rock both cost a few bytes per chunk instead of 32 KB.
    /// </summary>
    public sealed class Chunk
    {
        /// <summary>The material every cell holds while <see cref="Cells"/> is
        /// null. <see cref="Air"/> for an empty chunk.</summary>
        public byte Uniform;

        /// <summary>One material per cell, or null while the chunk is
        /// uniform. Indexed <c>(x &lt;&lt; 10) | (y &lt;&lt; 5) | z</c> in
        /// chunk-local coordinates.</summary>
        public byte[] Cells;

        /// <summary>How many cells hold something other than air. Kept as a
        /// running count so "is this chunk worth meshing" and "may this chunk
        /// be dropped" are answered without walking 32768 cells.</summary>
        public int SolidCount;

        /// <summary>True when this chunk holds no nodes at all — the common
        /// case for open sky, and the cheapest possible thing to mesh.</summary>
        public bool IsEmpty => Cells == null && Uniform == Air;

        /// <summary>Reads one cell, in chunk-local coordinates.</summary>
        public byte Get(int index) => Cells == null ? Uniform : Cells[index];

        /// <summary>
        /// Breaks uniformity, allocating the dense array and filling it with
        /// whatever the chunk uniformly held.
        ///
        /// Called on the first write that disagrees with the uniform value —
        /// so a chunk generated solid and never edited never pays for this,
        /// and a chunk of sky pays only when something is built in it.
        /// </summary>
        public void Materialise()
        {
            if (Cells != null)
                return;

            Cells = new byte[ChunkVolume];
            if (Uniform != 0)
                System.Array.Fill(Cells, Uniform);
        }
    }

    private readonly Dictionary<Vector3I, Chunk> _chunks = new();

    /// <summary>
    /// The last chunk looked up, cached per thread.
    ///
    /// Every read the mesher makes is part of a walk through neighbouring
    /// cells, so consecutive lookups land in the same chunk the overwhelming
    /// majority of the time and this turns a dictionary probe into a compare.
    /// ThreadStatic because worker threads mesh different chunks at once and
    /// would otherwise fight over one slot.
    /// </summary>
    [System.ThreadStatic] private static Vector3I _lastCoord;
    [System.ThreadStatic] private static Chunk _lastChunk;
    [System.ThreadStatic] private static NodeChunkStore _lastStore;

    /// <summary>Chunks currently held.</summary>
    public int ChunkCount => _chunks.Count;

    /// <summary>Every loaded chunk, for iteration by the streamer.</summary>
    public IReadOnlyDictionary<Vector3I, Chunk> Chunks => _chunks;

    /// <summary>Total nodes across all loaded chunks.</summary>
    public int NodeCount
    {
        get
        {
            int total = 0;
            foreach (var kv in _chunks)
                total += kv.Value.SolidCount;
            return total;
        }
    }

    /// <summary>The chunk a cell belongs to. Arithmetic shift, so negative
    /// coordinates floor rather than truncating toward zero — otherwise the
    /// chunks either side of the origin would overlap.</summary>
    public static Vector3I ChunkOf(Vector3I cell) =>
        new(cell.X >> ChunkShift, cell.Y >> ChunkShift, cell.Z >> ChunkShift);

    /// <summary>The min corner of a chunk, in cells.</summary>
    public static Vector3I OriginOf(Vector3I chunk) =>
        new(chunk.X << ChunkShift, chunk.Y << ChunkShift, chunk.Z << ChunkShift);

    /// <summary>The index of a cell within its chunk. The mask handles
    /// negatives correctly because it keeps only the low bits.</summary>
    public static int IndexOf(Vector3I cell) =>
        ((cell.X & ChunkMask) << (ChunkShift * 2))
        | ((cell.Y & ChunkMask) << ChunkShift)
        | (cell.Z & ChunkMask);

    /// <summary>The index of chunk-local coordinates already known to be in
    /// range.</summary>
    public static int LocalIndex(int x, int y, int z) =>
        (x << (ChunkShift * 2)) | (y << ChunkShift) | z;

    /// <summary>The chunk at a coordinate, or null if it is not loaded.</summary>
    public Chunk Find(Vector3I chunk)
    {
        // The cache is keyed by store as well as coordinate: a thread that
        // meshes one world and then another must not answer from the first
        // world's chunk.
        if (_lastStore == this && _lastChunk != null && _lastCoord == chunk)
            return _lastChunk;

        _chunks.TryGetValue(chunk, out Chunk found);
        if (found != null)
        {
            _lastStore = this;
            _lastCoord = chunk;
            _lastChunk = found;
        }

        return found;
    }

    /// <summary>The chunk at a coordinate, created empty if absent.</summary>
    public Chunk GetOrCreate(Vector3I chunk)
    {
        Chunk found = Find(chunk);
        if (found != null)
            return found;

        found = new Chunk { Uniform = Air };
        _chunks[chunk] = found;

        _lastStore = this;
        _lastCoord = chunk;
        _lastChunk = found;
        return found;
    }

    /// <summary>Is this chunk loaded?</summary>
    public bool IsLoaded(Vector3I chunk) => _chunks.ContainsKey(chunk);

    /// <summary>
    /// Drops a chunk's data.
    ///
    /// The counterpart the old dictionary never had. Streaming is only
    /// possible if leaving an area actually frees what it cost, and with the
    /// data owned per chunk that is one dictionary removal — the byte array
    /// goes with it.
    /// </summary>
    public bool Unload(Vector3I chunk)
    {
        if (!_chunks.Remove(chunk))
            return false;

        // The cache may name the chunk just dropped, which would hand a freed
        // chunk to the next reader on this thread.
        if (_lastStore == this && _lastCoord == chunk)
        {
            _lastChunk = null;
            _lastStore = null;
        }

        return true;
    }

    /// <summary>Forgets every chunk.</summary>
    public void Clear()
    {
        _chunks.Clear();
        _lastChunk = null;
        _lastStore = null;
    }

    /// <summary>
    /// The material at a cell, or <see cref="Air"/> if it is empty or its
    /// chunk is not loaded.
    ///
    /// An unloaded chunk reads as air because a caller asking what is THERE
    /// gets the same answer either way — nothing it can dig, walk on or hit.
    ///
    /// The mesher must NOT use this. It asks a different question: whether a
    /// face is covered, where "empty" and "not loaded yet" call for opposite
    /// answers. It uses <see cref="Has(Vector3I, out bool)"/>, which keeps the
    /// two apart. This overload folding them together is why the planet's core
    /// was visible through its own surface: the shells below the loaded region
    /// exist on the grid but hold no chunk, so every inward face at the bottom
    /// of the loaded region was drawn as though it opened onto sky.
    /// </summary>
    public byte Get(Vector3I cell)
    {
        Chunk chunk = Find(ChunkOf(cell));
        if (chunk == null)
            return Air;

        return chunk.Cells == null ? chunk.Uniform : chunk.Cells[IndexOf(cell)];
    }

    /// <summary>Is there a node at this cell?</summary>
    public bool Has(Vector3I cell) => Get(cell) != Air;

    /// <summary>
    /// Is there a node at this cell, or is the answer not known yet?
    ///
    /// The distinction <see cref="Has"/> cannot draw. Has folds "empty" and
    /// "not loaded" together into false, which is right for anything asking
    /// what is THERE and wrong for the mesher, which is asking whether a face
    /// is COVERED. An unloaded neighbour reported as air makes the mesher draw
    /// a face into a chunk that is about to arrive and hide it — and where the
    /// unloaded neighbour is inward, that face is a window into the planet's
    /// core.
    ///
    /// Returns false with <paramref name="known"/> false for a cell whose
    /// chunk is not resident, so the caller can decide for itself which way to
    /// resolve the unknown.
    /// </summary>
    public bool Has(Vector3I cell, out bool known)
    {
        Chunk chunk = Find(ChunkOf(cell));

        if (chunk == null)
        {
            known = false;
            return false;
        }

        known = true;
        return (chunk.Cells == null ? chunk.Uniform : chunk.Cells[IndexOf(cell)]) != Air;
    }

    /// <summary>
    /// Writes a cell, creating its chunk if needed. Returns whether anything
    /// changed.
    /// </summary>
    public bool Set(Vector3I cell, byte material)
    {
        Chunk chunk = GetOrCreate(ChunkOf(cell));
        return SetIn(chunk, IndexOf(cell), material);
    }

    /// <summary>Writes a cell of a chunk already in hand, by local index.</summary>
    public bool SetIn(Chunk chunk, int index, byte material)
    {
        if (chunk.Cells == null)
        {
            // Still uniform, and the write agrees with it: nothing to do, and
            // crucially no array to allocate. This is the path that keeps a
            // solidly generated chunk at a few bytes.
            if (chunk.Uniform == material)
                return false;

            chunk.Materialise();
        }

        byte previous = chunk.Cells[index];
        if (previous == material)
            return false;

        chunk.Cells[index] = material;

        if (previous == Air && material != Air)
            chunk.SolidCount++;
        else if (previous != Air && material == Air)
            chunk.SolidCount--;

        return true;
    }

    /// <summary>
    /// Fills a whole chunk with one material without allocating an array.
    ///
    /// The generator's fast path: a chunk that comes back entirely air or
    /// entirely rock — which most do — is stored as a single byte, and this is
    /// what lets a large loaded region cost megabytes rather than gigabytes.
    /// </summary>
    public void SetUniform(Vector3I coord, byte material)
    {
        Chunk chunk = GetOrCreate(coord);
        chunk.Cells = null;
        chunk.Uniform = material;
        chunk.SolidCount = material == Air ? 0 : ChunkVolume;
    }

    /// <summary>
    /// Installs a generated chunk from a dense array, collapsing it to the
    /// uniform form when every cell agrees.
    ///
    /// The collapse is worth its single pass over the array: it is what turns
    /// the sky and the deep interior of an island — the great majority of
    /// chunks by count — from 32 KB into one byte, and it happens exactly once
    /// per chunk at load.
    /// </summary>
    public void Install(Vector3I coord, byte[] cells, int solidCount)
    {
        Chunk chunk = GetOrCreate(coord);

        byte first = cells[0];
        bool uniform = true;
        for (int i = 1; i < cells.Length; i++)
        {
            if (cells[i] != first)
            {
                uniform = false;
                break;
            }
        }

        if (uniform)
        {
            chunk.Cells = null;
            chunk.Uniform = first;
            chunk.SolidCount = first == Air ? 0 : ChunkVolume;
            return;
        }

        chunk.Cells = cells;
        chunk.Uniform = Air;
        chunk.SolidCount = solidCount;
    }

    /// <summary>
    /// Approximate bytes held, for the stats overlay.
    ///
    /// Worth surfacing because memory, not generation time, is what used to
    /// bound the world — being able to watch it while flying is how a
    /// regression in the uniform-collapse path gets noticed.
    /// </summary>
    public long ApproximateBytes()
    {
        long total = 0;
        foreach (var kv in _chunks)
        {
            // Roughly the dictionary entry plus the chunk object; the array,
            // when there is one, dwarfs both.
            total += 64;
            if (kv.Value.Cells != null)
                total += ChunkVolume;
        }

        return total;
    }
}
