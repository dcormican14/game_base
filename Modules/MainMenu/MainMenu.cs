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
    /// The scene Play launches when the picker is empty or a chosen world is
    /// missing.
    ///
    /// The floating-island levels are still in the project and still work —
    /// point this at Game/IslandStreamLevel.tscn (endless islands) or
    /// Game/IslandLevel.tscn (one fixed region) to go back to them.
    /// </summary>
    [Export(PropertyHint.File, "*.tscn")]
    public string GameScenePath { get; set; } = "res://Game/CubePlanetLevel.tscn";

    [Export] public string GameTitle { get; set; } = "GAME BASE";

    /// <summary>
    /// One selectable world.
    ///
    /// The grids are built ALONGSIDE each other rather than one replacing the
    /// other, so the only way to judge them is to stand in both. This is what
    /// makes that a choice at the menu instead of an edit to the project's main
    /// scene.
    /// </summary>
    private readonly record struct WorldChoice(string Name, string Path, string Note);

    /// <remarks>
    /// ORDER IS THE DEFAULT. The picker selects the first entry, so whichever
    /// world sits at the top is the one Play launches when nobody touches the
    /// dropdown.
    /// </remarks>
    private static readonly WorldChoice[] Worlds =
    {
        new("Organic (Voronoi)",
            "res://Game/OrganicPlanetLevel.tscn",
            "Irregular rock cells around jittered sites, walled by the "
                + "bisectors between them. No grid, no seams, about fifteen "
                + "faces a node, over three layers of topsoil."),

        new("Even nodes (icosphere)",
            "res://Game/IcoPlanetLevel.tscn",
            "Hexagonal nodes on an icosahedral grid. Even spacing, twelve "
                + "pentagons at the icosahedron's corners."),

        new("Cube planet (quad sphere)",
            "res://Game/CubePlanetLevel.tscn",
            "The original cubed-sphere grid. Square nodes at each face's "
                + "centre, skewing toward rhombuses near the eight seams."),
    };

    private Control _menuRoot;
    private SettingsMenu _settingsMenu;
    private OptionButton _sceneChoice;
    private Label _sceneNote;

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;

        _menuRoot = GetNode<Control>("%MenuRoot");
        _settingsMenu = GetNode<SettingsMenu>("%SettingsMenu");
        _sceneChoice = GetNodeOrNull<OptionButton>("%SceneChoice");
        _sceneNote = GetNodeOrNull<Label>("%SceneNote");

        BuildWorldList();

        GetNode<Label>("%TitleLabel").Text = GameTitle;
        GetNode<Button>("%PlayButton").Pressed += OnPlayPressed;
        GetNode<Button>("%SettingsButton").Pressed += OnSettingsPressed;
        GetNode<Button>("%QuitButton").Pressed += () => GetTree().Quit();
        _settingsMenu.BackPressed += OnSettingsClosed;
    }

    /// <summary>
    /// Fills the picker with the worlds this build actually has.
    ///
    /// A world whose scene is missing is left out rather than offered and
    /// failed on, so a half-finished grid in the tree cannot strand the menu at
    /// a dead entry.
    /// </summary>
    private void BuildWorldList()
    {
        if (_sceneChoice == null)
            return;

        _sceneChoice.Clear();

        for (int i = 0; i < Worlds.Length; i++)
        {
            if (!ResourceLoader.Exists(Worlds[i].Path))
                continue;

            _sceneChoice.AddItem(Worlds[i].Name, i);
        }

        if (_sceneChoice.ItemCount == 0)
        {
            _sceneChoice.Visible = false;
            if (_sceneNote != null)
                _sceneNote.Visible = false;

            return;
        }

        _sceneChoice.Selected = 0;
        _sceneChoice.ItemSelected += _ => ShowNote();
        ShowNote();
    }

    /// <summary>Describes the selected world under the picker.</summary>
    private void ShowNote()
    {
        if (_sceneNote == null || _sceneChoice == null)
            return;

        _sceneNote.Text = Selected().Note;
    }

    /// <summary>The world the picker is on, or the exported default.</summary>
    private WorldChoice Selected()
    {
        if (_sceneChoice == null || _sceneChoice.ItemCount == 0)
            return new WorldChoice("", GameScenePath, "");

        int id = _sceneChoice.GetItemId(_sceneChoice.Selected);

        return (uint)id < (uint)Worlds.Length
            ? Worlds[id]
            : new WorldChoice("", GameScenePath, "");
    }

    private void OnPlayPressed()
    {
        string path = Selected().Path;

        if (string.IsNullOrEmpty(path) || !ResourceLoader.Exists(path))
            path = GameScenePath;

        Error err = GetTree().ChangeSceneToFile(path);
        if (err != Error.Ok)
            GD.PushError($"MainMenu: could not load game scene '{path}' ({err})");
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
