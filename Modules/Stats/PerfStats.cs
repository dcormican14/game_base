using Godot;
using GameBase.Core;
using GameBase.Player;

namespace GameBase.UI;

/// <summary>
/// Drop-in performance overlay (top-left): FPS, frame/physics times, draw
/// calls, primitives, VRAM and static memory. Instance PerfStats.tscn in any
/// scene; visibility follows SettingsService.ShowPerfStats when the autoload
/// is present (VisibleByDefault otherwise). Updates on a small interval so
/// the overlay itself costs next to nothing, and keeps processing while the
/// tree is paused.
///
/// It also shows the player's movement mode, so free-fly inspection is never
/// a hidden state — double-tapping jump by accident is otherwise confusing.
/// </summary>
public partial class PerfStats : CanvasLayer
{
    /// <summary>Seconds between text refreshes.</summary>
    [Export] public float UpdateInterval { get; set; } = 0.25f;
    [Export] public bool VisibleByDefault { get; set; }

    /// <summary>The player whose mode is reported. Found by search when left
    /// empty, so the overlay drops into a scene without wiring.</summary>
    [Export] public NodePath PlayerPath { get; set; } = "";

    private Label _label;
    private double _accumulator;
    private PlayerController _player;

    public override void _Ready()
    {
        _label = GetNode<Label>("%StatsLabel");

        _player = !PlayerPath.IsEmpty ? GetNodeOrNull<PlayerController>(PlayerPath) : null;
        _player ??= FindPlayer(GetTree().CurrentScene ?? GetParent());

        if (_player != null)
            _player.SandboxChanged += OnSandboxChanged;

        if (SettingsService.Instance != null)
            SettingsService.Instance.SettingsChanged += Apply;
        Apply();
    }

    private void OnSandboxChanged(bool sandbox) => Apply();

    private static PlayerController FindPlayer(Node root)
    {
        if (root == null)
            return null;
        if (root is PlayerController match)
            return match;
        foreach (Node child in root.GetChildren())
        {
            PlayerController found = FindPlayer(child);
            if (found != null)
                return found;
        }

        return null;
    }

    public override void _ExitTree()
    {
        if (_player != null)
            _player.SandboxChanged -= OnSandboxChanged;
        if (SettingsService.Instance != null)
            SettingsService.Instance.SettingsChanged -= Apply;
    }

    private void Apply()
    {
        // Sandbox forces the overlay on regardless of the setting: the mode is
        // toggled by a double-tap that is easy to hit by accident, and the
        // overlay is off by default, so otherwise the player would be flying
        // through walls with nothing on screen explaining why.
        bool wanted = SettingsService.Instance?.ShowPerfStats ?? VisibleByDefault;
        Visible = wanted || (_player?.IsSandbox ?? false);
        SetProcess(Visible);
        _accumulator = UpdateInterval; // refresh immediately on show
    }

    public override void _Process(double delta)
    {
        _accumulator += delta;
        if (_accumulator < UpdateInterval)
            return;
        _accumulator = 0;

        double fps = Performance.GetMonitor(Performance.Monitor.TimeFps);
        double frameMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
        double physicsMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
        double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double primitives = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        double vram = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed);
        double memory = Performance.GetMonitor(Performance.Monitor.MemoryStatic);

        _label.Text =
            $"FPS {fps:0}  ({frameMs:0.0} ms)\n" +
            $"Physics {physicsMs:0.0} ms\n" +
            $"Draw calls {drawCalls:0}\n" +
            $"Tris {FormatCount(primitives)}\n" +
            $"VRAM {vram / (1024.0 * 1024.0):0} MB  |  Mem {memory / (1024.0 * 1024.0):0} MB";
    }

    private static string FormatCount(double value)
    {
        if (value >= 1_000_000)
            return $"{value / 1_000_000.0:0.0}M";
        if (value >= 1_000)
            return $"{value / 1_000.0:0.0}K";
        return $"{value:0}";
    }
}
