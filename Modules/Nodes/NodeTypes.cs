using Godot;

namespace GameBase.Nodes;

/// <summary>
/// Every kind of node the game knows, looked up by the byte a cell stores.
///
/// The bridge between storage and behaviour. A cell keeps one byte so a planet
/// of millions of nodes stays small; everything that needs to KNOW something
/// about a node comes through here and gets a <see cref="NodeType"/> it can ask
/// questions of.
///
/// A flat array rather than a dictionary: material ids are small dense
/// integers and this sits on the mesher's hot path, where the difference
/// between an array index and a hash lookup is worth having.
/// </summary>
public static class NodeTypes
{
    /// <summary>The kinds, indexed by material id.</summary>
    private static readonly NodeType[] ById = BuildTable();

    /// <summary>The soil kind, for a planet asking how deep its skin runs.</summary>
    public static readonly SoilNode Soil = new();

    private static NodeType[] BuildTable()
    {
        NodeType[] kinds =
        {
            new UnknownNode(),
            new DarkNode(),
            new StoneNode(),
            new SoilNode(),
        };

        // Sized from the largest id present, so adding a kind above is the only
        // edit needed -- there is no length to keep in step.
        int highest = 0;

        foreach (NodeType kind in kinds)
            highest = Mathf.Max(highest, (int)kind.Material);

        var table = new NodeType[highest + 1];

        foreach (NodeType kind in kinds)
            table[(int)kind.Material] = kind;

        // Any gap falls back to unclassified rock rather than to null, so a
        // material id this build does not know still renders and still behaves.
        for (int n = 0; n < table.Length; n++)
            table[n] ??= kinds[0];

        return table;
    }

    /// <summary>
    /// The kind a material byte means.
    ///
    /// Falls back to unclassified rock for an id this build does not know, so a
    /// world saved with a kind added later still loads instead of throwing.
    /// </summary>
    public static NodeType Of(NodeMaterial material)
    {
        int id = (int)material;
        return (uint)id < (uint)ById.Length ? ById[id] : ById[0];
    }

    /// <summary>Does this material form part of a planet's skin?</summary>
    public static bool IsSurface(NodeMaterial material) => Of(material).IsSurface;
}
