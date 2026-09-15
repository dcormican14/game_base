using Godot;

namespace GameBase.Nodes;

/// <summary>
/// The six axis directions, and the six cube faces the quad sphere is built on.
///
/// A face of the sphere IS one of these directions, so the same small set names
/// both "which face of the cube" and "which way from a cell". Keeping them as
/// one set is what lets a cell's face index double as its outward orientation
/// without a conversion.
/// </summary>
public static class NodeOrientation
{
    public const int PosY = 0;
    public const int NegY = 1;
    public const int PosX = 2;
    public const int NegX = 3;
    public const int PosZ = 4;
    public const int NegZ = 5;

    /// <summary>How many faces a cube has, and so how many faces the sphere
    /// is divided into.</summary>
    public const int Count = 6;
}

/// <summary>
/// The six directions a block can have a neighbour in, in the cell's own frame.
///
/// The third component is RADIAL, positive meaning OUTWARD -- the convention
/// <see cref="QuadSphereGrid.Neighbour"/> takes. Everything that walks from a
/// cell to its neighbours shares this table, so there is one place to be wrong
/// about the sign rather than one per caller.
/// </summary>
public static class NodeFace
{
    /// <summary>Steps to the six face neighbours: -u, +u, -v, +v, inward,
    /// outward.</summary>
    public static readonly Vector3I[] Offsets =
    {
        new(-1, 0, 0), new(1, 0, 0),
        new(0, -1, 0), new(0, 1, 0),
        new(0, 0, -1), new(0, 0, 1),
    };
}
