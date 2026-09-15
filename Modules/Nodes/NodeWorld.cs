using Godot;
using System;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// A world of cube blocks on the quad sphere, meshed and collided in sections.
///
/// A cell is (u, v, shell) on a <see cref="QuadSphereGrid"/>, stored as one
/// byte of material in a <see cref="NodeChunkStore"/>. Geometry is built per
/// SECTION -- an 8-cell cube of cells -- so one edit rebuilds a few hundred
/// cells rather than a chunk's thirty thousand.
///
/// THE MESHING RULE, IN FULL
///
/// A block is a full cell, so a face is visible exactly when the neighbour on
/// that side is not solid. There is nothing else to it: no shape variants, no
/// sub-cell occupancy, no spill into adjoining cells. Six neighbour lookups per
/// block decide six faces.
///
/// That simplicity is deliberate and hard-won. The previous mesher solved a
/// per-node SHAPE, stamped its sub-cells into a map, and culled faces by
/// probing that map -- which meant the map's coverage and the probe's reach had
/// to agree exactly. They did not, and the disagreement drew faces between
/// solid cells: 74.7% of all emitted geometry was buried inside the planet,
/// rendering as sphere nested inside sphere. A rule with no map cannot drift
/// out of step with itself.
///
/// WHAT "SOLID" MEANS AT THE EDGES
///
/// Two directions leave the grid, and they answer differently:
///
///   - OUTWARD past shell 0 is the sky, which is empty, so the surface shows;
///   - INWARD past the deepest shell is the solid core, which is rock, so the
///     bottom of the world is not drawn.
///
/// Getting the second wrong draws a complete sphere of geometry against the
/// core that nothing can ever see.
/// </summary>
public partial class NodeWorld : StaticBody3D
{
    // ------------------------------------------------------------ appearance

    private float _nodeSize = 1f;
    private Color _colorA = new(0.155f, 0.155f, 0.170f);
    private Color _colorB = new(0.245f, 0.245f, 0.265f);

    [Export(PropertyHint.Range, "0.1,4,0.05")]
    public float NodeSize
    {
        get => _nodeSize;
        set { _nodeSize = Mathf.Max(0.05f, value); RebuildIfReady(); }
    }

    /// <summary>One of the two checkerboard shades.</summary>
    [Export]
    public Color ColorA { get => _colorA; set { _colorA = value; RebuildIfReady(); } }

    /// <summary>The other. Alternating on a 3D checkerboard is what makes a
    /// field of identical cubes readable -- without the shade break the eye
    /// cannot tell where one block ends and the next begins.</summary>
    [Export]
    public Color ColorB { get => _colorB; set { _colorB = value; RebuildIfReady(); } }

    // ----------------------------------------------------------------- grid

    /// <summary>
    /// The grid this world's nodes live on.
    ///
    /// Required: this world has no flat mode. Everything from face culling to
    /// collision asks the grid what lies next to a node, because on a sphere
    /// that question has no arithmetic answer.
    ///
    /// Held as the INTERFACE, not as one grid, so the same world can be a
    /// cubed sphere of four-walled arcs or an icosphere of hexagonal prisms.
    /// Nothing below here knows which -- a node is a polygon swept between two
    /// radii either way.
    /// </summary>
    public INodeGrid Grid { get; set; }

    // ---------------------------------------------------------------- store

    private readonly NodeChunkStore _store = new();

    public NodeChunkStore Store => _store;
    public int NodeCount => _store.NodeCount;
    public int LoadedChunks => _store.ChunkCount;
    public long ApproximateBytes => _store.ApproximateBytes();

    public const int ChunkSize = NodeChunkStore.ChunkSize;

    /// <summary>
    /// Cells along a section's edge.
    ///
    /// The unit of meshing, and deliberately far smaller than a chunk: an edit
    /// dirties the sections within its reach, and a section is a
    /// twenty-seventh of a chunk's volume. That ratio is what keeps mining one
    /// block off the frame budget.
    /// </summary>
    public const int SectionSize = 8;

    private const int SectionsPerChunk = ChunkSize / SectionSize;

    private static Vector3I SectionOf(Vector3I cell) => new(
        FloorDiv(cell.X, SectionSize),
        FloorDiv(cell.Y, SectionSize),
        FloorDiv(cell.Z, SectionSize));

    private static Vector3I SectionOrigin(Vector3I section) => new(
        section.X * SectionSize, section.Y * SectionSize, section.Z * SectionSize);

    private static Vector3I ChunkOf(Vector3I cell) => NodeChunkStore.ChunkOf(cell);

    /// <summary>Floor division, so negatives map to the cell below rather than
    /// truncating toward zero.</summary>
    internal static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        return value % divisor != 0 && (value < 0) != (divisor < 0) ? q - 1 : q;
    }

    // ------------------------------------------------------------- the faces

    /// <summary>
    /// The six faces of a cube, in the cell's own frame.
    ///
    /// The offset is (du, dv, dOut) as <see cref="QuadSphereGrid.Neighbour"/>
    /// reads it, so +Z is OUTWARD. Corners are listed so the quad winds
    /// correctly when the grid maps them; see <see cref="AddFace"/>.
    /// </summary>
    private readonly struct Face
    {
        public readonly Vector3I Step;
        public readonly Vector3 C0, C1, C2, C3;

        public Face(Vector3I step, Vector3 c0, Vector3 c1, Vector3 c2, Vector3 c3)
        {
            Step = step;
            C0 = c0; C1 = c1; C2 = c2; C3 = c3;
        }
    }

    /// <summary>
    /// Every face, with its corners in cell-local 0..1 coordinates.
    ///
    /// Ordered CLOCKWISE seen from outside the block, which is what Godot's
    /// front-face test wants -- the opposite of the usual maths convention, and
    /// the thing to check first when a surface renders invisible from the side
    /// it should be seen from. Listing them explicitly rather than deriving
    /// them from the axis keeps the winding a fact of the table instead of a
    /// rule to get wrong per face.
    ///
    /// Every entry was once wound the other way, so all six faces of every
    /// block were built back-to-front: the stored normal and the triangle
    /// order agreed with EACH OTHER -- which is why a winding-vs-normal check
    /// alone could not see it -- but both pointed into the block. What that
    /// looked like was a planet whose ground could be stood on and mined but
    /// never seen, with its far inner surface showing through from below the
    /// horizon. MeshAudit's winding count is the regression test.
    /// </summary>
    private static readonly Face[] Faces =
    {
        // Outward (+Z): the face that points at the sky.
        new(new Vector3I(0, 0, 1),
            new Vector3(0, 1, 1), new Vector3(1, 1, 1),
            new Vector3(1, 0, 1), new Vector3(0, 0, 1)),

        // Inward (-Z): toward the core.
        new(new Vector3I(0, 0, -1),
            new Vector3(0, 0, 0), new Vector3(1, 0, 0),
            new Vector3(1, 1, 0), new Vector3(0, 1, 0)),

        // +u
        new(new Vector3I(1, 0, 0),
            new Vector3(1, 0, 1), new Vector3(1, 1, 1),
            new Vector3(1, 1, 0), new Vector3(1, 0, 0)),

        // -u
        new(new Vector3I(-1, 0, 0),
            new Vector3(0, 1, 1), new Vector3(0, 0, 1),
            new Vector3(0, 0, 0), new Vector3(0, 1, 0)),

        // +v
        new(new Vector3I(0, 1, 0),
            new Vector3(1, 1, 1), new Vector3(0, 1, 1),
            new Vector3(0, 1, 0), new Vector3(1, 1, 0)),

        // -v
        new(new Vector3I(0, -1, 0),
            new Vector3(0, 0, 1), new Vector3(1, 0, 1),
            new Vector3(1, 0, 0), new Vector3(0, 0, 0)),
    };

    /// <summary>
    /// Is the cell one step this way solid?
    ///
    /// The whole face-culling rule. Off the grid INWARD is the solid core and
    /// counts as solid; off it OUTWARD is the sky and does not.
    ///
    /// UNKNOWN COUNTS AS SOLID. A neighbour whose chunk is not resident yet is
    /// not air — it is unmeasured, and the two must not be confused. The
    /// asymmetry is in what each mistake costs: hiding a face that should have
    /// been drawn is repaired the moment the neighbour lands and the chunk is
    /// re-meshed, while drawing one that should have been hidden is a hole
    /// straight through the world that nothing later takes back. So the
    /// unknown resolves to "covered", and the streamer's job is to re-mesh on
    /// arrival rather than to guarantee the answer was known up front.
    /// </summary>
    private bool RadialSolid(Vector3I cell, int dOut)
    {
        if (!Grid.RadialNeighbour(cell, dOut, out Vector3I at))
        {
            // Left the grid. Inward is the solid core; outward is the sky.
            return dOut < 0;
        }

        return Covered(at);
    }

    /// <summary>
    /// Is the node past one of this node's side walls solid?
    ///
    /// A wall with nothing beyond it is drawn: that only happens where the
    /// lattice itself ends, which is the edge of the world rather than the
    /// inside of it.
    /// </summary>
    private bool WallSolid(Vector3I cell, int wall)
    {
        if (!Grid.WallNeighbour(cell, wall, out Vector3I at))
            return false;

        return Covered(at);
    }

    /// <summary>
    /// Does this node hide the face pointing at it?
    ///
    /// UNKNOWN COUNTS AS SOLID. A neighbour whose chunk is not resident yet is
    /// not air — it is unmeasured, and the two must not be confused. The
    /// asymmetry is in what each mistake costs: hiding a face that should have
    /// been drawn is repaired the moment the neighbour lands and the chunk is
    /// re-meshed, while drawing one that should have been hidden is a hole
    /// straight through the world that nothing later takes back. So the
    /// unknown resolves to "covered", and the streamer's job is to re-mesh on
    /// arrival rather than to guarantee the answer was known up front.
    /// </summary>
    private bool Covered(Vector3I at)
    {
        // Canonical first: on a grid whose seam sites have several spellings,
        // the store only ever holds one of them, and asking under another
        // reports solid rock as empty air.
        at = Grid.Canonical(at);

        bool solid = _store.Has(at, out bool known);

        // Not resident: assume covered. This is what keeps the underside of the
        // loaded region from opening onto the core, whose shells are on the
        // grid but were never streamed in.
        return !known || solid;
    }

    // ----------------------------------------------------------- scene nodes

    private sealed class SectionNodes
    {
        public readonly MeshInstance3D MeshInstance;
        public readonly CollisionShape3D CollisionShape;
        public ConcavePolygonShape3D Trimesh;

        public SectionNodes(Node parent, Vector3I coord)
        {
            MeshInstance = new MeshInstance3D { Name = $"Mesh{coord.X}_{coord.Y}_{coord.Z}" };
            CollisionShape = new CollisionShape3D { Name = $"Col{coord.X}_{coord.Y}_{coord.Z}" };
            parent.AddChild(MeshInstance);
            parent.AddChild(CollisionShape);
        }

        public void Dispose()
        {
            MeshInstance.QueueFree();
            CollisionShape.QueueFree();
        }
    }

    private readonly Dictionary<Vector3I, SectionNodes> _sections = new();

    /// <summary>Sections whose geometry no longer matches the store.</summary>
    private readonly HashSet<Vector3I> _dirty = new();

    /// <summary>Chunks whose sections have been built.</summary>
    /// <remarks>
    /// Recorded rather than inferred from whether a scene node exists, because
    /// a chunk can legitimately mesh to NOTHING -- every section empty, or
    /// every face buried. Judging by scene nodes told the streamer such a chunk
    /// still owed geometry, so it re-queued it forever and the world never
    /// finished loading.
    /// </remarks>
    private readonly HashSet<Vector3I> _meshed = new();

    private readonly List<Vector3I> _scratch = new();

    // Batch state: whether edits are deferred, and what is owed.
    private bool _deferRebuild;
    private bool _rebuildPending;
    private bool _fullRebuildNeeded;

    // --------------------------------------------------------------- scratch

    /// <summary>
    /// Buffers for building one section.
    ///
    /// Reused rather than allocated per section: a build appends a few thousand
    /// vertices, and letting the lists keep their capacity turns a stream of
    /// allocations into none after the first few sections.
    /// </summary>
    private sealed class MeshScratch
    {
        public readonly List<Vector3> Vertices = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Color> Colors = new();
        public readonly List<int> Indices = new();
        public readonly List<Vector3> CollisionVertices = new();

        public void Clear()
        {
            Vertices.Clear();
            Normals.Clear();
            Colors.Clear();
            Indices.Clear();
            CollisionVertices.Clear();
        }
    }

    private readonly MeshScratch _scratchMesh = new();

    private StandardMaterial3D _material;

    /// <summary>One material for the whole world: the colour rides on the
    /// vertices, so every section can share a single shader instance.</summary>
    private StandardMaterial3D SharedMaterial => _material ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true,
        Roughness = 1f,
    };

    // --------------------------------------------------------------- queries

    /// <summary>The material in a cell, or Raw where there is nothing.</summary>
    public NodeMaterial MaterialAt(Vector3I cell)
    {
        byte raw = _store.Get(cell);
        return raw == NodeChunkStore.Air ? NodeMaterial.Raw : (NodeMaterial)raw;
    }

    /// <summary>Is there a block in this cell?</summary>
    public bool HasNode(Vector3I cell) => _store.Has(cell);

    /// <summary>
    /// Is there a block here, in a cell the grid actually has?
    ///
    /// The store does no bounds checking -- it cannot, because on a flat world
    /// every coordinate is a real place. Here a sky point has a NEGATIVE shell,
    /// and a negative index does not fail, it wraps: shell -1 lands in chunk -1
    /// at local slot 31, the top cell of a chunk that may well be solid rock.
    /// So the store cheerfully reported open sky as stone and a ray cast at the
    /// ground stopped a block short of it.
    /// </summary>
    public bool HasSolid(Vector3I cell) => Grid.Contains(cell) && _store.Has(cell);

    /// <summary>The cell containing a world point.</summary>
    public Vector3I CellAt(Vector3 worldPoint) => Grid.CellAt(ToLocal(worldPoint));

    /// <summary>The world position of a cell's centre.</summary>
    public Vector3 CellCentre(Vector3I cell) => ToGlobal(Grid.CentreOf(cell));

    /// <summary>
    /// The corners of a node, for anything drawing an outline around it.
    ///
    /// Handed out rather than recomputed by the caller so a highlight traces
    /// the same shape the mesh does -- a cube drawn from a centre and a size
    /// sits visibly off a curved block, and badly off a hexagonal one.
    ///
    /// Writes the outer ring first and then the inner, and returns how many are
    /// in EACH ring -- so corner `n` and corner `count + n` are the two ends of
    /// the same vertical edge. A caller wanting the whole node needs room for
    /// twice <see cref="INodeGrid.MaxWalls"/>.
    /// </summary>
    public int CellCorners(Vector3I cell, Span<Vector3> corners)
    {
        int walls = Grid.MaxWalls;

        if (corners.Length < walls * 2)
            return 0;

        int count = Grid.TopCorners(cell, corners[..walls]);
        Grid.BottomCorners(cell, corners.Slice(walls, walls));

        // Compacted so the two rings are adjacent even when a node has fewer
        // walls than the grid's maximum, which a pentagon does.
        for (int n = 0; n < count; n++)
            corners[count + n] = corners[walls + n];

        for (int n = 0; n < count * 2; n++)
            corners[n] = ToGlobal(corners[n]);

        return count;
    }

    // ---------------------------------------------------------------- edits

    /// <summary>Puts a block in a cell. False if one was already there.</summary>
    public bool AddNode(Vector3I cell, NodeMaterial material = NodeMaterial.Stone)
    {
        if (!Grid.Contains(cell) || !_store.Set(cell, (byte)material))
            return false;

        MarkDirty(cell);
        RebuildOrDefer();
        return true;
    }

    /// <summary>Takes a block out. False if the cell was already empty.</summary>
    public bool RemoveNode(Vector3I cell)
    {
        if (!_store.Set(cell, NodeChunkStore.Air))
            return false;

        MarkDirty(cell);
        RebuildOrDefer();
        return true;
    }

    /// <summary>Adds a block without triggering a rebuild, for bulk loading.</summary>
    public bool AddNodeGenerated(Vector3I cell, NodeMaterial material) =>
        Grid.Contains(cell) && _store.Set(cell, (byte)material);

    /// <summary>
    /// Marks every section an edit at this cell can change.
    ///
    /// A block's faces depend only on its six neighbours, so an edit changes
    /// geometry at most one cell away -- but that cell may be in an adjoining
    /// section, and on the sphere "one cell away" is a grid step rather than an
    /// addition. Walking the step through the grid is what makes an edit at a
    /// face fold rebuild the sections on both sides of it.
    /// </summary>
    private void MarkDirty(Vector3I cell)
    {
        _dirty.Add(SectionOf(cell));

        // Every node that shares a face with this one: the two radial
        // neighbours and one per side wall. Asked of the grid rather than added
        // to the address, which is what makes an edit at a seam rebuild the
        // sections on both sides of it.
        if (Grid.RadialNeighbour(cell, 1, out Vector3I above))
            _dirty.Add(SectionOf(above));

        if (Grid.RadialNeighbour(cell, -1, out Vector3I below))
            _dirty.Add(SectionOf(below));

        int walls = Grid.WallCount(cell);

        for (int w = 0; w < walls; w++)
        {
            if (Grid.WallNeighbour(cell, w, out Vector3I at))
                _dirty.Add(SectionOf(at));
        }
    }

    // ------------------------------------------------------------ ray picking

    /// <summary>
    /// Steps a ray through the grid and returns the first block it enters, plus
    /// the empty cell it passed through just before -- the face it arrived
    /// through, which is where a placed block belongs.
    /// </summary>
    public bool RayPick(Vector3 worldFrom, Vector3 worldDir, float maxDistance,
        out Vector3I hitCell, out Vector3I emptyCell)
    {
        hitCell = default;
        emptyCell = default;

        // Stepped against the GRID's node size, not the world's exported one.
        // They are the same on the sphere worlds and not on the organic
        // planet, whose nodes are twice the default -- and a step sized to the
        // wrong number either walks past cells or samples each of them many
        // times over.
        float node = Grid?.NodeSize ?? _nodeSize;

        // A TWENTIETH OF A NODE, not a fifth.
        //
        // The step decides how far past a surface the first sample inside it
        // can land, and that overshoot is along the RAY -- so at the shallow
        // angle a player looks at the ground ahead of them, a fifth of a node
        // of overshoot is several units sideways and the pick lands one or two
        // nodes past the one under the crosshair. Measured at a fifth, 137 of
        // 300 rays picked the wrong node.
        //
        // The cost is linear and small: a few hundred samples over a reach of
        // a few nodes, once per frame.
        float step = node * 0.05f;

        Vector3I previous = CellAt(worldFrom);
        bool started = false;

        for (float travelled = 0f; travelled <= maxDistance; travelled += step)
        {
            Vector3I cell = CellAt(worldFrom + worldDir * travelled);
            if (started && cell == previous)
                continue;

            if (HasSolid(cell))
            {
                hitCell = cell;

                // The empty cell is what a right-click builds into. Reported as
                // the cell the ray last passed through, whether or not the grid
                // contains it -- a caller that wants to BUILD there must test
                // Contains, and on a planet whose surface is a hard boundary
                // the honest answer above the outermost node is often "nowhere",
                // which is not the same as "here".
                emptyCell = started ? previous : cell;
                return true;
            }

            previous = cell;
            started = true;
        }

        return false;
    }

    // --------------------------------------------------------------- meshing

    /// <summary>
    /// Builds one section's render geometry and collision hull.
    ///
    /// PURE COMPUTATION -- touches no engine object, so it can run on any
    /// thread. It reads the store, which is immutable while a mesh job is
    /// outstanding, and writes only into the scratch it was handed.
    /// </summary>
    private void BuildSectionGeometry(Vector3I section, MeshScratch scratch)
    {
        scratch.Clear();

        Vector3I origin = SectionOrigin(section);

        // A grid whose nodes are not prisms hands over its faces directly.
        //
        // Taken BEFORE the shell test below, because that test does not apply
        // here: on a polyhedral grid the address is a position in space, so its
        // Z is an axis like any other rather than a depth. Rejecting negative Z
        // there threw away everything on one side of the planet -- exactly half
        // the world, which is what it looked like.
        if (Grid is IPolyhedralGrid polyhedral)
        {
            BuildPolyhedralSection(origin, polyhedral, scratch);
            return;
        }

        // A section spans one range of shells, and a shell outside the planet
        // holds nothing -- so a section wholly above the surface or below the
        // core is settled without looking at a cell.
        if (origin.Z + SectionSize <= 0 || origin.Z >= Grid.ShellCount)
            return;

        int maxWalls = Grid.MaxWalls;

        Span<Vector3> top = stackalloc Vector3[maxWalls];
        Span<Vector3> bottom = stackalloc Vector3[maxWalls];
        Span<Vector3> quad = stackalloc Vector3[4];

        for (int lu = 0; lu < SectionSize; lu++)
        {
            for (int lv = 0; lv < SectionSize; lv++)
            {
                for (int ls = 0; ls < SectionSize; ls++)
                {
                    var cell = new Vector3I(origin.X + lu, origin.Y + lv, origin.Z + ls);

                    byte raw = _store.Get(cell);
                    if (raw == NodeChunkStore.Air)
                        continue;

                    if (!Grid.Contains(cell))
                        continue;

                    // Only the address the node is stored under builds
                    // geometry. On a grid where a seam site has several
                    // spellings, meshing each of them would stack the same
                    // node's faces on top of one another.
                    if (Grid.Canonical(cell) != cell)
                        continue;

                    Color color = ShadeOf(cell, (NodeMaterial)raw);

                    int walls = Grid.TopCorners(cell, top);
                    if (walls < 3)
                        continue;

                    Grid.BottomCorners(cell, bottom);

                    // OUTER FACE, toward the sky.
                    if (!RadialSolid(cell, 1))
                        AddPolygon(top, walls, color, scratch, false);

                    // INNER FACE, toward the core. Wound the other way so it
                    // faces inward.
                    if (!RadialSolid(cell, -1))
                        AddPolygon(bottom, walls, color, scratch, true);

                    // SIDE WALLS. Wall w spans corner w to corner w+1 at both
                    // radii, which is the contract INodeGrid guarantees -- so a
                    // wall is built without asking the grid anything more.
                    for (int w = 0; w < walls; w++)
                    {
                        if (WallSolid(cell, w))
                            continue;

                        int next = (w + 1) % walls;

                        quad[0] = bottom[w];
                        quad[1] = bottom[next];
                        quad[2] = top[next];
                        quad[3] = top[w];

                        AddFace(quad, color, scratch);
                        AddCollisionQuad(quad, scratch);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Builds one section of a world whose nodes are arbitrary polyhedra.
    ///
    /// The rule is the same as for prisms and simpler to state: a face is drawn
    /// when the node behind it is not solid. What differs is that the faces
    /// come from the grid rather than being derived from two caps and a corner
    /// ring, because an organic cell has no top and bottom to derive them from.
    /// </summary>
    private void BuildPolyhedralSection(Vector3I origin, IPolyhedralGrid polyhedral,
        MeshScratch scratch)
    {
        int maxWalls = Grid.MaxWalls;

        Span<Vector3I> walls = stackalloc Vector3I[maxWalls];
        Span<int> sides = stackalloc int[maxWalls];
        Span<Vector3> corners = stackalloc Vector3[polyhedral.MaxFaceCorners];
        Span<Vector3> face = stackalloc Vector3[Hull.MaxCorners];

        for (int lu = 0; lu < SectionSize; lu++)
        {
            for (int lv = 0; lv < SectionSize; lv++)
            {
                for (int ls = 0; ls < SectionSize; ls++)
                {
                    var cell = new Vector3I(origin.X + lu, origin.Y + lv, origin.Z + ls);

                    byte raw = _store.Get(cell);
                    if (raw == NodeChunkStore.Air)
                        continue;

                    if (!Grid.Contains(cell))
                        continue;

                    Color color = ShadeOf(cell, (NodeMaterial)raw);

                    int count = polyhedral.Faces(cell, walls, sides, corners);
                    int at = 0;

                    for (int n = 0; n < count; n++)
                    {
                        int span = sides[n];

                        if (span < 3)
                        {
                            at += span;
                            continue;
                        }

                        // A face named by the node itself is the planet's own
                        // surface: there is nothing behind it to cover it, so it
                        // is always drawn.
                        bool boundary = walls[n] == cell;

                        if (!boundary && Covered(walls[n]))
                        {
                            at += span;
                            continue;
                        }

                        int written = Mathf.Min(span, face.Length);

                        for (int c = 0; c < written; c++)
                            face[c] = corners[at + c];

                        AddPolygon(face, written, color, scratch, false);
                        at += span;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Emits one cap of a node, fanned from its first corner.
    ///
    /// A fan rather than a strip because the polygon is convex by construction
    /// -- it is a Voronoi cell -- and a fan from a convex polygon needs no
    /// tessellation pass.
    /// </summary>
    private static void AddPolygon(ReadOnlySpan<Vector3> corners, int count,
        Color color, MeshScratch scratch, bool flip)
    {
        Span<Vector3> triangle = stackalloc Vector3[4];

        for (int c = 1; c + 1 < count; c++)
        {
            if (flip)
            {
                triangle[0] = corners[0];
                triangle[1] = corners[c + 1];
                triangle[2] = corners[c];
            }
            else
            {
                triangle[0] = corners[0];
                triangle[1] = corners[c];
                triangle[2] = corners[c + 1];
            }

            // A degenerate fourth corner keeps AddFace's quad shape; the
            // duplicate vertex costs nothing and collapses to a triangle.
            triangle[3] = triangle[2];

            AddFace(triangle, color, scratch);
            AddCollisionQuad(triangle, scratch);
        }
    }

    /// <summary>Adds a quad's two triangles to the collision hull.</summary>
    private static void AddCollisionQuad(ReadOnlySpan<Vector3> quad, MeshScratch scratch)
    {
        // Collision uses the same quads as the render mesh. They are the same
        // surface, and building one from the other is what keeps what you stand
        // on identical to what you see.
        scratch.CollisionVertices.Add(quad[0]);
        scratch.CollisionVertices.Add(quad[1]);
        scratch.CollisionVertices.Add(quad[2]);
        scratch.CollisionVertices.Add(quad[0]);
        scratch.CollisionVertices.Add(quad[2]);
        scratch.CollisionVertices.Add(quad[3]);
    }

    /// <summary>
    /// Which of the two shades a node takes.
    ///
    /// Alternating so neighbouring nodes rarely share a shade, because a field
    /// of identical blocks is unreadable without the break -- the eye cannot
    /// tell where one ends and the next begins.
    ///
    /// HASHED, NOT SUMMED. The obvious parity -- (x + y + z) odd or even --
    /// two-colours a square lattice properly but fails on a triangular one:
    /// stepping along its axes changes the sum by one in a pattern that lines
    /// up diagonally, so the world comes out in wide zigzag STRIPES rather than
    /// in nodes. Hashing the address scatters the choice instead, so adjacent
    /// nodes differ about half the time whatever the lattice is shaped like,
    /// and the eye reads individual cells.
    /// </summary>
    private Color ShadeOf(Vector3I cell, NodeMaterial material)
    {
        bool even = Scatter(cell);

        // Stone alone takes the world's exported colours, so the cubed sphere
        // and the icosphere can still be recoloured from the inspector. Every
        // other kind answers for itself.
        if (material == NodeMaterial.Stone)
            return even ? _colorA : _colorB;

        NodeType kind = NodeTypes.Of(material);
        return even ? kind.ShadeA : kind.ShadeB;
    }

    /// <summary>
    /// One bit of hash from a node's address.
    ///
    /// A cheap integer mix: the exact constants matter less than that nearby
    /// addresses land on uncorrelated bits, which is what keeps the shading
    /// from forming a pattern of its own.
    /// </summary>
    private static bool Scatter(Vector3I cell)
    {
        unchecked
        {
            int hash = cell.X * 73856093 ^ cell.Y * 19349663 ^ cell.Z * 83492791;

            hash ^= hash >> 13;
            hash *= 1274126177;
            hash ^= hash >> 16;

            return (hash & 1) == 0;
        }
    }

    /// <summary>
    /// Emits one quad, with a flat normal and the winding Godot wants.
    ///
    /// The normal is computed from the corners rather than taken from the face
    /// table, because on a sphere a face is not flat in world space -- its
    /// normal depends on where the cell sits, and a table could only hold the
    /// direction it would have had on a flat lattice. Deriving it here is also
    /// what guarantees the normal and the winding agree: both come from the
    /// same three corners.
    /// </summary>
    private static void AddFace(ReadOnlySpan<Vector3> corners, Color color,
        MeshScratch scratch)
    {
        // (c2 - c0) x (c1 - c0), NOT the other way round.
        //
        // Godot's front face is the one whose triangle winds CLOCKWISE as seen
        // from the front, which is the opposite of the usual maths convention.
        // So the normal agreeing with the winding is the reversed cross
        // product, and MeshAudit's winding check tests exactly this.
        //
        // Swapping the operands to the textbook order makes every quad in the
        // world inside-out: the normal points into the planet while the
        // triangles still wind for the outward face. Measured after doing just
        // that: 0 quads agreed and 47264 were inside out, and the surface went
        // invisible from above while the planet's far side showed through it.
        Vector3 normal = (corners[2] - corners[0]).Cross(corners[1] - corners[0]);

        float length = normal.Length();
        normal = length < 0.000001f ? Vector3.Up : normal / length;

        int start = scratch.Vertices.Count;

        for (int i = 0; i < 4; i++)
        {
            scratch.Vertices.Add(corners[i]);
            scratch.Normals.Add(normal);
            scratch.Colors.Add(color);
        }

        scratch.Indices.Add(start);
        scratch.Indices.Add(start + 1);
        scratch.Indices.Add(start + 2);
        scratch.Indices.Add(start);
        scratch.Indices.Add(start + 2);
        scratch.Indices.Add(start + 3);
    }

    /// <summary>
    /// Hands finished geometry to the rendering and physics servers.
    ///
    /// MAIN THREAD ONLY. Also the cheap half: measured at 0.03 ms to upload a
    /// section's mesh against several milliseconds to compute it.
    /// </summary>
    private void ApplySectionGeometry(Vector3I section, MeshScratch scratch)
    {
        if (scratch.Vertices.Count == 0)
        {
            // Nothing to draw: drop the scene nodes rather than leaving an
            // empty mesh behind, so a section carved away stops costing.
            if (_sections.TryGetValue(section, out SectionNodes empty))
            {
                empty.Dispose();
                _sections.Remove(section);
            }

            return;
        }

        if (!_sections.TryGetValue(section, out SectionNodes target))
        {
            target = new SectionNodes(this, section);
            _sections[section] = target;
        }

        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = scratch.Vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = scratch.Normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = scratch.Colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = scratch.Indices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, SharedMaterial);

        target.MeshInstance.Mesh = mesh;

        if (scratch.CollisionVertices.Count == 0)
        {
            target.CollisionShape.Shape = null;
            return;
        }

        // Reusing the shape instance lets the physics server update in place;
        // re-assigning Shape would re-register it every rebuild.
        if (target.Trimesh == null)
        {
            target.Trimesh = new ConcavePolygonShape3D
            {
                Data = scratch.CollisionVertices.ToArray(),
            };

            target.CollisionShape.Shape = target.Trimesh;
        }
        else
        {
            target.Trimesh.Data = scratch.CollisionVertices.ToArray();
        }
    }

    /// <summary>
    /// Builds and uploads one section on the CALLING thread.
    ///
    /// The synchronous path, for the editor rebuild and the whole-world build
    /// where there is no frame to protect. Streaming uses
    /// <see cref="FlushQueuedMeshes"/> instead, which hands the building half
    /// to workers and keeps only the upload here.
    ///
    /// Safe alongside those workers because it does not share their buffers:
    /// this one owns _scratchMesh and each worker is handed its own. The two
    /// write to different sections, and the upload is main-thread either way.
    /// </summary>
    private void MeshSection(Vector3I section)
    {
        BuildSectionGeometry(section, _scratchMesh);
        ApplySectionGeometry(section, _scratchMesh);
        _built++;
    }

    // ----------------------------------------------------------- rebuilding

    /// <summary>Rebuilds every resident section. For a wholesale change.</summary>
    public void Rebuild()
    {
        DiscardEmptyChunks();

        foreach (var kv in _store.Chunks)
        {
            if (kv.Value.SolidCount > 0)
                QueueChunkSections(kv.Key);
        }

        foreach (Vector3I section in _dirty)
            MeshSection(section);

        _dirty.Clear();
        BuildProgress = 1f;

        if (!_ready)
        {
            _ready = true;
            WorldReady?.Invoke();
        }
    }

    /// <summary>Re-meshes only the sections marked dirty.</summary>
    private void RebuildDirty()
    {
        if (_dirty.Count == 0)
            return;

        foreach (Vector3I section in _dirty)
            MeshSection(section);

        _dirty.Clear();
    }

    private void RebuildOrDefer()
    {
        if (_deferRebuild)
        {
            _rebuildPending = true;
            return;
        }

        RebuildDirty();
    }

    private void RebuildIfReady()
    {
        if (_ready)
            Rebuild();
    }

    /// <summary>
    /// Defers rebuilds until <see cref="EndBatch"/>, for a run of edits that
    /// would otherwise each pay for their own re-mesh.
    /// </summary>
    public void BeginBatch() => _deferRebuild = true;

    public void EndBatch()
    {
        _deferRebuild = false;

        if (_fullRebuildNeeded)
        {
            _fullRebuildNeeded = false;
            _rebuildPending = false;
            Rebuild();
            return;
        }

        if (_rebuildPending)
        {
            _rebuildPending = false;
            RebuildDirty();
        }
    }

    /// <summary>Drops chunks that turned out to hold nothing.</summary>
    private void DiscardEmptyChunks()
    {
        _scratch.Clear();

        foreach (var kv in _store.Chunks)
        {
            if (kv.Value.SolidCount == 0)
                _scratch.Add(kv.Key);
        }

        foreach (Vector3I chunk in _scratch)
            _store.Unload(chunk);
    }

    private void QueueChunkSections(Vector3I chunk)
    {
        Vector3I baseSection = new(
            chunk.X * SectionsPerChunk,
            chunk.Y * SectionsPerChunk,
            chunk.Z * SectionsPerChunk);

        for (int x = 0; x < SectionsPerChunk; x++)
            for (int y = 0; y < SectionsPerChunk; y++)
                for (int z = 0; z < SectionsPerChunk; z++)
                {
                    _dirty.Add(new Vector3I(
                        baseSection.X + x, baseSection.Y + y, baseSection.Z + z));
                }
    }

    // --------------------------------------------------------- streaming API

    public bool IsChunkLoaded(Vector3I chunk) => _store.IsLoaded(chunk);

    /// <summary>
    /// Does this chunk have geometry built?
    ///
    /// Distinct from having DATA: the streamer generates a margin of chunks
    /// beyond what it draws, purely so the chunks inside can cull their
    /// boundary faces against real neighbours.
    /// </summary>
    public bool HasChunkMesh(Vector3I chunk) => _meshed.Contains(chunk);

    /// <summary>
    /// How many SECTIONS have had geometry built.
    ///
    /// The honest numerator for a progress bar. Chunks are the wrong unit: a
    /// chunk of pure sky is marked meshed the instant residency rejects it,
    /// without any work being done, so the chunk count starts in the hundreds
    /// and barely moves -- measured, it sat at 115 of 179 for an entire load
    /// and the bar read a constant 64%.
    ///
    /// A section is only counted when its geometry has actually been uploaded,
    /// so this rises with the work the player is waiting through.
    /// </summary>
    public int BuiltSections => _built;

    /// <summary>Sections whose geometry has been uploaded.</summary>
    private int _built;

    public void MarkChunkMeshed(Vector3I chunk) => _meshed.Add(chunk);

    /// <summary>Queues a chunk for meshing, for the streamer.</summary>
    public void QueueChunkMesh(Vector3I chunk)
    {
        QueueChunkSections(chunk);
        _meshed.Add(chunk);
    }

    /// <summary>
    /// Is there meshing still to do?
    ///
    /// Counts work IN FLIGHT as well as work queued. A section handed to a
    /// worker has left the dirty set but has not been uploaded, and reporting
    /// the world finished at that moment lets the streamer call itself ready
    /// with geometry still on its way.
    /// </summary>
    public bool HasQueuedMeshes =>
        _dirty.Count > 0 || _building > 0 || !_finished.IsEmpty;

    /// <summary>
    /// Builds queued sections until the budget runs out.
    ///
    /// Returns true while work remains. The budget is what keeps a burst of
    /// newly streamed chunks from landing as one long frame.
    /// </summary>
    public bool FlushQueuedMeshes(float budgetMs)
    {
        // Take back whatever the workers finished, first: uploading is the
        // cheap half and the frame should spend its budget on that rather than
        // on starting more work it will not collect.
        CollectMeshed();

        if (_dirty.Count == 0)
            return _building > 0;

        ulong deadline = Time.GetTicksUsec() + (ulong)(Mathf.Max(1f, budgetMs) * 1000f);

        _scratch.Clear();
        _scratch.AddRange(_dirty);

        int started = 0;

        foreach (Vector3I section in _scratch)
        {
            if (ForceSingleThread)
            {
                _dirty.Remove(section);
                MeshSection(section);
                started++;
                if ((started & 3) == 0 && Time.GetTicksUsec() >= deadline) break;
                continue;
            }

            if (_building >= MeshWorkers)
                break;

            _dirty.Remove(section);
            Dispatch(section);
            started++;

            if ((started & 3) == 0 && Time.GetTicksUsec() >= deadline)
                break;
        }

        return _dirty.Count > 0 || _building > 0;
    }

    /// <summary>
    /// Sections being built on worker threads, and the results waiting to be
    /// uploaded.
    ///
    /// BUILDING A SECTION IS PURE COMPUTATION -- it reads the store and writes
    /// into scratch it was handed, touching no engine object -- so it belongs
    /// off the frame. Only the upload has to be on the main thread, and that
    /// was measured at 0.03 ms against several milliseconds to compute.
    ///
    /// Leaving it all in the frame is what capped the even-node world at 47 fps
    /// with 110 ms spikes: its nodes are hexagonal prisms whose corners come
    /// from a neighbour ring, so a section costs several times what the cubed
    /// sphere's does, and every millisecond of it landed between two frames.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<MeshResult> _finished = new();

    private int _building;

    /// <summary>
    /// How many sections may be built at once.
    ///
    /// One per core, less the one the frame itself needs. Generation uses the
    /// same pool, so this is a share of the machine rather than all of it.
    /// </summary>
    private static readonly int MeshWorkers =
        Mathf.Max(1, System.Environment.ProcessorCount - 1);

    /// <summary>
    /// Build sections on the calling thread instead of on workers.
    ///
    /// For diagnosis: when geometry comes out wrong, this says in one run
    /// whether threading is the cause or whether the mesher was already wrong.
    /// It answered exactly that once -- the fault was a reversed neighbour ring,
    /// not a race, and the single-threaded run failing identically is what
    /// ruled the threading out.
    /// </summary>
    public static bool ForceSingleThread;

    private sealed class MeshResult
    {
        public Vector3I Section;
        public MeshScratch Scratch;
        public int Generation;
    }

    /// <summary>
    /// Bumped whenever the world is dropped or its grid replaced.
    ///
    /// A worker started against the old world finishes against the new one, and
    /// uploading that would put the previous planet's geometry into this one.
    /// Stamping each result and checking it on arrival is cheaper than waiting
    /// for the workers to drain.
    /// </summary>
    private int _generation;

    /// <summary>Scratch buffers handed back after upload, so a section does not
    /// allocate a fresh set of lists every time it is rebuilt.</summary>
    private readonly System.Collections.Concurrent.ConcurrentBag<MeshScratch> _spare = new();

    /// <summary>Starts one section building on a worker.</summary>
    private void Dispatch(Vector3I section)
    {
        if (!_spare.TryTake(out MeshScratch scratch))
            scratch = new MeshScratch();

        System.Threading.Interlocked.Increment(ref _building);

        int generation = _generation;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                BuildSectionGeometry(section, scratch);

                _finished.Enqueue(new MeshResult
                {
                    Section = section, Scratch = scratch, Generation = generation,
                });
            }
            catch (System.Exception error)
            {
                // A worker that throws must not take the count with it, or the
                // streamer waits forever on a section that will never arrive.
                GD.PushError($"NodeWorld: meshing {section} failed - {error.Message}");
                _finished.Enqueue(new MeshResult { Section = section, Scratch = null });
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref _building);
            }
        });
    }

    /// <summary>Uploads whatever the workers have finished.</summary>
    private void CollectMeshed()
    {
        while (_finished.TryDequeue(out MeshResult done))
        {
            if (done.Scratch == null)
                continue;

            // Built against a world that has since been dropped.
            if (done.Generation != _generation)
            {
                done.Scratch.Clear();
                _spare.Add(done.Scratch);
                continue;
            }

            ApplySectionGeometry(done.Section, done.Scratch);
            _built++;

            done.Scratch.Clear();
            _spare.Add(done.Scratch);
        }
    }

    public void UnloadChunk(Vector3I chunk)
    {
        Vector3I baseSection = new(
            chunk.X * SectionsPerChunk,
            chunk.Y * SectionsPerChunk,
            chunk.Z * SectionsPerChunk);

        for (int x = 0; x < SectionsPerChunk; x++)
            for (int y = 0; y < SectionsPerChunk; y++)
                for (int z = 0; z < SectionsPerChunk; z++)
                {
                    var section = new Vector3I(
                        baseSection.X + x, baseSection.Y + y, baseSection.Z + z);

                    if (_sections.TryGetValue(section, out SectionNodes scene))
                    {
                        scene.Dispose();
                        _sections.Remove(section);
                    }

                    _dirty.Remove(section);
                }

        _store.Unload(chunk);
        _meshed.Remove(chunk);
    }

    /// <summary>Drops everything: data, geometry and readiness.</summary>
    public void Clear()
    {
        foreach (SectionNodes scene in _sections.Values)
            scene.Dispose();

        _sections.Clear();
        _dirty.Clear();
        _meshed.Clear();
        _store.Clear();
        _built = 0;

        // Whatever the workers are still building describes the world that was
        // just dropped. Their results are discarded on arrival rather than
        // waited for, since the store they would be uploaded against is gone.
        _generation++;

        while (_finished.TryDequeue(out _))
        {
        }

        _ready = false;
        BuildProgress = 0f;
    }

    // -------------------------------------------------------------- readiness

    /// <summary>0..1 while the world is meshing, 1 once it is done.</summary>
    public float BuildProgress { get; private set; }

    public bool IsWorldReady => _ready;

    /// <summary>Raised once, when the world first has geometry worth playing in.</summary>
    public event Action WorldReady;

    private bool _ready;

    /// <summary>Marks the world ready, for a streamer that decides readiness by
    /// its own rule rather than by everything being built.</summary>
    public void MarkReady()
    {
        if (_ready)
            return;

        _ready = true;
        BuildProgress = 1f;
        WorldReady?.Invoke();
    }
}
