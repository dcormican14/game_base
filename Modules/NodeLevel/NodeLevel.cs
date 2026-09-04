using Godot;
using System.Collections.Generic;
using GameBase.Nodes;

namespace GameBase.Levels;

/// <summary>
/// Builds a whole level out of nodes: a rounded pillar of rock falling away
/// into the void, its top cut flat, with primitive solids scattered across the
/// surface and a few floating above it.
///
/// Everything goes into a single <see cref="NodeWorld"/>, so the level is
/// editable node-by-node like anything the player builds. Shapes are
/// rasterised into the grid rather than instanced as meshes — a pyramid is a
/// stack of shrinking squares, a sphere a distance test — so the bismuth
/// shaping treats them as ordinary rock.
///
/// Attach to a NodeWorld (or instance NodeLevel.tscn); it is a [Tool] script,
/// so changing any export regenerates the level live in the editor.
/// </summary>
[Tool]
public partial class NodeLevel : Node
{
    private int _seed = 20240;
    private int _pillarRadius = 22;
    private int _pillarDepth = 40;
    private float _pillarTaper = 0.55f;
    private float _rimNoise = 0.16f;
    private int _propCount = 26;
    private float _floatingFraction = 0.28f;
    private bool _autoBuild = true;

    [ExportGroup("Pillar")]
    /// <summary>Radius of the flat top surface, in nodes.</summary>
    [Export(PropertyHint.Range, "4,64,1")]
    public int PillarRadius { get => _pillarRadius; set { _pillarRadius = Mathf.Max(3, value); RebuildIfReady(); } }

    /// <summary>How far the pillar extends below the surface, in nodes.</summary>
    [Export(PropertyHint.Range, "4,128,1")]
    public int PillarDepth { get => _pillarDepth; set { _pillarDepth = Mathf.Max(1, value); RebuildIfReady(); } }

    /// <summary>How much the pillar narrows toward its base. 0 is a straight
    /// cylinder; 1 tapers almost to a point.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")]
    public float PillarTaper { get => _pillarTaper; set { _pillarTaper = Mathf.Clamp(value, 0f, 1f); RebuildIfReady(); } }

    /// <summary>Irregularity of the pillar's silhouette, as a fraction of its
    /// radius. 0 gives a perfectly round column.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")]
    public float RimNoise { get => _rimNoise; set { _rimNoise = Mathf.Clamp(value, 0f, 0.5f); RebuildIfReady(); } }

    [ExportGroup("Props")]
    /// <summary>How many solids to scatter on and above the surface.</summary>
    [Export(PropertyHint.Range, "0,80,1")]
    public int PropCount { get => _propCount; set { _propCount = Mathf.Max(0, value); RebuildIfReady(); } }

    /// <summary>Fraction of props left floating in the air rather than resting
    /// on the surface.</summary>
    [Export(PropertyHint.Range, "0,1,0.02")]
    public float FloatingFraction { get => _floatingFraction; set { _floatingFraction = Mathf.Clamp(value, 0f, 1f); RebuildIfReady(); } }

    [ExportGroup("General")]
    [Export]
    public int Seed { get => _seed; set { _seed = value; RebuildIfReady(); } }

    /// <summary>Rebuild whenever an export changes. Turn off to edit the level
    /// by hand without it being regenerated underneath you.</summary>
    [Export]
    public bool AutoBuild { get => _autoBuild; set { _autoBuild = value; RebuildIfReady(); } }

    /// <summary>The world to fill. Defaults to the parent when left empty.</summary>
    [Export] public NodePath NodeWorldPath { get; set; } = "";

    private NodeWorld _world;
    private ulong _rng;

    /// <summary>Mesh the generated level a few chunks per frame rather than
    /// all at once, so a loading screen can show progress instead of the game
    /// freezing. Off regenerates synchronously, which is what the editor
    /// wants when a dial changes.</summary>
    [Export] public bool IncrementalBuild { get; set; } = true;

    public override void _Ready() => Build();

    private void RebuildIfReady()
    {
        if (IsNodeReady() && _autoBuild)
            Build();
    }

    /// <summary>Clears the world and regenerates the level.</summary>
    public void Build()
    {
        _world = !NodeWorldPath.IsEmpty ? GetNodeOrNull<NodeWorld>(NodeWorldPath) : null;
        _world ??= GetParent<NodeWorld>();
        if (_world == null)
        {
            GD.PushWarning("NodeLevel: needs a NodeWorld parent (or NodeWorldPath) — nothing built.");
            return;
        }

        // unchecked: the mix deliberately overflows, and a negative seed must
        // reinterpret its bits rather than throw on the cast.
        unchecked
        {
            _rng = (ulong)(uint)_seed * 6364136223846793005UL + 1442695040888963407UL;
            if (_rng == 0)
                _rng = 0x9E3779B97F4A7C15UL; // xorshift is stuck at zero
        }

        // One rebuild for the whole level rather than one per node: a rebuild
        // costs the same whether one node changed or fifty thousand.
        // Generate the node set without meshing, then hand the meshing to
        // the incremental path so the loading screen can report progress.
        _world.Batch(() =>
        {
            _world.Clear();
            BuildPillar();
            BuildProps();
        }, wholesale: true, deferMesh: IncrementalBuild && !Engine.IsEditorHint());

        if (IncrementalBuild && !Engine.IsEditorHint())
            _world.BeginIncrementalBuild();
    }

    // ------------------------------------------------------------------ pillar

    /// <summary>
    /// A rounded column, its top cut flat at y = -1 so the walkable surface is
    /// the plane y = 0. The radius shrinks with depth and is perturbed by
    /// angular noise, so the silhouette reads as weathered rock while the top
    /// stays flat.
    /// </summary>
    private void BuildPillar()
    {
        // Angular noise, sampled once per rebuild so every depth slice shares
        // the same lobes and the column has continuous vertical ribs rather
        // than a different outline on each layer.
        const int Lobes = 8;
        var phase = new float[Lobes];
        var amplitude = new float[Lobes];
        for (int i = 0; i < Lobes; i++)
        {
            phase[i] = NextFloat() * Mathf.Tau;
            // Higher lobes contribute progressively less, so the shape has a
            // few big bulges with fine detail on top instead of uniform fuzz.
            amplitude[i] = _rimNoise * _pillarRadius / (i + 1.5f);
        }

        for (int depth = 0; depth < _pillarDepth; depth++)
        {
            int y = -1 - depth;

            // Taper: full width at the top, narrowing with depth along a curve
            // that stays wide near the surface and falls away faster below.
            float t = depth / (float)Mathf.Max(_pillarDepth - 1, 1);
            float shrink = 1f - _pillarTaper * t * t;
            float baseRadius = _pillarRadius * shrink;
            if (baseRadius < 1.5f)
                continue;

            int extent = Mathf.CeilToInt(baseRadius + _rimNoise * _pillarRadius + 1);
            for (int x = -extent; x <= extent; x++)
            {
                for (int z = -extent; z <= extent; z++)
                {
                    float distance = Mathf.Sqrt(x * x + z * z);
                    if (distance > extent)
                        continue;

                    float angle = Mathf.Atan2(z, x);
                    float wobble = 0f;
                    for (int i = 0; i < Lobes; i++)
                        wobble += Mathf.Sin(angle * (i + 1) + phase[i]) * amplitude[i];

                    // Noise fades out toward the top, so the rim under
                    // the flat surface stays clean.
                    if (distance <= baseRadius + wobble * Mathf.Min(1f, t * 3f))
                        _world.AddNode(new Vector3I(x, y, z));
                }
            }
        }

        // The flat top: a clean disc at y = -1 with no wobble at all, so the
        // walkable surface has a crisp edge and no bites taken out of it.
        for (int x = -_pillarRadius; x <= _pillarRadius; x++)
        {
            for (int z = -_pillarRadius; z <= _pillarRadius; z++)
            {
                if (x * x + z * z <= _pillarRadius * _pillarRadius)
                    _world.AddNode(new Vector3I(x, -1, z));
            }
        }
    }

    // ------------------------------------------------------------------- props

    private enum PropKind
    {
        Cube,
        RectPrism,
        TriangularPrism,
        Pyramid,
        Sphere,
    }

    /// <summary>
    /// Scatters primitive solids over the surface, rejecting positions that
    /// would overlap an earlier prop or the player's spawn.
    /// </summary>
    private void BuildProps()
    {
        // Keep the spawn point clear so the player never starts inside a prop.
        // The spawn point counts as a ground prop, so nothing lands on the
        // player. Floating props may pass overhead freely.
        var placed = new List<(Vector2 centre, float radius, bool floating)>
        {
            (Vector2.Zero, 4f, false),
        };

        int attempts = 0;
        int built = 0;
        while (built < _propCount && attempts < _propCount * 40)
        {
            attempts++;

            // Cycling through the kinds rather than rolling each independently:
            // the bigger solids are rejected far more often (they need more
            // clearance), so an independent roll silts the level up with cubes.
            // Starting each attempt at the next kind in turn keeps the mix even.
            var kind = (PropKind)(attempts % 5);

            int size = 2 + (int)(NextFloat() * 4);

            // Per-kind dimensions are rolled HERE, before placement, so the
            // footprint below bounds the actual solid rather than an estimate.
            int width = size, depth = size, height = size;
            int prismLength = 0, prismAxis = 0;
            switch (kind)
            {
                case PropKind.RectPrism:
                    width = size + 1 + (int)(NextFloat() * 3);
                    depth = size + (int)(NextFloat() * 2);
                    break;
                case PropKind.TriangularPrism:
                    prismLength = size + 2 + (int)(NextFloat() * 3);
                    prismAxis = (int)(NextFloat() * 4) & 3;
                    break;
                case PropKind.Pyramid:
                    height = size + 2;
                    break;
            }

            // Half-extent of the solid in the widest horizontal direction.
            float reach = kind switch
            {
                PropKind.Pyramid => height,
                PropKind.TriangularPrism => Mathf.Max(prismLength * 0.5f, size - 1) + 1,
                PropKind.RectPrism => Mathf.Max(width, depth) * 0.5f + 1,
                PropKind.Sphere => size + 1,
                _ => width * 0.5f + 1,
            };

            // Uniform over the disc: sqrt keeps props from bunching at the
            // centre the way a linear radius would. Held inside the rim so
            // nothing hangs over the edge.
            // The extra node of margin absorbs rounding the centre to a cell,
            // which can push a prop half a node further out than planned.
            float maxRadius = _pillarRadius - reach - 2f;
            if (maxRadius <= 2f)
                continue;
            float radius = Mathf.Sqrt(NextFloat()) * maxRadius;
            float angle = NextFloat() * Mathf.Tau;
            var centre = new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);

            bool floating = NextFloat() < _floatingFraction;
            int baseY = floating ? 5 + (int)(NextFloat() * 11) : 0;

            // Floating props only need to clear other floating props, and
            // ground props only other ground props: a ball 10 nodes up and a
            // cube below it do not collide, and forcing them apart in plan
            // view is what starved the level of props.
            bool clear = true;
            foreach (var (other, otherRadius, otherFloating) in placed)
            {
                if (otherFloating != floating)
                    continue;
                if (centre.DistanceTo(other) < reach + otherRadius + 1f)
                {
                    clear = false;
                    break;
                }
            }

            if (!clear)
                continue;

            placed.Add((centre, reach, floating));
            built++;

            var origin = new Vector3I(Mathf.RoundToInt(centre.X), baseY, Mathf.RoundToInt(centre.Y));

            switch (kind)
            {
                case PropKind.Cube:
                    AddBox(origin, width, height, depth);
                    break;
                case PropKind.RectPrism:
                    AddBox(origin, width, height, depth);
                    break;
                case PropKind.TriangularPrism:
                    AddTriangularPrism(origin, size, prismLength, prismAxis);
                    break;
                case PropKind.Pyramid:
                    AddPyramid(origin, height);
                    break;
                case PropKind.Sphere:
                    AddSphere(origin, size);
                    break;
            }
        }
    }

    /// <summary>Cube or rectangular prism, sitting on `origin`.</summary>
    private void AddBox(Vector3I origin, int width, int height, int depth)
    {
        int hx = width / 2, hz = depth / 2;
        for (int x = -hx; x <= hx; x++)
            for (int y = 0; y < height; y++)
                for (int z = -hz; z <= hz; z++)
                    _world.AddNode(origin + new Vector3I(x, y, z));
    }

    /// <summary>
    /// A prism with a right-triangle cross-section: a ramp `length` nodes
    /// long rising to `height`, extruded across its width. `axis` picks which
    /// of the four horizontal directions it climbs toward.
    /// </summary>
    private void AddTriangularPrism(Vector3I origin, int height, int length, int axis)
    {
        int halfWidth = Mathf.Max(1, height - 1);

        // Centred on its origin along the run, not growing out from it, so the
        // footprint used to place it actually bounds it — otherwise a long
        // prism reaches past the rim it was measured against.
        int halfLength = length / 2;
        for (int step = 0; step < length; step++)
        {
            // Height falls off linearly along the run, so the slope is a
            // staircase of single nodes rather than a smooth wedge — which is
            // what a node grid can actually represent.
            int columnHeight = Mathf.Max(1, Mathf.RoundToInt(height * (1f - step / (float)length)));
            int along = step - halfLength;
            for (int across = -halfWidth; across <= halfWidth; across++)
            {
                for (int y = 0; y < columnHeight; y++)
                {
                    Vector3I offset = axis switch
                    {
                        0 => new Vector3I(along, y, across),
                        1 => new Vector3I(-along, y, across),
                        2 => new Vector3I(across, y, along),
                        _ => new Vector3I(across, y, -along),
                    };
                    _world.AddNode(origin + offset);
                }
            }
        }
    }

    /// <summary>A square pyramid: stacked squares shrinking by one ring per
    /// level, so the sides step at 45 degrees.</summary>
    private void AddPyramid(Vector3I origin, int baseHalfWidth)
    {
        for (int y = 0; y <= baseHalfWidth; y++)
        {
            int half = baseHalfWidth - y;
            for (int x = -half; x <= half; x++)
                for (int z = -half; z <= half; z++)
                    _world.AddNode(origin + new Vector3I(x, y, z));
        }
    }

    /// <summary>A ball of nodes, measured from cell centres so it comes out
    /// symmetric rather than lopsided toward the origin corner.</summary>
    private void AddSphere(Vector3I origin, int radius)
    {
        for (int x = -radius; x <= radius; x++)
        {
            for (int y = -radius; y <= radius; y++)
            {
                for (int z = -radius; z <= radius; z++)
                {
                    if (new Vector3(x, y, z).Length() <= radius + 0.25f)
                        _world.AddNode(origin + new Vector3I(x, y + radius, z));
                }
            }
        }
    }

    // --------------------------------------------------------------------- rng

    /// <summary>Deterministic 0..1 stream — same seed, same level.</summary>
    private float NextFloat()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 7;
        _rng ^= _rng << 17;
        return (_rng >> 40) / 16777216f;
    }
}
