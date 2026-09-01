using Godot;
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

    /// <summary>Number of blocks currently in the world.</summary>
    public int BlockCount => _blocks.Count;

    private readonly HashSet<Vector3I> _blocks = new();
    private MeshInstance3D _meshInstance;
    private CollisionShape3D _collisionShape;

    private readonly List<Vector3> _vertices = new();
    private readonly List<Vector3> _normals = new();
    private readonly List<Color> _colors = new();
    private readonly List<int> _indices = new();

    public override void _Ready() => Rebuild();

    private void RebuildIfReady()
    {
        if (IsNodeReady())
            Rebuild();
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

    public bool AddBlock(Vector3I cell)
    {
        if (!_blocks.Add(cell))
            return false;
        Rebuild();
        return true;
    }

    public bool RemoveBlock(Vector3I cell)
    {
        if (!_blocks.Remove(cell))
            return false;
        Rebuild();
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

        Rebuild();
    }

    public void Clear()
    {
        _blocks.Clear();
        Rebuild();
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
            mesh.SurfaceSetMaterial(0, new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 1f,
            });
        }

        _meshInstance.Mesh = mesh;
        _collisionShape.Shape = _vertices.Count > 0 ? mesh.CreateTrimeshShape() : null;
    }

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
