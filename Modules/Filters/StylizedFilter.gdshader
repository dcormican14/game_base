// Screen-space stylization: outlines, pixelation and ordered dithering.
//
// Runs on a full-screen quad drawn after everything else. Depth and
// normal-roughness are only exposed to `spatial` shaders, hence this is a
// spatial shader whose vertex stage forces the mesh to cover the screen
// rather than a ColorRect.
//
// Effects are individually toggleable; each costs nothing when off.
shader_type spatial;
render_mode unshaded, fog_disabled, depth_draw_never, depth_test_disabled, cull_disabled;

uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_nearest;
uniform sampler2D depth_texture : hint_depth_texture, repeat_disable, filter_nearest;
uniform sampler2D normal_texture : hint_normal_roughness_texture, repeat_disable, filter_nearest;

// --- Pixelation -------------------------------------------------------------
uniform bool pixelate_enabled = false;
// Height of the virtual low-res buffer at the NEAR end; width follows the
// screen's aspect so pixels stay square. Higher = finer pixels.
uniform int pixel_resolution : hint_range(60, 1080) = 320;

// Leave the sky at full resolution. The skybox is already drawn as pixel art
// at its own grid, so pixelating it again resamples one pixel grid onto
// another — which beats the stars into aliased noise that crawls as the camera
// turns. Objects are what the filter is for.
uniform bool pixelate_sky = false;
// Depth beyond which a pixel counts as sky. Reverse-Z puts the sky at the far
// plane, far past any real geometry, so this only has to sit above the
// furthest object.
uniform float sky_depth : hint_range(50.0, 5000.0) = 300.0;

// Distant objects get FINER pixels, so the jump between a chunky foreground
// and a distant one is less dramatic. `pixel_far_scale` is how many times
// finer the grid gets by `pixel_far_distance`; 1.0 disables the ramp.
uniform float pixel_far_scale : hint_range(1.0, 6.0) = 2.2;
uniform float pixel_near_distance : hint_range(0.5, 40.0) = 3.0;
uniform float pixel_far_distance : hint_range(2.0, 200.0) = 22.0;
// Number of discrete grid sizes between near and far. The ramp is QUANTIZED
// rather than continuous: a grid that changes smoothly with depth slides its
// pixel boundaries as the camera moves, and the whole image shimmers. Stepping
// it means a surface keeps one pixel size until it crosses a band edge.
uniform int pixel_distance_steps : hint_range(1, 12) = 4;

// --- Outlines ---------------------------------------------------------------
uniform bool outline_enabled = true;
uniform vec4 outline_color : source_color = vec4(0.05, 0.05, 0.07, 1.0);
uniform float outline_thickness : hint_range(1.0, 4.0) = 1.0;
// Depth discontinuity needed for an edge, scaled by distance so distant
// geometry does not smear into a solid outline.
uniform float depth_threshold : hint_range(0.01, 2.0) = 0.18;
// Cap on the grazing-angle compensation below. Higher tolerates steeper
// viewing angles before flat surfaces start reading as edges; surfaces seen
// closer to edge-on than 1/grazing_boost are suppressed outright (they are
// sub-pixel slivers by then).
uniform float grazing_boost : hint_range(1.0, 64.0) = 32.0;
// Normal difference that counts as a crease. Tuned so 90-degree flat-face
// creases (1.41) pass while smooth curvature on limbs/torso (<=1.1) does not.
uniform float normal_threshold : hint_range(0.1, 1.5) = 1.1;
// Lines are full-strength ink up to this distance...
uniform float outline_full_distance = 12.0;
// ...then thin out steadily, disappearing at this distance (0 disables both).
uniform float outline_max_distance = 60.0;
// Interior (normal-derived) lines fade out much earlier: on small distant
// geometry every crease line survives at full 1-pixel width, and the clumped
// lines otherwise merge into dark masses that crawl with the camera.
uniform float normal_max_distance = 35.0;

// --- Dither -----------------------------------------------------------------
uniform bool dither_enabled = false;
uniform float dither_strength : hint_range(0.0, 0.1) = 0.02;

void vertex() {
    // Cover the viewport regardless of the mesh's transform.
    POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

// View-space depth in metres, positive going into the screen.
//
// Forward+/Mobile render with Vulkan NDC: z spans [0, 1] and is reverse-Z
// (1.0 = near plane, 0.0 = far/sky). INV_PROJECTION_MATRIX is built for that
// exact convention, so the raw texture value is used as ndc.z untouched —
// per the 4.x "Advanced post-processing" docs. Remapping it OpenGL-style
// ((1-d)*2-1) puts near geometry outside the matrix's z-range and reads the
// sky as the NEAR plane, silently corrupting every depth comparison.
float linear_depth(vec2 uv, mat4 inv_projection) {
    float depth = textureLod(depth_texture, uv, 0.0).r;
    vec4 view = inv_projection * vec4(uv * 2.0 - 1.0, depth, 1.0);
    return -(view.xyz / view.w).z;
}

vec3 view_normal(vec2 uv) {
    return textureLod(normal_texture, uv, 0.0).xyz * 2.0 - 1.0;
}

// 4x4 Bayer matrix, returns 0..1.
float bayer(vec2 pixel) {
    int x = int(mod(pixel.x, 4.0));
    int y = int(mod(pixel.y, 4.0));
    int index = x + y * 4;
    float m[16] = {
         0.0,  8.0,  2.0, 10.0,
        12.0,  4.0, 14.0,  6.0,
         3.0, 11.0,  1.0,  9.0,
        15.0,  7.0, 13.0,  5.0
    };
    return m[index] / 16.0;
}

void fragment() {
    vec2 uv = SCREEN_UV;
    vec2 texel = 1.0 / VIEWPORT_SIZE;
    vec2 dither_coord = FRAGCOORD.xy;

    // Snap the sampling UV to a coarse grid. Sampling every buffer through the
    // snapped UV keeps outlines aligned to the same grid as the colour, so the
    // whole image reads as one low-res picture rather than crisp lines over a
    // blocky background.
    if (pixelate_enabled) {
        // Classify with depth at the TRUE uv, before any snapping. Reading it
        // through the snapped uv instead lets a pixel near a silhouette land
        // on a sky texel (or the reverse), so objects would sample the sky's
        // grid along every edge.
        float raw_here = textureLod(depth_texture, uv, 0.0).r;
        vec4 vp_here = INV_PROJECTION_MATRIX * vec4(uv * 2.0 - 1.0, raw_here, 1.0);
        float depth_here = -(vp_here.xyz / vp_here.w).z;

        bool is_sky = depth_here >= sky_depth;

        if (!is_sky || pixelate_sky) {
            float rows = float(pixel_resolution);

            if (pixel_far_scale > 1.0 && !is_sky) {
                // Finer grid with distance, in discrete bands. Smooth
                // interpolation here would drift pixel boundaries every frame
                // the camera moves; banding keeps a surface on one grid until
                // it crosses an edge.
                float span = max(pixel_far_distance - pixel_near_distance, 0.001);
                float t = clamp((depth_here - pixel_near_distance) / span, 0.0, 1.0);
                float steps = float(pixel_distance_steps);
                t = floor(t * steps) / steps;
                rows *= mix(1.0, pixel_far_scale, t);
            }

            vec2 grid = vec2(floor(rows * (VIEWPORT_SIZE.x / VIEWPORT_SIZE.y)), rows);
            dither_coord = floor(uv * grid);
            uv = (dither_coord + 0.5) / grid;
            texel = 1.0 / grid;
        }
    }

    vec3 color = textureLod(screen_texture, uv, 0.0).rgb;

    if (outline_enabled) {
        vec2 offset = texel * outline_thickness;

        // Roberts cross: compare opposite diagonal neighbours. Cheaper than a
        // full Sobel and gives cleaner one-pixel lines.
        vec2 d1 = vec2(offset.x, offset.y);
        vec2 d2 = vec2(offset.x, -offset.y);

        // Reconstruct the centre pixel's view-space position: its depth, and
        // the actual ray this pixel looks along (NOT the camera's forward
        // axis — the difference matters enormously near the screen edges and
        // on surfaces close to eye level).
        float center_raw = textureLod(depth_texture, uv, 0.0).r;
        vec4 vp = INV_PROJECTION_MATRIX * vec4(uv * 2.0 - 1.0, center_raw, 1.0);
        vec3 view_pos = vp.xyz / vp.w;
        float center_depth = -view_pos.z;
        vec3 view_ray = normalize(view_pos);

        vec3 center_normal = view_normal(uv);
        float depth_tl = linear_depth(uv - d1, INV_PROJECTION_MATRIX);
        float depth_br = linear_depth(uv + d1, INV_PROJECTION_MATRIX);
        float depth_tr = linear_depth(uv + d2, INV_PROJECTION_MATRIX);
        float depth_bl = linear_depth(uv - d2, INV_PROJECTION_MATRIX);

        float depth_delta = length(vec2(depth_br - depth_tl, depth_tr - depth_bl));

        // Threshold scales with distance (a 20cm step matters up close, is
        // noise at 50m) AND with the true grazing angle: on a surface seen
        // nearly edge-on — open floor stretching away, or a rooftop at eye
        // level — depth changes by metres between adjacent pixels even though
        // the surface is perfectly flat. The angle must be measured against
        // this pixel's VIEW RAY; using the camera's forward axis instead
        // under-boosts any surface near eye level or near the screen edges,
        // which then fills solid ('large black blocks' that sweep around as
        // the camera turns). With the per-ray angle, a flat surface's
        // delta-to-threshold ratio is constant in both distance and angle, so
        // flat geometry can never fire while real steps still do.
        float n_dot_ray = abs(dot(center_normal, view_ray));
        float facing = max(n_dot_ray, 1.0 / grazing_boost);
        float depth_edge = step(depth_threshold * max(center_depth, 1.0) * 0.1 / facing, depth_delta);
        // Steeper than the cap = a sub-pixel sliver; suppress rather than fill.
        depth_edge *= smoothstep(0.5 / grazing_boost, 1.0 / grazing_boost, n_dot_ray);
        // Ink the foreground side of a discontinuity only. The cross fires on
        // both sides of a step, doubling line width; keeping the nearer side
        // halves thickness and makes the line hug the object it belongs to.
        float nearest_neighbor = min(min(depth_tl, depth_br), min(depth_tr, depth_bl));
        depth_edge *= step(center_depth, nearest_neighbor + max(center_depth * 0.03, 0.08));

        vec3 n_tl = view_normal(uv - d1);
        vec3 n_br = view_normal(uv + d1);
        vec3 n_tr = view_normal(uv + d2);
        vec3 n_bl = view_normal(uv - d2);

        // Interior lines from normals need care on smooth meshes: at low
        // sampling resolution a curved limb bends its normal as fast as a real
        // crease does, so a naive test fills the whole character with outline
        // colour. Two defences:
        //  - use the WORST single pair, not the sum (halves curvature response
        //    while a true two-face crease keeps its full 1.41);
        //  - require depth continuity across all four samples. A crease joins
        //    two surfaces continuously; on thin limbs the diagonal samples
        //    fall off the limb entirely, so the fill is suppressed and the
        //    silhouette is left to the depth test where it belongs.
        float normal_delta = max(length(n_br - n_tl), length(n_tr - n_bl));
        float coherence_tolerance = max(center_depth * 0.03, 0.08);
        bool depth_continuous = abs(depth_tl - center_depth) < coherence_tolerance
            && abs(depth_br - center_depth) < coherence_tolerance
            && abs(depth_tr - center_depth) < coherence_tolerance
            && abs(depth_bl - center_depth) < coherence_tolerance;
        float normal_edge = depth_continuous ? step(normal_threshold, normal_delta) : 0.0;
        // Interior detail lines retire early; silhouettes carry the distance.
        normal_edge *= 1.0 - smoothstep(normal_max_distance * 0.6, normal_max_distance, center_depth);

        float edge = max(depth_edge, normal_edge);

        // Distance thinning: full ink close up, then a long steady lightening
        // until lines vanish at outline_max_distance. Distant lines read as
        // finer strokes instead of staying full weight until they pop out.
        // This also suppresses the sky, whose depth linearizes to the far
        // plane, well beyond any real geometry.
        if (outline_max_distance > 0.0) {
            edge *= 1.0 - smoothstep(outline_full_distance, outline_max_distance, center_depth);
        }

        color = mix(color, outline_color.rgb, edge * outline_color.a);
    }

    if (dither_enabled) {
        // Dither per VIRTUAL pixel while pixelating, so the pattern is part of
        // the low-res image instead of fine noise inside each chunky pixel.
        color += (bayer(dither_coord) - 0.5) * dither_strength;
    }

    ALBEDO = color;
}
