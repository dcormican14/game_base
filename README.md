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
| `Modules/Terrain/` | `[Tool]` procedural "workshop" terrain: flat dark checker floor, box platforms, prism ramps |
| `Game/World.tscn` | Example gameplay scene wiring the modules together |

## First run

1. Open the project in Godot 4.6 (.NET edition).
2. Build the C# assembly once (**Project → Tools → C#** or the hammer icon /
   `dotnet build`) so scripts — including the `[Tool]` terrain — resolve.
3. Run. Main menu → Play loads `Game/World.tscn`.

Default controls: WASD move, Space jump, Shift sprint, Ctrl crouch (press
while sprinting to slide), mouse look, Esc pause.

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
- **Settings screen is tabbed** (Video / Audio / Controls, a default Godot
  `TabContainer`). Video holds fullscreen, vsync and the first/third-person
  camera toggle; Audio holds master volume; Controls holds sensitivity and the
  keybind list.
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
  spring-arm length; the character model auto-hides in first person. Input
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
