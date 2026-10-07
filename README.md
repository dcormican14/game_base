# Game Base

A Godot 4.6 (.NET / C#) game: a large turning planet of plains, hill country
and mountain ranges, built of raw nodes under a skin of particle nodes, under a
pixel-art sky that runs from day to night, which the player digs, piles and
builds with a pickaxe and a shovel. Every feature lives in its own folder under `Modules/` (scene
and script side by side) so it can be ported into another project by copying
that folder.

## Running

1. Open the project in Godot 4.6 (.NET edition), or build from the command
   line with `dotnet build`.
2. Run. Main menu -> **Play** loads `Game/PlanetLevel.tscn`, building the
   world from the **seed** in the box above it (random at launch, editable,
   **New** picks another; any number or word works). The pause menu shows
   the seed of the world being played.

| Input | Action |
|---|---|
| WASD, mouse | move, look |
| Space / Shift / Ctrl | jump / sprint / crouch (while sprinting: slide) |
| Double-tap Space | sandbox: free flight, no collision |
| **Left mouse** | **mine** with the held tool |
| **Right mouse** | **place** with the held tool |
| 1-9, mouse wheel | pick a hotbar slot |
| Tab | inventory |
| Esc | pause |
| F3 | terrain readout: region, heights, slope, sand depth, seed, far terrain, time of day |
| [ / ] | an hour back / forward |

Every action is rebindable in **Settings -> Controls**.

## Layout

| Path | What it is |
|---|---|
| `Game/PlanetLevel.tscn` | The level: planet, player, spawn, HUD, filter |
| `Modules/Core/` | `Palette` (every colour), `PaletteTheme`, the `SettingsService` and `UiStateService` autoloads, `ILoadProgress`, `NodeSearch` |
| `Modules/Nodes/` | The node world: types, storage, the Voronoi grid, meshing, streaming, physics picking |
| `Modules/Planet/` | The planet: its numbers, its generator, and the spawn that stands the player on it |
| `Modules/Terrain/` | The terrain's shape (regions, heights, overhangs, arches), its noise, its settings, the seed, and the far terrain |
| `Modules/Tools/` | The tool interface, the pickaxe and shovel, the controller that drives them, and the highlight |
| `Modules/Player/` | `CharacterBody3D` player rig with radial gravity, first or third person, animated model |
| `Modules/Inventory/` | Hotbar and backpack, item resources, rendered item icons |
| `Modules/Skybox/` | Pixel-art sky, day and night, its sun and moon (the source of the palette); `DayCycle`, the turning planet's clock |
| `Modules/Lighting/` | `LightingRig`: sunlight, moonlight, and the ambient that fills their shadows |
| `Modules/Filters/` | Screen-space stylisation: outlines, pixelation, dither |
| `Modules/Crosshair/`, `LoadingScreen/`, `MainMenu/`, `PauseMenu/`, `SettingsMenu/`, `Stats/` | HUD and menus |
| `Tests/` | The test runner and suites; `Tests/Capture/` renders screenshots of the level |

## The world

### One grid, two forms of node

Space is a **Voronoi diagram over a jittered cubic lattice** (`VoronoiGrid`).
Every integer cell owns a site pushed a little off its lattice point by a hash
of its address; a node is the region closer to its site than to any other, so
neighbouring nodes share their walls exactly and the rock reads as shattered
stone rather than masonry. Sites are never stored — any thread can compute any
site.

Every cell holds one byte of material and one byte of fill
(`NodeChunkStore`). What a material *is* lives on its `NodeType`, and the type
hierarchy splits on **form**:

- **`RawNode`** — a solid that fills its whole Voronoi cell: faceted, all or
  nothing. `StoneNode` is the planet's body.
- **`ParticleNode`** — granular material that fills its cell *to a level*. The
  fill is a signed distance to the material's surface (`NodeFill`), so a bed of
  particles is drawn as one smooth surface that can sit anywhere between
  lattice points. `SandNode` is the planet's topsoil.

**Terminology.** *Raw nodes* and *particle nodes* are the two forms; *stone*
and *sand* are materials, one of each form, the way a type names what a node
is made of. The planet is a body of raw nodes under a skin of particle nodes.

The particle nodes are their own **shell**, not a coat of paint on the rock.
Their surface is a closed "cloud" around them that sinks part of the way into
the raw nodes beside them -- never to a raw node's core -- and both surfaces
are drawn, so the rock wins wherever they overlap: rock stands through a thin
shell, and a thick one hides the join without a seam. Mine the rock out from
under the shell and the shell stays put, rounded underneath; mine the shell
away and its edge rounds down into the rock instead of stopping at an angle.
Which cells are particle nodes is decided node by node at generation, so
pockets of particle nodes inside the planet are a generator change away.

Adding a material is a class under the right branch plus an id in
`NodeMaterial`; the store, the meshers, the streamer and the tools pick it up.

### Meshing

The world is built in 8-cell sections on worker threads, one `INodeMesher` per
form, and uploaded on the main thread under a per-frame budget with edits ahead
of streaming:

- `RawNodeMesher` draws each rock cell as its polyhedron, skipping any face a
  neighbour covers and any cell whose 26 neighbours cover it — the whole
  interior of the planet costs nothing.
- `ParticleNodeMesher` runs **surface nets** over the fills: a quad wherever
  the fill changes sign along a lattice edge, with vertices at the average of
  the crossings in each lattice cube. A raw node counts as *just outside* the
  particle surface (`SectionSample.RawLevel`), so the shell sinks into the rock
  by an amount set by how deep the particle node beside it is. Where either end
  is rock the crossing is taken between *sites*, whose midpoint lies on the
  rock's own face, so that depth is measured against the rock itself. Where the
  shell lies buried on the rock, neither surface is drawn.

Raw and particle geometry collide on separate bodies, so a ray that hits the
world knows which form it touched. `MeshingTests.PitIsWatertight` fires 3000
rays into dug pits to prove no seam between the two lets the sky through.

### The planet

`Planet` holds the numbers (radius 6000, node size 2) and builds, from them,
the terrain settings and the world seed, a terrain SHAPE and a
`PlanetGenerator`, which it hands to the world, the `ChunkStreamer` and the
`FarTerrain`. At this radius the horizon dips about 1.3 degrees below level
for a standing player, so plains read as flat while the planet still curves
for anyone who climbs high enough to look.

### The terrain

The shape and the material are separate questions. The **shape**
(`ITerrainShape`) is a signed distance field -- positive in the ground -- so it
can hold anything a height map cannot: overhangs, arches, caprock lips. The
**material** is the generator's: a node is stone when its site lies deeper
below the surface than the sand there, and sand fills the rest up to the
surface, each sand cell storing its true distance to it (the shape sampled
every other node and divided by its gradient -- `DistanceBlock`).

`PlanetTerrain` lays the planet out in about 100 large **regions**
(`RegionMap`), 1-3 km across: exactly 15% flat, 30% hills, 25% mountains and
30% basins, dealt along a large ridged noise so ranges run in chains with
foothills round them. **Basins** gather into 2-4 **oceans**, each grown out
from a seed far from the ranges into one connected body 5-15 km across: a
shelf sloping to 60-120 below the plains near the coast, falling to 250-400
further out -- dry for now, waiting for water. Borders wander, and any two regions blend over about 300 units. Each
mountain range has a **character**: jagged ridges, rounded domes, stepped
mesas with caprock lips (peaks 150-300 units), or towering ranges -- a craggy
massif under a few horn peaks that reach 350-500, over everything else. Small hollows in
flats and hills dip below **sea level** (8 units under flat ground) too.

A handful of **canyons** (`Canyons`) wind inland from an ocean's coast across
plains and hills -- never mountains or sky islands -- as old riverbeds would:
a sandy floor between stepped rock walls, as deep as the shelf where they
meet the sea and closing at the head. They count as their own terrain: the
readouts say "Canyon" between a canyon's rims.

Three to five **sky-island zones** (`SkyIslands`), a kilometre or two across,
sit anywhere: land shattering and floating off.
- **Crater.** A steep-walled crater about 250 deep with a rubble floor.
- **Cracking.** The ground round the rim is split into plates by deep
  crevices, barely lifted, and the floor is cracked into plates still resting.
- **Heap.** The crater is filled with around ten thousand pieces of broken
  ground, frozen mid-drift, in a heap that narrows to a point about 1,000 over
  the rim at the middle. Floes at the rim are close enough to hop across, and
  the heap thins and shrinks as it climbs, until only small pieces drift at
  the top.
- **Pieces.** Each is a sand-topped slab, a bare rock chunk too steep for
  sand, or a leaning shard.

How the terrain looks them up:
- A zone's pieces are laid out the first time anything asks about it, and
  filed in 32-unit buckets, so a point only measures the few near it.
- The distance is kept exact down to -12 near them (`SkyIslands.Plateau`).
- Culling asks the shape whether any piece can reach a block
  (`ITerrainShape.Within`) instead of trusting a slope bound near them.
Overhangs come from warping the point sideways by 3D noise; **arches** are
tubes carved from mountain rock that break through only where a ridge is thin.

**Sand** lies 6 units deep on gentle ground, thins between 40 and 50 degrees,
and is gone past that: cliffs, crags and the undersides of overhangs are bare
rock (`SandRules`). Raised ground is rock through and through.

Every number is in `Modules/Terrain/DefaultTerrain.tres` (`TerrainSettings`),
to tune in the inspector. `Planet.FlatWorld` swaps in the plain round planet
(`FlatTerrain`), which the tests and the older captures use. All the noise is
seeded, pure C# in doubles (`Noise`), so any thread builds any chunk the same
way on any machine.

### Streaming and the far terrain

`ChunkStreamer` keeps a ball of chunks around the player resident -- open sky
included, as uniform air, so there is no ceiling on building (generation on
worker threads, nearest first). Chunks that are all sky or all buried rock are
recognised from a coarse look at the shape, using its bound on how fast the
distance can change, and skipped.

`FarTerrain` draws everything beyond, out to 4 km: the same 3D shape, arches
and overhangs included, meshed with surface nets in cubic blocks whose cells
double in size with distance (4, 8, 16... units), and finer over sky-island
zones. Level-1 blocks sample the very lattice the chunks do. The far terrain
is drawn a little further from the camera than it really is, along each line
of sight, so it always sits just behind the real ground: wherever the real
ground is drawn it wins, and any gap in it shows far ground, not sky. Between
110 and 210 units from the camera the real chunks dissolve away over it, so
the two grounds have no edge -- wide, because real rock is whole faceted
nodes standing up to a node proud of the smooth shape the far terrain draws,
and over a narrow band that read as a ledge (the streamer loads 5 chunks round
the player to cover it). Nearer than 110, over drawn chunks, the far terrain
is cut away, so it never covers a pit; over chunks not drawn yet it shows
whole, so streaming never opens a hole. Between levels, each block's ground
slides onto the next coarser level's (geomorphing, `FarMesher` MORPH) as it
nears the distance that level takes over at, so neighbouring levels meet at
one height instead of a ledge; a skirt hanging straight down from every
block's rim, only as far as the ground below goes, closes whatever crack is
left. Blocks swap level only when their replacement is ready.
Space found to be all air or rock is never visited again, stale work is
skipped, and blocks under drawn chunks are built last: at a sprint the far
terrain keeps up with nothing waiting. Sand carries the real sand's grain up
close, rock a mosaic of node-sized cells each lit and shaded on its own as
real nodes are, both fading to their average further out, with haze toward
the horizon.

The `Planet` is what the loading screen waits on: ready when the ground under
the player is built and the far terrain has drawn the horizon (25-50 s on most
spots; up to about 75 s in the middle of a sky-island zone, with its thousands
of pieces). `PlanetSpawn` puts the player on top of whatever
is there -- found by probing the shape from the sky down, so never under an
arch -- and holds them frozen until the streamer confirms real collision
underfoot; without that hold the player falls through a planet that has not
been built yet.

## Tools

Everything a tool does goes through one interface, `ITool`: what it works
(`Works`), what it points at (`TryTarget`), `Mine`, `Place`, and `Outline` for
its highlight. `Tool` supplies the common half; a tool only ever targets and
outlines the form of node it works, so the pickaxe shows nothing over sand and
the shovel nothing over bare rock.

| Tool | Works | Left (mine) | Right (place) |
|---|---|---|---|
| `PickaxeTool` | raw nodes | removes the node | puts stone against the face |
| `ShovelTool` | particle nodes | applies the selected mode | nothing |

The shovel is a **terrain tool with modes**. **R** steps through them -- the
mode bar above the hotbar shows which is selected -- and holding the left
button shapes the ground gradually in that mode:

| Mode | What it does |
|---|---|
| Raise | lifts the ground as a flat top, and stops about a node high (see below) |
| Lower | sinks the ground, flat-bottomed; stops at rock |
| Level | flattens toward the height of the ground where the press began, so dragging carries one level across the terrain; ground more than about a node (2.5 units, a little over what one Raise makes) above or below it is left alone |
| Smooth | softens bumps and edges, moving ground around without adding or removing any |

The **brush** is one circle, 3 units across the ground from its middle.
Raise and Lower work at full strength almost to its edge, so what they make is
flat; Level and Smooth fade out toward it, so the patch they work blends into
the ground around. The shovel reaches 6 units, as the pickaxe does.

The circles lie **on the ground** (`SurfaceDisc`): every point is the radius
from the centre measured along the surface, so the brush lies flat on flat
ground, stands up on a wall, hangs upside down on a ceiling and folds over an
edge, always covering the same area. It is found by walking: paths set out
from the centre in every direction, settling back onto the surface after each
small step and turning to follow it. The highlight draws those paths, and the
shaping weighs every cell by the nearest point of them -- what is drawn is
what changes, and nothing past it moves.

**Raising grows a flat top, and stops at 45 degrees.** The ground is drawn
with one vertex in each lattice cube (2 units), at the average of where the
surface crosses the cube's edges (surface nets). A pointed top has no vertex
of its own: the cubes around its tip average it with the lower slopes, so the
drawn top sits below the real one -- by up to a node -- until the tip passes
the next lattice point and a vertex appears right at it, and the drawn ground
snaps up. Any raise that lifts its middle faster than its edge makes a pointed
top. A flat top crosses each lattice layer all at once, as flat ground does,
and flat ground is drawn exactly -- so a raise lifts everything under the
brush together, and stops as a whole once any slope it is making steeper
reaches 45 degrees, about a node above the ground around it. Held, the drawn
ground never moves more than about 0.1 in a frame (the brush's own rate is
0.04).

To build higher, widen the base: a raise leaves ground already standing above
where it was aimed alone, and fills in below it, so raising round a mound
builds up the ground around it until the middle has room to rise again. On
ground (a brush facing within 60 degrees of up) slopes are measured against
the level, so mounds cannot stack flank on flank. On a wall or a ceiling a
raise just pushes the face out, with no stop: a dug wall's own edges are far
steeper than 45 degrees to it. Open cells all read as the edge of the fill
range however far out they are, so slopes find their real distance by looking
down to the ground beneath them, and only cells within a node of the surface
count -- they are the ones that decide where it is drawn.

Every mode works **along the surface** (`ParticleSculpt`), on the fill field
itself: a fill is a distance to the surface, so adding to it pushes the surface
out along its own facing. Raise builds a wall out sideways and a ceiling
downward, Lower digs into either, Level cuts and fills toward the height where
the press began (level on the ground; the plane of the face on a wall or a
ceiling), and Smooth smooths whatever face it is on. A
fill clamped at the edge of its range only says "at least this far", so it is
placed by its neighbours (a distance changes by at most a node between lattice
points) rather than pulled into a surface that has not reached it. Byte-sized
fills would round a faint edge's per-frame change away, so the last step is
rounded up or down at random in proportion (`NodeFill.FromLevelDithered`).
Each step first copies the patch of fills around the brush (`ParticleField`),
so the thousands of field reads a frame are array lookups, and every change is
worked out from one steady picture of the ground.

**Every mine and place is checked** against an `IMaterialLedger` first, and
recorded after. Nothing is collected yet, so the only ledger is
`UnlimitedLedger`; an inventory-backed ledger plugs in there without touching
any tool.

Items become tools by id (`ToolRegistry`): the `pickaxe` and `shovel` item
resources are ordinary `ItemType`s. `ToolController`, under the player, reads
the held item, aims down the crosshair, drives the highlight and turns the two
buttons into tool actions — discrete tools once per click with hold-to-repeat
(each tool sets its own pace), continuous tools every frame. A tool with modes
(`ITool.Modes`) steps through them on the `tool_mode` action (R); each tool
remembers its own mode, and `ToolModeBar` shows them above the hotbar while
that tool is held. A press keeps one `ToolStroke` for as long as the button is
down, even if the crosshair slips off the ground for a moment -- which is what
lets Level hold to the height it was pressed at.

### The highlight

One shader (`ToolHighlight.gdshader`) draws both tools' highlights after the
stylised filter, so it stays crisp:

- a **thin outline**, sized by distance from the eye so it is a few pixels
  wide at any range;
- a **thin, faint gradient on the face being worked**, strongest at the
  outline and falling off quickly toward the middle, with a pulse that
  travels inward;
- **small square particles** in lanes along the outline, gliding inward and
  fading slowly as they near the middle -- precise, high-tech, and now and
  then glitching. Most are a single square; some chain two to four in a row
  along their path. Once in a while one stutters: two or three quick jumps
  within a quarter second, then back on its path. Each square is exactly as
  thick on screen as the outline at the same distance -- sized in screen
  pixels, so it keeps its shape on ground seen at a low angle.

All three are one gold off-white (`Palette.Highlight`), for both tools on
every material.

The pickaxe outlines every edge of the node and glows on each face, working in
from that face's edges. The shovel draws its brush ON the ground, like a texture: a thin skin laid
flush over the ground the brush covers, whose every point carries where it is
in a flat circle -- the disc wrapped onto the surface like a sticker. The
shader draws everything from that position, mathematically, after snapping
each fragment to the stylised filter's own pixel grid (the filter's settings
are copied onto the material): the brush's circle, as a pixel-art circle one
world pixel thick -- a pixel is lit when the circle passes through it -- then
the gradient, and particles one world pixel each, all on the same pixels as
the world. The disc lies on
the fill field, which the drawn mesh follows closely but not exactly, so the
skin is settled onto the drawn ground by rays at a sparse grid of its points,
blended between. Glow surfaces are fanned from their
middle, one wedge per outline edge, so "along the outline" and "in from the
outline" are exact in every wedge.

## Palette

`Modules/Core/Palette.cs` is every colour in the game, taken from the space
background (`space_pixel_art.jpg`): the plum ground `#28061e`, the star ramp
`#8c445c` / `#ba6976` / `#fee1ea`, and steps between them, plus the off-white
sand and plum-tinted stone. The interface keeps its own two colours against
the plum: `Gold` (`#e98b0f`, the crosshair's dashes, hover, focus and progress)
and `Cream` (`#ffebc2`, text, the crosshair's ring, slot borders). The
interface theme (`PaletteTheme`) is merged into Godot's default theme at
start-up, so every button, panel and bar is drawn in it without per-scene
styling.

Particle colours in the palette are what the player should *see*;
`NodeMaterials.Albedo` converts them to the albedo that renders at that
lightness under the level's moonlight (`LitExposure`), with the moonlight's
warm cast. Re-measure it with the capture tool if the lighting changes.

## Day and night

The planet turns (`DayCycle`, in the level beside the skybox). The sun circles
the planet's axis once every 20 minutes (`DayLengthMinutes`), with the moon
opposite it, so the time of day is local:
- one side of the planet is in daylight while the far side is at night;
- travelling far enough changes the time;
- near the poles the sun stays low.

The first frame sets 9:00 where the player stands (`StartHour`). `[` and `]`
move the clock an hour, and F3 shows the local time, the sun's elevation and
the stage of the day.

The sky follows the Day-Night Sky Cycle spec (`Day–Night Sky Cycle
Implementation Spec.md`; the decisions and the adapted palette are in
`SKY_TODO.md`):
- **Driven by elevation.** The spec's stages are set by the sun's elevation,
  so the sky is too. The elevation over the local horizon is mapped onto the
  spec's hour axis (`SkyPalette.HourFromElevation`), and its keyframes and
  curves run as written.
- **Colours adapted to our plum.** The spec's cool sky colours are turned
  +36 degrees onto our plum, its pinks are placed on our rose ramp, and its
  warm accents are kept. The day comes out lavender, the blue hour violet.
- **Night is the backdrop.** Twilight ends on the night sky's own space
  backdrop as it appears on screen, worked out from the skybox's settings,
  so the gradient lands exactly on it. The last stop is blue hour carried half
  way there in OKLab (equal steps look equal). The nebulae come through as the
  sun sinks from 6 to 18 degrees down, so dusk falls steadily into the night,
  with no drop at its end.
- **Display colours.** The colours are what should appear on screen. The
  shader is handed the values that land there through the Filmic tonemapper
  (`SkyPalette.BeforeTonemap`).

The day's layers are seamless: each is a smooth curve, with no bands, steps,
corners or edges at the horizon. They are worked out from the exact view
direction, not the pixel grid, and project-wide debanding dithers the final
image so no gradient shows 8-bit steps (`SkyCapture` shots measure a largest
step of one colour level between neighbouring pixels).
- **Gradient:** overhead to horizon through the spec's stops, joined by a
  smooth curve that levels off at the horizon.
- **Twilight:** a warm glow along the horizon on the sun's side (apricot at
  dawn, coral at dusk), purple light after sunset and before sunrise, and the
  Belt of Venus over the Earth's shadow opposite the sun. Each fades as a
  smooth bell and eases between the spec's stops.
- **Stars:** they fade by day, and turn with the planet.
- **The night:** unchanged, nebulae, stars and all.

The sun is worked out as a real one is seen:
- a disc far brighter than anything lit, darker at its limb, flattened as it
  sets, and peach low but cream high;
- the disc goes through the pixel filter, worked out on the sky's pixel grid
  and hidden below the planet's real horizon;
- around it, smooth: a forward-scattering glow (Cornette-Shanks), a haze
  that widens near the horizon, and a glare round the disc.

The moon glows the same way, far fainter. The far terrain's haze carries the
same sun and moon glow as the sky behind it, so distant land takes the sky's
light. The engine's bloom (`Skybox`, `BloomEnabled`) is off by default; the
sky draws its own glare.

## Lighting

`LightingRig` (drop `Modules/Lighting/LightingRig.tscn` into a level beside a
`Skybox`) is the scene's light:

- **Sunlight and moonlight.** With a `DayCycle`, the sunlight comes from the
  cycle's sun in the sun's own colour, and the moonlight from the moon
  opposite, a warm off-white gold. Each fades as the day's light comes and
  goes, and as it sinks below the local horizon; only the brighter casts
  shadows. Without a cycle the moonlight comes from where the skybox's moon
  hangs (`Skybox.MoonDirection`).
- **Ambient** is what fills the shadows. By night it is the backdrop's plum
  (`AmbientColor`), leaned a little toward the moonlight's warmth
  (`AmbientWarmth`). By day it moves toward the sky's own colours.
- **Haze.** The far terrain's haze takes the sky's colour (the night's wine,
  the day's horizon), lit toward the sun.

The skybox only draws the background; ambient light is the rig's to set.

## Tests

A small headless suite: grid geometry, the store and fill encoding, the
planet generator, the terrain (cost, determinism, thread safety, the slope
bound, over sky-island zones too, skipped chunks matching their cells, the
region mix, sky-island craters filled and heaped to a point, peak heights, sand
on the flat and none on cliffs, spawn points on top of the ground, seeds), the
day-night sky (the spec's keyframes and curves, no jump at midnight, the
elevation-to-hour mapping, the tonemap inverse, the sun crossing the sky and
the time being local), the far terrain (arches kept at three levels, the first ring on the chunks'
ground, each level morphing onto the next, no holes while streaming), the shovel on generated ground (raising on
a hillside and where the lattice leans, levelling a hillside, smoothing where
sand meets rock), both meshers (flatness, winding, closed solids, watertight
seams, the buried shell drawing nothing, the shell staying put when
undermined), the tools against real collision (targeting, raising and
lowering, nothing moving outside the brush, a held raise growing without a
single jump and stopping at 45 degrees, widening its base, lowering
stopping at rock, Level flattening to the press height, Smooth spreading a
peak, the ledger check, mode cycling, highlights) and the inventory.

```sh
godot --headless --path . res://Tests/TestRunner.tscn
godot --headless --path . res://Tests/TestRunner.tscn -- --filter=Tool
```

The runner exits with 0 when everything passes and 1 otherwise. A suite is any
`TestSuite` subclass; each `[Test]` method (void or `Task`) runs on a fresh
instance, with anything it adds to the tree freed afterwards.

`Tests/Capture/LevelCapture.tscn` is the visual check. It needs a window, and
loads the real level, times loading and frame rate, and saves screenshots of
the horizon, the moon, both highlights (with a strip of frames to see the
particles move), a dug pit and the menu. It then drives the real input path —
the player's own camera, inventory, the mode key and the left button — raises
and lowers the ground, raises a bump and levels it by dragging from flat
ground, and raises a column under the player's own feet and lowers it back,
reporting how wide its base spread and checking every physics frame that the
player never sinks into the ground or falls through it.

```sh
godot --path . res://Tests/Capture/LevelCapture.tscn -- --out=C:/some/folder
```

For the terrain:

- `Tests/Capture/PlanetMap.tscn` draws a seed's whole planet as a flat map --
  regions, mountain characters, basins, height shading -- for judging a layout
  at a glance (headless is fine).
- `Tests/Capture/TerrainCapture.tscn` plays the level at a flat, hills, each
  kind of range and a border, times each load (failing past 60 s) and saves
  shots from the eye and the air.
- `Tests/Capture/StressCapture.tscn` holds the shovel's Raise for a long time,
  walking and turning, logging frame times and memory every second; with
  `--fly=SPEED` it instead carries the player across the planet, logging the
  far terrain and streaming (and `--shots=DIR` saves a picture every 5 s).

```sh
godot --headless --path . res://Tests/Capture/PlanetMap.tscn -- --seed=1 --out=C:/maps/one.png
godot --path . res://Tests/Capture/TerrainCapture.tscn -- --seed=1 --out=C:/shots [--spots=flat,jagged]
godot --path . res://Tests/Capture/StressCapture.tscn -- --seconds=90 [--seed=1] [--spot=jagged]
```

For the sky, `Tests/Capture/SkyCapture.tscn` stops the clock at the spec's
checkpoints and photographs toward the sun, away from it and overhead:
- 5:30, blue hour;
- 6:30, the dawn glow with the belt opposite;
- 12:30, midday;
- 18:15, sunset;
- 18:55, purple light;
- 22:00, night;
- and either side of midnight.

```sh
godot --path . res://Tests/Capture/SkyCapture.tscn -- --out=C:/shots [--spot=flat] [--hours=5.5,12.5]
```

## Painting your own skybox

`Modules/Skybox/` ships a **placeholder**: a procedural starfield and nebula
(`SpaceSky.gdshader`). It exists so the scene is not empty while you make real
art. Pick a `Source` on the `Skybox` node to replace it.

### Option 1 — Panorama (simplest, best for hand-painted art)

Set `Source = Panorama` and drop your image into `PanoramaTexture`.

The image is **equirectangular**: one wide picture wrapped around the sphere,
exactly like a world map.

- **Aspect must be 2:1** (4096x2048 or 8192x4096 are good sizes). Anything else
  is stretched.
- **The horizontal edges wrap**, so the left and right edges must match
  seamlessly or you get a visible vertical seam.
- **The top and bottom rows squash to a point** — the poles. Keep detail away
  from the very top and bottom, or it smears.
- Paint it in any 2D tool. For a night sky the whole image is essentially
  black with stars, gas and distant galaxies painted on.
- **Import settings matter**: select the texture in Godot and, under Import,
  set **Repeat** to Enabled so the horizontal wrap is clean. Enable **High
  Dynamic Range** if you want stars brighter than white to bloom.
- **For pixel art, turn `Panorama Filter` OFF** on the Skybox node. Filtering
  smooths the texture when magnified, which blurs hand-placed pixels into mush.
  Leave it on for painted or photographic art.

To get a starting canvas, you can bake the current placeholder to an image and
paint over it:

```gdscript
var env := $Skybox/SkyboxEnvironment.environment
var img := RenderingServer.sky_bake_panorama(env.sky.get_rid(), 1.0, false, Vector2i(4096, 2048))
img.save_png("res://Assets/Sky/space_panorama.png")
```

Note that bake is saved in linear space with no tonemapping, so it looks very
dark opened straight in an image editor — that is expected, not a bug.

### Option 2 — Cubemap (six faces)

If your art is authored as six cube faces, import them as a single
`Cubemap` and sample it from a small custom sky shader:

```glsl
shader_type sky;
uniform samplerCube panorama;
void sky() { COLOR = texture(panorama, EYEDIR).rgb; }
```

Assign that shader to `CustomSkyShader` with `Source = CustomShader`.

### Option 3 — Your own sky shader

Set `Source = CustomShader` and supply any `shader_type sky` shader. The one
built-in you need is `EYEDIR`, the normalized view direction for the pixel
being drawn — everything is computed from that, so there is no geometry and no
seam to worry about. `SpaceSky.gdshader` is a worked example.

### Tuning the placeholder

With `Source = Procedural` the sky is drawn as **pixel art** matching
`space_pixel_art.jpg`, in four layers that composite in order. Each has its
own export group and can be dialled to zero independently.

**Brightness.** Space, nebulae and stars each have a brightness from 0 (black)
to 1 (the layer's full, calibrated luminosity): `SpaceBrightness` (0.25),
`NebulaBrightness` (0.5) and `StarBrightness` (0.75, shooting stars included).
Those defaults are what make it night.

**Everything snaps to a pixel grid.** `PixelGrid` sets the art's pixel size:
every layer samples at cell centres, so colour is flat across a cell and edges
land on cell boundaries. Smooth gradients and soft falloff are what made an
earlier attempt read as airbrushed rather than drawn, so they are deliberately
absent — the shader uses `step`, never `smoothstep`, for anything visible.

**Layer 1 — Space.** A flat plum ground: the reference's `#28061e` halved
toward black, i.e. `#14030f`.

**Layer 2 — Nebulae.** Clouds quantized into **three flat plum tones** with
hard stepped contours, like a topographic map. They orbit `NebulaAxis` at `NebulaSpin` — the player sits in
the eye of a very slow hurricane, a full turn taking about nine minutes — and
`NebulaMorph` reshapes them as they travel so they are not rigid stamps sliding
past. `NebulaFloor` is a threshold rather than a power curve: `pow()` never
reaches zero, so at any visible strength it washes the whole dome instead of
leaving gaps between clouds.

**Layer 3 — Stars.** Colour runs a **three-stop ramp** measured from the
reference — `#fee1ea` cores, `#ba6976` mid, `#8c445c` faint — rather than one
colour scaled up and down. The plum shift gets *stronger* as stars dim, which
is what keeps the field inside the background's palette instead of dusting it
with white specks. Per-star magnitude is squared and stepped so most stars stay
faint and only a few reach the core colour.

Three sizes, drawn as the reference draws them:

- **Large** (`StarLargeShare`, ~1%): a 3x3 core, four axis arms that are
  **exactly one pixel wide** and dozens long, and four shorter **dashed**
  diagonals — separate pixels with gaps, not solid lines.
- **Medium** (`StarMediumShare`, ~5%): one pixel plus four arms, either all
  straight or all diagonal.
- **Small** (the rest): a single pixel.

Arm brightness steps down in four flat levels rather than fading continuously.
**Only the small stars twinkle.** On a large star the pulse swings a 3x3 core
plus eight long arms at once, which reads as the whole sky flashing rather than
as distant points scintillating; the big stars are landmarks and hold steady.

The field **rotates** at `StarSpin` about the same axis as the nebulae, at a
third of the gas speed (a full turn in ~26 minutes against the clouds' ~9), so
the sky drifts as one while the stars clearly lag. The direction is spun
*before* snapping to the pixel grid, so cells and grid turn together as one
rigid field instead of stars sliding across a fixed grid.

**Stars sit on a SPHERE, not a cube.** The pixel snapping uses a cube-face
grid, and for several iterations the stars used it too. Every version showed
the cube, because the cube leaked in three independent ways:

- keying cells by `(cell, face)` gave the six faces independent randomness, so
  the pattern restarted at every edge;
- measuring arm length in face pixels clipped the arms of any star within one
  arm-length of an edge — about **14% of the sky**;
- enumerating candidate cells on the viewer's own face meant a star just across
  an edge was never considered, so it rendered cut in half.

The cube is also unevenly sized: cells shrink **2.17x** from a face centre to a
cube corner, so stars bunch there however the lookup is written. Patching the
leaks one at a time could never fix that.

Stars now live on a **ring lattice** with no faces at all. The sphere is cut
into `StarRings` bands of equal angular height, and each band holds cells
proportional to its circumference, which keeps every cell square to within
**5%** from pole to pole. Lookup is two divisions (`acos` for the ring,
`atan2` for the sector); neighbours are ring +/-1 and sector +/-1, the same
nine candidates the old face search used. Verified by enumerating every cell:
star density is uniform to **1.30x** across the whole sphere, poles included.

Two details it depends on:

- The tangent frame each star is drawn in comes from whichever world axis the
  star is **least** aligned with. A fixed axis leaves the cross product tiny
  near the poles and the frame badly conditioned.
- The view direction and the star are both converted to **integer pixel
  counts** in that frame before subtracting. Measuring a continuous angle and
  rounding afterwards leaves the two grids offset, so one star cell catches
  one, two or four screen pixels and every small star smears into a 2x2 block.

Also tried and reverted: a Fibonacci sphere has no faces either, but its
inverse does not localise — the nearest point sits at scattered index offsets,
so a shader would need a wide search rather than a fixed neighbourhood.

**Shooting stars** are modelled on a real meteor shower: mostly empty sky, then
a streak gone almost before it registers. Time is cut into short slots and
several are tested each frame, so shots can overlap and cluster rather than
arriving one at a time on a metronome; `ShootingChance` (default 0.07) is the
rarity dial. At the defaults that is about one visible every five and a half
minutes from a fixed view, each lasting **0.16 s** and crossing about **7
degrees** of sky (`ShootingArc`) — a blink across a small patch, roughly
47 deg/s, rather than a slow traverse that would read as a drifting object. Trajectories are random great
circles on the sphere, and the trail is drawn only *behind* the head with the
same stepped falloff and colour ramp as a twinkle arm.

**Layer 4 — Moon.** A still, pixel-drawn moon (`MoonDirection`, `MoonSize` as
an angular radius in degrees) on its own pixel grid as fine as the sky's:
off-white gold highlands (`MoonLight`) with grey maria (`MoonShade`) from a
stepped noise, round craters (`MoonCraters`) each with a lit and a shadowed
rim, two hard steps of limb darkening, and a smooth glow round it, forward
scattering like the sun's, in its light and the sky's dusty rose
(`MoonHalo`, `MoonHaloColor`). It keeps its place while the stars and clouds
turn, and it hides whatever is behind it. Its colours are kept below the
tonemapper's shoulder, or the Filmic curve flattens highlands and maria into
one pale disc.

Shots work in **direction space, not on a single cube face**. Confining them to
one face made five sixths of them invisible from any given view — they were
being generated correctly and simply never rendered where the camera was
looking.

**Two calibration notes.** The colour constants are *pre-tonemap* and were
measured against a render, not computed: the environment's Filmic curve crushes
darks so hard that the naive linear value for `#14030f` lands on screen at
about `#050104`. Re-measure if the tonemapper, `SkyEnergy` or the lighting
rig's ambient change. And any threshold applied to `fbm()` has to be centred on
its real range — two octaves span 0..0.75 with mean ~0.38, not 0..1 — or it
silently never (or always) fires.

Measured against the reference art (at full brightness), the layers land at
89% empty background / 10% nebula / 0.8% star pixels, versus 88% / 11% / 1.0%.
Star hue matches too: the render's faint tier is `#90425a` against the
reference's `#8c445c`.

### Performance notes

Sky shaders run per pixel over the whole background, so they are easy to make
accidentally expensive:

- The starfield searches a **3x3 grid on a cube face**, not a 3x3x3 grid in 3D.
  The 3D version costs 27 cell lookups per pixel and dropped this scene from
  60 to single-digit FPS on its own.
- The nebula's domain warp runs at **2 octaves** while the main density field
  runs at 5. Running the warp at full depth triples the cost of the most
  expensive part of the shader for detail the warp then smears away.
- The `AT_CUBEMAP_PASS` branch skips stars when baking the radiance cubemap.
  Stars contribute almost nothing to ambient light, and the bake covers six
  cube faces.
- The `Sky` uses `ProcessMode.Realtime` (fast filtering, 256x256 radiance).
  `Quality` uses importance sampling that costs far more and buys nothing for
  a smooth nebula gradient. If you author a sky with sharp bright features that
  should show up in reflections, `Quality` may be worth it — measure first.

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

  **The sky is excluded** (`PixelateSky`, off). The skybox is already drawn as
  pixel art on its own grid, and resampling one pixel grid onto another beats
  the stars into aliased noise that crawls as the camera turns. Sky is
  identified by depth: reverse-Z puts it at the far plane, well past any real
  geometry, so `SkyDepth` only has to sit above the furthest object.

  **Distant objects get finer pixels** (`PixelFarScale`, default 2.2x by
  `PixelFarDistance`), so the jump between a chunky foreground and a distant
  one is less dramatic. Two details this depends on:

  - The ramp is **quantized** into `PixelDistanceSteps` bands rather than
    varying smoothly. A grid that changes continuously with depth slides its
    pixel boundaries as the camera moves and the whole image shimmers; banding
    keeps a surface on one grid until it crosses an edge.
  - Depth is read at the **true** UV, before any snapping. Reading it through
    the snapped UV lets a pixel near a silhouette land on a sky texel (or the
    reverse), so objects would sample the sky's grid along every edge.

  `PixelNearDistance`/`PixelFarDistance` default to 3-22 m, the range where
  the ground near the player is. A range wider than the scene contains leaves
  the ramp doing nothing visible.
- **Dither** (off by default) - 4x4 ordered Bayer dither, applied in real
  screen pixels so the pattern stays fine even while pixelating.

Colour quantization is deliberately not included yet.

Note that screen-space pixelation is prone to *pixel crawl* with a free-look
camera - the pixel grid is fixed to the screen while the world moves, so
surfaces shimmer as you turn. It is exposed as an option rather than the
default for that reason; low-res textures with nearest-neighbour filtering are
the more stable route to a chunky look.

## Sandbox mode

Double-tap the jump key to toggle free-fly inspection: no gravity, no
collision, WASD to fly along the look direction, jump/crouch for straight up
and down, sprint to move faster. Double-tap again to drop back into normal
movement. Every value is exported on `PlayerController` under **Sandbox**, and
`AllowSandboxToggle` turns the gesture off entirely.

The mode is shown as the first line of the performance overlay, and **sandbox
forces that overlay visible** even when it is switched off in Settings. It is a
state reachable by an accidental double-tap, and without the readout a player
would be flying through walls with nothing on screen explaining why.

Four details the mode depends on:

1. **The first tap still jumps.** The detector runs before the jump code and
   `IsActionJustPressed` is idempotent within a frame, so ordinary jumping is
   untouched — only the second tap inside the window toggles.
2. **The window resets on toggle**, so a third fast tap starts a fresh pair
   instead of immediately flipping back.
3. **Position is written directly**, not through `MoveAndSlide`, which always
   resolves collisions and is exactly what would stop the camera entering a
   node. Disabling the collision shape alone is not enough.
4. **Sandbox forces first person.** A third-person spring arm pushes the camera
   out of anything solid, so it would shove the view around the moment you fly
   into a node, and the character model would fill the view from inside. The
   player's real camera preference is restored on the way out.

The pickaxe's don't-place-inside-yourself guard is skipped while in sandbox,
since an intangible free-flying player has no reason to be blocked from
building where they float.

## Porting a module to another project

1. Copy the module folder into the target project **at the same
   `res://Modules/...` path** (scene files reference scripts by absolute
   `res://` path).
2. `Modules/Core` is required by almost everything. Register its autoloads in
   the target `project.godot`:
   ```ini
   [autoload]
   SettingsService="*res://Modules/Core/SettingsService.tscn"
   UiStateService="*res://Modules/Core/UiStateService.tscn"
   ```
3. Copy the `[input]` section of `project.godot` (or define your own actions —
   every action name used by the player and the tools is an exported property).
4. Build the C# project.
