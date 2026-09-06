using Godot;
using GameBase.Levels;

namespace GameBase.PlanetLevel;

/// <summary>
/// Puts the player on the planet's surface at start.
///
/// A planet has no constant spawn point: the ground is at whatever radius the
/// terrain reaches along a given direction, so where to stand has to be asked
/// of the field rather than written into the scene.
///
/// WHY THE NORTH POLE
///
/// <see cref="GameBase.Player.PlayerController"/> falls along world -Y. On a
/// sphere that is only "down" where the outward direction happens to be +Y —
/// the north pole — and anywhere else the player would slide off sideways as
/// though the planet were a boulder sitting in a flat world.
///
/// Spawning at the pole makes the planet walkable with the controller as it
/// stands. Free movement over the whole globe needs gravity that points at the
/// planet's centre and a player basis that rotates with it, which is a change
/// to the controller rather than to the world, and is deliberately not made
/// here.
/// </summary>
public partial class PlanetSpawn : Node
{
    /// <summary>The streamer whose field decides where the ground is.</summary>
    [Export] public NodePath StreamerPath { get; set; } = "";

    /// <summary>The body to place.</summary>
    [Export] public NodePath PlayerPath { get; set; } = "";

    /// <summary>
    /// Which way from the centre to spawn. Up is the north pole, the one
    /// direction where the controller's world-axis gravity points at the
    /// planet's centre.
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

        player.GlobalPosition = streamer.SurfacePoint(Direction, Clearance);
    }
}
