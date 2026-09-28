using Godot;
using GameBase.Core;
using GameBase.Filters;
using GameBase.Nodes;

namespace GameBase.Tools.Highlight;

/// <summary>
/// Draws the held tool's highlight on its target: a thin outline and a glow,
/// or for a brush, a skin laid flush on the ground that the shader draws the
/// brush's circles onto, pixel by pixel on the world's own pixel grid.
///
/// The tool describes the shapes (<see cref="ITool.Outline"/>); this owns the
/// two meshes and the materials, and rebuilds only when the tool or its target
/// changes -- the glow animates in the shader, so a still target costs nothing
/// per frame.
/// </summary>
public partial class ToolHighlight : Node3D
{
    private const string ShaderPath = "res://Modules/Tools/Highlight/ToolHighlight.gdshader";

    /// <summary>
    /// Above the stylised filter's 100, so the highlight is drawn after it and
    /// stays crisp. The glow draws after the outline so the outline shows
    /// through it.
    /// </summary>
    private const int OutlinePriority = 101;
    private const int GlowPriority = 102;

    private readonly HighlightBuilder _builder = new();

    private MeshInstance3D _outline;
    private MeshInstance3D _glow;
    private MeshInstance3D _disc;
    private ShaderMaterial _outlineMaterial;
    private ShaderMaterial _glowMaterial;
    private ShaderMaterial _discMaterial;

    private ITool _tool;
    private int _mode;
    private NodeHit _target;
    private Vector3 _eye;

    /// <summary>Is a highlight on screen? For tests and tools.</summary>
    public bool Showing { get; private set; }

    /// <summary>The target the highlight is drawn on, while <see cref="Showing"/>.</summary>
    public NodeHit Target => _target;

    public override void _Ready()
    {
        var shader = GD.Load<Shader>(ShaderPath);

        _outlineMaterial = new ShaderMaterial { Shader = shader, RenderPriority = OutlinePriority };
        _outlineMaterial.SetShaderParameter("solid", 1f);
        _outlineMaterial.SetShaderParameter("depth_pull", 0.06f);
        _outlineMaterial.SetShaderParameter("color", Palette.Highlight);

        _glowMaterial = new ShaderMaterial { Shader = shader, RenderPriority = GlowPriority };
        _glowMaterial.SetShaderParameter("color", Palette.Highlight);

        // Particles are exactly as thick as the outline: the same width per
        // unit of distance, within the same limits.
        _glowMaterial.SetShaderParameter("width_per_distance", _builder.BarWidthPerDistance);
        _glowMaterial.SetShaderParameter("min_width", _builder.MinBarWidth);
        _glowMaterial.SetShaderParameter("max_width", _builder.MaxBarWidth);

        // A brush's skin: the same particles and gradient, drawn in disc mode.
        // Pulled further toward the camera than the glow: the skin lies on
        // the fill field's surface, which the drawn mesh follows closely but
        // not exactly.
        _discMaterial = new ShaderMaterial { Shader = shader, RenderPriority = GlowPriority };
        _discMaterial.SetShaderParameter("color", Palette.Highlight);
        _discMaterial.SetShaderParameter("disc_mode", 1f);
        _discMaterial.SetShaderParameter("depth_pull", 0.12f);

        _outline = NewMesh("Outline", _outlineMaterial);
        _glow = NewMesh("Glow", _glowMaterial);
        _disc = NewMesh("Disc", _discMaterial);
    }

    /// <summary>
    /// Copies the stylised filter's pixel grid onto the brush's material, so
    /// its circles are drawn on the same pixels as the world. Without a
    /// filter, or with pixelation off, they are drawn on the screen's own.
    /// </summary>
    private void MatchPixelGrid()
    {
        Node scene = GetTree()?.CurrentScene;
        StylizedFilter filter = scene != null ? NodeSearch.FindByType<StylizedFilter>(scene) : null;

        bool pixelate = filter != null && filter.PixelateEnabled && filter.Visible;
        _discMaterial.SetShaderParameter("pixelate", pixelate);

        if (!pixelate)
            return;

        _discMaterial.SetShaderParameter("pixel_resolution", filter.PixelResolution);
        _discMaterial.SetShaderParameter("pixel_far_scale", filter.PixelFarScale);
        _discMaterial.SetShaderParameter("pixel_near_distance", filter.PixelNearDistance);
        _discMaterial.SetShaderParameter("pixel_far_distance", filter.PixelFarDistance);
        _discMaterial.SetShaderParameter("pixel_distance_steps", filter.PixelDistanceSteps);
    }

    private MeshInstance3D NewMesh(string name, Material material)
    {
        var mesh = new MeshInstance3D
        {
            Name = name,

            // Built in world space; the player's transform must not move it again.
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = material,
            Visible = false,
        };

        AddChild(mesh);
        return mesh;
    }

    /// <summary>Draws the tool's highlight on a target, rebuilding only if it changed.</summary>
    public void Show(ITool tool, in ToolContext context, in NodeHit target)
    {
        // Rebuilt for a new target or mode, and also once the eye has moved far
        // enough that the outline's width -- sized by distance -- would be off.
        bool eyeMoved = context.Eye.DistanceTo(_eye) > 0.1f * context.Eye.DistanceTo(target.Point);

        if (Showing && tool == _tool && context.Mode == _mode && tool.SameOutline(_target, target) && !eyeMoved)
            return;

        _tool = tool;
        _target = target;
        _eye = context.Eye;
        _mode = context.Mode;

        _builder.Clear();
        _builder.Eye = context.Eye;
        tool.Outline(context, target, _builder);

        _outline.Mesh = _builder.BuildOutline();
        _glow.Mesh = _builder.BuildGlow();
        _disc.Mesh = _builder.BuildDisc();

        if (_disc.Mesh != null)
        {
            MatchPixelGrid();
            _discMaterial.SetShaderParameter("disc_radius", _builder.DiscRadius);
            _discMaterial.SetShaderParameter("disc_inner", _builder.DiscInner);
            _discMaterial.SetShaderParameter("show_inner", _builder.ShowInner ? 1f : 0f);
        }

        _outline.Visible = _outline.Mesh != null;
        _glow.Visible = _glow.Mesh != null;
        _disc.Visible = _disc.Mesh != null;
        Showing = true;
    }

    /// <summary>Takes the highlight off screen.</summary>
    public void Clear()
    {
        if (!Showing)
            return;

        Showing = false;
        _tool = null;
        _outline.Visible = false;
        _glow.Visible = false;
        _disc.Visible = false;
    }

    /// <summary>The brush skin the highlight is drawing, or null. For tests and tools.</summary>
    public ArrayMesh DiscMesh => _disc?.Visible == true ? _disc.Mesh as ArrayMesh : null;
}
