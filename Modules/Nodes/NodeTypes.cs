namespace GameBase.Nodes;

/// <summary>
/// Every kind of node the game knows, looked up by the byte a cell stores.
///
/// A flat array rather than a dictionary: material ids are small dense integers
/// and this sits on the mesher's hot path.
/// </summary>
public static class NodeTypes
{
    public static readonly StoneNode Stone = new();
    public static readonly SandNode Sand = new();

    private static readonly NodeType[] ById = BuildTable(Stone, Sand);

    private static NodeType[] BuildTable(params NodeType[] kinds)
    {
        int highest = 0;
        foreach (NodeType kind in kinds)
            highest = System.Math.Max(highest, (int)kind.Material);

        var table = new NodeType[highest + 1];
        foreach (NodeType kind in kinds)
            table[(int)kind.Material] = kind;

        // A gap in the ids falls back to stone rather than null, so an id this
        // build does not know still behaves as something solid.
        for (int n = 0; n < table.Length; n++)
            table[n] ??= Stone;

        return table;
    }

    /// <summary>The kind a material byte means, or null for air.</summary>
    public static NodeType Of(byte material)
    {
        if (material == NodeChunkStore.Air)
            return null;

        return material < ById.Length ? ById[material] : Stone;
    }

    /// <summary>The kind a material means.</summary>
    public static NodeType Of(NodeMaterial material) => Of((byte)material);

    /// <summary>Is this stored byte a raw node? Air is not.</summary>
    public static bool IsRaw(byte material) => Forms[material] == RawTag;

    /// <summary>Is this stored byte a particle node? Air is not.</summary>
    public static bool IsParticle(byte material) => Forms[material] == ParticleTag;

    // Form per possible byte, so the meshers classify a cell with one array
    // read instead of a lookup and a virtual call.
    private const byte AirTag = 0, RawTag = 1, ParticleTag = 2;

    private static readonly byte[] Forms = BuildForms();

    private static byte[] BuildForms()
    {
        var forms = new byte[256];

        for (int id = 0; id < 256; id++)
        {
            NodeType kind = Of((byte)id);
            forms[id] = kind == null ? AirTag
                : kind.Form == NodeForm.Raw ? RawTag : ParticleTag;
        }

        return forms;
    }
}
