using Godot;
using System.Collections.Generic;

namespace GameBase.Terrain;

/// <summary>
/// A small bismuth "hopper crystal" formation: stacked squarish terraces that
/// step inward as they rise, each tier twisted and drifted from the one below,
/// with a recessed stepped cavity at the top (rims grow faster than faces —
/// the skeletal-crystal signature). Gray, flat-shaded, hard edges, no texture.
///
/// Prototype of the project-infinite-world terrain art direction (WP05):
/// the tier solve maps to ITerraceQuantizer (TerraceLevel = tier index,
/// QuantizeAltitude = tier * step, LateralInset = per-tier footprint inset),
/// and the ground tessellation follows the determinism doctrine — a jittered
/// quad grid (never free-floating nodes) with vertices snapped to quarter-node
/// increments, cells formed from the dual of the jittered nodes.
///
/// Attach to a StaticBody3D (or instance BismuthBlob.tscn); it builds its own
/// mesh/collision children and regenerates live in the editor.
/// </summary>
[Tool]
public partial class BismuthBlob : StaticBody3D
{
    /// <summary>How tier footprints shrink as they rise, which sets the
    /// silhouette: a straight taper (mound) or a circular profile (dome).</summary>
    public enum BlobShape
    {
        /// <summary>Linear inset per tier — a stepped cone/mound.</summary>
        Mound,
        /// <summary>Circular profile — a terraced dome, the top half of a
        /// sphere cut into steps.</summary>
        Sphere,
    }

    private BlobShape _shape = BlobShape.Mound;

    private int _tierCount = 5;
    private float _stepHeight = 0.7f;
    private float _insetPerStep = 0.55f;
    private float _stepJitter = 0.2f;
    private float _tierTwistDegrees = 9f;
    private float _tierDrift = 0.18f;
    private int _hopperTiers = 2;
    private float _squareness = 0.85f;
    private float _nodeSpacing = 0.35f;
    private float _nodeJitter = 0.35f;
    private float _edgePatchSize = 2.2f;
    private int _seed = 421;
    private Color _colorA = new(0.10f, 0.10f, 0.12f);
    private Color _colorB = new(0.72f, 0.72f, 0.76f);
    private bool _generateCollision = true;

    [ExportGroup("Terraces")]
    /// <summary>Mound = linear taper; Sphere = terraced dome.</summary>
    [Export]
    public BlobShape Shape { get => _shape; set { _shape = value; RebuildIfReady(); } }

    /// <summary>Number of terraces. Blob size follows: footprint half-size is
    /// TierCount * InsetPerStep plus a small base rim.</summary>
    [Export(PropertyHint.Range, "2,16,1")]
    public int TierCount { get => _tierCount; set { _tierCount = Mathf.Max(2, value); RebuildIfReady(); } }

    /// <summary>Vertical rise per terrace (StepHeightM).</summary>
    [Export(PropertyHint.Range, "0.1,2,0.05")]
    public float StepHeight { get => _stepHeight; set { _stepHeight = Mathf.Max(0.05f, value); RebuildIfReady(); } }

    /// <summary>Lateral step-in per terrace — the hopper dial (InsetPerStepM).</summary>
    [Export(PropertyHint.Range, "0.1,2,0.02")]
    public float InsetPerStep { get => _insetPerStep; set { _insetPerStep = Mathf.Max(0.05f, value); RebuildIfReady(); } }

    /// <summary>Breaks perfect tier regularity, 0..1 (StepJitter).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")]
    public float StepJitter { get => _stepJitter; set { _stepJitter = Mathf.Clamp(value, 0f, 1f); RebuildIfReady(); } }

    /// <summary>Rotation of each tier relative to the one below — square
    /// spirals instead of concentric rings.</summary>
    [Export(PropertyHint.Range, "0,30,0.5")]
    public float TierTwistDegrees { get => _tierTwistDegrees; set { _tierTwistDegrees = value; RebuildIfReady(); } }

    /// <summary>Lateral drift of each tier from the one below, in meters.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")]
    public float TierDrift { get => _tierDrift; set { _tierDrift = Mathf.Max(0f, value); RebuildIfReady(); } }

    /// <summary>Recessed tiers at the top — 0 gives a solid stepped pyramid,
    /// more gives the hollow hopper pit with a raised rim.</summary>
    [Export(PropertyHint.Range, "0,6,1")]
    public int HopperTiers { get => _hopperTiers; set { _hopperTiers = Mathf.Max(0, value); RebuildIfReady(); } }

    /// <summary>0 = round terraces (rice paddies), 1 = square (crystal).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")]
    public float Squareness { get => _squareness; set { _squareness = Mathf.Clamp(value, 0f, 1f); RebuildIfReady(); } }

    [ExportGroup("Tessellation")]
    /// <summary>Node spacing of the jittered quad grid, in meters.</summary>
    [Export(PropertyHint.Range, "0.15,1.5,0.05")]
    public float NodeSpacing { get => _nodeSpacing; set { _nodeSpacing = Mathf.Max(0.1f, value); RebuildIfReady(); } }

    /// <summary>Node jitter as a fraction of spacing (kept below 0.5 so dual
    /// cells stay convex; positions snap to quarter-spacing increments).</summary>
    [Export(PropertyHint.Range, "0,0.45,0.01")]
    public float NodeJitter { get => _nodeJitter; set { _nodeJitter = Mathf.Clamp(value, 0f, 0.45f); RebuildIfReady(); } }

    /// <summary>Wavelength of the terrace-edge wobble, in meters. Must be
    /// several times NodeSpacing: at patch sizes near a cell the boundary
    /// combs into per-cell sawtooth instead of stepping in clean ledges.</summary>
    [Export(PropertyHint.Range, "0.5,8,0.1")]
    public float EdgePatchSize { get => _edgePatchSize; set { _edgePatchSize = Mathf.Max(0.2f, value); RebuildIfReady(); } }

    [Export]
    public int Seed { get => _seed; set { _seed = value; RebuildIfReady(); } }

    [ExportGroup("Appearance")]
    [Export] public Color ColorA { get => _colorA; set { _colorA = value; RebuildIfReady(); } }
    [Export] public Color ColorB { get => _colorB; set { _colorB = value; RebuildIfReady(); } }

    [ExportGroup("Physics")]
    [Export]
    public bool GenerateCollision { get => _generateCollision; set { _generateCollision = value; RebuildIfReady(); } }

    private MeshInstance3D _meshInstance;
    private CollisionShape3D _collisionShape;

    /// <summary>Sparse per-BLOCK edits: key is (cell x, cell z, tier), value
    /// is true for added, false for removed. Keyed per block rather than per
    /// column so a single cube can be carved out of the middle of a stack or
    /// stuck onto one face — a per-column "top tier" cannot express either.
    /// Held apart from the generated field so regenerating from the seed never
    /// loses player changes: the diff-against-seed model the terrain design
    /// calls for.</summary>
    private readonly Dictionary<Vector3I, bool> _edits = new();

    // Grid metrics from the last build, so edits can map world space to cells.
    private int _gridSize;
    private int _gridHalf;
    private float _gridStep;
    private float _gridBase;
    private HashSet<Vector3I> _solid = new();

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

    public void Rebuild()
    {
        EnsureChildren();
        _vertices.Clear();
        _normals.Clear();
        _colors.Clear();
        _indices.Clear();

        // --- Tier frames: half-size, accumulated twist and drift per tier ---
        int tierCount = _tierCount;

        // A sphere's width is set by its own height, not by InsetPerStep:
        // the stack is tierCount * StepHeight tall, so the radius that makes
        // it read round is half of that (scaled for the profile's clipped
        // span). Deriving it keeps the ball from coming out as a squat barrel
        // no matter how the terrace dials are set.
        float radius = _shape == BlobShape.Sphere
            ? tierCount * _insetPerStep * 0.9f
            : tierCount * _insetPerStep + 0.5f;

        // A sphere's terraces must be SHORT relative to how far each steps in,
        // or the rings read as a stack of tall discs. Deriving the rise from
        // the radius keeps height and width in proportion: the full stack is
        // roughly as tall as the ball is wide.
        float stepHeight = _shape == BlobShape.Sphere
            ? radius * 2f / tierCount
            : _stepHeight;
        var half = new float[tierCount];
        var offset = new Vector2[tierCount];
        var rotation = new float[tierCount];
        // Tiers above the hopper rim form the recessed cavity. They must stay
        // concentric with the rim: letting them keep twisting and drifting
        // makes each inner square land somewhere different, and the boundary
        // fragments into disconnected patches instead of a clean nested pit.
        int rimTier = _hopperTiers > 0 ? Mathf.Max(tierCount - 1 - _hopperTiers, 0) : tierCount - 1;

        Vector2 drift = Vector2.Zero;
        for (int t = 0; t < tierCount; t++)
        {
            if (_shape == BlobShape.Sphere)
            {
                // Circular profile: half-width follows sqrt(R^2 - y^2) at this
                // tier's height, so the stepped silhouette bulges and then
                // closes like a dome instead of tapering straight to a point.
                // Full sphere sampled at uniform ANGLE, not uniform height.
                // The equator sits mid-stack so tiers bulge out and taper back
                // in — a ball, not a dome. Sampling by height instead would
                // leave the middle tiers at nearly identical width (sqrt is
                // flat near the equator) and the result reads as a barrel.
                float angle = Mathf.Pi * (t + 0.5f) / tierCount;
                float y = -Mathf.Cos(angle) * 0.9f;
                half[t] = radius * Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            }
            else
            {
                half[t] = radius - t * _insetPerStep;
            }

            if (t <= rimTier)
            {
                rotation[t] = Mathf.DegToRad(_tierTwistDegrees) * t;
                if (t > 0)
                {
                    float angle = Hash01((uint)t, 11u) * Mathf.Tau;
                    drift += new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * _tierDrift;
                }
            }
            else
            {
                // Cavity tiers: lock to the rim's frame.
                rotation[t] = rotation[rimTier];
            }

            offset[t] = drift;
        }

        // --- Jittered quad-grid nodes, then terrace level per cell ---
        int n = Mathf.CeilToInt((radius + _nodeSpacing) / _nodeSpacing) + 1;
        int size = 2 * n + 1;
        var nodes = new Vector2[size, size];
        var levels = new int[size, size];
        float snap = _nodeSpacing * 0.25f;
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                float jx = (Hash01((uint)(i * 73856093 ^ j * 19349663), 3u) - 0.5f) * 2f * _nodeJitter * _nodeSpacing;
                float jz = (Hash01((uint)(i * 83492791 ^ j * 29765723), 7u) - 0.5f) * 2f * _nodeJitter * _nodeSpacing;
                var p = new Vector2((i - n) * _nodeSpacing + jx, (j - n) * _nodeSpacing + jz);
                nodes[i, j] = new Vector2(Mathf.Round(p.X / snap) * snap, Mathf.Round(p.Y / snap) * snap);

                // Classify at the UNJITTERED lattice position. Jitter shapes
                // the cell outline, but if it also moves the sample point the
                // tier boundary fragments: cells straddling a flat edge fall
                // in or out at random and the silhouette combs into gaps.
                var lattice = new Vector2((i - n) * _nodeSpacing, (j - n) * _nodeSpacing);
                levels[i, j] = TierAt(lattice, tierCount, half, offset, rotation);
            }
        }

        // Despeckle: a cell whose level matches none of its four neighbours is
        // a lone pillar or pinhole — the artifact of a boundary passing through
        // a single cell. Snap it to the surrounding level so terraces read as
        // solid plates. Bismuth steps are clean ledges, not rubble.
        var cleaned = (int[,])levels.Clone();
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                int level = levels[i, j];
                int matching = 0;
                int highestNeighbor = int.MinValue;
                int lowestNeighbor = int.MaxValue;
                for (int k = 0; k < 4; k++)
                {
                    int ni = i + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int nj = j + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    int neighbor = ni >= 0 && ni < size && nj >= 0 && nj < size ? levels[ni, nj] : -1;
                    if (neighbor == level)
                        matching++;
                    highestNeighbor = Mathf.Max(highestNeighbor, neighbor);
                    lowestNeighbor = Mathf.Min(lowestNeighbor, neighbor);
                }

                if (matching == 0)
                    cleaned[i, j] = level > highestNeighbor ? highestNeighbor : lowestNeighbor;
            }
        }

        levels = cleaned;

        // Dual corners: average of the four surrounding nodes, quarter-snapped.
        var corners = new Vector2[size + 1, size + 1];
        for (int j = 0; j <= size; j++)
        {
            for (int i = 0; i <= size; i++)
            {
                Vector2 sum = Vector2.Zero;
                int count = 0;
                for (int dj = -1; dj <= 0; dj++)
                {
                    for (int di = -1; di <= 0; di++)
                    {
                        int ii = i + di, jj = j + dj;
                        if (ii >= 0 && ii < size && jj >= 0 && jj < size)
                        {
                            sum += nodes[ii, jj];
                            count++;
                        }
                    }
                }

                Vector2 c = sum / count;
                corners[i, j] = new Vector2(Mathf.Round(c.X / snap) * snap, Mathf.Round(c.Y / snap) * snap);
            }
        }

        // The sphere sits ON the ground rather than sunk into it: the whole
        // ball is visible and inspectable from every side. (Burying it to the
        // equator hides half the geometry and reads as a mound.) Mounds keep
        // their base at y=0.
        float groundOffset = 0f;

        // --- Per-cell column extent ---
        // A mound is solid from the ground up, so its base is y=0 and its top
        // comes from the terrace level. A sphere curves back in below its
        // equator, so each column is a slab spanning [min,max] tiers — both
        // ends from the same span query.
        var baseY = new float[size, size];
        if (_shape == BlobShape.Sphere)
        {
            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    var lattice = new Vector2((i - n) * _nodeSpacing, (j - n) * _nodeSpacing);
                    if (TierSpanAt(lattice, tierCount, half, offset, rotation, out int lo, out int hi))
                    {
                        levels[i, j] = hi;
                        baseY[i, j] = lo * stepHeight;
                    }
                    else
                    {
                        levels[i, j] = -1;
                    }
                }
            }
        }

        _gridSize = size;
        _gridHalf = n;
        _gridStep = _nodeSpacing;
        _gridBase = stepHeight;

        // --- Occupancy: the generated field, then the player's per-block edits ---
        // Meshing from an explicit block set (rather than per-column spans) is
        // what lets a cube be carved from the middle of a stack or stuck onto
        // a single face; a column can only grow or vanish from its top.
        var solid = new HashSet<Vector3I>();
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                int top = levels[i, j];
                if (top < 0)
                    continue;
                int bottom = Mathf.RoundToInt(baseY[i, j] / Mathf.Max(stepHeight, 0.001f));
                for (int t = bottom; t <= top; t++)
                    solid.Add(new Vector3I(i, j, t));
            }
        }

        foreach (var edit in _edits)
        {
            if (edit.Value)
                solid.Add(edit.Key);
            else
                solid.Remove(edit.Key);
        }

        _solid = solid;

        // --- Emit each block's exposed faces ---
        foreach (Vector3I block in solid)
        {
            int i = block.X, j = block.Y, t = block.Z;
            if (i < 0 || i >= size || j < 0 || j >= size)
                continue;

            float bottom = t * stepHeight - groundOffset;
            float top = (t + 1) * stepHeight - groundOffset;

            // One flat colour per block, alternating on all three axes so no
            // two touching blocks ever share a shade.
            Color color = (i + j + t) % 2 == 0 ? _colorA : _colorB;

            Vector2 c00 = corners[i, j], c10 = corners[i + 1, j];
            Vector2 c11 = corners[i + 1, j + 1], c01 = corners[i, j + 1];

            if (!solid.Contains(new Vector3I(i, j, t + 1)))
                AddQuad(V3(c00, top), V3(c10, top), V3(c11, top), V3(c01, top), color, Vector3.Up);

            if (!solid.Contains(new Vector3I(i, j, t - 1)) && bottom > 0.0001f)
                AddQuad(V3(c00, bottom), V3(c10, bottom), V3(c11, bottom), V3(c01, bottom), color, Vector3.Down);

            EmitFace(solid, i, j, t, 1, 0, c10, c11, bottom, top, color);
            EmitFace(solid, i, j, t, -1, 0, c00, c01, bottom, top, color);
            EmitFace(solid, i, j, t, 0, 1, c01, c11, bottom, top, color);
            EmitFace(solid, i, j, t, 0, -1, c00, c10, bottom, top, color);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = _indices.ToArray();

        var mesh = new ArrayMesh();
        if (_vertices.Count > 0)
        {
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(0, new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 1f,
            });
        }

        _meshInstance.Mesh = mesh;
        _collisionShape.Shape = _generateCollision && _vertices.Count > 0 ? mesh.CreateTrimeshShape() : null;
    }

    /// <summary>
    /// Terrace level at a point: the highest tier whose (twisted, drifted,
    /// jitter-edged, squarish) footprint contains it, then remapped so the
    /// innermost HopperTiers descend again — the recessed hopper cavity.
    /// Maps to ITerraceQuantizer.TerraceLevel.
    /// </summary>
    private int TierAt(Vector2 p, int tierCount,
        float[] half, Vector2[] offset, float[] rotation)
    {
        int level = -1;
        for (int t = 0; t < tierCount; t++)
        {
            Vector2 q = p - offset[t];
            float cos = Mathf.Cos(-rotation[t]);
            float sin = Mathf.Sin(-rotation[t]);
            q = new Vector2(q.X * cos - q.Y * sin, q.X * sin + q.Y * cos);
            float round = q.Length();
            float square = Mathf.Max(Mathf.Abs(q.X), Mathf.Abs(q.Y));
            float squareness = _shape == BlobShape.Sphere ? _squareness * 0.35f : _squareness;
            float dist = Mathf.Lerp(round, square, squareness);

            // Jitter must be coherent along the boundary: hashing a coarse
            // quantisation of the position wobbles the tier edge in patches,
            // where hashing per cell speckles lone holes and pillars (rubble).
            // Bilinear-smoothed patch noise: a hard floor() here quantises the
            // wobble into blocky bites, while smoothing it lets the ledge
            // meander as one continuous line.
            float edge = t == 0
                ? 0f
                : (PatchNoise(q / _edgePatchSize, (uint)t) - 0.5f) * 2f * _stepJitter * _insetPerStep;
            if (dist <= half[t] + edge)
            {
                level = t;
            }
            else if (_shape == BlobShape.Mound)
            {
                break; // mound tiers nest: outside this one means outside all above
            }
        }

        if (level < 0 || _hopperTiers <= 0)
            return level;

        // Hopper remap: rim tier stays highest, inner tiers step back down.
        int rim = tierCount - 1 - _hopperTiers;
        if (rim >= 0 && level > rim)
            // Floor the cavity one step above the base: bottoming out at the
            // outer level makes the pit vanish into the surrounding plate.
            return Mathf.Max(2 * rim - level, 1);
        return level;
    }

    /// <summary>
    /// The contiguous span of tiers whose footprints cover this point. A
    /// vertical slab through a sphere enters and leaves the surface exactly
    /// once, so the covered tiers form one run: the column spans
    /// [min, max]. Top and base MUST come from this same query — deriving
    /// them separately lets them disagree and tears the mesh open.
    /// </summary>
    private bool TierSpanAt(Vector2 p, int tierCount, float[] half,
        Vector2[] offset, float[] rotation, out int minTier, out int maxTier)
    {
        minTier = int.MaxValue;
        maxTier = int.MinValue;
        for (int t = 0; t < tierCount; t++)
        {
            Vector2 q = p - offset[t];
            float cos = Mathf.Cos(-rotation[t]);
            float sin = Mathf.Sin(-rotation[t]);
            q = new Vector2(q.X * cos - q.Y * sin, q.X * sin + q.Y * cos);
            float round = q.Length();
            float square = Mathf.Max(Mathf.Abs(q.X), Mathf.Abs(q.Y));
            float squareness = _shape == BlobShape.Sphere ? _squareness * 0.35f : _squareness;
            float dist = Mathf.Lerp(round, square, squareness);

            float edge = t == 0
                ? 0f
                : (PatchNoise(q / _edgePatchSize, (uint)t) - 0.5f) * 2f * _stepJitter * _insetPerStep;

            if (dist <= half[t] + edge)
            {
                minTier = Mathf.Min(minTier, t);
                maxTier = Mathf.Max(maxTier, t);
            }
        }

        return maxTier >= minTier;
    }

    /// <summary>
    /// Side wall between this column and one neighbour. Each column spans
    /// [baseY, h]; the exposed wall is whatever part of that span the
    /// neighbour does not also cover. A sphere needs BOTH gaps closed — the
    /// part above the neighbour's top and the part below its base — or the
    /// underside curves away from a wall that was never emitted and the ball
    /// renders as a torn shell.
    /// </summary>
    /// <summary>Side face of one block, drawn only where the neighbouring
    /// block is absent.</summary>
    private void EmitFace(HashSet<Vector3I> solid, int i, int j, int t, int di, int dj,
        Vector2 a, Vector2 b, float bottom, float top, Color color)
    {
        if (solid.Contains(new Vector3I(i + di, j + dj, t)))
            return;
        AddQuad(V3(a, bottom), V3(a, top), V3(b, top), V3(b, bottom), color, new Vector3(di, 0, dj));
    }

    private static Vector3 V3(Vector2 xz, float y) => new(xz.X, y, xz.Y);

    /// <summary>Deterministic 0..1 hash — the fixed-jitter discipline: a pure
    /// function of indices and seed, identical on every rebuild.</summary>
    /// <summary>Smooth 0..1 value noise on a coarse lattice — used to wobble
    /// terrace boundaries coherently rather than per cell.</summary>
    private float PatchNoise(Vector2 p, uint salt)
    {
        int x0 = Mathf.FloorToInt(p.X), y0 = Mathf.FloorToInt(p.Y);
        float fx = p.X - x0, fy = p.Y - y0;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);

        float c00 = Hash01((uint)(x0 * 73856093 ^ y0 * 19349663), salt);
        float c10 = Hash01((uint)((x0 + 1) * 73856093 ^ y0 * 19349663), salt);
        float c01 = Hash01((uint)(x0 * 73856093 ^ (y0 + 1) * 19349663), salt);
        float c11 = Hash01((uint)((x0 + 1) * 73856093 ^ (y0 + 1) * 19349663), salt);
        return Mathf.Lerp(Mathf.Lerp(c00, c10, fx), Mathf.Lerp(c01, c11, fx), fy);
    }

    private float Hash01(uint value, uint salt)
    {
        uint x = value ^ (uint)_seed * 2654435761u ^ salt * 40503u;
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return (x & 0xFFFFFF) / 16777216f;
    }

    private void AddQuad(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Color color, Vector3 outwardHint)
    {
        Vector3 normal = (v2 - v0).Cross(v1 - v0);
        if (normal.LengthSquared() < 1e-10f)
            return;
        normal = normal.Normalized();
        if (normal.Dot(outwardHint) < 0f)
        {
            (v1, v3) = (v3, v1);
            normal = -normal;
        }

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

    /// <summary>Cell containing a world-space point, or false if off-grid.</summary>
    public bool TryGetCell(Vector3 worldPoint, out Vector2I cell)
    {
        Vector3 local = ToLocal(worldPoint);
        int i = Mathf.RoundToInt(local.X / _gridStep) + _gridHalf;
        int j = Mathf.RoundToInt(local.Z / _gridStep) + _gridHalf;
        cell = new Vector2I(i, j);
        return _gridSize > 0 && i >= 0 && i < _gridSize && j >= 0 && j < _gridSize;
    }

    /// <summary>
    /// Walks a ray forward in small steps and returns the first cell that
    /// actually holds a block, plus the last empty cell before it.
    ///
    /// Picking from the raycast's contact point alone is unreliable: the hit
    /// lands exactly on a face, which is a cell boundary, so rounding can pick
    /// either neighbour — often an empty one, which is why some clicks did
    /// nothing. Marching resolves faces, edges and corners consistently.
    /// </summary>
    private bool MarchToBlock(Vector3 worldFrom, Vector3 worldDir, float maxDistance,
        out Vector3I solidBlock, out Vector3I emptyBlock)
    {
        solidBlock = default;
        emptyBlock = default;

        float step = Mathf.Max(_gridStep, 0.01f) * 0.2f;
        var previous = new Vector3I(int.MinValue, int.MinValue, int.MinValue);
        bool havePrevious = false;

        for (float travelled = 0f; travelled <= maxDistance; travelled += step)
        {
            Vector3 world = worldFrom + worldDir * travelled;
            if (!TryGetCell(world, out Vector2I cell))
                continue;

            Vector3 local = ToLocal(world);
            int tier = Mathf.FloorToInt(local.Y / Mathf.Max(_gridBase, 0.001f));
            var here = new Vector3I(cell.X, cell.Y, tier);

            if (_solid.Contains(here))
            {
                solidBlock = here;
                // The last empty step is the face the ray came in through, so
                // a placed block lands against that face rather than on top of
                // the stack.
                emptyBlock = havePrevious ? previous : here;
                return true;
            }

            previous = here;
            havePrevious = true;
        }

        return false;
    }

    /// <summary>Whether a block exists at this cell and tier.</summary>
    private bool IsSolid(Vector2I cell, int tier)
    {
        return _solid.Contains(new Vector3I(cell.X, cell.Y, tier));
    }

    /// <summary>Removes just the single block the ray first meets.</summary>
    public bool RemoveAlongRay(Vector3 worldFrom, Vector3 worldDir, float maxDistance)
    {
        if (!MarchToBlock(worldFrom, worldDir, maxDistance, out Vector3I block, out _))
            return false;
        _edits[block] = false;
        Rebuild();
        return true;
    }

    /// <summary>Adds one block against the face of whatever the ray meets.</summary>
    public bool AddAlongRay(Vector3 worldFrom, Vector3 worldDir, float maxDistance)
    {
        if (!MarchToBlock(worldFrom, worldDir, maxDistance, out Vector3I block, out Vector3I empty))
            return false;
        if (empty == block)
            return false;
        _edits[empty] = true;
        Rebuild();
        return true;
    }

    /// <summary>Discards all player edits and returns to the generated shape.</summary>
    public void ClearEdits()
    {
        _edits.Clear();
        Rebuild();
    }

    private void EnsureChildren()
    {
        if (_meshInstance == null || !IsInstanceValid(_meshInstance))
        {
            _meshInstance = new MeshInstance3D { Name = "BlobMesh" };
            AddChild(_meshInstance);
        }

        if (_collisionShape == null || !IsInstanceValid(_collisionShape))
        {
            _collisionShape = new CollisionShape3D { Name = "BlobCollision" };
            AddChild(_collisionShape);
        }
    }
}
