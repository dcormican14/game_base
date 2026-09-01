// Procedural deep-space sky: starfield and nebula clouds. This is PLACEHOLDER
// art meant to be replaced by hand-painted work —
// see Skybox.cs for the two ways to swap it out (a panorama texture, or your
// own sky shader). Everything here is uniform-driven so it can be art-directed
// from the inspector without touching the code.
//
// A sky shader runs once per pixel of the background with EYEDIR holding the
// normalized view direction for that pixel, so all the work below is done in
// direction space on the unit sphere. There is no geometry and no UV seam.
shader_type sky;

// --- Space ------------------------------------------------------------------
uniform vec3 space_color : source_color = vec3(0.008, 0.010, 0.028);
// Subtle brightening toward the galactic band, so the sky is not a flat void.
uniform vec3 horizon_glow : source_color = vec3(0.05, 0.04, 0.10);

// --- Stars ------------------------------------------------------------------
// Cells across the sphere. Higher = more, smaller, denser stars.
//
// Keep this LOW. The stylized filter resamples the screen onto a ~320-row
// virtual grid, and a star smaller than one of those virtual pixels lands in a
// cell or misses it depending on where the camera happens to be pointing —
// which is what makes a dense field crawl and flicker as you look around. No
// correction inside this shader can fix that, because the resampling happens
// downstream of it. The only real fix is fewer, bigger stars: at this density
// and star_size a star spans roughly two virtual pixels and survives the snap.
uniform float star_density : hint_range(20.0, 400.0) = 70.0;
// Fraction of cells that actually contain a star.
uniform float star_coverage : hint_range(0.0, 1.0) = 0.045;
uniform float star_brightness : hint_range(0.0, 8.0) = 3.4;
// Star disc size within its cell, as a fraction of a cell. Large enough that
// the disc covers whole virtual pixels rather than slipping between them.
uniform float star_size : hint_range(0.005, 0.6) = 0.30;
// How much stars vary between warm and cool. 0 = all white.
uniform float star_color_variation : hint_range(0.0, 1.0) = 0.55;

// --- Nebulae ----------------------------------------------------------------
uniform bool nebula_enabled = true;
uniform vec3 nebula_color_a : source_color = vec3(0.35, 0.12, 0.55);
uniform vec3 nebula_color_b : source_color = vec3(0.05, 0.30, 0.55);
uniform float nebula_intensity : hint_range(0.0, 3.0) = 0.85;
uniform float nebula_scale : hint_range(0.2, 8.0) = 1.7;
// Raises the noise floor so clouds break into wisps instead of even fog.
uniform float nebula_contrast : hint_range(0.5, 6.0) = 2.6;

// ---------------------------------------------------------------------------
// Hashes: cheap deterministic pseudo-random. No texture lookups needed.
float hash31(vec3 p) {
    p = fract(p * 0.3183099 + vec3(0.71, 0.113, 0.419));
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

vec3 hash33(vec3 p) {
    return vec3(hash31(p), hash31(p + 19.19), hash31(p + 47.77));
}

// Value noise over 3D directions, so it tiles seamlessly on the sphere.
float value_noise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    // Smoothstep the interpolant; keeps the clouds soft rather than blocky.
    f = f * f * (3.0 - 2.0 * f);

    float n000 = hash31(i + vec3(0.0, 0.0, 0.0));
    float n100 = hash31(i + vec3(1.0, 0.0, 0.0));
    float n010 = hash31(i + vec3(0.0, 1.0, 0.0));
    float n110 = hash31(i + vec3(1.0, 1.0, 0.0));
    float n001 = hash31(i + vec3(0.0, 0.0, 1.0));
    float n101 = hash31(i + vec3(1.0, 0.0, 1.0));
    float n011 = hash31(i + vec3(0.0, 1.0, 1.0));
    float n111 = hash31(i + vec3(1.0, 1.0, 1.0));

    return mix(
        mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
        mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y),
        f.z);
}

// Fractal brownian motion: stacked octaves of noise. This is what gives the
// nebulae structure at several scales instead of one uniform blur.
//
// The octave count is a template argument rather than a fixed 5 because the
// domain warp below calls fbm three extra times per pixel; running the warp at
// full depth triples the cost of the most expensive part of the shader for
// detail that the warp immediately smears away.
float fbm(vec3 p, int octaves) {
    float sum = 0.0;
    float amplitude = 0.5;
    for (int i = 0; i < octaves; i++) {
        sum += value_noise(p) * amplitude;
        p *= 2.02;          // slightly off 2.0 to avoid axis-aligned repeats
        amplitude *= 0.5;
    }
    return sum;
}

// Starfield.
//
// The sphere is diced into cells holding at most one star each, so stars never
// clump the way per-pixel thresholding does. The cells are laid out on the
// CUBE FACE the view direction falls on rather than in 3D: a 3D grid needs a
// 3x3x3 = 27-cell neighbourhood search per pixel, which costs far more than
// this whole shader is worth (it dropped the scene to single-digit FPS). The
// projected version searches 3x3 = 9, and because the grid lies flat across
// the view it also keeps stars round instead of stretching them.
vec3 star_layer(vec3 dir, float density, float coverage, float size_scale, float brightness) {
    // Pick the dominant axis and project onto that cube face.
    vec3 a = abs(dir);
    vec2 uv;
    float face;
    if (a.x >= a.y && a.x >= a.z) {
        uv = dir.yz / a.x;
        face = dir.x > 0.0 ? 0.0 : 1.0;
    } else if (a.y >= a.z) {
        uv = dir.xz / a.y;
        face = dir.y > 0.0 ? 2.0 : 3.0;
    } else {
        uv = dir.xy / a.z;
        face = dir.z > 0.0 ? 4.0 : 5.0;
    }

    // The cube projection is perspective, not angular: one unit of `uv`
    // covers a much smaller angle near a face's corners than at its centre
    // (roughly 3x by the corner). Left uncorrected, stars toward the corners
    // shrink below a pixel and then flicker in and out as the camera turns,
    // because whether they are drawn depends on a pixel centre happening to
    // land inside them. Dividing the radius by the dominant component undoes
    // exactly that stretch — it is the projection's own Jacobian — and holds a
    // star's on-screen size constant across the face.
    float perspective = max(max(a.x, a.y), a.z);

    vec2 scaled = uv * density;
    vec2 cell = floor(scaled);
    vec2 local = fract(scaled) - 0.5;

    vec3 total = vec3(0.0);
    for (int x = -1; x <= 1; x++) {
        for (int y = -1; y <= 1; y++) {
            vec2 offset = vec2(float(x), float(y));
            vec3 id = vec3(cell + offset, face);
            vec3 random = hash33(id);

            // Only a fraction of cells hold a star.
            if (random.x > coverage) {
                continue;
            }

            // Jitter the star inside its cell so the grid never shows.
            vec2 centre = offset + (hash33(id + 3.3).xy - 0.5) * 0.7;
            float distance = length(local - centre);

            // Vary brightness so the field has depth rather than reading as
            // one uniform layer of identical dots. The floor is deliberately
            // high: a star dim enough to sit near the display's noise floor
            // pops in and out under the pixelation filter, so the range runs
            // from "clearly visible" to "bright" rather than down to nothing.
            float magnitude = mix(0.55, 1.0, random.y * random.y);

            // Undo the cube projection's stretch (see `perspective` above) so
            // every star covers the same angle wherever it sits on the face.
            float radius = star_size * size_scale * mix(0.6, 1.4, random.z) / perspective;

            // Hard floor on how small a star may get, in cell units. A star
            // thinner than the pixel grid cannot be sampled reliably: it
            // vanishes and reappears as the view moves. Below the floor the
            // star keeps a drawable size and loses brightness instead, which
            // reads as a faint distant star rather than a flickering one.
            float minimum = 0.35;
            float shrink = 1.0;
            if (radius < minimum) {
                shrink = radius / minimum;
                radius = minimum;
            }

            float intensity = 1.0 - smoothstep(0.0, radius, distance);
            // Squared falloff gives a tight core with a faint halo.
            intensity *= intensity;
            // Fade rather than shrink once the floor is hit; shrink is 1.0 for
            // every star large enough to sample properly, so this is free.
            intensity *= shrink * shrink;

            // Cool blue-white through warm orange, as real star colour.
            vec3 tint = mix(
                vec3(0.75, 0.85, 1.0),
                vec3(1.0, 0.80, 0.62),
                random.y);
            tint = mix(vec3(1.0), tint, star_color_variation);

            total += tint * intensity * magnitude;
        }
    }
    return total * brightness;
}

// One layer only. A second, denser layer of faint stars reads nicely at native
// resolution but is exactly what flickers once the pixelation filter resamples
// the screen: those stars are a fraction of a virtual pixel across and cannot
// be drawn consistently frame to frame. Depth comes from per-star brightness
// variation instead, which is stable under resampling.
vec3 stars(vec3 dir) {
    return star_layer(dir, star_density, star_coverage, 1.0, star_brightness);
}

// Two-tone nebula built from offset fbm lookups, so colour and density vary
// independently instead of one field simply being tinted.
vec3 nebula(vec3 dir) {
    vec3 p = dir * nebula_scale;

    // Domain warp: distort the lookup by another noise field. This is what
    // turns smooth blobs into the curled, filamentary shapes that read as gas.
    // Two cheap octaves are plenty for the warp itself.
    vec3 warp = vec3(fbm(p + 11.3, 2), fbm(p + 27.1, 2), fbm(p + 43.9, 2));
    float density = fbm(p + warp * 1.4, 5);

    // Push the midtones down so clouds occupy part of the sky, not all of it.
    density = pow(clamp(density, 0.0, 1.0), nebula_contrast);

    float blend = fbm(p * 0.7 + 5.5, 2);
    vec3 tint = mix(nebula_color_a, nebula_color_b, clamp(blend, 0.0, 1.0));

    return tint * density * nebula_intensity;
}

vec3 space(vec3 dir) {
    // Base gradient: slightly brighter around the equator of the sky.
    float band = 1.0 - abs(dir.y);
    vec3 color = space_color + horizon_glow * pow(band, 3.0);

    if (nebula_enabled) {
        color += nebula(dir);
    }

    // Stars sit on top of the gas.
    color += stars(dir);

    return color;
}

void sky() {
    vec3 dir = normalize(EYEDIR);

    // The radiance cubemap only feeds ambient light and reflections, and it is
    // heavily blurred by design — sampling it for the visible background turns
    // every star into a smudge. So the background is computed directly, and
    // the cubemap pass gets a cheap approximation instead: stars contribute
    // almost nothing to ambient light, and skipping them there keeps the bake
    // (which runs over six cube faces) from costing more than the view itself.
    if (AT_CUBEMAP_PASS) {
        float band = 1.0 - abs(dir.y);
        vec3 color = space_color + horizon_glow * pow(band, 3.0);
        if (nebula_enabled) {
            color += nebula(dir);
        }
        COLOR = color;
    } else {
        COLOR = space(dir);
    }
}
