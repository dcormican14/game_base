using Godot;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// What a node is made OF, as opposed to what shape it takes.
///
/// A node has two independent properties. Its <see cref="INodeType"/> decides
/// its GEOMETRY — how the cell is carved — and its material decides its
/// APPEARANCE and which type carves it. Keeping them apart is what lets an
/// island be raw bismuth everywhere except a capping layer of dark stone,
/// without either the mesher or the generator special-casing the cap.
///
/// Materials are identified by a byte stored per cell, so the world's node map
/// costs one extra byte per node rather than a reference. Ids are stable and
/// small because they are what save data records.
/// </summary>
public enum NodeMaterial : byte
{
    /// <summary>Raw bismuth crystal — the default rock, edge-and-corner grown
    /// by <see cref="RawNode"/>.</summary>
    Raw = 0,

    /// <summary>Dark capping stone. Plain cubes, near-black, used for the flat
    /// tops of floating islands so the walkable surface reads distinctly from
    /// the crystal beneath it.</summary>
    Dark = 1,
}

/// <summary>
/// The palette: what each material looks like and which node type shapes it.
///
/// Two shades per material rather than one, alternating on a 3D checkerboard,
/// because a single flat albedo makes node relief unreadable — the eye needs
/// the shade break to see where one node ends and the next begins.
/// </summary>
public static class NodeMaterials
{
    /// <summary>One material's appearance and geometry choice.</summary>
    public readonly struct Entry
    {
        /// <summary>The two alternating shades.</summary>
        public readonly Color ColorA;
        public readonly Color ColorB;

        /// <summary>Which <see cref="INodeType"/> id carves cells of this
        /// material. Materials that want plain cubes name "plain".</summary>
        public readonly string TypeId;

        public Entry(Color colorA, Color colorB, string typeId)
        {
            ColorA = colorA;
            ColorB = colorB;
            TypeId = typeId;
        }
    }

    /// <summary>
    /// Defaults per material. The world's exported ColorA/ColorB override the
    /// Raw entry, so the existing colour dials keep working; the rest are
    /// fixed here because they exist to CONTRAST with the rock rather than to
    /// be dialled alongside it.
    /// </summary>
    private static readonly Dictionary<NodeMaterial, Entry> Palette = new()
    {
        [NodeMaterial.Raw] = new Entry(
            new Color(0.30f, 0.30f, 0.34f),
            new Color(0.62f, 0.62f, 0.66f),
            "raw"),

        // Topsoil. Shaped by the "topsoil" type, whose lower half interlocks
        // with the crystal below it and whose upper half rounds over along a
        // contour field — so the cap reads as ground the rock turns into
        // rather than as a lid set on top of it.
        //
        // Warm and dark against the cold grey of the bismuth, with the same
        // two-shade split every material carries so the node grid stays
        // readable across it.
        [NodeMaterial.Dark] = new Entry(
            new Color(0.105f, 0.082f, 0.058f),
            new Color(0.140f, 0.112f, 0.078f),
            "topsoil"),
    };

    /// <summary>Appearance and geometry for a material, falling back to Raw
    /// for an id this build does not know — so a world saved with a material
    /// added later still loads instead of throwing.</summary>
    public static Entry Get(NodeMaterial material) =>
        Palette.TryGetValue(material, out Entry entry) ? entry : Palette[NodeMaterial.Raw];

    /// <summary>The node type id that shapes cells of this material.</summary>
    public static string TypeIdOf(NodeMaterial material) => Get(material).TypeId;
}
