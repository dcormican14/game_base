# Changes I would like to see made:

There are several things I would like to see changed. First, the visuals need some updating, we need to ensure the colors conform to our palette- the shovel selection outlines are too thick, there is no transluscent subtle gradient pulsing from the outline upward. The function needs to be reined in- when I right click, ground should be raised and when I left click, it should be lowered, mirroring the mining function. In general, the structure of the world needs a deep overhaul. Think of the world as one large 3D universe, and the world is comprized of different materials, with "particle" type materials take up the full space that they are given and the "raw node" type material are rocky-style node system that we have created. Additionally, tool functions should be a part of the same extended tool interface with mining, building, and outline functions. In all,  we need a dramatic rework while keeping the functionality we have now that respects object oriented programming with an emphasis on growth and building systems that can be extended as we improve the game. 

This current implementation is a game in godot, likely written in GD script. What I want is a highly optimized (both in terms of functionality, structure, and readability) C# OOP implementation of what we have with just the organic build (there are others, we can ignore them). Below are a list of steps which are suggestions on how to handle this overhaul.

## The culling
Begin from a new branch in git.
Step one is to remove all old and unused tests and levels. We have 3 levels available in the start menu. You can cut those as well and just use the organic world we have been working on.
If need be, rewrite the gd scripts of code we WILL use in C#.

## The color/vibe
We need a larger world. The goal is to make the world feel flat when the player is on the surface.
The style is stylized (similar to zelda, but more detailed) and pixelated, creating a fun and simple atmosphere to play in.
The color palette is based on the dark plum of the space background. The particle material should appear as sand, but it should have a light, off-white color.
For now, let's just make a flat, featureless world. We will add to that later.

## Conclusion
ASK ME IF YOU MAKE ANY DRASTIC DESIGN DECISIONS, otherwise you have full autonomy.
---

# Rework checklist

Branch: `world-rework` (from `streaming-chunks`). Decisions from the Q&A:
large organic planet that reads as flat; topsoil becomes a general **particle
node** living in the same store/stream/mesh pipeline as raw nodes; left mines,
right places; no collection yet, but every mine/place goes through a check;
outlines thin, faces glow with a pulsing gradient, tiny particles.

## 1. Culling
- [x] Delete every check/shot/timer/audit scene in `Game/` and their scripts in `Modules/Stats/` (keep `PerfStats`)
- [x] Delete the cube and ico worlds: `QuadSphere*`, `IcoSphere*`, `NodeOrientation`, the prism `INodeGrid` contract, `Cube*`/`Ico*` planet sources, streamers and scenes
- [x] Delete `Modules/Terrain/WorkshopTerrain.cs`
- [x] Delete `TopsoilCap` (its job moves into particle nodes) and `NodeEditor` / `NodeHighlight` / `ShovelHighlight` (replaced by the tool system)
- [x] Main menu: drop the three-world picker; Play launches the one planet level
- [x] Remove the `shovel_mode` input action
- [x] Rewrite the README for what the project is now

## 2. Palette
- [x] `Core/Palette.cs`: one list of colours derived from the space plum (`#28061e`) and the star ramp (`#fee1ea` / `#ba6976` / `#8c445c`)
- [x] Route every hard-coded colour through it: rock shades, sand, highlights, crosshair, inventory, loading screen, filter outline, menus (a palette theme merged into Godot's default theme), tool icons. The green demo cube became a Stone block item.
- [x] Sand is a light off-white; rock is plum-tinted stone

## 3. World structure
- [x] `VoronoiGrid`: the organic grid reduced to pure geometry (jittered sites, cell lookup, cell faces) with no planet clipping baked in
- [x] Node type hierarchy: `NodeType` -> `RawNode` (Voronoi polyhedra) / `ParticleNode` (fills its cell to a level); `StoneNode`, `SandNode`
- [x] `NodeChunkStore` keeps a fill level per cell alongside the material, thread-safe chunk table
- [x] `PlanetGenerator`: a flat, featureless planet with raw nodes throughout and a particle (sand) layer on top, surface exactly at the radius
- [x] Planet radius large enough to read as flat from the player's eye (radius 6000: horizon dip about 1.3 degrees)
- [x] Meshing split per form behind one interface: `RawNodeMesher` (polyhedra, fully enclosed cells skipped cheaply) and `ParticleNodeMesher` (smooth surface through the fill levels)
- [x] `NodeWorld`: store + sections + workers; one physics body per form so a hit knows what it touched; stale worker results can never overwrite newer geometry
- [x] `ChunkStreamer` simplified to the one grid we have
- [x] Sand shader in world space (the old one was tuned for a radius of 120 and would smear at scale)
- [x] Spawn: wait for ground, place on the sand surface, radial gravity

## 4. Tools
- [x] `ITool` / `Tool`: one interface covering targeting, mining, placing and outlining
- [x] `IMaterialLedger` check called before every mine/place (unlimited for now)
- [x] `PickaxeTool`: raw nodes only; left mines a node, right places one against the face
- [x] `ShovelTool`: particle nodes only; left lowers, right raises, continuous while held
- [x] `ToolController` on the player: input, inventory -> tool, hold-to-repeat
- [x] Input actions: `mine` = left mouse, `place` = right mouse
- [x] Outline only shows on material the held tool can work

## 5. Highlight visuals
- [x] Thin outline bars, same width for both tools (sized by distance, so a few pixels at any range)
- [x] Pickaxe: subtle pulsing gradient on the node's faces + very small particles falling and fading on them
- [x] Shovel: thin ring + vertical cylinder rising from it with the same pulsing gradient + very small particles rising up the cylinder
- [x] One shader drives both

## 6. Tests
- [x] Headless runner scene (`Tests/TestRunner.tscn`) that finds and runs test suites, exits non-zero on failure
- [x] Suites: grid, store, generator, meshers (incl. a 3000-ray watertight check on dug pits), tools + ledger, inventory -- 28 tests

## 7. Verify
- [x] `dotnet build` clean
- [x] Test suite passes headless
- [x] Screenshots of the level: surface reads flat, sand off-white, rock in palette, both highlights (`Tests/Capture/LevelCapture.tscn`, which also drives the real mine/place input path end to end)

## Found along the way
- [x] Sand/rock seams let the sky show through the planet: fixed by meeting the rock's faces between sites, drawing sliver faces, and only letting deeply buried sand hide rock
- [x] Meshing throughput: one section per worker per frame took 14s to settle the world; batched, it settles in about 1.3s and is playable in 0.2s
- [x] A held shovel starved its own geometry (only the newest build was ever uploaded); now any build newer than the last upload goes up
- [x] Heaps stood up as cliffs (the angle-of-repose sides were cut off at the dig radius); the sides now run out to the ground
- [x] Chunk table was a plain Dictionary read by mesh workers while written; now concurrent, with a version-stamped per-thread cache

---

# Round 2 checklist

Feedback: keep the original gold / off-white UI colours; one gold off-white
outline for both tools; the shovel's gradient lies on the face being changed
and moves inward; particles flow from the outline in toward the face.

## 1. UI colours back to gold and off-white
- [x] `Palette`: add the original UI colours as named entries -- `Gold` (#e98b0f, the crosshair dashes) and `Cream` (#ffebc2, the crosshair ring and inventory ink) -- and make them the UI's accent and ink
- [x] Crosshair: gold dashes, cream ring (the original values, now read from the palette)
- [x] Inventory: restore the original panel, slot and cream ink values exactly
- [x] Menus and loading screen: cream text, gold for hover / focus / progress fill; plum panels stay
- [x] Tests still pass; screenshot the menu and HUD to confirm

## 2. One gold off-white outline for both tools
- [x] `Palette.Highlight` becomes a gold off-white that sits against plum and reads on both sand and stone
- [x] Drop the per-material outline colour (`NodeType.OutlineColour`); both tools use the one outline colour
- [x] Screenshot both tools to check it reads on sand and on stone

## 3. Shovel gradient on the face being changed, moving inward
- [x] Replace the shovel's vertical cylinder with a glow draped over the sand inside the ring, following the ground (heaps and pits included)
- [x] Gradient strongest at the ring, fading toward the centre
- [x] The pulse travels inward from the ring rather than breathing in place (same shader, so the pickaxe's faces get the same inward motion from their edges)

## 4. Particles flow from the outline inward
- [x] Glow surfaces carry "distance along the outline" and "distance in from the outline" coordinates: each face is fanned from its centre so both are exact
- [x] Shader: particles are born at the outline and drift inward, fading as they near the centre -- for every face of the pickaxe's node and across the shovel's disc
- [x] Remove the old up/down flow (`Flow`, `ParticleHeightFade`) from the builder and shader
- [x] Update the highlight test; screenshot both tools

## 5. Verify
- [x] `dotnet build` clean, all tests pass headless
- [x] Capture: menu, HUD, both highlights
- [x] README updated where it describes colours and the highlight

# Round 3 checklist

- [x] Gradient and particles use the outline's gold off-white (`Palette.Highlight`); the per-material glow colour is gone
- [x] Particles exactly as thick as the outline: the same width-per-distance rule and limits, passed from `HighlightBuilder` to the shader
- [x] Particles sized in screen pixels, so they stay round (and outline-thick) on ground seen at a low angle
- [x] Neighbouring particle cells checked, so dots on edge-on faces are not clipped square
- [x] Tests pass; screenshots of both tools checked

# Round 4 checklist

- [x] Gradient thinner (falls off about twice as fast from the outline) and less opaque (peak alpha 0.35 -> 0.24)
- [x] Particles are screen-aligned squares, still exactly as thick as the outline
- [x] Fewer particles: printed in word-like runs with gaps (roughly a third of cells hold one)
- [x] Terminal-output feel: a character grid that scrolls inward one whole row per tick, rows appearing all at once at the outline, occasional single-tick blinks, fading near the middle
- [x] Tests pass; screenshots of both tools checked

