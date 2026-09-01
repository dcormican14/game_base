using Godot;
using System;
using System.Collections.Generic;

namespace GameBase.Blocks;

/// <summary>
/// A world-space grid of uniform cubes. Blocks live on a global lattice, so
/// anything placed anywhere lines up with everything else — unlike the bismuth
/// blobs, whose jittered per-instance grids made cells non-uniform and confined
/// edits to a single blob.
///
/// One mesh and one collision shape are rebuilt from the block set, emitting
/// only faces whose neighbour is absent. Blocks alternate colour on all three
/// axes so each cube reads as a discrete solid.
///
/// The block set is the whole state: no generated field underneath, so there
/// is nothing to diff against and every cube is equally editable.
/// </summary>
[Tool]
public partial class BlockWorld : StaticBody3D
{
    private float _blockSize = 1f;
    private Color _colorA = new(0.30f, 0.30f, 0.34f);
    private Color _colorB = new(0.62f, 0.62f, 0.66f);

    private bool _bismuth = true;
    private int _seed = 1337;
    private float _flowScale = 6f;
    private float _roughness = 0.35f;
    private float _growth = 0.7f;

    [Export(PropertyHint.Range, "0.1,4,0.05")]
    public float BlockSize
    {
        get => _blockSize;
        set { _blockSize = Mathf.Max(0.05f, value); RebuildIfReady(); }
    }

    [Export]
    public Color ColorA { get => _colorA; set { _colorA = value; RebuildIfReady(); } }

    [Export]
    public Color ColorB { get => _colorB; set { _colorB = value; RebuildIfReady(); } }

    [ExportGroup("Bismuth")]
    /// <summary>Shape blocks as tiered crystal instead of plain cubes. The
    /// grid, picking and editing are unchanged either way — only the geometry
    /// inside each cell differs.</summary>
    [Export]
    public bool Bismuth { get => _bismuth; set { _bismuth = value; InvalidateField(); } }

    /// <summary>Seed of the flow field. Same seed, same planet.</summary>
    [Export]
    public int Seed { get => _seed; set { _seed = value; InvalidateField(); } }

    /// <summary>Cells per lobe of the flow. Larger means long, lazy currents
    /// through the rock; smaller means a busier, more granular crystal.</summary>
    [Export(PropertyHint.Range, "1,32,0.5")]
    public float FlowScale { get => _flowScale; set { _flowScale = Mathf.Max(0.5f, value); InvalidateField(); } }

    /// <summary>0 lets the flow decide every contest, so growth runs in long
    /// directional currents; 1 makes contests essentially random and the
    /// crystal chaotic.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float Roughness { get => _roughness; set { _roughness = Mathf.Clamp(value, 0f, 1f); InvalidateField(); } }

    /// <summary>How many edge and corner contests are awarded at all. 0 leaves
    /// every rim flat (plain cubes); 1 claims every one.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")]
    public float Growth { get => _growth; set { _growth = Mathf.Clamp(value, 0f, 1f); InvalidateField(); } }

    [ExportGroup("Starter Fill")]
    /// <summary>Blocks laid down on ready, as a solid slab of this many cells
    /// per side centred on the node. 0 starts empty. A convenience for seeing
    /// the rock without building it by hand — the real world generator will
    /// replace this.</summary>
    [Export(PropertyHint.Range, "0,64,1")]
    public int StarterSize { get; set; } = 12;

    /// <summary>Depth of the starter slab, in blocks.</summary>
    [Export(PropertyHint.Range, "1,32,1")]
    public int StarterDepth { get; set; } = 3;

    /// <summary>Diameter of a floating ball of blocks spawned overhead, for
    /// inspecting the crystal shaping from every angle. 0 for none.</summary>
    [Export(PropertyHint.Range, "0,32,1")]
    public int DemoSphereSize { get; set; } = 9;

    /// <summary>Height of the demo sphere's centre above the node, in blocks.</summary>
    [Export(PropertyHint.Range, "0,64,1")]
    public int DemoSphereHeight { get; set; } = 12;

    private BismuthField _field;

    /// <summary>The flow field, rebuilt lazily after any dial changes.</summary>
    private BismuthField Field => _field ??= new BismuthField(_seed, _flowScale, _roughness, _growth);

    private void InvalidateField()
    {
        _field = null;
        RebuildIfReady();
    }

    /// <summary>Number of blocks currently in the world.</summary>
    public int BlockCount => _blocks.Count;

    private readonly HashSet<Vector3I> _blocks = new();
    private MeshInstance3D _meshInstance;
    private CollisionShape3D _collisionShape;

    private readonly List<Vector3> _vertices = new();
    private readonly List<Vector3> _normals = new();
    private readonly List<Color> _colors = new();
    private readonly List<int> _indices = new();

    public override void _Ready()
    {
        if (_blocks.Count == 0)
            BuildStarterContent();

        Rebuild();
    }

    private void RebuildIfReady()
    {
        if (IsNodeReady())
            Rebuild();
    }

    /// <summary>
    /// Lays down the starter slab and the floating demo ball. Blocks are added
    /// directly rather than through Fill/AddBlock so the whole thing meshes
    /// once at the end instead of once per block.
    /// </summary>
    private void BuildStarterContent()
    {
        // The slab sits ON the ground, its underside at y=0, because the
        // workshop terrain's floor is the plane y=0. Sinking it below that
        // buries the top face in the floor and the two coplanar surfaces
        // z-fight.
        if (StarterSize > 0)
        {
            int half = StarterSize / 2;
            for (int x = -half; x <= half; x++)
                for (int y = 0; y < StarterDepth; y++)
                    for (int z = -half; z <= half; z++)
                        _blocks.Add(new Vector3I(x, y, z));
        }

        // A ball floating overhead, so the crystal can be inspected from every
        // angle — including the undersides, which the slab never shows.
        if (DemoSphereSize > 0)
        {
            float radius = DemoSphereSize * 0.5f;
            int r = Mathf.CeilToInt(radius);
            var centre = new Vector3I(0, DemoSphereHeight, 0);
            for (int x = -r; x <= r; x++)
            {
                for (int y = -r; y <= r; y++)
                {
                    for (int z = -r; z <= r; z++)
                    {
                        // Measure from cell centres, so the ball is symmetric
                        // rather than lopsided toward the origin corner.
                        var d = new Vector3(x, y, z);
                        if (d.Length() <= radius)
                            _blocks.Add(centre + new Vector3I(x, y, z));
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------ block access

    public bool HasBlock(Vector3I cell) => _blocks.Contains(cell);

    /// <summary>Grid cell containing a world-space point.</summary>
    public Vector3I CellAt(Vector3 worldPoint)
    {
        Vector3 local = ToLocal(worldPoint) / _blockSize;
        return new Vector3I(
            Mathf.FloorToInt(local.X),
            Mathf.FloorToInt(local.Y),
            Mathf.FloorToInt(local.Z));
    }

    /// <summary>World-space centre of a cell.</summary>
    public Vector3 CellCentre(Vector3I cell)
    {
        return ToGlobal((new Vector3(cell.X, cell.Y, cell.Z) + Vector3.One * 0.5f) * _blockSize);
    }

    /// <summary>
    /// Runs several edits and meshes once at the end, instead of once per
    /// block. A rebuild costs the same whether one block changed or a hundred,
    /// so any multi-block operation should be wrapped in this.
    /// </summary>
    public void Batch(System.Action edits)
    {
        bool outermost = !_deferRebuild;
        _deferRebuild = true;
        try
        {
            edits();
        }
        finally
        {
            if (outermost)
            {
                _deferRebuild = false;
                if (_rebuildPending)
                {
                    _rebuildPending = false;
                    Rebuild();
                }
            }
        }
    }

    private bool _deferRebuild;
    private bool _rebuildPending;

    /// <summary>Rebuilds now, or marks one pending when inside a Batch.</summary>
    private void RebuildOrDefer()
    {
        if (_deferRebuild)
            _rebuildPending = true;
        else
            Rebuild();
    }

    public bool AddBlock(Vector3I cell)
    {
        if (!_blocks.Add(cell))
            return false;
        RebuildOrDefer();
        return true;
    }

    public bool RemoveBlock(Vector3I cell)
    {
        if (!_blocks.Remove(cell))
            return false;
        RebuildOrDefer();
        return true;
    }

    /// <summary>Fills a solid box of blocks, inclusive of both corners.</summary>
    public void Fill(Vector3I from, Vector3I to)
    {
        var min = new Vector3I(Mathf.Min(from.X, to.X), Mathf.Min(from.Y, to.Y), Mathf.Min(from.Z, to.Z));
        var max = new Vector3I(Mathf.Max(from.X, to.X), Mathf.Max(from.Y, to.Y), Mathf.Max(from.Z, to.Z));
        for (int x = min.X; x <= max.X; x++)
        {
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                    _blocks.Add(new Vector3I(x, y, z));
            }
        }

        RebuildOrDefer();
    }

    public void Clear()
    {
        _blocks.Clear();
        RebuildOrDefer();
    }

    // -------------------------------------------------------------- ray picking

    /// <summary>
    /// Steps a ray through the grid and returns the first block it enters,
    /// plus the empty cell it passed through immediately before — the face it
    /// arrived through, which is where a placed block belongs.
    ///
    /// Stepping in cell units (rather than mapping a physics contact point)
    /// keeps picking unambiguous: a raycast hit lands exactly on a face, which
    /// is a cell boundary, and rounding that point can pick either neighbour.
    /// </summary>
    public bool RayPick(Vector3 worldFrom, Vector3 worldDir, float maxDistance,
        out Vector3I hitCell, out Vector3I emptyCell)
    {
        hitCell = default;
        emptyCell = default;

        float step = _blockSize * 0.2f;
        var previous = CellAt(worldFrom);
        bool started = false;

        for (float travelled = 0f; travelled <= maxDistance; travelled += step)
        {
            Vector3I cell = CellAt(worldFrom + worldDir * travelled);
            if (started && cell == previous)
                continue;

            if (_blocks.Contains(cell))
            {
                hitCell = cell;
                emptyCell = started ? previous : cell;
                return true;
            }

            previous = cell;
            started = true;
        }

        return false;
    }

    // -------------------------------------------------------------------- mesh

    public void Rebuild()
    {
        EnsureChildren();
        if (_bismuth)
            BuildOccupancy();
        _vertices.Clear();
        _normals.Clear();
        _colors.Clear();
        _indices.Clear();

        foreach (Vector3I cell in _blocks)
        {
            Vector3 min = new Vector3(cell.X, cell.Y, cell.Z) * _blockSize;
            Vector3 max = min + Vector3.One * _blockSize;

            // Alternating on all three axes, so no two touching cubes share a
            // shade and every block's shape stays readable.
            Color color = (cell.X + cell.Y + cell.Z) % 2 == 0 ? _colorA : _colorB;

            if (_bismuth)
            {
                AddBismuthBlock(cell, min, color);
                continue;
            }

            AddFace(cell, Vector3I.Up, color,
                new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
                new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z), Vector3.Up);
            AddFace(cell, Vector3I.Down, color,
                new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
                new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, min.Y, max.Z), Vector3.Down);
            AddFace(cell, Vector3I.Right, color,
                new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
                new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, min.Y, max.Z), Vector3.Right);
            AddFace(cell, Vector3I.Left, color,
                new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z),
                new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, min.Y, max.Z), Vector3.Left);
            AddFace(cell, Vector3I.Back, color,
                new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, max.Y, max.Z),
                new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, min.Y, max.Z), Vector3.Back);
            AddFace(cell, Vector3I.Forward, color,
                new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z),
                new Vector3(max.X, max.Y, min.Z), new Vector3(max.X, min.Y, min.Z), Vector3.Forward);
        }

        var mesh = new ArrayMesh();
        if (_vertices.Count > 0)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = _indices.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            // One shared material rather than a fresh one per rebuild: a new
            // StandardMaterial3D every edit means a new shader instance and a
            // cold pipeline cache each time.
            mesh.SurfaceSetMaterial(0, SharedMaterial);
        }

        _meshInstance.Mesh = mesh;
        UpdateCollision(mesh);
    }

    private StandardMaterial3D _material;

    private StandardMaterial3D SharedMaterial => _material ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true,
        Roughness = 1f,
    };

    /// <summary>
    /// Rebuilds the collision shape from the BLOCK HULL rather than from the
    /// rendered bismuth surface.
    ///
    /// Building a trimesh over the detailed surface is by far the most
    /// expensive part of an edit: the physics engine builds a BVH over every
    /// triangle, and the crystal rims multiply the triangle count for detail
    /// no player can feel through a collision capsule. Colliding against plain
    /// cube faces is a fraction of the triangles, and the difference is at
    /// most a quarter-block of surface relief.
    /// </summary>
    private void UpdateCollision(ArrayMesh rendered)
    {
        if (!_bismuth)
        {
            _collisionShape.Shape = _vertices.Count > 0 ? rendered.CreateTrimeshShape() : null;
            return;
        }

        _collisionVertices.Clear();
        foreach (Vector3I cell in _blocks)
        {
            Vector3 min = new Vector3(cell.X, cell.Y, cell.Z) * _blockSize;
            Vector3 max = min + Vector3.One * _blockSize;

            // Only faces with no block behind them, the same hidden-surface
            // rule the renderer uses — an interior wall is unreachable.
            AddCollisionFace(cell, new Vector3I(0, 1, 0),
                new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
                new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z));
            AddCollisionFace(cell, new Vector3I(0, -1, 0),
                new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
                new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, min.Y, max.Z));
            AddCollisionFace(cell, new Vector3I(1, 0, 0),
                new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
                new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, min.Y, max.Z));
            AddCollisionFace(cell, new Vector3I(-1, 0, 0),
                new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z),
                new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, min.Y, max.Z));
            AddCollisionFace(cell, new Vector3I(0, 0, 1),
                new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, max.Y, max.Z),
                new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, min.Y, max.Z));
            AddCollisionFace(cell, new Vector3I(0, 0, -1),
                new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z),
                new Vector3(max.X, max.Y, min.Z), new Vector3(max.X, min.Y, min.Z));
        }

        if (_collisionVertices.Count == 0)
        {
            _collisionShape.Shape = null;
            return;
        }

        // Reusing the shape instance rather than allocating a new one lets the
        // physics server update in place.
        _trimesh ??= new ConcavePolygonShape3D();
        _trimesh.Data = _collisionVertices.ToArray();
        _collisionShape.Shape = _trimesh;
    }

    private readonly List<Vector3> _collisionVertices = new();
    private ConcavePolygonShape3D _trimesh;

    /// <summary>One cube face as two triangles, skipped when a block sits
    /// behind it. ConcavePolygonShape3D takes raw triangle soup, so vertices
    /// are appended directly with no index buffer.</summary>
    private void AddCollisionFace(Vector3I cell, Vector3I direction,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3)
    {
        if (_blocks.Contains(cell + direction))
            return;

        _collisionVertices.Add(v0);
        _collisionVertices.Add(v1);
        _collisionVertices.Add(v2);
        _collisionVertices.Add(v0);
        _collisionVertices.Add(v2);
        _collisionVertices.Add(v3);
    }

    /// <summary>
    /// Emits one bismuth block by copying its prebuilt variant into the mesh.
    ///
    /// This is the whole per-block cost at planet scale: six noise samples to
    /// find the variant, then a straight copy of that variant's quads with a
    /// scale and a translate. No geometry is solved here and no neighbouring
    /// block is consulted for SHAPE — the face field already guarantees the
    /// shapes interlock. Neighbours are consulted only to cull faces that are
    /// buried, which is pure rendering economy and cannot change the surface.
    /// </summary>
    private void AddBismuthBlock(Vector3I cell, Vector3 min, Color color)
    {
        BismuthShape.Variant variant = BismuthShape.Get(Field.MaskFor(cell));
        if (variant.Vertices.Length == 0)
            return;

        float quarter = _blockSize / BismuthShape.Sub;

        // Vertices arrive grouped four per quad, matching the variant's index
        // runs of six, so quads can be skipped without re-indexing anything.
        int quadCount = variant.Vertices.Length / 4;
        for (int q = 0; q < quadCount; q++)
        {
            int source = q * 4;
            if (IsBuried(cell, variant, q))
                continue;

            int start = _vertices.Count;
            for (int v = 0; v < 4; v++)
            {
                _vertices.Add(min + variant.Vertices[source + v] * quarter);
                _normals.Add(variant.Normals[source + v]);
                _colors.Add(color);
            }

            _indices.Add(start);
            _indices.Add(start + 1);
            _indices.Add(start + 2);
            _indices.Add(start);
            _indices.Add(start + 2);
            _indices.Add(start + 3);
        }
    }

    /// <summary>
    /// Is the quarter-cell in front of this quad actually filled by whichever
    /// block owns it? Only then is the quad safe to drop.
    ///
    /// Testing that a neighbouring BLOCK merely exists is not enough. Under
    /// edge-and-corner growth a neighbour's rim can retreat inward, so the
    /// shared boundary stays genuinely exposed even with a solid block next
    /// door — culling on presence alone tears visible holes in the surface.
    /// The occluding quarter-cell can also lie two cells away diagonally, so
    /// the owning block is derived from the cell itself rather than assumed to
    /// be a face neighbour.
    /// </summary>
    private bool IsBuried(Vector3I cell, BismuthShape.Variant variant, int quad)
    {
        // Every quarter-cell the quad covers must be filled by SOMETHING, or
        // part of the quad is still visible and dropping it opens a hole.
        //
        // The question asked is "is this space solid in the world", not "does
        // one particular neighbour fill it". Rims from edge and corner wins
        // reach diagonally, so the block that buries a quad is frequently not
        // the face neighbour a bake-time tag would have named — trusting that
        // tag left two thirds of the emitted surface buried but still drawn.
        int from = variant.OccludedStart[quad];
        int to = variant.OccludedStart[quad + 1];
        if (from == to)
            return false;

        for (int c = from; c < to; c += 3)
        {
            if (!SolidAt(cell,
                    variant.OccludedCells[c],
                    variant.OccludedCells[c + 1],
                    variant.OccludedCells[c + 2]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Every quarter-cell the world occupies, in global quarter-cell
    /// coordinates. Built once per rebuild by letting each block stamp in the
    /// cells it owns, so culling is a single hash lookup.
    ///
    /// The alternative — asking, per quad, which of the 27 blocks around a
    /// quarter-cell might reach into it — re-derives the same answer thousands
    /// of times and measured 15x slower than building this set up front.
    /// </summary>
    private readonly HashSet<Vector3I> _occupied = new();

    private void BuildOccupancy()
    {
        _occupied.Clear();
        foreach (Vector3I cell in _blocks)
        {
            BismuthShape.Mask mask = Field.MaskFor(cell);
            int[] cells = BismuthShape.OccupiedCells(mask);
            for (int c = 0; c < cells.Length; c += 3)
            {
                _occupied.Add(new Vector3I(
                    cell.X * BismuthShape.Sub + cells[c],
                    cell.Y * BismuthShape.Sub + cells[c + 1],
                    cell.Z * BismuthShape.Sub + cells[c + 2]));
            }
        }
    }

    private bool SolidAt(Vector3I cell, int i, int j, int k) =>
        _occupied.Contains(new Vector3I(
            cell.X * BismuthShape.Sub + i,
            cell.Y * BismuthShape.Sub + j,
            cell.Z * BismuthShape.Sub + k));

    /// <summary>Emits one cube face, but only where the neighbour is empty.</summary>
    private void AddFace(Vector3I cell, Vector3I direction, Color color,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 normal)
    {
        if (_blocks.Contains(cell + direction))
            return;

        // Wind so the face points along `normal`.
        Vector3 computed = (v2 - v0).Cross(v1 - v0);
        if (computed.Dot(normal) < 0f)
            (v1, v3) = (v3, v1);

        int start = _vertices.Count;
        _vertices.Add(v0);
        _vertices.Add(v1);
        _vertices.Add(v2);
        _vertices.Add(v3);
        for (int k = 0; k < 4; k++)
        {
            _normals.Add(normal);
            _colors.Add(color);
        }

        _indices.Add(start);
        _indices.Add(start + 1);
        _indices.Add(start + 2);
        _indices.Add(start);
        _indices.Add(start + 2);
        _indices.Add(start + 3);
    }

    private void EnsureChildren()
    {
        if (_meshInstance == null || !IsInstanceValid(_meshInstance))
        {
            _meshInstance = new MeshInstance3D { Name = "BlockMesh" };
            AddChild(_meshInstance);
        }

        if (_collisionShape == null || !IsInstanceValid(_collisionShape))
        {
            _collisionShape = new CollisionShape3D { Name = "BlockCollision" };
            AddChild(_collisionShape);
        }
    }
}
