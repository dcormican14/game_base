# Making a Convincing Sun in Godot 4: Shaders, Settings and Resources (2D and 3D)

The most realistic sun comes from one custom shader that you write yourself, plus HDR glow. In 3D that is a `shader_type sky` shader; in 2D it is a `shader_type canvas_item` shader on a full-screen rectangle. The shader should draw a tiny disc that is much brighter than 1.0, with limb darkening (the disc's edge is darker than its centre) and a forward-scattering "Mie" halo, and its colour should be driven by the sun's elevation. Bloom then makes the over-bright pixels bleed outward. The built-in sky materials are useful starting points, but none of them gives you a limb-darkened, art-directable disc, so for a stylized-but-physically-informed day–night cycle you will end up with a custom shader either way.

## TL;DR
- **3D:** Convert a ProceduralSkyMaterial or PhysicalSkyMaterial to a ShaderMaterial. Draw the sun yourself in `sky()` using `EYEDIR`, `LIGHT0_DIRECTION`, `LIGHT0_COLOR`, `LIGHT0_ENERGY` and `LIGHT0_SIZE`, with an HDR disc, limb darkening and a Henyey-Greenstein or Cornette-Shanks glow. Then turn on Environment glow with an AgX or Filmic tonemapper. Keep sky updates cheap: the Sky's process mode and radiance size control how often, and at what cost, the lighting cubemap is rebuilt.
- **2D:** Put a `canvas_item` shader with `render_mode blend_add` on a ColorRect between the sky gradient and the hills, and pass it `sun_uv`, `sun_elevation` and colours from the script that reads `hour`. Enable `rendering/viewport/hdr_2d` (Godot 4.2+) and use a WorldEnvironment in Canvas mode with glow so the over-bright core blooms. Add a cheap 2D god-ray overlay only if it suits the look.
- **Priority order (biggest payoff first):** (1) a correct tiny HDR disc with a soft edge; (2) a forward-scattering glow plus horizon reddening tied to elevation; (3) bloom with a good tonemapper; (4) fading the sun below the horizon and occluding it with terrain; (5) limb darkening and flattening near the horizon; (6) god rays or lens flare, if at all. Lens flares and god rays are not built in. They need screen-space shaders, CompositorEffect (Forward+, and partly Mobile), or volumetric fog (Forward+ only).

## Key Findings

- **The current stable version is Godot 4.7.x.** Godot 4.7-stable came out on 18 June 2026 and 4.7.2 on 18 August 2026.\[1\]\[2\] Everything below targets 4.x and notes when features arrived.
- **Sky shaders arrived in Godot 4.0.** They replaced Godot 3's ProceduralSky and PanoramaSky resources, and all three built-in sky materials can be converted to a ShaderMaterial to edit their code.\[3\]\[4\]
- **Sky shaders can read the first four DirectionalLight3D nodes.**\[3\]\[4\] The `LIGHTX_*` built-ins (LIGHT0 to LIGHT3) carry direction, energy, colour and angular size. `LIGHTX_SIZE` is the angular diameter in radians, and the docs give the sun from Earth as "about .0087 radians (0.5 degrees)".\[5\]\[6\]
- **Each built-in material has a clear limit.** ProceduralSkyMaterial is described as "simple and computationally cheap, but unrealistic". PhysicalSkyMaterial uses the Preetham model with one sun and is "substantially more realistic" but "less flexible".\[7\]\[8\] Neither does limb darkening or lets you art-direct the disc colour separately from the light colour.
- **Bloom setup differs by renderer and dimension.** In 3D, values above the HDR threshold glow. In 2D you need HDR 2D (4.2+), or a Glow HDR Threshold below 1.0 when 2D renders in SDR. The Compatibility renderer uses a different glow implementation and hides several glow controls.
- **Godot 4.6 changed glow and AgX defaults.** Glow is now blended before tonemapping, with Screen as the new default blend mode, and AgX gained `agx_white` and `agx_contrast` controls.\[9\] Tutorials written for 4.0–4.5 will look slightly different.
- **Volumetric god rays need Forward+.** Volumetric fog and FogVolume/`shader_type fog` are Forward+ only.\[10\] Screen-space radial-blur god rays work everywhere but must be added by hand.
- **Updating the sky every frame costs performance.** A sky shader can run up to six times per frame.\[6\]\[11\] Changing custom uniforms makes the default Automatic process mode choose Incremental, while using TIME or POSITION forces Real-Time, which requires a 256×256 radiance map.\[12\]\[13\]

## Details

### 1. Godot shader types and which ones matter for a sun

Godot requires each shader to declare a type, and each type has its own built-ins and render modes. The five types are spatial, canvas_item, particles, sky and fog.\[14\]

| Shader type | What it draws | Relevance to the sun |
|---|---|---|
| `sky` (4.0+) | The 3D background and the radiance cubemap used for ambient light and reflections | **Primary 3D tool**: the sun disc, glow and atmosphere |
| `canvas_item` | All 2D nodes and UI | **Primary 2D tool**: the sun disc, glow, 2D god rays, screen-space lens flare overlays |
| `spatial` | 3D meshes | Optional: a billboard sun or flare quads in 3D, and the "sun in front of geometry" tricks |
| `fog` (4.0+) | Density, albedo and emission inside FogVolume nodes | Volumetric light shafts (Forward+ only) |
| `particles` | GPU particles | Rarely needed; dust motes in sunbeams at most |

#### Sky shader reference (verified against the stable docs)

The sky shader is used for three things: drawing the background, updating the radiance cubemap, and drawing optional lower-resolution subpasses.\[6\] "In total, this means the sky shader can run up to six times per frame."\[5\]\[6\]

**Render modes**
- `use_half_res_pass`: allows the shader to write to and read the half-resolution pass.
- `use_quarter_res_pass`: the same for the quarter-resolution pass.
- `disable_fog`: "If used, fog will not affect the sky."\[6\]\[15\]

**Global built-ins**
- `TIME`: "Global time since the engine has started, in seconds". It repeats every 3,600 seconds and is affected by time_scale.\[16\]
- `POSITION`: "Camera position, in world space".\[5\]\[6\]
- `RADIANCE`: the radiance cubemap. It "Can only be read from during the background pass. Check !AT_CUBEMAP_PASS before using."\[5\]\[6\]
- `AT_HALF_RES_PASS`, `AT_QUARTER_RES_PASS` and `AT_CUBEMAP_PASS`: booleans that tell you which pass is running.\[5\]
- `LIGHTX_ENABLED`, `LIGHTX_ENERGY` ("Energy multiplier for LIGHTX"), `LIGHTX_DIRECTION` ("Direction that LIGHTX is facing"), `LIGHTX_COLOR` and `LIGHTX_SIZE` ("Angular diameter of LIGHTX in the sky. Expressed in radians.").\[5\]\[6\] Godot's own sky materials compare `EYEDIR` with `LIGHT0_DIRECTION` to find the sun, so in practice this vector points *toward* the sun in the sky, not along the light rays.

**Sky built-ins**
- `EYEDIR`: "Normalized direction of current pixel. Use this as your basic direction for procedural effects."
- `SKY_COORDS`: "Sphere UV. Used to map a panorama texture to the sky."\[5\]
- `SCREEN_UV`: used to map a texture to the full screen.
- `HALF_RES_COLOR` and `QUARTER_RES_COLOR`: the colour of the matching pixel from the lower-resolution subpasses.

**Outputs:** `COLOR` (vec3), `ALPHA` (the subpasses can use alpha) and `FOG` (vec4, for custom fog blending).

**Version notes**
- The original 2020 pull request called the subpass textures `HALF_RES_TEXTURE`/`QUARTER_RES_TEXTURE`. The shipped names are `HALF_RES_COLOR`/`QUARTER_RES_COLOR`.\[3\]\[4\]
- Early examples use `void fragment()`.\[4\] Current Godot uses `void sky()`.

**How DirectionalLight3D feeds these built-ins.** `sky_mode` has three values:
- **Light and Sky** (default): the light is used both for scene lighting and in the sky.
- **Light Only**: the light "will not be visible from sky shaders".
- **Sky Only**: the light is visible only to sky shaders. The docs suggest this "when you want to control sky effects without illuminating the scene (during a night cycle, for example)".\[17\]

`light_angular_distance` is "The light's angular size in degrees", and "the Sun from the Earth is approximately 0.5".\[18\] Setting it above 0 with shadows on enables PCSS soft shadows, which has "a noticeable performance cost" and is "only supported in the Forward+ rendering method".\[19\] This is what fills `LIGHT0_SIZE` in sky shaders. The practical consequence is that if you size your disc from `LIGHT0_SIZE`, you are tying the sun's visible size to an expensive shadow feature. A custom shader can use its own `sun_angular_diameter` uniform instead.

#### canvas_item shaders (2D)

CanvasItem shaders "are used to draw all 2D elements in Godot."\[20\] The render modes that matter here are:
- `blend_add`: "Additive blend mode".\[20\] This is ideal for light, because it can only brighten what is underneath.
- `blend_mix`: the default.
- `unshaded`.

Useful built-ins are `UV`, `SCREEN_UV` ("Screen UV coordinate for current pixel"), `COLOR` and `TIME`.\[21\]

**Godot 3 to 4 change:** the old `SCREEN_TEXTURE` built-in was "Removed in Godot 4. Use a sampler2D with hint_screen_texture instead", for example `uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_nearest;`.\[21\]\[22\]\[23\] Godot 3 shaders from tutorials will not compile until this is changed.

#### fog shaders

"Fog shaders are always used together with FogVolumes and volumetric fog." They have a single `fog()` function that writes `DENSITY`, `ALBEDO` and `EMISSION`, and their resolution follows the volumetric-fog froxel grid (a 3D grid of small cells aligned to the camera).\[24\]\[25\]

### 2. The built-in sky materials: what they give you for the sun

| Material | Sun model | Key sun parameters (defaults) | Strengths | Limits |
|---|---|---|---|---|
| **ProceduralSkyMaterial** | Up to 4 suns from the first four DirectionalLight3Ds (colour, energy, direction, angular distance) | `sun_angle_max` = 30° ("Distance from center of sun where it fades out completely"), `sun_curve` = 0.15, `sky_top_color`, `sky_horizon_color`, `sky_curve` = 0.15, energy multipliers, `use_debanding` = true | Cheap; "suited for real-time updates"; matches your two-colour zenith/horizon design | The docs call it "unrealistic": no scattering, no reddening, no limb darkening\[8\]\[26\] |
| **PhysicalSkyMaterial** | Preetham analytic daylight model; one sun from the first DirectionalLight3D | `rayleigh_coefficient` = 2.0, `rayleigh_color` = (0.3, 0.405, 0.6), `mie_coefficient` = 0.005, `mie_eccentricity` = 0.8, `mie_color` = (0.69, 0.729, 0.812), `turbidity` = 10, `sun_disk_scale` = 1.0, `ground_color`, `energy_multiplier` = 1.0, `night_sky` texture, `use_debanding` = true | Sunrise and sunset colours happen automatically; "substantially more realistic" | One sun, "less flexible", Preetham "with a few hacks", and it does not hit your hand-picked palette\[4\]\[7\] |
| **PanoramaSkyMaterial** | None; it maps an HDRI image onto the sky | Panorama texture | Photographic skies | A static image; the sun does not move with your `hour` |

**Terms used above**
- **Rayleigh scattering:** light bouncing off air molecules. It scatters blue far more than red, which makes the sky blue and the low sun orange.
- **Mie scattering:** light bouncing off larger particles such as haze and dust. It is roughly colour-neutral and strongly forward-directed, which produces the white glow (aureole) hugging the sun.
- **Eccentricity (g):** how forward-peaked that Mie glow is.

**A pitfall with PhysicalSkyMaterial.** In Godot 4.0 beta 1 it showed no sun disk at all, and `sun_disk_scale` did nothing, because the disk size is computed from `LIGHT0_SIZE`. The reporter's workaround was to convert the material to a shader and replace `LIGHT0_SIZE` with a constant.\[27\] If your disk is missing, check the light's Angular Distance first.

**Converting to a ShaderMaterial.** "For all three *SkyMaterial types, users can select 'Convert to ShaderMaterial' and edit the code directly."\[4\] The official Sky Shaders demo started this way: its volumetric clouds "were added after converting PhysicalSkyMaterial to a ShaderMaterial using the **Convert to ShaderMaterial** button in the editor resource dropdown."\[4\]\[11\] For your project, convert ProceduralSkyMaterial in 3D, because its two-colour gradient already matches your design, and then add the sun code from section 3.

**Godot 3.x.** There were no sky shaders. `ProceduralSky` had `sun_latitude`/`sun_longitude`, `sun_angle_min` = 1, `sun_angle_max` = 100, `sun_curve` = 0.05 and a `texture_size`, and it was baked to a texture.\[28\] A dynamic sky meant re-baking or using a sky dome mesh.

### 3. Techniques for a realistic sun

#### 3a. The sun disc

**Angular size.** The real sun is about 0.53° across, or 0.0087 rad per the Godot docs.\[6\] That is tiny. At a 70° field of view on a 1080p screen it is only about 8 pixels wide (my own estimate), which is why a realistic sun reads mostly through its *glow*, not its disc. Stylized games commonly make the disc 2–5× larger. Pick a value and keep it constant so the sun never "breathes" in size.

**Soft edge.** Measure the angle between `EYEDIR` and the sun direction, then apply `smoothstep` across the last 10–20% of the radius. That edge band is what anti-aliases the disc. Avoid `acos(dot(...))` for tiny angles because it loses precision near 1.0. `2.0 * asin(length(a - b) * 0.5)` is numerically safer.

**Limb darkening.** The standard linear law is I(μ) = I(0)·[1 − u(1 − μ)], where μ = cos θ = √(1 − r²) and r is the distance from the disc centre (0) to the edge (1).\[29\] Measured solar values of u in visible light are about 0.6–0.7: a 2017 SOHO/SDO study (J. Astron. Space Sci.) found averages of 0.691 (SOHO) and 0.648 (SDO) at 0.9 of the disc diameter, consistent with the long-standing value of about 0.6, and a 2020 Banaras Hindu University study (Journal of Scientific Research) found an average of 0.61. "Limb-darkening is much greater in the violet and near ultraviolet than in the red".\[29\] So a per-channel u (for example blue slightly higher than red) gives a faintly warmer edge, which is subtle but physically right.

**HDR brightness.** HDR (high dynamic range) means colour values are allowed to go above 1.0 ("whiter than white") before a final step maps them back to the screen. Give the disc a brightness of roughly 10–100× the sky so that bloom picks it up and the tonemapper rolls it off to white. One godotshaders.com atmosphere shader uses `sundisc_intensity = 10.0`. Godot's Light3D reference notes that "On a clear sunny day a surface in direct sunlight may be approximately 100,000 lux", while moonlit ground is about 0.1 lux, so real-world ratios are enormous. For your stylized look, the key is that the disc clearly exceeds the glow threshold.

#### 3b. Atmospheric scattering, glow and reddening

**Phase functions.** A *phase function* describes in which directions light scatters off a particle, in other words how bright the sky is at a given angle from the sun.
- **Henyey-Greenstein (HG):** p(θ) = (1 − g²) / (4π(1 + g² − 2g·cosθ)^1.5). One parameter, g, controls forward scattering.\[30\]
- **Cornette-Shanks (CS):** adds a (1 + cos²θ) term. It is "physically reasonable", has slightly more back-scatter, and approaches HG as g → 1. Bruneton's widely copied atmosphere model uses CS with **g = 0.76**.\[30\]\[31\]
- Both under-represent the very sharp forward peak of real Mie scattering. Recent research proposes better fits: NVIDIA's Jendersie and d'Eon (SIGGRAPH 2023 Talks) show that "a blend of HG and Draine's phase function can accurately match of the Mie phase function over a wide range of droplet sizes." For games, HG or CS with g ≈ 0.75–0.85 is the standard choice. PhysicalSkyMaterial's default `mie_eccentricity` is 0.8.\[7\]

**Reddening near the horizon.** This comes from *optical depth*: the low sun's light crosses far more air (more "air mass"), so the *transmittance* exp(−β·depth) removes more blue than red. A published Godot port uses standard sea-level Rayleigh coefficients `vec3(5.802, 13.558, 33.100) * 1e-6` per metre, a Rayleigh scale height of 8 km, and an ozone term.\[32\] Multiplying β by 8,000 m gives a zenith optical depth of about (0.046, 0.108, 0.265). That means the overhead sun loses about 23% of its blue but only about 5% of its red, and at the horizon, where Kasten and Young's air-mass formula (Applied Optics, 1989) gives about 38 times the overhead air mass, the sun turns deep orange (my own calculation from those constants). For your design, use this physics as a *driver*. Compute a 0–1 "redness" from elevation and use it to blend your art-directed `#FFF4DC` (high) to `#FFB98A` (low), rather than letting physics pick the exact colour.

**Atmosphere models people have used in Godot**
- **Preetham:** an analytic model; this is what PhysicalSkyMaterial uses.\[7\]
- **Nishita-style single scattering:** ray-march along the view ray and integrate Rayleigh and Mie scattering. Examples are the godotshaders.com "RayLeigh + Mie + Ozone" port and Dimev's Realistic Atmosphere, which is based on Scratchapixel.\[33\]
- **Bruneton:** precomputed scattering tables.
- **Hillaire (EGSR 2020):** small lookup tables (LUTs) for transmittance, multiple scattering and a "sky-view" texture. This is what Unreal's Sky Atmosphere implements. There is no Hillaire implementation in official Godot. A third-party fork (XoDot) has a pull request adding an "AtmosphereSkyMaterial" based on it, and states that Compatibility has no atmosphere.\[34\] Treat that as experimental and not mainline.
- Hosek-Wilkie: I found no maintained Godot 4 implementation.

#### 3c. Sunset behaviour

- **Colour shift with elevation:** drive it from a single `sun_elevation` value, as described above.
- **Horizon glow:** this is a wide, low-g phase lobe multiplied by a horizon band mask, concentrated on the sun's side. It is the "warm glow" overlay you already planned. Use the same `sun_elevation` and the azimuth difference to drive it, and scale sunset higher than sunrise.
- **Flattening.** Atmospheric refraction lifts the lower edge of the sun more than the upper edge, so a setting sun looks vertically squashed. Atmospheric-optics researcher Andrew T. Young (SDSU) gives 5/6 (about 0.83) as "a typical value for the ratio of vertical and horizontal diameters of the setting Sun"; treat it as an artistic target. Implement it by scaling the vertical axis of the disc's local coordinates as elevation approaches 0.
- **Fading below the horizon.** Multiply the disc by a mask on `EYEDIR.y` (3D) or on the screen-space horizon line (2D). Fade the *glow* more slowly than the disc so the afterglow lingers. Keep the scattering terms using the real negative elevation so the "purple light" at −3 to −4° still has a driver.
- **Occlusion:** see the occlusion notes after the 3D example below.

#### Code example 1: 3D sky shader with disc, limb darkening and Mie glow

*Status: **illustrative**, written for this report. The built-in names and render mode are verified against the stable docs; the phase-function formulas are verified against the papers cited above; it has not been compiled.*

```glsl
shader_type sky;
render_mode use_half_res_pass; // optional; remove if you don't use HALF_RES_COLOR

uniform vec3 zenith_color : source_color = vec3(0.18, 0.35, 0.7);
uniform vec3 horizon_color : source_color = vec3(0.75, 0.82, 0.9);
uniform vec3 sun_color_low : source_color = vec3(1.0, 0.725, 0.541);  // #FFB98A
uniform vec3 sun_color_high : source_color = vec3(1.0, 0.957, 0.863); // #FFF4DC
uniform float sun_diameter_deg = 0.53;   // decoupled from light_angular_distance (avoids PCSS cost)
uniform float sun_disc_energy = 40.0;    // HDR: well above glow threshold
uniform float edge_softness : hint_range(0.01, 0.5) = 0.15;
uniform vec3 limb_u = vec3(0.55, 0.6, 0.68); // per-channel linear limb darkening
uniform float mie_g : hint_range(0.0, 0.99) = 0.76;
uniform float mie_strength = 0.15;

float phase_cs(float c, float g) { // Cornette-Shanks
	float g2 = g * g;
	return 3.0 / (8.0 * PI) * ((1.0 - g2) * (1.0 + c * c))
		/ ((2.0 + g2) * pow(1.0 + g2 - 2.0 * g * c, 1.5));
}

void sky() {
	// Base gradient (your keyframed zenith/horizon colours go in these uniforms)
	float h = clamp(EYEDIR.y, 0.0, 1.0);
	vec3 col = mix(horizon_color, zenith_color, sqrt(h));

	if (LIGHT0_ENABLED) {
		vec3 sun_dir = normalize(LIGHT0_DIRECTION);
		float elev = sun_dir.y;                              // -1..1
		float low = 1.0 - smoothstep(0.0, 0.35, elev);       // 1 near horizon
		vec3 sun_tint = mix(sun_color_high, sun_color_low, low);

		float cos_t = dot(EYEDIR, sun_dir);
		float ang = 2.0 * asin(clamp(length(EYEDIR - sun_dir) * 0.5, 0.0, 1.0)); // precise small angles
		float radius = radians(sun_diameter_deg) * 0.5;
		float r = ang / radius;                              // 0 centre .. 1 limb

		// Disc with soft edge + limb darkening
		float disc = 1.0 - smoothstep(1.0 - edge_softness, 1.0, r);
		float mu = sqrt(max(0.0, 1.0 - min(r * r, 1.0)));
		vec3 limb = 1.0 - limb_u * (1.0 - mu);

		// Hide the disc below the horizon (terrain will also occlude it in 3D)
		float above = smoothstep(-0.002, 0.004, EYEDIR.y);

		// Mie aureole (fade it more slowly than the disc so afterglow lingers)
		float glow = phase_cs(cos_t, mie_g) * mie_strength * smoothstep(-0.1, 0.02, elev);

		vec3 light_rgb = LIGHT0_COLOR * LIGHT0_ENERGY;
		float disc_energy = AT_CUBEMAP_PASS ? 1.0 : sun_disc_energy; // keep reflections from blowing out
		col += sun_tint * light_rgb * (glow + disc * above * disc_energy * limb);
	}
	COLOR = col;
}
```

**Why the cubemap clamp?** The GDQuest-derived Stylized Sky shader does the same thing. Its comment explains that "if the sun's intensity goes over 1 its HDR and can bleed through shadows badly and muddy shadows", so the shader limits the sun in `AT_CUBEMAP_PASS`.\[35\]

**Occlusion**
- **3D:** terrain and cloud meshes draw over the sky automatically. Sky-shader clouds must attenuate the sun inside the shader.
- **2D:** draw order does the work. Put the sun layer behind the hills.

#### 3d. Glow, bloom, tonemapping and exposure

**Terms**
- **Bloom (glow):** simulates the way light "bleeds outwards to darker regions" when it exceeds what a camera can record.
- **Tonemapping:** "converts" HDR values so they can be shown on an ordinary (LDR) display.\[36\]

**Environment glow settings that matter**
- `glow_enabled`, `glow_intensity` and `glow_strength`.
- `glow_bloom`: "If set to a value higher than 0, this will make glow visible in areas darker than the glow_hdr_threshold". Keep it at 0 so only the sun glows.\[37\]
- `glow_hdr_threshold`: pixels above it glow. 1.0 means "light over the tonemapper White value".\[38\]
- `glow_hdr_scale` and `glow_hdr_luminance_cap`.
- The glow levels, the blend mode (Additive, Screen, Soft Light, Replace, Mix), and `glow_map` (a lens-dirt-style texture).
- Upscale mode: bicubic on desktop, bilinear on mobile by default.\[38\]

**Renderer and version differences for glow**
- **Compatibility:** "glow uses a different implementation with some properties being unavailable and hidden from the inspector: Levels, Normalized, Strength, Blend Mode, Mix, Map, and Map Strength."\[38\] The 4.3-era class reference said glow was not supported in Compatibility at all, so on that renderer use 4.4 or later and test.\[37\]
- **Mobile:** it "only supports a lower dynamic range up to 2.0", so the threshold "may need to be below 1.0 for glow to be visible".\[37\]
- **4.6 change:** "Glow is now blended before tonemapping, with Screen as the new default mode."\[9\]

**Tonemappers.** Linear "unnaturally clips bright values". Reinhard rolls them off simply. Filmic gives "better contrast than Reinhard".\[39\] ACES is also available. AgX (added in 4.4) "desaturates bright values for a more realistic appearance". AgX's White was fixed at 16.29 when it was introduced, which made it "unsuitable for use with the Mobile rendering method".\[38\] In 4.6 it gained configurable `agx_white` and `agx_contrast`.\[9\]

**Recommendation:** for a sun, AgX or Filmic are best. Their desaturating roll-off turns a peach disc into a cream-white core with a coloured fringe, which is exactly how a real overexposed sun reads. Linear will clip your `#FFB98A` into a flat yellow blob.

**Exposure.** `tonemap_exposure` scales the image before tonemapping. With physical light units you can also use camera attributes and auto exposure. These are 3D only: "Physical light units are only available in 3D rendering, not 2D."\[40\]\[41\]

**HDR display output (4.7)** is "supported on the Forward+ and Mobile renderers only", not Compatibility or the web.\[42\]

**2D glow** — "Since Godot 4.2, you can enable HDR for 2D rendering" (`rendering/viewport/hdr_2d`). This lets you choose which objects glow "using their individual Modulate or Self Modulate properties (use the RAW mode in the color picker)". The alternative is to set the environment background to **Canvas**, enable glow, and lower **Glow HDR Threshold** so non-overbright pixels still glow, using **Background > Canvas Max Layer** to limit which layers are affected.\[43\]
- When 2D HDR arrived in 4.2 it covered only Forward+ and Mobile.\[44\]
- A 2025 docs issue reports that `use_hdr_2d` "is no longer useless with Compatibility renderer", and the updated class reference says the framebuffer is RGBA16 on Forward+ and Compatibility and RGB10_A2 on Mobile.\[45\]\[46\]
- Test on your target renderer, because the docs have changed here.
- Known quirk: toggling `hdr_2d` also changes how 3D glow looks (issue #107682).\[47\]

#### 3e. Lens effects (flare, starburst, streaks, halos, dirt)

Godot has no built-in lens flare. The only built-in "dirt" is `glow_map`. Use one of these approaches instead:

- **Screen-space canvas_item overlay (all renderers).** Use a full-screen ColorRect with the sun's screen position passed in as a uniform. In 3D you get that position from `Camera3D.unproject_position`. The godotshaders.com "Lens Flare for Godot 4" shader (a port of a Shadertoy) switches to `blend_add`. Its example script raycasts toward the sun and hides the flare when the sun is blocked. A commenter points out that "you can use the depth buffer for this directly in the shader" as a cheaper occlusion test.\[48\]\[49\]
- **Bright-pass flares.** "Screen Space Lens Flare with rainbow colored effect" (Godot 4.3, CC0) "takes the rendered screen, and projects the bright parts" into ghosts.\[50\]
- **CompositorEffect (4.3+).** This adds compute passes inside the 3D pipeline. The docs say it is "only supported by the Mobile and Forward+ renderers". In practice, compute-based effects fail on Mobile because the colour buffer lacks the storage flag (issue #96737), and Compatibility has no Compositor at all. "Compositor Lens Flare / Godrays" (MIT, Godot 4.4+) is a ready-made option.\[51\]\[52\]\[53\]
- **Sky shaders cannot draw flares.** As one shader author notes, "Sky shaders are rendered below everything else, so the lens flare would not be rendered above any geometry."\[48\]

#### 3f. God rays (crepuscular rays)

- **2D, cheap and stylized:** pend00's "God rays" canvas_item shader on godotshaders.com animates noise-based rays with angle, spread, cutoff and falloff controls, and has 4.4 and 4.5 variants and a pixelated variant. These are *fake* rays that do not know where the hills are.
- **2D, physically convincing:** render an occlusion mask (sky bright, hills black) to a SubViewport, then radially blur it toward the sun's position. This is the classic GPU Gems 3 approach. The "Screen Space God Rays (Godot 4.3)" shader does exactly this with a `subviewport_tex` and `blend_add`.\[54\]
- **3D volumetric:** enable Environment volumetric fog, with **Anisotropy** set close to 1 for forward scattering.\[10\] Shadows from terrain then cut real shafts. Its limits:
  - Forward+ only.
  - The detail depends on froxel resolution.
  - FogVolume plus a `shader_type fog` shader adds local or animated fog.

#### 3g. The sun as an actual light (3D)

- **DirectionalLight3D:** set energy and colour. With physical light units (Project Settings > Rendering > Lights And Shadows > Use Physical Light Units), you get intensity in lux and colour temperature. The docs give these temperatures: clear-day sun "between 5500 to 6000 Kelvin", sunrise/sunset "around 1850 Kelvin", and 6500 K is white.\[40\]\[55\] Angular distance gives soft shadows (Forward+ PCSS).
- **Keep the disc in sync with the light.** Read `LIGHT0_DIRECTION` in the sky shader rather than passing a separate uniform. Rotate the light from `hour` once per frame.
- **Night:** switch the moon light to Sky Only, or hand LIGHT0 over to it, so the sky keeps a driver while the scene stays dark.
- **Ambient and reflections:** these come from the radiance cubemap.\[12\]
  - Automatic process mode chooses Real-Time if the shader uses `TIME` or `POSITION`, Incremental if it uses `LIGHT_*` or custom uniforms, and Quality otherwise.
  - Real-Time requires `radiance_size` = 256.\[12\]
  - Incremental "updates over several frames", which is ideal for a slowly moving sun.\[12\]
  - The official demo warns that per-frame sky updates have "a significant performance impact for complex sky shaders".\[11\]\[56\]

#### 3h. The 2D path in detail

**Scene layout (back to front)**
1. Sky gradient ColorRect: your zenith/horizon shader.
2. Sun ColorRect with `blend_add`: disc and inner glow.
3. Hills: these occlude the disc.
4. Optional wide-glow and god-ray ColorRect with `blend_add`, *above* the hills. This lets haze bleed over the silhouettes, which looks realistic.
5. CanvasModulate for the overall terrain tint.

**Tinting.** CanvasModulate "applies a color tint to all nodes on a canvas" (only one per canvas). DirectionalLight2D "models an infinite number of parallel rays", "for example: to model sunlight or moonlight".\[57\]\[58\] Use it for rim light on the hills if they have normal maps.

**Feeding values from `hour`**
- `ShaderMaterial.set_shader_parameter(name, value)` sets a uniform. The name "must match the name of the uniform in the code exactly".\[59\]
- Global uniforms (`global uniform float sun_elevation;`) are created in **Project Settings > Shader Globals**. You set them with `RenderingServer.global_shader_parameter_set`, which can be called "as many times as desired without impacting performance".\[60\]\[61\]
- Use a global uniform for `sun_elevation` and `sun_uv` so the sky, sun, hills and stars all read the same value.

#### Code example 2: 2D canvas_item sun with glow

*Status: **illustrative**, not compiled. `blend_add`, `UV`, `COLOR` and `TIME` are verified built-ins.*

```glsl
shader_type canvas_item;
render_mode blend_add, unshaded;

uniform vec2 sun_uv = vec2(0.5, 0.4);      // sun centre in this rect's UV space (0..1)
uniform float aspect = 1.7778;             // rect width / height
uniform float sun_radius = 0.025;          // in units of rect height
uniform float sun_elevation = 0.5;         // -1..1, from hour
uniform float horizon_uv_y = 0.72;         // where the hills' skyline sits
uniform vec3 sun_color_low : source_color = vec3(1.0, 0.725, 0.541);  // #FFB98A
uniform vec3 sun_color_high : source_color = vec3(1.0, 0.957, 0.863); // #FFF4DC
uniform float disc_energy = 4.0;           // >1.0 only blooms with hdr_2d on
uniform float limb_u = 0.6;
uniform float glow_strength = 0.6;
uniform float glow_falloff = 18.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

void fragment() {
	float low = 1.0 - smoothstep(0.0, 0.35, sun_elevation);
	vec3 tint = mix(sun_color_high, sun_color_low, low);

	// Flatten the disc vertically as it nears the horizon (refraction look)
	float flatten = mix(1.0, 0.82, low);
	vec2 d = (UV - sun_uv) * vec2(aspect, 1.0 / flatten);
	float r = length(d) / sun_radius;           // 0 centre .. 1 limb

	float disc = 1.0 - smoothstep(0.85, 1.0, r);
	float mu = sqrt(max(0.0, 1.0 - min(r * r, 1.0)));
	float limb = 1.0 - limb_u * (1.0 - mu);

	// Two-part glow: tight aureole + wide haze (stronger when low)
	float rr = length(d);
	float glow = exp(-rr * glow_falloff) + 0.25 * low / (1.0 + 400.0 * rr * rr);

	// Fade with elevation; hills occlude by draw order
	float vis = smoothstep(-0.05, 0.02, sun_elevation);
	vec3 rgb = tint * (disc * limb * disc_energy + glow * glow_strength) * vis;

	rgb += (hash(UV * 1024.0) - 0.5) / 255.0;    // dither against banding
	COLOR = vec4(max(rgb, 0.0), 1.0);
}
```

**GDScript driver (illustrative)**

```gdscript
@export var hour := 12.0
@onready var sun_mat: ShaderMaterial = $Sun.material

func _process(_delta: float) -> void:
	var t := clampf((hour - 6.0) / 12.0, -0.25, 1.25)   # 0 = sunrise, 1 = sunset
	var elevation := sin(t * PI)                         # negative after sunset
	var uv := Vector2(lerpf(0.08, 0.92, t), 0.72 - 0.55 * maxf(elevation, 0.0))
	sun_mat.set_shader_parameter("sun_uv", uv)
	RenderingServer.global_shader_parameter_set("sun_elevation", elevation) # declared in Project Settings > Shader Globals
```

To make the sun visibly bloom, turn on `rendering/viewport/hdr_2d` and add a WorldEnvironment with Background = Canvas and glow enabled. Start with a threshold around 1.0 and bloom at 0. If you can't use HDR 2D, lower the threshold below 1.0 and keep `disc_energy` ≤ 1.

### 4. Community resources

| Resource | What it does | Godot version / renderer | License | Fit for you |
|---|---|---|---|---|
| **Sky3D** (TokisanGames) — github.com/TokisanGames/Sky3D, Asset Library #5167 | Full day/night cycle: rotating sun/moon/stars, moon phases, dynamic atmosphere, fog and clouds, exposure management | 4.3+; Forward+, Mobile and Compatibility (v2.1) | MIT | Best 3D reference. One reviewer says nights are "pitch-black most the time"\[62\]\[63\] |
| **Official Sky Shaders 3D demo** (godot-demo-projects/3d/sky_shaders) | PhysicalSkyMaterial converted to a shader, with volumetric clouds and an AnimationPlayer day/night cycle | 4.x | MIT (demo repo) | Good for learning conversion and the performance trade-offs\[11\]\[56\] |
| **GDQuest Stylized Sky** — github.com/gdquest-demos/godot-4-stylized-sky (mirrored on godotshaders.com) | Three-colour gradient, stars, clouds, sun/moon "astro" image following the DirectionalLight3D | Godot 4 | Open source (check the repo) | Very close to your stylized brief; includes the cubemap sun clamp\[64\] |
| **Stylized Sky Shader With Clouds For Godot 4** (godotshaders.com) | Day, sunset and night gradients, horizon colours, sun size and blur, uses `use_half_res_pass` | 4.x | godotshaders posts are CC0 by default (check the post) | Easy to adapt your keyframes into |
| **RayLeigh + Mie + Ozone based atmospheric scatter** (godotshaders.com) | Ray-marched scattering, sun disc "faded into blackness at sunset" | 4+ | Check the post | Physically based reference for reddening and transmittance\[32\] |
| **Dimev Realistic-Atmosphere-Godot-and-UE4** (GitHub) | Scratchapixel-based atmosphere, works from space | Built for Godot 3 era | Check the repo | Reference only; needs porting |
| **Zylann godot_atmosphere_shader** (GitHub) | Planet atmosphere with scattering | 4.3+; "If your renderer is set to Compatibility, no atmosphere renders" | Check the repo | Only relevant for planets\[65\] |
| **Lexpartizan Godot_4_sky_shader** (GitHub) | Dynamic sky with clouds and a moon, ported from Godot 3 | Godot 4 port | Check the repo | Older; reference only |
| **Lens Flare for Godot 4** (godotshaders.com) | Procedural flare using `blend_add`, with a sun-tracking and raycast script | 4.x | Check the post | Good 3D flare; adapt it for 2D (a commenter flips UV.y) |
| **Screen Space Lens Flare with rainbow colored effect** (godotshaders.com) | Bright-pass flare ghosts | 4.3, Forward+ | CC0 | Works in 2D too |
| **Compositor Lens Flare / Godrays** (Godot Asset Store, arez) | Flares and god rays as a CompositorEffect | 4.4+; Forward+ (Mobile is unreliable) | MIT | 3D on desktop only |
| **God rays** (pend00), **God Rays 2D v4.4.1**, **Pixelated God Rays** (godotshaders.com) | Animated noise rays (canvas_item) | 4.x | CC0 | Quick 2D shafts |
| **Screen Space God Rays (Godot 4.3)** (godotshaders.com) | Occlusion-mask radial blur, `blend_add` | 4.3 | Check the post | The best "real" 2D or 3D shaft method |
| **Official Volumetric Fog demo** | FogVolumes and a custom fog shader | 4.2+, Forward+ | MIT | 3D light shafts\[66\] |

**Version caveat:** Godot 4.3 introduced reverse-Z depth, which "broke compatibility" for depth-based shaders written for 4.2 and earlier.\[65\]\[67\] Check any flare or fog shader that reads depth.

### 5. Performance and compatibility

| Technique | Cost | Forward+ | Mobile | Compatibility / Web |
|---|---|---|---|---|
| Custom sky shader (analytic disc and glow) | Low | ✅ | ✅ | ✅ (but the 3D sky still has a cost) |
| Ray-marched atmosphere in the sky | High, because it can run up to six times per frame | ✅ | ⚠️ | ⚠️ |
| Radiance updates each frame | High; Real-Time needs 256² | ✅ | ⚠️ | ⚠️ |
| Glow / bloom | Low to medium | ✅ | ✅ (range up to 2.0) | ⚠️ different implementation, fewer controls |
| HDR 2D | Medium (16-bit buffer) | ✅ (4.2+) | ✅ (10-bit) | ⚠️ newer versions only; test |
| PCSS soft sun shadows | High | ✅ | ❌ | ❌ |
| Volumetric fog / fog shaders | High | ✅ | ❌ | ❌ |
| CompositorEffect | Varies | ✅ | ⚠️ | ❌ |
| canvas_item flares and god rays | Low to medium | ✅ | ✅ | ✅ |

**Cutting the sky cost**
- Do the expensive work only in `AT_CUBEMAP_PASS` and read `RADIANCE` for the background. "With a completely static sky, this means that it needs to be rendered only once."\[6\]\[68\]
- Use the half- or quarter-res passes for clouds.\[4\]
- Use Incremental process mode, and update `hour`-driven uniforms in small steps rather than every frame if the sky is expensive.

**Banding.** Smooth glows around the sun band easily, especially in 8-bit 2D. Keep `use_debanding` on in the sky materials (it defaults to true).\[7\]\[26\] Add a dither of about ±0.5/255 noise in custom shaders, and prefer HDR 2D where available.

## Recommendations

### (a) 2D game — in priority order
1. **A canvas_item sun with `blend_add`** (code example 2): a small disc, a soft edge, and colour that follows `sun_elevation` from `#FFF4DC` to `#FFB98A`. This is the biggest payoff.
2. **A two-part glow:** a tight aureole plus a wide, low haze that strengthens near the horizon and is biased to the sun's side to feed your warm horizon overlay.
3. **HDR 2D plus WorldEnvironment (Canvas) glow,** with AgX or Filmic tonemapping, so the core blooms into cream-white.
4. **Horizon handling:** draw order behind the hills; fade the disc fast and the glow slowly; flatten the disc near the horizon. Share one global `sun_elevation` with the sky, stars, Belt of Venus and purple-light overlays.
5. **Limb darkening and dither:** subtle, cheap, and they make it read as a sphere.
6. **Optional:** occlusion-mask god rays over the hills at golden hour, then a restrained screen-space flare. Skip both if they fight the stylized look.

**Implementation order:** global uniforms and the `hour` driver → sun shader → HDR 2D and glow → horizon and occlusion → CanvasModulate tint of the hills → extras.

### (b) 3D game — in priority order
1. **Convert ProceduralSkyMaterial to a ShaderMaterial** and add the disc, limb darkening and Cornette-Shanks glow (code example 1). Size the disc with your own uniform rather than relying on Angular Distance.
2. **Drive DirectionalLight3D rotation, colour and energy from `hour`** and read `LIGHT0_*` in the sky so the disc and the light never drift apart. Use Sky Only or moon handover at night.
3. **Environment glow** with a threshold around 1.0, bloom at 0, and AgX (4.4+, configurable in 4.6+) or Filmic.
4. **Elevation-driven reddening and horizon glow,** borrowing the Rayleigh constants and air-mass idea from the "RayLeigh + Mie + Ozone" shader as a driver for your palette.
5. **Radiance strategy:** Incremental process mode, radiance size 256 or lower, and a clamped sun in the cubemap pass.
6. **Optional, Forward+ only:** volumetric fog with high anisotropy for shafts, PCSS at about 0.5° for soft shadows, and a CompositorEffect or canvas lens flare with a depth or raycast occlusion test.

**If you want to skip writing shaders**, try **Sky3D** first (MIT, 4.3+, all renderers). Then study its sun code before replacing pieces with your own palette.

## Caveats
- The shader examples are illustrative and have not been compiled. Built-in names are verified against the stable docs, but test each one in your exact Godot version.
- Where the docs say `LIGHTX_DIRECTION` is the "Direction that LIGHTX is facing", the built-in materials use it as the toward-the-sun vector. Verify the sign with a quick test such as `COLOR = vec3(max(dot(EYEDIR, LIGHT0_DIRECTION), 0.0));`.
- The docs on HDR 2D and glow in the Compatibility renderer changed between 4.2 and 4.7, and I could not fully confirm current behaviour there.
- Not verified in this research:
  - The sunset flattening ratio (about 5/6, per Andrew T. Young) and the horizon air mass (about 38, per Kasten and Young 1989) come from general atmospheric-optics sources and have not been tested in Godot.
  - The license terms of several community repos and posts. Check each one before reuse.
  - The XoDot Hillaire atmosphere is from a third-party fork, not official Godot.

## Sources

1. [Download Godot 4.7 (stable)](https://godotengine.org/download/archive/4.7-stable/)
2. [Godot download archive](https://godotengine.org/download/archive/)
3. [Implement Sky Shaders by clayjohn · Pull Request #37179 · godotengine/godot](https://github.com/godotengine/godot/pull/37179)
4. [Custom sky shaders in Godot 4.0](https://godotengine.org/article/custom-sky-shaders-godot-4-0/)
5. [Sky shaders — Godot Engine (4.4) documentation in English](https://docs.godotengine.org/en/4.4/tutorials/shaders/shader_reference/sky_shader.html)
6. [Sky shaders — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/tutorials/shaders/shader_reference/sky_shader.html)
7. [PhysicalSkyMaterial — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/classes/class_physicalskymaterial.html)
8. [ProceduralSkyMaterial](https://rokojori.com/en/labs/godot/docs/4.3/proceduralskymaterial-class)
9. [Godot 4.6 Release: It's all about your flow](https://godotengine.org/releases/4.6/)
10. [Volumetric fog and fog volumes — Godot Engine (latest) documentation in English](https://docs.godotengine.org/en/latest/tutorials/3d/volumetric_fog.html)
11. [Sky Shaders 3D Demo - Godot Asset Store](https://store.godotengine.org/asset/godot-foundation/sky-shaders-3d-demo/)
12. [Sky — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/classes/class_sky.html)
13. [Sky - Godot 4.2 - W3cubDocs](https://docs.w3cub.com/godot~4.2/classes/class_sky.html)
14. [Introduction to shaders — Godot Engine (4.5) documentation in English](https://docs.godotengine.org/en/4.5/tutorials/shaders/introduction_to_shaders.html)
15. [Sky package - github.com/AveryLucas/gogogd/shaders/pipeline/Sky - Go Packages](https://pkg.go.dev/github.com/AveryLucas/gogogd/shaders/pipeline/Sky)
16. [Spatial shaders — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/tutorials/shaders/shader_reference/spatial_shader.html)
17. [DirectionalLight3D - Godot Docs](https://docs.godotengine.org/en/stable/classes/class_directionallight3d.html)
18. [3D lights and shadows - Godot Docs](https://docs.godotengine.org/en/4.4/tutorials/3d/lights_and_shadows.html)
19. [Light3D — Godot Engine (4.4) documentation in English](https://docs.godotengine.org/en/4.4/classes/class_light3d.html)
20. [CanvasItem shaders — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/tutorials/shaders/shader_reference/canvas_item_shader.html)
21. [CanvasItem shaders — Godot Engine (4.4) documentation in English](https://docs.godotengine.org/en/4.4/tutorials/shaders/shader_reference/canvas_item_shader.html)
22. [Screen-reading shaders — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/tutorials/shaders/screen-reading_shaders.html)
23. [hint\_screen\_texture in Godot 4: Full Guide + Examples](https://gtstu.com/godot-4-shader-tutorial-visual-effects/)
24. [Fog shaders — Godot Engine (4.4) documentation in English](https://docs.godotengine.org/en/4.4/tutorials/shaders/shader_reference/fog_shader.html)
25. [Fog shaders](https://trinovantes.github.io/godot-docs/tutorials/shaders/shader_reference/fog_shader.html)
26. [ProceduralSkyMaterial — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/classes/class_proceduralskymaterial.html)
27. [Physical sky material missing the sun disk · Issue #65921 · godotengine/godot](https://github.com/godotengine/godot/issues/65921)
28. [ProceduralSky — Godot Engine (3.2) documentation in English](https://docs.godotengine.org/en/3.2/classes/class_proceduralsky.html)
29. [6.1: Introduction. The Empirical Limb-darkening - Physics LibreTexts](<https://phys.libretexts.org/Bookshelves/Astronomy__Cosmology/Stellar_Atmospheres_(Tatum)/06:_Limb_Darkening/6.01:_Introduction._The_Empirical_Limb-darkening>)
30. [Physically Based Real‐Time Rendering of Atmospheres using Mie Theory - Schneegans - 2024 - Computer Graphics Forum - Wiley Online Library](https://onlinelibrary.wiley.com/doi/10.1111/cgf.15010)
31. [Physically Based Real-Time Rendering of the Martian Atmosphere Tim Meyran](https://elib.dlr.de/203154/1/HSH_Masterarbeit_Tim_Meyran.pdf)
32. [RayLeigh + Mie + Ozone based atmospheric scatter - Godot Shaders](https://godotshaders.com/shader/rayleigh-mie-ozone-based-atmospheric-scatter/)
33. [GitHub - Dimev/Realistic-Atmosphere-Godot-and-UE4: A realistic atmosphere material for both the Godot game engine and Unreal Engine 4 · GitHub](https://github.com/Dimev/Realistic-Atmosphere-Godot-and-UE4)
34. [Add AtmosphereSkyMaterial and Environment atmosphere support by CryDot42 · Pull Request #18 · CryDot42/XoDot](https://github.com/CryDot42/XoDot/pull/18)
35. [Stylized Sky - Godot Shaders](https://godotshaders.com/shader/stylized-sky/)
36. [Environment — Godot Engine (4.5) documentation in English](https://docs.godotengine.org/en/4.5/classes/class_environment.html)
37. [Environment](https://rokojori.com/en/labs/godot/docs/4.3/environment-class)
38. [Environment and post-processing — Godot Engine (4.4) documentation in English](https://docs.godotengine.org/en/4.4/tutorials/3d/environment_and_post_processing.html)
39. [Environment and post-processing — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/tutorials/3d/environment_and_post_processing.html)
40. [Physical light and camera units — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/tutorials/3d/physical_light_and_camera_units.html)
41. [Environment — Godot Engine (4.2) documentation in English](https://docs.godotengine.org/en/4.2/classes/class_environment.html)
42. [HDR output arrives in Godot 4.7](https://godotengine.org/article/hdr-output-arrives-in-godot-4-7/)
43. [github.com](https://github.com/godotengine/godot-docs/pull/8285.diff)
44. [Dev snapshot: Godot 4.2 dev 3](https://godotengine.org/article/dev-snapshot-godot-4-2-dev-3/)
45. [Viewport.use\_hdr\_2d is no longer useless with Compatibility renderer · Issue #10896 · godotengine/godot-docs](https://github.com/godotengine/godot-docs/issues/10896)
46. [github.com](https://github.com/godotengine/godot-docs/pull/11261.patch)
47. [HDR 2D affects 3D glow and BCS · Issue #107682 · godotengine/godot](https://github.com/godotengine/godot/issues/107682)
48. [Lens Flare Shader - Godot Shaders](https://godotshaders.com/shader/lens-flare-shader/)
49. [Lens Flare for Godot 4 - Godot Shaders](https://godotshaders.com/shader/lens-flare-for-godot-4/)
50. [Screen Space Lens Flare with rainbow colored effect - Godot Shaders](https://godotshaders.com/shader/screen-space-lens-flare-with-rainbow-colored-effect/)
51. [Compositor Lens Flare / Godrays - Godot Asset Store](https://store.godotengine.org/asset/arez/compositor-lens-flare-godrays/)
52. [The Compositor - Godot Docs](https://docs.godotengine.org/en/stable/tutorials/rendering/compositor.html)
53. [CompositorEffect in Mobile renderer throws "Image needs the TEXTURE\_USAGE\_STORAGE\_BIT usage flag" · Issue #96737 · godotengine/godot](https://github.com/godotengine/godot/issues/96737)
54. [Screen Space God Rays (Godot.4.3) - Godot Shaders](https://godotshaders.com/shader/screen-space-god-rays-godot-4-3/)
55. [Physical light and camera units — Godot Engine (latest) documentation in English](https://docs.godotengine.org/en/latest/tutorials/3d/physical_light_and_camera_units.html)
56. [godot-demo-projects/3d/sky\_shaders at master · godotengine/godot-demo-projects](https://github.com/godotengine/godot-demo-projects/tree/master/3d/sky_shaders)
57. [DirectionalLight2D — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/classes/class_directionallight2d.html)
58. [CanvasModulate — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/classes/class_canvasmodulate.html)
59. [ShaderMaterial — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/classes/class_shadermaterial.html)
60. [Shading language — Godot Engine (stable) documentation in English](https://docs.godotengine.org/en/stable/tutorials/shaders/shader_reference/shading_language.html)
61. [Shading language — Godot Engine (4.3) documentation in English](https://docs.godotengine.org/en/4.3/tutorials/shaders/shader_reference/shading_language.html)
62. [Sky3D - Godot Asset Store](https://store.godotengine.org/asset/tokisangames/sky3d/)
63. [GitHub - TokisanGames/Sky3D: Atmospheric Day/Night Cycle for Godot 4 · GitHub](https://github.com/TokisanGames/Sky3D)
64. [GitHub - gdquest-demos/godot-4-stylized-sky: An open-source shader for creating stylized skies. Compatible with Godot game engine version 4. · GitHub](https://github.com/gdquest-demos/godot-4-stylized-sky)
65. [GitHub - Zylann/godot\_atmosphere\_shader: Planet atmosphere shader for Godot Engine · GitHub](https://github.com/Zylann/godot_atmosphere_shader)
66. [Volumetric Fog Demo - Godot Asset Library](https://godotengine.org/asset-library/asset/2754)
67. [A downloadable asset pack](https://immaculate-lift-studio.itch.io/psx-style-camera-shader-for-godot-4)
68. [github.com](https://github.com/godotengine/godot-docs/pull/8069.diff)
