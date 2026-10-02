using Godot;
using GameBase.Core;
using GameBase.Terrain;

namespace GameBase.UI;

/// <summary>
/// Main menu: the world's seed, Play, Settings, Quit. Set as the project's main
/// scene. The scene Play launches is exported, so the module ports to another
/// project by pointing it at that project's first level.
///
/// The seed box starts with a random seed, picked once when the game launches
/// (so going back to the menu shows the world just played), and takes any
/// edit: Play builds the world from whatever it holds (see <see cref="WorldSeed"/>).
/// </summary>
public partial class MainMenu : Control
{
    [Export(PropertyHint.File, "*.tscn")]
    public string GameScenePath { get; set; } = "res://Game/PlanetLevel.tscn";

    [Export] public string GameTitle { get; set; } = "GAME BASE";

    private Control _menuRoot;
    private SettingsMenu _settingsMenu;
    private LineEdit _seedEdit;

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;

        // No level exists yet, so the stack starts here rather than at
        // Gameplay. Reset rather than push: arriving from a level leaves that
        // level's stack behind, and it describes a scene being torn down.
        UiStateService.Instance?.Reset(UiState.MainMenu);

        _menuRoot = GetNode<Control>("%MenuRoot");
        _settingsMenu = GetNode<SettingsMenu>("%SettingsMenu");

        GetNode<ColorRect>("Background").Color = Palette.Plum;

        var title = GetNode<Label>("%TitleLabel");
        title.Text = GameTitle;
        title.AddThemeColorOverride("font_color", Palette.Cream);

        WorldSeed.Chosen ??= WorldSeed.RandomText();
        _seedEdit = GetNode<LineEdit>("%SeedEdit");
        _seedEdit.Text = WorldSeed.Chosen;
        _seedEdit.TextChanged += text => WorldSeed.Chosen = text;
        GetNode<Button>("%RerollButton").Pressed += Reroll;

        GetNode<Button>("%PlayButton").Pressed += OnPlayPressed;
        GetNode<Button>("%SettingsButton").Pressed += OnSettingsPressed;
        GetNode<Button>("%QuitButton").Pressed += () => GetTree().Quit();
        _settingsMenu.BackPressed += OnSettingsClosed;
    }

    private void Reroll()
    {
        WorldSeed.Chosen = WorldSeed.RandomText();
        _seedEdit.Text = WorldSeed.Chosen;
    }

    private void OnPlayPressed()
    {
        // An emptied box means "anything": a fresh seed, shown so it can be
        // noted, rather than the same default world every time.
        if (string.IsNullOrWhiteSpace(WorldSeed.Chosen))
            Reroll();

        Error err = GetTree().ChangeSceneToFile(GameScenePath);
        if (err != Error.Ok)
            GD.PushError($"MainMenu: could not load game scene '{GameScenePath}' ({err})");
    }

    private void OnSettingsPressed()
    {
        UiStateService.Instance?.Push(UiState.Settings);
        _menuRoot.Visible = false;
        _settingsMenu.Visible = true;
    }

    private void OnSettingsClosed()
    {
        UiStateService.Instance?.Close(UiState.Settings);
        _settingsMenu.Visible = false;
        _menuRoot.Visible = true;
    }
}
