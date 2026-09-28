using System.Collections.Generic;
using GameBase.Nodes;
using GameBase.Tools.Highlight;

namespace GameBase.Tools;

/// <summary>How a tool responds to a held button.</summary>
public enum ToolCadence
{
    /// <summary>One action per click; holding repeats after a delay, like a key.</summary>
    Discrete,

    /// <summary>Acts every frame the button is held, scaled by the frame's length.</summary>
    Continuous,
}

/// <summary>
/// A tool: something held that targets part of the world and changes it.
///
/// One interface for everything a tool does -- what it can work, what it is
/// pointing at, mining, placing, and the outline that shows the player what a
/// click will do -- so the controller that drives tools, and anything added
/// later, deals only in <see cref="ITool"/>.
///
/// MINE is the left button and PLACE the right, whatever the tool; each tool
/// decides what those mean for the material it works.
/// </summary>
public interface ITool
{
    /// <summary>The item id this tool is held as (see <see cref="Items.ItemType.Id"/>).</summary>
    string Id { get; }

    ToolCadence Cadence { get; }

    /// <summary>How long a button must be held before a discrete tool repeats, in seconds.</summary>
    float RepeatDelay { get; }

    /// <summary>Seconds between a discrete tool's repeats once it is repeating.</summary>
    float RepeatInterval { get; }

    /// <summary>How far from the user the tool reaches, in world units.</summary>
    float Reach { get; }

    /// <summary>
    /// The names of the tool's modes, in the order the mode key steps through
    /// them, shown in the mode bar. Empty for a tool with a single way of
    /// working. The selected one reaches the tool as <see cref="ToolContext.Mode"/>.
    /// </summary>
    IReadOnlyList<string> Modes { get; }

    /// <summary>Can this tool mine or place this kind of node?</summary>
    bool Works(NodeType type);

    /// <summary>
    /// What the tool is pointing at. False when the crosshair is on nothing in
    /// reach, or on something this tool cannot work -- which is also when no
    /// outline shows.
    /// </summary>
    bool TryTarget(in ToolContext context, out NodeHit target);

    /// <summary>Removes material at the target. Returns whether the world changed.</summary>
    bool Mine(in ToolContext context, in NodeHit target);

    /// <summary>Adds material at the target. Returns whether the world changed.</summary>
    bool Place(in ToolContext context, in NodeHit target);

    /// <summary>Describes the highlight for a target.</summary>
    void Outline(in ToolContext context, in NodeHit target, HighlightBuilder builder);

    /// <summary>
    /// Would these two targets draw the same outline? Lets the highlight skip
    /// rebuilding while the player looks at the same thing.
    /// </summary>
    bool SameOutline(in NodeHit previous, in NodeHit current);
}
