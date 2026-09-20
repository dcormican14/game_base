using Godot;
using GameBase.Core;

namespace GameBase.Stats;

/// <summary>
/// Confirms the inventory's toggle is a first-class, rebindable action.
///
/// The settings screen builds its keybind rows from
/// SettingsService.GetRebindableActions(), so an action that is missing there
/// is invisible to the player even though it works in game — and an action
/// bound to a key another action already uses is worse, because rebinding one
/// silently steals from the other. Neither shows up in a screenshot.
/// </summary>
public partial class KeybindCheck : Node
{
    /// <summary>The action the inventory opens on.</summary>
    [Export] public StringName Action { get; set; } = "toggle_inventory";

    public override void _Ready()
    {
        int failures = 0;

        if (!InputMap.HasAction(Action))
        {
            GD.PrintErr($"  FAIL '{Action}' is not in the InputMap at all");
            GetTree().Quit(1);
            return;
        }

        SettingsService svc = SettingsService.Instance;
        if (svc == null)
        {
            GD.PrintErr("  FAIL SettingsService autoload is missing");
            GetTree().Quit(1);
            return;
        }

        bool listed = false;
        foreach (StringName action in svc.GetRebindableActions())
        {
            if (action == Action)
                listed = true;
        }

        if (!listed)
        {
            GD.PrintErr($"  FAIL '{Action}' is not offered for rebinding in the settings menu");
            failures++;
        }
        else
        {
            GD.Print($"  '{Action}' is rebindable, bound to {svc.GetBindingLabel(Action)}");
        }

        // No other action may share the key, or rebinding either one quietly
        // breaks the other.
        var events = InputMap.ActionGetEvents(Action);
        if (events.Count == 0)
        {
            GD.PrintErr($"  FAIL '{Action}' has no default binding");
            failures++;
        }
        else
        {
            string signature = SettingsService.SerializeEvent(events[0]);
            foreach (StringName other in svc.GetRebindableActions())
            {
                if (other == Action)
                    continue;

                foreach (InputEvent e in InputMap.ActionGetEvents(other))
                {
                    if (SettingsService.SerializeEvent(e) == signature)
                    {
                        GD.PrintErr($"  FAIL '{other}' shares the same key as '{Action}'");
                        failures++;
                    }
                }
            }
        }

        GD.Print(failures == 0 ? "KeybindCheck: PASS" : $"KeybindCheck: FAIL ({failures})");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }
}
