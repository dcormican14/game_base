# Sky: day-night cycle, sun and moon

Started 2026-10-02 on `world-rework`, after the terrain work was committed (`132f53e`; terrain's outstanding work is in TERRAIN_TODO.md).

Sources:
- `Day–Night Sky Cycle Implementation Spec.md` (the user's spec);
- `compass_artifact_wf-1988f78d-…_text_markdown.md` (research: a convincing sun in Godot 4).

## Decisions (asked and answered)

- **The planet turns.** The sun and moon circle the planet, so time of day depends on where you stand. One side is in daylight while the far side is at night, and travelling far enough changes the time.
- **The day sky is seamless.** At first it was drawn as pixel art like the night: flat stepped bands, glows cut into steps. In play the user found the bands and the glows' "strangely shaped" waves severe: "Please ensure that we have no lines or hard stops in the colors or the shaders."
  - Every day layer is now a smooth curve, worked out from the exact direction.
  - The sun and moon discs stay on the pixel grid.
  - The night sky and its starry style are kept as they are, nebula steps included.
- **A realistic sun under a pixel filter.** The sun behaves physically:
  - a small over-bright disc, with limb darkening (its edge darker than its centre);
  - a forward-scattering glow;
  - reddening and flattening near the horizon;
  - bloom.
  It is then sampled on the sky's pixel grid. The moon shines the same way.
- **The spec is used whole for the sky:** keyframes, twilight stages (blue hour, purple light, Belt of Venus, the warm glow on the sun's side), sun colours, star fading, and the terrain lit by the sky.
- **Not used from the spec:**
  - its teal terrain ("we will add that later");
  - its UI colours: the UI keeps the plum base, gold `#e98b0f` and cream `#ffebc2`.
- **The spec's colours are adapted to our plum by colour theory,** not used as given (see the palette below).
- **The moon sits opposite the sun:** it rises at sunset and sets at sunrise, always full.

## Turning the spec into a 3D planet sky

The spec is a flat 2D scene: screen-space gradients, a sun on a fixed arc, and hills as layers. The 3D version:

- **Hour → sun elevation.** The spec defines its stages by sun elevation (purple light at -3 to -4 degrees, blue hour at -4 to -8, and so on), so the 3D sky is driven by the sun's elevation over the player's local horizon. Rising or setting is decided by morning or afternoon. Each elevation maps back onto the spec's hour axis, through the elevations its keyframe table gives:
  - morning: -18 → 4.5, -9 → 5.3, -6 → 5.8, -2.5 → 6.25, 0 → 6.75, 6 → 7.75, highest → 12.5;
  - evening: highest → 12.5, 6 → 16.75, 3 → 17.6, 0 → 18.25, -2.5 → 18.75, -6 → 19.25, -12 → 20.2, -18 → 21, lowest → 24.

  Through twilight these are spread as real twilight is, rather than at the table's own labels. The labels put purple light at -3 and blue hour at -4, so half an hour of the spec's clock passed in one degree of the sun, and the lightness dropped from 0.43 to 0.31 (OKLab) in about three seconds of play. The spec's clock has the sun sink about 7 degrees in that half hour. Real purple light runs to about -6, and blue hour centres there.
  Every keyframe and bump function then runs exactly as the spec writes it. Near the poles the sun stays low, so the sky stays in golden hour, as a real polar sky does.
- **The vertical gradient** runs from zenith to horizon by elevation angle (90 degrees to 0), not screen height, through the spec's stops (0, 0.55 at 38% of the way to the horizon colour, 1). The stops are joined by a smooth curve (Hermite segments) that is level at the zenith and the horizon, so it has no corner at any stop or at the horizon.
- **The warm glow, purple light, and Belt of Venus with the Earth's shadow** are placed by direction rather than screen x:
  - the glow is centred on the horizon under the sun (radius about 75 degrees);
  - the purple light is centred about 16 degrees up on the sun's side (radius about 45 degrees);
  - the Belt of Venus is a band from the horizon to 15 degrees up, opposite the sun.
- **The sun is the scene's directional light,** and the sky draws the disc where that light comes from. The moon is a second directional light, opposite. Only the brighter of the two casts shadows.
- **"Terrain lit by the sky":**
  - the sunlight's colour follows the spec's sun colours (peach low, cream high);
  - its strength follows the spec's daylight curve;
  - the ambient light blends the night's plum toward the day sky;
  - the far terrain's haze takes the horizon colour, with the sun's glow scattered through it.
- **Colours are display colours.** Each spec hex is what should appear on screen, so the shader is handed the pre-tonemap values that land there through the Filmic tonemapper.

## Palette: the spec's colours adapted to our plum

The method:
- Our plum `#28061e` sits at 318 degrees, the spec's `#1E1024` at 282 degrees.
- **Cool sky colours** turn by +36 degrees. That keeps every relationship the spec designed (spacing, lightness, saturation) around our anchor.
- **The pinks** (purple light, Belt of Venus) are placed on our own rose ramp, Dusk to Blush, 341 to 353 degrees.
- **Warm accents** (apricot, coral, golden haze, sun) are kept. They already sit beside our gold (35 degrees) and cream (42 degrees).
- **Night is the space backdrop, not a plum of its own.** The user found the step from the last plum-like twilight colour into the dark night sky too abrupt, and asked for the transition to go to the space backdrop instead.
  - The night stops (0:00, 4:30, and a new 21:00 at -18 degrees) are the backdrop as it appears on screen. `DayCycle` works it out from the skybox's space colour, brightness and energy through the Filmic curve, `#060104` by default; measured on screen it is `#070104`.
  - The -12 degree stop (20:12) is blue hour carried half way to the backdrop in OKLab.
  - The gradient covers the night's nebulae from -18 to -6 degrees, where it had used 19.25-20.2 spec hours (-4 to -12).
  - Measured toward the sun, every 3 degrees of dusk, the OKLab lightness falls 0.69, 0.52, 0.38, 0.28, 0.18, 0.11, 0.095. Before, it held about 0.2 then dropped to the backdrop, and fell 0.43 to 0.31 in a single degree at blue hour.

Derivation script: kept in the session scratchpad as `palette.py`; the method above reproduces it.

| Name | Spec | Ours | How |
| --- | --- | --- | --- |
| Night zenith | `#120A18` | the backdrop (`#060104`) | night is the space backdrop |
| Plum night | `#1E1024` | the backdrop (`#060104`) | night is the space backdrop |
| Blue hour indigo | `#363C78` | `#583678` | cool 235 → 271 |
| Purple light | `#A9618C` | `#A96174` | pink 324 → 344 |
| Purple light glow | `#C488B4` | `#C4889D` | pink 316 → 339 |
| Sunrise apricot | `#F6C9A8` | `#F6C9A8` | warm, kept |
| Sunset coral | `#F0936E` | `#F0936E` | warm, kept |
| Golden-hour haze | `#F2CFA0` | `#F2CFA0` | warm, kept |
| Day overhead | `#7C98E0` | `#9C7CE0` | cool 223 → 259 |
| Horizon lilac | `#D4DAF4` | `#E1D4F4` | cool 229 → 265 |
| Belt of Venus | `#E3A9BD` | `#E3A9AF` | pink 339 → 354 |
| Earth's shadow | `#4A4F85` | `#684A85` | cool 235 → 271 |
| Sun low | `#FFB98A` | `#FFB98A` | warm, kept |
| Sun high | `#FFF4DC` | `#FFF4DC` | warm, kept |

Keyframes (zenith, horizon):

| Hour | Spec | Ours |
| --- | --- | --- |
| 0.00 | `#120A18`, `#1E1024` | the backdrop |
| 4.50 | `#150C21`, `#2B1D40` | the backdrop (twilight starts at -18 degrees) |
| 5.30 | `#1C2150`, `#363C78` | `#361C50`, `#583678` |
| 5.80 | `#283069`, `#8E6A98` | `#472869`, `#986A88` |
| 6.25 | `#45559A`, `#E3B19E` | `#68459A`, `#E3B19E` |
| 6.75 | `#6A80C4`, `#F6C9A8` | `#8A6AC4`, `#F6C9A8` |
| 7.75 | `#7892D6`, `#DCD4E6` | `#9678D6`, `#E6D4E5` |
| 12.50 | `#7C98E0`, `#D4DAF4` | `#9C7CE0`, `#E1D4F4` |
| 16.75 | `#7890D8`, `#DCD6EC` | `#9A78D8`, `#E9D6EC` |
| 17.60 | `#6E86CC`, `#F2CFA0` | `#8E6ECC`, `#F2CFA0` |
| 18.25 | `#5763AA`, `#F0936E` | `#7D57AA`, `#F0936E` |
| 18.75 | `#353B79`, `#A9618C` | `#583579`, `#A96174` |
| 19.25 | `#1D2252`, `#3A3F7A` | `#381D52`, `#5B3A7A` |
| 20.20 | `#160D24`, `#2A1A3C` | blue hour half way to the backdrop in OKLab (about `#1C0A22`, `#2A152F`) |
| 21.00 | (new) | the backdrop (twilight ends at -18 degrees) |
| 24.00 | `#120A18`, `#1E1024` | the backdrop |

## Status (2026-10-02)

First version built, not committed. 78 tests pass (8 new in `SkyTests`). Seen at every checkpoint with `SkyCapture`, on a flat spot at about 56 degrees latitude and on a jagged range.

Built:
- **`SkyPalette`:** the keyframes, the effect curves, the sun's colour, the elevation-to-hour mapping and the Filmic inverse. Pure.
- **`DayCycle`** (in `PlanetLevel`): a 20-minute day starting at 9:00 where the player stands, with `[` and `]` to scrub the time. It drives the sky, the lights and the far terrain's haze.
- **Sky shader:**
  - the day gradient, smooth;
  - the warm glow, flattened along the horizon (75 degrees wide, 35 tall), where the spec's 75-degree circle reached the zenith. Its stops are one smooth bell (0.6 exp(-(a/0.34)^2)), measured by a bearing from atan rather than acos, which had put a point in it;
  - the purple light (fading by smoothstep, and softly at the horizon), and the Belt of Venus over the Earth's shadow (its stops eased, and held past the horizon rather than cut off);
  - stars fading and turning with the planet;
  - the realistic sun under the pixel filter: disc energy 14, limb darkening, flattening, Cornette-Shanks glow, horizon haze and a glare;
  - the moon's glow, smooth;
  - the discs hidden below the planet's horizon; the glows are not cut off there (a cut showed as a dark strip beyond the land);
  - project-wide debanding, so smooth gradients show no 8-bit steps.
- **Seamlessness, measured:** across the checkpoint shots, the largest colour step between neighbouring sky pixels is 1 level (the debanding dither). The banded version had steps of 6-43 levels, a thousand or more per shot. Frames hold at 16.7 ms in play.
- **`LightingRig`:** sunlight (energy 1.15) and moonlight opposite, shadows on the brighter, ambient from night plum to the day sky (0.6 at day).
- **Bloom setting** on `Skybox`, off by default: the engine's bloom is a smooth blur over the pixel art. With it on, the noon sun was a soft white blob with no disc. The sky draws its own glare instead.
- **Far terrain haze:** Wine by night, the horizon colour by day, and the same sun and moon glow the sky has (passed from `DayCycle`, whose `SunGlow`, `SunHaze`, `SunScatter` and `MoonScatter` drive both).
- **F3 readout:** local time, sun elevation, stage.

Measured: at midday the sky reads about `#A689E2` overhead (target `#9C7CE0`) and `#D7C7F0` low (target `#E1D4F4`), from shots not exactly at the zenith or the horizon. So the tonemap inverse lands the spec's colours.

Fixed (2026-10-02): **the sky flipped between dawn and sunset at the pole.**
- *Seen:* on seed 3446493149, around sunrise, looking left and right of the sun showed stark orange, then ordinary daytime.
- *Cause:* the default spawn (`PlanetSpawn.Direction`, `Vector3.Up`) is the planet's north pole, on its axis. Morning or evening was decided by which side of the local meridian the sun was on, and at the pole the meridian is undefined. The third-person camera swinging round the player swept the local hour through the whole clock, and the sky jumped between the morning reading (`#CB9DA8`) and the evening one (`#C47384`).
- *Fix:* the sky blends its morning and evening readings by how fast the sun is climbing where the camera is (`DayCycle.Rising`). That is smooth everywhere. It is zero at noon and midnight, where the two look alike, and at the poles, where it is an even mix that holds still.
- *Checked:* at that seed and spawn, turning the player round now holds the sky at `#C88796`, within one level. Tests: `TheSkyHoldsStillAtThePole`, `CrossingThePoleIsSmooth`.
- *Still to note:* the F3 readout's local hour and stage still swing at the pole, because they are undefined there. The sky no longer uses them.

Open, for the user to judge in play:
- [ ] **Spawning at the pole.** With no axial tilt, the pole sees the sun circle the horizon all day, a permanent sunrise and sunset. Should the spawn move off the pole (for example to the equator, or to a seeded spot away from the poles)?
- [ ] **Lavender day.** The day sky is lavender, the spec's periwinkle turned onto our plum. If it should read bluer, turn the cool family less than +36 degrees (`palette.py`).
- [x] **Rings and bands.** Done: all of the day is smooth (see Decisions).
- [ ] **Glare over terrain.** With bloom off, a sun sitting on a ridge does not bleed light over the rock. It could be added in the stylized filter: a glare worked out from the sun's screen position, faded when the sun is hidden. That keeps it on the pixel grid.
- [ ] **Day length.** 20 minutes is a guess (Minecraft's). Could be a setting in the menu.
- [ ] **Midday exposure.** The sand still reads nearly white at noon. Tune `SunEnergy` and `DayAmbientEnergy` by eye.
- [ ] **Teal terrain.** Deferred by the user ("we will add that later").

## Plan

- [x] **Day cycle** (`DayCycle`):
  - the planet's turn and a clock (the day's length in minutes);
  - the sun's direction, with the moon opposite;
  - the player's local hour, the sun's elevation and the stage of the day;
  - the spec's keyframes and effects.
  It drives the sky, both lights, the ambient light and the far terrain's haze.
- [x] **Sky shader:**
  - day layers: the banded gradient, warm glow, purple light, the Belt of Venus and the Earth's shadow;
  - stars and nebulae fading by the spec's star curve, and turning with the planet;
  - the realistic sun under the pixel filter;
  - the moon's glow;
  - everything kept cheap in the radiance (cubemap) pass.
- [x] **Lights** (`LightingRig`): sunlight, its colour and strength from the cycle; moonlight opposite; shadows on the brighter of the two; ambient from plum night to day sky.
- [x] **Bloom:** environment glow that only the sun's disc passes. Built, then turned off by default; the sky's own glare replaces it (see Status).
- [x] **Far terrain haze:** the horizon colour plus the sun's scatter.
- [x] **Readouts and dev keys:** local time, sun elevation and stage in the F3 readout; keys to scrub the time.
- [x] **Tests:**
  - keyframes hit their stops, and the loop has no jump at 23:59 → 0:00;
  - the sun is up at local noon and down at midnight;
  - the local hour round-trips;
  - elevation maps onto the hour axis;
  - the moon is opposite the sun;
  - the tonemap inverse round-trips.
- [x] **Captures at the spec's checkpoints:** 5:30 (blue hour), 6:30 (dawn glow, belt opposite), 12:30 (midday), 18:15 (sunset), 18:55 (purple light), 22:00 (night); and the loop across midnight.
