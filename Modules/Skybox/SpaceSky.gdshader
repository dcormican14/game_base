// Procedural day-and-night sky in the style of the reference pixel art,
// built as layers that composite in order:
//
//   1. SPACE    flat plum ground (#28061e halved toward black)
//   2. NEBULAE  clouds quantized into a few flat tones with hard stepped
//               edges, orbiting one axis like a very slow hurricane
//   3. DAY      the Day-Night spec's sky: overhead-to-horizon gradient, the
//               warm glow on the sun's side, purple light, and the Belt of
//               Venus over the Earth's shadow (see DayCycle, which hands over
//               every colour and strength ready to draw). SEAMLESS: every
//               layer is a smooth curve -- no bands, no steps, no corners,
//               nothing stopping at the horizon -- worked out from the exact
//               direction, not the pixel grid
//   4. STARS    three sizes with drawn twinkle arms, plus rare shooting
//               stars, fading out by day
//   5. SUN      a realistic sun -- an over-bright disc, darker at its limb,
//               flattened near the horizon, in a forward-scattering glow.
//               The disc is put through the pixel filter (worked out on the
//               sky's pixel grid); its glow is smooth, like the day's layers
//   6. MOON     a pixel-drawn moon with maria and craters, opposite the sun,
//               shining the same way
//
// The night is unchanged: with the day's layers at zero, what is drawn is the
// night sky as it always was. The stars and nebulae turn with the planet.
//
// Each of the first three has a BRIGHTNESS from 0 (black) to 1 (the layer's
// full, calibrated luminosity), so the night can be dimmed layer by layer.
//
// EVERYTHING SNAPS TO A PIXEL GRID. The reference is pixel art: hard edges, no
// antialiasing, flat colour bands. That is enforced by quantizing the view
// direction onto a fixed grid up front (`to_pixel`) and by stepping colour
// into discrete levels rather than interpolating. Smooth gradients and soft
// falloff are what made an earlier version read as airbrushed rather than
// drawn, so they are deliberately absent.
//
// A sky shader runs once per pixel of the background with EYEDIR holding the
// normalized view direction, so all the work below is in direction space on
// the unit sphere. There is no geometry and no UV seam.
shader_type sky;

// --- Pixel grid -------------------------------------------------------------
// Cells across a cube face: the art's "pixel" size. Every layer samples at
// cell centres, so colour is constant across a cell and every edge lands on a
// cell boundary.
uniform float pixel_grid : hint_range(64.0, 1024.0) = 300.0;

// --- Layer 1: space ---------------------------------------------------------
// The reference background #28061e halved toward black (#14030f). PRE-TONEMAP:
// the environment's Filmic curve crushes darks hard, so this is calibrated
// against a render, not computed. Re-measure if the tonemapper, SkyEnergy or
// AmbientEnergy change.
uniform vec3 space_color : source_color = vec3(0.0747, 0.0092, 0.0521);
uniform float space_brightness : hint_range(0.0, 1.0) = 0.25;

// --- Layer 2: nebulae -------------------------------------------------------
uniform bool nebula_enabled = true;
// The reference nebula is a handful of FLAT tones with visible stepped
// contours, like a topographic map — not a gradient. These three are sampled
// from it (#300c2a, #3c1230, #4e1836), brightest last.
uniform vec3 nebula_tone_1 : source_color = vec3(0.115, 0.020, 0.093);
uniform vec3 nebula_tone_2 : source_color = vec3(0.180, 0.035, 0.130);
uniform vec3 nebula_tone_3 : source_color = vec3(0.320, 0.060, 0.180);
uniform float nebula_opacity : hint_range(0.0, 2.0) = 1.0;
uniform float nebula_brightness : hint_range(0.0, 1.0) = 0.5;
uniform float nebula_scale : hint_range(0.2, 12.0) = 4.5;
// Density below `floor` is empty sky. A threshold, not a power curve: pow()
// never reaches zero, so at any visible strength it washes the whole dome
// instead of leaving gaps between clouds.
uniform float nebula_floor : hint_range(0.0, 1.0) = 0.50;
uniform float nebula_span : hint_range(0.05, 0.6) = 0.26;
// Radians per second about the hurricane axis; a full turn takes ~9 minutes.
uniform float nebula_spin : hint_range(0.0, 0.05) = 0.012;
uniform vec3 nebula_axis = vec3(0.0, 1.0, 0.0);
// How fast clouds change shape as they travel, independent of the spin.
uniform float nebula_morph : hint_range(0.0, 0.2) = 0.02;

// --- Layer 3: stars ---------------------------------------------------------
// Latitude rings the star sphere is cut into. Each ring holds cells
// proportional to its circumference, so cells stay square from pole to pole
// and there is no face, corner or seam anywhere. More rings = more, finer
// stars; total cells is roughly rings^2 * 4/pi.
uniform int star_rings : hint_range(8, 160) = 64;
uniform float star_coverage : hint_range(0.0, 1.0) = 0.34;
// Three tiers measured from the reference art, which runs a ladder rather
// than one colour scaled: cores are near-white with a pink cast (#fee1ea),
// mid tones are clearly rosy (#ba6976) and faint dots are deep plum-rose
// (#8c445c). The plum shift gets STRONGER as stars get dimmer, which is what
// ties the field to the background instead of dusting it with white specks.
//
// Pre-tonemap values, solved through the Filmic curve.
uniform vec3 star_core : source_color = vec3(1.05, 0.62, 0.74);
uniform vec3 star_mid : source_color = vec3(0.400, 0.175, 0.235);
uniform vec3 star_faint : source_color = vec3(0.150, 0.062, 0.098);
uniform float star_brightness : hint_range(0.0, 1.0) = 0.75;

// Share of stars drawn large (3x3 core, 8 arms) and medium (1 px core,
// 4 arms). The rest are single dots.
uniform float star_large_share : hint_range(0.0, 0.3) = 0.012;
uniform float star_medium_share : hint_range(0.0, 0.6) = 0.05;

// Arm length in PIXELS. The reference's big star runs its axis arms far out;
// long and thin is the whole character of the look.
uniform float twinkle_axis_pixels : hint_range(2.0, 128.0) = 22.0;
// Diagonals are shorter and drawn as a dashed run of single pixels.
uniform float twinkle_diagonal_pixels : hint_range(1.0, 64.0) = 9.0;
uniform float twinkle_medium_scale : hint_range(0.1, 1.0) = 0.42;

// Stars drift with the sky, far slower than the gas. A shared rotation for the
// whole field, so constellations hold their shape while the dome turns.
uniform float star_spin : hint_range(0.0, 0.05) = 0.004;

// --- Shooting stars ---------------------------------------------------------
// Modelled on a real meteor shower: mostly empty sky, then a streak that is
// gone almost before it registers.
uniform bool shooting_enabled = true;
// Seconds per slot. Several slots are checked each frame so shots can overlap
// and cluster the way real meteors do, instead of arriving strictly one at a
// time; rarity comes from `shooting_chance`, not from the slot spacing.
uniform float shooting_period : hint_range(0.5, 30.0) = 3.5;
// Odds a slot holds a shot at all. Low: this is the rarity dial.
uniform float shooting_chance : hint_range(0.0, 1.0) = 0.07;
// Seconds a shot takes to cross its arc. A blink: real meteors are gone in a
// fraction of a second, and anything slower reads as a drifting object.
uniform float shooting_duration : hint_range(0.03, 2.0) = 0.16;
// Trail length in pixels, measured backwards from the head.
uniform float shooting_pixels : hint_range(4.0, 128.0) = 18.0;
// How far across the sky a shot travels, in radians. Small: a meteor crosses
// a patch of sky, not the dome.
uniform float shooting_arc : hint_range(0.02, 1.0) = 0.13;
// How many slots are tested per frame. Higher lets more shots overlap, at a
// cost of that many extra hash evaluations per pixel.
uniform int shooting_slots : hint_range(1, 8) = 4;

// --- Layer 4: moon ----------------------------------------------------------
// Still in the sky: it keeps its place while the stars and clouds turn, and
// the scene's key light comes from exactly this direction (LightingRig).
uniform bool moon_enabled = true;
uniform vec3 moon_direction = vec3(0.3, 0.57, -0.76);
// Angular radius, in degrees. Far larger than the real moon's quarter degree:
// this is a drawn moon, and it has to carry its maria and craters as pixels.
uniform float moon_size : hint_range(0.5, 20.0) = 4.5;
// The off-white gold of the highlands and the grey of the maria. Pre-tonemap,
// and kept below the tonemapper's shoulder: brighter, and the Filmic curve
// flattens the two into one pale disc with no seas on it.
uniform vec3 moon_light : source_color = vec3(0.78, 0.68, 0.50);
uniform vec3 moon_shade : source_color = vec3(0.34, 0.31, 0.30);
uniform float moon_brightness : hint_range(0.0, 2.0) = 1.0;
// A faint stepped halo around the disc, in the sky's own dusty rose.
uniform vec3 moon_halo_color : source_color = vec3(0.30, 0.12, 0.15);
// The moon's glow: forward scattering like the sun's, far fainter.
uniform float moon_halo : hint_range(0.0, 1.0) = 0.3;
uniform float moon_scatter : hint_range(0.0, 0.99) = 0.85;
uniform int moon_craters : hint_range(0, 24) = 11;

// --- The day (DayCycle sets all of these every frame) -----------------------
// Colours are pre-tonemap values that land on screen as the spec's display
// colours (SkyPalette.BeforeTonemap).
uniform vec3 local_up = vec3(0.0, 1.0, 0.0);
uniform vec3 sun_direction = vec3(0.0, -1.0, 0.0);
// The sine of the planet's horizon below the level: the planet hides the sun,
// the moon and their glow below it.
uniform float horizon_sine = 0.0;
// The planet's axis and how far it has turned: the stars and nebulae turn
// with it.
uniform vec3 sky_axis = vec3(0.0, 1.0, 0.0);
uniform float sky_turn = 0.0;

// How much of the night's layers the day's gradient covers.
uniform float day_amount = 0.0;
uniform vec3 day_zenith = vec3(0.0);
uniform vec3 day_horizon = vec3(0.0);
uniform float star_visibility = 1.0;

uniform vec3 glow_color = vec3(1.0, 0.6, 0.4);
uniform float glow_strength = 0.0;
uniform vec3 purple_color = vec3(0.5, 0.3, 0.35);
uniform float purple_strength = 0.0;
uniform vec3 belt_color = vec3(0.8, 0.4, 0.4);
uniform vec3 shadow_color = vec3(0.2, 0.1, 0.25);
uniform float belt_strength = 0.0;

// --- The sun -------------------------------------------------------------------
uniform bool sun_enabled = true;
uniform vec3 sun_color = vec3(1.0, 0.9, 0.75);
// Angular radius in degrees. Larger than the real sun's quarter degree, as
// the moon is: a drawn sun, a few dozen pixels across.
uniform float sun_size : hint_range(0.2, 10.0) = 2.2;
// How bright the disc is, against the sky: well past the glow threshold, so
// the bloom picks it up and the tonemapper rolls it to a cream-white core.
uniform float sun_energy : hint_range(1.0, 100.0) = 14.0;
// The share of the radius over which the disc's edge softens.
uniform float sun_edge : hint_range(0.01, 0.5) = 0.15;
// Limb darkening, per channel: the edge is dimmer than the centre, a little
// more so in blue (measured solar values are about 0.6).
uniform vec3 sun_limb = vec3(0.55, 0.6, 0.68);
// Height over width: 0.83 setting, 1 overhead (DayCycle).
uniform float sun_flatten = 1.0;
// The glow: forward scattering (Cornette-Shanks), how peaked and how strong,
// and a wide haze that grows as the sun nears the horizon.
uniform float sun_scatter : hint_range(0.0, 0.99) = 0.76;
uniform float sun_glow : hint_range(0.0, 2.0) = 0.08;
uniform float sun_haze : hint_range(0.0, 2.0) = 0.35;
// The glare round the disc -- what a bloom would do, but worked out here so
// it lands on the pixel grid with everything else. Its brightness at the
// disc's edge (1 is the sky's own white), and its reach in sun radii.
uniform float sun_glare : hint_range(0.0, 4.0) = 0.9;
uniform float sun_glare_radius : hint_range(1.0, 8.0) = 1.8;

// ---------------------------------------------------------------------------
// Hashes: cheap deterministic pseudo-random, no texture lookups.
float hash31(vec3 p) {
    p = fract(p * 0.3183099 + vec3(0.71, 0.113, 0.419));
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

vec3 hash33(vec3 p) {
    return vec3(hash31(p), hash31(p + 19.19), hash31(p + 47.77));
}

float hash11(float n) {
    return fract(sin(n * 127.1) * 43758.5453);
}

// Value noise over 3D directions, so it tiles seamlessly on the sphere.
float value_noise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
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

// Fractal brownian motion. The octave count is a parameter because the domain
// warp calls this three extra times per pixel; at full depth that triples the
// cost of the most expensive part of the shader for detail the warp smears.
//
// NOTE the range: amplitudes sum to (1 - 0.5^octaves), so 2 octaves span
// 0..0.75 with mean ~0.38 and 5 octaves span 0..0.97 with mean ~0.48. Any
// threshold applied to the result must be centred on THAT, not on 0..1 —
// getting this wrong makes a threshold never (or always) fire.
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

// Rotate `v` about `axis` by `angle` (Rodrigues). Spins the nebula field
// rather than the sky, so stars stay fixed while gas drifts.
vec3 rotate_about(vec3 v, vec3 axis, float angle) {
    float c = cos(angle);
    float s = sin(angle);
    return v * c + cross(axis, v) * s + axis * dot(axis, v) * (1.0 - c);
}

// ---------------------------------------------------------------------------
// THE PIXEL GRID.
//
// Every layer works in this space: the view direction is projected onto its
// cube face, snapped to a cell centre, and unprojected. Two screen pixels
// inside the same cell get an identical direction and therefore an identical
// colour, which is what produces hard edges and flat blocks.
void to_pixel(vec3 dir, out vec3 snapped_dir, out vec2 cell, out float face) {
    vec3 a = abs(dir);
    vec2 uv;
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

    vec2 scaled = uv * pixel_grid;
    cell = floor(scaled);
    vec2 centre = (cell + 0.5) / pixel_grid;

    vec3 snapped;
    if (face == 0.0)      snapped = vec3(1.0, centre.x, centre.y);
    else if (face == 1.0) snapped = vec3(-1.0, centre.x, centre.y);
    else if (face == 2.0) snapped = vec3(centre.x, 1.0, centre.y);
    else if (face == 3.0) snapped = vec3(centre.x, -1.0, centre.y);
    else if (face == 4.0) snapped = vec3(centre.x, centre.y, 1.0);
    else                  snapped = vec3(centre.x, centre.y, -1.0);

    snapped_dir = normalize(snapped);
}

// ---------------------------------------------------------------------------
// LAYER 2 — nebulae.
//
// Clouds orbit `nebula_axis`, so from inside the sphere the field wheels
// slowly around one point: the eye of the hurricane. Shape comes from a
// domain-warped fbm whose warp also advances with time, which is what makes a
// cloud change form as it travels rather than sliding past as a rigid stamp.
//
// The density is then QUANTIZED into three flat tones. That step is the whole
// pixel-art look: the reference nebula is bands of solid colour with visible
// stepped contours, and any smooth ramp reads as an airbrush instead.
vec4 nebula(vec3 dir, float time) {
    vec3 axis = normalize(nebula_axis);

    // Rotating the LOOKUP direction backwards moves the clouds forwards.
    vec3 spun = rotate_about(dir, axis, -time * nebula_spin);
    vec3 p = spun * nebula_scale;

    // Domain warp: distort the lookup by another noise field, which turns
    // smooth blobs into the curled shapes that read as gas. Drifting the
    // warp's own sample point is what reshapes clouds as they travel.
    float drift = time * nebula_morph;
    vec3 warp = vec3(
        fbm(p + vec3(11.3, drift, 0.0), 2),
        fbm(p + vec3(27.1, 0.0, drift), 2),
        fbm(p + vec3(drift, 43.9, 0.0), 2));

    float density = fbm(p + warp * 1.4, 5);

    // Everything below the floor is empty sky.
    float t = (density - nebula_floor) / nebula_span;
    if (t <= 0.0) {
        return vec4(0.0);
    }
    t = min(t, 1.0);

    // Three flat bands, hard-edged: `step`, never `smoothstep`.
    vec3 tone = nebula_tone_1;
    tone = mix(tone, nebula_tone_2, step(0.34, t));
    tone = mix(tone, nebula_tone_3, step(0.68, t));

    // Coverage is stepped too, so a cloud's edge is a hard boundary rather
    // than a fade. Three alpha levels matching the three tone bands.
    float alpha = 0.34;
    alpha = max(alpha, step(0.34, t) * 0.67);
    alpha = max(alpha, step(0.68, t) * 1.0);

    return vec4(tone, alpha * nebula_opacity);
}

// ---------------------------------------------------------------------------
// LAYER 3 — stars.
//
// Placement uses a coarse cell grid holding at most one star each, so stars
// never clump the way per-pixel thresholding does. Work happens in PIXEL units
// so arm lengths are exact pixel counts, matching the reference where an axis
// arm is one pixel wide and dozens long.
//
// STARS SIT ON A SPHERE, NOT ON A CUBE.
//
// Every earlier version placed stars on the cube-face grid the pixel snapping
// uses, and every one showed the cube. Three separate leaks, each patched and
// each replaced by the next:
//
//   - keying cells by (cell, face) gave the six faces independent randomness,
//     so the pattern restarted at each edge;
//   - measuring arm length in face pixels clipped the arms of any star within
//     one arm-length of an edge, about 14% of the sky;
//   - enumerating candidate cells on the viewer's own face meant a star just
//     across an edge was never considered, so it rendered cut in half.
//
// The cube is also unevenly sized: cells shrink 2.17x from a face centre to a
// cube corner, so stars bunch there no matter how the lookup is written.
//
// This version has no faces at all. Stars live on a RING LATTICE: the sphere
// is cut into `star_rings` bands of equal angular height, and each band holds
// a number of cells proportional to its circumference, which makes every cell
// square to within 5% from pole to pole. Lookup is two divisions, neighbours
// are ring +/-1 and sector +/-1, and nothing anywhere branches on a face.
int ring_cells(int ring, float rings) {
    float theta = 3.14159265 * (float(ring) + 0.5) / rings;
    return max(1, int(floor(2.0 * rings * sin(theta) + 0.5)));
}

// Direction of a cell's centre, plus a deterministic jitter inside the cell.
vec3 ring_cell_dir(int ring, int sector, float rings, vec3 jitter) {
    float count = float(ring_cells(ring, rings));
    float theta = 3.14159265 * (float(ring) + 0.5 + jitter.x * 0.7) / rings;
    float phi = 6.28318531 * (float(sector) + 0.5 + jitter.y * 0.7) / count;
    float st = sin(theta);
    return vec3(st * cos(phi), st * sin(phi), cos(theta));
}

vec3 stars(vec3 dir, float time) {
    float rings = float(star_rings);
    float px_angle = 1.0 / pixel_grid;

    // Which ring and sector this direction falls in. No face, no projection.
    float theta = acos(clamp(dir.z, -1.0, 1.0));
    float phi = atan(dir.y, dir.x);
    if (phi < 0.0) {
        phi += 6.28318531;
    }

    int ring0 = int(floor(theta / 3.14159265 * rings));

    vec3 total = vec3(0.0);

    // Ring +/-1, sector +/-1. Nine candidates, the same cost the old face
    // search paid, but the neighbourhood is continuous across the whole sphere.
    for (int dr = -1; dr <= 1; dr++) {
        int ring = ring0 + dr;
        if (ring < 0 || ring >= star_rings) {
            continue;   // past a pole; there is no ring beyond it
        }

        int count = ring_cells(ring, rings);
        int sector0 = int(floor(phi / 6.28318531 * float(count)));

        for (int ds = -1; ds <= 1; ds++) {
            // A ring with three or fewer sectors wraps onto itself under
            // +/-1, so the same cell would be evaluated twice or three times.
            // Skipping the duplicates is free; it does not change the image.
            if (ds != 0 && count <= 3) {
                continue;
            }

            // Sectors wrap around the ring, so a star at phi = 0 is a
            // neighbour of one at phi = 2pi. This is the wrap the cube grid
            // could never express.
            int sector = sector0 + ds;
            sector = int(mod(float(sector), float(count)));

            vec3 id = vec3(float(ring), float(sector), 0.0);
            vec3 random = hash33(id);

            if (random.x > star_coverage) {
                continue;
            }

            vec3 jitter = hash33(id + 3.3) - 0.5;
            vec3 star_dir = ring_cell_dir(ring, sector, rings, jitter);

            // Tangent frame around the star, built from whichever world axis
            // the star is LEAST aligned with. A fixed axis with a narrow guard
            // (only swapping within a couple of degrees of the pole) leaves
            // the cross product tiny for a wide band around it — |cross| is
            // still only 0.14 at eight degrees out — and the resulting frame
            // is badly conditioned, which drew oversized blobs near the poles.
            vec3 a3 = abs(star_dir);
            vec3 up = a3.x <= a3.y && a3.x <= a3.z ? vec3(1.0, 0.0, 0.0)
                    : a3.y <= a3.z ? vec3(0.0, 1.0, 0.0)
                    : vec3(0.0, 0.0, 1.0);
            vec3 tx = normalize(cross(up, star_dir));
            vec3 ty = cross(star_dir, tx);

            // Both the view direction and the star as INTEGER pixel counts in
            // that frame, so the offset is exact and stars stay one pixel
            // wide. Measuring a continuous angle and rounding afterwards
            // leaves the two grids offset and every small star smears into a
            // 2x2 block.
            vec2 view_px = floor(vec2(dot(dir, tx), dot(dir, ty)) / px_angle + 0.5);
            vec2 star_px = floor(vec2(dot(star_dir, tx), dot(star_dir, ty)) / px_angle + 0.5);
            vec2 delta = view_px - star_px;

            // Behind the star is a different part of the sky; without this a
            // star also draws at its antipode.
            if (dot(dir, star_dir) < 0.0) {
                continue;
            }

            float tier = hash31(id + 7.77);
            bool large = tier < star_large_share;
            bool medium = !large && tier < star_large_share + star_medium_share;

            float ax = abs(delta.x);
            float ay = abs(delta.y);
            float value = 0.0;

            if (large) {
                if (ax < 1.5 && ay < 1.5) {
                    // 3x3 core.
                    value = 1.0;
                } else {
                    // Axis arms: exactly ONE pixel wide and long. This is the
                    // signature of the reference art.
                    float arm = twinkle_axis_pixels;
                    if (ay < 0.5 && ax <= arm) {
                        value = max(value, 1.0 - ax / arm);
                    }
                    if (ax < 0.5 && ay <= arm) {
                        value = max(value, 1.0 - ay / arm);
                    }

                    // Diagonals: shorter, and DASHED — the reference draws
                    // them as separate squares with gaps, not solid lines.
                    float dlen = twinkle_diagonal_pixels;
                    if (abs(ax - ay) < 0.5 && ax <= dlen) {
                        float dash = mod(floor(ax), 2.0);
                        value = max(value, (1.0 - ax / dlen) * (1.0 - dash));
                    }
                }
            } else if (medium) {
                float arm = twinkle_axis_pixels * twinkle_medium_scale;
                if (ax < 0.5 && ay < 0.5) {
                    value = 1.0;
                } else if (random.z > 0.5) {
                    if (ay < 0.5 && ax <= arm) value = max(value, 1.0 - ax / arm);
                    if (ax < 0.5 && ay <= arm) value = max(value, 1.0 - ay / arm);
                } else {
                    float dlen = arm * 0.8;
                    if (abs(ax - ay) < 0.5 && ax <= dlen) {
                        float dash = mod(floor(ax), 2.0);
                        value = max(value, (1.0 - ax / dlen) * (1.0 - dash));
                    }
                }
            } else {
                // Small: a single pixel.
                if (ax < 0.5 && ay < 0.5) {
                    value = 1.0;
                }
            }

            if (value <= 0.0) {
                continue;
            }

            // Twinkle ONLY the small stars. On a large star the pulse swings a
            // 3x3 core plus eight long arms at once, which reads as the whole
            // sky flashing rather than as distant points scintillating.
            float pulse = 1.0;
            if (!large && !medium) {
                float phase = time * 0.8 + random.y * 6.2831;
                pulse = 0.7 + 0.3 * step(0.0, sin(phase));
            }

            // Quantize into four levels. Continuous falloff is what makes a
            // spike look airbrushed; the reference steps down in flat jumps.
            value = ceil(value * 4.0) / 4.0;

            // Three-stop ramp: faint -> mid -> core. A straight two-colour mix
            // runs through desaturated pinks and loses the plum cast that
            // keeps the field inside the background's palette.
            vec3 tint = value < 0.5
                ? mix(star_faint, star_mid, value * 2.0)
                : mix(star_mid, star_core, (value - 0.5) * 2.0);

            // Per-star magnitude, squared and stepped so most stars stay faint
            // and only a few reach the core colour — the reference's
            // distribution, where over half sit in the faintest tier.
            float mag_roll = random.y * random.y;
            float magnitude = 0.45 + 0.55 * floor(mag_roll * 4.0) / 3.0;

            total = max(total, tint * magnitude * pulse);
        }
    }

    return total * star_brightness;
}

// ---------------------------------------------------------------------------
// Shooting stars — a meteor shower.
//
// Time is cut into short slots and SEVERAL are tested every frame, so shots
// can overlap and cluster the way real meteors do rather than arriving one at
// a time on a metronome. Each slot either holds a shot or does not, decided by
// a hash, and `shooting_chance` is low enough that most slots are empty.
//
// A shot is fast: `shooting_duration` defaults to well under half a second,
// which is roughly how long a real meteor is visible. The trail is drawn only
// BEHIND the head with the same stepped falloff as a twinkle arm, brightest at
// the head and fading through the star ramp into the background — matching the
// reference art, where a streak is a one-pixel staircase running white to dark
// plum.
vec3 one_shot(vec3 dir, float time, float slot) {
    // Does this slot hold a shot?
    if (hash11(slot * 3.719) > shooting_chance) {
        return vec3(0.0);
    }

    // Start somewhere inside the slot rather than always at its top, so shots
    // do not land on a visible beat.
    float start = slot * shooting_period
        + hash11(slot * 8.233) * max(shooting_period - shooting_duration, 0.0);
    float t = (time - start) / shooting_duration;
    if (t < 0.0 || t > 1.0) {
        return vec3(0.0);
    }

    // A random great circle on the sphere, so a shot can appear anywhere and
    // travel any direction. Working in DIRECTION space rather than on a single
    // cube face matters: confining a shot to one face made five sixths of them
    // invisible from any given view, which is why none ever showed up.
    vec3 seed = hash33(vec3(slot, 5.1, 9.2));
    vec3 axis = normalize(seed * 2.0 - 1.0 + vec3(0.001));
    vec3 helper = abs(axis.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 u = normalize(cross(axis, helper));
    vec3 v = cross(axis, u);

    // Sweep an arc of that circle. Deliberately tiny: a meteor is a blink
    // across a small patch of sky, not a traverse of the dome.
    float arc = shooting_arc * mix(0.7, 1.4, hash11(slot * 6.77));
    float begin = hash11(slot * 2.13) * 6.2831;
    float angle = begin + t * arc;
    vec3 head = u * cos(angle) + v * sin(angle);

    // Direction of travel at the head, so the trail can be aimed opposite it.
    vec3 heading = normalize(-u * sin(angle) + v * cos(angle));

    // Angular half-width of one pixel, so the trail is one pixel thick and its
    // length is an honest pixel count wherever it lands on the sphere.
    float px_angle = 1.0 / pixel_grid;

    vec3 rel = dir - head;
    float along = dot(rel, heading);
    float across = length(rel - heading * along);

    // One pixel wide, and only behind the head.
    float tail = shooting_pixels * px_angle;
    if (across > px_angle * 0.9 || along > px_angle || along < -tail) {
        return vec3(0.0);
    }

    float value = 1.0 + along / tail;
    value = ceil(value * 4.0) / 4.0;

    // Fade in and out over the shot's life so it does not blink on and off at
    // full brightness. Stepped, like every other falloff here.
    float life = sin(t * 3.14159265);
    value *= ceil(clamp(life * 1.4, 0.0, 1.0) * 3.0) / 3.0;

    if (value <= 0.0) {
        return vec3(0.0);
    }

    // Same three-stop ramp as the stars, so a meteor reads as part of the same
    // palette rather than as a white scratch across the sky.
    return value < 0.5
        ? mix(star_faint, star_mid, value * 2.0)
        : mix(star_mid, star_core, (value - 0.5) * 2.0);
}

vec3 shooting_star(vec3 dir, float time) {
    float current = floor(time / shooting_period);

    // Test a few recent slots, not just the current one: a shot started in an
    // earlier slot may still be in flight, and overlapping shots are what make
    // it read as a shower rather than a queue.
    vec3 total = vec3(0.0);
    for (int i = 0; i < 8; i++) {
        if (i >= shooting_slots) {
            break;
        }
        total = max(total, one_shot(dir, time, current - float(i)));
    }

    return total * star_brightness;
}

// ---------------------------------------------------------------------------
// LAYER 4 — the moon.
//
// Drawn on its own pixel grid, square to the moon, with the grid's pixel as
// wide as the sky's: a disc whose every pixel is a flat tone. The disc is a
// sphere seen face on; its maria are a stepped noise over that sphere and its
// craters are round pits with a lit rim on the side the light comes from and
// a shadowed one opposite.

// The frame the moon is drawn in: `axis` toward it, `right` and `up` across.
void moon_frame(out vec3 axis, out vec3 right, out vec3 up) {
    axis = normalize(moon_direction);
    vec3 world_up = abs(axis.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    right = normalize(cross(world_up, axis));
    up = cross(axis, right);
}

// Where a view direction lands on the moon's disc, in moon radii, snapped to
// the moon's pixel grid. Far from the moon it is far outside the disc.
vec2 moon_disc(vec3 dir) {
    vec3 axis, right, up;
    moon_frame(axis, right, up);

    float along = dot(dir, axis);
    if (along <= 0.0) {
        return vec2(1e3);
    }

    float radius = radians(moon_size);
    vec2 q = vec2(dot(dir, right), dot(dir, up)) / (along * tan(radius));

    // As many pixels across a radius as the sky's own grid puts there.
    float per_radius = max(6.0, radius / (1.5707963 / pixel_grid));
    return (floor(q * per_radius) + 0.5) / per_radius;
}

// The moon's colour at a point on its disc (in moon radii, inside the disc).
vec3 moon_surface(vec2 q) {
    float r2 = dot(q, q);
    vec3 n = vec3(q, sqrt(max(1.0 - r2, 0.0)));

    // Maria: broad grey seas over the gold highlands, in two flat depths.
    // fbm's 4-octave range is 0..0.94 with mean ~0.47.
    float sea = fbm(n * 2.3 + vec3(3.1, 7.7, 1.9), 4);
    float mare = step(0.50, sea) * 0.55 + step(0.58, sea) * 0.25;
    vec3 tone = mix(moon_light, moon_shade, mare);

    // Craters: pits scattered over the face we see, each with a lit rim
    // toward the upper left, where the light is drawn from, and a shadowed
    // rim opposite.
    vec2 light = normalize(vec2(-1.0, 1.0));

    for (int i = 0; i < 24; i++) {
        if (i >= moon_craters) {
            break;
        }

        vec3 roll = hash33(vec3(float(i) * 3.7, 11.0, 5.0));
        vec3 centre = normalize(vec3(roll.xy * 1.6 - 0.8, 0.35 + roll.z));
        float size = 0.07 + 0.13 * hash31(vec3(float(i), 41.0, 2.0));
        float d = distance(n, centre);

        if (d < size) {
            vec2 toward = normalize((n - centre).xy + vec2(1e-5));
            float facing = dot(toward, light);
            float rim = step(size * 0.72, d);

            tone *= 0.78;
            tone = mix(tone, facing > 0.0 ? tone * 0.62 : moon_light * 1.08, rim * step(0.2, abs(facing)));
        }
    }

    // Limb darkening in two hard steps toward the edge.
    tone *= 1.0 - 0.12 * step(0.80, r2) - 0.10 * step(0.93, r2);

    return tone * moon_brightness;
}

// ---------------------------------------------------------------------------
// LAYER 3 — the day.
//
// Everything from DayCycle is ready to draw: colours and strengths for the
// current hour. What is worked out here is only WHERE: by the direction's
// elevation over the local horizon and its bearing from the sun, where the
// spec works by screen position.

// How far from the overhead colour to the horizon colour, for a height from
// 0 (overhead) to 1 (the horizon): the spec's stops (0 at 0, 0.38 at 0.55, 1
// at 1), joined by a smooth curve with no corner at any stop. It flattens out
// at the horizon, so holding it there below the horizon leaves no line.
float horizon_weight(float s) {
    s = clamp(s, 0.0, 1.0);
    // Hermite segments: level at the zenith, 1.5 steep through the middle stop,
    // level again at the horizon.
    const float steep = 1.5;
    if (s <= 0.55) {
        float t = s / 0.55;
        return 0.38 * (3.0 * t * t - 2.0 * t * t * t) + 0.55 * steep * (t * t * t - t * t);
    }
    float t = (s - 0.55) / 0.45;
    return 0.38 * (2.0 * t * t * t - 3.0 * t * t + 1.0)
        + 0.45 * steep * (t * t * t - 2.0 * t * t + t)
        + (3.0 * t * t - 2.0 * t * t * t);
}

// A smooth step between two values, flat at both ends.
float ease(float from, float to, float t) {
    return mix(from, to, smoothstep(0.0, 1.0, t));
}

// The point on the horizon beneath the sun.
vec3 under_sun(vec3 up) {
    vec3 level = sun_direction - up * dot(sun_direction, up);
    float length_level = length(level);
    return length_level > 1e-4 ? level / length_level : vec3(0.0);
}

vec3 day(vec3 view, vec3 night) {
    vec3 up = local_up;
    float up_sine = dot(view, up);
    float elevation = asin(clamp(up_sine, -1.0, 1.0));

    // The gradient: overhead colour at the zenith, horizon colour at the
    // horizon, deep for most of the sky and paling fast near the horizon.
    float w = horizon_weight(1.0 - elevation / 1.5707963);
    vec3 color = mix(night, mix(day_zenith, day_horizon, w), day_amount);

    vec3 beneath = under_sun(up);
    if (dot(beneath, beneath) < 0.5) {
        return color;
    }

    // Bearing from the sun, round the horizon (-pi..pi). It is undefined only
    // straight up and down, where every glow below has died away.
    vec3 side = cross(up, beneath);
    float bearing = atan(dot(view, side), dot(view, beneath));

    // The warm glow round the sun's side of the horizon: 75 degrees along it
    // and 35 up, hugging the horizon as a real one does. The spec's stops
    // (0.6 at the centre, 0.22 a third of the way out, nothing at the edge)
    // as one smooth bell.
    if (glow_strength > 0.001) {
        float a = length(vec2(bearing / radians(75.0), elevation / radians(35.0)));
        float alpha = 0.6 * exp(-pow(a / 0.34, 2.0));
        color = mix(color, glow_color, alpha * glow_strength);
    }

    // Purple light: a rosy disc 16 degrees up on the sun's side, fading
    // smoothly to nothing at 45 degrees out and below the horizon.
    if (purple_strength > 0.001) {
        vec3 centre = normalize(beneath * cos(radians(16.0)) + up * sin(radians(16.0)));
        float a = acos(clamp(dot(view, centre), -1.0, 1.0)) / radians(45.0);
        float alpha = 0.5 * purple_strength * (1.0 - smoothstep(0.0, 1.0, a))
            * smoothstep(radians(-4.0), radians(4.0), elevation);
        color = mix(color, purple_color, alpha);
    }

    // The Belt of Venus over the Earth's shadow: a band from the horizon to
    // 15 degrees up, opposite the sun, fading toward the sun's side. The
    // spec's stops, eased from one to the next so none shows as a line; past
    // the horizon it holds as it is there.
    if (belt_strength > 0.001) {
        float v = clamp(1.0 - degrees(elevation) / 15.0, 0.0, 1.0);
        vec3 tint = mix(belt_color, shadow_color, smoothstep(0.6, 0.85, v));
        float alpha = v < 0.35 ? ease(0.0, 0.50, v / 0.35)
            : v < 0.60 ? ease(0.50, 0.30, (v - 0.35) / 0.25)
            : v < 0.85 ? ease(0.30, 0.45, (v - 0.60) / 0.25)
            : ease(0.45, 0.60, (v - 0.85) / 0.15);
        alpha *= belt_strength * smoothstep(60.0, 120.0, degrees(abs(bearing)));
        color = mix(color, tint, alpha);
    }

    return color;
}

// ---------------------------------------------------------------------------
// LAYER 5 — the sun.
//
// Worked out as a real sun is seen -- then sampled on the sky's pixel grid,
// like everything else, which is the pixel filter.

// Cornette-Shanks phase function: how much light scatters toward the eye at
// an angle (cosine c) from a light, for haze with forward peak g.
float phase_cs(float c, float g) {
    float g2 = g * g;
    return 3.0 / (8.0 * 3.14159265) * ((1.0 - g2) * (1.0 + c * c))
        / ((2.0 + g2) * pow(1.0 + g2 - 2.0 * g * c, 1.5));
}

// Below the planet's horizon, the planet is in the way.
float above_horizon(vec3 view) {
    return smoothstep(horizon_sine - 0.004, horizon_sine + 0.002, dot(view, local_up));
}

// `view` is the direction on the pixel grid, for the disc; `exact` the true
// direction, for the glow, so it stays one smooth curve.
vec3 sun(vec3 view, vec3 exact, bool cubemap) {
    vec3 to_sun = normalize(sun_direction);
    float sun_up = dot(to_sun, local_up);
    float cosine = dot(view, to_sun);

    // The disc, in its own frame: flattened upright as it nears the horizon.
    vec3 upright = local_up - to_sun * sun_up;
    upright = dot(upright, upright) > 1e-6 ? normalize(upright) : normalize(cross(to_sun, vec3(1.0, 0.0, 0.0)));
    vec3 side = cross(to_sun, upright);
    vec3 offset = view - to_sun * cosine;
    vec2 q = vec2(dot(offset, side), dot(offset, upright) / sun_flatten) / tan(radians(sun_size));
    float r = length(q);

    float disc = cosine > 0.0 ? 1.0 - smoothstep(1.0 - sun_edge, 1.0, r) : 0.0;
    float mu = sqrt(max(0.0, 1.0 - min(r * r, 1.0)));
    vec3 limb = 1.0 - sun_limb * (1.0 - mu);

    // The glow: tight forward scattering, plus a wide haze that grows as the
    // sun gets low; it lingers a little after the disc has set. Not cut off
    // at the horizon: below it, the ground is in front of it anyway.
    float exact_cosine = dot(exact, to_sun);
    float angle = 2.0 * asin(clamp(length(exact - to_sun) * 0.5, 0.0, 1.0));
    float low = 1.0 - smoothstep(0.0, 0.35, sun_up);
    float glow = phase_cs(exact_cosine, sun_scatter) * sun_glow
        + sun_haze * low / (1.0 + pow(angle / 0.12, 2.0));
    glow *= smoothstep(-0.1, 0.02, sun_up);

    // Over-bright only for what is seen: in the ambient cubemap a blazing
    // disc would bleed into every shadow. The glare is a smooth bell from
    // the middle of the disc.
    float energy = cubemap ? 1.0 : sun_energy;
    float glare = cubemap ? 0.0 : sun_glare * exp(-pow(r / sun_glare_radius, 2.0) + pow(1.0 / sun_glare_radius, 2.0));
    return sun_color * (glow + glare) + sun_color * disc * energy * limb * above_horizon(view);
}

// ---------------------------------------------------------------------------
// Composite the layers, in order.
vec3 space(vec3 dir, float time, bool with_stars) {
    // Stars rotate with the sky, far slower than the gas. The direction is
    // spun BEFORE snapping, so the pixel grid and the star cells turn together
    // as one rigid field rather than stars sliding across a fixed grid. The
    // whole field also turns with the planet, as the sun does.
    vec3 turned = rotate_about(dir, normalize(sky_axis), sky_turn);
    vec3 star_dir = rotate_about(turned, normalize(nebula_axis), -time * star_spin);

    vec3 snapped;
    vec2 cell;
    float face;
    to_pixel(star_dir, snapped, cell, face);

    // LAYER 1 — flat ground.
    vec3 color = space_color * space_brightness;

    // LAYER 2 — gas, inside the background. Alpha-blended so the flat tones
    // sit ON the ground rather than adding to it, which would brighten the
    // background and wash out the banding.
    if (nebula_enabled) {
        vec4 gas = nebula(snapped, time);
        color = mix(color, gas.rgb * nebula_brightness, clamp(gas.a, 0.0, 1.0));
    }

    // LAYER 3 — the day, from the exact direction (unturned: the day's light
    // belongs to the sun, not to the stars). The discs below use the sky's
    // pixel grid.
    vec3 view;
    vec2 view_cell;
    float view_face;
    to_pixel(dir, view, view_cell, view_face);
    color = day(dir, color);

    // The moon, from the unturned direction: it stays opposite the sun while
    // the rest of the sky wheels past it. Hidden below the planet's horizon.
    float moon_seen = above_horizon(view);
    vec2 disc = moon_enabled ? moon_disc(dir) : vec2(1e3);
    float from_centre = length(disc);

    if (from_centre < 1.0 && moon_seen > 0.5) {
        return moon_surface(disc);
    }

    // Its glow: forward scattering like the sun's, far fainter, over the gas
    // and under the stars. Smooth, and not cut off at the horizon.
    if (moon_enabled) {
        float toward = dot(dir, normalize(moon_direction));
        color += (moon_light * 0.6 + moon_halo_color) * moon_brightness * moon_halo
            * phase_cs(toward, moon_scatter) * 0.25;
    }

    // LAYER 4 — stars, in front of the gas, fading by day.
    if (with_stars && star_visibility > 0.001) {
        vec3 shine = stars(snapped, time);
        if (shooting_enabled) {
            shine += shooting_star(snapped, time);
        }
        color += shine * star_visibility;
    }

    // LAYER 5 — the sun.
    if (sun_enabled) {
        color += sun(view, dir, !with_stars);
    }

    return color;
}

void sky() {
    vec3 dir = normalize(EYEDIR);

    // The radiance cubemap only feeds ambient light and reflections and is
    // heavily blurred by design — sampling it for the visible background turns
    // every star into a smudge. So the background is computed directly, and
    // the cubemap pass skips stars: they contribute almost nothing to ambient
    // light, and skipping them keeps the six-face bake cheap.
    COLOR = space(dir, TIME, !AT_CUBEMAP_PASS);
}
