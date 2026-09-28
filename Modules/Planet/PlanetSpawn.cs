using Godot;
using GameBase.Nodes;
using GameBase.Player;

namespace GameBase.Planets;

/// <summary>
/// Puts the player on the planet: installs radial gravity at once, then holds
/// the body still until the streamer reports ground and stands it on the
/// surface.
///
/// The hold is the important half. Nothing is generated when the scene loads,
/// so a player given physics straight away falls through a planet that does
/// not exist yet. The body is frozen -- not merely repositioned -- so it cannot
/// build up fall speed while it waits.
/// </summary>
public partial class PlanetSpawn : Node
{
    [Export] public NodePath PlanetPath { get; set; } = "";

    [Export] public NodePath PlayerPath { get; set; } = "";

    /// <summary>Which way out of the centre to spawn.</summary>
    [Export] public Vector3 Direction { get; set; } = Vector3.Up;

    /// <summary>Height above the ground to drop the player from.</summary>
    [Export(PropertyHint.Range, "0,20,0.25")] public float Clearance { get; set; } = 1f;

    private Planet _planet;
    private PlayerController _player;

    public override void _Ready()
    {
        _planet = GetNodeOrNull<Planet>(PlanetPath);
        _player = GetNodeOrNull<PlayerController>(PlayerPath);

        if (_planet == null || _player == null)
        {
            GD.PushWarning("PlanetSpawn: needs PlanetPath and PlayerPath - player left where it is.");
            return;
        }

        CallDeferred(MethodName.Hold);
    }

    public override void _ExitTree()
    {
        if (_planet?.Streamer != null)
            _planet.Streamer.BecameReady -= Place;
    }

    /// <summary>Gravity and an upright body now; the ground check starts from here.</summary>
    private void Hold()
    {
        Vector3 up = Direction.LengthSquared() > 0.0001f ? Direction.Normalized() : Vector3.Up;
        Vector3 at = _planet.SurfacePoint(up) + up * (Clearance + 1f);

        _player.GravityField = _planet.Gravity;
        _player.GlobalTransform = new Transform3D(UprightAt(up), at);
        _player.Velocity = Vector3.Zero;
        _player.SetPhysicsProcess(false);

        if (_planet.Streamer.IsReady)
            Place();
        else
            _planet.Streamer.BecameReady += Place;
    }

    /// <summary>Stands the player on whatever is actually solid below the spawn.</summary>
    private void Place()
    {
        _planet.Streamer.BecameReady -= Place;

        Vector3 up = _planet.UpAt(_player.GlobalPosition);
        Vector3 surface = _planet.SurfacePoint(up);

        var exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };

        Vector3 at = _planet.World.Raycast(surface + up * 64f, surface - up * 64f, out NodeHit hit, exclude)
            ? hit.Point
            : surface;

        at += up * Clearance;

        _player.GlobalTransform = new Transform3D(UprightAt(up), at);
        _player.Velocity = Vector3.Zero;
        _player.SetPhysicsProcess(true);
    }

    /// <summary>A basis whose up is the given direction.</summary>
    private static Basis UprightAt(Vector3 up)
    {
        Vector3 reference = Mathf.Abs(up.Dot(Vector3.Right)) < 0.9f ? Vector3.Right : Vector3.Forward;
        Vector3 forward = up.Cross(reference).Normalized();
        Vector3 right = forward.Cross(up).Normalized();

        return new Basis(right, up, -forward).Orthonormalized();
    }
}
