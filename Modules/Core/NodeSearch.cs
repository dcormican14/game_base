using Godot;

namespace GameBase.Core;

/// <summary>
/// Finds a node by type, so modules can locate the camera, player or world
/// they need without the scene having to wire every path by hand.
/// </summary>
public static class NodeSearch
{
    /// <summary>
    /// First node of type T at or under `root`, depth-first; null if none.
    /// </summary>
    public static T FindByType<T>(Node root) where T : Node
    {
        if (root == null)
            return null;
        if (root is T match)
            return match;

        foreach (Node child in root.GetChildren())
        {
            T found = FindByType<T>(child);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// First node at or under `root` that implements an interface, depth-first;
    /// null if none. For finding a collaborator by what it can do rather than
    /// by what it is.
    /// </summary>
    public static T FindImplementing<T>(Node root) where T : class
    {
        if (root == null)
            return null;
        if (root is T match)
            return match;

        foreach (Node child in root.GetChildren())
        {
            T found = FindImplementing<T>(child);
            if (found != null)
                return found;
        }

        return null;
    }
}
