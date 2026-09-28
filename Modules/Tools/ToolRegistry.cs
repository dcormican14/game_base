using System.Collections.Generic;
using GameBase.Items;

namespace GameBase.Tools;

/// <summary>
/// Which tool an item is, by its id.
///
/// Items stay plain data (<see cref="ItemType"/> resources); holding one whose
/// id is registered here puts that tool in the player's hand. Adding a tool is
/// a class and one line below.
/// </summary>
public static class ToolRegistry
{
    private static readonly Dictionary<string, ITool> ById = new();

    static ToolRegistry()
    {
        Register(new PickaxeTool());
        Register(new ShovelTool());
    }

    public static void Register(ITool tool) => ById[tool.Id] = tool;

    /// <summary>The tool an item is, or null for an item that is not a tool.</summary>
    public static ITool For(ItemType item) =>
        item != null && ById.TryGetValue(item.Id, out ITool tool) ? tool : null;

    /// <summary>The tool with an id, or null.</summary>
    public static ITool Find(string id) => ById.TryGetValue(id, out ITool tool) ? tool : null;
}
