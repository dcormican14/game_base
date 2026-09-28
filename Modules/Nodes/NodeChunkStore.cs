using Godot;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace GameBase.Nodes;

/// <summary>
/// The world's node data, held one chunk at a time.
///
/// A cell is two bytes: its material (<see cref="NodeMaterial"/>, or
/// <see cref="Air"/>) and its fill (<see cref="NodeFill"/>), which only
/// particle nodes use. A chunk keeps them as two dense arrays -- or, when every
/// cell agrees, as a single value of each and no arrays at all. Deep rock and
/// open sky are uniform, so most of a planet costs a few bytes a chunk.
///
/// THREADING. Writes are main-thread only. Reads are safe from mesh workers at
/// the same time: the chunk table is concurrent, a chunk's arrays are published
/// only once filled, and a byte read racing a byte write sees one value or the
/// other -- either way the section is marked dirty by the write and re-meshed.
/// </summary>
public sealed class NodeChunkStore
{
    /// <summary>Cells per chunk edge.</summary>
    public const int ChunkSize = 32;

    /// <summary>Cells in one chunk.</summary>
    public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

    private const int ChunkShift = 5;
    private const int ChunkMask = ChunkSize - 1;

    /// <summary>The material byte of an empty cell.</summary>
    public const byte Air = 255;

    /// <summary>One chunk's cells.</summary>
    public sealed class Chunk
    {
        /// <summary>Material of every cell while <see cref="Materials"/> is null.</summary>
        public byte UniformMaterial = Air;

        /// <summary>Fill of every cell while <see cref="Fills"/> is null.</summary>
        public byte UniformFill = NodeFill.Empty;

        /// <summary>One material per cell, or null while uniform.</summary>
        public byte[] Materials;

        /// <summary>One fill per cell, or null while uniform. Never null when
        /// <see cref="Materials"/> is set.</summary>
        public byte[] Fills;

        /// <summary>Cells holding anything but air.</summary>
        public int SolidCount;

        public bool IsUniform => Materials == null;

        public byte MaterialAt(int index)
        {
            byte[] materials = Materials;
            return materials == null ? UniformMaterial : materials[index];
        }

        public byte FillAt(int index)
        {
            // Fills is published before Materials, so whenever a reader sees
            // the dense materials it also sees the dense fills.
            byte[] fills = Fills;
            return fills == null ? UniformFill : fills[index];
        }

        /// <summary>Breaks uniformity so a single cell can differ.</summary>
        public void Materialise()
        {
            if (Materials != null)
                return;

            var fills = new byte[ChunkVolume];
            var materials = new byte[ChunkVolume];
            System.Array.Fill(fills, UniformFill);
            System.Array.Fill(materials, UniformMaterial);

            Volatile.Write(ref Fills, fills);
            Volatile.Write(ref Materials, materials);
        }
    }

    private readonly ConcurrentDictionary<Vector3I, Chunk> _chunks = new();

    // The last chunk looked up, per thread: consecutive reads almost always
    // land in the same chunk, which turns a table probe into a compare.
    //
    // Stamped with the table's version, which every install and unload bumps,
    // so a worker never keeps reading a chunk object the table has replaced.
    [System.ThreadStatic] private static NodeChunkStore _lastStore;
    [System.ThreadStatic] private static Vector3I _lastCoord;
    [System.ThreadStatic] private static Chunk _lastChunk;
    [System.ThreadStatic] private static int _lastVersion;

    private int _version;

    public int ChunkCount => _chunks.Count;

    public IEnumerable<KeyValuePair<Vector3I, Chunk>> Chunks => _chunks;

    /// <summary>Non-air cells across every loaded chunk.</summary>
    public long NodeCount
    {
        get
        {
            long total = 0;
            foreach (var pair in _chunks)
                total += pair.Value.SolidCount;
            return total;
        }
    }

    // --------------------------------------------------------------- indexing

    /// <summary>The chunk a cell belongs to (floor division, so negatives work).</summary>
    public static Vector3I ChunkOf(Vector3I cell) =>
        new(cell.X >> ChunkShift, cell.Y >> ChunkShift, cell.Z >> ChunkShift);

    /// <summary>The lowest cell of a chunk.</summary>
    public static Vector3I OriginOf(Vector3I chunk) =>
        new(chunk.X << ChunkShift, chunk.Y << ChunkShift, chunk.Z << ChunkShift);

    /// <summary>A cell's index within its chunk.</summary>
    public static int IndexOf(Vector3I cell) =>
        ((cell.X & ChunkMask) << (ChunkShift * 2))
        | ((cell.Y & ChunkMask) << ChunkShift)
        | (cell.Z & ChunkMask);

    /// <summary>The index of chunk-local coordinates.</summary>
    public static int LocalIndex(int x, int y, int z) =>
        (x << (ChunkShift * 2)) | (y << ChunkShift) | z;

    // ------------------------------------------------------------------ reads

    /// <summary>The chunk at a coordinate, or null if it is not loaded.</summary>
    public Chunk Find(Vector3I chunk)
    {
        int version = Volatile.Read(ref _version);

        if (_lastStore == this && _lastCoord == chunk && _lastVersion == version
            && _lastChunk != null)
            return _lastChunk;

        if (!_chunks.TryGetValue(chunk, out Chunk found))
            return null;

        _lastStore = this;
        _lastCoord = chunk;
        _lastChunk = found;
        _lastVersion = version;
        return found;
    }

    public bool IsLoaded(Vector3I chunk) => _chunks.ContainsKey(chunk);

    /// <summary>
    /// Reads a cell. False when its chunk is not loaded, which is NOT the same
    /// as air: the meshers treat an unknown neighbour as covering, so a face is
    /// never drawn into a chunk that has not arrived yet.
    /// </summary>
    public bool TryRead(Vector3I cell, out byte material, out byte fill)
    {
        Chunk chunk = Find(ChunkOf(cell));

        if (chunk == null)
        {
            material = Air;
            fill = NodeFill.Empty;
            return false;
        }

        int index = IndexOf(cell);
        material = chunk.MaterialAt(index);
        fill = chunk.FillAt(index);
        return true;
    }

    /// <summary>
    /// Is every cell in the box from <paramref name="low"/> to
    /// <paramref name="high"/> (inclusive) air, in chunks that are loaded and
    /// still uniform? Answered from the chunks alone, without reading a cell --
    /// which is how open sky gets skipped by the meshers.
    /// </summary>
    public bool IsUniformAir(Vector3I low, Vector3I high)
    {
        Vector3I first = ChunkOf(low);
        Vector3I last = ChunkOf(high);

        for (int x = first.X; x <= last.X; x++)
        for (int y = first.Y; y <= last.Y; y++)
        for (int z = first.Z; z <= last.Z; z++)
        {
            Chunk chunk = Find(new Vector3I(x, y, z));

            if (chunk == null || !chunk.IsUniform || chunk.UniformMaterial != Air)
                return false;
        }

        return true;
    }

    /// <summary>A cell's material byte; air when empty or not loaded.</summary>
    public byte MaterialAt(Vector3I cell)
    {
        Chunk chunk = Find(ChunkOf(cell));
        return chunk?.MaterialAt(IndexOf(cell)) ?? Air;
    }

    /// <summary>A cell's fill; empty when air or not loaded.</summary>
    public byte FillAt(Vector3I cell)
    {
        Chunk chunk = Find(ChunkOf(cell));
        return chunk?.FillAt(IndexOf(cell)) ?? NodeFill.Empty;
    }

    // ----------------------------------------------------------------- writes

    /// <summary>
    /// Writes a cell, creating its chunk if needed. Main thread only.
    /// Returns whether anything changed.
    /// </summary>
    public bool Set(Vector3I cell, byte material, byte fill)
    {
        Vector3I coord = ChunkOf(cell);
        Chunk chunk = Find(coord);

        if (chunk == null)
        {
            chunk = new Chunk();
            _chunks[coord] = chunk;
            Interlocked.Increment(ref _version);
        }

        int index = IndexOf(cell);
        byte wasMaterial = chunk.MaterialAt(index);

        if (wasMaterial == material && chunk.FillAt(index) == fill)
            return false;

        chunk.Materialise();
        chunk.Fills[index] = fill;
        chunk.Materials[index] = material;

        if (wasMaterial == Air && material != Air)
            chunk.SolidCount++;
        else if (wasMaterial != Air && material == Air)
            chunk.SolidCount--;

        return true;
    }

    /// <summary>
    /// Installs a generated chunk, collapsing it to the uniform form when every
    /// cell agrees. Main thread only.
    /// </summary>
    public void Install(Vector3I coord, byte[] materials, byte[] fills)
    {
        var chunk = new Chunk();

        int solid = 0;
        bool uniform = true;
        byte firstMaterial = materials[0];
        byte firstFill = fills[0];

        for (int i = 0; i < ChunkVolume; i++)
        {
            if (materials[i] != Air)
                solid++;

            if (uniform && (materials[i] != firstMaterial || fills[i] != firstFill))
                uniform = false;
        }

        chunk.SolidCount = solid;

        if (uniform)
        {
            chunk.UniformMaterial = firstMaterial;
            chunk.UniformFill = firstFill;
        }
        else
        {
            chunk.Fills = fills;
            chunk.Materials = materials;
        }

        // Replaced wholesale rather than written into, so a worker holding the
        // previous chunk object keeps reading a consistent one.
        _chunks[coord] = chunk;
        Interlocked.Increment(ref _version);
    }

    /// <summary>Drops a chunk's data. Main thread only.</summary>
    public bool Unload(Vector3I chunk)
    {
        if (!_chunks.TryRemove(chunk, out _))
            return false;

        Interlocked.Increment(ref _version);
        return true;
    }

    public void Clear()
    {
        _chunks.Clear();
        Interlocked.Increment(ref _version);
    }
}
