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

    [ExportGroup("Placeholder Look")]
    private Color _spaceColor = new(0.008f, 0.010f, 0.028f);
    [Export]
    public Color SpaceColor
    {
        get => _spaceColor;
        set { _spaceColor = value; PushProceduralParameters(); }
    }

    private float _starDensity = 70f;
    /// <summary>Cells across the sky; higher means more, smaller stars.
    /// Raising this far past the default brings back the flicker the
    /// pixelation filter causes on sub-virtual-pixel stars.</summary>
    [Export(PropertyHint.Range, "20,400,1")]
    public float StarDensity
    {
        get => _starDensity;
        set { _starDensity = value; PushProceduralParameters(); }
    }

    private float _starCoverage = 0.045f;
    /// <summary>Fraction of cells holding a star — the main density control.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.005")]
    public float StarCoverage
    {
        get => _starCoverage;
        set { _starCoverage = value; PushProceduralParameters(); }
    }

    private float _starBrightness = 3.4f;
    [Export(PropertyHint.Range, "0,8,0.05")]
    public float StarBrightness
    {
        get => _starBrightness;
        set { _starBrightness = value; PushProceduralParameters(); }
    }

    private bool _nebulaEnabled = true;
    [Export]
    public bool NebulaEnabled
    {
        get => _nebulaEnabled;
        set { _nebulaEnabled = value; PushProceduralParameters(); }
    }

    private Color _nebulaColorA = new(0.35f, 0.12f, 0.55f);
    [Export]
    public Color NebulaColorA
    {
        get => _nebulaColorA;
        set { _nebulaColorA = value; PushProceduralParameters(); }
    }

    private Color _nebulaColorB = new(0.05f, 0.30f, 0.55f);
    [Export]
    public Color NebulaColorB
    {
        get => _nebulaColorB;
        set { _nebulaColorB = value; PushProceduralParameters(); }
    }

    private float _nebulaIntensity = 0.85f;
    [Export(PropertyHint.Range, "0,3,0.05")]
    public float NebulaIntensity
    {
        get => _nebulaIntensity;
        set { _nebulaIntensity = value; PushProceduralParameters(); }
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
            // Nearest keeps hand-drawn pixel art crisp; switch to true for
            // painted or photographic panoramas.
            Filter = true,
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

        _proceduralMaterial.SetShaderParameter("space_color", SpaceColor);
        _proceduralMaterial.SetShaderParameter("star_density", StarDensity);
        _proceduralMaterial.SetShaderParameter("star_coverage", StarCoverage);
        _proceduralMaterial.SetShaderParameter("star_brightness", StarBrightness);
        _proceduralMaterial.SetShaderParameter("nebula_enabled", NebulaEnabled);
        _proceduralMaterial.SetShaderParameter("nebula_color_a", NebulaColorA);
        _proceduralMaterial.SetShaderParameter("nebula_color_b", NebulaColorB);
        _proceduralMaterial.SetShaderParameter("nebula_intensity", NebulaIntensity);
    }

    /// <summary>Hides the exports that do not apply to the chosen source.</summary>
    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        string name = property["name"].AsStringName();

        bool hide = name switch
        {
            nameof(PanoramaTexture) => Source != SkySource.Panorama,
            nameof(CustomSkyShader) => Source != SkySource.CustomShader,
            nameof(SpaceColor) or nameof(StarDensity) or nameof(StarCoverage) or nameof(StarBrightness)
                or nameof(NebulaEnabled) or nameof(NebulaColorA) or nameof(NebulaColorB)
                or nameof(NebulaIntensity)
                => Source != SkySource.Procedural,
            _ => false,
        };

        if (hide)
        {
            property["usage"] = (int)PropertyUsageFlags.NoEditor;
        }
    }
}
