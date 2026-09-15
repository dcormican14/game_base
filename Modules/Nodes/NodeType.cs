using Godot;

namespace GameBase.Nodes;

/// <summary>
/// What a kind of node IS: how it looks, how it behaves, and where it belongs
/// in a planet.
///
/// One instance per kind, shared by every node of that kind -- a world holds
/// millions of nodes and storing a reference per node would cost eight bytes
/// where one suffices. A cell keeps its <see cref="NodeMaterial"/> byte and
/// this is looked up from it, so the store is unchanged and the behaviour is
/// polymorphic anyway.
///
/// WHY A HIERARCHY RATHER THAN A SWITCH
///
/// The world has two kinds of node today and will have more -- ores, ice,
/// clays, whatever a planet turns out to need. A switch on the material byte
/// would put every one of those decisions in a different file from the others,
/// and adding a kind would mean finding them all. A type says everything about
/// itself in one place, and adding a kind is adding a class.
///
/// <see cref="SurfaceNode"/> is the branch for anything that forms a planet's
/// skin, and <see cref="RawNode"/> for anything that makes up its body. The
/// distinction is not cosmetic: it decides which nodes are cut by the planet's
/// surface and which keep their whole shape.
/// </summary>
public abstract class NodeType
{
    protected NodeType(NodeMaterial material)
    {
        Material = material;
    }

    /// <summary>The byte a cell stores to mean this kind.</summary>
    public NodeMaterial Material { get; }

    /// <summary>A name for logs and tools.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Is this kind part of the planet's SKIN?
    ///
    /// A skin node is trimmed by the surface so the planet ends exactly at its
    /// radius; a body node keeps its whole irregular shape and is never cut.
    /// That is the whole difference between the soil you walk on and the rock
    /// underneath it.
    /// </summary>
    public abstract bool IsSurface { get; }

    /// <summary>
    /// The two shades this kind alternates between.
    ///
    /// Two rather than one because a field of identically coloured cells is
    /// unreadable -- the eye needs the break to see where one node ends and the
    /// next begins.
    /// </summary>
    public abstract Color ShadeA { get; }

    public abstract Color ShadeB { get; }

    /// <summary>
    /// Can a player dig this out?
    ///
    /// Here so that a future kind -- bedrock at the core, say -- can refuse
    /// without the editor having to know about it.
    /// </summary>
    public virtual bool CanMine => true;
}

/// <summary>
/// A node of the planet's BODY: the rock under the skin.
///
/// Never cut by the surface. A raw node keeps the whole irregular shape the
/// Voronoi diagram gives it, and the skin above sits on top of that shape
/// rather than replacing it.
/// </summary>
public abstract class RawNode : NodeType
{
    protected RawNode(NodeMaterial material) : base(material)
    {
    }

    public sealed override bool IsSurface => false;
}

/// <summary>
/// A node of the planet's SKIN: what a player walks on.
///
/// Trimmed by the planet's surface, so its outer face is the sphere itself and
/// the world has a clean edge. Its inner and side faces are ordinary Voronoi
/// walls, which is what lets it sit exactly on the rock below with no gap.
/// </summary>
public abstract class SurfaceNode : NodeType
{
    protected SurfaceNode(NodeMaterial material) : base(material)
    {
    }

    public sealed override bool IsSurface => true;

    /// <summary>
    /// How deep this kind of skin runs, in LAYERS of nodes.
    ///
    /// In layers rather than world units so it means the same at any node size:
    /// three layers is three nodes down whether a node is one unit across or
    /// four.
    /// </summary>
    public abstract float Layers { get; }
}

// --------------------------------------------------------------- the kinds

/// <summary>Ordinary grey rock: what most of a planet is made of.</summary>
public sealed class StoneNode : RawNode
{
    public StoneNode() : base(NodeMaterial.Stone)
    {
    }

    public override string Name => "Stone";

    public override Color ShadeA => new(0.155f, 0.155f, 0.170f);
    public override Color ShadeB => new(0.245f, 0.245f, 0.265f);
}

/// <summary>
/// Unclassified rock.
///
/// Kept at material id 0 so a cell whose byte was never set reads as ordinary
/// rock rather than as something exotic.
/// </summary>
public sealed class UnknownNode : RawNode
{
    public UnknownNode() : base(NodeMaterial.Raw)
    {
    }

    public override string Name => "Raw";

    public override Color ShadeA => new(0.30f, 0.30f, 0.34f);
    public override Color ShadeB => new(0.42f, 0.42f, 0.46f);
}

/// <summary>Dark capping stone.</summary>
public sealed class DarkNode : RawNode
{
    public DarkNode() : base(NodeMaterial.Dark)
    {
    }

    public override string Name => "Dark";

    public override Color ShadeA => new(0.105f, 0.082f, 0.058f);
    public override Color ShadeB => new(0.140f, 0.112f, 0.078f);
}

/// <summary>
/// The earth over the rock: the planet's topsoil.
///
/// Warm and clearly not grey, so the boundary between soil and rock reads at a
/// glance when a hole is dug through it. Darker than it looks here for the same
/// reason as the rest of the palette -- the stylised filter lifts midtones
/// hard.
/// </summary>
public sealed class SoilNode : SurfaceNode
{
    public SoilNode() : base(NodeMaterial.Soil)
    {
    }

    public override string Name => "Soil";

    public override float Layers => 3f;

    public override Color ShadeA => new(0.185f, 0.125f, 0.070f);
    public override Color ShadeB => new(0.240f, 0.170f, 0.098f);
}
