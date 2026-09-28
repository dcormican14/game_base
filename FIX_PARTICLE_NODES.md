Ok, what you are doing worked, but there are some things I want to nail down now:

## Fixing the naming
The name should make it more synonymous with the raw nodes. Something like particle nodes would work. Even though it is a slightly different system, it will still utilize the node system to create the initial topsoil generation.

## Fixing the soil shape
The topsoil/particle nodes should be a section of the planet, meaning if mined beneath them, they don't just keep extending. Think of it like this: when the planet is created, it is a ball of raw nodes with a shell of topsoil nodes arouind it. The topsoil shell is wrapped in a cloud-like wrapper that extends slightly into the raw nodes' layer, but raw nodes take priority meaning if I mine down far enough, raw nodes will stick through (like what we have now), but if I mine all of the topsoil in the area, the edges of the pit will round off to only contain the existing topsoil (it shouldn't just cut off at a sharp angle). Also of note, though I mentioned that the topsoil contains "nodes" imagine that it has those "nodes" only to create the shape we see, then those nodes don't matter for placing/shaping later since we have a different system.

Imagine as well if we extended that system so that we had pockets of topsoil within the planet. Though I don't want to add this now, In the future, it certainly is possible. The pockets would comprise some of the old raw nodes that we had, but instead of using those to create soil nodes, we would draw a pocket of topsoil around the nodes, clipping slightly into the raw nodes around it (likely just bordering their cores). That way, when mined around it, it will reveal this pocket but also the pocket will appear as its own thing.

## Fixing the digging/building of particle
Placing should add a node slightly above the center of the circle where we are looking. It will raise up the ground around it (just as we have now), but nothing substantially further than the circle. This means we need a build height. As we increase in height, our base needs to be wider. By implementing a dynamic build height, we get around this. By dynamic build height, I mean that we should only be able to stack maybe 5 particle nodes on top of eachother without support. I don't know how a check for a slim tower would look, but we need one because right now, I can build full mountains in half a second.

## Fixing the outline and particle effect

1. Particles should still travel inward from the outline for any node outline we have (including pickaxe), but they should slowly fade as they approach the center. 
2. They should be slightly less frequent. When traveling, they have the ability to glitch. 
3. Occasionally (think uncommon not mythic rare), they can quickly adjust their location 2-3 times in short succession (within a quarter second) then resume their path. 
4. Particles can chain together in their travels, stacking up to 4 times in a row (appearing as one long particle), but most are 1 individual particle.

The vibe is high tech or advanced sci-fi elegant tech that sometimes glitches. Right now, it looks bad.

## Fixing the lighting + skybox

### Lighting adjustments:
I want a custom modular lighting rig. This lighting rig should provide a warm ambient lighting to the scene from a moon present in the skybox. We will get to the moon in a second.
Lighting should keep the slightly plum shadows.

### Skybox adjustments
I want this to feel light night time. To achieve this, let's do the following:
1. Remove the orange/yellow nebulaic clouds. Those look bad.
2. Make a brightness filter over each layer of the skybox so they can be adjusted from godot. Set their defaults to the following: plum background: 25% what it is currently, nebulae/cloud background: 50% what it currently is, and stars: 75% what it currently is. Keep them as 0-1 values of full luminosity to 0% luminosity.
3. Add a moon (off-white gold + gray) that matches the background, but still provides a good moon look. It should have noise patterns on it just like our moon and some craters. This moon is stagnant in the night sky and is the source of our lighting rig mentioned earlier.

## Prompt
Read through this document in its entirety, reason what a fix would look like, then ask me any questions if something is not abundantly clear. When you have the answers, create a comprehensive and detailed itemized TODO checklist and begin each phase of this implementation.

---

# Decisions (answers to the questions)

- **Naming.** The system is *particle nodes* everywhere, as the counterpart to *raw nodes*. "Sand" survives only as a particle node's material name, the way "Stone" is a raw node's material.
- **Undermining.** Mining the rock out from under the topsoil leaves the topsoil in place as a shell, with a rounded underside showing in the cavity. It never wraps down the cavity's rock walls. There's no gravity for now.
- **Shovel cadence.** One node per click: each click adds or removes one node's worth of soil as a smooth bump confined to the circle, and holding the button repeats at a steady pace. Digging mirrors placing.
- **Support.** The peak may stand at most 5 nodes (10 units) above the *average* ground height on a ring just outside the circle.

Assumptions I made myself (tell me if any is wrong):

- A layer brightness of 1 means today's luminosity and 0 means black. Defaults: space 0.25, nebulae 0.5, stars 0.75. Shooting stars belong to the stars layer.
- A chained particle's squares run along its direction of travel, reading as one longer dash.
- The moon sits at a fixed direction high in the sky over the spawn. It doesn't turn with the stars, and the moonlight comes from exactly that direction.

# TODO checklist

## Phase 1: Naming
- [x] Rename `SandDepth` to `ParticleDepth` (Planet, PlanetGenerator, tests, test worlds).
- [x] Rename the system in comments and docs: "sand" or "topsoil" as a system becomes "particle nodes"; `Sand` / `SandNode` stay as the material and type.
- [x] README: a short terminology section (raw nodes, particle nodes, materials), written with the Phase 7 docs.

## Phase 2: Soil shape (the particle shell)
- [x] Rock stops counting as "inside" the particle field. For the particle surface, a raw node takes a small negative *bleed* level, so the surface around particle nodes reaches into a neighbouring raw node but stops short of its core.
- [x] Particle-to-rock lattice edges get particle quads, interpolated between sites. The particle surface becomes its own closed "cloud" that sinks slightly into the rock, and rock wins wherever the two overlap.
- [x] Delete the envelope, `Wrapped` and `ParticleBeside` rules. Raw nodes draw their faces again, except toward covering cells.
- [x] Edges round off: once the topsoil in an area is mined, the particle surface curves down into the rock instead of stopping at an angle.
- [x] Undermining leaves a shell: mining rock under particle nodes shows a rounded particle underside in the cavity and no particle surface on the cavity's rock walls.
- [x] Generation stays per node (a particle node is a cell whose site is in the shell), behind one `Classify` rule, so underground pockets can be added later without touching the meshers.
- [x] Tests:
  - where the shell lies buried on the rock, neither surface draws anything;
  - the particle surface doesn't spread into a mined cavity;
  - pits stay watertight.
  (No test that the surface never reaches a raw node's core: its vertices are averages, so beside a rock buried in particle nodes they can come close to the core. It's always hidden inside the rock there.)

## Phase 3: Shovel (place and dig, one node per click)
- [x] Discrete cadence: act on the click, then repeat while held, at a pace that can't build a mountain in seconds.
- [x] Place is a smooth union of the ground with one node's volume of soil, centred just above the circle's centre. The blend raises the ground around it only within the circle.
- [x] Dig is the mirror image: a smooth subtraction centred just below the centre. Rock is never touched.
- [x] Dynamic build height: placing is refused when the new peak would stand more than 5 nodes above the average ground on a ring just outside the circle.
- [x] Remove the cone sculpt, which grew its footprint forever. The shovel no longer needs a stroke anchor.
- [x] Keep the check-then-record ledger calls, sized to one node.
- [x] Tests:
  - one click moves about one node of volume, all inside the circle;
  - a slim tower stops at the support limit, and a wider base lets it go higher;
  - digging stops at rock.

## Phase 4: Outline particles (both tools)
- [x] Particles travel inward smoothly on every outline, pickaxe included, and fade gradually as they near the centre.
- [x] Slightly fewer particles than now.
- [x] Glitches, uncommon but not rare: a particle jumps 2–3 times within a quarter second, then resumes its path.
- [x] Chains: most particles are a single square, and some chain 2–4 squares in a row along their direction of travel.
- [x] Squares stay as thick as the outline, in the same cream. The look is elegant high-tech that sometimes glitches.

## Phase 5: Skybox
- [x] Remove the warm/orange nebula accent (uniforms, exports, code).
- [x] Per-layer brightness filters exported to the editor (0–1): space 0.25, nebulae 0.5, stars 0.75 by default.
- [x] Moon layer:
  - pixel-art disc in off-white gold and grey;
  - mare-like noise and craters;
  - hides the stars and clouds behind it;
  - fixed direction, with exports for direction, size, brightness and colours.

## Phase 6: Lighting rig
- [x] A new modular `LightingRig` scene (moonlight plus ambient) that anyone can drop into a level. It takes the moon's direction from the Skybox so the two can never disagree.
- [x] Moonlight is warm and is the key light. Ambient is warm, with plum-tinted shadows.
- [x] Replace the Sun in the planet level with the rig.
- [x] Recalibrate particle and raw node albedo for the new light, so the soil still reads as light off-white.

## Phase 7: Verification and docs
- [x] Update the capture tool:
  - sky and moon shot;
  - pit edge shot;
  - undermined shell shot;
  - support-limit tower check (replaces the tall pile);
  - dig regression.
- [x] All tests pass. Screenshots checked. Frame rate holds.
- [x] Update the README.
---

# Shovel revamp (Valheim-style terrain modes)

The one-node-per-click shovel was too clunky. It goes back to gradual shaping,
with modes.

## Decisions

- **Modes**, cycled with **R** in this order:
  1. Raise (sharp)
  2. Raise (gradual)
  3. Lower (sharp)
  4. Lower (gradual)
  5. Level
  6. Smooth
- **Left mouse** applies the selected mode gradually while held. Right does nothing with the shovel.
- **The brush is two circles.** The ground changes at full strength inside the inner circle and fades to nothing at the outer one. Sharp and gradual share the same outer circle. Sharp has a small inner circle, so the change concentrates into a peak or point. Gradual has a large inner circle, giving a broad, even rise or dip.
- **Level** flattens toward the ground height under the crosshair at the moment the press started. Dragging carries that same level across the terrain.
- **Smooth** softens bumps inside the circle without raising or lowering it overall.
- **Mode bar:** text labels in a row above the hotbar, shown only while the shovel is held. The selected mode is in gold, the rest in cream.
- **Height (my call, as asked):** no cap. Raising only ever changes the ground inside the brush, and never a cone that widens with height. Building straight up gives a column or steep mound as wide as the brush, not a mountain spreading across the landscape. The gradual rate is the only other limit.

## TODO checklist

### Shaping engine
- [x] Heightmap sampling: find the ground height along the up axis on a grid over the brush, from the fill field itself (rock counts as ground). Works at any angle on the planet, and on narrow shapes a blended estimate would average away.
- [x] Each mode computes new heights per column (raise, lower, level toward a plane, smooth toward the neighbourhood average), weighted by the two-circle falloff and scaled by the frame's length.
- [x] Write the new heights back into the fills inside the brush only. A rising column only ever adds and a falling one only ever removes, so caves and overhangs nearby survive.
- [x] Report the volume added and removed separately for the ledger.
- [x] Remove the one-node ball, the support rule and the click cadence.

### Tool and controls
- [x] Tool modes as part of `ITool` (a list of mode names; tools without modes have none), with the selected mode carried in `ToolContext`.
- [x] `tool_mode` input action on **R**. It shows up in the keybind settings automatically.
- [x] `ToolController`:
  - remembers each tool's mode;
  - cycles on R;
  - starts a fresh stroke when the mode changes;
  - raises an event so the bar can update.
- [x] `ShovelTool`: continuous, left applies the selected mode, Level anchors on the press, with the ledger checked before and recorded after.
- [x] Highlight: the outer circle is the outline, the inner circle is drawn too, and the glow and particles are unchanged.

### Mode bar
- [x] New `ToolModeBar` HUD: text labels above the hotbar, selected in gold, the rest in cream, visible only while the held tool has modes and gameplay has the input.
- [x] Added to the planet level.

### Verification
- [x] Tests:
  - raising stays inside the brush;
  - sharp concentrates while gradual spreads;
  - lowering stops at rock;
  - Level flattens to the press height;
  - Smooth softens a peak;
  - the ledger can refuse;
  - modes cycle.
- [x] Capture (a shot with the mode bar comes from every gameplay shot, since the HUD is in them):
  - raise, cycle to lower and dig through the real input path;
  - build straight up underfoot (no fall-through, and the base stays brush-wide);
  - level a bump;
  - a shot with the mode bar.
- [x] README updated.
