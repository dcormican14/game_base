using Godot;

namespace GameBase.Nodes;

/// <summary>
/// One way of turning a grid cell into geometry — a "node type".
///
/// A node is a single cell of the world grid. What it looks like is decided by
/// its node type: <see cref="RawNode"/> grows raw bismuth crystal, and other
/// types (smoothed, faceted, ore-veined, machined) can be added by
/// implementing this interface without touching the mesher.
///
/// THE CONTRACT
///
/// Two rules keep a node type usable at planet scale, and both are properties
/// of the type rather than of the mesher:
///
/// 1. **Shape depends only on position and seed.** <see cref="ShapeAt"/> may
///    not consult which cells are filled. That is what lets a node placed
///    mid-game take exactly the shape it would have had if the world had
///    generated it, and lets generation run in any order on any thread.
/// 2. **Shapes must tile space exactly.** The union of what every node
///    occupies must leave no gap and no overlap, so neighbouring nodes fit
///    like puzzle pieces. A type that grows outside its own cell has to
///    guarantee the neighbour it grew into vacated exactly that space.
///
/// The mesher relies on both. It culls a face when the space in front is
/// occupied by anything, and it re-meshes only the cells near an edit — both
/// of which are wrong if shape is order-dependent or if space is not tiled.
/// </summary>
public interface INodeType
{
    /// <summary>Name shown in the editor and used in save data.</summary>
    string Id { get; }

    /// <summary>
    /// Sub-cells per node edge. Geometry is expressed on this finer lattice,
    /// so a node can have relief without leaving its cell's footprint.
    /// </summary>
    int Subdivision { get; }

    /// <summary>
    /// The shape of the node at `cell`, as an opaque handle. Types are free to
    /// key shapes however they like — a bitmask, an index, a hash — so long as
    /// equal handles mean identical geometry, which is what lets the mesher
    /// cache meshes per distinct shape.
    ///
    /// Must be a pure function of `cell` and the type's own seed.
    /// </summary>
    NodeShape ShapeAt(Vector3I cell);

    /// <summary>
    /// The meshed geometry for a shape, in sub-cell units relative to the
    /// node's min corner. Cached by the implementation, since a world reuses
    /// far fewer distinct shapes than it has nodes.
    /// </summary>
    NodeMesh MeshFor(NodeShape shape);

    /// <summary>
    /// The sub-cells this shape fills, as flat (i, j, k) triples in node-local
    /// coordinates. Used to build the world occupancy map that face culling
    /// tests against. May range outside 0..Subdivision-1 where a shape grows
    /// into space a neighbour vacated.
    /// </summary>
    int[] OccupiedCells(NodeShape shape);
}

/// <summary>
/// An opaque handle to one node shape. Equality means identical geometry, so
/// the mesher can cache per shape; the two integers are whatever the node type
/// finds convenient to pack.
/// </summary>
public readonly struct NodeShape : System.IEquatable<NodeShape>
{
    public readonly int A;
    public readonly int B;

    public NodeShape(int a, int b = 0)
    {
        A = a;
        B = b;
    }

    public bool Equals(NodeShape other) => A == other.A && B == other.B;
    public override bool Equals(object obj) => obj is NodeShape other && Equals(other);
    public override int GetHashCode() => A * 397 ^ B;
    public static bool operator ==(NodeShape x, NodeShape y) => x.Equals(y);
    public static bool operator !=(NodeShape x, NodeShape y) => !x.Equals(y);
}

/// <summary>
/// One node shape's geometry, ready to be scaled and translated into a chunk
/// mesh. Quads are stored four vertices at a time, matching index runs of six.
/// </summary>
public sealed class NodeMesh
{
    /// <summary>Vertices in sub-cell units, relative to the node's min corner.
    /// May reach outside 0..Subdivision where a shape grows into a
    /// neighbour's vacated space.</summary>
    public Vector3[] Vertices = System.Array.Empty<Vector3>();

    public Vector3[] Normals = System.Array.Empty<Vector3>();
    public int[] Indices = System.Array.Empty<int>();

    /// <summary>
    /// Per quad, the sub-cells that must all be filled for it to be hidden, as
    /// flat (i, j, k) triples indexed by <see cref="OccludedStart"/>.
    ///
    /// Testing that a neighbouring NODE merely exists is not enough: a shape
    /// whose relief retreats inward leaves the shared boundary exposed even
    /// with a solid neighbour, and culling on presence alone tears holes.
    /// </summary>
    public int[] OccludedCells = System.Array.Empty<int>();

    /// <summary>Per quad, its first index into <see cref="OccludedCells"/>;
    /// the run ends where the next quad's begins. Length is quadCount + 1.</summary>
    public int[] OccludedStart = System.Array.Empty<int>();
}
