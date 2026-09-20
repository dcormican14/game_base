namespace GameBase.Core;

/// <summary>
/// What the game is currently asking the player to look at.
///
/// The list is deliberately about INTENT rather than about which node is
/// visible: two screens that both happen to be a full-screen Control still
/// want different things from the player, and a state that merely said "some
/// menu is up" could not tell a loading screen (which must not pause) from a
/// pause menu (whose whole job is to pause).
/// </summary>
public enum UiState
{
    /// <summary>
    /// The player is in the world with the mouse captured. The only state in
    /// which gameplay input — looking, moving, mining, placing — is accepted.
    /// </summary>
    Gameplay,

    /// <summary>
    /// A level is still building. Distinct from every menu state because it is
    /// the one screen that must NOT pause the tree: pausing would stop the
    /// chunk work the player is waiting on, and the screen only lifts when
    /// that work reports done.
    /// </summary>
    Loading,

    /// <summary>The inventory is open.</summary>
    Inventory,

    /// <summary>The Escape menu is open.</summary>
    Menu,

    /// <summary>The settings screen, usually opened from Menu or MainMenu.</summary>
    Settings,

    /// <summary>The main menu, before any level exists.</summary>
    MainMenu,
}
