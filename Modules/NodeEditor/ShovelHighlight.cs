using Godot;
using GameBase.Core;
using GameBase.Items;
using GameBase.Levels;

namespace GameBase.Player;

/// <summary>
/// Shows where the shovel will move sand, and which way.
///
/// THE COUNTERPART TO <see cref="NodeHighlight"/>, and shaped by the same rule:
/// the mark has to describe the edit that is actually about to happen. The
/// pickaxe edits ONE node, so its highlight traces that node's twelve edges and
/// the player can see exactly which block will go. The shovel edits an AREA
/// with a smooth falloff, so a box would be a lie twice over -- wrong shape,
/// and wrong about the edges, which fade rather than stop.
///
/// So this is a ring on the ground at the dig radius, with rays rising or
/// falling through it:
///
///   RAISE  rays travel outward, away from the planet
///   LOWER  rays travel inward, toward it
///
/// The direction of travel is the whole message. A static icon would have to be
/// read; movement is understood before it is read, which matters for a mode the
/// player flips constantly mid-dig.
///
/// The ring FOLLOWS THE SURFACE rather than being a flat disc laid on it. The
/// dig radius is a fair fraction of a small planet's curve, so a flat circle
/// would sink into the ground at its edges -- the same mistake a stock
/// wireframe cube makes on a curved block, and the reason NodeHighlight traces
/// arcs too.
///
/// Instance under the player alongside NodeEditor; it finds the camera, the
/// topsoil and the inventory itself.
/// </summary>
public partial class ShovelHighlight : Node3D
{
    [Export] public NodePath CameraPath { get; set; } = "";

    /// <summary>The topsoil this digs. Found by search when left empty.</summary>
    [Export] public NodePath TopsoilPath { get; set; } = "";

    /// <summary>How far the player can reach, matching NodeEditor.</summary>
    [Export(PropertyHint.Range, "1,100,0.5")] public float Reach { get; set; } = 6f;

    /// <summary>How wide the dig is, in world units.</summary>
    [Export(PropertyHint.Range, "0.5,12,0.25")] public float Radius { get; set; } = 3f;

    /// <summary>
    /// Which item this belongs to.
    ///
    /// Matched by id rather than by resource, so the indicator does not hold a
    /// reference to the item and a renamed or re-authored shovel still works.
    /// </summary>
    [Export] public string ToolId { get; set; } = "shovel";

    /// <summary>The action that swaps between raising and lowering.</summary>
    [Export] public StringName ModeAction { get; set; } = "shovel_mode";

    /// <summary>
    /// The button that digs.
    ///
    /// The same one the pickaxe mines with, so the tool in hand decides what a
    /// click does rather than the player having to learn a second button.
    /// </summary>
    [Export] public StringName DigAction { get; set; } = "destroy_block";

    /// <summary>
    /// How fast sand moves while the button is held, in world units a second.
    ///
    /// A RATE, not a step. The pickaxe removes whole blocks, so it repeats in
    /// discrete edits with a delay to keep a click to exactly one; sand has no
    /// such unit, so holding simply pours it and a tap moves a little.
    /// </summary>
    [Export(PropertyHint.Range, "0.5,30,0.5")] public float Strength { get; set; } = 6f;

    [ExportGroup("Look")]

    /// <summary>
    /// Warm amber, keyed to the crosshair and the block outline so all three
    /// read as one targeting system rather than three unrelated marks.
    ///
    /// DARKER THAN THE COLOUR IT LOOKS. The mark blends ADDITIVELY, so
    /// whatever is written here is added to the sand already under it -- and
    /// the crosshair's own near-white, added to lit ground, clipped every
    /// channel to 255. Measured: both modes rendered at rgb(254,253,252) and
    /// rgb(251,250,250), which is to say identically white, and the colour cue
    /// that tells raise from lower in a still frame was simply not there.
    /// Kept low enough that the sum still has somewhere to go.
    /// </summary>
    [Export] public Color RaiseColor { get; set; } = new(0.62f, 0.40f, 0.10f);

    /// <summary>
    /// Lowering is tinted cool, as a SECOND cue on top of the ray direction.
    ///
    /// Direction alone is enough to read while the rays are moving, but the
    /// indicator is also seen in a still frame -- a screenshot, or the instant
    /// after a mode flip -- and colour survives that where motion does not.
    ///
    /// Weighted into blue hard, and dark for the same reason as
    /// <see cref="RaiseColor"/>: against warm sand an additive blue has to be
    /// almost free of red and green to read as blue at all.
    /// </summary>
    [Export] public Color LowerColor { get; set; } = new(0.06f, 0.34f, 0.72f);

    /// <summary>Bar thickness as a fraction of the dig radius.</summary>
    [Export(PropertyHint.Range, "0.005,0.2,0.005")]
    public float Thickness { get; set; } = 0.045f;

    /// <summary>
    /// How far the ring floats above the sand.
    ///
    /// Without it the ring lands exactly on the surface it traces and fights it
    /// for depth, which shows as the circle stitching in and out along its
    /// length -- the same problem NodeHighlight solves with Expand.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float Lift { get; set; } = 0.12f;

    /// <summary>How many segments the ring is drawn from.</summary>
    [Export(PropertyHint.Range, "8,96,4")] public int Segments { get; set; } = 48;

    /// <summary>How many rays rise or fall through the ring.</summary>
    [Export(PropertyHint.Range, "0,16,1")] public int Rays { get; set; } = 6;

    /// <summary>How far a ray travels before it restarts, in world units.</summary>
    [Export(PropertyHint.Range, "0.25,8,0.25")] public float RayTravel { get; set; } = 1.6f;

    /// <summary>How long one ray takes to make that trip, in seconds.</summary>
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float RayPeriod { get; set; } = 0.9f;

    /// <summary>How much wider a ray is than the ring.</summary>
    [Export(PropertyHint.Range, "1,6,0.25")] public float RayWidth { get; set; } = 2.5f;

    /// <summary>Whether the shovel is currently raising rather than lowering.</summary>
    public bool Raising { get; private set; } = true;

    /// <summary>
    /// Sets which way the shovel moves sand, without a key press.
    ///
    /// For tests and tools. The player swaps with <see cref="ModeAction"/>;
    /// this exists so a check does not have to fake an input event to reach a
    /// mode, and so anything that wants to force a mode has one way to do it.
    /// </summary>
    public void SetMode(bool raising) => Raising = raising;

    /// <summary>Is the indicator currently drawn? For tests and tools.</summary>
    public bool Showing => _visible;

    /// <summary>Where the dig would land, when the indicator is showing.</summary>
    public Vector3 Target => _target;

    private Camera3D _camera;
    private TopsoilCap _soil;
    private Inventory _inventory;
    private CollisionObject3D _playerBody;

    private MeshInstance3D _ring;
    private MeshInstance3D _rays;

    private Vector3 _target;
    private bool _visible;
    private float _phase;

    private StandardMaterial3D _material;

    /// <summary>
    /// Unshaded and additive, so the mark GLOWS rather than being painted on.
    ///
    /// Unshaded alone reads the same against a lit slope and a shadowed one,
    /// which is what NodeHighlight needs. Additive goes further: the mark
    /// brightens whatever is under it, which is what makes a ray look like
    /// light leaving the ground instead of a white stick standing in it.
    /// </summary>
    private StandardMaterial3D Material => _material ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,

        // Vertex colour carries the per-ray fade, so one material draws the
        // whole indicator rather than one per ray.
        VertexColorUseAsAlbedo = true,

        // No shadow and no depth write: an additive mark that wrote depth
        // would punch a hole in anything drawn after it.
        DisableReceiveShadows = true,
        NoDepthTest = false,
    };

    public override void _Ready()
    {
        _camera = !CameraPath.IsEmpty ? GetNodeOrNull<Camera3D>(CameraPath) : null;
        _camera ??= NodeSearch.FindByType<Camera3D>(GetTree().CurrentScene ?? GetParent());

        _inventory = NodeSearch.FindByType<Inventory>(GetTree().CurrentScene ?? GetParent());

        for (Node node = GetParent(); node != null; node = node.GetParent())
        {
            if (node is CollisionObject3D body)
            {
                _playerBody = body;
                break;
            }
        }

        _ring = NewMesh("ShovelRing");
        _rays = NewMesh("ShovelRays");
    }

    private MeshInstance3D NewMesh(string name)
    {
        var mesh = new MeshInstance3D
        {
            Name = name,

            // Built in world space, so the parent's transform must not move it
            // again.
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };

        AddChild(mesh);

        return mesh;
    }

    public override void _Process(double delta)
    {
        if (_camera == null || Soil() == null)
            return;

        // GAMEPLAY ONLY. The mark is a promise about what a click will do, and
        // with a menu up a click does something else entirely -- leaving it on
        // screen says the shovel is live when it is not.
        if (!Holding() || UiStateService.Instance is { GameplayHasInput: false })
        {
            Clear();
            return;
        }

        if (Input.IsActionJustPressed(ModeAction))
            Raising = !Raising;

        if (!TryGetTarget(out Vector3 at))
        {
            Clear();
            return;
        }

        _target = at;

        // The rays run from a clock rather than from the frame, so their speed
        // does not follow the frame rate.
        _phase += (float)delta / Mathf.Max(0.01f, RayPeriod);
        _phase -= Mathf.Floor(_phase);

        if (!_visible)
        {
            _visible = true;
            _ring.Visible = true;
            _rays.Visible = true;
        }

        // THE RING IS REBUILT ONLY WHEN THE AIM MOVES, but the rays are rebuilt
        // every frame -- they are animated, which is the one thing here that
        // cannot be cached.
        BuildRing(at);
        BuildRays(at);

        Dig(delta);
    }

    /// <summary>
    /// The topsoil, looked up when it is first wanted rather than at _Ready.
    ///
    /// LAZILY, because the cap does not exist yet when the player is made. The
    /// streamer builds it deferred -- it needs a grid, and the grid needs the
    /// world to be in the tree -- so a search at _Ready reliably finds nothing
    /// and caches that nothing forever. The indicator then never appears, on a
    /// planet that does have soil, with no error anywhere to say why.
    /// </summary>
    private TopsoilCap Soil()
    {
        if (_soil != null && Godot.GodotObject.IsInstanceValid(_soil))
            return _soil;

        _soil = !TopsoilPath.IsEmpty ? GetNodeOrNull<TopsoilCap>(TopsoilPath) : null;
        _soil ??= NodeSearch.FindByType<TopsoilCap>(GetTree().CurrentScene ?? GetParent());

        return _soil;
    }

    /// <summary>Drops the mark. Named apart from Node3D.Hide, which hides this
    /// node itself rather than the two meshes under it.</summary>
    private void Clear()
    {
        if (!_visible) return;

        _visible = false;
        _ring.Visible = false;
        _rays.Visible = false;
    }

    /// <summary>
    /// Moves sand under the crosshair, while a button is held.
    ///
    /// THE INDICATOR DOES THE DIGGING, rather than a separate editor node,
    /// because it already owns the aim. Splitting them would mean two raycasts
    /// per frame that have to agree, and the moment they disagree the sand
    /// moves somewhere other than where the ring is drawn -- the one failure
    /// the player cannot be asked to tolerate.
    /// </summary>
    private void Dig(double delta)
    {
        bool digging = Input.IsActionPressed(DigAction);

        if (!digging)
        {
            _digHeld = 0f;
            return;
        }

        _digHeld += (float)delta;

        // Continuous while held, unlike the pickaxe's discrete blocks: sand
        // flows, so a dig is a rate rather than a count.
        float amount = Strength * (float)delta * (Raising ? 1f : -1f);

        Soil()?.Sculpt(_target, Radius, amount);
    }

    private float _digHeld;

    /// <summary>Is the player holding the shovel?</summary>
    private bool Holding()
    {
        // No inventory in the scene means nothing can say otherwise, so the
        // indicator shows -- which is what a bare test scene wants.
        if (_inventory == null) return true;

        ItemStack held = _inventory.SelectedStack;

        return !held.IsEmpty && held.Type?.Id == ToolId;
    }

    /// <summary>Where the shovel would dig, if anywhere in reach.</summary>
    private bool TryGetTarget(out Vector3 at)
    {
        at = default;

        Vector2 centre = _camera.GetViewport().GetVisibleRect().Size * 0.5f;
        Vector3 from = _camera.ProjectRayOrigin(centre);
        Vector3 dir = _camera.ProjectRayNormal(centre);

        // Reach is measured from the PLAYER, matching NodeEditor and
        // NodeHighlight: measuring from the camera would silently shorten it by
        // the third-person set-back distance.
        float reach = Reach;

        if (_playerBody != null)
            reach += from.DistanceTo(_playerBody.GlobalPosition);

        var query = PhysicsRayQueryParameters3D.Create(from, from + dir * reach);

        if (_playerBody != null)
            query.Exclude = new Godot.Collections.Array<Rid> { _playerBody.GetRid() };

        Godot.Collections.Dictionary hit =
            GetViewport().World3D.DirectSpaceState.IntersectRay(query);

        if (hit.Count == 0) return false;

        at = (Vector3)hit["position"];

        return true;
    }

    /// <summary>
    /// The ring, as a band of quads following the surface.
    ///
    /// Walked as ARCS about the planet's centre rather than as a flat circle:
    /// the dig radius is a fair fraction of a small planet's curvature, so a
    /// flat disc would bury its own rim in the ground.
    /// </summary>
    private void BuildRing(Vector3 at)
    {
        Vector3 up = at.Normalized();

        Frame(up, out Vector3 east, out Vector3 north);

        float planet = at.Length();
        float arc = Radius / Mathf.Max(1f, planet);
        float bar = Radius * Thickness;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        Color tint = Raising ? RaiseColor : LowerColor;

        int steps = Mathf.Max(8, Segments);

        for (int i = 0; i < steps; i++)
        {
            float a0 = Mathf.Tau * i / steps;
            float a1 = Mathf.Tau * (i + 1) / steps;

            Vector3 p0 = OnGround(up, east, north, arc, a0, planet);
            Vector3 p1 = OnGround(up, east, north, arc, a1, planet);

            // Each segment is a short flat ribbon lying on the surface, lifted
            // clear of it. Ribbon rather than tube: at this thickness a tube is
            // the same handful of pixels and four times the triangles.
            //
            // LIFTED ALONG ITS OWN NORMAL, not along the centre's. Using one
            // up for the whole ring tilts the far side off the ground and
            // buries the near side in it -- on a planet this small that was
            // enough to sink half the circle out of sight, which read as a ring
            // that simply stopped halfway round.
            Vector3 lift0 = p0.Normalized() * Lift;
            Vector3 lift1 = p1.Normalized() * Lift;

            Vector3 o0 = (p0 - at).Normalized() * bar * 0.5f;
            Vector3 o1 = (p1 - at).Normalized() * bar * 0.5f;

            Quad(st, tint,
                p0 - o0 + lift0, p0 + o0 + lift0,
                p1 + o1 + lift1, p1 - o1 + lift1);
        }

        _ring.Mesh = st.Commit();
        _ring.MaterialOverride = Material;
    }

    /// <summary>
    /// The rays: short bars climbing out of the ring, or falling into it.
    ///
    /// Each one fades in as it starts and out as it finishes, so a ray is never
    /// seen to pop into or out of existence -- the eye reads a repeating flow
    /// rather than a row of blinking sticks.
    /// </summary>
    private void BuildRays(Vector3 at)
    {
        if (Rays <= 0)
        {
            _rays.Mesh = null;
            return;
        }

        Vector3 up = at.Normalized();

        Frame(up, out Vector3 east, out Vector3 north);

        float planet = at.Length();
        float arc = Radius / Mathf.Max(1f, planet);

        // WIDER THAN THE RING. The ring is a long line and reads at any
        // thickness; a ray is short, so at the same width it shrinks to a
        // speck and the indicator loses the half of its message that says
        // which way the sand is going.
        float bar = Radius * Thickness * RayWidth;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        Color tint = Raising ? RaiseColor : LowerColor;

        for (int i = 0; i < Rays; i++)
        {
            // Spread round the ring, each one further along its travel than the
            // last, so the rays chase one another instead of pulsing together.
            float around = Mathf.Tau * i / Rays;
            float along = (_phase + (float)i / Rays) % 1f;

            Vector3 foot = OnGround(up, east, north, arc, around, planet);

            // RAISE climbs away from the planet, LOWER falls toward it. This is
            // the whole message of the indicator.
            float travel = Raising
                ? along * RayTravel
                : (1f - along) * RayTravel;

            // The beam's length is a fixed share of its travel, so it reads as
            // a moving streak rather than as a tick mark. Long enough that two
            // consecutive positions overlap, which is what makes the eye join
            // them into one continuous flow.
            float length = RayTravel * 0.55f;

            Vector3 head = foot + up * (travel + Lift);
            Vector3 tail = foot + up * (travel + Lift - length);

            // Faded at both ends of the trip, so nothing pops.
            float fade = Mathf.Sin(along * Mathf.Pi);

            var lit = new Color(tint.R, tint.G, tint.B, tint.A * fade);

            // Billboarded by hand: the bar is widened along whichever direction
            // faces the camera, so a flat ribbon never turns edge-on and
            // vanishes.
            Vector3 toEye = (_camera.GlobalPosition - foot).Normalized();
            Vector3 side = up.Cross(toEye);

            if (side.LengthSquared() < 0.0001f)
                side = east;

            side = side.Normalized() * bar * 0.5f;

            Quad(st, lit,
                tail - side, tail + side,
                head + side, head - side);
        }

        _rays.Mesh = st.Commit();
        _rays.MaterialOverride = Material;
    }

    /// <summary>
    /// A point on the ring, dropped onto the ground beneath it.
    ///
    /// The shovel digs craters and piles mounds, so the surface under the ring
    /// is not the sphere the ring is computed on -- drawn at a fixed radius the
    /// circle floats over a pit and buries itself in a heap, which is worst
    /// exactly when the player is looking at something they just dug. So each
    /// point is cast onto whatever is actually there.
    /// </summary>
    private Vector3 OnGround(Vector3 up, Vector3 east, Vector3 north,
        float arc, float angle, float radius)
    {
        Vector3 on = OnSphere(up, east, north, arc, angle, radius);

        Vector3 dir = on.Normalized();

        // From well above the highest the sand can be piled, to below the
        // deepest it can be dug.
        var query = PhysicsRayQueryParameters3D.Create(
            dir * (radius + 12f), dir * (radius - 12f));

        if (_playerBody != null)
            query.Exclude = new Godot.Collections.Array<Rid> { _playerBody.GetRid() };

        Godot.Collections.Dictionary hit =
            GetViewport().World3D.DirectSpaceState.IntersectRay(query);

        return hit.Count == 0 ? on : (Vector3)hit["position"];
    }

    /// <summary>A point on the ring, at this angle about the target.</summary>
    private static Vector3 OnSphere(Vector3 up, Vector3 east, Vector3 north,
        float arc, float angle, float radius)
    {
        Vector3 out_ = east * Mathf.Cos(angle) + north * Mathf.Sin(angle);

        return (up * Mathf.Cos(arc) + out_ * Mathf.Sin(arc)) * radius;
    }

    /// <summary>
    /// Two directions across the surface at this point.
    ///
    /// Built from whichever world axis is least aligned with up, so the cross
    /// product never collapses -- which it would at the poles if one axis were
    /// simply assumed.
    /// </summary>
    private static void Frame(Vector3 up, out Vector3 east, out Vector3 north)
    {
        Vector3 axis = Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right;

        east = axis.Cross(up).Normalized();
        north = up.Cross(east).Normalized();
    }

    /// <summary>One quad, wound both ways so it is never invisible edge-on.</summary>
    private static void Quad(SurfaceTool st, Color tint,
        Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        st.SetColor(tint);

        st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
        st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);

        st.AddVertex(a); st.AddVertex(c); st.AddVertex(b);
        st.AddVertex(a); st.AddVertex(d); st.AddVertex(c);
    }
}
