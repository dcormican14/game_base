using Godot;
using GameBase.Nodes;

namespace GameBase.Tools;

/// <summary>
/// Everything a tool needs to act for one frame: the world, where the user is
/// looking from and toward, what they are, and what they may spend.
///
/// Built fresh by the <see cref="ToolController"/> each frame, so a tool holds
/// no per-user state and one tool instance can serve anyone.
/// </summary>
public readonly struct ToolContext
{
    public ToolContext(NodeWorld world, Vector3 eye, Vector3 aim, IMaterialLedger ledger,
        CollisionObject3D user, float delta, float reachBonus = 0f, ToolStroke stroke = null, int mode = 0)
    {
        World = world;
        Eye = eye;
        Aim = aim.Normalized();
        Ledger = ledger ?? UnlimitedLedger.Instance;
        User = user;
        Delta = delta;
        ReachBonus = reachBonus;
        Stroke = stroke ?? new ToolStroke();
        Mode = mode;
    }

    public NodeWorld World { get; }

    /// <summary>Where the ray starts: the camera, so the crosshair is what gets targeted.</summary>
    public Vector3 Eye { get; }

    /// <summary>The unit direction the crosshair points.</summary>
    public Vector3 Aim { get; }

    public IMaterialLedger Ledger { get; }

    /// <summary>The body using the tool, excluded from its rays; may be null.</summary>
    public CollisionObject3D User { get; }

    /// <summary>Seconds this frame covers, for tools that work at a rate.</summary>
    public float Delta { get; }

    /// <summary>
    /// Extra reach for the distance between the eye and the body, so a
    /// third-person camera set back from the player does not shorten the reach.
    /// </summary>
    public float ReachBonus { get; }

    /// <summary>The press this frame belongs to; a new one for every press.</summary>
    public ToolStroke Stroke { get; }

    /// <summary>The selected mode, an index into <see cref="ITool.Modes"/>; 0 for a tool without modes.</summary>
    public int Mode { get; }

    /// <summary>The user's up direction, or world up without a user.</summary>
    public Vector3 Up => User?.GlobalBasis.Y.Normalized() ?? Vector3.Up;

    public Godot.Collections.Array<Rid> Exclude =>
        User == null ? null : new Godot.Collections.Array<Rid> { User.GetRid() };
}
