using Godot;

namespace GameBase.Nodes;

/// <summary>
/// Decides what a world is made of, one chunk at a time.
///
/// Called from worker threads, so implementations must be pure functions of
/// the chunk coordinate: no shared mutable state, and the same answer however
/// often and in whatever order a chunk is asked for. That is what lets a chunk
/// be dropped when the player leaves and regenerated identically on return.
/// </summary>
public interface IChunkGenerator
{
    /// <summary>
    /// Fills one chunk: a material byte (or <see cref="NodeChunkStore.Air"/>)
    /// and a fill byte per cell, indexed by <see cref="NodeChunkStore.LocalIndex"/>.
    /// </summary>
    void Generate(Vector3I chunk, byte[] materials, byte[] fills);

    /// <summary>Which way is down at a point in the world's local space.</summary>
    Vector3 DownAt(Vector3 localPoint);
}
