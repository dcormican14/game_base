using Godot;
using GameBase.Blocks;
using GameBase.Core;

namespace GameBase.UI;

/// <summary>
/// Covers the screen with a progress bar while the block world meshes, then
/// drops the player in and fades out.
///
/// A large level takes a noticeable time to mesh, and the player is a live
/// physics body from the moment the scene loads — without a gate it spawns into
/// a world with no collision and falls through the floor. Freezing instead
/// would look like a hang, so the world meshes a few chunks per frame.
///
/// The player is held by disabling its processing rather than delaying its
/// instantiation, so the scene tree stays as authored and the camera is live
/// behind the fade.
///
/// Instance LoadingScreen.tscn in a scene with a BlockWorld; it finds the
/// world and the player itself.
/// </summary>
public partial class LoadingScreen : CanvasLayer
{
    /// <summary>The world to wait on. Found by search when left empty.</summary>
    [Export] public NodePath BlockWorldPath { get; set; } = "";

    /// <summary>The body to hold until the world is ready. Found by search
    /// when left empty.</summary>
    [Export] public NodePath PlayerPath { get; set; } = "";

    /// <summary>Seconds the finished screen takes to fade away.</summary>
    [Export(PropertyHint.Range, "0,2,0.05")] public float FadeSeconds { get; set; } = 0.4f;

    /// <summary>Height above the world's surface to drop the player from, so
    /// it settles onto solid ground rather than starting inside it.</summary>
    [Export(PropertyHint.Range, "0,10,0.5")] public float DropHeight { get; set; } = 2f;

    /// <summary>Text shown above the bar.</summary>
    [Export] public string Message { get; set; } = "Building world";

    private BlockWorld _world;
    private Node3D _player;
    private ProgressBar _bar;
    private Label _label;
    private Control _root;
    private float _fade = -1f;
    private bool _released;

    public override void _Ready()
    {
        _root = GetNode<Control>("%Root");
        _bar = GetNode<ProgressBar>("%Bar");
        _label = GetNode<Label>("%Label");
        _label.Text = Message;

        _world = !BlockWorldPath.IsEmpty ? GetNodeOrNull<BlockWorld>(BlockWorldPath) : null;
        _world ??= NodeSearch.FindByType<BlockWorld>(GetTree().CurrentScene ?? GetParent());

        _player = !PlayerPath.IsEmpty ? GetNodeOrNull<Node3D>(PlayerPath) : null;
        _player ??= NodeSearch.FindByType<CharacterBody3D>(GetTree().CurrentScene ?? GetParent());

        if (_world == null)
        {
            // Nothing to wait for — get out of the way rather than covering a
            // scene that is already playable.
            Release();
            return;
        }

        HoldPlayer(true);

        if (_world.IsWorldReady)
            Release();
        else
            _world.WorldReady += Release;
    }

    public override void _ExitTree()
    {
        if (_world != null)
            _world.WorldReady -= Release;
    }

    public override void _Process(double delta)
    {
        if (_world != null && !_released)
            _bar.Value = _world.BuildProgress * 100.0;

        if (_fade < 0f)
            return;

        _fade += (float)delta;
        float alpha = FadeSeconds <= 0f ? 0f : Mathf.Clamp(1f - _fade / FadeSeconds, 0f, 1f);
        _root.Modulate = new Color(1f, 1f, 1f, alpha);

        if (alpha <= 0f)
        {
            SetProcess(false);
            QueueFree();
        }
    }

    /// <summary>Freezes the player. Physics is what has to stop: a
    /// CharacterBody3D left processing falls through a world with no
    /// collision yet.</summary>
    private void HoldPlayer(bool held)
    {
        if (_player == null)
            return;

        _player.SetPhysicsProcess(!held);
        _player.SetProcess(!held);
        _player.SetProcessInput(!held);
        _player.SetProcessUnhandledInput(!held);

        if (_player is CharacterBody3D body && held)
            body.Velocity = Vector3.Zero;
    }

    /// <summary>Drops the player in and starts the fade.</summary>
    private void Release()
    {
        if (_released)
            return;
        _released = true;

        _bar.Value = 100.0;
        PlacePlayer();
        HoldPlayer(false);
        _fade = 0f;
    }

    /// <summary>
    /// Puts the player just above the highest solid block over its spawn
    /// column, so it lands on the surface whatever the generator produced.
    /// </summary>
    private void PlacePlayer()
    {
        if (_player == null || _world == null)
            return;

        Vector3 spawn = _player.GlobalPosition;
        Vector3I cell = _world.CellAt(spawn);

        // Search down from well above the spawn for the first solid block.
        const int SearchUp = 64;
        const int SearchDown = 128;
        for (int y = cell.Y + SearchUp; y >= cell.Y - SearchDown; y--)
        {
            if (!_world.HasBlock(new Vector3I(cell.X, y, cell.Z)))
                continue;

            // Stand on top of that block, plus the drop margin.
            Vector3 top = _world.CellCentre(new Vector3I(cell.X, y, cell.Z));
            top.Y += _world.BlockSize * 0.5f + DropHeight;
            _player.GlobalPosition = new Vector3(spawn.X, top.Y, spawn.Z);
            return;
        }

        // Nothing underneath: leave the authored position alone rather than
        // moving the player somewhere arbitrary.
    }
}
