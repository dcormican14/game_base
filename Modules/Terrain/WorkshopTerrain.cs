using Godot;
using System.Collections.Generic;

namespace GameBase.Terrain;

/// <summary>
/// Procedural "workshop" terrain: a flat, dark checkerboard floor scattered
/// with seeded box platforms (rectangular prisms / cubes), each reachable via
/// full-width triangular-prism ramps — hard right angles and flat-shaded
/// surfaces throughout, no thin geometry. Some structures are multi-staged:
/// terraced boxes with roof-to-roof ramps climbing to the highest one. Ramp
/// walking surfaces carry the same checkerboard as the floor.
///
/// Attach to a StaticBody3D; it builds its own MeshInstance3D and
/// CollisionShape3D children and, being a [Tool] script, regenerates live in
/// the editor whenever an export changes (after the assembly is built once).
///
/// Placement is deterministic per Seed. An area of SpawnClearRadius around the
/// local origin is kept free of structures so a player can spawn on open floor.
/// </summary>
[Tool]
public partial class WorkshopTerrain : StaticBody3D
{
    private int _floorCellsX = 48;
    private int _floorCellsZ = 48;
    private float _cellSize = 2f;
    private int _platformCount = 14;
    private int _minPlatformCells = 2;
    private int _maxPlatformCells = 5;
    private float _heightStep = 1.5f;
    private int _minHeightSteps = 3;
    private int _maxHeightSteps = 10;
    private float _rampRunPerHeight = 2f;
    private float _multiStageChance = 0.6f;
    private int _maxStages = 3;
    private int _landingCells = 1;
    private float _spawnClearRadius = 6f;
    private int _seed = 1337;
    private Color _floorColorA = new(0.22f, 0.22f, 0.24f);
    private Color _floorColorB = new(0.28f, 0.28f, 0.3f);
    private Color _structureColorLow = new(0.24f, 0.24f, 0.26f);
    private Color _structureColorHigh = new(0.3f, 0.3f, 0.33f);
    private bool _generateCollision = true;

    [ExportGroup("Floor")]
    [Export(PropertyHint.Range, "1,256,1")]
    public int FloorCellsX { get => _floorCellsX; set { _floorCellsX = Mathf.Max(1, value); RebuildIfReady(); } }

    [Export(PropertyHint.Range, "1,256,1")]
    public int FloorCellsZ { get => _floorCellsZ; set { _floorCellsZ = Mathf.Max(1, value); RebuildIfReady(); } }

    /// <summary>Size of one checker cell in meters.</summary>
    [Export(PropertyHint.Range, "0.1,20,0.1")]
    public float CellSize { get => _cellSize; set { _cellSize = Mathf.Max(0.01f, value); RebuildIfReady(); } }

    [ExportGroup("Structures")]
    [Export(PropertyHint.Range, "0,100,1")]
    public int PlatformCount { get => _platformCount; set { _platformCount = Mathf.Max(0, value); RebuildIfReady(); } }

    /// <summary>Smallest platform footprint, in floor cells per side.</summary>
    [Export(PropertyHint.Range, "1,32,1")]
    public int MinPlatformCells { get => _minPlatformCells; set { _minPlatformCells = Mathf.Max(1, value); RebuildIfReady(); } }

    [Export(PropertyHint.Range, "1,32,1")]
    public int MaxPlatformCells { get => _maxPlatformCells; set { _maxPlatformCells = Mathf.Max(1, value); RebuildIfReady(); } }

    /// <summary>Platform heights are whole multiples of this, in meters.</summary>
    [Export(PropertyHint.Range, "0.25,10,0.25")]
    public float HeightStep { get => _heightStep; set { _heightStep = Mathf.Max(0.01f, value); RebuildIfReady(); } }

    [Export(PropertyHint.Range, "1,20,1")]
    public int MinHeightSteps { get => _minHeightSteps; set { _minHeightSteps = Mathf.Max(1, value); RebuildIfReady(); } }

    [Export(PropertyHint.Range, "1,20,1")]
    public int MaxHeightSteps { get => _maxHeightSteps; set { _maxHeightSteps = Mathf.Max(1, value); RebuildIfReady(); } }

    /// <summary>Horizontal ramp length per meter of height (2 ≈ 26.6° slope).
    /// Runs are snapped up to whole cells, so actual slopes can be slightly gentler.</summary>
    [Export(PropertyHint.Range, "0.5,10,0.1")]
    public float RampRunPerHeight { get => _rampRunPerHeight; set { _rampRunPerHeight = Mathf.Max(0.1f, value); RebuildIfReady(); } }

    /// <summary>Chance that a structure is built as multiple terraced stages
    /// with roof-to-roof ramps instead of a single box.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")]
    public float MultiStageChance { get => _multiStageChance; set { _multiStageChance = Mathf.Clamp(value, 0f, 1f); RebuildIfReady(); } }

    [Export(PropertyHint.Range, "1,6,1")]
    public int MaxStages { get => _maxStages; set { _maxStages = Mathf.Max(1, value); RebuildIfReady(); } }

    /// <summary>Flat walkable cells left on each intermediate roof between the
    /// arriving ramp and the next one.</summary>
    [Export(PropertyHint.Range, "0,8,1")]
    public int LandingCells { get => _landingCells; set { _landingCells = Mathf.Max(0, value); RebuildIfReady(); } }

    /// <summary>Structures are kept at least this far (meters) from the local origin.</summary>
    [Export(PropertyHint.Range, "0,50,0.5")]
    public float SpawnClearRadius { get => _spawnClearRadius; set { _spawnClearRadius = Mathf.Max(0f, value); RebuildIfReady(); } }

    [Export]
    public int Seed { get => _seed; set { _seed = value; RebuildIfReady(); } }

    [ExportGroup("Appearance")]
    [Export]
    public Color FloorColorA { get => _floorColorA; set { _floorColorA = value; RebuildIfReady(); } }

    [Export]
    public Color FloorColorB { get => _floorColorB; set { _floorColorB = value; RebuildIfReady(); } }

    /// <summary>Ramp trim (sides/underside/back) color at the lowest platform
    /// height. Platform tops and sides use the floor checker colors.</summary>
    [Export]
    public Color StructureColorLow { get => _structureColorLow; set { _structureColorLow = value; RebuildIfReady(); } }

    /// <summary>Ramp trim color at the tallest platform height.</summary>
    [Export]
    public Color StructureColorHigh { get => _structureColorHigh; set { _structureColorHigh = value; RebuildIfReady(); } }

    [ExportGroup("Physics")]
    [Export]
    public bool GenerateCollision { get => _generateCollision; set { _generateCollision = value; RebuildIfReady(); } }

    private MeshInstance3D _meshInstance;
    private CollisionShape3D _collisionShape;

    private readonly List<Vector3> _vertices = new();
    private readonly List<Vector3> _normals = new();
    private readonly List<Color> _colors = new();
    private readonly List<int> _indices = new();

    public override void _Ready()
    {
        Rebuild();
    }

    private void RebuildIfReady()
    {
        if (IsNodeReady())
            Rebuild();
    }

    public void Rebuild()
    {
        EnsureChildren();

        _vertices.Clear();
        _normals.Clear();
        _colors.Clear();
        _indices.Clear();

        BuildFloor();
        BuildStructures();

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = _indices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 1f,
        });

        _meshInstance.Mesh = mesh;
        _collisionShape.Shape = _generateCollision ? mesh.CreateTrimeshShape() : null;
    }

    // ------------------------------------------------------------------ layout

    private void BuildFloor()
    {
        float originX = -_floorCellsX * _cellSize * 0.5f;
        float originZ = -_floorCellsZ * _cellSize * 0.5f;

        for (int z = 0; z < _floorCellsZ; z++)
        {
            for (int x = 0; x < _floorCellsX; x++)
            {
                float x0 = originX + x * _cellSize;
                float z0 = originZ + z * _cellSize;
                float x1 = x0 + _cellSize;
                float z1 = z0 + _cellSize;
                Color color = (x + z) % 2 == 0 ? _floorColorA : _floorColorB;

                AddQuad(
                    new Vector3(x0, 0, z0),
                    new Vector3(x1, 0, z0),
                    new Vector3(x1, 0, z1),
                    new Vector3(x0, 0, z1),
                    color,
                    Vector3.Up);
            }
        }
    }

    private void BuildStructures()
    {
        int minCells = Mathf.Min(_minPlatformCells, _maxPlatformCells);
        int maxCells = Mathf.Max(_minPlatformCells, _maxPlatformCells);
        int minSteps = Mathf.Min(_minHeightSteps, _maxHeightSteps);
        int maxSteps = Mathf.Max(_minHeightSteps, _maxHeightSteps);

        var rng = new RandomNumberGenerator { Seed = (ulong)(long)_seed };
        var occupied = new List<Rect2>
        {
            // Keep the spawn area (around the local origin) free of structures.
            new(-_spawnClearRadius, -_spawnClearRadius, _spawnClearRadius * 2f, _spawnClearRadius * 2f),
        };

        float maxHeight = maxSteps * _heightStep;

        for (int i = 0; i < _platformCount; i++)
        {
            for (int attempt = 0; attempt < 24; attempt++)
            {
                int dir = rng.RandiRange(0, 3); // ascent direction: 0=+X, 1=-X, 2=+Z, 3=-Z
                int width = rng.RandiRange(minCells, maxCells);
                int totalSteps = rng.RandiRange(minSteps, maxSteps);

                int stageCount = _maxStages > 1 && totalSteps > 1 && rng.Randf() < _multiStageChance
                    ? rng.RandiRange(2, _maxStages)
                    : 1;
                stageCount = Mathf.Min(stageCount, totalSteps);

                // Strictly ascending roof heights (in steps), last = totalSteps.
                var topSteps = new int[stageCount];
                for (int k = 0; k < stageCount; k++)
                    topSteps[k] = Mathf.RoundToInt(totalSteps * (k + 1) / (float)stageCount);
                for (int k = 1; k < stageCount; k++)
                    topSteps[k] = Mathf.Max(topSteps[k], topSteps[k - 1] + 1);
                topSteps[stageCount - 1] = totalSteps;
                for (int k = stageCount - 2; k >= 0; k--)
                    topSteps[k] = Mathf.Min(topSteps[k], topSteps[k + 1] - 1);

                // Ramp runs (cells, snapped up) and stage depths along the ascent.
                var rampCells = new int[stageCount];
                var depthCells = new int[stageCount];
                float previousHeight = 0f;
                for (int k = 0; k < stageCount; k++)
                {
                    float h = topSteps[k] * _heightStep;
                    rampCells[k] = Mathf.Max(1, Mathf.CeilToInt((h - previousHeight) * _rampRunPerHeight / _cellSize));
                    previousHeight = h;
                }

                for (int k = 0; k < stageCount - 1; k++)
                    depthCells[k] = rampCells[k + 1] + _landingCells;
                depthCells[stageCount - 1] = rng.RandiRange(minCells, maxCells);

                int totalU = rampCells[0];
                foreach (int depth in depthCells)
                    totalU += depth;

                int extentX = dir <= 1 ? totalU : width;
                int extentZ = dir <= 1 ? width : totalU;
                if (extentX > _floorCellsX || extentZ > _floorCellsZ)
                    continue;

                int originCellX = rng.RandiRange(0, _floorCellsX - extentX);
                int originCellZ = rng.RandiRange(0, _floorCellsZ - extentZ);

                Rect2 bounds = SegmentRect(originCellX, originCellZ, 0, totalU, width, totalU, dir);
                bool blocked = false;
                Rect2 padded = bounds.Grow(_cellSize * 0.5f);
                foreach (Rect2 other in occupied)
                {
                    if (padded.Intersects(other))
                    {
                        blocked = true;
                        break;
                    }
                }

                if (blocked)
                    continue;

                occupied.Add(bounds);

                int u = 0;
                previousHeight = 0f;
                for (int k = 0; k < stageCount; k++)
                {
                    float h = topSteps[k] * _heightStep;
                    Color color = _structureColorLow.Lerp(_structureColorHigh, maxHeight > 0f ? h / maxHeight : 0f);

                    if (k == 0)
                    {
                        // Floor ramp leading up to the first roof.
                        BuildRamp(SegmentRect(originCellX, originCellZ, 0, rampCells[0], width, totalU, dir), 0f, h, dir, color);
                        u += rampCells[0];
                    }
                    else
                    {
                        // Roof ramp: sits on the tail of the previous stage, flush
                        // against this one.
                        BuildRamp(SegmentRect(originCellX, originCellZ, u - rampCells[k], rampCells[k], width, totalU, dir), previousHeight, h, dir, color);
                    }

                    BuildBox(SegmentRect(originCellX, originCellZ, u, depthCells[k], width, totalU, dir), h);
                    u += depthCells[k];
                    previousHeight = h;
                }

                break;
            }
        }
    }

    /// <summary>
    /// World-space rect for a structure segment. Segments are laid out in
    /// "ascent" coordinates: u runs from the foot of the structure toward its
    /// top, uStartCells/uLenCells select the segment, widthCells spans the
    /// perpendicular axis. dir maps u onto +X/-X/+Z/-Z; (originCellX,
    /// originCellZ) is the min corner of the structure's bounding box in floor
    /// cells, totalUCells its full length along the ascent.
    /// </summary>
    private Rect2 SegmentRect(int originCellX, int originCellZ, int uStartCells, int uLenCells, int widthCells, int totalUCells, int dir)
    {
        float s = _cellSize;
        float minX = -_floorCellsX * s * 0.5f;
        float minZ = -_floorCellsZ * s * 0.5f;
        return dir switch
        {
            0 => new Rect2(minX + (originCellX + uStartCells) * s, minZ + originCellZ * s, uLenCells * s, widthCells * s),
            1 => new Rect2(minX + (originCellX + totalUCells - uStartCells - uLenCells) * s, minZ + originCellZ * s, uLenCells * s, widthCells * s),
            2 => new Rect2(minX + originCellX * s, minZ + (originCellZ + uStartCells) * s, widthCells * s, uLenCells * s),
            _ => new Rect2(minX + originCellX * s, minZ + (originCellZ + totalUCells - uStartCells - uLenCells) * s, widthCells * s, uLenCells * s),
        };
    }

    // ---------------------------------------------------------------- geometry

    /// <summary>
    /// Grid-aligned box from the floor to <paramref name="height"/>. The top
    /// continues the floor's checkerboard; the four side walls are checkered
    /// on the same grid, in rows of CellSize starting at y=0 (the top row may
    /// be shorter when the height is not a multiple of CellSize).
    /// </summary>
    private void BuildBox(Rect2 footprint, float height)
    {
        float x0 = footprint.Position.X, z0 = footprint.Position.Y;
        float x1 = footprint.End.X, z1 = footprint.End.Y;

        float floorMinX = -_floorCellsX * _cellSize * 0.5f;
        float floorMinZ = -_floorCellsZ * _cellSize * 0.5f;
        int gridX0 = Mathf.RoundToInt((x0 - floorMinX) / _cellSize);
        int gridZ0 = Mathf.RoundToInt((z0 - floorMinZ) / _cellSize);
        int cellsX = Mathf.Max(1, Mathf.RoundToInt(footprint.Size.X / _cellSize));
        int cellsZ = Mathf.Max(1, Mathf.RoundToInt(footprint.Size.Y / _cellSize));

        Color Checker(int a, int b) => (a + b) % 2 == 0 ? _floorColorA : _floorColorB;

        // Top face, cell by cell, continuing the floor pattern.
        for (int iz = 0; iz < cellsZ; iz++)
        {
            for (int ix = 0; ix < cellsX; ix++)
            {
                float qx0 = x0 + ix * _cellSize;
                float qz0 = z0 + iz * _cellSize;
                float qx1 = Mathf.Min(qx0 + _cellSize, x1);
                float qz1 = Mathf.Min(qz0 + _cellSize, z1);
                AddQuad(
                    new Vector3(qx0, height, qz0),
                    new Vector3(qx1, height, qz0),
                    new Vector3(qx1, height, qz1),
                    new Vector3(qx0, height, qz1),
                    Checker(gridX0 + ix, gridZ0 + iz),
                    Vector3.Up);
            }
        }

        // Bottom face (hidden against the floor) as a single quad.
        AddQuad(
            new Vector3(x0, 0, z0), new Vector3(x1, 0, z0),
            new Vector3(x1, 0, z1), new Vector3(x0, 0, z1),
            _floorColorA, Vector3.Down);

        // Side walls in vertical rows of CellSize.
        int rows = Mathf.Max(1, Mathf.CeilToInt(height / _cellSize));
        for (int row = 0; row < rows; row++)
        {
            float y0 = row * _cellSize;
            float y1 = Mathf.Min(y0 + _cellSize, height);

            for (int iz = 0; iz < cellsZ; iz++)
            {
                float qz0 = z0 + iz * _cellSize;
                float qz1 = Mathf.Min(qz0 + _cellSize, z1);
                Color color = Checker(gridZ0 + iz, row);

                AddQuad( // +X wall
                    new Vector3(x1, y0, qz0), new Vector3(x1, y1, qz0),
                    new Vector3(x1, y1, qz1), new Vector3(x1, y0, qz1),
                    color, Vector3.Right);
                AddQuad( // -X wall
                    new Vector3(x0, y0, qz0), new Vector3(x0, y1, qz0),
                    new Vector3(x0, y1, qz1), new Vector3(x0, y0, qz1),
                    color, Vector3.Left);
            }

            for (int ix = 0; ix < cellsX; ix++)
            {
                float qx0 = x0 + ix * _cellSize;
                float qx1 = Mathf.Min(qx0 + _cellSize, x1);
                Color color = Checker(gridX0 + ix, row);

                AddQuad( // +Z wall
                    new Vector3(qx0, y0, z1), new Vector3(qx0, y1, z1),
                    new Vector3(qx1, y1, z1), new Vector3(qx1, y0, z1),
                    color, Vector3.Back);
                AddQuad( // -Z wall
                    new Vector3(qx0, y0, z0), new Vector3(qx0, y1, z0),
                    new Vector3(qx1, y1, z0), new Vector3(qx1, y0, z0),
                    color, Vector3.Forward);
            }
        }
    }

    /// <summary>
    /// Full-width triangular prism rising from <paramref name="baseY"/> at its
    /// outer edge to <paramref name="topY"/> where it meets a box.
    /// <paramref name="ascendDir"/> (0=+X, 1=-X, 2=+Z, 3=-Z) is the direction
    /// the ramp climbs toward. When baseY &gt; 0 the prism sits flush on a
    /// roof. The sloped surface is checkered with the floor colors, aligned to
    /// the floor grid; the solid sides/underside use <paramref name="sideColor"/>.
    /// </summary>
    private void BuildRamp(Rect2 footprint, float baseY, float topY, int ascendDir, Color sideColor)
    {
        float x0 = footprint.Position.X, z0 = footprint.Position.Y;
        float x1 = footprint.End.X, z1 = footprint.End.Y;

        // Low edge (outer) and high edge (against the box).
        Vector3 low0, low1, high0, high1;
        switch (ascendDir)
        {
            case 0: // rises toward +X
                low0 = new Vector3(x0, baseY, z0); low1 = new Vector3(x0, baseY, z1);
                high0 = new Vector3(x1, topY, z0); high1 = new Vector3(x1, topY, z1);
                break;
            case 1: // rises toward -X
                low0 = new Vector3(x1, baseY, z0); low1 = new Vector3(x1, baseY, z1);
                high0 = new Vector3(x0, topY, z0); high1 = new Vector3(x0, topY, z1);
                break;
            case 2: // rises toward +Z
                low0 = new Vector3(x0, baseY, z0); low1 = new Vector3(x1, baseY, z0);
                high0 = new Vector3(x0, topY, z1); high1 = new Vector3(x1, topY, z1);
                break;
            default: // rises toward -Z
                low0 = new Vector3(x0, baseY, z1); low1 = new Vector3(x1, baseY, z1);
                high0 = new Vector3(x0, topY, z0); high1 = new Vector3(x1, topY, z0);
                break;
        }

        Vector3 base0 = new(high0.X, baseY, high0.Z);
        Vector3 base1 = new(high1.X, baseY, high1.Z);
        Vector3 toHigh = (base0 - low0).Normalized();
        Vector3 slopeNormal = (Vector3.Up * (base0 - low0).Length() - toHigh * (topY - baseY)).Normalized();
        Vector3 sideNormal = (low1 - low0).Normalized();

        AddQuad(low0, low1, base1, base0, sideColor, Vector3.Down); // underside
        AddQuad(base0, base1, high1, high0, sideColor, toHigh);     // back (against box)
        BuildRampSide(low0, base0, topY, -sideNormal);              // checkered side
        BuildRampSide(low1, base1, topY, sideNormal);               // checkered side

        // Sloped top, checkered per floor cell and continuing the floor's pattern.
        float floorMinX = -_floorCellsX * _cellSize * 0.5f;
        float floorMinZ = -_floorCellsZ * _cellSize * 0.5f;
        int cellsAlongX = Mathf.Max(1, Mathf.RoundToInt(footprint.Size.X / _cellSize));
        int cellsAlongZ = Mathf.Max(1, Mathf.RoundToInt(footprint.Size.Y / _cellSize));

        float HeightAt(float x, float z)
        {
            float t = ascendDir switch
            {
                0 => (x - x0) / (x1 - x0),
                1 => (x1 - x) / (x1 - x0),
                2 => (z - z0) / (z1 - z0),
                _ => (z1 - z) / (z1 - z0),
            };
            return Mathf.Lerp(baseY, topY, t);
        }

        for (int iz = 0; iz < cellsAlongZ; iz++)
        {
            for (int ix = 0; ix < cellsAlongX; ix++)
            {
                float qx0 = x0 + ix * _cellSize;
                float qz0 = z0 + iz * _cellSize;
                float qx1 = Mathf.Min(qx0 + _cellSize, x1);
                float qz1 = Mathf.Min(qz0 + _cellSize, z1);

                int gridX = Mathf.RoundToInt((qx0 - floorMinX) / _cellSize);
                int gridZ = Mathf.RoundToInt((qz0 - floorMinZ) / _cellSize);
                Color color = (gridX + gridZ) % 2 == 0 ? _floorColorA : _floorColorB;

                AddQuad(
                    new Vector3(qx0, HeightAt(qx0, qz0), qz0),
                    new Vector3(qx1, HeightAt(qx1, qz0), qz0),
                    new Vector3(qx1, HeightAt(qx1, qz1), qz1),
                    new Vector3(qx0, HeightAt(qx0, qz1), qz1),
                    color,
                    slopeNormal);
            }
        }
    }

    /// <summary>
    /// Checkered triangular side wall of a ramp: the region between the
    /// ramp's base height and its sloped top edge, cut into world-grid cells
    /// (columns along the run, rows of CellSize anchored at y=0 — matching
    /// the platform walls) with each cell clipped against the slope line.
    /// </summary>
    private void BuildRampSide(Vector3 lowCorner, Vector3 baseCorner, float topY, Vector3 outward)
    {
        float baseY = lowCorner.Y;
        Vector3 run = baseCorner - lowCorner;
        float length = run.Length();
        if (length < 0.001f || topY - baseY < 0.001f)
            return;

        Vector3 toHigh = run / length;
        float slope = (topY - baseY) / length;

        float floorMinX = -_floorCellsX * _cellSize * 0.5f;
        float floorMinZ = -_floorCellsZ * _cellSize * 0.5f;
        bool runsAlongX = Mathf.Abs(toHigh.X) > Mathf.Abs(toHigh.Z);

        int columns = Mathf.Max(1, Mathf.RoundToInt(length / _cellSize));
        int rowStart = Mathf.FloorToInt(baseY / _cellSize + 0.0001f);
        int rowEnd = Mathf.CeilToInt(topY / _cellSize - 0.0001f);

        Vector3 ToWorld(Vector2 p) => new(lowCorner.X + toHigh.X * p.X, p.Y, lowCorner.Z + toHigh.Z * p.X);

        var points = new List<Vector2>(5);
        for (int j = 0; j < columns; j++)
        {
            float u0 = j * _cellSize;
            float u1 = Mathf.Min(u0 + _cellSize, length);

            Vector3 mid = lowCorner + toHigh * ((u0 + u1) * 0.5f);
            float axisCoord = runsAlongX ? mid.X - floorMinX : mid.Z - floorMinZ;
            int columnIndex = Mathf.FloorToInt(axisCoord / _cellSize);

            for (int r = rowStart; r < rowEnd; r++)
            {
                float y0 = Mathf.Max(baseY, r * _cellSize);
                float y1 = Mathf.Min(topY, (r + 1) * _cellSize);
                if (y1 - y0 < 0.0001f)
                    continue;

                points.Clear();
                points.Add(new Vector2(u0, y0));
                points.Add(new Vector2(u1, y0));
                points.Add(new Vector2(u1, y1));
                points.Add(new Vector2(u0, y1));
                ClipBelowSlope(points, baseY, slope);
                if (points.Count < 3)
                    continue;

                Color color = (columnIndex + r) % 2 == 0 ? _floorColorA : _floorColorB;
                for (int i = 1; i < points.Count - 1; i++)
                    AddTriangle(ToWorld(points[0]), ToWorld(points[i]), ToWorld(points[i + 1]), color, outward);
            }
        }
    }

    /// <summary>Clips a convex polygon in (run, height) coordinates to the
    /// region below the slope line y = baseY + slope * u, in place.</summary>
    private static void ClipBelowSlope(List<Vector2> points, float baseY, float slope)
    {
        var input = new List<Vector2>(points);
        points.Clear();
        for (int i = 0; i < input.Count; i++)
        {
            Vector2 a = input[i];
            Vector2 b = input[(i + 1) % input.Count];
            float fa = baseY + slope * a.X - a.Y;
            float fb = baseY + slope * b.X - b.Y;
            if (fa >= 0f)
                points.Add(a);
            if (fa >= 0f != fb >= 0f)
                points.Add(a + (b - a) * (fa / (fa - fb)));
        }
    }

    /// <summary>
    /// Adds a flat-shaded quad. Winding is corrected automatically so the face
    /// normal points to the same side as <paramref name="outwardHint"/>.
    /// </summary>
    private void AddQuad(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Color color, Vector3 outwardHint)
    {
        Vector3 normal = (v2 - v0).Cross(v1 - v0).Normalized();
        if (normal.Dot(outwardHint) < 0f)
        {
            (v1, v3) = (v3, v1);
            normal = -normal;
        }

        int start = _vertices.Count;
        _vertices.Add(v0); _vertices.Add(v1); _vertices.Add(v2); _vertices.Add(v3);
        for (int i = 0; i < 4; i++)
        {
            _normals.Add(normal);
            _colors.Add(color);
        }

        _indices.Add(start); _indices.Add(start + 1); _indices.Add(start + 2);
        _indices.Add(start); _indices.Add(start + 2); _indices.Add(start + 3);
    }

    private void AddTriangle(Vector3 v0, Vector3 v1, Vector3 v2, Color color, Vector3 outwardHint)
    {
        Vector3 normal = (v2 - v0).Cross(v1 - v0);
        if (normal.LengthSquared() < 1e-10f)
            return; // degenerate sliver from clipping
        normal = normal.Normalized();
        if (normal.Dot(outwardHint) < 0f)
        {
            (v1, v2) = (v2, v1);
            normal = -normal;
        }

        int start = _vertices.Count;
        _vertices.Add(v0); _vertices.Add(v1); _vertices.Add(v2);
        for (int i = 0; i < 3; i++)
        {
            _normals.Add(normal);
            _colors.Add(color);
        }

        _indices.Add(start); _indices.Add(start + 1); _indices.Add(start + 2);
    }

    private void EnsureChildren()
    {
        if (_meshInstance == null || !IsInstanceValid(_meshInstance))
        {
            _meshInstance = new MeshInstance3D { Name = "TerrainMesh" };
            AddChild(_meshInstance);
        }

        if (_collisionShape == null || !IsInstanceValid(_collisionShape))
        {
            _collisionShape = new CollisionShape3D { Name = "TerrainCollision" };
            AddChild(_collisionShape);
        }
    }
}
