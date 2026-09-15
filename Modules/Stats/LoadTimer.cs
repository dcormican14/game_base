using Godot;
using System;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Times how long a world takes to become playable, and how it runs afterward.
///
/// Load time and frame rate are the two things a player actually feels, and
/// neither is visible in a mesh audit -- a world can be geometrically perfect
/// and still take a minute to appear and then run at one frame a second. This
/// measures both, so a change meant to make streaming faster can be shown to
/// have done it rather than assumed to have.
/// </summary>
public partial class LoadTimer : Node
{
    /// <summary>Frames to measure once the world is ready.</summary>
    [Export] public int SampleFrames { get; set; } = 240;

    /// <summary>Give up after this many frames, so a stall still reports.</summary>
    [Export] public int MaxFrames { get; set; } = 20000;

    [Export] public bool QuitWhenDone { get; set; } = true;

    private ChunkStreamer _streamer;
    private NodeWorld _world;
    private Node3D _player;

    private ulong _start;
    private ulong _ready;
    private int _frames;
    private int _sampled;
    private double _worst;
    private double _total;
    private bool _done;
    private float _lastProgress = -1f;

    /// <summary>How many section meshes exist right now.</summary>
    private static int CountMeshInstances(Node from)
    {
        int count = from is MeshInstance3D ? 1 : 0;

        foreach (Node child in from.GetChildren())
            count += CountMeshInstances(child);

        return count;
    }

    public override void _Ready()
    {
        Node scene = GetTree().CurrentScene;
        _streamer = NodeSearch.FindByType<ChunkStreamer>(scene);
        _world = NodeSearch.FindByType<NodeWorld>(scene);
        _player = scene?.GetNodeOrNull<Node3D>("Player");

        _start = Time.GetTicksMsec();
    }

    public override void _Process(double delta)
    {
        if (_done)
            return;

        _frames++;

        if (_ready == 0)
        {
            bool ready = _streamer == null || _streamer.IsReady;

            // Sample the bar the player is watching, so a progress figure that
            // sticks or jumps shows up as a number rather than as a feeling.
            if (_streamer != null && _frames % 15 == 0)
            {
                float now = _streamer.ReadyProgress;

                if (Mathf.Abs(now - _lastProgress) > 0.0001f || _frames % 120 == 0)
                {
                    GD.Print($"  PROGRESS frame {_frames,5}"
                        + $" {(Time.GetTicksMsec() - _start) / 1000.0,6:F2}s"
                        + $"  {now,6:P1}"
                        + $"  meshes {CountMeshInstances(GetTree().CurrentScene),5}");

                    _lastProgress = now;
                }
            }

            if (!ready && _frames < MaxFrames)
                return;

            _ready = Time.GetTicksMsec();

            GD.Print("=== LOAD TIMER ===");
            GD.Print($"  time to ready: {(_ready - _start) / 1000.0:F2} s"
                + (_streamer is { IsReady: false } ? "  (GAVE UP -- never became ready)" : ""));
            GD.Print($"  frames to ready: {_frames}");
            GD.Print($"  nodes {_world?.NodeCount ?? 0:N0}"
                + $"  chunks {_world?.LoadedChunks ?? 0:N0}"
                + $"  memory {(_world?.ApproximateBytes ?? 0) / 1048576.0:F1} MB");

            Probe();
            return;
        }

        // Steady-state frame timing, once the world is up.
        _sampled++;
        _total += delta;

        if (delta > _worst)
            _worst = delta;

        if (_sampled < SampleFrames)
            return;

        double mean = _total / _sampled;

        GD.Print($"  after ready, over {_sampled} frames:");
        GD.Print($"    mean  {mean * 1000.0:F1} ms  ({1.0 / Mathf.Max(mean, 0.000001):F1} fps)");
        GD.Print($"    worst {_worst * 1000.0:F1} ms  ({1.0 / Mathf.Max(_worst, 0.000001):F1} fps)");
        GD.Print("=== END LOAD TIMER ===");

        _done = true;

        if (QuitWhenDone)
            GetTree().Quit(0);
    }

    /// <summary>
    /// Times the grid calls the mesher makes, one at a time.
    ///
    /// Load time on its own says a world is slow; it does not say which call is
    /// slow. These are the four the mesher leans on per node, measured on real
    /// addresses from the streamed world so the numbers include whatever the
    /// seams and the caches actually do.
    /// </summary>
    private void Probe()
    {
        INodeGrid grid = _world?.Grid;
        if (grid == null || _player == null)
            return;

        // A patch of real nodes around the player, walked the way the mesher
        // reaches them.
        var sample = new System.Collections.Generic.List<Vector3I>();

        // Taken from just BELOW the surface under the player, not from the
        // player's own position: they stand above the ground, and a point in
        // the sky belongs to no node -- the grid says so with a negative shell,
        // which is what silently emptied this probe's first run.
        Vector3 outward = _player.GlobalPosition.Normalized();
        Vector3I at = grid.CellAt(outward * (grid.SurfaceRadius - grid.NodeSize * 0.5f));

        var seen = new System.Collections.Generic.HashSet<Vector3I>();
        var queue = new System.Collections.Generic.Queue<Vector3I>();

        if (grid.Contains(at))
        {
            queue.Enqueue(at);
            seen.Add(at);
        }

        while (queue.Count > 0 && sample.Count < 2000)
        {
            Vector3I cell = queue.Dequeue();
            sample.Add(cell);

            int walls = grid.WallCount(cell);
            for (int w = 0; w < walls; w++)
            {
                if (grid.WallNeighbour(cell, w, out Vector3I next) && seen.Add(next))
                    queue.Enqueue(next);
            }
        }

        if (sample.Count == 0)
            return;


        GD.Print($"  per-call cost over {sample.Count} real nodes:");
        Time_("CentreOf", sample, c => { grid.CentreOf(c); });
        Time_("WallCount", sample, c => { grid.WallCount(c); });
        Time_("Canonical", sample, c => { grid.Canonical(c); });
        Time_("CellAt", sample, c => { grid.CellAt(grid.CentreOf(c)); });

        // Corners need the span, so it is timed separately.
        // Allocated once, outside the loop: a stackalloc per iteration grows
        // the frame until it overflows.
        Span<Vector3> scratch = stackalloc Vector3[16];

        ulong start = Time.GetTicksUsec();
        foreach (Vector3I c in sample)
            grid.TopCorners(c, scratch);

        GD.Print($"    {"TopCorners",-12} {(Time.GetTicksUsec() - start) / (double)sample.Count:F2} us");
    }

    private static void Time_(string label,
        System.Collections.Generic.List<Vector3I> sample, System.Action<Vector3I> call)
    {
        ulong start = Time.GetTicksUsec();

        foreach (Vector3I cell in sample)
            call(cell);

        double each = (Time.GetTicksUsec() - start) / (double)sample.Count;
        GD.Print($"    {label,-12} {each:F2} us");
    }
}
