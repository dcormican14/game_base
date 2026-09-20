using Godot;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Exercises the UI state stack directly, without screens attached.
///
/// The stack is what decides who gets a key press, so a fault in it does not
/// look like a fault in the stack — it looks like the pause menu opening
/// behind the inventory, or the player turning while a menu is up. Those are
/// hard to attribute and easy to "fix" in the wrong module, so the rules are
/// pinned down here instead.
/// </summary>
public partial class UiStateCheck : Node
{
    private int _failures;

    public override void _Ready()
    {
        UiStateService states = UiStateService.Instance;
        if (states == null)
        {
            GD.PrintErr("UiStateCheck: UiStateService autoload is missing.");
            GetTree().Quit(1);
            return;
        }

        CheckStartsInGameplay(states);
        CheckPushPop(states);
        CheckNesting(states);
        CheckEscapeArbitration(states);
        CheckCloseTakesChildren(states);
        CheckBaseSurvivesOverPop(states);
        CheckDoublePushRefused(states);
        CheckReset(states);

        GD.Print(_failures == 0
            ? "UiStateCheck: PASS"
            : $"UiStateCheck: FAIL ({_failures} problem(s))");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Fail(string message)
    {
        _failures++;
        GD.PrintErr($"  FAIL {message}");
    }

    private void CheckStartsInGameplay(UiStateService s)
    {
        s.Reset(UiState.Gameplay);
        if (!s.GameplayHasInput)
            Fail("gameplay does not have input at rest");
        else
            GD.Print("  base: gameplay has input when nothing is open");
    }

    private void CheckPushPop(UiStateService s)
    {
        s.Reset(UiState.Gameplay);
        s.Push(UiState.Inventory);

        if (s.Current != UiState.Inventory)
            Fail($"after pushing Inventory the current state is {s.Current}");

        // The whole point of the service: gameplay must go quiet while a
        // screen is up, without the screen having to tell it so.
        if (s.GameplayHasInput)
            Fail("gameplay still has input with the inventory open");

        s.Pop();
        if (s.Current != UiState.Gameplay || !s.GameplayHasInput)
            Fail("closing the inventory did not hand input back to gameplay");
        else
            GD.Print("  push/pop: gameplay goes quiet under a screen and recovers");
    }

    /// <summary>Settings over the menu over gameplay unwinds one at a time.</summary>
    private void CheckNesting(UiStateService s)
    {
        s.Reset(UiState.Gameplay);
        s.Push(UiState.Menu);
        s.Push(UiState.Settings);

        if (s.Depth != 3)
            Fail($"nested stack is {s.Depth} deep, expected 3");

        if (s.Pop() != UiState.Menu)
            Fail("closing settings did not reveal the menu underneath");

        if (s.Pop() != UiState.Gameplay)
            Fail("closing the menu did not return to gameplay");
        else
            GD.Print("  nesting: settings -> menu -> gameplay unwinds in order");
    }

    /// <summary>
    /// The rule that fixes the original collision: with the inventory open,
    /// Escape belongs to the inventory and NOT to the pause menu.
    /// </summary>
    private void CheckEscapeArbitration(UiStateService s)
    {
        s.Reset(UiState.Gameplay);
        s.Push(UiState.Inventory);

        if (!s.WantsEscape(UiState.Inventory))
            Fail("the inventory does not get Escape while it is on top");
        if (s.WantsEscape(UiState.Menu))
            Fail("the pause menu would ALSO act on Escape behind the inventory");

        s.Pop();

        // And with nothing open, Escape is the pause menu's again.
        if (s.WantsEscape(UiState.Inventory))
            Fail("the inventory would act on Escape while closed");
        if (!s.WantsEscape(UiState.Gameplay))
            Fail("gameplay does not own Escape when nothing is open");
        else
            GD.Print("  escape: exactly one screen claims it at a time");
    }

    /// <summary>Closing a screen takes anything stacked on top of it.</summary>
    private void CheckCloseTakesChildren(UiStateService s)
    {
        s.Reset(UiState.Gameplay);
        s.Push(UiState.Menu);
        s.Push(UiState.Settings);
        s.Close(UiState.Menu);

        if (s.IsActive(UiState.Settings))
            Fail("settings survived its parent menu closing");
        if (s.Current != UiState.Gameplay)
            Fail($"closing the menu left {s.Current} rather than gameplay");
        else
            GD.Print("  close: takes anything stacked above it");
    }

    /// <summary>An extra Escape cannot empty the stack.</summary>
    private void CheckBaseSurvivesOverPop(UiStateService s)
    {
        s.Reset(UiState.Gameplay);
        s.Pop();
        s.Pop();
        s.Pop();

        if (s.Depth != 1 || s.Current != UiState.Gameplay)
            Fail($"over-popping emptied the stack: depth {s.Depth}, current {s.Current}");
        else
            GD.Print("  base: survives being popped more than it was pushed");
    }

    private void CheckDoublePushRefused(UiStateService s)
    {
        s.Reset(UiState.Gameplay);
        s.Push(UiState.Inventory);
        s.Push(UiState.Inventory, warnIfOpen: false);

        if (s.Depth != 2)
            Fail($"pushing the same state twice stacked it: depth {s.Depth}");
        else
            GD.Print("  push: refuses to open the same screen twice");
    }

    private void CheckReset(UiStateService s)
    {
        s.Reset(UiState.Gameplay);
        s.Push(UiState.Menu);
        s.Push(UiState.Settings);
        s.Reset(UiState.MainMenu);

        if (s.Depth != 1 || s.Current != UiState.MainMenu)
            Fail($"reset left depth {s.Depth} at {s.Current}");
        else
            GD.Print("  reset: drops a whole level's stack on a scene change");

        s.Reset(UiState.Gameplay);
    }
}
