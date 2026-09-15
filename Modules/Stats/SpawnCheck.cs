using Godot;
using GameBase.Nodes;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Watches the player from the first frame and fails if they ever leave the
/// surface.
///
/// This exists because the spawn has regressed twice: at _Ready no chunk has
/// been generated, let alone meshed, so there is no collision anywhere, and a
/// player given physics then accelerates under radial gravity straight through
/// the planet and out the far side. It is invisible in a headless smoke test --
/// the scene loads fine and exits zero -- so the only way to catch it is to
/// trace the radius over time and assert it never falls.
///
/// Keep this runnable after any change to NodeWorld, the streamer, or the spawn.
/// </summary>
public partial class SpawnCheck : Node
{
    /// <summary>How long to watch, in frames.</summary>
    [Export] public int Frames { get; set; } = 600;

    /// <summary>How far below the surface counts as having fallen through, in
    /// blocks. Generous: settling onto the ground dips a fraction of a block,
    /// and a band of slack keeps this from failing on landing.</summary>
    [Export] public float Tolerance { get; set; } = 4f;

    private NodeWorld _world;
    private Node3D _player;
    private ChunkStreamer _streamer;

    private int _frames;
    private float _lowest = float.MaxValue;
    private int _lowestFrame;
    private bool _everReady;
    private int _readyFrame = -1;

    public override void _Ready()
    {
        Node scene = GetTree().CurrentScene;
        _world = NodeSearch.FindByType<NodeWorld>(scene);
        _player = scene?.GetNodeOrNull<Node3D>("Player");
        _streamer = NodeSearch.FindByType<ChunkStreamer>(scene);
    }

    public override void _Process(double delta)
    {
        _frames++;

        if (_player == null || _world?.Grid == null)
            return;

        bool ready = _streamer?.IsReady ?? true;
        if (ready && !_everReady)
        {
            _everReady = true;
            _readyFrame = _frames;
        }

        float radius = _player.GlobalPosition.Length();

        if (radius < _lowest)
        {
            _lowest = radius;
            _lowestFrame = _frames;
        }

        if (_frames <= 10 || _frames % 100 == 0)
            GD.Print($"  frame {_frames,4}: radius {radius:F2} ready={ready}");

        if (_frames < Frames)
            return;

        Report(radius);
        GetTree().Quit(_lowest >= _world.Grid.SurfaceRadius - Tolerance ? 0 : 1);
    }

    private void Report(float radius)
    {
        float surface = _world.Grid.SurfaceRadius;

        GD.Print("=== SPAWN CHECK ===");
        GD.Print($"  surface radius        {surface:F2}");
        GD.Print($"  world became ready at frame {_readyFrame}");
        GD.Print($"  lowest radius reached {_lowest:F2} (frame {_lowestFrame})");
        GD.Print($"  final radius          {radius:F2}");

        bool held = _lowest >= surface - Tolerance;

        GD.Print(held
            ? "  [PASS] the player never fell through the planet"
            : $"  [FAIL] the player fell {surface - _lowest:F1} blocks below the surface");

        GD.Print("=== END SPAWN CHECK ===");
    }
}
