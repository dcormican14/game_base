using Godot;
using GameBase.Core;
using GameBase.Items;
using GameBase.UI;

namespace GameBase.Stats;

/// <summary>
/// Drives real key presses through the real screens, and checks where they
/// land.
///
/// UiStateCheck proves the stack obeys its own rules; this proves the screens
/// actually consult it. The two failures worth catching are opposites of each
/// other and neither shows up in the stack alone: Escape closing the inventory
/// AND opening the pause menu behind it (both acted), or Escape doing nothing
/// at all (neither did, each assuming the other would).
///
/// Events are fed with Input.ParseInputEvent so they travel the same path a
/// real press does — through _Input, then _UnhandledInput, in CanvasLayer
/// order — rather than being handed straight to one node, which would prove
/// nothing about arbitration.
/// </summary>
public partial class EscapeFlowCheck : Node
{
    private int _failures;
    private int _step;

    private InventoryHud _inventory;
    private PauseMenu _pause;
    private UiStateService _states;

    public override void _Ready()
    {
        // Runs while the tree is paused, which both screens do.
        ProcessMode = ProcessModeEnum.Always;

        Node scene = GetTree().CurrentScene;
        _inventory = NodeSearch.FindByType<InventoryHud>(scene);
        _pause = NodeSearch.FindByType<PauseMenu>(scene);
        _states = UiStateService.Instance;

        if (_inventory == null || _pause == null || _states == null)
        {
            GD.PrintErr("EscapeFlowCheck: needs an InventoryHud, a PauseMenu and the state service.");
            GetTree().Quit(1);
        }
    }

    /// <summary>
    /// One action per frame. The steps are spread over frames rather than run
    /// in a loop because input is delivered between frames: pressing and then
    /// immediately asking what happened would read the state from before the
    /// event was dispatched.
    /// </summary>
    public override void _Process(double delta)
    {
        switch (_step++)
        {
            case 0:
                // Nothing open: the world should own input.
                Expect(_states.Current == UiState.Gameplay, $"started in {_states.Current}");
                Press("toggle_inventory");
                break;

            case 1:
                Expect(_inventory.IsOpen, "Tab did not open the inventory");
                Expect(_states.Current == UiState.Inventory,
                    $"opening the inventory left the state at {_states.Current}");
                Press("pause");
                break;

            case 2:
                // THE BUG THIS EXISTS FOR. Escape must close the inventory and
                // hand back the world — not close it and open the menu behind.
                Expect(!_inventory.IsOpen, "Escape did not close the inventory");
                Expect(!_pause.IsPaused, "Escape closed the inventory AND opened the pause menu");
                Expect(_states.Current == UiState.Gameplay,
                    $"after Escape the state is {_states.Current}, expected Gameplay");

                if (_failures == 0)
                    GD.Print("  escape in inventory: closes it, and does not open the menu");

                Press("pause");
                break;

            case 3:
                // With nothing open, the same key is the pause menu's again.
                Expect(_pause.IsPaused, "Escape did not open the pause menu from gameplay");
                Expect(_states.Current == UiState.Menu,
                    $"opening the menu left the state at {_states.Current}");
                GD.Print("  escape in world: opens the pause menu");
                Press("pause");
                break;

            case 4:
                Expect(!_pause.IsPaused, "Escape did not close the pause menu");
                Expect(_states.Current == UiState.Gameplay,
                    $"closing the menu left the state at {_states.Current}");
                GD.Print("  escape in menu: closes it and returns to the world");

                // Tab must not reach into an open menu and swap it for the
                // inventory.
                Press("pause");
                break;

            case 5:
                Expect(_pause.IsPaused, "could not reopen the pause menu");
                Press("toggle_inventory");
                break;

            case 6:
                Expect(!_inventory.IsOpen, "Tab opened the inventory underneath the pause menu");
                Expect(_states.Current == UiState.Menu,
                    $"Tab in the menu changed the state to {_states.Current}");
                GD.Print("  tab in menu: ignored, the menu keeps the screen");
                Press("pause");
                break;

            default:
                GD.Print(_failures == 0
                    ? "EscapeFlowCheck: PASS"
                    : $"EscapeFlowCheck: FAIL ({_failures} problem(s))");
                GetTree().Quit(_failures == 0 ? 0 : 1);
                break;
        }
    }

    private void Expect(bool condition, string failureMessage)
    {
        if (condition)
            return;

        _failures++;
        GD.PrintErr($"  FAIL {failureMessage}");
    }

    /// <summary>Feeds a press and its release, as a real key would.</summary>
    private static void Press(StringName action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }
}
