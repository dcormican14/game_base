using Godot;

namespace GameBase.Nodes;

/// <summary>
/// A node that is just its cell: a full cube, no growth, no relief.
///
/// This is the trivial <see cref="INodeType"/>, and it exists for two reasons.
/// It gives materials like the islands' dark capping stone a deliberately
/// plain read against the crystal around them, and it is the reference
/// implementation of the interface's contract — a type that fills exactly its
/// own cell tiles space by construction.
///
/// Because it never reaches outside its cell, a plain node next to a raw one
/// still interlocks correctly: the raw node's rims grow into space this node
/// does not claim, and the mesher's occupancy test sees the truth either way.
/// The seam is honest — a bismuth rim may overhang a dark cube — which is
/// what makes the capping layer look laid ON the rock rather than grown from
/// it.
/// </summary>
public sealed class PlainNode : INodeType
{
    public string Id => "plain";

    /// <summary>
    /// Matched to the raw type's subdivision rather than set to 1.
    ///
    /// The world keeps ONE occupancy map on a single sub-cell lattice, and
    /// face culling tests neighbours through it. A type on a coarser lattice
    /// would stamp cells the finer type cannot address, and the two would
    /// disagree about what is solid — so every type in a world must share a
    /// subdivision. This is that shared value.
    /// </summary>
    public int Subdivision => RawNodeGeometry.Sub;

    // Every plain node is the same shape, so there is exactly one handle and
    // one cached mesh for the entire world.
    private static readonly NodeShape TheShape = new(0, 0);

    public NodeShape ShapeAt(Vector3I cell) => TheShape;

    public NodeMesh MeshFor(NodeShape shape) => Mesh;

    public int[] OccupiedCells(NodeShape shape) => Occupied;

    public bool Occupies(NodeShape shape, int i, int j, int k) =>
        i >= 0 && i < Sub && j >= 0 && j < Sub && k >= 0 && k < Sub;

    private static int Sub => RawNodeGeometry.Sub;

    // ------------------------------------------------------------------ mesh

    private static NodeMesh _mesh;
    private static int[] _occupied;

    /// <summary>The cube, in sub-cell units, built once.</summary>
    private static NodeMesh Mesh => _mesh ??= BuildMesh();

    /// <summary>Every sub-cell of the node's own cell, built once.</summary>
    private static int[] Occupied => _occupied ??= BuildOccupied();

    private static int[] BuildOccupied()
    {
        int sub = Sub;
        var cells = new int[sub * sub * sub * 3];
        int at = 0;
        for (int i = 0; i < sub; i++)
            for (int j = 0; j < sub; j++)
                for (int k = 0; k < sub; k++)
                {
                    cells[at++] = i;
                    cells[at++] = j;
                    cells[at++] = k;
                }

        return cells;
    }

    /// <summary>
    /// The six faces of the cube, each one quad spanning the whole cell.
    ///
    /// Each quad's occlusion list is the layer of sub-cells immediately in
    /// front of it — the same test the raw type's quads use — so a plain face
    /// is dropped exactly when the neighbouring geometry actually seals it,
    /// whatever type that neighbour happens to be.
    /// </summary>
    private static NodeMesh BuildMesh()
    {
        int sub = Sub;
        float s = sub;

        // Direction, then the four corners as per-axis picks from (0, sub).
        (Vector3I Dir, int[] Xs, int[] Ys, int[] Zs)[] faces =
        {
            (new Vector3I(0, 1, 0),  new[]{0,1,1,0}, new[]{1,1,1,1}, new[]{0,0,1,1}),
            (new Vector3I(0, -1, 0), new[]{0,1,1,0}, new[]{0,0,0,0}, new[]{0,0,1,1}),
            (new Vector3I(1, 0, 0),  new[]{1,1,1,1}, new[]{0,1,1,0}, new[]{0,0,1,1}),
            (new Vector3I(-1, 0, 0), new[]{0,0,0,0}, new[]{0,1,1,0}, new[]{0,0,1,1}),
            (new Vector3I(0, 0, 1),  new[]{0,0,1,1}, new[]{0,1,1,0}, new[]{1,1,1,1}),
            (new Vector3I(0, 0, -1), new[]{0,0,1,1}, new[]{0,1,1,0}, new[]{0,0,0,0}),
        };

        var vertices = new Vector3[faces.Length * 4];
        var normals = new Vector3[faces.Length * 4];
        var indices = new int[faces.Length * 6];
        var occludedStart = new int[faces.Length + 1];
        var occluded = new System.Collections.Generic.List<int>(faces.Length * sub * sub * 3);

        for (int f = 0; f < faces.Length; f++)
        {
            var face = faces[f];
            var normal = new Vector3(face.Dir.X, face.Dir.Y, face.Dir.Z);

            var corner = new Vector3[4];
            for (int v = 0; v < 4; v++)
            {
                corner[v] = new Vector3(
                    face.Xs[v] == 0 ? 0f : s,
                    face.Ys[v] == 0 ? 0f : s,
                    face.Zs[v] == 0 ? 0f : s);
            }

            // Godot treats clockwise winding as front-facing; flip if this
            // corner order came out the other way for this face.
            if ((corner[2] - corner[0]).Cross(corner[1] - corner[0]).Dot(normal) < 0f)
                (corner[1], corner[3]) = (corner[3], corner[1]);

            int at = f * 4;
            for (int v = 0; v < 4; v++)
            {
                vertices[at + v] = corner[v];
                normals[at + v] = normal;
            }

            indices[f * 6 + 0] = at;
            indices[f * 6 + 1] = at + 1;
            indices[f * 6 + 2] = at + 2;
            indices[f * 6 + 3] = at;
            indices[f * 6 + 4] = at + 2;
            indices[f * 6 + 5] = at + 3;

            // The sub-cell layer just outside this face, in node-local
            // coordinates: the quad is hidden only if every one is filled.
            occludedStart[f] = occluded.Count;

            // The face's own axis is pinned one sub-cell outside the node; the
            // other two sweep the whole face.
            int pinnedAxis = face.Dir.X != 0 ? 0 : face.Dir.Y != 0 ? 1 : 2;
            int pinned = (face.Dir.X + face.Dir.Y + face.Dir.Z) > 0 ? sub : -1;
            int axisA = pinnedAxis == 0 ? 1 : 0;
            int axisB = pinnedAxis == 2 ? 1 : 2;

            var subCell = new int[3];
            subCell[pinnedAxis] = pinned;
            for (int a = 0; a < sub; a++)
            {
                for (int b = 0; b < sub; b++)
                {
                    subCell[axisA] = a;
                    subCell[axisB] = b;
                    occluded.Add(subCell[0]);
                    occluded.Add(subCell[1]);
                    occluded.Add(subCell[2]);
                }
            }
        }

        occludedStart[faces.Length] = occluded.Count;

        return new NodeMesh
        {
            Vertices = vertices,
            Normals = normals,
            Indices = indices,
            OccludedCells = occluded.ToArray(),
            OccludedStart = occludedStart,
        };
    }
}
