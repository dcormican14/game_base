// Procedural deep-space sky in the style of the reference pixel art, built as
// three layers that composite in order:
//
//   1. SPACE    flat plum ground (#28061e halved toward black)
//   2. NEBULAE  clouds quantized into a few flat tones with hard stepped
//               edges, orbiting one axis like a very slow hurricane
//   3. STARS    three sizes with drawn twinkle arms, plus rare shooting stars
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

// --- Layer 2: nebulae -------------------------------------------------------
uniform bool nebula_enabled = true;
// The reference nebula is a handful of FLAT tones with visible stepped
// contours, like a topographic map — not a gradient. These three are sampled
// from it (#300c2a, #3c1230, #4e1836), brightest last.
uniform vec3 nebula_tone_1 : source_color = vec3(0.115, 0.020, 0.093);
uniform vec3 nebula_tone_2 : source_color = vec3(0.180, 0.035, 0.130);
uniform vec3 nebula_tone_3 : source_color = vec3(0.320, 0.060, 0.180);
// A warm accent for a minority of clouds, from the reference's rusty patches.
uniform vec3 nebula_warm : source_color = vec3(0.300, 0.085, 0.090);
uniform float nebula_warm_share : hint_range(0.0, 1.0) = 0.06;
uniform float nebula_opacity : hint_range(0.0, 2.0) = 1.0;
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
uniform float star_brightness : hint_range(0.0, 4.0) = 1.0;

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
// getting this wrong is what made the warm nebula tint never appear.
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

    // A minority of clouds take the warm accent instead. Thresholded on fbm's
    // real 2-octave range (mean ~0.38), not on 0..1.
    // fbm's 2-octave range is 0..0.75 with mean ~0.38, so the threshold sits
    // well above the mean to keep the warm tint to a minority of clouds.
    float warm = fbm(p * 0.6 + 5.5, 2);
    float warm_mix = step(0.56 - nebula_warm_share * 0.3, warm);
    tone = mix(tone, nebula_warm, warm_mix * 0.55);

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
// Composite the three layers, in order.
vec3 space(vec3 dir, float time, bool with_stars) {
    // Stars rotate with the sky, far slower than the gas. The direction is
    // spun BEFORE snapping, so the pixel grid and the star cells turn together
    // as one rigid field rather than stars sliding across a fixed grid.
    vec3 star_dir = rotate_about(dir, normalize(nebula_axis), -time * star_spin);

    vec3 snapped;
    vec2 cell;
    float face;
    to_pixel(star_dir, snapped, cell, face);

    // LAYER 1 — flat ground.
    vec3 color = space_color;

    // LAYER 2 — gas, inside the background. Alpha-blended so the flat tones
    // sit ON the ground rather than adding to it, which would brighten the
    // background and wash out the banding.
    if (nebula_enabled) {
        vec4 gas = nebula(snapped, time);
        color = mix(color, gas.rgb, clamp(gas.a, 0.0, 1.0));
    }

    // LAYER 3 — stars, in front of the gas.
    if (with_stars) {
        color += stars(snapped, time);
        if (shooting_enabled) {
            color += shooting_star(snapped, time);
        }
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
