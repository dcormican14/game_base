using Godot;
using System.Collections.Generic;
using GameBase.Core;

namespace GameBase.UI;

/// <summary>
/// Reusable settings screen: video, audio, mouse sensitivity and a keybind
/// list generated dynamically from the InputMap (every rebindable action gets
/// a row — no per-action code). Instance it hidden inside any menu, show it,
/// and listen for BackPressed to close it.
///
/// Rebinding: click a binding button, then press a key / mouse button /
/// gamepad button. Escape cancels the capture.
/// </summary>
public partial class SettingsMenu : Control
{
    [Signal]
    public delegate void BackPressedEventHandler();

    [Export] public string ListeningText { get; set; } = "Press a key…";

    private CheckButton _fullscreenCheck;
    private CheckButton _vsyncCheck;
    private CheckButton _thirdPersonCheck;
    private CheckButton _outlineCheck;
    private CheckButton _pixelateCheck;
    private CheckButton _ditherCheck;
    private CheckButton _perfStatsCheck;
    private HSlider _volumeSlider;
    private HSlider _sensitivitySlider;
    private HSlider _crosshairLinesSlider;
    private Label _crosshairLinesValue;
    private VBoxContainer _keybindList;

    private readonly Dictionary<StringName, Button> _bindButtons = new();
    private StringName _listeningAction;

    private static SettingsService Svc => SettingsService.Instance;

    public override void _Ready()
    {
        _fullscreenCheck = GetNode<CheckButton>("%FullscreenCheck");
        _vsyncCheck = GetNode<CheckButton>("%VSyncCheck");
        _thirdPersonCheck = GetNode<CheckButton>("%ThirdPersonCheck");
        _outlineCheck = GetNode<CheckButton>("%OutlineCheck");
        _pixelateCheck = GetNode<CheckButton>("%PixelateCheck");
        _ditherCheck = GetNode<CheckButton>("%DitherCheck");
        _perfStatsCheck = GetNode<CheckButton>("%PerfStatsCheck");
        _volumeSlider = GetNode<HSlider>("%MasterVolumeSlider");
        _sensitivitySlider = GetNode<HSlider>("%MouseSensitivitySlider");
        _crosshairLinesSlider = GetNode<HSlider>("%CrosshairLinesSlider");
        _crosshairLinesValue = GetNode<Label>("%CrosshairLinesValue");
        _keybindList = GetNode<VBoxContainer>("%KeybindList");

        _fullscreenCheck.Toggled += on => { if (Svc != null) Svc.Fullscreen = on; };
        _vsyncCheck.Toggled += on => { if (Svc != null) Svc.VSync = on; };
        _thirdPersonCheck.Toggled += on => { if (Svc != null) Svc.ThirdPerson = on; };
        _outlineCheck.Toggled += on => { if (Svc != null) Svc.OutlineFilter = on; };
        _pixelateCheck.Toggled += on => { if (Svc != null) Svc.PixelateFilter = on; };
        _ditherCheck.Toggled += on => { if (Svc != null) Svc.DitherFilter = on; };
        _perfStatsCheck.Toggled += on => { if (Svc != null) Svc.ShowPerfStats = on; };
        _volumeSlider.ValueChanged += v => { if (Svc != null) Svc.MasterVolume = (float)v; };
        _sensitivitySlider.ValueChanged += v => { if (Svc != null) Svc.MouseSensitivity = (float)v; };
        _crosshairLinesSlider.ValueChanged += v =>
        {
            if (Svc != null)
                Svc.CrosshairLines = (int)v;
            _crosshairLinesValue.Text = ((int)v).ToString();
        };
        GetNode<Button>("%ResetKeybindsButton").Pressed += OnResetKeybinds;
        GetNode<Button>("%BackButton").Pressed += () => EmitSignal(SignalName.BackPressed);
        VisibilityChanged += () => { if (Visible) RefreshAll(); };

        if (Svc == null)
            GD.PushWarning("SettingsMenu: SettingsService autoload not found — values will not persist.");

        RefreshAll();
    }

    /// <summary>Captures the next key/mouse/gamepad press while a rebind is in progress.</summary>
    public override void _Input(InputEvent @event)
    {
        if (!Visible || _listeningAction == null)
            return;

        switch (@event)
        {
            case InputEventKey key when key.Pressed && !key.Echo:
                if (key.Keycode == Key.Escape || key.PhysicalKeycode == Key.Escape)
                    CancelListening();
                else
                    ApplyBinding(CleanKeyEvent(key));
                GetViewport().SetInputAsHandled();
                break;

            case InputEventMouseButton mouse when mouse.Pressed:
                ApplyBinding(new InputEventMouseButton { ButtonIndex = mouse.ButtonIndex });
                GetViewport().SetInputAsHandled();
                break;

            case InputEventJoypadButton joy when joy.Pressed:
                ApplyBinding(new InputEventJoypadButton { ButtonIndex = joy.ButtonIndex });
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private static InputEventKey CleanKeyEvent(InputEventKey source)
    {
        // Prefer the physical key so bindings survive keyboard-layout changes.
        return source.PhysicalKeycode != Key.None
            ? new InputEventKey { PhysicalKeycode = source.PhysicalKeycode }
            : new InputEventKey { Keycode = source.Keycode };
    }

    // ---------------------------------------------------------------- keybinds

    private void RebuildKeybindList()
    {
        CancelListening();
        foreach (Node child in _keybindList.GetChildren())
            child.QueueFree();
        _bindButtons.Clear();

        if (Svc == null)
            return;

        foreach (StringName action in Svc.GetRebindableActions())
        {
            var row = new HBoxContainer();
            var label = new Label
            {
                Text = Prettify(action.ToString()),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(200, 0),
            };
            var button = new Button
            {
                Text = Svc.GetBindingLabel(action),
                CustomMinimumSize = new Vector2(170, 0),
            };

            StringName captured = action;
            button.Pressed += () => StartListening(captured);

            row.AddChild(label);
            row.AddChild(button);
            _keybindList.AddChild(row);
            _bindButtons[action] = button;
        }
    }

    private void StartListening(StringName action)
    {
        CancelListening();
        _listeningAction = action;
        _bindButtons[action].Text = ListeningText;
    }

    private void CancelListening()
    {
        if (_listeningAction != null && _bindButtons.TryGetValue(_listeningAction, out Button button))
            button.Text = Svc?.GetBindingLabel(_listeningAction) ?? "?";
        _listeningAction = null;
    }

    private void ApplyBinding(InputEvent newEvent)
    {
        Svc?.RebindAction(_listeningAction, newEvent);
        _listeningAction = null;
        RefreshBindLabels();
    }

    private void OnResetKeybinds()
    {
        CancelListening();
        Svc?.ResetKeybinds();
        RefreshBindLabels();
    }

    private void RefreshBindLabels()
    {
        if (Svc == null)
            return;
        foreach (var pair in _bindButtons)
            pair.Value.Text = Svc.GetBindingLabel(pair.Key);
    }

    // ----------------------------------------------------------------- refresh

    private void RefreshAll()
    {
        if (Svc != null)
        {
            _fullscreenCheck.SetPressedNoSignal(Svc.Fullscreen);
            _vsyncCheck.SetPressedNoSignal(Svc.VSync);
            _thirdPersonCheck.SetPressedNoSignal(Svc.ThirdPerson);
            _outlineCheck.SetPressedNoSignal(Svc.OutlineFilter);
            _pixelateCheck.SetPressedNoSignal(Svc.PixelateFilter);
            _ditherCheck.SetPressedNoSignal(Svc.DitherFilter);
            _perfStatsCheck.SetPressedNoSignal(Svc.ShowPerfStats);
            _volumeSlider.SetValueNoSignal(Svc.MasterVolume);
            _sensitivitySlider.SetValueNoSignal(Svc.MouseSensitivity);
            _crosshairLinesSlider.SetValueNoSignal(Svc.CrosshairLines);
            _crosshairLinesValue.Text = Svc.CrosshairLines.ToString();
        }

        RebuildKeybindList();
    }

    private static string Prettify(string actionName)
    {
        string[] words = actionName.Split('_', System.StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        return string.Join(' ', words);
    }
}
