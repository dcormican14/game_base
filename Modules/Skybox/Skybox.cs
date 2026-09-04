using Godot;

namespace GameBase.World;

/// <summary>
/// Drop-in space skybox. Instance Skybox.tscn into a 3D scene and it takes over
/// the background, replacing whatever WorldEnvironment is already there — no
/// other wiring needed.
///
/// Three sources, chosen with <see cref="Source"/>:
///
///  - <b>Procedural</b> (default): the placeholder <c>SpaceSky.gdshader</c>,
///    art-directed from the exports below.
///  - <b>Panorama</b>: your own equirectangular image in <see cref="PanoramaTexture"/>.
///    This is the simplest route for hand-painted art.
///  - <b>CustomShader</b>: your own <c>shader_type sky</c> shader in
///    <see cref="CustomSkyShader"/>, for art that needs to be generated.
///
/// See README.md ("Painting your own skybox") for the art pipeline.
/// </summary>
[Tool]
public partial class Skybox : Node
{
    public enum SkySource
    {
        /// <summary>Built-in placeholder: procedural stars and nebulae.</summary>
        Procedural,
        /// <summary>A single equirectangular (2:1) texture. Best for hand-painted art.</summary>
        Panorama,
        /// <summary>Your own sky shader.</summary>
        CustomShader,
    }

    private SkySource _source = SkySource.Procedural;
    /// <summary>Where the sky's pixels come from.</summary>
    [Export]
    public SkySource Source
    {
        get => _source;
        set { _source = value; Rebuild(); NotifyPropertyListChanged(); }
    }

    private Texture2D _panoramaTexture;
    /// <summary>Equirectangular sky image, 2:1 aspect. Used when Source is Panorama.</summary>
    [Export]
    public Texture2D PanoramaTexture
    {
        get => _panoramaTexture;
        set { _panoramaTexture = value; Rebuild(); }
    }

    private bool _panoramaFilter = true;
    /// <summary>
    /// Smooth the panorama texture when it is magnified. Leave on for painted
    /// or photographic art; turn OFF for pixel art, which filtering blurs into
    /// mush. Only applies when Source is Panorama.
    /// </summary>
    [Export]
    public bool PanoramaFilter
    {
        get => _panoramaFilter;
        set { _panoramaFilter = value; Rebuild(); }
    }

    private Shader _customSkyShader;
    /// <summary>A <c>shader_type sky</c> shader. Used when Source is CustomShader.</summary>
    [Export]
    public Shader CustomSkyShader
    {
        get => _customSkyShader;
        set { _customSkyShader = value; Rebuild(); }
    }

    private float _skyEnergy = 1f;
    /// <summary>Overall brightness of the background.</summary>
    [Export(PropertyHint.Range, "0,8,0.05")]
    public float SkyEnergy
    {
        get => _skyEnergy;
        set { _skyEnergy = value; Rebuild(); }
    }

    /// <summary>
    /// How much the sky lights the scene. Space is dark, so this is low by
    /// default — raise it to let nebula colour spill onto your geometry.
    /// </summary>
    private float _ambientEnergy = 0.35f;
    [Export(PropertyHint.Range, "0,4,0.05")]
    public float AmbientEnergy
    {
        get => _ambientEnergy;
        set { _ambientEnergy = value; Rebuild(); }
    }

    // The three layers the sky shader composites, in order: space, then
    // nebulae inside it, then stars in front. Each group maps to the matching
    // block of uniforms in SpaceSky.gdshader.

    [ExportGroup("Pixel Grid")]
    private float _pixelGrid = 300f;
    /// <summary>
    /// Cells across a cube face - the art's "pixel" size. Every layer samples
    /// at cell centres, so colour is flat across a cell and edges land on cell
    /// boundaries. Lower means chunkier pixels.
    /// </summary>
    [Export(PropertyHint.Range, "64,1024,1")]
    public float PixelGrid
    {
        get => _pixelGrid;
        set { _pixelGrid = value; PushProceduralParameters(); }
    }

    [ExportGroup("Layer 1 - Space")]
    // The reference background #28061e halved toward black (#14030f).
    // Calibrated against a render, not computed: the environment's Filmic
    // tonemapper crushes darks so hard that the naive linear value lands on
    // screen at about #050104. Re-measure if the tonemapper, SkyEnergy or
    // AmbientEnergy change.
    private Color _spaceColor = new(0.0747f, 0.0092f, 0.0521f);
    [Export]
    public Color SpaceColor
    {
        get => _spaceColor;
        set { _spaceColor = value; PushProceduralParameters(); }
    }

    [ExportGroup("Layer 2 - Nebulae")]
    private bool _nebulaEnabled = true;
    [Export]
    public bool NebulaEnabled
    {
        get => _nebulaEnabled;
        set { _nebulaEnabled = value; PushProceduralParameters(); }
    }

    // Three FLAT tones sampled from the reference (#300c2a, #3c1230, #4e1836).
    // The nebula is banded like a topographic map, not a gradient, so these
    // are stepped between rather than interpolated.
    private Color _nebulaTone1 = new(0.115f, 0.020f, 0.093f);
    [Export]
    public Color NebulaTone1
    {
        get => _nebulaTone1;
        set { _nebulaTone1 = value; PushProceduralParameters(); }
    }

    private Color _nebulaTone2 = new(0.180f, 0.035f, 0.130f);
    [Export]
    public Color NebulaTone2
    {
        get => _nebulaTone2;
        set { _nebulaTone2 = value; PushProceduralParameters(); }
    }

    private Color _nebulaTone3 = new(0.320f, 0.060f, 0.180f);
    [Export]
    public Color NebulaTone3
    {
        get => _nebulaTone3;
        set { _nebulaTone3 = value; PushProceduralParameters(); }
    }

    private Color _nebulaWarm = new(0.300f, 0.085f, 0.090f);
    /// <summary>Warm accent taken by a minority of clouds.</summary>
    [Export]
    public Color NebulaWarm
    {
        get => _nebulaWarm;
        set { _nebulaWarm = value; PushProceduralParameters(); }
    }

    private float _nebulaWarmShare = 0.06f;
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float NebulaWarmShare
    {
        get => _nebulaWarmShare;
        set { _nebulaWarmShare = value; PushProceduralParameters(); }
    }

    private float _nebulaOpacity = 1f;
    [Export(PropertyHint.Range, "0,2,0.01")]
    public float NebulaOpacity
    {
        get => _nebulaOpacity;
        set { _nebulaOpacity = value; PushProceduralParameters(); }
    }

    private float _nebulaScale = 4.5f;
    [Export(PropertyHint.Range, "0.2,12,0.1")]
    public float NebulaScale
    {
        get => _nebulaScale;
        set { _nebulaScale = value; PushProceduralParameters(); }
    }

    private float _nebulaFloor = 0.50f;
    /// <summary>Density below this is empty sky. A threshold rather than a
    /// power curve, because pow() never reaches zero and so washes the whole
    /// dome instead of leaving gaps between clouds.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float NebulaFloor
    {
        get => _nebulaFloor;
        set { _nebulaFloor = value; PushProceduralParameters(); }
    }

    private float _nebulaSpan = 0.26f;
    /// <summary>Density range over which a cloud climbs through its three
    /// tone bands.</summary>
    [Export(PropertyHint.Range, "0.05,0.6,0.01")]
    public float NebulaSpan
    {
        get => _nebulaSpan;
        set { _nebulaSpan = value; PushProceduralParameters(); }
    }

    private float _nebulaSpin = 0.012f;
    /// <summary>Radians per second about the hurricane axis. At the default a
    /// full turn takes about nine minutes.</summary>
    [Export(PropertyHint.Range, "0,0.05,0.001")]
    public float NebulaSpin
    {
        get => _nebulaSpin;
        set { _nebulaSpin = value; PushProceduralParameters(); }
    }

    private Vector3 _nebulaAxis = Vector3.Up;
    /// <summary>The eye of the hurricane: clouds circle this direction.</summary>
    [Export]
    public Vector3 NebulaAxis
    {
        get => _nebulaAxis;
        set { _nebulaAxis = value; PushProceduralParameters(); }
    }

    private float _nebulaMorph = 0.02f;
    /// <summary>How fast clouds change shape as they travel, independent of
    /// the spin. Zero makes them rigid stamps sliding past.</summary>
    [Export(PropertyHint.Range, "0,0.2,0.001")]
    public float NebulaMorph
    {
        get => _nebulaMorph;
        set { _nebulaMorph = value; PushProceduralParameters(); }
    }

    [ExportGroup("Layer 3 - Stars")]
    private int _starRings = 64;
    /// <summary>
    /// Latitude rings the star sphere is cut into. Each ring holds cells
    /// proportional to its circumference, so cells stay square from pole to
    /// pole and there is no face, corner or seam anywhere. More rings means
    /// more and finer stars; total cells is roughly rings^2 * 4/pi.
    /// </summary>
    [Export(PropertyHint.Range, "8,160,1")]
    public int StarRings
    {
        get => _starRings;
        set { _starRings = value; PushProceduralParameters(); }
    }

    private float _starCoverage = 0.34f;
    /// <summary>Fraction of cells holding a star - the main density control.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float StarCoverage
    {
        get => _starCoverage;
        set { _starCoverage = value; PushProceduralParameters(); }
    }

    // Three tiers measured from the reference art, which runs a ladder rather
    // than one colour scaled: #fee1ea cores, #ba6976 mid, #8c445c faint. The
    // plum shift gets STRONGER as stars dim, which is what ties the field to
    // the background instead of dusting it with white specks. Pre-tonemap.
    private Color _starCore = new(1.05f, 0.62f, 0.74f);
    /// <summary>Brightest tier: off-white with a pink cast.</summary>
    [Export]
    public Color StarCore
    {
        get => _starCore;
        set { _starCore = value; PushProceduralParameters(); }
    }

    private Color _starMid = new(0.400f, 0.175f, 0.235f);
    /// <summary>Middle tier: clearly rosy.</summary>
    [Export]
    public Color StarMid
    {
        get => _starMid;
        set { _starMid = value; PushProceduralParameters(); }
    }

    private Color _starFaint = new(0.150f, 0.062f, 0.098f);
    /// <summary>Faintest dots and arm tips: deep plum-rose, close to the
    /// background so the field sits in the same palette.</summary>
    [Export]
    public Color StarFaint
    {
        get => _starFaint;
        set { _starFaint = value; PushProceduralParameters(); }
    }

    private float _starBrightness = 1f;
    [Export(PropertyHint.Range, "0,4,0.05")]
    public float StarBrightness
    {
        get => _starBrightness;
        set { _starBrightness = value; PushProceduralParameters(); }
    }

    private float _starLargeShare = 0.012f;
    /// <summary>Share drawn large: a 3x3 core with all eight arms.</summary>
    [Export(PropertyHint.Range, "0,0.3,0.005")]
    public float StarLargeShare
    {
        get => _starLargeShare;
        set { _starLargeShare = value; PushProceduralParameters(); }
    }

    private float _starMediumShare = 0.05f;
    /// <summary>Share drawn medium: one pixel plus four arms, either all
    /// straight or all diagonal. The rest are single dots.</summary>
    [Export(PropertyHint.Range, "0,0.6,0.005")]
    public float StarMediumShare
    {
        get => _starMediumShare;
        set { _starMediumShare = value; PushProceduralParameters(); }
    }

    private float _twinkleAxisPixels = 22f;
    /// <summary>Axis arm length in PIXELS. One pixel wide and long is the
    /// character of the reference art.</summary>
    [Export(PropertyHint.Range, "2,128,1")]
    public float TwinkleAxisPixels
    {
        get => _twinkleAxisPixels;
        set { _twinkleAxisPixels = value; PushProceduralParameters(); }
    }

    private float _twinkleDiagonalPixels = 9f;
    /// <summary>Diagonal arm length in pixels. Shorter than the axis arms and
    /// drawn as a dashed run of single pixels, as in the reference.</summary>
    [Export(PropertyHint.Range, "1,64,1")]
    public float TwinkleDiagonalPixels
    {
        get => _twinkleDiagonalPixels;
        set { _twinkleDiagonalPixels = value; PushProceduralParameters(); }
    }

    private float _starSpin = 0.004f;
    /// <summary>Radians per second the star field rotates, about the same axis
    /// as the nebulae. Roughly a third of the gas speed, so the sky drifts as
    /// one but the stars clearly lag the clouds.</summary>
    [Export(PropertyHint.Range, "0,0.05,0.0005")]
    public float StarSpin
    {
        get => _starSpin;
        set { _starSpin = value; PushProceduralParameters(); }
    }

    [ExportGroup("Shooting Stars")]
    private bool _shootingEnabled = true;
    [Export]
    public bool ShootingEnabled
    {
        get => _shootingEnabled;
        set { _shootingEnabled = value; PushProceduralParameters(); }
    }

    private float _shootingPeriod = 3.5f;
    /// <summary>Seconds per slot. Several slots are tested each frame so shots
    /// can overlap and cluster like real meteors; rarity comes from
    /// <see cref="ShootingChance"/>, not from the slot spacing.</summary>
    [Export(PropertyHint.Range, "0.5,30,0.1")]
    public float ShootingPeriod
    {
        get => _shootingPeriod;
        set { _shootingPeriod = value; PushProceduralParameters(); }
    }

    private float _shootingChance = 0.07f;
    /// <summary>Odds a slot holds a shot at all - the rarity dial.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ShootingChance
    {
        get => _shootingChance;
        set { _shootingChance = value; PushProceduralParameters(); }
    }

    private float _shootingDuration = 0.16f;
    /// <summary>Seconds a shot takes to cross its arc. A blink: real meteors
    /// are gone in a fraction of a second, and anything slower reads as a
    /// drifting object rather than a meteor.</summary>
    [Export(PropertyHint.Range, "0.03,2,0.01")]
    public float ShootingDuration
    {
        get => _shootingDuration;
        set { _shootingDuration = value; PushProceduralParameters(); }
    }

    private float _shootingPixels = 18f;
    /// <summary>Trail length in pixels, measured back from the head.</summary>
    [Export(PropertyHint.Range, "4,128,1")]
    public float ShootingPixels
    {
        get => _shootingPixels;
        set { _shootingPixels = value; PushProceduralParameters(); }
    }

    private float _shootingArc = 0.13f;
    /// <summary>How far across the sky a shot travels, in radians. Small: a
    /// meteor crosses a patch of sky, not the dome.</summary>
    [Export(PropertyHint.Range, "0.02,1,0.01")]
    public float ShootingArc
    {
        get => _shootingArc;
        set { _shootingArc = value; PushProceduralParameters(); }
    }

    private int _shootingSlots = 4;
    /// <summary>How many recent slots are tested per frame. Higher lets more
    /// shots overlap, at a cost of that many extra hashes per pixel.</summary>
    [Export(PropertyHint.Range, "1,8,1")]
    public int ShootingSlots
    {
        get => _shootingSlots;
        set { _shootingSlots = value; PushProceduralParameters(); }
    }

    private WorldEnvironment _worldEnvironment;
    private Godot.Environment _environment;
    private Sky _sky;
    private ShaderMaterial _proceduralMaterial;

    public override void _Ready()
    {
        Rebuild();
    }

    public override void _ExitTree()
    {
        // Only tear down the node this module created; a WorldEnvironment that
        // was already in the scene is left exactly as it was found.
        if (_worldEnvironment != null && _worldEnvironment.GetParent() == this)
        {
            _worldEnvironment.QueueFree();
            _worldEnvironment = null;
        }
    }

    /// <summary>Rebuilds the environment and re-applies the chosen sky source.</summary>
    public void Rebuild()
    {
        if (!IsInsideTree())
            return;

        EnsureEnvironment();

        Material material = Source switch
        {
            SkySource.Panorama => BuildPanoramaMaterial(),
            SkySource.CustomShader => BuildCustomMaterial(),
            _ => BuildProceduralMaterial(),
        };

        // Fall back to the placeholder rather than rendering nothing when the
        // chosen source has not been given its texture or shader yet.
        if (material == null)
        {
            if (Source != SkySource.Procedural)
            {
                GD.PushWarning(
                    $"Skybox: Source is {Source} but no {(Source == SkySource.Panorama ? "PanoramaTexture" : "CustomSkyShader")} " +
                    "is set — falling back to the procedural placeholder.");
            }
            material = BuildProceduralMaterial();
        }

        _sky.SkyMaterial = material;
        _environment.BackgroundEnergyMultiplier = SkyEnergy;
        _environment.AmbientLightEnergy = AmbientEnergy;
    }

    /// <summary>Creates the WorldEnvironment this module drives, once.</summary>
    private void EnsureEnvironment()
    {
        if (_worldEnvironment != null && IsInstanceValid(_worldEnvironment))
            return;

        _sky = new Sky
        {
            // Realtime uses the fast filtering path for the radiance map.
            // Quality's importance sampling is dramatically more expensive and
            // buys nothing here: the radiance map only drives ambient light and
            // reflections, and this sky is a smooth nebula gradient whose
            // low-frequency ambient contribution the fast filter reproduces
            // just as well.
            ProcessMode = Sky.ProcessModeEnum.Realtime,
            // The fast filter is limited to 256x256 cubemaps; anything else is
            // ignored with a warning.
            RadianceSize = Sky.RadianceSizeEnum.Size256,
        };

        _environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = _sky,
            // Light the scene from the sky itself: in space there is no
            // bounce light from ground, so the nebulae are the ambient source.
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightSkyContribution = 1f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };

        _worldEnvironment = new WorldEnvironment
        {
            Name = "SkyboxEnvironment",
            Environment = _environment,
        };
        AddChild(_worldEnvironment);
    }

    private Material BuildProceduralMaterial()
    {
        var shader = GD.Load<Shader>("res://Modules/Skybox/SpaceSky.gdshader");
        if (shader == null)
        {
            GD.PushWarning("Skybox: SpaceSky.gdshader missing — sky will render black.");
            return null;
        }

        _proceduralMaterial = new ShaderMaterial { Shader = shader };
        PushProceduralParameters();
        return _proceduralMaterial;
    }

    private Material BuildPanoramaMaterial()
    {
        if (PanoramaTexture == null)
            return null;

        return new PanoramaSkyMaterial
        {
            Panorama = PanoramaTexture,
            Filter = PanoramaFilter,
        };
    }

    private Material BuildCustomMaterial()
    {
        if (CustomSkyShader == null)
            return null;

        return new ShaderMaterial { Shader = CustomSkyShader };
    }

    /// <summary>Pushes the inspector values onto the placeholder shader.</summary>
    private void PushProceduralParameters()
    {
        if (_proceduralMaterial == null || Source != SkySource.Procedural)
            return;

        _proceduralMaterial.SetShaderParameter("pixel_grid", PixelGrid);

        // Layer 1 - space.
        _proceduralMaterial.SetShaderParameter("space_color", SpaceColor);

        // Layer 2 - nebulae.
        _proceduralMaterial.SetShaderParameter("nebula_enabled", NebulaEnabled);
        _proceduralMaterial.SetShaderParameter("nebula_tone_1", NebulaTone1);
        _proceduralMaterial.SetShaderParameter("nebula_tone_2", NebulaTone2);
        _proceduralMaterial.SetShaderParameter("nebula_tone_3", NebulaTone3);
        _proceduralMaterial.SetShaderParameter("nebula_warm", NebulaWarm);
        _proceduralMaterial.SetShaderParameter("nebula_warm_share", NebulaWarmShare);
        _proceduralMaterial.SetShaderParameter("nebula_opacity", NebulaOpacity);
        _proceduralMaterial.SetShaderParameter("nebula_scale", NebulaScale);
        _proceduralMaterial.SetShaderParameter("nebula_floor", NebulaFloor);
        _proceduralMaterial.SetShaderParameter("nebula_span", NebulaSpan);
        _proceduralMaterial.SetShaderParameter("nebula_spin", NebulaSpin);
        // Normalized here rather than in the shader, which would pay for it
        // once per pixel; a zero vector would make the rotation undefined.
        _proceduralMaterial.SetShaderParameter("nebula_axis",
            NebulaAxis.LengthSquared() > 0.0001f ? NebulaAxis.Normalized() : Vector3.Up);
        _proceduralMaterial.SetShaderParameter("nebula_morph", NebulaMorph);

        // Layer 3 - stars.
        _proceduralMaterial.SetShaderParameter("star_rings", StarRings);
        _proceduralMaterial.SetShaderParameter("star_coverage", StarCoverage);
        _proceduralMaterial.SetShaderParameter("star_core", StarCore);
        _proceduralMaterial.SetShaderParameter("star_mid", StarMid);
        _proceduralMaterial.SetShaderParameter("star_faint", StarFaint);
        _proceduralMaterial.SetShaderParameter("star_brightness", StarBrightness);
        _proceduralMaterial.SetShaderParameter("star_large_share", StarLargeShare);
        _proceduralMaterial.SetShaderParameter("star_medium_share", StarMediumShare);
        _proceduralMaterial.SetShaderParameter("twinkle_axis_pixels", TwinkleAxisPixels);
        _proceduralMaterial.SetShaderParameter("twinkle_diagonal_pixels", TwinkleDiagonalPixels);
        _proceduralMaterial.SetShaderParameter("star_spin", StarSpin);

        _proceduralMaterial.SetShaderParameter("shooting_enabled", ShootingEnabled);
        _proceduralMaterial.SetShaderParameter("shooting_period", ShootingPeriod);
        _proceduralMaterial.SetShaderParameter("shooting_chance", ShootingChance);
        _proceduralMaterial.SetShaderParameter("shooting_duration", ShootingDuration);
        _proceduralMaterial.SetShaderParameter("shooting_pixels", ShootingPixels);
        _proceduralMaterial.SetShaderParameter("shooting_arc", ShootingArc);
        _proceduralMaterial.SetShaderParameter("shooting_slots", ShootingSlots);
    }

    /// <summary>Hides the exports that do not apply to the chosen source.</summary>
    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        string name = property["name"].AsStringName();

        // Matched by PREFIX rather than by listing every export: the
        // procedural look has a few dozen dials across four groups, and a
        // hand-written list silently rots every time one is added or renamed.
        bool procedural = name.StartsWith("Space") || name.StartsWith("Nebula")
            || name.StartsWith("Star") || name.StartsWith("Twinkle")
            || name.StartsWith("Shooting") || name == nameof(PixelGrid);

        bool hide = name switch
        {
            nameof(PanoramaTexture) or nameof(PanoramaFilter) => Source != SkySource.Panorama,
            nameof(CustomSkyShader) => Source != SkySource.CustomShader,
            _ when procedural => Source != SkySource.Procedural,
            _ => false,
        };

        if (hide)
        {
            property["usage"] = (int)PropertyUsageFlags.NoEditor;
        }
    }
}
