using Godot;

namespace GameBase.Items;

/// <summary>
/// Draws one item as a small piece of pixel art: a real lit mesh, rendered
/// into a deliberately tiny SubViewport and blown back up with nearest-
/// neighbour filtering.
///
/// This is how the item gets the game's pixel look without the surrounding UI
/// getting it too. StylizedFilter pixelates the main viewport as a screen-space
/// pass, so anything drawn there — panel edges, text, slot borders — would be
/// chewed up along with the items. Here the pixel grid comes from the render
/// target's own size instead: at <see cref="IconResolution"/> of 32 the mesh is
/// genuinely rasterised into 32x32 texels, and the slot then displays those
/// texels as visible squares. The chrome around it is drawn at full resolution
/// and stays crisp, which is what lets the panel read as authored pixel art
/// rather than as a filtered photograph of a UI.
///
/// Rendering is on demand: the viewport updates for exactly one frame whenever
/// the item or the size changes, then goes idle. A grid of three dozen slots
/// each running a live 3D viewport every frame would cost far more than the
/// picture is worth, and the picture never changes on its own.
/// </summary>
public partial class ItemIcon : TextureRect
{
    /// <summary>
    /// Width and height of the render target, in texels — the icon's pixel
    /// grid. Low on purpose: 32 gives a chunky, clearly hand-made look that
    /// sits with the crosshair's blocks. Raising it past the slot's on-screen
    /// size stops it reading as pixel art at all.
    /// </summary>
    [Export(PropertyHint.Range, "8,128,1")]
    public int IconResolution { get; set; } = 32;

    /// <summary>Key light direction, in degrees. Angled so a cube shows a lit
    /// face, a mid face and a shadowed one rather than flat colour.</summary>
    [Export] public Vector3 LightRotation { get; set; } = new(-35f, -50f, 0f);

    /// <summary>Secondary light, roughly opposite the key, so the faces it
    /// misses do not all settle at one value.</summary>
    [Export] public Vector3 FillRotation { get; set; } = new(-10f, 135f, 0f);

    /// <summary>
    /// How far a long thin item is allowed to overhang its slot so that it
    /// still reads at a similar size to a compact one. 0 fits the whole
    /// silhouette (long items look small); 1 frames on the short axis alone
    /// (long items are heavily cropped).
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float LongAxisRelief { get; set; } = 0.45f;

    /// <summary>
    /// Key light strength. Modest on purpose: the icon is lit for READABILITY
    /// at 32 pixels, not for realism. Pushed high the lit face clips to white
    /// and every gradient inside it is lost, which is what collapses a cube's
    /// three faces into one flat shape at slot size.
    /// </summary>
    [Export(PropertyHint.Range, "0,8,0.05")] public float LightEnergy { get; set; } = 1.1f;

    /// <summary>
    /// Fill light colour: a plum tint, but much paler than the scene's own
    /// ambient. The world can afford a saturated plum fill because its
    /// surfaces are muted earth tones; an icon cannot, because the fill is
    /// most of what the shadowed faces get, and at full saturation it turns a
    /// green cube's dark faces purple and the item stops being recognisable as
    /// one colour of thing.
    /// </summary>
    [Export] public Color AmbientColor { get; set; } = new(0.62f, 0.52f, 0.60f);

    /// <summary>
    /// Fill strength, and the floor under the faces the key light misses.
    ///
    /// It is a balance rather than a preference. Too low and the unlit faces
    /// resolve to near-black, which against a near-black slot means the
    /// silhouette loses its shaded sides and the item reads as a flat patch of
    /// colour. Too high and it washes out the difference between faces, which
    /// costs the same shape from the other direction. 0.9 keeps the darkest
    /// face visibly green while the lit face stays clearly brighter.
    /// </summary>
    [Export(PropertyHint.Range, "0,4,0.05")] public float AmbientEnergy { get; set; } = 0.9f;

    private SubViewport _viewport;
    private Camera3D _camera;
    private MeshInstance3D _mesh;
    private ItemType _item;

    /// <summary>The item drawn here. Null renders nothing.</summary>
    public ItemType Item
    {
        get => _item;
        set
        {
            if (_item == value)
                return;
            _item = value;
            ApplyItem();
        }
    }

    public override void _Ready()
    {
        // Nearest-neighbour is the whole point: the texels the mesh was
        // rasterised into must survive being scaled up as hard squares. Linear
        // filtering here would blur them back into a small smooth render,
        // which is exactly the look this class exists to avoid.
        TextureFilter = TextureFilterEnum.Nearest;
        StretchMode = StretchModeEnum.KeepAspectCentered;
        MouseFilter = MouseFilterEnum.Ignore;

        // The inventory pauses the tree while it is open, and every icon in it
        // is built and rendered during exactly that time. Without this the
        // reveal below never runs and each icon stays invisible.
        ProcessMode = ProcessModeEnum.Always;

        BuildViewport();

        // Item may have been assigned before this node entered the tree — the
        // drag preview does exactly that — in which case ApplyItem could not
        // do anything yet, because there was no viewport to draw into. Run it
        // now that there is.
        ApplyItem();
    }

    private void BuildViewport()
    {
        _viewport = new SubViewport
        {
            Size = new Vector2I(IconResolution, IconResolution),
            // The slot's plum shows through wherever the mesh is not.
            TransparentBg = true,
            // Do not let the viewport keep the previous frame as a starting
            // point. Without this a transparent-background viewport composites
            // each render OVER what the target already held, so an item that
            // replaced another appeared on top of it rather than instead of
            // it — which is the overlap seen when two slots swap.
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            // Items are lit by this viewport's own light, not the level's.
            OwnWorld3D = true,
            World3D = new World3D(),
            // No MSAA: smoothed silhouette edges would half-fill the border
            // texels and undo the hard pixel edges.
            Msaa2D = Viewport.Msaa.Disabled,
            Msaa3D = Viewport.Msaa.Disabled,
        };
        AddChild(_viewport);

        var environment = new Godot.Environment
        {
            // CLEAR_COLOR, not CANVAS. Canvas mode composites whatever canvas
            // sits behind the viewport into its background, so the icon came
            // out carrying a patch of the UI behind it — most obvious on a
            // drag preview, which floats over the panel rather than sitting in
            // a slot. Clear-colour leaves the untouched texels genuinely
            // transparent, which is what TransparentBg above is for.
            BackgroundMode = Godot.Environment.BGMode.ClearColor,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = AmbientColor,
            AmbientLightEnergy = AmbientEnergy,
            // Linear rather than the main view's Filmic. Filmic exists to tame
            // a scene with a real dynamic range; applied to three flat faces it
            // only compresses the differences between them, which is the one
            // thing the icon needs to keep.
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };

        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            // Orthographic so the icon has no perspective convergence. A
            // perspective camera this close to a cube splays its edges and the
            // silhouette stops reading as a clean isometric block.
            Size = 1.6f,
            Near = 0.01f,
            Far = 10f,
            Position = new Vector3(0f, 0f, 3f),
            Environment = environment,
        };
        _viewport.AddChild(_camera);

        var light = new DirectionalLight3D
        {
            RotationDegrees = LightRotation,
            LightEnergy = LightEnergy,
        };
        _viewport.AddChild(light);

        // A second, weaker light from the opposite side. Ambient alone leaves
        // every face the key light misses at exactly the same value, so a
        // cube's two shadowed faces merge into one dark region and the near
        // vertical edge disappears. This picks one of them out just enough to
        // put that edge back, without competing with the key light for which
        // face reads as lit.
        var fill = new DirectionalLight3D
        {
            RotationDegrees = FillRotation,
            LightEnergy = LightEnergy * 0.45f,
        };
        _viewport.AddChild(fill);

        _mesh = new MeshInstance3D();
        _viewport.AddChild(_mesh);

        Texture = _viewport.GetTexture();
    }

    private void ApplyItem()
    {
        if (_mesh == null)
            return;

        _mesh.Mesh = _item?.ResolvedIconMesh;
        if (_item != null)
        {
            _mesh.RotationDegrees = _item.IconRotation;

            // Aim at the middle of the mesh AS ROTATED, rather than at the
            // origin. A mesh is not generally centred on its own origin, and
            // rotating it moves the middle of its silhouette regardless — a
            // cube turned -20/-35 sits visibly up and left of centre. Framing
            // on the transformed bounds means every item lands in the middle
            // of its slot without per-item nudging.
            Aabb bounds = _mesh.Transform * _mesh.GetAabb();
            Vector3 centre = bounds.GetCenter();

            // Fit the whole silhouette, then back off so it does not touch the
            // edges. The longest horizontal/vertical extent decides the frame,
            // so a tall item and a wide one are both contained.
            //
            // The long axis is pulled back toward the short one before it
            // decides the frame. Framing purely on the longest extent is
            // correct for a roughly square item like a cube, but it punishes a
            // long thin one: a shovel is three times taller than it is wide, so
            // fitting its height leaves its width occupying a third of the
            // slot and the tool reads as a faint scratch next to a cube that
            // fills its own slot. Splitting the difference lets the long axis
            // overhang the frame slightly — which costs nothing, because the
            // overhang is the empty air beside a narrow handle — while the
            // item itself comes out at a comparable visual weight.
            float longest = Mathf.Max(bounds.Size.X, bounds.Size.Y);
            float shortest = Mathf.Min(bounds.Size.X, bounds.Size.Y);
            float extent = Mathf.Lerp(longest, shortest, LongAxisRelief);
            float fit = Mathf.Max(0.01f, extent) * 1.25f;

            _camera.Size = fit / Mathf.Max(0.01f, _item.IconZoom);
            _camera.Position = new Vector3(centre.X, centre.Y, centre.Z + 3f);
        }

        Redraw();
    }

    /// <summary>
    /// Renders one frame, then lets the viewport go idle again. Public so a
    /// caller that animates an item's icon can drive it; nothing in the module
    /// does today.
    /// </summary>
    public void Redraw()
    {
        if (_viewport == null)
            return;

        // An empty icon is hidden outright rather than rendered.
        //
        // Clearing the mesh and re-rendering is not enough: a SubViewport with
        // nothing to draw does not necessarily clear its target, so the
        // texture keeps the last item that WAS drawn into it — which is how an
        // emptied slot went on showing its old item, and how a slot whose item
        // had been dragged away still displayed it.
        if (_item == null)
        {
            Visible = false;
            _pendingReveal = false;
            SetProcess(false);
            return;
        }

        Visible = true;
        _viewport.Size = new Vector2I(IconResolution, IconResolution);

        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;

        // Only a target that has NEVER been drawn into needs hiding.
        //
        // A render target is reused memory, so before its first render it
        // holds whatever the last user of that memory left — which for a drag
        // preview, built fresh every drag, is some previously dragged item.
        // Hiding until the render lands is what stops that ghost.
        //
        // An icon that is merely CHANGING does not need it, and paying it
        // anyway is worse than the problem: the render takes a few frames to
        // arrive, so every swap blinked its items out and back. What makes
        // that safe is ClearMode.Always above — the target is wiped before
        // each render rather than composited onto, so the worst a visible icon
        // can show in the meantime is its own previous frame, which is what it
        // was already showing.
        if (_everRendered)
            return;

        Modulate = Colors.Transparent;

        // Counted in FRAMES DRAWN rather than _Process ticks: the two are not
        // in step, especially while the tree is paused, so a tick is no
        // evidence that a frame was drawn.
        _revealOnFrame = Engine.GetFramesDrawn() + 3;
        _pendingReveal = true;
        SetProcess(true);
    }

    /// <summary>
    /// Whether this icon's render target has been drawn into at least once.
    /// Until it has, the target holds unrelated memory and must not be shown.
    /// </summary>
    private bool _everRendered;

    /// <summary>
    /// Reveals the icon once the requested render has landed.
    ///
    /// Waits a full frame rather than revealing immediately: the render
    /// happens at the end of the frame the request was made in, so the texture
    /// is only guaranteed current from the NEXT frame onward.
    /// </summary>
    public override void _Process(double delta)
    {
        if (!_pendingReveal)
        {
            SetProcess(false);
            return;
        }

        if (Engine.GetFramesDrawn() < _revealOnFrame)
            return;

        _pendingReveal = false;
        _everRendered = true;
        Modulate = Colors.White;
        SetProcess(false);
    }

    private bool _pendingReveal;

    /// <summary>Frame counter value at which the requested render is certain to
    /// have landed, so the icon can be shown.</summary>
    private int _revealOnFrame;
}
