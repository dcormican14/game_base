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

    private Skybox _skybox;

    public override void _Ready()
    {
        Moonlight = GetNodeOrNull<DirectionalLight3D>("Moonlight");

        if (Moonlight == null)
        {
            Moonlight = new DirectionalLight3D { Name = "Moonlight" };
            AddChild(Moonlight);
        }

        _skybox = !SkyboxPath.IsEmpty ? GetNodeOrNull<Skybox>(SkyboxPath) : null;
        _skybox ??= NodeSearch.FindByType<Skybox>(GetTree().EditedSceneRoot ?? GetTree().CurrentScene ?? GetParent());

        if (_skybox != null)
        {
            _skybox.EnvironmentReady += OnEnvironment;
            _skybox.MoonMoved += OnMoonMoved;
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
    }

    private void OnEnvironment(Godot.Environment environment) => Apply();

    private void OnMoonMoved(Vector3 direction) => Apply();

    /// <summary>Where the moonlight comes from: the skybox's moon, or the fallback.</summary>
    public Vector3 MoonDirection =>
        (_skybox != null && IsInstanceValid(_skybox) ? _skybox.MoonDirection : FallbackDirection).Normalized();

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

        Godot.Environment environment = _skybox?.Environment;

        if (environment != null)
        {
            environment.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
            environment.AmbientLightColor = AmbientColor.Lerp(MoonColor, AmbientWarmth);
            environment.AmbientLightEnergy = AmbientEnergy;
            environment.AmbientLightSkyContribution = SkyAmbientShare;
        }
    }
}
