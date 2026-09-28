namespace GameBase.Nodes;

/// <summary>
/// What a node is made of, as the byte a cell stores.
///
/// The identity only. Everything a kind of node does or looks like lives on its
/// <see cref="NodeType"/>, reached through <see cref="NodeTypes.Of"/>; the enum
/// exists so a planet of millions of nodes costs one byte a node rather than a
/// reference. Adding a kind means adding an id here and a class there.
///
/// Empty space is not a material: the store marks it with
/// <see cref="NodeChunkStore.Air"/>.
/// </summary>
public enum NodeMaterial : byte
{
    /// <summary>Plain rock, the body of the planet.</summary>
    Stone = 0,

    /// <summary>Sand: the material of the planet's particle nodes.</summary>
    Sand = 1,
}

/// <summary>
/// The two ways a node can occupy its cell.
///
/// This is the split every system keys off: how a node is meshed, which body it
/// collides on, and which tools can work it.
/// </summary>
public enum NodeForm
{
    /// <summary>
    /// A solid cell with the exact shape of its Voronoi region: rock-like,
    /// faceted, all or nothing.
    /// </summary>
    Raw,

    /// <summary>
    /// A granular fill that takes up as much of its cell as its fill level says,
    /// and flows into its neighbours as one smooth surface.
    /// </summary>
    Particle,
}
