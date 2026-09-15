using Godot;
using GameBase.Levels;
using GameBase.Nodes;

namespace GameBase.PlanetLevel;

/// <summary>
/// Puts the player on a planet and makes down point at its core.
///
/// Named for the cube planet it was written for, and no longer limited to it:
/// the grid is taken from the WORLD as <see cref="INodeGrid"/>, so the same
/// node spawns onto the icosphere world too. Nothing here depends on how a
/// node is shaped -- only on there being a surface radius and something solid
/// under it.
///
/// A planet has no constant spawn point -- where the ground is depends on which
/// way out of the centre you go -- so the position has to be asked of the
/// streamer rather than written into the scene.
///
/// It also installs the gravity the controller uses. Without that the player
/// falls along world -Y, which points at the planet's centre only at the north
/// pole; anywhere else they slide off as though the planet were a boulder
/// sitting in a flat world.
/// </summary>
public partial class CubePlanetSpawn : Node
{
    /// <summary>The streamer whose grid says where the surface is.</summary>
    [Export] public NodePath StreamerPath { get; set; } = "";

    /// <summary>The body to place.</summary>
    [Export] public NodePath PlayerPath { get; set; } = "";

    /// <summary>Which way out of the centre to spawn. Any direction works,
    /// since gravity follows the surface rather than the world axes.</summary>
    [Export] public Vector3 Direction { get; set; } = Vector3.Up;

    /// <summary>Blocks of clearance above the ground, so the player drops onto
    /// the surface rather than starting inside it.</summary>
    [Export(PropertyHint.Range, "0,40,0.5")] public float Clearance { get; set; } = 3f;

    public override void _Ready()
    {
        var streamer = GetNodeOrNull<ChunkStreamer>(StreamerPath);
        var player = GetNodeOrNull<Node3D>(PlayerPath);

        if (streamer == null || player == null)
        {
            GD.PushWarning("CubePlanetSpawn: needs both StreamerPath and PlayerPath - player left where it is.");
            return;
        }

        _streamer = streamer;
        _player = player;

        // ORIENT NOW, STAND UP LATER.
        //
        // Gravity and the body's frame have to be installed immediately: the
        // controller falls along world -Y until it is told otherwise, and on a
        // planet that is only "down" at the north pole.
        //
        // But the POSITION cannot be final yet. Nothing is generated at _Ready
        // -- the first chunk has not been asked for, let alone meshed -- so
        // there is no collision anywhere and a player dropped in here falls
        // through the planet and out the far side before the ground arrives.
        // Measured: radius 799 to 358 to 112, through the core, still moving.
        //
        // So the placement waits for the streamer to report a world worth
        // standing on. Until then the body is frozen rather than merely
        // repositioned, because a frozen body cannot accumulate fall speed.
        CallDeferred(nameof(Orient));

        if (streamer.IsReady)
            CallDeferred(nameof(Place));
        else
            streamer.BecameReady += OnWorldReady;
    }

    private ChunkStreamer _streamer;
    private Node3D _player;

    public override void _ExitTree()
    {
        if (_streamer != null)
            _streamer.BecameReady -= OnWorldReady;
    }

    private void OnWorldReady()
    {
        _streamer.BecameReady -= OnWorldReady;
        CallDeferred(nameof(Place));
    }

    /// <summary>
    /// Installs radial gravity and stands the body up, without moving it.
    ///
    /// Runs before the world exists, so it must not depend on anything being
    /// generated. Freezing physics here is what keeps the player where they
    /// were authored instead of falling for the seconds it takes the first
    /// chunks to mesh.
    /// </summary>
    private void Orient()
    {
        if (_player is not GameBase.Player.PlayerController controller)
            return;

        controller.GravityField = new GameBase.Player.RadialGravity(Vector3.Zero);

        Vector3 at = controller.GlobalPosition;
        controller.GlobalTransform = new Transform3D(UprightAt(at), at);

        // Held until there is ground. The LoadingScreen does the same for the
        // player it finds; doing it here too means the spawn is safe even in a
        // scene without one.
        controller.Velocity = Vector3.Zero;
        controller.SetPhysicsProcess(false);
    }

    /// <summary>A basis whose up is away from the planet's centre.</summary>
    private static Basis UprightAt(Vector3 at)
    {
        Vector3 up = at.LengthSquared() > 0.0001f ? at.Normalized() : Vector3.Up;

        Vector3 forward = up.Cross(Vector3.Right);
        if (forward.LengthSquared() < 0.0001f)
            forward = up.Cross(Vector3.Forward);

        forward = forward.Normalized();
        return new Basis(forward.Cross(up).Normalized(), up, -forward).Orthonormalized();
    }

    /// <summary>
    /// Puts the player on the ground, once there is ground to put them on.
    ///
    /// The surface is found by asking the world what is actually solid along
    /// the spawn direction rather than trusting the nominal radius, so a
    /// carved or still-thickening world still lands the player on rock.
    /// </summary>
    private void Place()
    {
        // Taken from the WORLD rather than from the streamer, so this serves
        // any grid. Each streamer exposes its own concrete grid type; the world
        // holds whichever one it was handed, as the interface.
        var world = _streamer?.GetParentOrNull<NodeWorld>();
        INodeGrid grid = world?.Grid;

        if (grid == null || _player == null)
            return;

        Vector3 outward = Direction.LengthSquared() > 0.0001f
            ? Direction.Normalized() : Vector3.Up;

        float top = grid.SurfaceRadius;

        if (world != null)
        {
            int shells = Mathf.Max(1, Mathf.RoundToInt(grid.SurfaceRadius / grid.NodeSize));
            for (int shell = 0; shell < shells; shell++)
            {
                Vector3I cell = grid.CellAt(
                    outward * (grid.SurfaceRadius - (shell + 0.5f) * grid.NodeSize));

                if (!world.HasNode(cell))
                    continue;

                // The cell's OUTER face: the surface being stood on.
                top = grid.SurfaceRadius - shell * grid.NodeSize;
                break;
            }
        }

        Vector3 at = outward * (top + Clearance);
        _player.GlobalPosition = at;

        if (_player is not GameBase.Player.PlayerController controller)
            return;

        controller.GlobalTransform = new Transform3D(UprightAt(at), at);
        controller.Velocity = Vector3.Zero;

        // Physics resumes only now, with rock underneath to catch them.
        controller.SetPhysicsProcess(true);
    }
}
