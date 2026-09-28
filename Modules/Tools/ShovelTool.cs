using Godot;
using System.Collections.Generic;
using GameBase.Nodes;
using GameBase.Tools.Highlight;

namespace GameBase.Tools;

/// <summary>The shovel's modes, in the order the mode key steps through them.</summary>
public enum ShovelMode
{
    RaiseSharp,
    RaiseGradual,
    LowerSharp,
    LowerGradual,
    Level,
    Smooth,
}

/// <summary>
/// Shapes particle nodes like a terrain tool: hold the left button and the
/// ground inside the brush moves gradually, in whichever mode is selected (the
/// mode key steps through them; the mode bar shows which).
///
///   - RAISE and LOWER lift or sink the ground. SHARP concentrates the change
///     into a point; GRADUAL spreads it evenly over the brush.
///   - LEVEL flattens toward the height of the ground under the crosshair when
///     the press began, so dragging carries one level across the terrain.
///   - SMOOTH softens bumps and edges without raising or lowering overall.
///
/// THE BRUSH is two circles: full strength inside the inner, fading to nothing
/// at the outer. Every mode shares the outer circle; the modes differ in the
/// inner one. Nothing outside the outer circle ever moves -- building straight
/// up raises a column as wide as the brush, never a mountain whose foot
/// spreads as it grows (see <see cref="ParticleSculpt"/>).
///
/// The circles lie ON the ground (<see cref="SurfaceDisc"/>): flat on flat
/// ground, standing on a wall, upside down on a ceiling, folded over an edge,
/// always covering the same area. Every mode works along the surface the
/// brush is on: raise pushes a wall out sideways and a ceiling down, and
/// Level flattens to the plane of the surface where the press began.
///
/// The right button does nothing with the shovel.
///
/// Its highlight is the brush drawn ON the ground, like a texture: the outer
/// circle, and for the raise and lower modes the inner one too, alike, as
/// pixel-art circles on the world's own pixel grid, with a glow over the
/// ground inside -- the face that will change -- strongest at the outer
/// circle and working inward, particles drifting in toward the middle.
/// </summary>
public sealed class ShovelTool : Tool
{
    public override string Id => "shovel";

    public override NodeForm Form => NodeForm.Particle;

    public override ToolCadence Cadence => ToolCadence.Continuous;

    private static readonly string[] ModeNames =
    {
        "Raise sharp", "Raise gradual", "Lower sharp", "Lower gradual", "Level", "Smooth",
    };

    public override IReadOnlyList<string> Modes => ModeNames;

    /// <summary>A few steps in front of the player: as far as the pickaxe.</summary>
    public override float Reach => 6f;

    /// <summary>The brush's outer circle, shared by every mode.</summary>
    public float OuterRadius { get; set; } = 3f;

    /// <summary>The inner circle of the sharp modes: a small one, concentrating the change.</summary>
    public float SharpInner { get; set; } = 0.6f;

    /// <summary>The inner circle of the gradual modes: a large one, spreading the change evenly.</summary>
    public float GradualInner { get; set; } = 2.2f;

    /// <summary>
    /// Where Level and Smooth start to fade out toward the rim, so the patch
    /// they work blends into the ground around it rather than ending in a step.
    /// Not drawn: each has only the one brush.
    /// </summary>
    public float ShapingInner { get; set; } = 1.8f;

    /// <summary>How fast Raise and Lower move the ground at full strength, in world units a second.</summary>
    public float Rate { get; set; } = 2.5f;

    /// <summary>How fast Level moves the ground toward its height, in world units a second.</summary>
    public float LevelRate { get; set; } = 3f;

    /// <summary>How much of the way toward the surrounding average Smooth moves the ground in a second.</summary>
    public float SmoothRate { get; set; } = 2.5f;

    /// <summary>How far the highlight's skin floats above the ground.</summary>
    public float Lift { get; set; } = 0.02f;

    /// <summary>Paths the brush's disc walks out along; also the segments of its drawn circles.</summary>
    public int Directions { get; set; } = 40;

    /// <summary>The disc a brush lies on, centred on a target (in the world's local space).</summary>
    public SurfaceDisc DiscAt(NodeWorld world, in NodeHit target) => Brush(world, target).Disc;

    // The last brush laid, kept for the rest of its frame: the highlight lays
    // it first, and the shaping that follows in the same frame reuses it
    // rather than walking the same disc again. Shaping changes the ground, so
    // it drops the brush once it has used it.
    private (NodeWorld World, Vector3 Point, ulong Frame, ParticleField Field, SurfaceDisc Disc) _laid;

    /// <summary>The patch of field around a target, and the disc walked over it.</summary>
    private (ParticleField Field, SurfaceDisc Disc) Brush(NodeWorld world, in NodeHit target)
    {
        ulong frame = Engine.GetProcessFrames();

        if (_laid.Disc != null && _laid.World == world && _laid.Frame == frame && _laid.Point == target.Point)
            return (_laid.Field, _laid.Disc);

        Vector3 centre = world.ToLocal(target.Point);

        // A disc walked along the surface never strays further than its
        // radius through the air, and shaping reaches a little past the
        // surface on either side.
        float radius = OuterRadius + SkinMargin;
        var field = ParticleField.Around(world, centre, radius + (NodeFill.Range + 1.5f) * world.NodeSize);
        SurfaceDisc disc = SurfaceDisc.Build(field, centre, world.GlobalBasis.Inverse() * target.Normal,
            radius, Mathf.Max(8, Directions));

        _laid = (world, target.Point, frame, field, disc);
        return (field, disc);
    }

    /// <summary>The mode a context selects.</summary>
    public static ShovelMode ModeOf(in ToolContext context) =>
        (ShovelMode)Mathf.PosMod(context.Mode, ModeNames.Length);

    /// <summary>The brush a mode works with.</summary>
    public SculptBrush BrushFor(ShovelMode mode) => new(mode switch
    {
        ShovelMode.RaiseSharp or ShovelMode.LowerSharp => SharpInner,
        ShovelMode.RaiseGradual or ShovelMode.LowerGradual => GradualInner,
        _ => ShapingInner,
    }, OuterRadius);

    /// <summary>Applies the selected mode for one frame.</summary>
    public override bool Mine(in ToolContext context, in NodeHit target)
    {
        if (target.Type is not ParticleNode type)
            return false;

        ShovelMode mode = ModeOf(context);
        SculptBrush brush = BrushFor(mode);
        float delta = context.Delta;

        (SculptOperation operation, float amount) = mode switch
        {
            ShovelMode.RaiseSharp or ShovelMode.RaiseGradual => (SculptOperation.Raise, Rate * delta),
            ShovelMode.LowerSharp or ShovelMode.LowerGradual => (SculptOperation.Lower, Rate * delta),
            ShovelMode.Level => (SculptOperation.Level, LevelRate * delta),
            _ => (SculptOperation.Smooth, Mathf.Clamp(SmoothRate * delta, 0f, 1f)),
        };

        // The ledger is asked about the most this step could move, each way
        // the operation can move it.
        float node = context.World.NodeSize;
        float estimate = Mathf.Pi * brush.Outer * brush.Outer * Rate * delta / (node * node * node);
        bool adds = operation != SculptOperation.Lower;
        bool removes = operation != SculptOperation.Raise;

        if ((adds && !MayPlace(context, type, estimate)) || (removes && !MayMine(context, type, estimate)))
            return false;

        NodeWorld world = context.World;
        (ParticleField field, SurfaceDisc disc) = Brush(world, target);
        _laid = default;

        // Level holds to the plane of the surface where the press began: its
        // point, and the way the ground faced there.
        ToolStroke stroke = context.Stroke;
        if (stroke.IsNew)
            stroke.Begin(target.Point, world.GlobalBasis * disc.Centre.Normal);

        Vector3 planePoint = world.ToLocal(stroke.Anchor);
        Vector3 planeNormal = world.GlobalBasis.Inverse() * stroke.AnchorNormal;

        SculptResult result = ParticleSculpt.Apply(field, type, disc, brush, operation, amount,
            planePoint, planeNormal);

        if (result.Added > 0f)
            context.Ledger.Placed(type, result.Added);

        if (result.Removed > 0f)
            context.Ledger.Mined(type, result.Removed);

        return result.Changed;
    }

    /// <summary>The right button does nothing with the shovel: every mode is on the left.</summary>
    public override bool Place(in ToolContext context, in NodeHit target) => false;

    public override bool SameOutline(in NodeHit previous, in NodeHit current) =>
        previous.Point.DistanceSquaredTo(current.Point) < 0.0004f;

    /// <summary>Every how many rings and directions the skin is measured against the drawn ground.</summary>
    public int GapStride { get; set; } = 3;

    /// <summary>
    /// How far the drawn ground lies from each point of the skin, along the
    /// ground's facing there: measured with a ray at every
    /// <see cref="GapStride"/>-th ring and direction, and blended between.
    /// </summary>
    private float[,] GapToDrawnGround(NodeWorld world, SurfaceDisc.Sample[,] samples, int rings, int directions,
        Godot.Collections.Array<Rid> exclude)
    {
        int stride = Mathf.Max(1, GapStride);
        float span = world.NodeSize * 0.5f;
        var gap = new float[rings + 1, directions + 1];

        float Measure(int r, int i)
        {
            SurfaceDisc.Sample sample = samples[r, i];
            Vector3 point = world.ToGlobal(sample.Position);
            Vector3 normal = (world.GlobalBasis * sample.Normal).Normalized();

            return world.Raycast(point + normal * span, point - normal * span, out NodeHit hit, exclude)
                ? (hit.Point - point).Dot(normal)
                : 0f;
        }

        // The measured points: every stride-th ring and direction, and always
        // the outermost ring and the last direction, so every point lies
        // between measured ones.
        var ringsAt = new System.Collections.Generic.List<int>();
        for (int r = 0; r < rings; r += stride)
            ringsAt.Add(r);
        ringsAt.Add(rings);

        var waysAt = new System.Collections.Generic.List<int>();
        for (int i = 0; i < directions; i += stride)
            waysAt.Add(i);
        waysAt.Add(directions);

        var measured = new float[ringsAt.Count, waysAt.Count];
        for (int a = 0; a < ringsAt.Count; a++)
        for (int b = 0; b < waysAt.Count; b++)
            measured[a, b] = waysAt[b] == directions && b > 0 ? measured[a, 0] : Measure(ringsAt[a], waysAt[b]);

        // Blend between the four measured points around each one.
        for (int a = 0; a + 1 < ringsAt.Count; a++)
        for (int b = 0; b + 1 < waysAt.Count; b++)
        {
            for (int r = ringsAt[a]; r <= ringsAt[a + 1]; r++)
            for (int i = waysAt[b]; i <= waysAt[b + 1]; i++)
            {
                float tr = (r - ringsAt[a]) / (float)Mathf.Max(1, ringsAt[a + 1] - ringsAt[a]);
                float ti = (i - waysAt[b]) / (float)Mathf.Max(1, waysAt[b + 1] - waysAt[b]);

                gap[r, i] = Mathf.Lerp(
                    Mathf.Lerp(measured[a, b], measured[a, b + 1], ti),
                    Mathf.Lerp(measured[a + 1, b], measured[a + 1, b + 1], ti),
                    tr);
            }
        }

        return gap;
    }

    /// <summary>
    /// How far past the outer circle the disc is walked: room under the
    /// circle's own pixels, which straddle it. The brush stops at the outer
    /// circle regardless.
    /// </summary>
    public float SkinMargin { get; set; } = 0.4f;

    /// <summary>Spacing of the skin's rings, in world units along the surface.</summary>
    public float SkinStep { get; set; } = 0.25f;

    /// <summary>
    /// The brush's highlight: a skin laid flush over the ground the brush
    /// covers, carrying at every point where it is in a flat circle -- the
    /// disc wrapped onto the ground like a sticker. The shader draws the
    /// circles, the gradient and the particles from that, on the world's own
    /// pixel grid.
    /// </summary>
    public override void Outline(in ToolContext context, in NodeHit target, HighlightBuilder builder)
    {
        NodeWorld world = context.World;
        SurfaceDisc disc = DiscAt(world, target);

        ShovelMode mode = ModeOf(context);

        // Only the raise and lower modes show their inner circle: each comes
        // sharp and gradual, and the inner circle is the difference. Level and
        // Smooth have one brush each -- still fading out toward the rim, so
        // the patch they work blends into the ground around it -- and a
        // circle there would mark a choice that does not exist.
        builder.DiscRadius = OuterRadius;
        builder.DiscInner = BrushFor(mode).Inner;
        builder.ShowInner = mode is not (ShovelMode.Level or ShovelMode.Smooth) && builder.DiscInner > 0.05f;

        int directions = disc.Directions;
        int rings = Mathf.Max(1, Mathf.CeilToInt(disc.Radius / SkinStep));

        // Where each point of the skin is on the ground, and where it is in
        // the flat circle: the distance along the surface and the direction
        // it set out in, which is exactly what those are in the circle.
        var on = new Vector3[rings + 1, directions + 1];
        var at = new Vector2[rings + 1, directions + 1];
        var samples = new SurfaceDisc.Sample[rings + 1, directions + 1];

        for (int i = 0; i <= directions; i++)
        {
            float angle = Mathf.Tau * i / directions;
            var way = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

            for (int r = 0; r <= rings; r++)
            {
                samples[r, i] = r == 0 ? disc.Centre : disc.At(i, disc.Radius * r / rings);
                at[r, i] = way * samples[r, i].Arc;
            }
        }

        // The disc lies on the fill field; the ground is DRAWN a little off it
        // -- inside it over a crest, outside it in a hollow -- and a skin on
        // the field floated clear of a crest as a flap. How far off is
        // measured with a ray at every few points, and blended across the rest:
        // the gap changes only over the lattice's own spacing.
        float[,] gap = GapToDrawnGround(world, samples, rings, directions, context.Exclude);

        for (int i = 0; i <= directions; i++)
        for (int r = 0; r <= rings; r++)
        {
            SurfaceDisc.Sample sample = samples[r, i];
            on[r, i] = world.ToGlobal(sample.Position + sample.Normal * (gap[r, i] + Lift));
        }

        for (int r = 0; r < rings; r++)
        for (int i = 0; i < directions; i++)
        {
            builder.DiscTriangle(on[r, i], at[r, i], on[r + 1, i], at[r + 1, i], on[r + 1, i + 1], at[r + 1, i + 1]);

            // The innermost ring meets at the centre: one triangle per sector.
            if (r > 0)
                builder.DiscTriangle(on[r, i], at[r, i], on[r + 1, i + 1], at[r + 1, i + 1], on[r, i + 1], at[r, i + 1]);
        }
    }
}
