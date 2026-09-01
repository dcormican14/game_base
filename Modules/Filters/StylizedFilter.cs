using Godot;
using GameBase.Core;

namespace GameBase.Filters;

/// <summary>
/// Drop-in screen-space stylization: outlines, pixelation and ordered dither.
/// Instance StylizedFilter.tscn anywhere in a 3D scene — it renders a
/// full-screen pass after everything else and needs no other wiring.
///
/// Depth and normal buffers are only readable from spatial shaders, so this is
/// a MeshInstance3D whose shader forces its quad to cover the viewport, rather
/// than a CanvasLayer/ColorRect.
///
/// When SettingsService is present the per-effect toggles follow the user's
/// saved settings; the exports below act as defaults and as the values used
/// when it is absent.
/// </summary>
public partial class StylizedFilter : MeshInstance3D
{
    [ExportGroup("Outlines")]
    [Export] public bool OutlineEnabled { get; set; } = true;
    [Export] public Color OutlineColor { get; set; } = new(0.05f, 0.05f, 0.07f);
    [Export(PropertyHint.Range, "1,4,0.1")] public float OutlineThickness { get; set; } = 1f;
    [Export(PropertyHint.Range, "0.01,2,0.01")] public float DepthThreshold { get; set; } = 0.18f;
    /// <summary>Cap on grazing-angle compensation: how much the depth-edge
    /// threshold may rise on surfaces seen edge-on (distant floor), which
    /// otherwise read as one giant outline.</summary>
    [Export(PropertyHint.Range, "1,64,0.5")] public float GrazingBoost { get; set; } = 32f;
    /// <summary>Normal difference counted as a crease. 90-degree flat-face
    /// creases measure 1.41; smooth limb/torso curvature stays at or below ~1.1.</summary>
    [Export(PropertyHint.Range, "0.1,1.5,0.01")] public float NormalThreshold { get; set; } = 1.1f;
    /// <summary>Outlines are full-strength ink up to this distance, in meters.</summary>
    [Export] public float OutlineFullDistance { get; set; } = 12f;
    /// <summary>Beyond OutlineFullDistance lines thin out steadily, vanishing here. 0 disables.</summary>
    [Export] public float OutlineMaxDistance { get; set; } = 60f;
    /// <summary>Interior (crease) lines fade out much earlier than silhouettes,
    /// so line clusters on distant geometry cannot merge into dark masses.</summary>
    [Export] public float NormalMaxDistance { get; set; } = 35f;

    [ExportGroup("Pixelation")]
    [Export] public bool PixelateEnabled { get; set; } = true;
    /// <summary>Virtual vertical resolution; width follows the screen aspect.</summary>
    [Export(PropertyHint.Range, "60,1080,1")] public int PixelResolution { get; set; } = 320;

    [ExportGroup("Dither")]
    [Export] public bool DitherEnabled { get; set; }
    [Export(PropertyHint.Range, "0,0.1,0.001")] public float DitherStrength { get; set; } = 0.02f;

    private ShaderMaterial _material;

    public override void _Ready()
    {
        _material = GetActiveMaterial(0) as ShaderMaterial;
        if (_material == null)
        {
            GD.PushWarning("StylizedFilter: no ShaderMaterial on the quad — filter disabled.");
            return;
        }

        // Own the material so several instances (or scene reloads) never share
        // one set of uniforms.
        _material = (ShaderMaterial)_material.Duplicate();
        MaterialOverride = _material;

        if (SettingsService.Instance != null)
            SettingsService.Instance.SettingsChanged += Apply;

        Apply();
    }

    public override void _ExitTree()
    {
        if (SettingsService.Instance != null)
            SettingsService.Instance.SettingsChanged -= Apply;
    }

    /// <summary>Pushes the current settings (or the exported defaults) to the shader.</summary>
    public void Apply()
    {
        if (_material == null)
            return;

        var settings = SettingsService.Instance;
        bool outline = settings?.OutlineFilter ?? OutlineEnabled;
        bool pixelate = settings?.PixelateFilter ?? PixelateEnabled;
        bool dither = settings?.DitherFilter ?? DitherEnabled;

        _material.SetShaderParameter("outline_enabled", outline);
        _material.SetShaderParameter("outline_color", OutlineColor);
        _material.SetShaderParameter("outline_thickness", OutlineThickness);
        _material.SetShaderParameter("depth_threshold", DepthThreshold);
        _material.SetShaderParameter("grazing_boost", GrazingBoost);
        _material.SetShaderParameter("normal_threshold", NormalThreshold);
        _material.SetShaderParameter("outline_full_distance", OutlineFullDistance);
        _material.SetShaderParameter("outline_max_distance", OutlineMaxDistance);
        _material.SetShaderParameter("normal_max_distance", NormalMaxDistance);

        _material.SetShaderParameter("pixelate_enabled", pixelate);
        _material.SetShaderParameter("pixel_resolution", PixelResolution);

        _material.SetShaderParameter("dither_enabled", dither);
        _material.SetShaderParameter("dither_strength", DitherStrength);

        // The pass is pure cost when every effect is off.
        Visible = outline || pixelate || dither;
    }
}
