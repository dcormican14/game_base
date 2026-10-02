// The far terrain: the ground beyond the loaded chunks, out to the horizon.
//
// It has to pass for the real ground, so it borrows its look:
//   SAND   the particle surface's own layers -- grain, drift, ripples -- as
//          it draws them, fading to their average further out, where grain
//          would be finer than a pixel and only shimmer;
//   ROCK   the raw nodes' two stone shades, faceted: lit by each triangle's
//          flat normal, and shaded cell by cell on the nodes' own two-unit
//          lattice, as real rock's Voronoi cells are.
// A vertex says which it is (COLOR.r: 1 rock, 0 sand); the boundary between
// them is kept crisp rather than blended.
//
// BEHIND THE REAL GROUND. Every vertex is pushed a little further from the
// camera along its line of sight (depth_push): it lands on the very same
// pixel, only deeper. Wherever the real ground is drawn it wins, and wherever
// it is not -- dissolved away, or a hairline seam, or a hollow between rock
// nodes -- this shows through instead of the sky. Freshly drawn chunks are
// never poked through by the old ground.
//
// HANDOVER. Near the camera the real chunks draw the ground; between
// handover_start and handover_end they dissolve away with distance (see
// NodeMaterials), revealing this whole underneath, so neither ground has an
// edge. A block that stands in for drawn chunks is drawn with handover set, cut
// away nearer than handover_start, where the real ground is solid -- so it
// can never cover a pit dug there.
//
// MORPH. Each vertex slides onto where the next coarser level puts the
// ground (CUSTOM0.xyz its offset, CUSTOM1.xyz the normal there; see
// FarMesher), between two distances from the camera that are the same for
// the whole block (CUSTOM0.w and CUSTOM1.w). So a block meets a coarser
// neighbour at the same height, with no ledge between levels.
//
// HAZE thickens with distance so the horizon sits back.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, cull_back, skip_vertex_transform;

uniform vec3 sand_color : source_color = vec3(0.937, 0.890, 0.875);
uniform vec3 sand_shade : source_color = vec3(0.824, 0.741, 0.741);
uniform vec3 rock_light : source_color = vec3(0.357, 0.267, 0.329);
uniform vec3 rock_dark : source_color = vec3(0.271, 0.192, 0.247);

// The particle surface's layers (see ParticleSurface.gdshader).
uniform float grain_scale = 10.0;
uniform float grain = 0.5;
uniform int grain_levels = 4;
uniform float drift_scale = 0.05;
uniform float drift = 0.3;
uniform float ripple_scale = 0.8;
uniform float ripple_stretch = 8.0;
uniform float ripples = 0.3;
uniform float ripple_sharpness = 2.2;

// The rock mosaic (see fragment): the nodes' lattice and how far sites
// stray from it, in nodes, and how far each cell's light is tilted.
uniform float node_size = 2.0;
uniform float site_jitter = 0.5;
uniform float facet_tilt = 1.6;

// Where the fine layers give way to their average.
uniform float detail_start = 210.0;
uniform float detail_end = 450.0;

uniform float handover_start = 110.0;
uniform float handover_end = 210.0;

// How much deeper than itself the far terrain is drawn (see BEHIND).
uniform float depth_push = 2.5;

uniform vec3 haze_color : source_color = vec3(0.239, 0.063, 0.188);
uniform float haze_start = 350.0;
uniform float haze_end = 4000.0;
uniform float haze_amount : hint_range(0.0, 1.0) = 0.45;

// Set on the second of the two materials FarTerrain draws with, the one for
// blocks standing in for drawn chunks (see HANDOVER). No per-instance values:
// the engine sets aside sixteen slots for every instance that has any, and
// its room runs out at a few thousand -- fewer blocks than an island zone
// draws.
uniform bool handover = false;

varying vec3 world_pos;
varying float rock;

float hash(ivec3 cell) {
    uvec3 v = uvec3(cell) * uvec3(1664525u) + uvec3(1013904223u);
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    return float(v.x & 0xFFFFu) / 65535.0;
}

float value_noise(vec3 at) {
    vec3 floored = floor(at);
    ivec3 cell = ivec3(floored);
    vec3 t = at - floored;
    t = t * t * (3.0 - 2.0 * t);

    return mix(
        mix(mix(hash(cell), hash(cell + ivec3(1, 0, 0)), t.x),
            mix(hash(cell + ivec3(0, 1, 0)), hash(cell + ivec3(1, 1, 0)), t.x), t.y),
        mix(mix(hash(cell + ivec3(0, 0, 1)), hash(cell + ivec3(1, 0, 1)), t.x),
            mix(hash(cell + ivec3(0, 1, 1)), hash(cell + ivec3(1, 1, 1)), t.x), t.y),
        t.z);
}

float fbm(vec3 at, int octaves) {
    float total = 0.0;
    float weight = 0.0;
    float amplitude = 1.0;

    for (int i = 0; i < octaves; i++) {
        total += value_noise(at) * amplitude;
        weight += amplitude;
        at *= 2.13;
        amplitude *= 0.5;
    }

    return total / max(weight, 0.0001);
}

void vertex() {
    float seen = distance((MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz, CAMERA_POSITION_WORLD);
    float morph = smoothstep(CUSTOM0.w, CUSTOM1.w, seen);
    VERTEX += CUSTOM0.xyz * morph;
    NORMAL = normalize(mix(NORMAL, CUSTOM1.xyz, morph));

    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    rock = COLOR.r;

    VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    NORMAL = normalize((MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);

    // Along the line of sight: the same pixel, only further away.
    float along = length(VERTEX);
    if (along > 0.001) {
        VERTEX *= (along + depth_push) / along;
    }
}

void fragment() {
    float distance = length(VERTEX) - depth_push;

    // Where the real ground is solid, it alone draws.
    if (handover && distance < handover_start) {
        discard;
    }

    float detail = 1.0 - smoothstep(detail_start, detail_end, distance);

    if (rock > 0.5) {
        vec3 flat_normal = normalize(cross(dFdx(VERTEX), dFdy(VERTEX)));
        if (dot(flat_normal, -VERTEX) < 0.0) {
            flat_normal = -flat_normal;
        }

        // Real rock is a mosaic of nodes -- Voronoi cells round sites jittered
        // off a two-unit lattice -- each lit by its own faces and in one of
        // two shades. So is this, close enough to stand beside it: the cell
        // a pixel falls in, found as the grid finds it, tilts the light it
        // gets and picks its shade. Further out, where cells are finer than a
        // pixel, the mosaic gives way to its average.
        if (detail > 0.0) {
            vec3 at = world_pos / node_size;
            ivec3 base = ivec3(round(at));
            float best = 1e9;
            ivec3 owner = base;
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++) {
                ivec3 cell = base + ivec3(x, y, z);
                vec3 site = vec3(cell) + (vec3(hash(cell), hash(cell + ivec3(71, 0, 0)), hash(cell + ivec3(0, 71, 0))) - 0.5) * site_jitter;
                vec3 off = at - site;
                float d = dot(off, off);
                if (d < best) {
                    best = d;
                    owner = cell;
                }
            }

            vec3 tilt = vec3(hash(owner + ivec3(13, 0, 0)), hash(owner + ivec3(0, 13, 0)), hash(owner + ivec3(0, 0, 13))) - 0.5;
            vec3 tilted = normalize(flat_normal + (VIEW_MATRIX * vec4(tilt, 0.0)).xyz * facet_tilt);
            NORMAL = normalize(mix(flat_normal, tilted, detail));

            // Two shades, as the nodes have, and now and then a face turned
            // from the light, as a node's side often is.
            float pick = hash(owner + ivec3(5, 5, 5));
            vec3 shade = pick > 0.55 ? rock_light : pick > 0.2 ? rock_dark : rock_dark * 0.65;
            ALBEDO = mix(mix(rock_light, rock_dark, 0.5), shade, detail);
        } else {
            NORMAL = flat_normal;
            ALBEDO = mix(rock_light, rock_dark, 0.5);
        }
    } else {
        vec3 up = normalize(world_pos);
        float broad = fbm(world_pos * drift_scale, 3);

        // The particle surface's ripples and grain close up; their averages
        // (what they come to over many pixels) further out.
        float band = 0.066;
        float speck = 0.5;
        if (detail > 0.0) {
            vec3 axis = abs(up.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
            vec3 along = normalize(cross(axis, up));
            vec3 rippled = world_pos * ripple_scale
                - along * dot(world_pos, along) * ripple_scale * (1.0 - 1.0 / ripple_stretch);
            float near_band = pow(clamp(fbm(rippled, 2), 0.0, 1.0), ripple_sharpness);
            float near_speck = value_noise(world_pos * grain_scale);
            near_speck = floor(near_speck * float(grain_levels)) / float(grain_levels - 1);
            band = mix(band, near_band, detail);
            speck = mix(speck, near_speck, detail);
        }

        float shade = broad * drift + band * ripples + speck * grain;
        shade /= max(drift + ripples + grain, 0.0001);
        ALBEDO = mix(sand_color, sand_shade, clamp(shade, 0.0, 1.0));
    }

    ROUGHNESS = 1.0;
    SPECULAR = 0.0;

    float haze = smoothstep(haze_start, haze_end, distance) * haze_amount;
    FOG = vec4(haze_color, haze);
}
