using System;
using System.Collections.Generic;
using GameBase.Nodes;
using GameBase.Tools.Highlight;

namespace GameBase.Tools;

/// <summary>
/// The shared half of every tool: a tool works one <see cref="NodeForm"/>, and
/// targets whatever of that form the crosshair meets first.
///
/// Subclasses supply what mining and placing mean for their form, and the
/// outline. They must go through <see cref="MayMine"/> and
/// <see cref="MayPlace"/> before changing the world, which is where the
/// material ledger and each node kind's own rules get their say.
/// </summary>
public abstract class Tool : ITool
{
    public abstract string Id { get; }

    /// <summary>The one form of node this tool works.</summary>
    public abstract NodeForm Form { get; }

    public abstract ToolCadence Cadence { get; }

    public abstract float Reach { get; }

    public virtual IReadOnlyList<string> Modes => Array.Empty<string>();

    public virtual float RepeatDelay => 0.35f;

    public virtual float RepeatInterval => 0.12f;

    public bool Works(NodeType type) => type != null && type.Form == Form;

    public bool TryTarget(in ToolContext context, out NodeHit target)
    {
        target = default;

        if (context.World?.Grid == null)
            return false;

        float reach = Reach + context.ReachBonus;

        // Whatever the ray meets first is the target -- even if it is the wrong
        // form. A tool never reaches through the surface in front of it.
        return context.World.Raycast(context.Eye, context.Eye + context.Aim * reach,
                out target, context.Exclude)
            && Works(target.Type);
    }

    public abstract bool Mine(in ToolContext context, in NodeHit target);

    public abstract bool Place(in ToolContext context, in NodeHit target);

    public abstract void Outline(in ToolContext context, in NodeHit target, HighlightBuilder builder);

    public abstract bool SameOutline(in NodeHit previous, in NodeHit current);

    /// <summary>May this much of this kind be removed? The check before every mine.</summary>
    protected static bool MayMine(in ToolContext context, NodeType type, float amount) =>
        type != null && type.CanMine && context.Ledger.CanMine(type, amount);

    /// <summary>May this much of this kind be put in? The check before every place.</summary>
    protected static bool MayPlace(in ToolContext context, NodeType type, float amount) =>
        type != null && type.CanPlace && context.Ledger.CanPlace(type, amount);
}
