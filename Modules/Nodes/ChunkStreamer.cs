using Godot;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// Keeps the chunks around a target resident, and drops the ones behind it.
///
/// This is the piece that turns a finite generated region into an endless
/// world. The generator it drives defines its field over all of space already
/// — density is a pure function of position and seed — so what bounded the
/// world was never the field, it was the fact that everything generated stayed
/// in memory forever. Streaming is the missing driver: decide which chunks
/// should exist right now, make those, and free the rest.
///
/// FULLY 3D
///
/// Residency is a sphere of chunks around the target, in all three axes. Not
/// columns: a world of floating islands has as much interesting structure
/// above and below the player as around them, and a column-based scheme would
/// either load the entire vertical extent of the world at every horizontal
/// position (which is what made the old region so expensive) or clip the world
/// at an arbitrary ceiling and floor.
///
/// The radius is a distance in chunks, so residency is a ball rather than a
/// cube — about half the chunks of the enclosing cube for the same view
/// distance, and no far corners loaded at twice the range of the axes.
///
/// HYSTERESIS
///
/// Chunks are loaded inside <see cref="LoadRadius"/> and dropped outside
/// <see cref="UnloadRadius"/>, which is deliberately larger. With a single
/// radius a player standing on a boundary loads and frees the same chunk every
/// time they step back and forth — the most expensive operations in the
/// system, run repeatedly for no change in what is visible.
///
/// NEAREST FIRST
///
/// Work is ordered by distance from the target, so the chunk the player is
/// about to walk into is built before the one at the edge of the view. Under a
/// per-frame budget that ordering is what decides whether the world appears to
/// keep up: the same throughput spent in the wrong order shows holes exactly
/// where the player is looking.
/// </summary>
public abstract partial class ChunkStreamer : Node
{
    /// <summary>What to follow. Defaults to the scene's player.</summary>
    [Export] public NodePath TargetPath { get; set; } = "";

    /// <summary>Stream chunks at all. Off leaves whatever is already loaded.</summary>
    [Export] public bool Enabled { get; set; } = true;

    /// <summary>
    /// How far chunks are kept resident, in chunks.
    ///
    /// The cost of a view distance grows with its cube, so this is the single
    /// most expensive dial in the engine — 6 is roughly 900 resident chunks,
    /// 8 is over 2000.
    /// </summary>
    [Export(PropertyHint.Range, "1,24,1")]
    public int LoadRadius { get; set; } = 3;

    /// <summary>
    /// How far a chunk must be before it is dropped, in chunks. Must exceed
    /// <see cref="LoadRadius"/>, or a chunk on the boundary thrashes.
    /// </summary>
    [Export(PropertyHint.Range, "2,32,1")]
    public int UnloadRadius { get; set; } = 5;

    /// <summary>
    /// Chunks whose data is generated per frame.
    ///
    /// Generation is the cheaper half — it writes into a byte array and
    /// touches no scene state — so it can run ahead of meshing.
    /// </summary>
    [Export(PropertyHint.Range, "1,64,1")]
    public int GeneratePerFrame { get; set; } = 8;

    /// <summary>
    /// Chunks meshed per frame.
    ///
    /// Kept low because meshing ends in an ArrayMesh upload and a collision
    /// shape rebuild, both of which touch the rendering and physics servers on
    /// the main thread. This is the number that decides frame pacing while
    /// flying.
    /// </summary>
    [Export(PropertyHint.Range, "1,32,1")]
    public int MeshPerFrame { get; set; } = 2;

    /// <summary>
    /// Milliseconds per frame the streamer may spend, across both phases.
    /// A ceiling on top of the per-phase counts, since chunk costs vary a lot.
    /// </summary>
    [Export(PropertyHint.Range, "1,50,0.5")]
    public float MillisecondsPerFrame { get; set; } = 6f;

    /// <summary>
    /// Milliseconds per frame spent turning chunk data into meshes.
    ///
    /// Separate from <see cref="MillisecondsPerFrame"/>, which bounds the
    /// generation side. This one is the dial that decides whether streaming is
    /// felt: a chunk is 64 sections and the whole batch used to be built in
    /// one call, which measured at a 912ms frame -- roughly one frame per
    /// second while a chunk landed. Spending a few milliseconds a frame
    /// instead spreads the same work over about a second of play at full rate.
    /// </summary>
    [Export(PropertyHint.Range, "1,50,0.5")]
    public float MeshMillisecondsPerFrame { get; set; } = 4f;

    /// <summary>
    /// How far the target must move before residency is recomputed, in cells.
    /// Rescanning every frame is pure waste — the answer only changes when the
    /// target crosses into a new chunk.
    /// </summary>
    [Export(PropertyHint.Range, "1,64,1")]
    public int RescanAfterCells { get; set; } = NodeChunkStore.ChunkSize / 2;

    /// <summary>
    /// Most chunks to keep queued at once, nearest first.
    ///
    /// A backlog longer than the workers can clear before the next rescan is
    /// not throughput, it is latency: the far end of it is terrain the player
    /// will have left before it is built. Bounding it keeps the queue a
    /// picture of what is wanted now rather than a record of everything ever
    /// asked for.
    /// </summary>
    [Export(PropertyHint.Range, "8,512,1")]
    public int QueueLimit { get; set; } = 64;

    protected Node3D Target;

    /// <summary>The world being streamed into. Supplied by the subclass.</summary>
    protected abstract NodeWorld World { get; }

    /// <summary>
    /// Fills `cells` with one chunk's materials, and returns how many are
    /// solid.
    ///
    /// The subclass's whole responsibility. It must be a pure function of the
    /// chunk coordinate and the generator's seed — no dependence on which
    /// other chunks exist or on the order they are asked for — because chunks
    /// are generated in whatever order the player's movement demands, and a
    /// chunk unloaded and revisited must come back identical.
    /// </summary>
    protected abstract int GenerateChunk(Vector3I chunk, byte[] cells);

    /// <summary>
    /// Could this chunk hold anything at all?
    ///
    /// Asked during the residency scan, before a chunk is queued. The default
    /// says yes, which is the safe answer and what a generator with no cheap
    /// bound should keep.
    ///
    /// It exists so view distance can be large without the scan being
    /// hopeless. Residency is a ball, so its cost grows with the cube of the
    /// radius -- but for a world with structure the interesting chunks are a
    /// SURFACE inside that volume, and the rest is empty space that can be
    /// rejected analytically. On a planet the whole surface is about 2000
    /// chunks while a radius-100 ball is over four million, so rejecting is
    /// the difference between a view distance that reaches the horizon and one
    /// that cannot leave the ground.
    ///
    /// Must be conservative: returning false for a chunk that does hold
    /// something punches a permanent hole in the world.
    /// </summary>
    protected virtual bool CouldHoldAnything(Vector3I chunk) => true;

    /// <summary>Chunks whose data exists but whose mesh does not.</summary>
    private readonly List<Vector3I> _toGenerate = new();
    private readonly List<Vector3I> _toMesh = new();

    /// <summary>Chunks already queued, so a rescan does not enqueue them
    /// twice.</summary>
    private readonly HashSet<Vector3I> _queued = new();

    private readonly List<Vector3I> _scratch = new();

    private Vector3I _lastScanCell;
    private bool _hasScanned;

    /// <summary>The radius the last scan actually used, for the ready check.</summary>
    private int _lastLoadRadius;

    /// <summary>
    /// Chunks around the target that must be meshed before the world counts as
    /// ready to play in, as a radius in chunks.
    ///
    /// A streaming world is never "finished" — there is always more of it just
    /// out of range — so the old signal of "every chunk meshed" would never
    /// fire and the loading screen would never lift. Readiness has to mean
    /// something local instead: enough solid ground around the spawn for the
    /// player to stand on and not see holes.
    ///
    /// 1 is the 3x3x3 of chunks containing and surrounding the spawn, which at
    /// 32 cells a chunk is a 96-cell box — comfortably more than the player can
    /// cross before streaming catches up.
    /// </summary>
    [Export(PropertyHint.Range, "0,6,1")]
    public int ReadyRadius { get; set; } = 1;

    /// <summary>True once the chunks around the spawn are built.</summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// Raised once, when <see cref="IsReady"/> first becomes true.
    ///
    /// Not called "Ready": Godot's Node already has a signal by that name, and
    /// shadowing it would make which one a subscriber gets depend on the
    /// static type of the reference.
    /// </summary>
    public event System.Action BecameReady;

    /// <summary>
    /// How much of the initial ready-set is built, 0..1. What a loading screen
    /// shows: progress toward being able to play, not toward an end of work
    /// that never comes.
    /// </summary>
    public float ReadyProgress { get; private set; }

    /// <summary>Chunks in the ready-set when it was first measured, so
    /// progress has a stable denominator to count against.</summary>
    private int _readyTotal;

    public override void _Ready()
    {
        Target = !TargetPath.IsEmpty ? GetNodeOrNull<Node3D>(TargetPath) : null;
        Target ??= FindPlayer(GetTree().CurrentScene ?? GetParent());
    }

    /// <summary>Finds the scene's player by group, falling back to a search
    /// for anything named Player.</summary>
    private static Node3D FindPlayer(Node from)
    {
        if (from == null)
            return null;

        foreach (Node node in from.GetTree().GetNodesInGroup("player"))
        {
            if (node is Node3D found)
                return found;
        }

        return from.GetNodeOrNull<Node3D>("Player");
    }

    public override void _Process(double delta)
    {
        if (!Enabled || World == null)
            return;

        if (Target == null)
        {
            Target = !TargetPath.IsEmpty ? GetNodeOrNull<Node3D>(TargetPath) : null;
            Target ??= FindPlayer(GetTree().CurrentScene ?? GetParent());
            if (Target == null)
                return;
        }

        Vector3I centre = World.CellAt(Target.GlobalPosition);

        // Rescan when the target has moved somewhere that could change the
        // answer, OR when generation has run dry while the world is still not
        // ready.
        //
        // THE SECOND CASE IS A DEADLOCK FIX, NOT AN OPTIMISATION.
        //
        // Rescan queues the whole residency ball and Trim keeps only the
        // nearest QueueLimit, un-marking the rest so a later scan can offer
        // them again. At LoadRadius 3 the ball is 257 chunks against a limit of
        // 64, so most of it is dropped every scan and only re-offered by the
        // next one. Movement alone does not provide a next one: a player
        // standing still at spawn never travels the cells that triggers it.
        //
        // What that left was a mesh queue full of chunks whose face neighbours
        // had been dropped and would never be asked for again. Measured on the
        // planet, the streamer settled at 85% ready with 29 chunks queued for
        // meshing, each waiting on neighbours only two or three chunks away
        // that were in no queue, in no worker, and not marked — unchanged from
        // four seconds in to twenty.
        //
        // Generation running dry is the signal, NOT every queue being empty:
        // the mesh queue is precisely what stays full in this state, so
        // waiting for it to drain would wait forever.
        bool starved = _toGenerate.Count == 0 && _inFlight.Count == 0;

        if (!_hasScanned || (starved && !IsReady)
            || Distance(centre, _lastScanCell) >= RescanAfterCells)
        {
            Rescan(NodeChunkStore.ChunkOf(centre));
            _lastScanCell = centre;
            _hasScanned = true;
        }

        Pump();
    }

    private static int Distance(Vector3I a, Vector3I b)
    {
        Vector3I d = a - b;
        return Mathf.Max(Mathf.Abs(d.X), Mathf.Max(Mathf.Abs(d.Y), Mathf.Abs(d.Z)));
    }

    /// <summary>
    /// Recomputes which chunks should be resident, queueing the missing ones
    /// nearest-first and dropping the departed.
    /// </summary>
    private void Rescan(Vector3I centre)
    {
        int load = Mathf.Max(1, EffectiveLoadRadius(centre));

        // The unload radius has to clear the generate margin too, or the
        // margin chunks are dropped the moment they are made and the frontier
        // never gets the neighbours it is waiting on.
        int unload = Mathf.Max(load + 2, UnloadRadius);
        _lastLoadRadius = load;

        // Drop first, so the memory the new chunks need is already free by the
        // time they are generated rather than after.
        _scratch.Clear();
        foreach (var kv in World.Store.Chunks)
        {
            if (ChunkDistance(kv.Key, centre) > unload)
                _scratch.Add(kv.Key);
        }

        foreach (Vector3I dead in _scratch)
        {
            World.UnloadChunk(dead);
            _queued.Remove(dead);
        }

        // Anything still queued but now out of range is work nobody wants.
        //
        // Dropping these from _queued as well as from the lists is what makes
        // the streamer recoverable. _queued exists to stop a chunk being
        // enqueued twice; a chunk removed from a list but left in that set is
        // marked as pending forever and can never be asked for again. Walking
        // away from an area and back used to leave a growing set of chunks in
        // exactly that state — the world emptied to a handful of chunks while
        // the pending count sat stuck in the hundreds, which is the bug behind
        // terrain thinning out as you travel.

        DropOutOfRange(_toGenerate, centre, unload);
        DropOutOfRange(_toMesh, centre, unload);

        // One chunk PAST the load radius is generated but never meshed.
        //
        // Meshing a chunk reads one cell into each neighbour to cull the faces
        // between them, so a chunk on the frontier cannot be meshed until its
        // neighbours' data exists. Without this margin the outermost shell's
        // neighbours are never asked for, those chunks wait forever, and the
        // ready check that counts them never completes.
        //
        // The margin is data only: generating a chunk fills a byte array,
        // which is far cheaper than meshing one, and these are never meshed
        // unless the player moves far enough to bring them inside the radius.
        int outer = load + 1;

        // ARC CHUNKS ARE WALKED, NOT ENUMERATED.
        //
        // A chunk index on the cubed sphere is (u, v, shell) with the face
        // folded into u, which is not a metric space: chunks touching across a
        // face edge are 39 apart by index. Sweeping a ball of indices
        // therefore never leaves the face the player is on -- measured, all
        // 257 resident chunks sat on face 0 of 6, so the world simply ended at
        // every seam.
        //
        // Flooding outward through real neighbours follows the surface instead,
        // crossing seams because the grid knows what is over an edge. On a flat
        // world the sweep below is kept, since a ball of indices IS the right
        // answer there and enumerating beats walking.
        if (World.Grid != null)
        {
            FloodResidency(centre, load, outer);
            Sort(centre);
            return;
        }

        // A generator that knows its world is a thin structure inside a large
        // volume can narrow the sweep to a band of distances, so the scan cost
        // follows the structure rather than the cube of the radius.
        ChunkBand(centre, outer, out int bandLo, out int bandHi);

        for (int x = -outer; x <= outer; x++)
        {
            // The ball bounds y for this slice, so rows outside it are never
            // entered rather than entered and rejected.
            int yReach = IntSqrt(outer * outer - x * x);

            for (int y = -yReach; y <= yReach; y++)
            {
                // And z is bounded twice over: by the ball, and by the band of
                // distances the generator says its world occupies.
                //
                // SOLVING for z rather than scanning it is what lets the view
                // distance reach a planet from orbit. The ball alone is cubic
                // in the radius -- at 96 that is seven million cells to walk
                // every rescan, at 256 it is 135 million -- while the chunks
                // that survive are a shell whose area grows only as the square.
                // Iterating the answer instead of filtering for it keeps the
                // scan proportional to what it finds.
                int ballZ = IntSqrt(outer * outer - x * x - y * y);

                int zFrom = -ballZ;
                int zTo = ballZ;

                if (bandLo <= bandHi)
                {
                    // Chunk distance is measured from the WORLD origin, so the
                    // slice's own offset has to be carried in.
                    int wx = centre.X + x;
                    int wy = centre.Y + y;
                    int flat = wx * wx + wy * wy;

                    // |wz| must satisfy bandLo <= flat + wz^2 <= bandHi.
                    if (flat > bandHi)
                        continue;

                    int zOuter = IntSqrt(bandHi - flat);
                    int zInner = flat >= bandLo ? 0 : IntSqrt(bandLo - flat - 1) + 1;

                    // Two arcs where the band is an annulus, one where the
                    // slice passes through it. Emitting both halves keeps the
                    // loop a single pass over what is wanted.
                    EmitRow(centre, x, y, zFrom, zTo, -zOuter - centre.Z, -zInner - centre.Z, load, outer);
                    EmitRow(centre, x, y, zFrom, zTo, zInner - centre.Z, zOuter - centre.Z, load, outer);
                    continue;
                }

                EmitRow(centre, x, y, zFrom, zTo, zFrom, zTo, load, outer);
            }
        }

        Sort(centre);
    }

    /// <summary>
    /// Queues the chunks around the target by walking outward through
    /// neighbours, for a world whose chunks are arcs.
    ///
    /// Breadth-first from the player's own chunk, so distance is travelled
    /// rather than computed, and a face edge is just another step. The visited
    /// set bounds the work; the queue is trimmed afterwards exactly as the
    /// enumerated path's is.
    /// </summary>
    private void FloodResidency(Vector3I centre, int load, int outer)
    {
        _floodSeen.Clear();
        _floodQueue.Clear();

        _floodSeen.Add(centre);
        _floodQueue.Enqueue(centre);

        // Bounded so a pathological grid cannot spin: the ball of chunks a
        // radius `outer` reaches, with room to spare for the seams where a
        // face edge fans out.
        int budget = (2 * outer + 1) * (2 * outer + 1) * (2 * outer + 1) * 2 + 64;

        while (_floodQueue.Count > 0 && _floodSeen.Count < budget)
        {
            Vector3I chunk = _floodQueue.Dequeue();

            int distance = ChunkDistance(chunk, centre);
            if (distance > outer)
                continue;

            Offer(chunk, distance <= load);

            // Step to the six neighbouring chunks THROUGH THE GRID, so a step
            // off a face lands on the next one.
            for (int face = 0; face < 6; face++)
            {
                Vector3I dir = NodeFace.Offsets[face];
                if (!NeighbourChunk(chunk, dir, out Vector3I next))
                    continue;

                if (_floodSeen.Add(next))
                    _floodQueue.Enqueue(next);
            }
        }
    }

    /// <summary>
    /// The chunk next to this one in a direction, found through the grid so
    /// face edges are crossed correctly.
    /// </summary>
    private bool NeighbourChunk(Vector3I chunk, Vector3I direction, out Vector3I result)
    {
        const int Size = NodeChunkStore.ChunkSize;
        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        int half = Size / 2;

        // A cell just past this chunk's boundary in that direction. Stepping a
        // whole chunk at once from the centre keeps the query on the grid even
        // where a face's resolution changes.
        var from = new Vector3I(origin.X + half, origin.Y + half, origin.Z + half);

        if (!World.Grid.Neighbour(from,
                direction.X * Size, direction.Y * Size, direction.Z * Size,
                out Vector3I cell))
        {
            result = default;
            return false;
        }

        result = NodeChunkStore.ChunkOf(cell);
        return result != chunk;
    }

    /// <summary>
    /// Queues one chunk for generation, or for meshing if its data is already
    /// resident. Shared by both residency scans.
    /// </summary>
    private void Offer(Vector3I chunk, bool inside)
    {
        if (!CouldHoldAnything(chunk))
        {
            if (!World.IsChunkLoaded(chunk) || !World.HasChunkMesh(chunk))
            {
                World.Store.SetUniform(chunk, NodeChunkStore.Air);
                World.MarkChunkMeshed(chunk);
            }

            _queued.Remove(chunk);
            return;
        }

        if (World.IsChunkLoaded(chunk))
        {
            if (inside && !World.HasChunkMesh(chunk) && _queued.Add(chunk))
                _toMesh.Add(chunk);

            return;
        }

        if (!_queued.Add(chunk))
            return;

        _toGenerate.Add(chunk);
    }

    private readonly HashSet<Vector3I> _floodSeen = new();
    private readonly Queue<Vector3I> _floodQueue = new();
    private readonly HashSet<Vector3I> _readySeen = new();
    private readonly Queue<Vector3I> _readyQueue = new();

    /// <summary>
    /// Queues one row of chunks, clipped to both the ball and the band.
    /// </summary>
    private void EmitRow(Vector3I centre, int x, int y,
        int ballFrom, int ballTo, int bandFrom, int bandTo, int load, int outer)
    {
        int from = Mathf.Max(ballFrom, bandFrom);
        int to = Mathf.Min(ballTo, bandTo);

        for (int z = from; z <= to; z++)
        {
            var chunk = new Vector3I(centre.X + x, centre.Y + y, centre.Z + z);
            {
                {
                    // Nothing here to find, and the generator knows it without
                    // sampling. Recording it keeps the queue full of chunks
                    // that can actually contribute, which is what lets the
                    // radius grow far enough to see a planet from orbit.
                    //
                    // RECORDED AS EMPTY, not merely skipped. A skipped chunk is
                    // invisible to everything downstream: the readiness check
                    // counts it and never sees it arrive, so a spawn with sky
                    // overhead stalled at 67% forever. Storing the uniform air
                    // byte is what the generator would have produced anyway,
                    // for none of the cost.
                    if (!CouldHoldAnything(chunk))
                    {
                        if (!World.IsChunkLoaded(chunk) || !World.HasChunkMesh(chunk))
                        {
                            World.Store.SetUniform(chunk, NodeChunkStore.Air);
                            World.MarkChunkMeshed(chunk);
                        }

                        _queued.Remove(chunk);
                        continue;
                    }

                    if (World.IsChunkLoaded(chunk))
                    {
                        // Already generated. It may still be an unmeshed
                        // margin chunk that the player has since moved toward,
                        // in which case it now needs geometry.
                        bool inside = x * x + y * y + z * z <= load * load;
                        if (inside && !World.HasChunkMesh(chunk) && _queued.Add(chunk))
                            _toMesh.Add(chunk);

                        continue;
                    }

                    if (!_queued.Add(chunk))
                        continue;

                    _toGenerate.Add(chunk);
                }
            }
        }
    }

    /// <summary>Integer floor of the square root, for non-negative input.</summary>
    private static int IntSqrt(int v) => v <= 0 ? 0 : (int)Mathf.Sqrt(v);

    /// <summary>Orders the queues nearest-first and trims them.</summary>
    private void Sort(Vector3I centre)
    {
        // Nearest first, so the chunk the player is walking into is built
        // before the one at the edge of view.
        _toGenerate.Sort((a, b) =>
            SquareDistance(a, centre).CompareTo(SquareDistance(b, centre)));
        _toMesh.Sort((a, b) =>
            SquareDistance(a, centre).CompareTo(SquareDistance(b, centre)));

        // Keep only the nearest QueueLimit. The queue used to hold every chunk
        // of the ball at once — hundreds of them, most of which the player had
        // moved away from long before a worker reached them, so the streamer
        // spent its time generating terrain that was stale on arrival while
        // the ground underfoot waited behind it.
        //
        // Nothing is lost by forgetting the far ones: the rescan re-derives
        // the whole ball from the player's position every time, so a chunk
        // dropped here is simply re-offered when it matters. What the limit
        // buys is that the work in flight is always work that is still wanted.
        Trim(_toGenerate, QueueLimit);
        Trim(_toMesh, QueueLimit);
    }

    /// <summary>
    /// Keeps the first `limit` entries of an already-sorted queue and un-marks
    /// the rest so a later rescan can offer them again.
    /// </summary>
    private void Trim(List<Vector3I> queue, int limit)
    {
        if (limit <= 0 || queue.Count <= limit)
            return;

        for (int i = limit; i < queue.Count; i++)
        {
            Vector3I chunk = queue[i];
            if (!_inFlight.Contains(chunk))
                _queued.Remove(chunk);
        }

        queue.RemoveRange(limit, queue.Count - limit);
    }

    /// <summary>
    /// Removes chunks beyond `limit` from a queue, and un-marks them so they
    /// can be requested again if the player returns.
    /// </summary>
    private void DropOutOfRange(List<Vector3I> queue, Vector3I centre, int limit)
    {
        for (int i = queue.Count - 1; i >= 0; i--)
        {
            Vector3I chunk = queue[i];
            if (ChunkDistance(chunk, centre) <= limit)
                continue;

            queue.RemoveAt(i);

            // Only un-mark chunks nothing else is still tracking: one being
            // generated right now is legitimately queued until it lands.
            if (!_inFlight.Contains(chunk))
                _queued.Remove(chunk);
        }
    }

    /// <summary>
    /// The load radius to use right now, which a generator may widen.
    ///
    /// A fixed radius is a fixed number of chunks in every direction, which is
    /// the right rule standing on the ground and the wrong one in the air. Fly
    /// away from a planet and the ground leaves the ball entirely: measured,
    /// at 3500 nodes up the nearest rock was 97 chunks below a radius of 3, and
    /// every one of the 250-odd resident chunks held nothing but sky.
    /// </summary>
    protected virtual int EffectiveLoadRadius(Vector3I centre) => LoadRadius;

    /// <summary>
    /// The band of squared chunk-distances-from-the-origin that can hold
    /// anything, or an empty range (lo > hi) for a world with no such bound.
    ///
    /// This is what makes a long view distance affordable for a planet. The
    /// residency ball grows as the cube of its radius -- a radius of 80 is over
    /// four million chunks to even LOOK at -- but a planet's rock lives in a
    /// shell, so the scan can skip every chunk whose distance from the planet's
    /// centre puts it inside the core or out in space.
    /// </summary>
    protected virtual void ChunkBand(Vector3I centre, int radius, out int lo, out int hi)
    {
        lo = 1;
        hi = 0;
    }

    /// <summary>
    /// How far apart two chunks are, in chunks.
    ///
    /// MEASURED IN THE WORLD, not in chunk indices, whenever the world has a
    /// grid. On a cubed sphere the chunk coordinate is (u, v, shell) with the
    /// face folded into u, which is not a metric space: two chunks touching
    /// across a face edge are 39 chunks apart by index. Residency computed on
    /// that never crosses a seam, so the world stopped at the edge of one face
    /// -- measured, 257 resident chunks all on face 0 of 6.
    ///
    /// Comparing the chunks' actual positions fixes it, and costs one distance
    /// per candidate rather than a subtraction. That is the price of chunks
    /// being arcs rather than boxes.
    /// </summary>
    private int ChunkDistance(Vector3I chunk, Vector3I centre)
    {
        if (World?.Grid == null)
        {
            Vector3I d = chunk - centre;
            return Mathf.RoundToInt(Mathf.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z));
        }

        float world = ChunkCentre(chunk).DistanceTo(ChunkCentre(centre));
        return Mathf.RoundToInt(world / NodeChunkStore.ChunkSize);
    }

    private int SquareDistance(Vector3I chunk, Vector3I centre)
    {
        if (World?.Grid == null)
        {
            Vector3I d = chunk - centre;
            return d.X * d.X + d.Y * d.Y + d.Z * d.Z;
        }

        float world = ChunkCentre(chunk).DistanceSquaredTo(ChunkCentre(centre));
        float scale = NodeChunkStore.ChunkSize * NodeChunkStore.ChunkSize;
        return Mathf.RoundToInt(world / scale);
    }

    /// <summary>The world position a chunk sits at, for distance tests.</summary>
    private Vector3 ChunkCentre(Vector3I chunk)
    {
        Vector3I origin = NodeChunkStore.OriginOf(chunk);
        int half = NodeChunkStore.ChunkSize / 2;

        return World.Grid.CentreOf(new Vector3I(
            origin.X + half, origin.Y + half, origin.Z + half));
    }

    /// <summary>
    /// Worker threads generating chunk data. 0 picks a share of the machine
    /// (see <see cref="ThreadBudget"/>).
    ///
    /// This used to default to every core but one, on the reasoning that
    /// generation is the bottleneck so it should have the machine. That is
    /// true and still the wrong thing to do: a game is a guest on the player's
    /// desktop, and saturating 23 of 24 hardware threads with uninterruptible
    /// noise evaluation starves everything else they are running — video
    /// stutters, the compositor drops frames, fans spin up. A background
    /// loader has no business being the heaviest process on the machine.
    /// </summary>
    [Export(PropertyHint.Range, "0,32,1")]
    public int WorkerThreads { get; set; }

    /// <summary>
    /// Share of the machine's threads to use when <see cref="WorkerThreads"/>
    /// is 0.
    ///
    /// A quarter, capped: enough to keep several chunks in flight, few enough
    /// that the rest of the machine stays responsive. The cap matters more
    /// than the fraction on a big machine — a 24-thread desktop does not want
    /// six background threads any more than it wants twenty-three.
    /// </summary>
    [Export(PropertyHint.Range, "0.1,1,0.05")]
    public float ThreadBudget { get; set; } = 0.25f;

    /// <summary>Most workers to run whatever the machine's size.</summary>
    [Export(PropertyHint.Range, "1,16,1")]
    public int MaxWorkers { get; set; } = 4;

    /// <summary>How many worker threads to actually run.</summary>
    private int ThreadCount()
    {
        if (WorkerThreads > 0)
            return WorkerThreads;

        int share = Mathf.FloorToInt(System.Environment.ProcessorCount * ThreadBudget);
        return Mathf.Clamp(share, 1, Mathf.Max(1, MaxWorkers));
    }

    /// <summary>One chunk generated and waiting to be installed.</summary>
    private readonly struct Generated
    {
        public readonly Vector3I Chunk;
        public readonly byte[] Cells;
        public readonly int Solid;

        public Generated(Vector3I chunk, byte[] cells, int solid)
        {
            Chunk = chunk;
            Cells = cells;
            Solid = solid;
        }
    }

    /// <summary>Chunks handed to workers, so the same one is not dispatched
    /// twice while it is in flight.</summary>
    private readonly HashSet<Vector3I> _inFlight = new();

    /// <summary>Finished chunks waiting for the main thread to install them.
    /// Guarded by its own lock, which is the only state the two sides
    /// share.</summary>
    private readonly Queue<Generated> _finished = new();
    private readonly object _finishedLock = new();

    /// <summary>How many workers are running right now.</summary>
    private int _running;


    /// <summary>
    /// Bumped whenever the world this streamer describes changes, so results
    /// from work started against the old one are discarded rather than
    /// installed into the new.
    /// </summary>
    private volatile int _generation;

    /// <summary>
    /// Starts workers on the nearest queued chunks, up to the thread budget.
    ///
    /// Dispatch is from the FRONT of the queue, which the rescan sorted
    /// nearest-first, so the chunks the player is walking into are the ones
    /// occupying the workers.
    /// </summary>
    private void DispatchGeneration()
    {
        int threads = ThreadCount();

        while (_running < threads && _toGenerate.Count > 0)
        {
            Vector3I chunk = _toGenerate[0];
            _toGenerate.RemoveAt(0);

            if (!_inFlight.Add(chunk))
                continue;

            _running++;
            int generation = _generation;

            System.Threading.Tasks.Task.Run(() =>
            {
                // Below normal priority, so the operating system hands the CPU
                // to anything the player is actually interacting with first.
                // Chunk generation is background work by nature — it has a
                // deadline measured in seconds, not frames — and dropping the
                // priority costs it almost nothing on an idle machine while
                // making it yield promptly on a busy one.
                System.Threading.Thread current = System.Threading.Thread.CurrentThread;
                System.Threading.ThreadPriority was = current.Priority;
                try
                {
                    current.Priority = System.Threading.ThreadPriority.BelowNormal;
                }
                catch (System.Exception)
                {
                    // Priority is advisory and some platforms refuse it.
                }

                // A buffer per job rather than one shared scratch: workers run
                // concurrently, and the array is handed straight to the store
                // on completion, so it could not be reused anyway.
                var cells = new byte[NodeChunkStore.ChunkVolume];
                int solid;

                try
                {
                    solid = GenerateChunk(chunk, cells);
                }
                catch (System.Exception e)
                {
                    GD.PushError($"ChunkStreamer: generating {chunk} failed — {e}");
                    solid = 0;
                }

                lock (_finishedLock)
                {
                    // Results for a superseded world are dropped: the field
                    // they were generated against no longer describes the
                    // world they would land in.
                    if (generation == _generation)
                        _finished.Enqueue(new Generated(chunk, cells, solid));

                    _running--;
                }

                // Thread-pool threads are reused, so the priority has to go
                // back or it would leak onto whatever runs next.
                try
                {
                    current.Priority = was;
                }
                catch (System.Exception)
                {
                }
            });
        }
    }

    /// <summary>
    /// Installs whatever the workers finished, and queues the solid ones for
    /// meshing.
    ///
    /// All of it, every frame: installing is a dictionary write and an array
    /// handover, cheap enough that throttling it would only let the queue grow
    /// while the workers sat idle behind it.
    /// </summary>
    private void CollectGenerated(Vector3I meshCentre)
    {
        while (true)
        {
            Generated done;
            lock (_finishedLock)
            {
                if (_finished.Count == 0)
                    return;
                done = _finished.Dequeue();
            }

            _inFlight.Remove(done.Chunk);

            // Unloaded while it was being generated — the player moved away.
            // The result is stale; dropping the mark lets it be asked for
            // again if they come back.
            if (!_queued.Contains(done.Chunk))
                continue;

            if (done.Solid == 0)
            {
                // Empty sky. Recorded as a uniform air chunk rather than
                // skipped, so residency knows it has been considered and the
                // rescan does not queue it again every time.
                World.Store.SetUniform(done.Chunk, NodeChunkStore.Air);
                _queued.Remove(done.Chunk);
                continue;
            }

            World.Store.Install(done.Chunk, done.Cells, done.Solid);

            // Margin chunks exist so their neighbours can be meshed; they are
            // not meshed themselves until the player comes close enough to
            // bring them inside the load radius, which the next rescan does.
            if (SquareDistance(done.Chunk, meshCentre) <= LoadRadius * LoadRadius)
                _toMesh.Add(done.Chunk);
            else
                _queued.Remove(done.Chunk);
        }
    }

    /// <summary>
    /// Does one frame's worth of generating and meshing, under both the
    /// per-phase counts and the overall time budget.
    /// </summary>
    private void Pump()
    {
        Vector3I meshCentre = _hasScanned
            ? NodeChunkStore.ChunkOf(_lastScanCell)
            : Vector3I.Zero;

        ulong deadline = Time.GetTicksMsec()
            + (ulong)Mathf.Max(1f, MillisecondsPerFrame);

        // Hand out generation work and take back what finished. Generating a
        // chunk is by far the most expensive thing the streamer does —
        // hundreds of milliseconds of density-field evaluation — and none of
        // it touches the scene, so it belongs on a worker rather than in the
        // frame. What remains on the main thread is only installing the
        // finished arrays, which is a dictionary write.
        DispatchGeneration();
        CollectGenerated(meshCentre);

        // FINISH WHAT IS ALREADY QUEUED BEFORE TAKING MORE.
        //
        // A chunk queues 64 sections and they are meshed under a time budget
        // across however many frames that takes, so the backlog has to be
        // drained before another chunk is added to it. Otherwise the queue
        // grows faster than it is served and the hitch simply arrives later.
        if (World.HasQueuedMeshes)
        {
            World.FlushQueuedMeshes(MeshMillisecondsPerFrame);
            if (!IsReady)
                CheckReady();
            return;
        }

        int meshed = 0;
        while (_toMesh.Count > 0 && meshed < MeshPerFrame)
        {
            Vector3I chunk = _toMesh[0];
            _toMesh.RemoveAt(0);
            _queued.Remove(chunk);

            // A chunk cannot be meshed until its neighbours' data exists: face
            // culling reads one cell past the boundary, and a missing
            // neighbour reads as air, which would draw an interior wall that
            // the neighbour's arrival never removes. Chunks whose neighbours
            // are still pending go back in the queue.
            if (!NeighboursReady(chunk))
            {
                _toMesh.Add(chunk);
                _queued.Add(chunk);

                // Everything left may be waiting on the same missing
                // neighbours; generating more is the way forward, not spinning
                // here.
                break;
            }

            MeshOne(chunk);
            meshed++;
            break;
        }

        // Mesh what was just queued, under the frame budget. Whatever does not
        // fit stays dirty and is picked up next frame by the branch above.
        if (World.HasQueuedMeshes)
            World.FlushQueuedMeshes(MeshMillisecondsPerFrame);

        if (!IsReady)
            CheckReady();
    }

    /// <summary>
    /// Reports whether the chunks around the target have been built, and how
    /// far along that is.
    ///
    /// Counts only chunks that hold something: a spawn surrounded by open sky
    /// would otherwise never be ready, because an empty chunk is stored as a
    /// uniform byte and never enters the mesh queue at all.
    /// </summary>
    private void CheckReady()
    {
        if (Target == null || World == null)
            return;

        Vector3I centre = NodeChunkStore.ChunkOf(World.CellAt(Target.GlobalPosition));
        int radius = Mathf.Max(0, ReadyRadius);

        int wanted = 0;
        int have = 0;

        // WALKED, not enumerated, for the same reason residency is: a chunk
        // offset is packed-index arithmetic, and on the sphere most of those
        // offsets name chunks that do not exist. Counting them left readiness
        // pinned at 33% -- one layer of the 3x3x3 was real and the other two
        // never arrived.
        if (World.Grid != null)
        {
            _readySeen.Clear();
            _readyQueue.Clear();
            _readySeen.Add(centre);
            _readyQueue.Enqueue(centre);

            while (_readyQueue.Count > 0)
            {
                Vector3I chunk = _readyQueue.Dequeue();
                if (ChunkDistance(chunk, centre) > radius)
                    continue;

                wanted++;
                if (World.IsChunkLoaded(chunk) && !_queued.Contains(chunk))
                    have++;

                for (int face = 0; face < 6; face++)
                {
                    if (!NeighbourChunk(chunk, NodeFace.Offsets[face], out Vector3I next))
                        continue;

                    if (_readySeen.Add(next))
                        _readyQueue.Enqueue(next);
                }
            }
        }
        else
        {
            for (int x = -radius; x <= radius; x++)
                for (int y = -radius; y <= radius; y++)
                    for (int z = -radius; z <= radius; z++)
                    {
                        var chunk = new Vector3I(centre.X + x, centre.Y + y, centre.Z + z);
                        wanted++;
                        if (World.IsChunkLoaded(chunk) && !_queued.Contains(chunk))
                            have++;
                    }
        }

        if (_readyTotal == 0)
            _readyTotal = wanted;

        ReadyProgress = _readyTotal == 0 ? 1f : Mathf.Clamp(have / (float)_readyTotal, 0f, 1f);

        if (have < wanted)
            return;

        IsReady = true;
        ReadyProgress = 1f;
        BecameReady?.Invoke();
    }

    /// <summary>
    /// Are all six face neighbours' data resident?
    ///
    /// Face neighbours only. Occupancy reaches two cells past a boundary,
    /// which touches edge and corner neighbours as well — but only for the
    /// outermost sub-cells of the outermost nodes, where a wrong answer costs
    /// a few surplus triangles rather than a hole. Requiring all 26 would stall
    /// the frontier badly for that.
    /// </summary>
    private bool NeighboursReady(Vector3I chunk)
    {
        if (World.Grid == null)
        {
            return World.IsChunkLoaded(chunk + Vector3I.Right)
                && World.IsChunkLoaded(chunk + Vector3I.Left)
                && World.IsChunkLoaded(chunk + Vector3I.Up)
                && World.IsChunkLoaded(chunk + Vector3I.Down)
                && World.IsChunkLoaded(chunk + Vector3I.Back)
                && World.IsChunkLoaded(chunk + Vector3I.Forward);
        }

        // Asked through the grid, so a neighbour across a face edge is the
        // chunk actually there rather than an index one step along u.
        //
        // A direction with NO neighbour does not block: past the outermost
        // shell there is only sky, and waiting for it to load left every chunk
        // meshless forever -- readiness sat at 36% with the world fully
        // generated underneath it.
        for (int face = 0; face < 6; face++)
        {
            if (!NeighbourChunk(chunk, NodeFace.Offsets[face], out Vector3I next))
                continue;

            if (!World.IsChunkLoaded(next))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Queues one chunk's sections. The actual meshing is paced by
    /// <see cref="Pump"/> under a time budget, not done here.
    /// </summary>
    private void MeshOne(Vector3I chunk)
    {
        World.QueueChunkMesh(chunk);
    }

    /// <summary>
    /// Forgets what has been scanned, so the next frame recomputes residency
    /// from scratch.
    ///
    /// For a generator whose field has changed underneath the streamer: the
    /// chunks already resident are stale in a way distance cannot detect, so
    /// the queues are dropped and everything in range is asked for again.
    /// </summary>
    public void ForceRescan()
    {
        // Anything a worker is part-way through was generated against the
        // world as it was; bumping the generation makes those results land in
        // the bin rather than in the new world.
        _generation++;

        _toGenerate.Clear();
        _toMesh.Clear();
        _queued.Clear();
        _inFlight.Clear();

        lock (_finishedLock)
            _finished.Clear();

        _hasScanned = false;
        _readyTotal = 0;
        IsReady = false;
        ReadyProgress = 0f;
    }

    /// <summary>Chunks still waiting to be generated or meshed.</summary>
    public int PendingChunks => _toGenerate.Count + _toMesh.Count;
}
