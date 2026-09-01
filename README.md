# Game Base

A modular Godot 4.6 (.NET / C#) starter project. Every feature lives in its own
folder under `Modules/` (scene + script side by side) so it can be ported into
another project by copying that folder.

## Layout

| Path | What it is |
|---|---|
| `Modules/Core/` | `SettingsService` autoload — persists video/audio/control settings and dynamic keybinds to `user://settings.cfg` |
| `Modules/MainMenu/` | Main menu screen (Play / Settings / Quit) |
| `Modules/PauseMenu/` | Drop-in pause menu (`CanvasLayer`, pauses the scene tree) |
| `Modules/SettingsMenu/` | Settings screen with a keybind list generated from the InputMap |
| `Modules/Player/` | `CharacterBody3D` player rig — first- or third-person, toggled from settings; animated humanoid model |
| `Assets/Characters/` | Character models, one folder per character (Mixamo XBot & YBot dummies) |
| `Modules/Filters/` | Drop-in screen-space stylization: outlines, pixelation, dither |
| `Modules/Bismuth/` | `[Tool]` bismuth hopper-crystal blobs — stepped terraces on a jittered tessellation (art-direction prototype for project-infinite-world WP05) |
| `Modules/Crosshair/` | Centre-screen pixel-art crosshair — 0-4 dashes spread evenly around the centre, fading out toward the tips |
| `Modules/BlockEditor/` | Place/destroy blocks on bismuth blobs by looking at them |
| `Modules/Stats/` | Performance overlay (FPS, frame/physics time, draw calls, tris, VRAM/memory), toggled in Settings -> Video |
| `Modules/Terrain/` | `[Tool]` procedural "workshop" terrain: flat dark checker floor, box platforms, prism ramps |
| `Game/World.tscn` | Example gameplay scene wiring the modules together |

## First run

1. Open the project in Godot 4.6 (.NET edition).
2. Build the C# assembly once (**Project → Tools → C#** or the hammer icon /
   `dotnet build`) so scripts — including the `[Tool]` terrain — resolve.
3. Run. Main menu → Play loads `Game/World.tscn`.

Default controls: WASD move, Space jump, Shift sprint, Ctrl crouch (press
while sprinting to slide), mouse look, Esc pause. Left click places a block on
a bismuth blob, right click destroys one.

## Porting a module to another project

1. Copy the module folder into the target project **at the same
   `res://Modules/...` path** (scene files reference scripts by absolute
   `res://` path).
2. `Modules/Core` is required by the Settings/Pause/Player modules. Register it
   as an autoload in the target `project.godot`:
   ```ini
   [autoload]
   SettingsService="*res://Modules/Core/SettingsService.tscn"
   ```
   (The UI and player degrade gracefully without it, but nothing persists.)
3. Copy the `[input]` section of `project.godot` (or define your own actions —
   every action name used by the player is an exported variable).
4. Build the C# project.

## Design notes / knobs

- **Everything tunable is exported.** Speeds, gravity, camera distance, terrain
  size/seed/colors, scene paths, action names, settings defaults — all editable
  in the inspector without touching code.
- **Settings screen is tabbed** (Video / Audio / HUD / Controls, a default
  Godot `TabContainer`). Video holds fullscreen, vsync and the first/third-person
  camera toggle; Audio holds master volume; HUD holds the crosshair line count;
  Controls holds sensitivity and the keybind list.
- **Dynamic keybinds:** the settings screen lists every InputMap action that
  doesn't start with a `NonRebindablePrefixes` entry (default: `ui_`). Add a new
  action in Project Settings → Input Map and it shows up automatically.
  Rebinding uses physical keycodes (layout-independent), steals the key from any
  other action bound to it (no double bindings), and persists to
  `user://settings.cfg`. Delete that file to fully reset.
- **One binding per action** is the persistence model (`RebindAction` replaces
  all events). Extend `SettingsService.SerializeEvent`/`DeserializeEvent` if you
  need joystick axes or multi-bind.
- **Player rig:** the first/third-person choice lives in
  `SettingsService.ThirdPerson` (persisted; the rig reacts live via the
  `SettingsChanged` signal, falling back to its `ThirdPersonFallback` export
  when the autoload is absent). `CameraDistance` sets the third-person
  spring-arm length, with `ShoulderOffset`/`ShoulderHeight` placing the
  character left of the crosshair so the model never covers the aim point;
  the character model auto-hides in first person. Camera pitch spans a full
  vertical sweep (`MinPitchDegrees` -89 to `MaxPitchDegrees` +89, stopping
  just short of the poles to avoid gimbal flip). The **head** is what stays
  limited — `PlayerAnimator`'s `MaxHeadPitchUp/DownDegrees` clamp how far the
  neck turns, so at extreme angles the head stops at a natural limit while
  the view keeps going. Input
  action names are exports, so the rig works with whatever action names a
  project already uses.
- **Crouch & slide:** hold crouch to crouch (capsule shrinks, camera lowers,
  slower speed; standing up is blocked while under a ceiling via a shape
  query). Pressing crouch above `SlideMinSpeed` starts a slide: a speed boost
  up to `SlideBoostSpeed` decaying at `SlideFriction`, steerable with
  `SlideSteerStrength`, ending on release/slow-down/leaving the ground —
  jumping out of a slide works. All speeds/heights/frictions are exports.
  The animator has matching crouch (bent-leg sneak) and slide (lean-back,
  front leg extended) poses, plus clip slots named `crouch`/`sneak`/`slide`.
- **Character model & animations:** the player shows a Mixamo dummy
  (`Assets/Characters/XBot`) instanced under `%CharacterRig` in `Player.tscn`
  — swap the instanced scene to change characters (e.g. YBot). `PlayerAnimator`
  (on the rig node) animates it in two modes:
  - **Clip mode** — if an `AnimationPlayer` under the rig has clips whose
    names contain `idle` / `walk` / `run` / `sprint` / `jump` / `fall`, it
    cross-fades between them based on movement. To use real Mixamo clips:
    download animations for the same character ("without skin"), import them,
    and add their animations to an `AnimationPlayer` under the rig with those
    names — the animator picks them up automatically.
  - **Procedural mode** (active fallback for the T-pose dummies) — a
    footstep-planner + IK locomotion system, not a canned cycle:
    - **Phase-locked gait oscillator**: both legs share one gait clock, hard
      offset by half a cycle (they structurally cannot sync into a gallop),
      with a **duty factor** that shrinks with speed — above 0.5 at a walk
      (double support, trailing foot) and below 0.5 at a run (a real flight
      phase with both feet airborne). Cadence = speed / step length, with
      stride warping at a run. Idle repositioning steps are distance-based
      (covers turning in place).
    - Each swing **predicts its landing spot from velocity** (where home
      will be at touch-down, plus half the upcoming stance), then snaps it to
      the ground with a raycast. Sideways travel is handled separately from
      forward travel: the foot on the side of travel **leads** out
      (`ShuffleLeadScale`) while the other only **closes up underneath**
      (`ShuffleTrailScale`), giving a human sidestep shuffle instead of wide
      splits — with a center-line clamp (`StanceWidthKeep`) so legs never
      crisscross.
    - **Body orientation**: while running the rig yaws partway toward its
      movement direction (`RunTurnTowardMovement`, capped by
      `MaxBodyTurnDegrees`), so a sideways sprint reads as an angled run
      while the character still faces roughly toward the screen center. The
      turn is scaled down for walking (`WalkTurnScale`), off by default when
      crouched (`CrouchTurnScale`) so crouch-strafing stays a square
      shuffle, and full during a slide. Backward movement never turns the
      model around — it keeps facing forward and walks in reverse, with
      back-diagonals angling to their respective side. Only the model
      rotates — the collision body and camera keep facing where the player
      aims.
    - **Head aim**: the head (and neck, sharing the turn via `NeckAimShare`)
      continuously lerps toward the camera's look direction, so the model
      always shows where the player is aiming. Clamped by
      `MaxHeadYaw/PitchDegrees`, eased at `HeadAimSpeed`, and layered on top
      of the gait and crouch head pose. The aim source defaults to the
      player's camera; override with `AimSourcePath`.
    - Planted feet are **world-locked**; swinging feet travel a smootherstep
      arc with a sine height profile and per-frame terrain clearance.
    - **Analytic two-bone IK** (law of cosines, forward knee pole) drives
      each thigh/knee/foot independently to reach its target; feet align to
      the sampled ground normal. Crouching just lowers the pelvis — the IK
      bends the knees to keep the feet planted.
    - Torso lean/twist/sway and arm counter-swing are driven by the
      measured forwardness of each leg; acceleration-based weight shift
      leans into speed-ups and turns; air/slide leg poses are authored and
      blended over the IK with critically damped springs.
    - Finds bones by Mixamo-style name suffixes and works in model space
      from the rest pose, so it works on any `mixamorig` humanoid. Every
      distance/height/duration/angle is exported.
- **Terrain:** attach `WorkshopTerrain.cs` to any `StaticBody3D`. Generates a
  flat dark-gray checkerboard floor plus seeded box platforms reached by
  full-width triangular-prism ramps — hard right angles, flat shading, no thin
  geometry. Some structures are multi-staged: terraced boxes in a line with
  roof-to-roof ramps and a walkable landing on each roof (`MultiStageChance`,
  `MaxStages`, `LandingCells`). The checkerboard covers everything: floor,
  ramp walking surfaces, and platform tops and side walls, all aligned to one
  world grid (side walls checker vertically in `CellSize` rows from y=0).
  Ramp sides/undersides use the separate `StructureColor*` trim grays. Everything is exported:
  floor size, cell size, platform count/footprint, height step and min/max
  steps, ramp slope, spawn-clear radius, seed, colors.
  It's a `[Tool]` script, so after the assembly is built any export change
  regenerates the mesh live in the editor. Collision is an exact trimesh of
  the render mesh, and platforms never spawn within `SpawnClearRadius` of the
  node's origin.
- **Pause menu** can be instanced into any gameplay scene; it runs with
  `ProcessMode.Always` so it works while the tree is paused, and closes/opens
  the shared `SettingsMenu` scene internally.

## Scene lighting

`Game/World.tscn` enables sky-based ambient light on its `Environment`
(`ambient_light_source = 3`, sky contribution 1.0). Without an ambient source a
`DirectionalLight3D` is the only light in the scene, so every surface facing
away from it receives zero light and renders black. Flat blocky terrain mostly
hides this; rounded meshes such as the character do not - their shadowed side
goes solid black. Any new scene wanting the same look needs the same ambient
setup.

## Stylized filter

`Modules/Filters/StylizedFilter.tscn` is a drop-in post-process — instance it
anywhere in a 3D scene and it renders a full-screen pass after everything else.
Depth and normal buffers are only readable from `spatial` shaders, so it is a
`MeshInstance3D` whose shader forces its quad over the viewport (not a
ColorRect), with a large `extra_cull_margin` so it is never frustum-culled.

Three independent effects, each toggleable in **Settings -> Video** and
persisted like any other setting:

- **Outlines** (on by default) - Roberts-cross edge detection over the depth
  and normal buffers. Needs no per-mesh setup, so it works on the procedural
  terrain automatically. The depth threshold scales with distance so far
  geometry does not smear into solid lines, and outlines fade out approaching
  `OutlineMaxDistance`.
- **Pixelation** (off by default) - snaps sampling UVs to a virtual grid of
  `PixelResolution` rows, width following the screen aspect so pixels stay
  square. Every buffer is sampled through the snapped UV, so outlines land on
  the same grid as the colour rather than drawing crisp lines over blocky
  pixels.
- **Dither** (off by default) - 4x4 ordered Bayer dither, applied in real
  screen pixels so the pattern stays fine even while pixelating.

Colour quantization is deliberately not included yet.

Note that screen-space pixelation is prone to *pixel crawl* with a free-look
camera - the pixel grid is fixed to the screen while the world moves, so
surfaces shimmer as you turn. It is exposed as an option rather than the
default for that reason; low-res textures with nearest-neighbour filtering are
the more stable route to a chunky look.

## Bismuth blobs

`Modules/Bismuth/BismuthBlob.tscn` prototypes the terrain art direction from
`project-infinite-world/.docs` (WP05 — the bismuth gate). Attach to any
`StaticBody3D`; it is a `[Tool]` script, so every export regenerates the mesh
live in the editor.

**What it implements from the spec**

- **Hopper-crystal stepping** — concentric terraces stepping inward as they
  rise, each tier twisted (`TierTwistDegrees`) and drifted (`TierDrift`) from
  the one below, so tiers form a square spiral rather than flat contour rings.
  This is the doc's stated distinction: rice terraces vs. crystal. Set
  `Squareness` to 0 to see the rice-terrace failure mode for comparison.
- **The recessed cavity** — `HopperTiers` reverses the innermost tiers back
  downward, giving the skeletal rim-grows-faster-than-face signature. 0 gives
  a solid stepped pyramid.
- **Terrace quantizer mapping** — the tier solve corresponds to the doc's
  `ITerraceQuantizer`: `TerraceLevel` = tier index, `QuantizeAltitude` =
  tier x `StepHeight`, `LateralInset` = `InsetPerStep`, plus `StepJitter`.
- **Determinism doctrine** — a jittered quad grid (never free-floating nodes),
  all jitter from a pure position+seed hash, and every vertex snapped to
  quarter-node increments, per the spec's fixed-jitter rule.
- **Tessellation** — cells are the dual of the jittered node grid, so terrace
  faces are irregular quads rather than a visible square lattice, while
  remaining watertight.
- **Checkerboard** — cells alternate between `ColorA` and `ColorB` by grid
  parity, matching the world floor, so the tessellation is legible as colour
  as well as silhouette (the doc's "material banding falls out for free").
- **`Shape`** — `Mound` is the default stepped cone. `Sphere` switches the tier
  profile to a full sphere sampled at uniform angle, with its lower half sunk
  below ground, giving a terraced ball. Its radius and step height derive from
  `TierCount` and `InsetPerStep` so the proportions stay round regardless of
  the mound dials.

**Three non-obvious details that the look depends on**

1. Tier membership is classified at each cell's *unjittered lattice*
   position. Jitter shapes the cell outline, but if it also moves the sample
   point, boundaries fragment and the silhouette combs into gaps.
2. Terrace-edge wobble uses smoothed patch noise at `EdgePatchSize`, which
   must stay several times `NodeSpacing`. Per-cell noise produces sawtooth
   rubble instead of clean ledges.
3. The cavity floor sits one step above the base, and the outermost tier takes
   no edge jitter — otherwise the pit vanishes into the surrounding plate and
   the silhouette breaks up.
4. `Sphere` samples its profile at uniform *angle*, not uniform height. A
   sphere is nearly flat near its equator, so height-sampled tiers come out at
   almost identical widths and the result reads as a barrel.

## Block editing

`Modules/BlockEditor` raycasts from the camera centre (matching the crosshair)
and edits whichever `BismuthBlob` it hits — left click places, right click
destroys, both as rebindable input actions. It is instanced under the player
and finds the active camera itself.

The ray is cast from the **camera**, so the crosshair always edits what it
covers and the character model is never in the way; `Reach` is measured from
the *player* rather than the camera, or the camera's set-back distance would
silently shorten it when standing away from a ledge. A placement is refused
only when the cube would genuinely overlap the player capsule (a real
box-vs-capsule test — a keep-out box around the body origin rejected valid
placements near the feet, which is what made ledge edges refuse to build).

Picking hands the blob the **ray**, not the contact point, and the blob marches
along it to the first solid cell. A raycast hit lands exactly on a block face —
a cell boundary — so mapping that single point to a cell rounds ambiguously
between neighbours and can target an empty cell, which is why some clicks
appeared to do nothing. Marching resolves faces, edges and corners
consistently; measured 38/38 removals across angles and heights on a full blob.

Edits are stored as **sparse per-block deltas on the blob** — keyed by
(cell x, cell z, tier), not by column — applied on top of the generated field
rather than baked into it. Per-block is what makes single-cube editing
possible: a per-column "top tier" can only raise or clear a whole stack, so
removing carved out the entire column and placing always landed on top of it.
The mesher works from an explicit block set and emits only faces whose
neighbour is absent, so a cube can be carved from the middle of a stack or
stuck onto any one face. That is the
diff-against-seed model from the terrain design: the blob still regenerates
from its seed, and changing `Seed` or any terrace dial keeps player edits
intact. `ClearEdits()` discards them.

Note that each edit triggers a full blob rebuild (mesh plus trimesh collision),
which is fine at these blob sizes but is the thing to replace with a dirty-chunk
rebuild if blobs get much larger.
