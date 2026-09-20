// The topsoil's surface: sand.
//
// PER PIXEL, NOT PER VERTEX. The shell's quads are about 3.9 world units on
// the ground, so anything painted into vertex colours is at best a blotch that
// size, interpolated soft across each quad. Sand has to be finer than the mesh
// by a long way, so it is computed per fragment and costs no geometry at all.
//
// WHAT MAKES IT READ AS SAND, in the order the eye picks it up:
//
//   1. GRAIN      fine speckle, the size of sand itself, which is what stops
//                 the ground reading as a painted surface;
//   2. DRIFT      broad slow variation, so one patch of ground is a slightly
//                 different tan from the next and the surface is not uniform;
//   3. RIPPLES    the wind-blown banding sand forms, stretched and low in
//                 contrast -- the one feature that says "sand" rather than
//                 "dirt", and the one that looks wrong the moment it is
//                 overdone.
//
// All three are value noise: a lattice of stable random values, smoothed
// between its corners. Hashing a SNAPPED direction instead -- one flat value
// per lattice cell -- tiles the planet in squares, which rendered as a visible
// checkerboard over the whole surface at every scale it was tried. The
// smoothing between corners is what removes the grid.
shader_type spatial;

// Sand is matte. A specular highlight rolling across it reads as wet plastic.
render_mode diffuse_lambert, specular_disabled;

// --- Colour -----------------------------------------------------------------
// CALIBRATED AGAINST A RENDER, NOT PICKED, AND AGAINST THE ROCK BESIDE IT.
// The scene tonemaps Filmic under a 1.5-energy sun and is tuned for dark
// terrain -- the rock next to this uses albedo 0.155 to 0.245, and the skybox
// notes that soil around 0.10 is what the ambient was balanced for. A tan
// chosen as the colour wanted ON SCREEN (0.64, 0.51, 0.34) arrived as
// near-white cream, measured at luminance 0.90 where tan wants about 0.45.
// These sit in the rock's range, so the sand reads as ground beside it rather
// than as paper. Re-measure against a render if the sun, the tonemapper or the
// ambient energy change.
uniform vec3 sand : source_color = vec3(0.24, 0.180, 0.105);

// The darker tan the grain and ripples shade toward. A DARKER TAN RATHER THAN
// A DIFFERENT HUE: the moment it drifts toward grey or red the surface stops
// looking like sand and starts looking like something spilled on it.
uniform vec3 sand_dark : source_color = vec3(0.150, 0.108, 0.060);

// --- Grain ------------------------------------------------------------------
// The speckle. Fine enough to sit well below the quad size, which is the whole
// reason this is a shader rather than vertex colour.
uniform float grain_scale : hint_range(50.0, 4000.0) = 1400.0;
uniform float grain : hint_range(0.0, 1.0) = 0.55;

// Grain quantized into a few flat levels rather than left smooth.
//
// Sand is made of discrete bits, and a smooth gradient of tone reads as
// airbrush -- the same mistake the sky shader warns about. Stepping the value
// keeps the surface granular. Few levels, because many is indistinguishable
// from smooth.
uniform int grain_levels : hint_range(2, 12) = 5;

// --- Drift ------------------------------------------------------------------
// Broad variation, so the ground is not one flat tone out to the horizon.
uniform float drift_scale : hint_range(1.0, 200.0) = 26.0;
uniform float drift : hint_range(0.0, 1.0) = 0.35;

// --- Ripples ----------------------------------------------------------------
// The wind-blown banding. STRETCHED, because ripples are long across the wind
// and tight along it -- sampling round noise gives blotches, not ripples, and
// the stretch is the whole difference.
uniform float ripple_scale : hint_range(1.0, 300.0) = 60.0;
uniform float ripple_stretch : hint_range(1.0, 30.0) = 9.0;
uniform float ripples : hint_range(0.0, 1.0) = 0.30;

// How sharply a crest turns into a trough. Low is a soft dune surface, high is
// corrugated.
uniform float ripple_sharpness : hint_range(1.0, 8.0) = 2.2;

varying vec3 dir;

void vertex() {
    // The shell is a sphere about the planet's centre, so the object-space
    // position IS the outward direction. Everything below works in this
    // direction space rather than in UV: the shell is a cubed sphere of six
    // faces, and any UV would seam where they meet.
    dir = normalize(VERTEX);
}

float hash3(vec3 c) {
    return fract(sin(dot(c, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
}

// Value noise: a lattice of stable random values, smoothed between its corners.
float value_noise(vec3 at) {
    vec3 cell = floor(at);
    vec3 t = at - cell;

    // Smoothstep per axis, so there is no kink at a lattice boundary.
    t = t * t * (3.0 - 2.0 * t);

    float n000 = hash3(cell + vec3(0.0, 0.0, 0.0));
    float n100 = hash3(cell + vec3(1.0, 0.0, 0.0));
    float n010 = hash3(cell + vec3(0.0, 1.0, 0.0));
    float n110 = hash3(cell + vec3(1.0, 1.0, 0.0));
    float n001 = hash3(cell + vec3(0.0, 0.0, 1.0));
    float n101 = hash3(cell + vec3(1.0, 0.0, 1.0));
    float n011 = hash3(cell + vec3(0.0, 1.0, 1.0));
    float n111 = hash3(cell + vec3(1.0, 1.0, 1.0));

    return mix(
        mix(mix(n000, n100, t.x), mix(n010, n110, t.x), t.y),
        mix(mix(n001, n101, t.x), mix(n011, n111, t.x), t.y),
        t.z);
}

// Several octaves of value noise. Sand is self-similar -- dunes carry ripples
// carry grains -- so one frequency alone reads as a single artificial scale.
float fbm(vec3 at, int octaves) {
    float total = 0.0;
    float weight = 0.0;
    float amplitude = 1.0;

    for (int i = 0; i < octaves; i++) {
        total += value_noise(at) * amplitude;
        weight += amplitude;

        // Not exactly two, so the octaves do not land on harmonics of one
        // another and beat into visible banding.
        at *= 2.13;
        amplitude *= 0.5;
    }

    return total / max(weight, 0.0001);
}

void fragment() {
    vec3 d = normalize(dir);

    // 1. DRIFT: broad and slow. Sets which patch of ground is a little lighter
    //    or darker than its neighbours.
    float broad = fbm(d * drift_scale, 3);

    // 2. RIPPLES: noise sampled through a STRETCHED frame, so it comes out as
    //    long bands rather than as blotches.
    //
    //    The frame is built from whichever world axis is least aligned with
    //    this direction, so the cross product never collapses. That makes the
    //    ripple direction vary smoothly over the planet -- the wind is not the
    //    same everywhere, which is what stops the banding looking stamped on.
    vec3 axis = abs(d.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 east = normalize(cross(axis, d));

    vec3 rippled = d * ripple_scale
        + east * dot(d, east) * ripple_scale * (ripple_stretch - 1.0);

    float band = fbm(rippled, 2);

    // Pushed toward its ends, so a crest has a shape rather than being a
    // smooth swell. This is what makes ripples read as ripples.
    band = pow(clamp(band, 0.0, 1.0), ripple_sharpness);

    // 3. GRAIN: the fine speckle, stepped into flat levels so the surface stays
    //    granular rather than airbrushed.
    float speck = value_noise(d * grain_scale);
    speck = floor(speck * float(grain_levels)) / float(grain_levels - 1);

    // Combined as a single darkness, so the three never fight: each one only
    // ever shades the sand toward the darker tan.
    //
    // NORMALISED BY THE TOTAL WEIGHT, so turning one contribution up changes
    // the balance between them rather than simply darkening the whole planet.
    float shade = broad * drift + band * ripples + speck * grain;

    shade /= max(drift + ripples + grain, 0.0001);

    ALBEDO = mix(sand, sand_dark, clamp(shade, 0.0, 1.0));

    // Matte. Sand has no sheen at all.
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;
}
