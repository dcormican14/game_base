using Godot;
using GameBase.Core;
using GameBase.Items;
using GameBase.Nodes;
using GameBase.Tools.Highlight;

namespace GameBase.Tools;

/// <summary>
/// Puts the held item to work: finds the tool it is, aims it down the
/// crosshair, shows its highlight, and turns the mine and place buttons into
/// tool actions.
///
/// Mine is the left button and place the right, for every tool. A discrete
/// tool acts once per click and repeats while held, after a delay long enough
/// that an ordinary click never acts twice; a continuous tool acts every frame
/// the button is down.
///
/// A tool with MODES (<see cref="ITool.Modes"/>) steps through them on the
/// mode key. Each tool remembers its own mode while it is put away.
///
/// Instance under the player; it finds the camera, the world and the
/// inventory itself.
/// </summary>
public partial class ToolController : Node3D
{
    [Export] public NodePath CameraPath { get; set; } = "";

    [Export] public StringName MineAction { get; set; } = "mine";

    [Export] public StringName PlaceAction { get; set; } = "place";

    [Export] public StringName ModeAction { get; set; } = "tool_mode";

    /// <summary>What mining and placing may spend. Unlimited until collecting exists.</summary>
    public IMaterialLedger Ledger { get; set; } = UnlimitedLedger.Instance;

    /// <summary>The tool in hand this frame, or null.</summary>
    public ITool Tool { get; private set; }

    public ToolHighlight Highlight { get; private set; }

    private Camera3D _camera;
    private NodeWorld _world;
    private Inventory _inventory;
    private CollisionObject3D _body;

    private enum Action { None, Mine, Place }

    private Action _held;
    private Action _lastPressed;
    private float _heldFor;
    private float _sinceRepeat;
    private ToolStroke _stroke = new();
    private readonly System.Collections.Generic.Dictionary<string, int> _modes = new();

    /// <summary>The mode a tool is in, an index into its <see cref="ITool.Modes"/>.</summary>
    public int ModeOf(ITool tool)
    {
        if (tool == null || tool.Modes.Count == 0)
            return 0;

        return _modes.TryGetValue(tool.Id, out int mode) ? Mathf.PosMod(mode, tool.Modes.Count) : 0;
    }

    /// <summary>Steps a tool on to its next mode, wrapping round.</summary>
    public void CycleMode(ITool tool)
    {
        if (tool == null || tool.Modes.Count == 0)
            return;

        _modes[tool.Id] = (ModeOf(tool) + 1) % tool.Modes.Count;

        // A held press does not carry into the new mode: Level's anchor, for
        // one, belongs to the mode it was taken in.
        _stroke = new ToolStroke();
    }

    public override void _Ready()
    {
        Node scene = GetTree().CurrentScene ?? GetParent();

        _camera = !CameraPath.IsEmpty ? GetNodeOrNull<Camera3D>(CameraPath) : null;
        _camera ??= NodeSearch.FindByType<Camera3D>(scene);
        _inventory = NodeSearch.FindByType<Inventory>(scene);

        for (Node node = GetParent(); node != null; node = node.GetParent())
        {
            if (node is CollisionObject3D body)
            {
                _body = body;
                break;
            }
        }

        Highlight = new ToolHighlight { Name = "Highlight" };
        AddChild(Highlight);
    }

    public override void _Process(double delta)
    {
        Tool = HeldTool();

        if (Tool == null || !InGameplay() || !FindWorld())
        {
            // Put away or behind a menu: whatever comes next is a new press.
            _lastPressed = Action.None;
            Stop();
            return;
        }

        if (Input.IsActionJustPressed(ModeAction))
            CycleMode(Tool);

        Action pressed = Input.IsActionPressed(MineAction) ? Action.Mine
            : Input.IsActionPressed(PlaceAction) ? Action.Place
            : Action.None;

        // A fresh stroke for a fresh press, made BEFORE the context that
        // carries it. Made after, the press's first action ran on the previous
        // stroke -- so the first frame of digging after a long pile reused the
        // pile's anchor and its whole height, and carved a pit as deep as the
        // mound was tall straight out from under the player.
        //
        // Keyed to the BUTTON, not to whether the tool acted: a press whose
        // crosshair slips off the ground for a frame is still the same press,
        // and Level's anchor belongs to it until the button comes up.
        if (pressed != _lastPressed)
            _stroke = new ToolStroke();

        _lastPressed = pressed;

        ToolContext context = ContextFor((float)delta);

        if (!Tool.TryTarget(context, out NodeHit target))
        {
            Stop();
            return;
        }

        Highlight.Show(Tool, context, target);

        if (ShouldAct(pressed, (float)delta))
            Act(Tool, context, target, pressed);
    }

    /// <summary>
    /// Does the held button fire this frame? Continuous tools fire every frame;
    /// discrete ones on the press, then on a repeat once held past the delay.
    /// </summary>
    private bool ShouldAct(Action pressed, float delta)
    {
        if (pressed == Action.None)
        {
            _held = Action.None;
            return false;
        }

        if (pressed != _held)
        {
            _held = pressed;
            _heldFor = 0f;
            _sinceRepeat = 0f;
            return true;
        }

        if (Tool.Cadence == ToolCadence.Continuous)
            return true;

        _heldFor += delta;
        if (_heldFor < Tool.RepeatDelay)
            return false;

        _sinceRepeat += delta;
        if (_sinceRepeat < Tool.RepeatInterval)
            return false;

        _sinceRepeat = 0f;
        return true;
    }

    private static void Act(ITool tool, in ToolContext context, in NodeHit target, Action action)
    {
        if (action == Action.Mine)
            tool.Mine(context, target);
        else if (action == Action.Place)
            tool.Place(context, target);
    }

    private void Stop()
    {
        Highlight?.Clear();
        _held = Action.None;
    }

    private ITool HeldTool()
    {
        if (_inventory == null || !IsInstanceValid(_inventory))
            return null;

        ItemStack held = _inventory.SelectedStack;
        return held.IsEmpty ? null : ToolRegistry.For(held.Type);
    }

    /// <summary>
    /// Gameplay has the input: the state service says so, and the mouse is
    /// captured -- the per-frame truth about where the pointer is while a
    /// screen opens or closes.
    /// </summary>
    private bool InGameplay() =>
        _camera != null
        && Input.MouseMode == Input.MouseModeEnum.Captured
        && UiStateService.Instance is not { GameplayHasInput: false };

    private bool FindWorld()
    {
        if (_world == null || !IsInstanceValid(_world))
            _world = NodeSearch.FindByType<NodeWorld>(GetTree().CurrentScene ?? GetParent());

        return _world?.Grid != null;
    }

    private ToolContext ContextFor(float delta)
    {
        Vector2 centre = _camera.GetViewport().GetVisibleRect().Size * 0.5f;
        Vector3 eye = _camera.ProjectRayOrigin(centre);
        Vector3 aim = _camera.ProjectRayNormal(centre);

        // Reach is measured from the body, so a third-person camera set back
        // behind the player does not quietly shorten it.
        float bonus = _body != null ? eye.DistanceTo(_body.GlobalPosition) : 0f;

        return new ToolContext(_world, eye, aim, Ledger, _body, delta, bonus, _stroke, ModeOf(Tool));
    }
}
