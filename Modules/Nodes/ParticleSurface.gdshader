// The surface of particle nodes, in their material's colours (sand, for now).
//
// Drawn per pixel, because the mesh is a node (two units) apart at its finest
// and sand has to be far finer than that. Three layers, each only ever shading
// the base colour toward the shade colour:
//
//   1. GRAIN    fine speckle stepped into a few flat levels, so the ground
//               reads as granular pixel art rather than airbrush;
//   2. DRIFT    broad slow variation, so one patch is not the same tone as the
//               next all the way to the horizon;
//   3. RIPPLES  wind-blown banding: noise stretched across the wind.
//
// Everything is measured in WORLD UNITS on the planet's surface, so the pattern
// is the same size wherever the player stands and whatever the planet's radius.
// Noise is hashed on integer lattice cells rather than with sin(), which loses
// all precision at the coordinates a large planet's surface sits at.
shader_type spatial;
// Double-sided: see NodeMaterials.Raw for why.
render_mode diffuse_lambert, specular_disabled, cull_disabled;

uniform vec3 base_color : source_color = vec3(0.937, 0.890, 0.875);
uniform vec3 shade_color : source_color = vec3(0.824, 0.741, 0.741);

// Grains per world unit, and how strongly they show.
uniform float grain_scale : hint_range(1.0, 40.0) = 10.0;
uniform float grain : hint_range(0.0, 1.0) = 0.5;
uniform int grain_levels : hint_range(2, 12) = 4;

// Broad variation: cycles per world unit.
uniform float drift_scale : hint_range(0.001, 1.0) = 0.05;
uniform float drift : hint_range(0.0, 1.0) = 0.3;

// Ripples: bands per world unit across the wind, stretched along it.
uniform float ripple_scale : hint_range(0.05, 8.0) = 0.8;
uniform float ripple_stretch : hint_range(1.0, 30.0) = 8.0;
uniform float ripples : hint_range(0.0, 1.0) = 0.3;
uniform float ripple_sharpness : hint_range(1.0, 8.0) = 2.2;

// Handing over to the far terrain: between these distances from the camera
// the surface dissolves away, pixel by pixel, on the same pattern the far
// terrain dissolves in on (and the raw nodes' material fades by). Off while
// handover_end is 0.
uniform float handover_start = 0.0;
uniform float handover_end = 0.0;

varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// PCG-style integer hash of a lattice cell, to 0..1.
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

// Value noise: stable random values on the lattice, smoothed between corners.
float value_noise(vec3 at) {
    vec3 floored = floor(at);
    ivec3 cell = ivec3(floored);
    vec3 t = at - floored;
    t = t * t * (3.0 - 2.0 * t);

    float n000 = hash(cell);
    float n100 = hash(cell + ivec3(1, 0, 0));
    float n010 = hash(cell + ivec3(0, 1, 0));
    float n110 = hash(cell + ivec3(1, 1, 0));
    float n001 = hash(cell + ivec3(0, 0, 1));
    float n101 = hash(cell + ivec3(1, 0, 1));
    float n011 = hash(cell + ivec3(0, 1, 1));
    float n111 = hash(cell + ivec3(1, 1, 1));

    return mix(
        mix(mix(n000, n100, t.x), mix(n010, n110, t.x), t.y),
        mix(mix(n001, n101, t.x), mix(n011, n111, t.x), t.y),
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

void fragment() {
    if (handover_end > 0.0) {
        // The engine's own distance-fade test (interleaved gradient noise),
        // reversed: whole near, gone past handover_end.
        float fade = clamp(smoothstep(handover_end, handover_start, length(VERTEX)), 0.0, 1.0);
        float threshold = fract(52.9829189 * fract(dot(FRAGCOORD.xy, vec2(0.06711056, 0.00583715))));
        if (fade < 0.001 || fade < threshold) {
            discard;
        }
    }

    // The planet is centred on the world origin, so up is simply outward.
    vec3 up = normalize(world_pos);

    float broad = fbm(world_pos * drift_scale, 3);

    // Ripples: sample through a frame stretched along one surface direction,
    // so round noise comes out as long parallel bands.
    vec3 axis = abs(up.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 along = normalize(cross(axis, up));
    vec3 rippled = world_pos * ripple_scale
        - along * dot(world_pos, along) * ripple_scale * (1.0 - 1.0 / ripple_stretch);
    float band = pow(clamp(fbm(rippled, 2), 0.0, 1.0), ripple_sharpness);

    float speck = value_noise(world_pos * grain_scale);
    speck = floor(speck * float(grain_levels)) / float(grain_levels - 1);

    float shade = broad * drift + band * ripples + speck * grain;
    shade /= max(drift + ripples + grain, 0.0001);

    ALBEDO = mix(base_color, shade_color, clamp(shade, 0.0, 1.0));
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;
}
