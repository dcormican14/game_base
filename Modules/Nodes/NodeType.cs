using Godot;
using GameBase.Core;

namespace GameBase.Nodes;

/// <summary>
/// What a kind of node is: its name, its form, and what may be done to it.
///
/// One instance per kind, shared by every node of that kind. A cell stores only
/// its <see cref="NodeMaterial"/> byte and this is looked up from it, so the
/// store stays one byte a node while behaviour is still polymorphic.
///
/// The hierarchy splits on <see cref="NodeForm"/> first, because form decides
/// how a node is meshed and collided and which tools can work it:
/// <see cref="RawNode"/> for faceted solids, <see cref="ParticleNode"/> for
/// granular fills. A new kind is a new class under whichever branch fits.
/// </summary>
public abstract class NodeType
{
    protected NodeType(NodeMaterial material, string name)
    {
        Material = material;
        Name = name;
    }

    /// <summary>The byte a cell stores to mean this kind.</summary>
    public NodeMaterial Material { get; }

    /// <summary>A name for logs, tools and the HUD.</summary>
    public string Name { get; }

    /// <summary>How this kind occupies its cell.</summary>
    public abstract NodeForm Form { get; }

    /// <summary>Can a player remove this kind? A future bedrock says no.</summary>
    public virtual bool CanMine => true;

    /// <summary>Can a player put this kind into the world?</summary>
    public virtual bool CanPlace => true;

    public override string ToString() => Name;
}

/// <summary>
/// A node that fills its whole Voronoi cell: faceted, solid, all or nothing.
///
/// Drawn cell by cell as the polyhedron the Voronoi diagram gives it, with two
/// shades alternated between neighbours so individual nodes stay readable.
/// </summary>
public abstract class RawNode : NodeType
{
    protected RawNode(NodeMaterial material, string name) : base(material, name)
    {
    }

    public sealed override NodeForm Form => NodeForm.Raw;

    /// <summary>The lighter of the two shades neighbouring nodes alternate between.</summary>
    public abstract Color ShadeA { get; }

    /// <summary>The darker shade.</summary>
    public abstract Color ShadeB { get; }
}

/// <summary>
/// A node that fills its cell to a level, like grains poured into a box.
///
/// Stored as a fill level per cell (see <see cref="NodeFill"/>), and drawn as
/// one smooth surface through every particle cell at once, so a bed of them
/// reads as a continuous material rather than as blocks. Digging lowers the
/// fill and building raises it; a cell whose fill runs out becomes air.
/// </summary>
public abstract class ParticleNode : NodeType
{
    protected ParticleNode(NodeMaterial material, string name) : base(material, name)
    {
    }

    public sealed override NodeForm Form => NodeForm.Particle;

    /// <summary>The material's lit colour.</summary>
    public abstract Color Colour { get; }

    /// <summary>The colour its grain and ripples shade toward.</summary>
    public abstract Color Shade { get; }
}

// ------------------------------------------------------------------- kinds

/// <summary>Plain rock: the body of the planet.</summary>
public sealed class StoneNode : RawNode
{
    public StoneNode() : base(NodeMaterial.Stone, "Stone")
    {
    }

    public override Color ShadeA => Palette.StoneLight;
    public override Color ShadeB => Palette.StoneDark;
}

/// <summary>Sand: the particle node the planet's shell is made of.</summary>
public sealed class SandNode : ParticleNode
{
    public SandNode() : base(NodeMaterial.Sand, "Sand")
    {
    }

    public override Color Colour => Palette.Sand;
    public override Color Shade => Palette.SandShade;

}
