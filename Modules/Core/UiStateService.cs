using Godot;
using System.Collections.Generic;

namespace GameBase.Core;

/// <summary>
/// Autoload that owns what the game is currently showing, and therefore who is
/// allowed to act on input.
///
/// Registered in project.godot under [autoload] as UiStateService. Access it
/// anywhere with UiStateService.Instance.
///
/// WHY THIS EXISTS. Before it, every screen decided for itself whether it
/// should react, and the only thing they could all see was
/// <c>Input.MouseMode</c> — so mouse capture became an accidental state flag,
/// checked by the player controller and the node editor to mean "no menu is
/// up". That works until two screens want the same key. Escape belonged to the
/// pause menu, and the inventory could not close on Escape without the pause
/// menu also opening behind it, because both listened on _UnhandledInput and
/// CanvasLayer order — not intent — decided who heard it first.
///
/// So the state is stated once, here, and the screens ask.
///
/// A STACK, not a single value: screens nest. Settings opens from inside the
/// pause menu, which is itself over gameplay, and closing it should reveal the
/// menu rather than teleport the player back to the world. The stack remembers
/// what to go back to without every screen having to store a return path.
///
/// This service REPORTS; it does not impose. It does not touch
/// <c>Input.MouseMode</c> or <c>SceneTree.Paused</c> — the screens still own
/// their own presentation, because a screen knows things about itself (a fade
/// still running, a world still streaming) that a general state machine should
/// not have to model. What the service guarantees is that they all agree on
/// what is happening.
/// </summary>
public partial class UiStateService : Node
{
    public static UiStateService Instance { get; private set; }

    /// <summary>
    /// Emitted after any push or pop, with the state now on top. Screens that
    /// need to react to something else opening — hiding a HUD element, say —
    /// listen here rather than polling.
    /// </summary>
    [Signal]
    public delegate void StateChangedEventHandler(UiState current);

    /// <summary>
    /// The state stack, innermost last. Never empty: <see cref="Gameplay"/>
    /// sits at the bottom so there is always something to fall back to, and so
    /// no caller has to handle "nothing is active".
    /// </summary>
    private readonly List<UiState> _stack = new() { UiState.Gameplay };

    /// <summary>What the player is looking at now.</summary>
    public UiState Current => _stack[^1];

    /// <summary>How deep the stack is. 1 means only the base state.</summary>
    public int Depth => _stack.Count;

    /// <summary>
    /// True when the world should be taking input: the player is in it, and
    /// nothing is layered over the top.
    ///
    /// This is the check gameplay code wants —
    /// the player looking around, a tool mining or placing
    /// — in place of asking whether the mouse happens to be captured.
    /// </summary>
    public bool GameplayHasInput => Current == UiState.Gameplay;

    /// <summary>True when <paramref name="state"/> is anywhere on the stack.</summary>
    public bool IsActive(UiState state) => _stack.Contains(state);

    /// <summary>True when <paramref name="state"/> is the one on top.</summary>
    public bool IsCurrent(UiState state) => Current == state;

    public override void _EnterTree() => Instance = this;

    /// <summary>
    /// Merges the palette theme into the engine's default theme, so every
    /// screen in the game is drawn from <see cref="Palette"/> without styling
    /// itself. Into the DEFAULT theme rather than onto the root window: a theme
    /// on the window only reaches controls parented to controls, and every HUD
    /// here hangs off a CanvasLayer.
    /// </summary>
    public override void _Ready() => ThemeDB.GetDefaultTheme().MergeWith(PaletteTheme.Create());

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    /// <summary>
    /// Opens <paramref name="state"/> over whatever is showing.
    ///
    /// Pushing a state that is already on the stack is refused rather than
    /// duplicated: a screen that somehow opens twice would otherwise need
    /// closing twice, and the second Escape would look like it did nothing.
    /// </summary>
    /// <param name="warnIfOpen">
    /// Whether a refused push is worth complaining about. True for real
    /// screens, where opening twice means a wiring fault; false for callers
    /// that are deliberately testing the guard.
    /// </param>
    public void Push(UiState state, bool warnIfOpen = true)
    {
        if (_stack.Contains(state))
        {
            if (warnIfOpen)
                GD.PushWarning($"UiStateService: {state} is already open; ignoring the second push.");
            return;
        }

        _stack.Add(state);
        EmitSignal(SignalName.StateChanged, (int)Current);
    }

    /// <summary>
    /// Closes the top state and returns what is revealed underneath.
    ///
    /// The base state is never popped, so the stack cannot be emptied by an
    /// extra Escape.
    /// </summary>
    public UiState Pop()
    {
        if (_stack.Count <= 1)
            return Current;

        _stack.RemoveAt(_stack.Count - 1);
        EmitSignal(SignalName.StateChanged, (int)Current);
        return Current;
    }

    /// <summary>
    /// Closes <paramref name="state"/> specifically, wherever it sits.
    ///
    /// For a screen that closes itself by its own button rather than by
    /// Escape. Anything opened ON TOP of it goes too — closing a window out
    /// from under the one covering it would leave the player looking at a
    /// screen whose parent is gone.
    /// </summary>
    public void Close(UiState state)
    {
        int index = _stack.IndexOf(state);
        if (index <= 0)
            return;

        _stack.RemoveRange(index, _stack.Count - index);
        EmitSignal(SignalName.StateChanged, (int)Current);
    }

    /// <summary>
    /// Drops everything back to <paramref name="baseState"/>.
    ///
    /// For a hard transition where the stack has no meaning any more — leaving
    /// a level for the main menu, where the pause menu that was open belongs
    /// to a scene that no longer exists.
    /// </summary>
    public void Reset(UiState baseState)
    {
        _stack.Clear();
        _stack.Add(baseState);
        EmitSignal(SignalName.StateChanged, (int)Current);
    }

    /// <summary>
    /// Decides who gets to act on a press of Escape.
    ///
    /// THE ARBITRATION LIVES HERE rather than in the screens, because it is
    /// the one decision none of them can make alone. Every screen that listens
    /// for Escape would otherwise have to know about every other screen that
    /// might also want it, and be ordered correctly against them by
    /// CanvasLayer number — which is how the inventory and the pause menu
    /// ended up fighting over it.
    ///
    /// The rule is simply: the state on top gets it. A screen asks whether it
    /// is the one being addressed, and only then acts.
    /// </summary>
    public bool WantsEscape(UiState asker) => Current == asker;
}
