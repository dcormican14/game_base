using Godot;
using System.Collections.Generic;

namespace GameBase.Core;

/// <summary>
/// Autoload singleton that owns persistent user settings: video, audio, mouse
/// sensitivity and dynamic keybinds. Registered in project.godot under
/// [autoload] as SettingsService (via SettingsService.tscn so its exports are
/// editable in the editor). Access it anywhere with SettingsService.Instance.
///
/// Keybinds are discovered dynamically from the InputMap: every action that
/// does not start with one of NonRebindablePrefixes is treated as rebindable.
/// Overrides are persisted to SettingsFilePath and re-applied on startup, so
/// porting this module to another project needs no per-action code.
/// </summary>
public partial class SettingsService : Node
{
    public static SettingsService Instance { get; private set; }

    /// <summary>Emitted after any setting changes and has been applied + saved.</summary>
    [Signal]
    public delegate void SettingsChangedEventHandler();

    [Export] public string SettingsFilePath { get; set; } = "user://settings.cfg";

    /// <summary>Actions starting with any of these prefixes are hidden from rebinding UI.</summary>
    [Export] public string[] NonRebindablePrefixes { get; set; } = { "ui_" };

    [ExportGroup("Defaults")]
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float DefaultMouseSensitivity { get; set; } = 0.15f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float DefaultMasterVolume { get; set; } = 0.8f;
    [Export] public bool DefaultFullscreen { get; set; }
    [Export] public bool DefaultVSync { get; set; } = true;
    [Export] public bool DefaultThirdPerson { get; set; } = true;

    private readonly ConfigFile _config = new();
    private readonly Dictionary<StringName, InputEvent[]> _defaultBindings = new();

    private float _mouseSensitivity;
    private float _masterVolume;
    private bool _fullscreen;
    private bool _vsync;
    private bool _thirdPerson;

    /// <summary>Degrees of camera rotation per pixel of mouse movement.</summary>
    public float MouseSensitivity
    {
        get => _mouseSensitivity;
        set { _mouseSensitivity = Mathf.Clamp(value, 0.001f, 2f); SaveAndNotify(); }
    }

    /// <summary>Master bus volume, linear 0..1.</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set { _masterVolume = Mathf.Clamp(value, 0f, 1f); ApplyVolume(); SaveAndNotify(); }
    }

    public bool Fullscreen
    {
        get => _fullscreen;
        set { _fullscreen = value; ApplyWindowMode(); SaveAndNotify(); }
    }

    public bool VSync
    {
        get => _vsync;
        set { _vsync = value; ApplyVSync(); SaveAndNotify(); }
    }

    /// <summary>Third-person camera when true, first-person when false.
    /// Consumers (e.g. PlayerController) react via the SettingsChanged signal.</summary>
    public bool ThirdPerson
    {
        get => _thirdPerson;
        set { _thirdPerson = value; SaveAndNotify(); }
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    public override void _Ready()
    {
        CaptureDefaultBindings();
        LoadFromDisk();
        ApplyAll();
    }

    // ---------------------------------------------------------------- keybinds

    /// <summary>All InputMap actions the player is allowed to rebind.</summary>
    public List<StringName> GetRebindableActions()
    {
        var result = new List<StringName>();
        foreach (StringName action in InputMap.GetActions())
        {
            string name = action.ToString();
            bool excluded = false;
            foreach (string prefix in NonRebindablePrefixes)
            {
                if (name.StartsWith(prefix))
                {
                    excluded = true;
                    break;
                }
            }

            if (!excluded)
                result.Add(action);
        }

        return result;
    }

    /// <summary>
    /// Replaces the bindings of <paramref name="action"/> with <paramref name="newEvent"/>.
    /// When <paramref name="stealFromOtherActions"/> is true, the same physical input is
    /// removed from every other rebindable action so a key can never trigger two actions.
    /// </summary>
    public void RebindAction(StringName action, InputEvent newEvent, bool stealFromOtherActions = true)
    {
        if (!InputMap.HasAction(action) || newEvent == null)
            return;

        string signature = SerializeEvent(newEvent);
        if (stealFromOtherActions && signature != null)
        {
            foreach (StringName other in GetRebindableActions())
            {
                if (other == action)
                    continue;
                foreach (InputEvent existing in InputMap.ActionGetEvents(other))
                {
                    if (SerializeEvent(existing) == signature)
                        InputMap.ActionEraseEvent(other, existing);
                }
            }
        }

        InputMap.ActionEraseEvents(action);
        InputMap.ActionAddEvent(action, newEvent);
        SaveAndNotify();
    }

    /// <summary>Restores every rebindable action to the bindings defined in project.godot.</summary>
    public void ResetKeybinds()
    {
        foreach (var pair in _defaultBindings)
        {
            if (!InputMap.HasAction(pair.Key))
                continue;
            InputMap.ActionEraseEvents(pair.Key);
            foreach (InputEvent e in pair.Value)
                InputMap.ActionAddEvent(pair.Key, (InputEvent)e.Duplicate());
        }

        SaveAndNotify();
    }

    /// <summary>Short human-readable label for an action's current first binding.</summary>
    public string GetBindingLabel(StringName action)
    {
        var events = InputMap.ActionGetEvents(action);
        return events.Count == 0 ? "Unbound" : DescribeEvent(events[0]);
    }

    public static string DescribeEvent(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey key:
            {
                Key keycode = key.Keycode;
                if (key.PhysicalKeycode != Key.None)
                    keycode = DisplayServer.KeyboardGetKeycodeFromPhysical(key.PhysicalKeycode);
                string text = OS.GetKeycodeString(keycode);
                return string.IsNullOrEmpty(text) ? key.AsText() : text;
            }
            case InputEventMouseButton mouse:
                return $"Mouse {mouse.ButtonIndex}";
            case InputEventJoypadButton joy:
                return $"Gamepad {joy.ButtonIndex}";
            default:
                return e.AsText();
        }
    }

    /// <summary>Serializes a supported InputEvent to a compact config-file string, or null.</summary>
    public static string SerializeEvent(InputEvent e) => e switch
    {
        InputEventKey k when k.PhysicalKeycode != Key.None => $"pkey:{(long)k.PhysicalKeycode}",
        InputEventKey k => $"key:{(long)k.Keycode}",
        InputEventMouseButton m => $"mouse:{(long)m.ButtonIndex}",
        InputEventJoypadButton j => $"joy:{(long)j.ButtonIndex}",
        _ => null,
    };

    public static InputEvent DeserializeEvent(string s)
    {
        int sep = s?.IndexOf(':') ?? -1;
        if (sep <= 0 || !long.TryParse(s[(sep + 1)..], out long code))
            return null;

        return s[..sep] switch
        {
            "pkey" => new InputEventKey { PhysicalKeycode = (Key)code },
            "key" => new InputEventKey { Keycode = (Key)code },
            "mouse" => new InputEventMouseButton { ButtonIndex = (MouseButton)code },
            "joy" => new InputEventJoypadButton { ButtonIndex = (JoyButton)code },
            _ => null,
        };
    }

    private void CaptureDefaultBindings()
    {
        foreach (StringName action in GetRebindableActions())
        {
            var events = InputMap.ActionGetEvents(action);
            var copy = new InputEvent[events.Count];
            for (int i = 0; i < events.Count; i++)
                copy[i] = (InputEvent)events[i].Duplicate();
            _defaultBindings[action] = copy;
        }
    }

    // ------------------------------------------------------------- persistence

    private void LoadFromDisk()
    {
        _mouseSensitivity = DefaultMouseSensitivity;
        _masterVolume = DefaultMasterVolume;
        _fullscreen = DefaultFullscreen;
        _vsync = DefaultVSync;
        _thirdPerson = DefaultThirdPerson;

        if (_config.Load(SettingsFilePath) != Error.Ok)
            return;

        _mouseSensitivity = _config.GetValue("controls", "mouse_sensitivity", DefaultMouseSensitivity).AsSingle();
        _masterVolume = _config.GetValue("audio", "master_volume", DefaultMasterVolume).AsSingle();
        _fullscreen = _config.GetValue("video", "fullscreen", DefaultFullscreen).AsBool();
        _vsync = _config.GetValue("video", "vsync", DefaultVSync).AsBool();
        _thirdPerson = _config.GetValue("video", "third_person", DefaultThirdPerson).AsBool();

        if (!_config.HasSection("keybinds"))
            return;

        foreach (string actionName in _config.GetSectionKeys("keybinds"))
        {
            var action = new StringName(actionName);
            if (!InputMap.HasAction(action))
                continue;

            string[] serialized = _config.GetValue("keybinds", actionName, System.Array.Empty<string>()).AsStringArray();
            InputMap.ActionEraseEvents(action);
            foreach (string s in serialized)
            {
                InputEvent e = DeserializeEvent(s);
                if (e != null)
                    InputMap.ActionAddEvent(action, e);
            }
        }
    }

    public void SaveToDisk()
    {
        _config.SetValue("controls", "mouse_sensitivity", _mouseSensitivity);
        _config.SetValue("audio", "master_volume", _masterVolume);
        _config.SetValue("video", "fullscreen", _fullscreen);
        _config.SetValue("video", "vsync", _vsync);
        _config.SetValue("video", "third_person", _thirdPerson);

        foreach (StringName action in GetRebindableActions())
        {
            var serialized = new List<string>();
            foreach (InputEvent e in InputMap.ActionGetEvents(action))
            {
                string s = SerializeEvent(e);
                if (s != null)
                    serialized.Add(s);
            }

            _config.SetValue("keybinds", action.ToString(), serialized.ToArray());
        }

        Error err = _config.Save(SettingsFilePath);
        if (err != Error.Ok)
            GD.PushError($"SettingsService: failed to save '{SettingsFilePath}' ({err})");
    }

    // ------------------------------------------------------------------- apply

    private void ApplyAll()
    {
        ApplyVolume();
        ApplyWindowMode();
        ApplyVSync();
    }

    private void ApplyVolume()
    {
        int bus = AudioServer.GetBusIndex("Master");
        if (bus < 0)
            return;

        bool muted = _masterVolume <= 0.0001f;
        AudioServer.SetBusMute(bus, muted);
        if (!muted)
            AudioServer.SetBusVolumeDb(bus, Mathf.LinearToDb(_masterVolume));
    }

    private void ApplyWindowMode()
    {
        DisplayServer.WindowSetMode(_fullscreen
            ? DisplayServer.WindowMode.Fullscreen
            : DisplayServer.WindowMode.Windowed);
    }

    private void ApplyVSync()
    {
        DisplayServer.WindowSetVsyncMode(_vsync
            ? DisplayServer.VSyncMode.Enabled
            : DisplayServer.VSyncMode.Disabled);
    }

    private void SaveAndNotify()
    {
        SaveToDisk();
        EmitSignal(SignalName.SettingsChanged);
    }
}
