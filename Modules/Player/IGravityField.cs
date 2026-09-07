using Godot;

namespace GameBase.Player;

/// <summary>
/// Which way is down, at a given point.
///
/// A flat world does not need this: down is -Y everywhere, and every
/// controller in existence hard-codes it. A planet does, because "down" is a
/// direction that changes as you walk — stand on the far side and world -Y is
/// straight up.
///
/// Kept as an interface with a fallback rather than as a dependency on the
/// planet, so the player rig still works in a level that has no planet in it.
/// </summary>
public interface IGravityField
{
    /// <summary>
    /// The unit vector pointing DOWN at a world point.
    ///
    /// Down rather than up, because that is the direction the force acts in
    /// and the one a controller actually applies. Up is its negation.
    /// </summary>
    Vector3 DownAt(Vector3 point);
}

/// <summary>
/// Down is toward a point — the centre of a planet.
///
/// The whole of spherical gravity. Everything else that makes a planet
/// walkable (the player's basis, which way the camera calls up, which way the
/// capsule stands) follows from asking this one question at the player's
/// position.
/// </summary>
public sealed class RadialGravity : IGravityField
{
    private readonly Vector3 _centre;

    public RadialGravity(Vector3 centre)
    {
        _centre = centre;
    }

    public Vector3 DownAt(Vector3 point)
    {
        Vector3 toCentre = _centre - point;

        // At the exact centre there is no direction to fall in. Any answer is
        // as good as another, and -Y keeps the controller's arithmetic defined.
        float length = toCentre.Length();
        if (length < 0.0001f)
            return Vector3.Down;

        return toCentre / length;
    }
}

/// <summary>Down is world -Y, wherever you stand. The flat-world default.</summary>
public sealed class FlatGravity : IGravityField
{
    public Vector3 DownAt(Vector3 point) => Vector3.Down;
}
