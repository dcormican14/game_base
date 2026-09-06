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
/// <summary>
/// The directions a node's neighbours lie in, as bit positions in a neighbour
/// mask: the six faces, then the four horizontal diagonals, then the four
/// cells diagonally above the horizontal faces.
///
/// Faces are ordered so the opposite of a face is its index XOR 1, which is
/// what lets a shape mirror itself without a lookup table.
/// </summary>
public static class NodeFace
{
    public const int NegX = 0;
    public const int PosX = 1;
    public const int NegY = 2;
    public const int PosY = 3;
    public const int NegZ = 4;
    public const int PosZ = 5;

    // The four horizontal DIAGONALS, after the faces.
    //
    // A surface material needs these as well as the faces. Where two soil
    // nodes meet a diagonal that is air, each of them bevels the edge running
    // toward the node between them — and that node, whose four faces are all
    // soil, has to cut the corner where those two lines arrive or they stop
    // dead at its boundary.
    public const int NegXNegZ = 6;
    public const int NegXPosZ = 7;
    public const int PosXNegZ = 8;
    public const int PosXPosZ = 9;

    // The four cells diagonally ABOVE the horizontal faces.
    //
    // These say which way the ground RISES. A neighbour being solid only says
    // the ground continues; the cell above that neighbour being solid says it
    // continues one level higher, which is the difference between flat ground
    // and a slope — and a surface material has to build its back up toward a
    // rise or the terrain reads as a staircase of cliffs.
    public const int UpNegX = 10;
    public const int UpPosX = 11;
    public const int UpNegZ = 12;
    public const int UpPosZ = 13;

    /// <summary>How many directions a neighbour mask covers.</summary>
    public const int Count = 14;

    /// <summary>The directions, indexed by the constants above.</summary>
    public static readonly Vector3I[] Offsets =
    {
        new(-1, 0, 0), new(1, 0, 0),
        new(0, -1, 0), new(0, 1, 0),
        new(0, 0, -1), new(0, 0, 1),
        new(-1, 0, -1), new(-1, 0, 1),
        new(1, 0, -1), new(1, 0, 1),
        new(-1, 1, 0), new(1, 1, 0),
        new(0, 1, -1), new(0, 1, 1),
    };

    /// <summary>Is the neighbour in this direction solid?</summary>
    public static bool Has(int mask, int face) => (mask & 1 << face) != 0;
}

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
    /// The shape of the node at `cell`, told which of its six face neighbours
    /// hold something solid.
    ///
    /// For types whose shape is a property of the SURFACE rather than of the
    /// rock itself — soil rounding over where it meets air, and stepping up
    /// where it meets more soil. Such a type cannot answer from position alone,
    /// because where the ground ends is the generator's decision, not a
    /// function anyone can evaluate independently.
    ///
    /// This does not weaken the no-neighbour-queries contract that makes
    /// planet-scale generation work. The mesher already reads the six
    /// neighbours of every node it draws, to cull buried faces; it simply
    /// passes on what it found. Nothing here searches the world, and the
    /// shape stays a pure function of its inputs, so it remains cacheable and
    /// safe to evaluate on any thread in any order.
    ///
    /// `neighbours` is a bitmask indexed by <see cref="NodeFace"/> — six faces,
    /// four horizontal diagonals, and the four cells above the horizontal
    /// faces. The default implementation ignores it, so
    /// a type whose shape owes nothing to its surroundings needs no changes.
    /// </summary>
    NodeShape ShapeAt(Vector3I cell, int neighbours) => ShapeAt(cell);

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

    /// <summary>
    /// Does this shape fill one sub-cell, in node-local coordinates?
    ///
    /// The same answer <see cref="OccupiedCells"/> gives, asked one cell at a
    /// time. Picking needs it: the world's occupancy map records THAT a
    /// sub-cell is filled but not BY WHICH node, and a shape reaching outside
    /// its own cell means the sub-cell's index cannot be divided down to name
    /// its owner. Only the shape itself can answer.
    /// </summary>
    bool Occupies(NodeShape shape, int i, int j, int k);
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
