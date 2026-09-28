using Godot;

namespace GameBase.Nodes;

/// <summary>
/// Where a ray met the world, and what it met.
///
/// <see cref="Cell"/> is the node that was hit. <see cref="Outside"/> is the
/// cell on the near side of the hit face -- where a node placed against that
/// face would go. For a particle hit the surface is smooth and the cells are
/// only approximate: the point and normal are what describe it.
/// </summary>
public readonly struct NodeHit
{
    public NodeHit(Vector3 point, Vector3 normal, NodeType type, Vector3I cell, Vector3I outside)
    {
        Point = point;
        Normal = normal;
        Type = type;
        Cell = cell;
        Outside = outside;
    }

    /// <summary>The hit point, in world space.</summary>
    public Vector3 Point { get; }

    /// <summary>The surface normal at the hit, in world space.</summary>
    public Vector3 Normal { get; }

    /// <summary>The kind of node hit. Never null for a real hit.</summary>
    public NodeType Type { get; }

    public Vector3I Cell { get; }

    public Vector3I Outside { get; }
}
