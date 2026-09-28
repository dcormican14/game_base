using Godot;

namespace GameBase.Tools;

/// <summary>
/// What a tool remembers while one button is held down.
///
/// Tools are shared and hold no state of their own; anything that has to carry
/// from one frame to the next of the same press lives here instead. The
/// <see cref="ToolController"/> starts a fresh stroke on every press and drops
/// it on release, so nothing leaks from one press into the next.
/// </summary>
public sealed class ToolStroke
{
    /// <summary>True until a tool first acts in this stroke.</summary>
    public bool IsNew { get; private set; } = true;

    /// <summary>Where the stroke is working, in global space.</summary>
    public Vector3 Anchor { get; private set; }

    /// <summary>The way out of the surface at the anchor, in global space; zero if not given.</summary>
    public Vector3 AnchorNormal { get; private set; }

    /// <summary>How far the stroke has moved its material so far.</summary>
    public float Progress { get; private set; }

    /// <summary>Starts working at a new place.</summary>
    public void Begin(Vector3 anchor, Vector3 normal = default)
    {
        IsNew = false;
        Anchor = anchor;
        AnchorNormal = normal;
        Progress = 0f;
    }

    public void Advance(float amount) => Progress += amount;
}
