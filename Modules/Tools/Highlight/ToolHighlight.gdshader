// A tool's highlight: the outline bars, and the glow on its target.
//
// One shader for both. With `solid` on it draws flat colour -- the outline.
// Otherwise it draws the glow, and everything in the glow works IN FROM THE
// OUTLINE: a thin translucent gradient strongest at the outline and fading
// quickly toward the middle of the face, a pulse that travels inward through
// it, and small square particles that glide in from the outline and fade as
// they near the middle. The feel is precise, high-tech machinery that now and
// then glitches: a particle may be a short chain of squares, and once in a
// while one stutters -- two or three quick jumps -- before carrying on.
//
// Drawn AFTER the stylised filter (render priority above it), so it is not
// pixelated: that is what lets the outline stay thin and the particles be a
// pixel or two across. It is still depth tested against the scene, pulled a
// little toward the camera so it wins against the surface it lies on.
//
// Mesh coordinates, in world units (see HighlightBuilder):
//   UV   (distance along the outline, distance in from it)
//   UV2  (distance in from the outline, how far in the middle is)
//
// DISC MODE (`disc_mode` on) draws a brush instead: a thin skin laid flush on
// the ground, whose UV is a position IN THE DISC -- a flat circle's own x and
// y, wrapped onto the surface like a sticker (see SurfaceDisc). Everything is
// drawn from that position, mathematically: the circles, the gradient and the
// particles. Every fragment is first snapped to the stylised filter's own
// pixel grid, the same grid the world is drawn on, so the circles come out as
// pixel-art circles one world pixel thick and the particles as single world
// pixels -- part of the pixel art on the ground, not lines laid over it.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix, shadows_disabled, fog_disabled;

uniform vec4 color : source_color = vec4(1.0, 0.922, 0.761, 1.0);

// 1 draws flat `color`: the outline.
uniform float solid = 0.0;

// Peak alpha of the gradient at the outline, and how fast it falls off inward.
uniform float gradient = 0.24;
uniform float gradient_falloff = 3.2;

// The pulse: waves per second, how far apart the waves are (world units), and
// how far the gradient dims between them.
uniform float pulse_speed = 0.7;
uniform float pulse_wavelength = 1.4;
uniform float pulse_depth = 0.5;

// Particles run in LANES side by side along the outline, each lane
// `lane_spacing` wide. In every lane, a particle may be born each
// `spawn_period` seconds (with odds `spawn_chance`, at a random moment in the
// period), then glides inward at `speed` world units a second and fades out
// over its path, gone by the middle of the face.
uniform float lane_spacing = 0.26;
uniform float spawn_period = 1.1;
uniform float spawn_chance = 0.3;
uniform float speed = 0.55;
uniform float particle_alpha = 0.95;

// Chains: the odds a particle is 2, 3 or 4 squares long, one behind the
// other along its path, reading as one longer particle. The rest are single.
uniform float chain_2 = 0.14;
uniform float chain_3 = 0.08;
uniform float chain_4 = 0.04;

// Glitches: each second of a particle's life (`glitch_window`) holds a
// stutter with odds `glitch_chance`: over `glitch_duration` seconds it jumps
// to two or three places up to `glitch_reach` lanes away, then snaps back to
// its path.
uniform float glitch_window = 1.0;
uniform float glitch_chance = 0.14;
uniform float glitch_duration = 0.25;
uniform float glitch_reach = 1.5;

// A particle is as wide on screen as an outline bar at the same distance: the
// bar's width per unit of distance from the eye, within the bar's limits (set
// from HighlightBuilder, so the two can never drift apart).
uniform float width_per_distance = 0.0085;
uniform float min_width = 0.012;
uniform float max_width = 0.08;

// World units the surface is pulled toward the camera, to beat z-fighting.
uniform float depth_pull = 0.04;

// Disc mode: the brush's outer circle, its inner one, and whether to draw it.
uniform float disc_mode = 0.0;
uniform float disc_radius = 3.0;
uniform float disc_inner = 1.0;
uniform float show_inner = 1.0;

// The stylised filter's pixel grid (see StylizedFilter.gdshader), mirrored so
// the disc's pixels land exactly on the world's.
uniform bool pixelate = true;
uniform int pixel_resolution = 320;
uniform float pixel_far_scale = 2.2;
uniform float pixel_near_distance = 3.0;
uniform float pixel_far_distance = 22.0;
uniform int pixel_distance_steps = 4;

void vertex() {
    vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    view.xyz += normalize(-view.xyz) * depth_pull;
    POSITION = PROJECTION_MATRIX * view;
}

// Integer-valued inputs only: each distinct pair gives its own value in 0..1.
float hash(vec2 cell) {
    uvec2 v = uvec2(ivec2(cell)) * uvec2(1664525u, 22695477u) + uvec2(1013904223u, 1u);
    v.x += v.y * 1664525u;
    v.y += v.x * 1013904223u;
    v ^= v >> 16u;
    v.x += v.y * 1664525u;
    return float(v.x & 0xFFFFu) / 65535.0;
}

// The filter's grid for something at a depth: columns and rows across the screen.
vec2 pixel_grid(float depth, vec2 viewport) {
    if (!pixelate) {
        return viewport;
    }

    float rows = float(pixel_resolution);

    if (pixel_far_scale > 1.0) {
        float span = max(pixel_far_distance - pixel_near_distance, 0.001);
        float t = clamp((depth - pixel_near_distance) / span, 0.0, 1.0);
        float steps = float(pixel_distance_steps);
        t = floor(t * steps) / steps;
        rows *= mix(1.0, pixel_far_scale, t);
    }

    return vec2(floor(rows * (viewport.x / viewport.y)), rows);
}

void fragment() {
    if (solid > 0.5) {
        ALBEDO = color.rgb;
        ALPHA = color.a;
    } else if (disc_mode > 0.5) {
        // Snap to the world's pixel: read the disc at the centre of the
        // filter's pixel this fragment falls in, by stepping from here along
        // how the disc position changes across the screen.
        vec2 grid = pixel_grid(-VERTEX.z, VIEWPORT_SIZE);
        vec2 centre_uv = (floor(SCREEN_UV * grid) + 0.5) / grid;
        vec2 cell_px = VIEWPORT_SIZE / grid;
        mat2 per_px = mat2(dFdx(UV), dFdy(UV));
        vec2 xy = UV + per_px * ((centre_uv - SCREEN_UV) * VIEWPORT_SIZE);

        // The world pixel's four corners, in the disc.
        vec2 cx = per_px[0] * (0.5 * cell_px.x);
        vec2 cy = per_px[1] * (0.5 * cell_px.y);
        float r = length(xy);
        float r0 = length(xy - cx - cy), r1 = length(xy + cx - cy);
        float r2 = length(xy - cx + cy), r3 = length(xy + cx + cy);
        float near_r = min(min(r0, r1), min(r2, r3));
        float far_r = max(max(r0, r1), max(r2, r3));

        // Nothing past the outer circle.
        if (near_r > disc_radius) {
            discard;
        }

        // A pixel is on a circle when the circle passes through it.
        bool ring = near_r <= disc_radius && far_r >= disc_radius;
        ring = ring || (show_inner > 0.5 && near_r <= disc_inner && far_r >= disc_inner);

        // The gradient and its pulse, working in from the outer circle.
        float depth = max(disc_radius, 0.0001);
        float inward = max(disc_radius - r, 0.0);
        float toward_middle = clamp(inward / depth, 0.0, 1.0);
        float wave = 0.5 + 0.5 * sin(TAU * (pulse_speed * TIME - inward / pulse_wavelength));
        float glow = gradient * pow(1.0 - toward_middle, gradient_falloff)
            * (1.0 - pulse_depth * (1.0 - wave));

        // Particles in lanes round the circle, travelling in toward the
        // middle, each exactly one world pixel: a particle is drawn in the
        // pixel its position falls in. Lanes are spaced to fit the
        // circumference evenly, so they meet up where the angle wraps.
        float pixel = max(length(per_px[0]) * cell_px.x, length(per_px[1]) * cell_px.y);
        float circumference = TAU * disc_radius;
        float lanes = max(1.0, floor(circumference / lane_spacing));
        float spacing = circumference / lanes;
        float along = (atan(xy.y, xy.x) + PI) * disc_radius;
        float lane_here = floor(along / spacing);
        float reach_in = 4.0 * pixel + glitch_reach * spacing;
        mat2 to_px = abs(determinant(per_px)) > 1e-12 ? inverse(per_px) : mat2(0.0);
        float speck = 0.0;

        for (int dl = -2; dl <= 2; dl++) {
            float lane = lane_here + float(dl);
            float lane_id = mod(lane, lanes);

            float first = floor((TIME - (inward + reach_in) / speed) / spawn_period);
            float last = floor((TIME - (inward - reach_in) / speed) / spawn_period);

            for (int n = 0; n < 4; n++) {
                float slot = first + float(n);
                if (slot > last) {
                    break;
                }

                vec2 id = vec2(lane_id, slot);
                if (hash(id + vec2(0.0, 101.0)) >= spawn_chance) {
                    continue;
                }

                float born = (slot + 0.8 * hash(id + vec2(17.0, 3.0))) * spawn_period;
                float age = TIME - born;
                float head = age * speed;

                if (age < 0.0 || head > depth) {
                    continue;
                }

                float roll = hash(id + vec2(5.0, 211.0));
                float squares = roll < chain_4 ? 4.0
                    : roll < chain_4 + chain_3 ? 3.0
                    : roll < chain_4 + chain_3 + chain_2 ? 2.0
                    : 1.0;

                // A stutter: a few quick jumps, then back on its path.
                vec2 jump = vec2(0.0);
                float window = floor(age / glitch_window);
                vec2 wid = vec2(lane_id * 131.0 + window, slot);

                if (hash(wid + vec2(29.0, 7.0)) < glitch_chance) {
                    float starts = hash(wid + vec2(43.0, 11.0)) * (glitch_window - glitch_duration);
                    float into = age - window * glitch_window - starts;

                    if (into >= 0.0 && into < glitch_duration) {
                        float jumps = hash(wid + vec2(59.0, 13.0)) < 0.5 ? 2.0 : 3.0;
                        float step_index = floor(into / (glitch_duration / jumps));
                        vec2 sid = wid + vec2(step_index * 7.0 + 71.0, 19.0);
                        jump = (vec2(hash(sid), hash(sid + vec2(3.0, 5.0))) * 2.0 - 1.0)
                            * glitch_reach * spacing;
                    }
                }

                // Head and tail in the disc: a run of pixels along its way in.
                float angle = ((lane + 0.5) * spacing + jump.x) / disc_radius - PI;
                vec2 way = vec2(cos(angle), sin(angle));
                float head_r = disc_radius - (head + jump.y);
                float tail_r = head_r + (squares - 1.0) * pixel;
                float along_way = clamp(dot(xy, way), head_r, tail_r);
                vec2 offset = to_px * (way * along_way - xy);

                if (max(abs(offset.x) / cell_px.x, abs(offset.y) / cell_px.y) <= 0.5) {
                    // In quickly at the outline, then a long, slow fade.
                    float path = clamp(head / depth, 0.0, 1.0);
                    float fade = smoothstep(0.0, 0.06, path) * (1.0 - smoothstep(0.05, 1.0, path));
                    speck = max(speck, fade);
                }
            }
        }

        ALBEDO = color.rgb;
        ALPHA = ring ? 1.0 : clamp(max(glow, speck * particle_alpha), 0.0, 1.0);
    } else {
        float inward = max(UV2.x, 0.0);
        float depth = max(UV2.y, 0.0001);
        float toward_middle = clamp(inward / depth, 0.0, 1.0);

        // A wave of brightness leaving the outline and travelling inward:
        // its phase falls with distance in, so each crest moves toward the middle.
        float wave = 0.5 + 0.5 * sin(TAU * (pulse_speed * TIME - inward / pulse_wavelength));
        float glow = gradient * pow(1.0 - toward_middle, gradient_falloff)
            * (1.0 - pulse_depth * (1.0 - wave));

        // As thick as a bar at this distance, measured ON SCREEN. VERTEX is the
        // fragment's view-space position, so its length is the distance from
        // the eye, as the builder measures it for a bar. A pixel there is the
        // angle one pixel spans -- how far the view direction turns from one
        // pixel to the next -- times that distance.
        float distance_from_eye = length(VERTEX);
        float width = clamp(distance_from_eye * width_per_distance, min_width, max_width);
        vec3 view_dir = VERTEX / distance_from_eye;
        float pixel_angle = max(length(dFdx(view_dir)), length(dFdy(view_dir)));
        float radius = 0.5 * width / max(distance_from_eye * pixel_angle, 1e-6);

        // How the surface coordinates change across the screen, inverted: turns
        // an offset on the surface into one in pixels. A dot drawn round on the
        // surface is squashed flat on ground seen at a low angle; drawn round
        // on screen, it keeps the outline's thickness from any angle.
        mat2 per_pixel = mat2(dFdx(UV), dFdy(UV));
        bool usable = abs(determinant(per_pixel)) > 1e-12;
        mat2 to_screen = usable ? inverse(per_pixel) : mat2(0.0);

        // Squares are tested against every particle that could reach this
        // fragment: the lanes either side (a square sized on screen spills
        // past its lane on a surface seen edge-on, and a glitch throws a
        // particle sideways), and every birth whose head could be near here.
        float along = UV.x;
        float lane_here = floor(along / lane_spacing);
        float reach_in = (3.0 * width + glitch_reach * lane_spacing) + width;
        float speck = 0.0;

        for (int dl = -2; dl <= 2; dl++) {
            float lane = lane_here + float(dl);

            // Births whose head is between `inward - reach_in` and
            // `inward + reach_in` from the outline now.
            float first = floor((TIME - (inward + reach_in) / speed) / spawn_period);
            float last = floor((TIME - (inward - reach_in) / speed) / spawn_period);

            for (int n = 0; n < 4; n++) {
                float slot = first + float(n);
                if (slot > last) {
                    break;
                }

                vec2 id = vec2(lane, slot);
                if (hash(id + vec2(0.0, 101.0)) >= spawn_chance) {
                    continue;
                }

                float born = (slot + 0.8 * hash(id + vec2(17.0, 3.0))) * spawn_period;
                float age = TIME - born;
                float head = age * speed;

                if (age < 0.0 || head > depth) {
                    continue;
                }

                // Its length, in squares.
                float roll = hash(id + vec2(5.0, 211.0));
                float squares = roll < chain_4 ? 4.0
                    : roll < chain_4 + chain_3 ? 3.0
                    : roll < chain_4 + chain_3 + chain_2 ? 2.0
                    : 1.0;

                // A stutter, if this second of its life holds one: a few quick
                // jumps, each to its own place, then back on its path.
                vec2 jump = vec2(0.0);
                float window = floor(age / glitch_window);
                vec2 wid = vec2(lane * 131.0 + window, slot);

                if (hash(wid + vec2(29.0, 7.0)) < glitch_chance) {
                    float starts = hash(wid + vec2(43.0, 11.0)) * (glitch_window - glitch_duration);
                    float into = age - window * glitch_window - starts;

                    if (into >= 0.0 && into < glitch_duration) {
                        float jumps = hash(wid + vec2(59.0, 13.0)) < 0.5 ? 2.0 : 3.0;
                        float step_index = floor(into / (glitch_duration / jumps));
                        vec2 sid = wid + vec2(step_index * 7.0 + 71.0, 19.0);
                        jump = (vec2(hash(sid), hash(sid + vec2(3.0, 5.0))) * 2.0 - 1.0)
                            * glitch_reach * lane_spacing;
                    }
                }

                // The particle is a run of squares from its head back along
                // its path, one square's width apart: a segment on the surface
                // from tail to head, drawn square-ended on screen.
                vec2 head_at = vec2((lane + 0.5) * lane_spacing, head) + jump;
                vec2 tail_at = head_at - vec2(0.0, (squares - 1.0) * width);
                vec2 here = vec2(along, inward);
                vec2 nearest = vec2(head_at.x, clamp(here.y, tail_at.y, head_at.y));
                vec2 pixels = usable ? abs(to_screen * (here - nearest)) : vec2(1e6);
                float coverage = 1.0 - smoothstep(radius - 0.5, radius + 0.5, max(pixels.x, pixels.y));

                // In quickly at the outline, then a long, slow fade toward the
                // middle of the face.
                float path = clamp(head / depth, 0.0, 1.0);
                float fade = smoothstep(0.0, 0.06, path) * (1.0 - smoothstep(0.05, 1.0, path));

                speck = max(speck, coverage * fade);
            }
        }

        speck *= step(0.0, inward);

        ALBEDO = color.rgb;
        ALPHA = clamp(max(glow, speck * particle_alpha), 0.0, 1.0);
    }
}
