using Godot;
using System.Collections.Generic;

namespace GameBase.Nodes;

/// <summary>
/// The render materials the world draws with: one for every raw node, one per
/// particle kind. Shared by every section, so the whole world is a handful of
/// materials however many sections it has.
/// </summary>
public static class NodeMaterials
{
    private const string ParticleShaderPath = "res://Modules/Nodes/ParticleSurface.gdshader";

    private static StandardMaterial3D _raw;
    private static readonly Dictionary<NodeMaterial, Material> Particles = new();

    /// <summary>
    /// Raw nodes: flat, matte, coloured per vertex. The colour is authored in
    /// sRGB (see <see cref="Core.Palette"/>), so the material is told so.
    ///
    /// DOUBLE-SIDED, as the particle surface is. Where raw nodes meet particle nodes the two
    /// surfaces are built by different rules and cannot meet perfectly; seen
    /// through the hairline between them, a back face drawn as shadowed
    /// material reads as a crease, where a culled one would be a slit of sky.
    /// </summary>
    public static StandardMaterial3D Raw => _raw ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        Roughness = 1f,
        MetallicSpecular = 0.1f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private static float _handoverStart, _handoverEnd;

    /// <summary>
    /// Dissolves every node surface away with distance from the camera,
    /// whole nearer than <paramref name="start"/> and gone past
    /// <paramref name="end"/>, where something else draws the ground (the
    /// far terrain dissolves in on the same pixels). Raw nodes use the
    /// engine's pixel-dither distance fade, reversed; particle surfaces the
    /// same test in their shader. An end of 0 turns it off.
    /// </summary>
    public static void SetHandover(float start, float end)
    {
        _handoverStart = start;
        _handoverEnd = end;

        StandardMaterial3D raw = Raw;
        if (end > 0f)
        {
            raw.DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelDither;
            // Min past max reverses the fade: gone at min, whole at max.
            raw.DistanceFadeMinDistance = end;
            raw.DistanceFadeMaxDistance = start;
        }
        else
        {
            raw.DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.Disabled;
        }

        foreach (Material material in Particles.Values)
            ApplyHandover(material);
    }

    private static void ApplyHandover(Material material)
    {
        if (material is not ShaderMaterial shaded)
            return;

        shaded.SetShaderParameter("handover_start", _handoverStart);
        shaded.SetShaderParameter("handover_end", _handoverEnd);
    }

    /// <summary>The surface material for one kind of particle.</summary>
    public static Material Particle(ParticleNode kind)
    {
        if (Particles.TryGetValue(kind.Material, out Material cached))
            return cached;

        var shader = GD.Load<Shader>(ParticleShaderPath);
        Material material;

        if (shader == null)
        {
            // Still drawable without the shader, just without grain.
            material = new StandardMaterial3D { AlbedoColor = Albedo(kind.Colour), Roughness = 1f };
        }
        else
        {
            var shaded = new ShaderMaterial { Shader = shader };
            shaded.SetShaderParameter("base_color", AsVector(Albedo(kind.Colour)));
            shaded.SetShaderParameter("shade_color", AsVector(Albedo(kind.Shade)));
            material = shaded;
        }

        ApplyHandover(material);
        Particles[kind.Material] = material;
        return material;
    }

    private static Vector3 AsVector(Color color) => new(color.R, color.G, color.B);

    /// <summary>
    /// How much light the level puts on an open surface in full moonlight, as a
    /// linear multiplier on albedo. Calibrated against a render of the planet
    /// level under its LightingRig: lit sand lands at the palette's lightness,
    /// with the moonlight's warm cast.
    /// </summary>
    public const float LitExposure = 2.3f;

    /// <summary>
    /// The albedo that renders as a given on-screen colour under the level's
    /// light. Particle colours in the palette are what the player should SEE;
    /// a pale material given its screen colour as albedo washes out to white
    /// under the moonlight, taking its grain with it.
    /// </summary>
    public static Color Albedo(Color onScreen) =>
        (onScreen.SrgbToLinear() * (1f / LitExposure)).LinearToSrgb();
}
