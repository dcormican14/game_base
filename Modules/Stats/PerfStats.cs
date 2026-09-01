using Godot;
using GameBase.Core;

namespace GameBase.UI;

/// <summary>
/// Drop-in performance overlay (top-left): FPS, frame/physics times, draw
/// calls, primitives, VRAM and static memory. Instance PerfStats.tscn in any
/// scene; visibility follows SettingsService.ShowPerfStats when the autoload
/// is present (VisibleByDefault otherwise). Updates on a small interval so
/// the overlay itself costs next to nothing, and keeps processing while the
/// tree is paused.
/// </summary>
public partial class PerfStats : CanvasLayer
{
    /// <summary>Seconds between text refreshes.</summary>
    [Export] public float UpdateInterval { get; set; } = 0.25f;
    [Export] public bool VisibleByDefault { get; set; }

    private Label _label;
    private double _accumulator;

    public override void _Ready()
    {
        _label = GetNode<Label>("%StatsLabel");
        if (SettingsService.Instance != null)
            SettingsService.Instance.SettingsChanged += Apply;
        Apply();
    }

    public override void _ExitTree()
    {
        if (SettingsService.Instance != null)
            SettingsService.Instance.SettingsChanged -= Apply;
    }

    private void Apply()
    {
        Visible = SettingsService.Instance?.ShowPerfStats ?? VisibleByDefault;
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
