using Godot;

namespace GameBase.UI;

/// <summary>
/// Main menu screen. Set as the project's main scene (or instanced anywhere).
/// The scene it launches is exposed as GameScenePath so the module can be
/// ported to any project and pointed at that project's first gameplay scene.
/// </summary>
public partial class MainMenu : Control
{
    /// <summary>
    /// The scene Play launches.
    ///
    /// The floating-island levels are still in the project and still work —
    /// point this at Game/IslandStreamLevel.tscn (endless islands) or
    /// Game/IslandLevel.tscn (one fixed region) to go back to them.
    /// </summary>
    [Export(PropertyHint.File, "*.tscn")]
    public string GameScenePath { get; set; } = "res://Game/PlanetLevel.tscn";

    [Export] public string GameTitle { get; set; } = "GAME BASE";

    private Control _menuRoot;
    private SettingsMenu _settingsMenu;

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;

        _menuRoot = GetNode<Control>("%MenuRoot");
        _settingsMenu = GetNode<SettingsMenu>("%SettingsMenu");

        GetNode<Label>("%TitleLabel").Text = GameTitle;
        GetNode<Button>("%PlayButton").Pressed += OnPlayPressed;
        GetNode<Button>("%SettingsButton").Pressed += OnSettingsPressed;
        GetNode<Button>("%QuitButton").Pressed += () => GetTree().Quit();
        _settingsMenu.BackPressed += OnSettingsClosed;
    }

    private void OnPlayPressed()
    {
        Error err = GetTree().ChangeSceneToFile(GameScenePath);
        if (err != Error.Ok)
            GD.PushError($"MainMenu: could not load game scene '{GameScenePath}' ({err})");
    }

    private void OnSettingsPressed()
    {
        _menuRoot.Visible = false;
        _settingsMenu.Visible = true;
    }

    private void OnSettingsClosed()
    {
        _settingsMenu.Visible = false;
        _menuRoot.Visible = true;
    }
}
