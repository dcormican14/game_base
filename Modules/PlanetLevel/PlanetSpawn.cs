using Godot;
using GameBase.Levels;

namespace GameBase.PlanetLevel;

/// <summary>
/// Puts the player on the planet's surface and makes down point at its core.
///
/// A planet has no constant spawn point: the ground is at whatever radius the
/// terrain reaches along a given direction, so where to stand has to be asked
/// of the field rather than written into the scene.
///
/// It also installs the gravity the controller uses. Without that the player
/// falls along world -Y, which points at the planet's centre only at the north
/// pole -- anywhere else they slide off as though the planet were a boulder
/// sitting in a flat world.
/// </summary>
public partial class PlanetSpawn : Node
{
    /// <summary>The streamer whose field decides where the ground is.</summary>
    [Export] public NodePath StreamerPath { get; set; } = "";

    /// <summary>The body to place.</summary>
    [Export] public NodePath PlayerPath { get; set; } = "";

    /// <summary>
    /// Which way from the centre to spawn. Any direction now works, since
    /// gravity follows the surface rather than the world axes.
    /// </summary>
    [Export] public Vector3 Direction { get; set; } = Vector3.Up;

    /// <summary>Nodes of clearance above the ground, so the player drops onto
    /// the surface rather than starting inside it.</summary>
    [Export(PropertyHint.Range, "0,40,0.5")] public float Clearance { get; set; } = 4f;

    public override void _Ready()
    {
        var streamer = GetNodeOrNull<PlanetStreamer>(StreamerPath);
        var player = GetNodeOrNull<Node3D>(PlayerPath);

        if (streamer == null || player == null)
        {
            GD.PushWarning("PlanetSpawn: needs both StreamerPath and PlayerPath — player left where it is.");
            return;
        }

        // Deferred: the streamer builds its field in _Ready, and this node's
        // own _Ready may run first depending on tree order.
        CallDeferred(nameof(Place), streamer, player);
    }

    private void Place(PlanetStreamer streamer, Node3D player)
    {
        if (streamer.Field == null)
            return;

        Vector3 at = streamer.SurfacePoint(Direction, Clearance);
        player.GlobalPosition = at;

        // DOWN IS TOWARD THE CORE, from here on.
        //
        // The planet is centred on the origin, so the field is just "fall
        // toward zero". Handing it to the controller is the whole of spherical
        // gravity: the body re-aligns to it each step, and every axis the
        // movement code uses comes from the body.
        if (player is GameBase.Player.PlayerController controller)
        {
            controller.GravityField = new GameBase.Player.RadialGravity(Vector3.Zero);

            // Stand the player up along the local vertical straight away, so
            // the first frame is not spent toppling from world-up to
            // surface-up.
            Vector3 up = at.Normalized();
            Vector3 forward = up.Cross(Vector3.Right);
            if (forward.LengthSquared() < 0.0001f)
                forward = up.Cross(Vector3.Forward);

            forward = forward.Normalized();
            var basis = new Basis(forward.Cross(up).Normalized(), up, -forward);
            controller.GlobalTransform = new Transform3D(basis.Orthonormalized(), at);
        }
    }
}
