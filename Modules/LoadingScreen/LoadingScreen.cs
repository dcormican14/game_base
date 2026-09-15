using Godot;
using GameBase.Nodes;
using GameBase.Core;
using GameBase.Levels;

namespace GameBase.UI;

/// <summary>
/// Covers the screen with a progress bar while the node world meshes, then
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
/// Instance LoadingScreen.tscn in a scene with a NodeWorld; it finds the
/// world and the player itself.
/// </summary>
public partial class LoadingScreen : CanvasLayer
{
    /// <summary>The world to wait on. Found by search when left empty.</summary>
    [Export] public NodePath NodeWorldPath { get; set; } = "";

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

    private NodeWorld _world;
    private Node3D _player;


    /// <summary>
    /// The streamer, when the world is endless rather than a fixed region.
    ///
    /// A streaming world never finishes — there is always more of it out of
    /// range — so waiting on the world to be wholly built would hold the
    /// screen up forever. The streamer reports something different and more
    /// useful: whether the ground around the SPAWN exists yet, which is the
    /// actual precondition for letting the player go.
    /// </summary>
    private ChunkStreamer _streamer;

    /// <summary>
    /// The scene's pause menu, suspended while the world builds.
    ///
    /// Pausing mid-build would stop the very work the player is waiting on,
    /// and the menu's Resume would drop them into a world that does not exist
    /// yet.
    /// </summary>
    private PauseMenu _pauseMenu;
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

        _world = !NodeWorldPath.IsEmpty ? GetNodeOrNull<NodeWorld>(NodeWorldPath) : null;
        _world ??= NodeSearch.FindByType<NodeWorld>(GetTree().CurrentScene ?? GetParent());

        _player = !PlayerPath.IsEmpty ? GetNodeOrNull<Node3D>(PlayerPath) : null;
        _player ??= NodeSearch.FindByType<CharacterBody3D>(GetTree().CurrentScene ?? GetParent());

        _streamer = NodeSearch.FindByType<ChunkStreamer>(GetTree().CurrentScene ?? GetParent());

        _pauseMenu = NodeSearch.FindByType<PauseMenu>(GetTree().CurrentScene ?? GetParent());
        if (_pauseMenu != null)
            _pauseMenu.Suspended = true;

        if (_world == null)
        {
            // Nothing to wait for — get out of the way rather than covering a
            // scene that is already playable.
            Release();
            return;
        }

        HoldPlayer(true);

        // A streamed world answers for itself; only a fixed-region world uses
        // the world's own one-shot ready signal.
        if (_streamer != null)
        {
            if (_streamer.IsReady)
                Release();
            else
                _streamer.BecameReady += Release;

            return;
        }

        if (_world.IsWorldReady)
            Release();
        else
            _world.WorldReady += Release;
    }

    public override void _ExitTree()
    {
        if (_world != null)
            _world.WorldReady -= Release;

        if (_streamer != null)
            _streamer.BecameReady -= Release;

        // Never leave the menu suspended behind us. If this screen is torn
        // down before the world finished — a scene change mid-load — the
        // suspension would otherwise outlive the thing that imposed it and
        // silently disable pausing for the rest of the session.
        if (_pauseMenu != null && GodotObject.IsInstanceValid(_pauseMenu))
            _pauseMenu.Suspended = false;
    }

    public override void _Process(double delta)
    {
        if (_world != null && !_released)
        {
            _bar.Value = Progress() * 100.0;

            // RE-ASSERTED EVERY FRAME, not set once.
            //
            // The player captures the mouse in its own _Ready, and which of the
            // two runs first depends on where each sits in the scene -- so
            // releasing the cursor once here wins or loses by accident. Holding
            // it each frame while the screen is up does not care about the
            // order, and stops the moment the world is handed over.
            if (Input.MouseMode == Input.MouseModeEnum.Captured)
                Input.MouseMode = Input.MouseModeEnum.Visible;
        }

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

    /// <summary>
    /// How far the wait has come, over both phases.
    ///
    /// Generation is given the larger share because it is the longer of the
    /// two by some way; the split is only about where the bar sits, never
    /// about when the player is released, which still waits on the world
    /// itself.
    /// </summary>
    private float Progress()
    {
        const float GenerateShare = 0.7f;

        // A streamed world's progress is toward being PLAYABLE — the chunks
        // around the spawn — rather than toward an end of work that never
        // arrives.
        //
        // PACED AGAINST THE WORLD'S OWN HISTORY, not against a fixed fraction.
        //
        // The streamer measures its whole backlog, and a world becomes playable
        // well before that empties -- the rest is scenery arriving behind the
        // player. How far before varies enormously: measured at readiness, the
        // cube planet had cleared 6.5% of its queue, the icosphere 15.1%, the
        // organic planet 59.5%. Dividing by any one constant would leave two of
        // the three visibly wrong.
        //
        // So the bar is paced on TIME instead, with the streamer's own figure
        // as the guarantee of honesty: it may never exceed what has actually
        // been built plus the share of the wait already spent, and it stops
        // short of the end until the world really is ready. What the player
        // sees is a bar that moves steadily from the first frame and arrives as
        // the world does.
        if (_streamer != null)
        {
            float built = _streamer.ReadyProgress;

            // Time-based pacing, easing off as it approaches the end so it
            // cannot run out before the world arrives.
            _waited += (float)GetProcessDeltaTime();
            float paced = 1f - Mathf.Exp(-_waited / ExpectedSeconds);

            return Mathf.Clamp(Mathf.Max(built, paced), 0f, 0.99f);
        }

        return GenerateShare + _world.BuildProgress * (1f - GenerateShare);
    }

    /// <summary>
    /// Roughly how long a world takes to become playable, in seconds.
    ///
    /// Only sets the PACE of the bar between real milestones; it never decides
    /// when the player is released, which waits on the streamer. Chosen from
    /// the measured spread -- under a second for the cube planet, about four
    /// and a half for the organic one -- so the bar neither crawls on the fast
    /// worlds nor stalls on the slow ones.
    /// </summary>
    [Export(PropertyHint.Range, "0.5,20,0.5")]
    public float ExpectedSeconds { get; set; } = 3f;

    /// <summary>How long this screen has been up.</summary>
    private float _waited;

    /// <summary>
    /// Freezes the player, and gives the mouse back.
    ///
    /// Physics is what has to stop: a CharacterBody3D left processing falls
    /// through a world with no collision yet.
    ///
    /// THE CURSOR MATTERS TOO. The player captures the mouse the moment it
    /// enters the tree, which is well before the world exists -- so a build
    /// that takes several seconds left the pointer locked to a frozen camera
    /// with nothing to do and no way to reach anything else on the desktop.
    /// Holding the player and holding the cursor are the same decision, so
    /// they are made in the same place.
    /// </summary>
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

        // Released to the desktop while waiting, captured when the world is
        // handed over. Not forced on release if something else has already
        // taken it -- the pause menu, most likely -- since overriding that
        // would trap the pointer in a menu the player opened on purpose.
        if (held)
            Input.MouseMode = Input.MouseModeEnum.Visible;
        else if (Input.MouseMode == Input.MouseModeEnum.Visible)
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>
    /// Stands the player on the outermost solid shell along their own radius.
    ///
    /// The sphere's answer to the column walk: the spawn direction picks a
    /// column of cells, and the first solid one going INWARD is the ground.
    /// Placing them on its outer face -- rather than at a cell centre -- is
    /// what stops the drop starting half a block inside the rock.
    /// </summary>
    private void PlaceOnSphere(Vector3 spawn)
    {
        INodeGrid grid = _world.Grid;

        Vector3 outward = spawn.LengthSquared() > 0.0001f
            ? spawn.Normalized()
            : Vector3.Up;

        // Walk inward from the outermost shell. The surface is shell 0 on a
        // full planet, but a carved or partly-streamed world may not have it,
        // so this searches rather than assuming.
        int shells = Mathf.Max(1,
            Mathf.RoundToInt(grid.SurfaceRadius / grid.NodeSize));

        for (int shell = 0; shell < shells; shell++)
        {
            float radius = grid.SurfaceRadius - (shell + 0.5f) * grid.NodeSize;
            Vector3I cell = grid.CellAt(outward * radius);

            if (!_world.HasNode(cell))
                continue;

            // The cell's OUTER face, which is the surface being stood on, plus
            // the drop margin.
            float top = grid.SurfaceRadius - shell * grid.NodeSize;
            _player.GlobalPosition = outward * (top + DropHeight);
            return;
        }

        // Nothing solid along this direction: leave the authored position
        // rather than moving the player somewhere arbitrary.
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

        // The scene is playable from here, so the menu becomes meaningful.
        if (_pauseMenu != null)
            _pauseMenu.Suspended = false;

        _fade = 0f;
    }

    /// <summary>
    /// Puts the player just above the highest solid node over its spawn
    /// column, so it lands on the surface whatever the generator produced.
    /// </summary>
    private void PlacePlayer()
    {
        if (_player == null || _world == null)
            return;

        Vector3 spawn = _player.GlobalPosition;

        // ON A SPHERE, DOWN IS INWARD.
        //
        // The flat search below walks the Y axis, which is the world's vertical
        // only on a flat world. On the cubed sphere a cell is (u, v, shell) and
        // "the node under this one" is the next shell IN, so searching Y walks
        // sideways across the face instead of downward and finds nothing --
        // leaving the player at the authored spawn, in mid-air, to fall.
        if (_world.Grid != null)
        {
            PlaceOnSphere(spawn);
            return;
        }

        Vector3I cell = _world.CellAt(spawn);

        // Search down from well above the spawn for the first solid node.
        const int SearchUp = 64;
        const int SearchDown = 128;
        for (int y = cell.Y + SearchUp; y >= cell.Y - SearchDown; y--)
        {
            if (!_world.HasNode(new Vector3I(cell.X, y, cell.Z)))
                continue;

            // Stand on top of that node, plus the drop margin.
            Vector3 top = _world.CellCentre(new Vector3I(cell.X, y, cell.Z));
            top.Y += _world.NodeSize * 0.5f + DropHeight;
            _player.GlobalPosition = new Vector3(spawn.X, top.Y, spawn.Z);
            return;
        }

        // Nothing underneath: leave the authored position alone rather than
        // moving the player somewhere arbitrary.
    }
}
