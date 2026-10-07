using Godot;
using GameBase.Core;
using GameBase.World;

namespace GameBase.Lighting;

/// <summary>
/// The scene's light, as one drop-in piece: moonlight and the ambient that
/// fills its shadows. Instance LightingRig.tscn into a level beside a
/// <see cref="Skybox"/> and it lights the level from the skybox's moon -- no
/// other wiring needed.
///
/// MOONLIGHT is the key light: a warm, off-white gold directional light cast
/// from exactly where the moon hangs (<see cref="Skybox.MoonDirection"/>), so
/// the light and the moon can never disagree. Move the moon and the light
/// follows. With no skybox, it comes from <see cref="FallbackDirection"/>.
///
/// AMBIENT is what a face out of the moonlight receives, so it is what a
/// shadow looks like. It is plum, the colour of the night sky around the
/// moon, leaned a little toward the moon's warmth (<see cref="AmbientWarmth"/>):
/// shadows stay the slightly plum shadows of the backdrop while the whole
/// scene sits in warm light.
///
/// WITH A <see cref="DayCycle"/> in the scene, the rig follows it every frame:
/// SUNLIGHT comes from the cycle's sun in the sun's own colour (peach low,
/// cream high), the moonlight from the moon opposite, each fading as the
/// day's light comes and goes and as it sinks below the local horizon. Only
/// the brighter of the two casts shadows. The ambient moves from the night's
/// plum toward the day sky's own colour.
/// </summary>
[Tool]
public partial class LightingRig : Node3D
{
    /// <summary>The skybox whose moon lights the scene. Defaults to the first one found in the scene.</summary>
    [Export] public NodePath SkyboxPath { get; set; } = "";

    // ------------------------------------------------------------ moonlight

    private Color _moonColor = new(1.0f, 0.93f, 0.82f);
    [ExportGroup("Moonlight")]
    /// <summary>The moonlight's colour: warm off-white gold.</summary>
    [Export]
    public Color MoonColor
    {
        get => _moonColor;
        set { _moonColor = value; Apply(); }
    }

    private float _moonEnergy = 1.8f;
    [Export(PropertyHint.Range, "0,8,0.05")]
    public float MoonEnergy
    {
        get => _moonEnergy;
        set { _moonEnergy = value; Apply(); }
    }

    private float _shadowOpacity = 0.6f;
    /// <summary>How dark shadows fall: below 1 lets the ambient's plum through.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ShadowOpacity
    {
        get => _shadowOpacity;
        set { _shadowOpacity = value; Apply(); }
    }

    private float _shadowDistance = 120f;
    /// <summary>How far from the camera shadows are drawn.</summary>
    [Export(PropertyHint.Range, "10,1000,5")]
    public float ShadowDistance
    {
        get => _shadowDistance;
        set { _shadowDistance = value; Apply(); }
    }

    private Vector3 _fallbackDirection = new Vector3(0.3f, 0.57f, -0.76f).Normalized();
    /// <summary>Where the light comes from when there is no skybox to take a moon from.</summary>
    [Export]
    public Vector3 FallbackDirection
    {
        get => _fallbackDirection;
        set { _fallbackDirection = value; Apply(); }
    }

    // ------------------------------------------------------------ sunlight

    [ExportGroup("Sunlight")]
    /// <summary>The sunlight's strength at full day (its colour follows the sun's).</summary>
    [Export(PropertyHint.Range, "0,8,0.05")] public float SunEnergy { get; set; } = 1.15f;

    /// <summary>The ambient's strength at full day.</summary>
    [Export(PropertyHint.Range, "0,4,0.05")] public float DayAmbientEnergy { get; set; } = 0.6f;

    // -------------------------------------------------------------- ambient

    private Color _ambientColor = new(0.300f, 0.085f, 0.245f);
    [ExportGroup("Ambient")]
    /// <summary>
    /// The colour of the light that fills shadow: the backdrop's plum. Brighter
    /// and more saturated than the sky itself on purpose -- the Filmic
    /// tonemapper crushes darks hard, and a shadow lit by the nearly black sky
    /// alone reads as crushed rather than coloured.
    /// </summary>
    [Export]
    public Color AmbientColor
    {
        get => _ambientColor;
        set { _ambientColor = value; Apply(); }
    }

    private float _ambientWarmth = 0.25f;
    /// <summary>How far the ambient leans from plum toward the moonlight's warm colour.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float AmbientWarmth
    {
        get => _ambientWarmth;
        set { _ambientWarmth = value; Apply(); }
    }

    private float _ambientEnergy = 0.85f;
    [Export(PropertyHint.Range, "0,4,0.05")]
    public float AmbientEnergy
    {
        get => _ambientEnergy;
        set { _ambientEnergy = value; Apply(); }
    }

    private float _skyAmbientShare = 0.2f;
    /// <summary>
    /// How much of the ambient comes from the sky itself rather than
    /// <see cref="AmbientColor"/>. A little keeps the sky spilling onto the
    /// faces turned toward it.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SkyAmbientShare
    {
        get => _skyAmbientShare;
        set { _skyAmbientShare = value; Apply(); }
    }

    // ------------------------------------------------------------ lifecycle

    /// <summary>The moonlight itself, made by the rig.</summary>
    public DirectionalLight3D Moonlight { get; private set; }

    /// <summary>The sunlight, made by the rig; dark without a <see cref="DayCycle"/>.</summary>
    public DirectionalLight3D Sunlight { get; private set; }

    private Skybox _skybox;
    private DayCycle _cycle;

    public override void _Ready()
    {
        Moonlight = GetNodeOrNull<DirectionalLight3D>("Moonlight");

        if (Moonlight == null)
        {
            Moonlight = new DirectionalLight3D { Name = "Moonlight" };
            AddChild(Moonlight);
        }

        Sunlight = GetNodeOrNull<DirectionalLight3D>("Sunlight");
        if (Sunlight == null)
        {
            Sunlight = new DirectionalLight3D { Name = "Sunlight", LightEnergy = 0f, Visible = false };
            AddChild(Sunlight);
        }

        Node root = GetTree().EditedSceneRoot ?? GetTree().CurrentScene ?? GetParent();
        _skybox = !SkyboxPath.IsEmpty ? GetNodeOrNull<Skybox>(SkyboxPath) : null;
        _skybox ??= NodeSearch.FindByType<Skybox>(root);

        if (_skybox != null)
        {
            _skybox.EnvironmentReady += OnEnvironment;
            _skybox.MoonMoved += OnMoonMoved;
        }

        if (!Engine.IsEditorHint())
        {
            _cycle = NodeSearch.FindByType<DayCycle>(root);
            if (_cycle != null)
                _cycle.Updated += OnCycle;
        }

        Apply();
    }

    public override void _ExitTree()
    {
        if (_skybox != null)
        {
            _skybox.EnvironmentReady -= OnEnvironment;
            _skybox.MoonMoved -= OnMoonMoved;
            _skybox = null;
        }

        if (_cycle != null)
        {
            _cycle.Updated -= OnCycle;
            _cycle = null;
        }
    }

    private void OnEnvironment(Godot.Environment environment) => Apply();

    private void OnMoonMoved(Vector3 direction) => Apply();

    /// <summary>Where the moonlight comes from: the day cycle's moon, the skybox's, or the fallback.</summary>
    public Vector3 MoonDirection =>
        (_cycle != null && IsInstanceValid(_cycle) ? _cycle.MoonDirection
            : _skybox != null && IsInstanceValid(_skybox) ? _skybox.MoonDirection : FallbackDirection).Normalized();

    /// <summary>
    /// Follows the day: both lights' directions, colours and strengths, which
    /// of them casts shadows, and the ambient. Only what changes with the
    /// time of day is set here.
    /// </summary>
    private void OnCycle(DayCycle cycle)
    {
        if (Moonlight == null || Sunlight == null || !IsInsideTree())
            return;

        float daylight = cycle.Effects.Daylight;

        // Each light also fades out as it sinks below the local horizon, so a
        // cliff turned toward a sun already set is not lit by it.
        float sunUp = Above(cycle.SunElevation);
        float moonUp = Above(-cycle.SunElevation);

        float sun = SunEnergy * daylight * sunUp;
        float moon = MoonEnergy * (1f - daylight) * moonUp;

        Aim(Sunlight, cycle.SunDirection);
        Aim(Moonlight, cycle.MoonDirection);

        Sunlight.LightColor = cycle.SunColor;
        Sunlight.LightEnergy = sun;
        Sunlight.Visible = sun > 0.001f;
        Moonlight.LightEnergy = moon;
        Moonlight.Visible = moon > 0.001f;

        // Shadows from the brighter light only.
        bool sunShadows = sun >= moon;
        if (Sunlight.ShadowEnabled != sunShadows || Moonlight.ShadowEnabled == sunShadows)
        {
            Sunlight.ShadowEnabled = sunShadows;
            Moonlight.ShadowEnabled = !sunShadows;
        }

        Godot.Environment environment = _skybox?.Environment;
        if (environment != null)
        {
            Color night = AmbientColor.Lerp(MoonColor, AmbientWarmth);
            Color day = cycle.Zenith.Lerp(cycle.Horizon, 0.5f);
            environment.AmbientLightColor = night.Lerp(day, daylight);
            environment.AmbientLightEnergy = Mathf.Lerp(AmbientEnergy, DayAmbientEnergy, daylight);
        }
    }

    /// <summary>How much of a light is left at an elevation: gone a degree below the horizon, whole three above.</summary>
    private static float Above(float elevation) => SkyPalette.Smooth(-1f, 3f, elevation);

    /// <summary>Points a directional light so it shines from a direction.</summary>
    private static void Aim(DirectionalLight3D light, Vector3 from)
    {
        Vector3 travel = -from.Normalized();
        Vector3 hint = Mathf.Abs(travel.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        light.GlobalBasis = Basis.LookingAt(travel, hint);
    }

    /// <summary>Puts every setting on the light and the environment.</summary>
    public void Apply()
    {
        if (Moonlight == null || !IsInsideTree())
            return;

        // The light travels AWAY from the moon; a directional light shines
        // along its own -Z.
        Vector3 travel = -MoonDirection;
        Vector3 hint = Mathf.Abs(travel.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        Moonlight.GlobalBasis = Basis.LookingAt(travel, hint);

        Moonlight.LightColor = MoonColor;
        Moonlight.LightEnergy = MoonEnergy;
        Moonlight.LightSpecular = 0.2f;
        Moonlight.ShadowEnabled = true;
        Moonlight.ShadowOpacity = ShadowOpacity;
        Moonlight.ShadowBlur = 1f;
        Moonlight.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits;
        Moonlight.DirectionalShadowBlendSplits = true;
        Moonlight.DirectionalShadowMaxDistance = ShadowDistance;

        // The sunlight shadows as the moonlight does; the day cycle decides
        // which of the two casts them.
        Sunlight.LightSpecular = 0.3f;
        Sunlight.ShadowOpacity = ShadowOpacity;
        Sunlight.ShadowBlur = 1f;
        Sunlight.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits;
        Sunlight.DirectionalShadowBlendSplits = true;
        Sunlight.DirectionalShadowMaxDistance = ShadowDistance;

        Godot.Environment environment = _skybox?.Environment;

        if (environment != null)
        {
            environment.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
            environment.AmbientLightColor = AmbientColor.Lerp(MoonColor, AmbientWarmth);
            environment.AmbientLightEnergy = AmbientEnergy;
            environment.AmbientLightSkyContribution = SkyAmbientShare;
        }

        // With a day cycle, the time of day sets the rest.
        if (_cycle != null && IsInstanceValid(_cycle))
            OnCycle(_cycle);
    }
}
