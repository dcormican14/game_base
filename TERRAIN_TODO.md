# Terrain Shape: First Implementation

The plan for the "original terrain" from `TERRAIN_UPDATES.md`: flat, hilly and mountainous regions.
Sky islands and caves come after this is tuned.

## Where things stand (2026-10-02)

Committed on `world-rework`, 70 tests passing. Terrain work is paused while the day-night sky and the sun and moon shaders are worked on.

When terrain work resumes, take it up from these sections below:

- **Next:** distant detail ("Later improvements", first item). The user chose faceted far rock, and keeping each level further out.
- **Open from the sky-island pass:** frame rate over a zone; spawns at a zone's middle; raising on canyon walls; density feel.
- **Open from earlier:** the ridge sparkle, the low-angle push limit and the mosaic speckle in the handover band; very fast travel; zone rims on slopes; island undersides; and the three tool items in Phases 3 and 6. Raising onto bare rock needs the user's decision.

## Status (first implementation done)

Built and tested: 63 tests pass (the 43 from before, on the plain round
planet, and 20 new). First loads take 17-28 s on every kind of spot measured
(`TerrainCapture`), against the 60 s budget. Holding Raise for a minute on a
jagged range runs at 60 fps with memory flat.

Where the build differs from the plan:

- **Every cell is interpolated**, not just the far ones. The shape is sampled
  every other node (4 units) and every cell reads between samples; nothing is
  evaluated exactly per cell. It is fast enough, and it means the far
  terrain's first ring samples the very same points as the chunks.
- **The seam overlap is not sunk.** Sinking the one-cell overlap cut a groove
  along every seam between blocks of the same level; unsunk, same-level blocks
  draw identical triangles there.
- **Cross-fading** happens where a first-ring block hands over to a real chunk
  (it fades out over 0.3 s). Swaps between far levels are instant, and only
  happen once the replacement is ready.
- **Far terrain follows the camera**, not the player, so free flight and
  captures see the right detail; the first ring still follows the player the
  chunks load around.
- **Regions are dealt along a ridged noise**, not shuffled, so mountains form
  chains with hills round them. The split is still exactly 40/35/25.

Fixed along the way:

- **Raise stalled away from the planet's axes.** On sloped ground where the
  lattice leans against gravity (almost everywhere but the poles), a neighbour
  clamped deep in the ground read as a cliff and stopped every raise dead.
  Clamped-deep neighbours no longer count (`ParticleSculpt.RaiseFraction`). On
  a 22-degree hillside a held raise now builds a small shelf (about half a
  unit) and stops; on flat ground between the axes, about 1.5 units.

Open:

- **Raise onto bare rock** needs a decision: the shovel only works sand (tools
  work one form), so it cannot aim at a cliff at all.
- **Lower down to rock** is the open issue from before; thin sand near cliffs
  makes it more common.
- **Taper edge**: sand under 1.5 units is left off (`SandMinimum`), which
  keeps rock from speckling. Judge it in play.
- **Tuning**: hills top out around 25-30 units (the plan said 20-40), and a
  few small ranges only reach ~40 before their edges pull them down.
- **Seen from the air**, a faint line marks where the loaded chunks meet the
  first ring. From the player's eye it is 200+ units away.
- **Found along the way, not new**: a section's collision can be missing from
  physics queries for a frame when streaming rebuilds it (its mesh is not).

## Decisions

| Topic | Decision |
|---|---|
| Mountain height | Big: peaks 150–300 units above the surrounding ground |
| Far view | Far terrain drawn to the horizon in 3D, arches and overhangs included, detailed enough that the handover to real nodes isn't jarring |
| Loading | May be slower than today, but the first load must reach a playable world in under a minute |
| Shape | 3D from the start (overhangs, arches, leaning spires possible) |
| Sand on steep ground | Thins away past about 45°, leaving bare rock cliffs and crags |
| Region size | Large, continent-like, about 1–3 km across (roughly 100 on the planet) |
| Transitions | Any region can border any other, blended smoothly over a few hundred units |
| Mountain look | Each range picks a character: jagged, rounded, towering or mesa |
| Spawn | Anywhere: wherever the spawn direction lands |
| Water | None yet, but some low ground dips below a sea level so it can be added later |
| Seed | Random at launch, shown in an editable field on the main menu; the world is built from it |
| Mix | 40% flat, 35% hills, 25% mountains |

### Defaults I picked (easy to change while tuning)

- **Blend width** between regions: about 300 units.
- **Heights:**
  - Flat: within ±2 units.
  - Hills: 20–40 units.
  - Mountains: 150–300 units.
- **Basins:** some flat and hill ground dips as low as 30 units below sea level.
- **Sea level:** 8 units below today's surface radius, so ordinary flat ground stays dry.
- **Sand:** 6 units deep on ground up to 40°, thinning to nothing by 50° (centred on the 45° a Raise stops at). Overhang undersides are always bare rock.
- **Seed text:** a number is used as-is; any other text is hashed, so "banana" is a valid seed.
- **Tuning values** live in one `TerrainSettings` resource so they can be changed in the inspector.

---

## Phase 0: Groundwork

- [x] **Terrain shape interface.** Split *what shape the planet is* from *what it is made of*.
  - Add `ITerrainShape` with `double Distance(Vector3D p)`: a signed distance to the surface, positive inside the ground and in world units, in the world's local space.
  - It also gives per-point info for later layers: region weights, height above sea level, slope.
  - `PlanetGenerator` asks the shape for distances. It no longer assumes a sphere.
- [x] **`FlatTerrain` shape.** Today's perfect sphere, kept as a shape.
  - `TestWorlds` and every existing test use it, so the 43 tests keep their flat ground and stay valid.
  - The real planet uses the new shape.
- [x] **Double-precision points.** Sample positions in doubles. The surface is 6,000 units out and noise at float precision would shimmer.
- [x] **Noise library.** A pure C# 3D noise (an OpenSimplex2 port; public domain) that takes doubles and a seed.
  - It has no shared state, so worker threads can call it freely.
  - It is deterministic across machines.
  - Godot's `FastNoiseLite` is a Resource, works in floats and is not safe to share across threads.
- [x] **Sphere-seamless sampling.** All 2D-style fields ("height at this spot") sample 3D noise at the point's *direction* × a scale, so there are no seams or pole pinches.
- [x] **`TerrainSettings` resource** (`.tres`) holding every tuning value above, exported on `Planet`.

## Phase 1: Seed

- [x] **World seed.** A seed value (ulong) that `Planet` passes to the terrain shape. Every noise and hash call is keyed off it.
- [x] **Main menu field.**
  - A seed box above Play, filled with a random seed at launch and editable.
  - A small reroll button beside it, and Play builds the world from the box.
  - The field keeps the gold and cream UI style.
- [x] **Carry the seed into the game.** Pass it into the game scene through a small static `WorldOptions`, not a scene parameter. The scene can still start directly from the editor (F6), falling back to a fixed default seed.
- [x] **Show the seed in game.** Put it in the pause menu so a good world can be written down.

## Phase 2: The Terrain Shape

### 2a. Regions

- [x] **Region points.** Place about 100 points on the sphere (a Fibonacci sphere, jittered by seed). Each point owns one region.
- [x] **Assign types from a shuffled list,** so the split is exactly 40/35/25 rather than "about that on average".
- [x] **Give each mountain region a character** at random: jagged, rounded, towering or mesa.
- [x] **Ragged borders.**
  - Warp the direction with low-frequency noise before finding the nearest region points, so borders wander instead of being straight Voronoi lines.
- [x] **Blending.**
  - Weight each nearby region by how much closer its point is than the next nearest, so a border blends over the blend width.
  - Any pair can blend, flat included.
- [x] **Region lookup cost.** Brute force over 100 points is fine at first. Add a spatial bucket (cube-face grid) if profiling says so.

### 2b. Heights (the large shape)

- [x] **Flat:** very gentle roll within ±2 units, the same flat-to-build-on ground as today.
- [x] **Hills:** domain-warped rolling noise with hills and dips of 20–40 units.
- [x] **Basins:** a low-frequency mask in flat and hill regions that sinks some areas below sea level for future lakes and seas.
- [x] **Mountains:** a mountain mask that is 0 at the region's edge and 1 in its core, so peaks sit inside the region and the blend ramps up to them. Each character has its own height function:
  - **Jagged:** ridged multifractal chains with sharp crests and a few standout peaks.
  - **Rounded:** broad domes and smooth ridges.
  - **Towering** (was spired; changed on review, the needle field read as unfinished): extra-tall jagged peaks, a craggy massif under a few horns reaching 350-500 units (`TowerPeakLow`/`TowerPeakHigh`).
  - **Mesa:** terraced plateaus with sheer walls and flat tops (a terrace curve on the height).
- [x] **Combine.** Take the region-weighted blend of every type's height, then add sea-level offsets. This gives the heightmap part of the shape.

### 2c. The 3D part

- [x] **Distance.** The shape's distance is `(surface radius + height) − distance from centre`, plus a 3D term.
- [x] **3D domain warp** of the sample point, scaled by region, to make overhangs, undercuts and leaning forms:
  - strong in jagged and towering mountains;
  - light in hills;
  - none in flats.
- [x] **Occasional arches** and natural bridges in mountain regions. A 3D noise band is carved away where it crosses a ridge.
- [x] **Size features for the far view.** Arches, spires and overhangs must be big enough to survive the far terrain's coarser cells at the distance they're seen from. As a starting point, arch legs and spans should be at least 16–24 units thick. Anything thinner is detail you see only up close.
- [x] **Mesa caprock:** a slight overhang on the top terrace's lip.
- [x] **Bound the 3D term.** It must stay within a known maximum so chunk culling (Phase 4) stays correct.
- [x] **Keep the field a true distance.**
  - Divide by the gradient's length (finite differences) near the surface, so the stored distance is honest on steep ground.
  - The particle fill *is* a distance, and the mesher, sculpting and Level all rely on neighbouring cells agreeing. The research earlier (Zylann's sculpting) showed an incoherent field breaks growing and smoothing.

## Phase 3: Materials (rock core, thin sand)

- [x] **Rock.** A node is rock (raw) when its *site* is deeper below the surface than the local sand depth. Raised terrain is rock all the way through, and only the skin is sand.
- [x] **Sand depth from slope.** Compare the surface normal (the field's gradient) with gravity's up:
  - up to 40°: 6 units deep;
  - between 40° and 50°: a smooth taper to nothing;
  - past 50° (cliffs and overhang undersides): none.
- [x] **Sand cells.** Cells within the sand band get their fill from the true distance, exactly as the shell does today. Cells past it are air.
- [x] **Material choice in one place.** Keep it in `Classify`, so biomes can later choose the top material there.
- [ ] **Check the taper edge.** Where sand is under a node thick over faceted rock, it may look speckled. Tune the taper or require a minimum sand thickness.

## Phase 4: Generation speed and bounds

The shape is far more expensive than one sphere test per cell, and big mountains mean many more chunks cross the surface.

- [x] **Cull whole chunks.**
  - Evaluate the distance at the chunk's centre.
  - If it is further from the surface than half the chunk's diagonal (times the field's slope bound, plus margin), the chunk is all sky or all rock. This replaces the fixed-radius early-outs.
- [x] **Coarse-then-fine sampling.**
  - Sample the distance on a coarse grid (every 4 cells) and interpolate it.
  - Evaluate exactly only for cells near the surface band.
  - Share one region lookup per coarse sample.
- [x] **Benchmark chunk generation** with a test that reports time per surface chunk. The budget: the first load reaches a playable world in **under 60 seconds**, far terrain included, measured from pressing Play to the player being unfrozen. Streaming may be slower than today, as long as the far terrain covers anything not yet loaded so there are no holes.
- [x] **Load-time capture.** Launch the real game for several seeds and spawn directions (flat, mountain, border) and log the time to playable. Fail if any run goes over 60 s.
- [x] **Share the work with the far terrain.** Both the near chunks and the far terrain sample the same shape, so whatever makes one fast (culling, coarse sampling, region lookup) is written once and used by both.
- [x] **Check the "wholly sky" assumption** everywhere it is used. Air above a 300-unit mountain is no longer "anything past the radius".

## Phase 5: Spawn and streaming

- [x] **`Planet.SurfacePoint`** finds the real surface along a direction. It searches the shape from above the highest possible peak down to the first solid ground, so it doesn't land under an arch or inside an overhang.
- [x] **`PlanetSpawn`** uses the new surface point, keeping the rule to never unfreeze the player before the streamer says the world is ready.
- [x] **Ground probe.** The streamer's probe (96 units) must still find ground at the spawn point, measured from the spawn height rather than the base radius.
- [x] **Steep spawns.** Spawning "anywhere" can land on a cliff face or a peak. Check that the player stands, or slides somewhere sensible, rather than wedging into rock.

## Phase 6: Tools on the new terrain

- [ ] **Raise onto bare rock.** Check that it lays sand on exposed rock (cliffs, mesa tops), not only on existing sand.
- [x] **Raise on generated slopes.** Check that the 45° stop behaves on hills that were already steep when generated.
- [x] **Level and Smooth.** Check them on mountain ground and where sand meets rock.
- [ ] **Lower down to rock.** Revisit the open issue (the last sand layer vanishing all at once): thin sand at the taper makes it far more common.
- [x] **Surface disc on sharp ground.** Run the stress capture on a mountain. Sharp crests and overhangs are where the surface disc's walk was capped.

## Phase 7: Far terrain

The far view is 3D: arches, overhangs and leaning spires show on the horizon. It meshes the full distance field (2a–2c), not a heightmap. This is the same approach as volumetric level of detail in voxel engines such as Zylann's Voxel Tools.

- [x] **`FarTerrain` node.** A set of level-of-detail rings around the player:
  - Ring 0 is the real node chunks.
  - Each ring further out samples the shape's distance on a grid twice as coarse: 4-unit cells, then 8, 16, 32 and so on.
  - Each ring's blocks are the same size in cells as a chunk, so every ring costs about the same to build.
- [x] **Mesh with surface nets.** Mesh far blocks the same way as the particle mesher, so the far ground has the same character as the near sand.
  - Rock takes flat, faceted shading so it reads like the raw nodes' Voronoi facets.
  - Sand takes smooth shading.
- [x] **Reach.** A 300-unit peak is visible from about 2 km, so draw out to about 3 km (5–6 rings). The camera's far plane (4,000) covers that.
- [x] **Seams between rings.** Where a fine ring meets a coarser one, close the gaps with skirts, or by stitching the edge cells to the coarser ring's vertices. No cracks may show against the sky.
- [x] **A handover that isn't jarring.**
  - The first far ring (4-unit cells) is only twice as coarse as real nodes, so the shape barely changes when real chunks replace it.
  - Swap per block, not by radius. A far block hides only once every real chunk it covers has been meshed, so streaming never opens a hole.
  - Cross-fade over a few frames with a dither, so the swap doesn't pop.
  - Near the handover the far mesh sinks by a fraction of a unit, so any overlap stays under the real ground.
- [x] **Keep detail at distance.** Check that the arches, spires and overhangs from 2c survive each ring at the distance it's drawn. If one vanishes or breaks up, raise the detail of that ring, or widen the feature (see "Size features for the far view").
- [x] **Build off the main thread** (same worker style as chunk meshing). Rebuild only the rings that shift as the player moves, nearest first. The initial far build counts toward the 60-second load.
- [x] **Match the look.** Sand and rock colours come from the same slope rule as Phase 3, with the same pixel filter and lighting, plus some atmospheric fade with distance.
- [x] **Known limit:** sculpting far away won't show in the far view. It only draws the generated shape.
- [x] **Keep it extensible.** Sky islands and caves come through the same distance field, so the far rings will draw them without a separate system.

## Phase 8: Tuning tools

- [x] **Planet map export.** A test-runner command writes an equirectangular PNG of the planet for a seed: region types, mountain characters, height shading and basins. This is the fastest way to judge layout and the 40/35/25 mix.
- [x] **`TerrainCapture` scene** (like `LevelCapture`). It flies to a flat, a hill region, each mountain character and a border, and screenshots each for review.
- [x] **Debug readout** (toggle key) of what's under the player: region type and character, blend weights, height above sea level, slope, sand depth and seed.

## Phase 9: Tests and docs

- [x] **Determinism:** the same seed gives identical chunk bytes, and different seeds differ.
- [x] **Thread safety:** generating chunks in parallel matches generating them one at a time.
- [x] **Coherent distance:** neighbouring particle cells never disagree by more than a node plus a margin.
- [x] **Culling:** chunks skipped as all sky or all rock match a full per-cell pass on a random sample of chunks.
- [x] **Sand rules:** flat ground has 6 units of sand over rock, and ground past 50° has none.
- [x] **Mix:** sampling thousands of directions lands within a few percent of 40/35/25.
- [x] **Heights:** mountain peaks fall in 150–300 and hills in 20–40.
- [x] **Spawn:** the player ends up standing on ground for many random directions and seeds.
- [x] **Seed field:** a numeric seed round-trips, and text seeds hash consistently.
- [x] **Far terrain agrees with real ground:** at the handover, the first far ring's surface lies within a node of the real chunks' surface.
- [x] **Far terrain keeps arches:** a ray through an arch's opening passes clear in every ring that draws it, and a ray at its span hits.
- [x] **No holes while streaming:** after a teleport, every direction from the player hits either real ground or far terrain on every frame while chunks load.
- [x] **Load time:** the load-time capture from Phase 4 stays under 60 s.
- [x] **Existing tests:** all 43 still pass on `FlatTerrain`.
- [x] **Docs:** update the README's planet section, and the docs on `PlanetGenerator`, `Planet` and `PlanetSpawn`.

## Later (not this pass)

- **Biome layer:** consumes the per-point info from Phase 0 (region weights, height, slope, sea level) and picks top materials.
- **Water** in the basins.
- **Sky islands and caves.** Both carve and add through the same distance field, and the far terrain draws them as it is.

## Suggested order

Build 0 → 1 → 2 → 3 → 4 → 5, then tune with 8 until you're happy with the shapes. Then do 6, then 7 (the far view is the largest single piece), then finish 9. Tests are written alongside each phase, not saved for the end.

The shapes are tuned before the far view is built, but arches and spires are sized with it in mind from the start (2c). Once 7 lands, a second tuning pass checks how every feature reads from a distance.

---

# Second pass: basins, canyons and sky islands

| Topic | Decision |
|---|---|
| Mix | 15% flat, 30% hills, 25% mountains, 30% basins |
| Basins | Their own region type: broad bowls, floors 60-120 below the plains; the small hollows in flats and hills stay |
| Canyons | A handful (5-10): winding from a basin out across plains and hills; never through mountains or sky islands |
| Sky islands | 3-5 zones, anywhere: a chasm about 250 deep with a rubble floor; great islands near the rim, smaller and higher toward the middle, up to about 400 over the rim |
| Islands | Floating earth chunks: sandy near-flat tops, rocky undersides tapering to jagged points |
| Traversal | Gaps you build across: a few units near the rim, widening higher up |

- [x] Basin region type, dealt lowest on the ridged noise (furthest from ranges); depth per basin from its size draw.
- [x] Hollows (the old in-region "basins") renamed so the two are not confused (`HollowDepth`).
- [x] Canyons laid out once per planet as paths; stepped walls, sandy floor, floor climbing from the basin to the head.
- [x] Sky-island zones: chasm with rubble floor, three layers of islands (great, middle, small clusters).
- [x] Islands as separate solids joined by union, so the field stays continuous where one gives way to the next.
- [x] Culling near islands: `ITerrainShape.Within` lists the islands that can reach a block; blocks measure only those, and the slope bound holds inside the block. (A first cut that just refused to cull near islands took 72 s to load in a zone.)
- [x] Far terrain's horizon trusts only the lowest ground anywhere: trusting "at least 50 below flat" left basin and chasm floors undrawn.
- [x] Tests: the mix, basin floors, canyons (count, route, depth), island zones (count, floating ground, chasm depth); skipped chunks and the slope bound cover basins, canyons and island zones.
- [x] Map, stats line, F3 readout and captures know basins and sky islands (`--spots=basin,canyon,islands,rim`).

Measured on seed 1: 29 basins with floors 61-117 down (median 90); 10 canyons cut 32-96 below the ground beside them; 4 zones, floors about 250 below their rims, ground floating over a third to nearly half of each. Loads: 27 s in a basin, 40 s in an island zone, 39 s on a towering range (horizon culling now trusts less, so more far terrain is built everywhere).

Changed on review (2026-09-29):

- [x] **Canyons are their own terrain**: `RegionType.Canyon`, reported between a canyon's rims (stats line, F3 readout, map in red-brown).
- [x] **Basins are oceans**: the 30% share gathers into 2-4 connected oceans (`OceansLow`/`OceansHigh`), each grown from a seed far from the ranges. Floors: a shelf 60-120 down near the coast, falling to 250-400 out past `ShelfWidth`/`DeepWidth`. Seed 1: 4 oceans of 7-8 regions, floors 57-356 down.
- [x] **Canyons start at a coast**: mouths spread along the shores, the floor as deep as the shelf where it meets the sea, a trench carrying on a little way across the shallows. Seed 1: 10 canyons, cut 65-117 below the ground beside them.

Streaming fixes (2026-09-29):

- [x] **Holes where far levels meet**: a block's overlap on a side facing anything but a same-level block sinks a cell under its neighbour (shader, per side, as neighbours change). No grooves between same-level blocks.
- [x] **Hard border to the real chunks**: real ground dissolves out 140-190 units from the camera (raw: the engine's reversed pixel-dither distance fade; sand: the same test in its shader) while the far terrain dissolves in on complementary pixels. Far sand has the particle surface's grain and ripples up close, far rock the nodes' two-unit cell shading.
- [x] **Low-res ground left over drawn chunks**: under drawn chunks nearer than the handover the far terrain is not drawn; its overlap into drawn chunks sinks.
- [x] **Falling behind**: space probed as all air or rock is never visited again (wanted blocks ~5,200 -> ~2,900 at spawn), stale builds skipped, blocks under drawn chunks built last, workers = half the cores, layouts at most every 0.1 s. Flying at 20 units/s: nothing waiting, 60 fps. At 100 units/s it still falls behind (fast travel only).
- [x] **Distant sky islands**: blocks over a zone keep their detail twice as far out.
- [x] **Ledge at the handover on rock**: measured, sand matches within 0.01 but real rock (whole Voronoi nodes) stands 0.65 off the far terrain's smooth rock on average, up to 3.7. The band is now 110-210 (was 140-190) and the streamer loads 5 chunks (was 4; unloads at 7) so real ground reaches its end. Unloads now go a few a frame: dropping a whole slab of dense rock at once hitched 110-145 ms. Loads 25-45 s; sprinting over the towering range, frames average 18-22 ms (was 16-20), with one 164 ms hitch in 30 s.

Diamonds and ledges, second pass (2026-09-29). The first pass read the same in play; looked at from eye level it had three faults of its own:

- [x] **Sinking opened the cracks it was for**: the per-side sink counted pruned or empty neighbours as "not same level" and pulled overlaps down beside them, leaving lines of sky along seams and beside fresh chunks (the diamonds). Replaced by **skirts**: a curtain hanging straight down from each block's rim, only as far as the ground below goes (so none hangs into an arch or under an overhang). Hung along the surface normal instead, curtains stood up out of valley floors; `FirstRingMeetsTheDrawnGround` now counts skirts and would catch that.
- [x] **Complementary dither left gaps**: wherever the real ground had a hairline seam or a hollow between rock nodes, neither ground drew. Now the far terrain is drawn whole but pushed 2.5 units back along each line of sight: the real ground always wins where drawn, and every gap shows far ground. Real ground still dissolves 110-210; far blocks over drawn chunks (any level up to 3) are cut nearer than 110.
- [x] **Ledges between far levels**: geomorphing. Each vertex carries the nearest point on the next level's mesh (rebuilt exactly from every other sample, `FarMesher.CoarseGround`) and slides onto it as it nears the distance that level takes over at (`FarTerrain.MorphRange`). Level 1 against level 2 on mountains: 0.80 apart unmorphed, 0.03 morphed (`MorphedGroundMeetsTheCoarserLevel`).
- [x] **The visible border was the rock's look**: real rock is lit node by node; far rock was a flat two-tone checker. Far rock is now a mosaic of node-sized Voronoi cells, each with its own tilted light and shade, fading to its average over 210-450.
- [x] Split readiness ignores level-1 blocks that would be hidden under drawn chunks anyway (they are built last and kept coarse blocks standing over the player).

## Sky islands, third pass (2026-09-29)

The user's picture: the zone's land is just starting to crack and float off at its edges and floor, and the middle has small chunks drifting into space. The whole cavity of the crater is filled with islands of different shapes, and they climb further up into the sky.

Decisions (asked and answered):

- **Frozen in place.** Pieces are terrain like everything else: diggable, buildable, and in the far view. "Drifting" comes from placement, not motion.
- **Height.** The debris reaches about 1,000 above the rim (was about 400).
- **Kinds of pieces.**
  - Rock + topsoil slabs: sand on a flat top, rock beneath, hanging points under.
  - Bare rock chunks: angular, steep-sided, so no sand settles.
  - Shards and spikes: long thin splinters.
  - All stay upright; tilted or overturned pieces were not wanted.
- **Density.** Packed and hoppable at the edges, then slowly less traversable the further up and inward you go.
- **Rim.** Cracked, barely lifted. The ground round the rim and the top of the wall are split into plates by deep crevices, and the plates are raised only a few units.
- **Floor.** Cracked, still resting. The floor is split by crevices into plates sitting on the ground; the lifting starts above it.
- **Overall shape.** The debris fills the whole crater low down and narrows to a point as it climbs, thinnest and highest at the centre.
- **Load time.** Longer loads are accepted: build everything in full detail, even if spawning in or near a zone takes 1-2 minutes.

Built (2026-09-29):

- [x] **Pieces** are laid out per zone, lazily, on four lattices:
  - rim floes (44 apart, one tier, drifting up 140 across the outer 30%);
  - great slabs (110, low);
  - middle pieces (52 across, 40 up);
  - small pieces (26 across, 24 up).
  They are kept under a crest of `30 + 970 * inward^1.6` over the rim, and from overlapping much. Sparser and smaller along the climb.
  Seed 1, zone 0: 2,818 slabs, 4,898 chunks, 3,964 shards, laid out in 0.18 s.
- [x] **Lookup.** Pieces are filed in 32-unit buckets, each listing those within 12 (`Plateau`). The distance is exact down to -12 and levelled there, so it stays continuous. `Within` gives the islands view only to boxes a piece's bounding ball reaches. The slope bound holds over the zones (4.06 worst of 8, all box sizes).
- [x] **Cracks.** The rim uses a Voronoi pattern of 36-unit plates, crevices 3.5 wide and 25 deep, plates lifted up to 2.5, reaching 180 out. The floor uses 55-unit plates, crevices 2.5 wide and 12 deep. The zone cap now reaches past the cracks.
- [x] **No per-instance shader values in the far terrain.** Godot sets aside 16 slots for every instance that has any, and its room ran out at about 4,000 blocks, fewer than an island zone draws. Morph distances now ride in the mesh (CUSTOM0.w/CUSTOM1.w); handover is a second material.
- Heap: 914 over the rim at the middle, 98 near the edge. Volume filled about 4% low and 1% high.
- Loads: 49 s at a rim, 73 s at a zone's middle. Frames: about 41-49 fps looking into a zone at eye level (3-4M triangles); an aerial view over the whole heap drops to about 24 fps.

Open, from this pass:

- [ ] **Frame rate over a zone.** 5-7M triangles in view from the air. Ideas: coarser far levels for small pieces, or fewer small pieces far from the player.
- [ ] **Spawning at a zone's middle** probes one column from the sky. The sparse top of the heap usually misses it, so the player lands on the crater floor. That's fine as "spawn anywhere", but if spawns should land on the heap, probe a few columns.
- [ ] **Raising on a canyon's stepped wall does nothing.** Found when a canyon moved into the hillside test's first site. Raised 0.00, the height measured 2.8 under the surface point. Investigate with the shovel on a canyon wall. (Canyons moved because the wider zone cap widens the zones canyons keep clear of.)
- [ ] **Density feel.** About 4% of the lower crater is solid. If it should read fuller, raise the middle layer's keep, or the slab sizes.

## Later improvements (to look at)

Noted 2026-09-29, after the second streaming pass. None blocks play; each is worth a look.

- [ ] **NEXT, after the sky islands: distant detail.** In the user's words: "at a distance, we miss most of the details and the transition to detailed chunks is jarring. Comparing nearby cliff faces with distant ones feels like I am playing 2 separate video games." They want the distant terrain drawn at a higher detail and shape, so the transition is smoother, with no terrain jumping and no drastic detail differences.
  - *Why it happens:*
    - **Shape.** A cliff 800 away is drawn with 16-unit cells, so crags, gullies and the 14-unit warp smaller than that vanish. Level ranges now: real chunks 0-210, far level 1 (4-unit cells) to ~540, level 2 (8) to ~770, level 3 (16) to ~1,540, level 4 (32) to ~3,070, level 5 (64) to 4,000; doubled over island zones.
    - **Look.** Real rock is faceted nodes, each lit on its own. Far rock is a smooth surface with a painted mosaic, gone to flat colour past 450.
  - *Chosen approach (the user picked both):*
    1. **Faceted far rock.** Real flat-sided facets that catch light as the nodes do (flat-shaded faces on a node-sized pattern, lasting further out), not only a painted mosaic.
    2. **Detail further out.** Raise `FarTerrain.SplitFactor` from 3 to about 4.5, so each level is kept about 1.5x further out. That is roughly 2x the far triangles and longer loads.
    - *Measure before and after:* load times per spot, frame time, and triangle count.
  - *Not chosen for now:* a finer first ring (2-unit cells past the real chunks), and loading real chunks further out. Keep them as fallbacks if the hand-off still reads as a jump.

- [ ] **Ridge sparkle in the handover band.**
  - *What:* along mountain silhouettes 110-210 units out, a few speckled sky pixels.
  - *Why:* real rock (whole nodes) stands up to about a node higher than the far terrain's smoother ridge. As the real rock dissolves there is nothing behind it but sky.
  - *Idea:* swell the far terrain along its normal by about 1 unit inside the band, easing to 0 by around 450 so nothing pops. Check that the swell doesn't then cover the real ground at low angles (see the next item).
  - *See it:* `TerrainCapture --spots=jagged`, crop `jagged_band.png` along the ridge.
- [ ] **Low-angle limit of the push-behind.**
  - *What:* the far terrain sits 2.5 units behind the real ground along the line of sight. That isn't enough at very shallow angles, where a far surface slightly higher than the real one lands in front of it inside the band.
  - *Why it's minor:* at eye height the band is only a few pixels tall on flat ground.
  - *Idea:* scale the push with distance, or with how shallow the view is. Only worth doing if it shows up in play.
- [ ] **Island zones load in 50-58 s**, close to the one-minute budget (other spots 25-35 s). *Since superseded:* the third sky-island pass raised this to about 75 s at a zone's middle, and the user accepted longer loads for zones. Keep only if loads should come down again.
  - *Why:* about 5,800 level-1 blocks are wanted over a zone, because island blocks keep their detail twice as far out (`IntricateDetail`).
  - *Ideas:*
    - lower `IntricateDetail` for level 1 only;
    - skip probing blocks known to be deep in the chasm;
    - count only the nearer rings toward readiness and let the rest fill in after the loading screen.
  - *See it:* `TerrainCapture --spots=islands` prints the ready time and blocks by level.
- [ ] **Very fast travel** (100+ units/s) outruns the far terrain and chunk streaming; the view thins until you slow down.
- [ ] **The rim of a zone** takes whatever height the land round it averages; a zone on a mountain's flank has a lopsided rim.
- [ ] **Islands' undersides** are dark rock seen from below; biomes may want to hang roots or vines there.
- [ ] **Speckle on the far rock mosaic** at mid-distance: node-sized cells only 2-3 pixels across shimmer when moving. The fade now runs 210-450 (was 250-600); if it still shimmers in play, bring the fade in further or blur the cells into their average sooner.

