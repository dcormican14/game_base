using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using GameBase.Core;

namespace GameBase.Nodes;

/// <summary>
/// Keeps the chunks around a target resident and drops the ones behind it.
///
/// Residency is a ball of <see cref="LoadRadius"/> chunks around the target --
/// ALL of them, open sky included. Sky costs almost nothing (a uniform chunk is
/// a few bytes, and a section of nothing but air is never meshed), and it has
/// to be there: a chunk that is not loaded cannot be edited, so skipping the
/// sky would put a ceiling on how high anything can be built. Missing chunks are generated on
/// worker threads nearest-first and installed into the <see cref="NodeWorld"/>,
/// which meshes them. Chunks past <see cref="UnloadRadius"/> -- deliberately
/// larger, so a player on the boundary does not load and free the same chunk
/// on every step -- are dropped.
///
/// READY means the player can be let go: every chunk within
/// <see cref="ReadyRadius"/> is resident and a ray cast down from the target
/// meets real collision. Only the second part cannot be fooled by a spawn with
/// nothing but sky around it.
/// </summary>
public partial class ChunkStreamer : Node, ILoadProgress
{
    /// <summary>The world to stream into. Defaults to a sibling or parent NodeWorld.</summary>
    [Export] public NodePath WorldPath { get; set; } = "";

    /// <summary>What to follow. Defaults to the node in the "player" group.</summary>
    [Export] public NodePath TargetPath { get; set; } = "";

    /// <summary>Chunks kept resident around the target, as a radius in chunks.</summary>
    [Export(PropertyHint.Range, "1,16,1")] public int LoadRadius { get; set; } = 4;

    /// <summary>How far a chunk must be before it is dropped. Kept above <see cref="LoadRadius"/>.</summary>
    [Export(PropertyHint.Range, "2,24,1")] public int UnloadRadius { get; set; } = 6;

    /// <summary>Chunks around the target that must be resident before the world is ready.</summary>
    [Export(PropertyHint.Range, "0,4,1")] public int ReadyRadius { get; set; } = 1;

    /// <summary>How far below the target to look for ground before calling the world ready.</summary>
    [Export(PropertyHint.Range, "4,512,4")] public float GroundProbe { get; set; } = 96f;

    /// <summary>Worker threads generating chunks.</summary>
    [Export(PropertyHint.Range, "1,16,1")] public int Workers { get; set; } = 3;

    public IChunkGenerator Generator { get; set; }

    public NodeWorld World { get; set; }

    public Node3D Target { get; set; }

    public bool IsReady { get; private set; }

    public float Progress { get; private set; }

    public event Action BecameReady;

    /// <summary>Chunks queued or being generated.</summary>
    public int PendingChunks => _queue.Count + _inFlight.Count;

    private readonly List<Vector3I> _queue = new();
    private readonly HashSet<Vector3I> _inFlight = new();
    private readonly ConcurrentQueue<Generated> _finished = new();
    private readonly List<Vector3I> _scratch = new();

    private Vector3I _centre;
    private bool _scanned;
    private int _generation;

    private readonly record struct Generated(Vector3I Chunk, byte[] Materials, byte[] Fills, int Generation);

    public override void _Ready()
    {
        World ??= !WorldPath.IsEmpty ? GetNodeOrNull<NodeWorld>(WorldPath) : null;
        World ??= GetParent()?.GetNodeOrNull<NodeWorld>("NodeWorld") ?? GetParentOrNull<NodeWorld>();
    }

    /// <summary>Forgets everything resident and queued, for a world that has been rebuilt.</summary>
    public void Restart()
    {
        Interlocked.Increment(ref _generation);
        _queue.Clear();
        _inFlight.Clear();
        _finished.Clear();
        _scanned = false;
        IsReady = false;
        Progress = 0f;
        _progressFloor = 0f;
    }

    public override void _Process(double delta)
    {
        if (World?.Grid == null || Generator == null || !FindTarget())
            return;

        Vector3I centre = NodeChunkStore.ChunkOf(World.CellAt(Target.GlobalPosition));

        if (!_scanned || centre != _centre)
        {
            _centre = centre;
            _scanned = true;
            Rescan();
        }

        Collect();
        Dispatch();

        if (!IsReady)
            CheckReady();
    }

    private bool FindTarget()
    {
        if (Target != null && IsInstanceValid(Target))
            return true;

        Target = !TargetPath.IsEmpty ? GetNodeOrNull<Node3D>(TargetPath) : null;

        if (Target == null)
        {
            foreach (Node node in GetTree().GetNodesInGroup(Groups.Player))
            {
                if (node is Node3D found)
                {
                    Target = found;
                    break;
                }
            }
        }

        return Target != null;
    }

    // ------------------------------------------------------------ residency

    private void Rescan()
    {
        int unload = Math.Max(UnloadRadius, LoadRadius + 1);

        _scratch.Clear();
        foreach (var pair in World.Store.Chunks)
        {
            if (DistanceSquared(pair.Key, _centre) > unload * unload)
                _scratch.Add(pair.Key);
        }

        foreach (Vector3I chunk in _scratch)
            World.UnloadChunk(chunk);

        _queue.Clear();

        int r = LoadRadius;

        for (int x = -r; x <= r; x++)
        for (int y = -r; y <= r; y++)
        for (int z = -r; z <= r; z++)
        {
            if (x * x + y * y + z * z > r * r)
                continue;

            var chunk = new Vector3I(_centre.X + x, _centre.Y + y, _centre.Z + z);

            if (!World.IsChunkLoaded(chunk) && !_inFlight.Contains(chunk))
                _queue.Add(chunk);
        }

        // Nearest first, so the ground the player is about to stand on is built
        // before the scenery at the edge of view. Sorted descending so the
        // nearest is taken from the end of the list.
        _queue.Sort((a, b) => DistanceSquared(b, _centre).CompareTo(DistanceSquared(a, _centre)));
    }

    private static int DistanceSquared(Vector3I a, Vector3I b)
    {
        Vector3I d = a - b;
        return d.X * d.X + d.Y * d.Y + d.Z * d.Z;
    }

    // ----------------------------------------------------------- generation

    private void Dispatch()
    {
        while (_inFlight.Count < Workers && _queue.Count > 0)
        {
            Vector3I chunk = _queue[^1];
            _queue.RemoveAt(_queue.Count - 1);

            if (!_inFlight.Add(chunk))
                continue;

            IChunkGenerator generator = Generator;
            int generation = Volatile.Read(ref _generation);

            System.Threading.Tasks.Task.Run(() =>
            {
                var materials = new byte[NodeChunkStore.ChunkVolume];
                var fills = new byte[NodeChunkStore.ChunkVolume];

                try
                {
                    generator.Generate(chunk, materials, fills);
                }
                catch (Exception error)
                {
                    GD.PushError($"ChunkStreamer: generating {chunk} failed - {error}");
                    Array.Fill(materials, NodeChunkStore.Air);
                    Array.Clear(fills);
                }

                _finished.Enqueue(new Generated(chunk, materials, fills, generation));
            });
        }
    }

    /// <summary>Installs whatever the workers finished that is still wanted.</summary>
    private void Collect()
    {
        int unload = Math.Max(UnloadRadius, LoadRadius + 1);

        while (_finished.TryDequeue(out Generated done))
        {
            if (done.Generation != Volatile.Read(ref _generation))
                continue;

            _inFlight.Remove(done.Chunk);

            // The player moved on while it was being made.
            if (DistanceSquared(done.Chunk, _centre) > unload * unload)
                continue;

            World.InstallChunk(done.Chunk, done.Materials, done.Fills);
        }
    }

    // ------------------------------------------------------------ readiness

    private float _progressFloor;

    private void CheckReady()
    {
        int wanted = 0;
        int have = 0;
        int r = ReadyRadius;

        for (int x = -r; x <= r; x++)
        for (int y = -r; y <= r; y++)
        for (int z = -r; z <= r; z++)
        {
            var chunk = new Vector3I(_centre.X + x, _centre.Y + y, _centre.Z + z);

            wanted++;
            if (World.IsChunkLoaded(chunk))
                have++;
        }

        float resident = wanted == 0 ? 1f : have / (float)wanted;

        int built = World.BuiltSections;
        float meshed = built / (float)Math.Max(1, built + World.PendingSections);

        // Never allowed to fall: a bar that slides back tells the player the
        // wait just got longer.
        _progressFloor = Math.Max(_progressFloor, 0.6f * resident + 0.39f * meshed);
        Progress = _progressFloor;

        if (have < wanted || !HasGround())
            return;

        IsReady = true;
        Progress = 1f;
        BecameReady?.Invoke();
    }

    /// <summary>Is there collision under the target yet?</summary>
    private bool HasGround()
    {
        Vector3 from = Target.GlobalPosition;
        Vector3 down = World.GlobalBasis * Generator.DownAt(World.ToLocal(from));

        var exclude = Target is CollisionObject3D body
            ? new Godot.Collections.Array<Rid> { body.GetRid() }
            : null;

        return World.Raycast(from, from + down.Normalized() * GroundProbe, out _, exclude);
    }
}
