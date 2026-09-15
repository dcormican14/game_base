
namespace GameBase.Nodes;

/// <summary>
/// What a block is made OF, as the byte a cell stores.
///
/// The IDENTITY only. Everything a kind of node does or looks like lives on its
/// <see cref="NodeType"/>, reached through <see cref="NodeTypes.Of"/>; this
/// enum exists so a planet of millions of nodes costs one byte each rather than
/// a reference each. Adding a kind means adding an id here and a class there.
///
/// Ids are stable and small because they are
/// what save data records.
/// </summary>
public enum NodeMaterial : byte
{
    /// <summary>The default, kept at id 0 so an unset byte reads as ordinary
    /// rock rather than as something exotic.</summary>
    Raw = 0,

    /// <summary>Dark capping stone.</summary>
    Dark = 1,

    /// <summary>The planet's grey stone: what the cube world is built from.</summary>
    Stone = 2,

    /// <summary>
    /// The planet's skin: the few layers of earth over the rock.
    ///
    /// A material rather than a flag, so a dug block keeps being soil wherever
    /// it is put back and the mesher needs to know nothing about depth.
    /// </summary>
    Soil = 3,
}
