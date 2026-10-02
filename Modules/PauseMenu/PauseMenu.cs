using Godot;
using GameBase.Core;
using GameBase.Terrain;

namespace GameBase.UI;

/// <summary>
/// Drop-in pause menu. Instance PauseMenu.tscn anywhere in a gameplay scene;
/// it pauses/unpauses the whole scene tree on the "pause" action (rebindable,
/// and the action name itself is exported). The CanvasLayer root uses
/// ProcessMode.Always so the menu keeps working while the tree is paused.
///
/// It shows the world's seed (see <see cref="WorldSeed"/>), so a world worth
/// keeping can be noted and typed back in on the main menu.
/// </summary>
public partial class PauseMenu : CanvasLayer
{
    [Export] public StringName PauseAction { get; set; } = "pause";

    [Export(PropertyHint.File, "*.tscn")]
    public string MainMenuScenePath { get; set; } = "res://Modules/MainMenu/MainMenu.tscn";

    /// <summary>Recapture the mouse when resuming (true for mouse-look games).</summary>
    [Export] public bool CaptureMouseOnResume { get; set; } = true;

    private Control _root;
    private Control _menuPanel;
    private SettingsMenu _settings;

    /// <summary>
    /// Suppresses the menu entirely: the pause action does nothing and, if the
    /// menu is already open, it closes.
    ///
    /// For the stretch before a scene is playable — a level still building
    /// behind a loading screen. Pausing then is meaningless (there is nothing
    /// running to pause) and actively harmful: the tree stops, so the very
    /// work the player is waiting on stops with it, and the menu's Resume
    /// hands them a world that is not there yet.
    ///
    /// A plain flag rather than a reference to whatever is loading, so the
    /// module stays drop-in: anything that knows the scene is not ready yet
    /// can set it.
    /// </summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            _suspended = value;
            // Keyed on this menu being open, not on the tree being paused:
            // another screen may have paused it, and resuming then would
            // unpause on its behalf.
            if (_suspended && _root != null && _root.Visible)
                Resume();
        }
    }

    private bool _suspended;

    public bool IsPaused => GetTree().Paused;

    public override void _Ready()
    {
        _root = GetNode<Control>("%Root");
        _menuPanel = GetNode<Control>("%MenuPanel");
        _settings = GetNode<SettingsMenu>("%SettingsMenu");
        GetNode<Label>("%SeedLabel").Text = $"Seed {WorldSeed.Current}";

        GetNode<Button>("%ResumeButton").Pressed += Resume;
        GetNode<Button>("%SettingsButton").Pressed += OpenSettings;
        GetNode<Button>("%MainMenuButton").Pressed += OnMainMenuPressed;
        GetNode<Button>("%QuitButton").Pressed += () => GetTree().Quit();
        _settings.BackPressed += CloseSettings;

        _root.Visible = false;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!@event.IsActionPressed(PauseAction))
            return;

        if (_suspended)
        {
            // Swallowed rather than ignored, so the key does not fall through
            // to whatever else might be listening while the scene loads.
            GetViewport().SetInputAsHandled();
            return;
        }

        UiStateService states = UiStateService.Instance;
        if (states != null)
        {
            // Escape belongs to whatever screen is on top. Asked of the state
            // service rather than of the tree: the tree being paused does NOT
            // mean this menu is what paused it — the inventory pauses too, and
            // reading IsPaused here made Escape-in-the-inventory look like
            // Escape-in-the-menu, so this resumed a menu that was never open
            // and left the inventory sitting there.
            bool mine = states.IsCurrent(UiState.Menu)
                || states.IsCurrent(UiState.Settings)
                || states.IsCurrent(UiState.Gameplay);

            if (!mine)
                return;
        }

        // Whether this menu is open, rather than whether the tree is paused.
        // Another screen may have paused it.
        bool menuIsOpen = _root.Visible;

        if (_settings.Visible)
            CloseSettings();
        else if (menuIsOpen)
            Resume();
        else
            Pause();

        GetViewport().SetInputAsHandled();
    }

    public void Pause()
    {
        UiStateService.Instance?.Push(UiState.Menu);
        GetTree().Paused = true;
        _root.Visible = true;
        _menuPanel.Visible = true;
        _settings.Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public void Resume()
    {
        // Closed by name rather than popped, so that resuming from the
        // Resume button — which can happen with Settings open on top — takes
        // the whole menu down instead of revealing a settings screen whose
        // parent has just left.
        UiStateService.Instance?.Close(UiState.Menu);
        GetTree().Paused = false;
        _root.Visible = false;
        if (CaptureMouseOnResume)
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private void OpenSettings()
    {
        UiStateService.Instance?.Push(UiState.Settings);
        _menuPanel.Visible = false;
        _settings.Visible = true;
    }

    private void CloseSettings()
    {
        UiStateService.Instance?.Close(UiState.Settings);
        _settings.Visible = false;
        _menuPanel.Visible = true;
    }

    private void OnMainMenuPressed()
    {
        // The stack describes a scene that is about to stop existing, so it is
        // reset rather than unwound.
        UiStateService.Instance?.Reset(UiState.MainMenu);
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        Error err = GetTree().ChangeSceneToFile(MainMenuScenePath);
        if (err != Error.Ok)
            GD.PushError($"PauseMenu: could not load main menu '{MainMenuScenePath}' ({err})");
    }
}
