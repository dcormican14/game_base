# Terrain, second pass: plan

The plan for `Terrain_TODO_2.md`, in order of priority:

1. Smoother hand-off between near and far terrain, using node clumps.
2. A planet twice as wide.
3. Sky islands in large, fractal clusters.

It picks up from `TERRAIN_TODO.md`. That file's next item was "distant detail", with two chosen fixes: faceted far rock, and each level kept further out. This plan covers both of those and replaces them.

## Decisions (answered 2026-10-07)

| Topic | Decision |
|---|---|
| Clump sizes | Medium = 2x2x2 nodes (4 units), long = 4x4x4 (8 units). The aim is to save time without losing much detail. If coarser clumps (16 units) hold up, report it (experiment in 1g). |
| Larger planet | Features stay the same size; there are more of them. |
| Sky islands | Many islands, in **fractal clusters**: large islands with medium islands around them, each medium with its own small ones. Shapes can be a little misshapen; they don't all have to be perfect. |
| Detail against cost | **Detail first, for now.** Take the fuller ring reach (near ring dissolving over 180-300) and the proposed ring ends. Measure everything, but efficiency comes in a later pass. |

Because detail comes first, the gates below **report** their numbers and stop only on breakage:
- load over budget (60 s; 120 s in island zones);
- holes;
- frame rate falling to where play suffers.

Cost alone does not stop a step; it is written down for the efficiency pass.

## Where things stand (2026-10-02)

- On `world-rework`, 70 tests pass. The day-night sky work is not committed yet. It touches `FarTerrain.cs` and `FarTerrain.gdshader` (`SetSky`, haze glow). Commit it before any of this starts, so the terrain diffs stay separate.
- Today's level ranges:
  - Real chunks: 0-210 (dissolving out over 110-210).
  - Far level 1 (4-unit cells): to about 540.
  - Level 2 (8-unit): to about 770.
  - Level 3 (16-unit): to about 1,540.
  - Levels 4-5: out to 4,000.
- Every far level is drawn as surface nets: one smooth surface, with a painted Voronoi mosaic on the rock that fades out by 450. Levels geomorph into each other, so there is no ledge between them. Real rock is faceted Voronoi nodes, and that difference in look is the "two separate video games" problem.

---

## Part 1: Clump LOD

### What the idea maps onto

| Your ring | Clump | Our grid | Today's far level it replaces |
|---|---|---|---|
| Short | 1 node (2 units) | `VoronoiGrid(2)`, the real chunks | (real chunks) |
| Medium | "4-node clump": 2x2 seen from above, 2x2x2 in 3D, 4 units | `VoronoiGrid(4)` | level 1 (4-unit cells) |
| Long | "16-node clump": 4x4 seen from above, 4x4x4 in 3D, 8 units | `VoronoiGrid(8)` | level 2 (8-unit cells) |
| Beyond | none: today's surface nets | | levels 3-6, unchanged |

I read "4" and "16" as the footprint seen from above (2x2 and 4x4). That makes the clumps 2x and 4x a node's width, which lines up exactly with far levels 1 and 2. Each clump draws as one big Voronoi cell of its *predominant* type: rock as a faceted polyhedron, sand as the particle shell over it. So far ground looks like large raw nodes with a thin topsoil top.

The code already allows this almost for free. `VoronoiGrid`, `PlanetGenerator`, `SectionSample`, `RawNodeMesher` and `ParticleNodeMesher` all take the node size as a parameter. A clump block is the real chunk pipeline (classify the cells, then run both meshers) run on a grid 2x or 4x coarser. "Predominant type" comes free too: `Classify` decides a cell from the shape at its site, the clump's centre, which is the same as asking what most of the clump is. Nothing needs the full-resolution nodes underneath.

### Is it fast? Measured

I wrote a throwaway benchmark (since deleted). Seed 1, 9 spots, a 3x3x3 group of blocks at each spot. For each block I built today's far mesh (`FarMesher.Build`) and a clump mesh (`PlanetGenerator` on a coarse grid, then `RawNodeMesher` and `ParticleNodeMesher`). All single-threaded.

**First run, rules unchanged: clumps cost 3.2x the triangles at medium.** On flat ground, the 6-unit sand bed is thinner than 1.5 clumps. So a rock clump right under the shell is never "covered", and its top faces are drawn hidden under the sand everywhere: about 35,000 wasted triangles per spot on the plains.

**Second run, with the rock buried under at least 2.5 clumps of sand at clump scale:**

| Ground | Medium (L1) triangles, nets → clumps | Vertices, nets → clumps |
|---|---|---|
| Flat | 14,900 → 11,500 (0.8x) | 6,900 → 11,100 (1.6x) |
| Hills | 14,000 → 9,700 (0.7x) | 6,500 → 8,900 (1.4x) |
| Border | 14,100 → 10,000 (0.7x) | 6,500 → 9,200 (1.4x) |
| Mesa | 17,000 → 24,400 (1.4x) | 7,900 → 33,000 (4.2x) |
| Jagged | 21,100 → 46,600 (2.2x) | 9,800 → 69,000 (7.1x) |
| Towering | 20,800 → 51,500 (2.5x) | 9,700 → 77,900 (8.1x) |
| Canyon | 17,600 → 43,800 (2.5x) | 8,200 → 67,100 (8.2x) |
| Island rim | 34,200 → 85,200 (2.5x) | 15,800 → 127,600 (8.1x) |
| Island zone | 36,400 → 63,900 (1.8x) | 17,400 → 89,500 (5.2x) |
| **All, medium** | **1.8x** | **5.6x** |
| **All, long (L2)** | **1.3x** | **3.7x** |

Build time on the CPU was **0.4-0.55x** today's far mesher. It skips the geomorph projection that `FarMesher` does per vertex. One caveat: the clumps read the shape every 2 cells, as the chunk generator does, so this is not yet a like-for-like comparison of shape detail (see 1b).

**What this says:**

- **Plains, hills and basins:** clumps cost the same as today or less. Most of the world is this ground.
- **Exposed rock** (mountains, canyons, rims): about 2.5x the triangles and about 8x the vertices. Each facet needs its own vertices for its flat normal and its per-node shade. Vertex count is the real cost, and two fixes in 1a below should cut it by more than half.
- **CPU time** is not the problem. Clumps build faster than today's blocks, so the load time is safe.
- **The GPU is the risk.** Mountain and island-zone views already run 24-49 fps (3-7M triangles), and extending the rings (Part 1, point 2) multiplies whatever each ring costs. Every phase below is measured before the next one starts.
- **Small sky-island pieces disappear in long clumps.** Island rock at L2 dropped from 55,000 to 15,000 triangles. Thin shards don't survive 8-unit cells sampled every 16 units. Island blocks need their own rule (1f).

My assessment: it's worth building. It fixes the look by construction, since far rock *is* nodes, only larger. It costs nothing on most of the planet. Its one cost, on rocky ground, is a known one with clear fixes. Build it behind a switch, at medium first, and judge it in play before going further.

### 1a. The clump mesher (offline, no game changes yet)

- [ ] **`ClumpMesher`** (`Modules/Terrain/`): builds a far block at a level from a coarse `PlanetGenerator`, then runs the two node meshers over its 8 sections.
  - Pure and thread-safe, like `FarMesher`.
  - Each worker keeps its own small scratch store (the block plus a 2-cell border).
  - Blocks generate their own border cells, so neighbours at the same level share every cell and meet without a seam, exactly as chunks do.
- [ ] **`SectionSample.Read` from any source**: an overload reading from a `ReadCell` delegate or a flat array, not only a `NodeChunkStore`.
- [ ] **Hide buried rock at clump scale.** Measured above: this halves the triangle count.
  - Option A: bury rock deeper at clump scale (sand depth `max(SandDepth, 2.5 x clump)`), as the benchmark did.
  - Option B (preferred): a far "cover" rule. A full sand cell covers rock even when it is next to open air. Near the player, that rule exists to stop hairline seams showing the sky. Far away, the coarser level drawn behind fills any seam.
  - Compare A and B in screenshots: A changes where rock shows on slopes; B doesn't.
- [ ] **No collision soup.** `MeshBuffers.AddTriangle` copies every triangle for physics. Add a render-only mode for far use.
- [ ] **Fewer vertices.**
  1. Flat normals from screen derivatives in the far shader, so a cell's faces share its corners. Expect about 2x fewer vertices.
  2. Drop faces under about 1/20 of the clump's area. That's safe here and not near the player, because the coarser level behind catches any pinhole.
  - Measure both with the benchmark (keep it as a `--filter=ClumpBench` suite this time).
- [ ] **One surface per block.** Rock and sand go in one mesh, with the rock flag in the vertex colour as `FarMesher` does now, so clumps keep today's draw-call count.
- [ ] **Benchmark suite**: the measurement above as a real suite (`Tests/ClumpBenchTests.cs`) that prints triangles, vertices and build time per spot and level for nets against clumps. It reports numbers only; the gates below are judged from them.

**Gate 1 (report):** triangles, vertices and build time per spot, against today's L1. The earlier aim was at most about 2x the triangles and 3x the vertices. With detail first, being over that is written down for the efficiency pass rather than stopping work. Stop only if the build time rises enough to threaten the load budget.

### 1b. Shape detail

- [ ] **Sample spacing per grid.** `PlanetGenerator.SampleSpacing` is fixed at 2 nodes. Make it a constructor parameter:
  - medium clumps sample every 4 units (today's L1 detail), not every 8;
  - long clumps every 8.
  - Benchmark the cost. Generation was about 20% of clump build time, so even 8x the samples still leaves clumps level with `FarMesher`.
- [ ] **Check the features at clump scale**: arches at 16-24 units and mesa caprock lips. Extend `FarKeepsArches` to clump levels.

### 1c. Into the far terrain (medium only, behind a switch)

- [ ] **`FarTerrain.ClumpLevels`** (export, default 0 while building, then 1, then 2): levels up to this one build with `ClumpMesher`; the rest keep `FarMesher`.
- [ ] **Shading**: clump rock takes the *real* rock's look: the same `ShadeA`/`ShadeB` per-cell parity colours, the flat facet lighting, the far shader's haze and push-behind. The painted mosaic is skipped for clump levels; the facets now do its job. Clump sand keeps the far sand's grain.
- [ ] **No geomorph across clump levels.** Voronoi cells have nothing to slide onto. The hand-offs at both ends of each clump ring become fades (1d). Geomorph stays from L3 outward.
- [ ] **Outlines**: the stylized filter's edge detection will find every facet. Check whether far clumps read as noise. If they do, fade the normal-edge outline sooner on far terrain, or key it off depth only past the near ring.

**Gate 2 (in play):** `TerrainCapture` at every spot, before and after: load time, frame time, triangles, and side-by-side eye-level shots of the 210 hand-off. You decide whether the look justifies going on.

### 1d. Fades between rings (your point 3, by distance)

Today the real chunks dissolve over the far terrain with a pixel dither, and the far ground is drawn whole behind them ("push-behind"). Every ring uses the same mechanism:

- [ ] **Each finer ring dissolves out over its outer band.** The next coarser ring is drawn whole underneath and pushed further back, so the finer ring always wins where it's drawn and any gap shows coarse ground, never sky.
  - Push per level: about one clump width (2.5 today for L1; then more for L2, and so on).
- [ ] **Coarser blocks drawn under the band.** At the moment a split block is not drawn at all. Within the band it must be, and cut away nearer than the band's inner edge. This is today's `handover` material, generalised from "level 1 over real chunks" to "level k+1 over level k".
  - Cost: about one band's width of extra coarse geometry per ring. Keep bands about 30-40% of a ring's depth.
- [ ] **Dither pattern.** Use the existing screen-space dither so the new bands match the real-chunk dissolve. Check that two bands overlapping on screen (steep views) don't stack into a visible checker.
- [ ] **Avoid Godot's own visibility-range fades.** The Godot 4.6 docs say its `Self` and `Dependencies` fade modes use alpha blending, which goes through the transparent pipeline (sorted, no depth prepass). Keep the in-shader dither.

### 1e. Fades over time (your point 3, entering and leaving)

Distance bands cover steady walking. Time fades cover detail that arrives late (spawn, teleport, fast travel, a block finishing after its band has passed) and detail that is dropped.

- [ ] **Fade cohorts, not per-instance values.** Per-instance shader values hit the instance-uniform buffer limit at about 4,000 blocks before, which is why they were removed. Instead keep a small pool of shared materials (say 8), each with its own `fade_start` time:
  - blocks that start fading in the same layout share one;
  - a pool material returns to the steady material once its fade is done.
  - Shared parameters cost nothing per block.
  - Fallback, if the pool gets fiddly: raise `rendering/limits/global_shader_variables/buffer_size` and go back to instance uniforms.
- [ ] **Fade in on arrival.** A newly uploaded finer block dithers in over about 0.4 s over the coarser one, which is still drawn behind it.
- [ ] **Fade out on departure.** When finer blocks merge back into their parent, the parent goes up first, then the children dither out over about 0.4 s, then they're freed. This replaces today's instant swap ("a block is only taken away the frame its replacement goes up"). The rule that nothing is removed before its replacement is drawn stays.
- [ ] **Real chunks too.** A section uploaded nearer than the end of the near band fades in through the same cohort trick on `NodeMaterials`. In normal play, chunks load beyond the band, so this mostly affects spawn, teleport and fast travel.
- [ ] **Time source.** A shared clock uniform set from C# each frame, not shader `TIME`, which rolls over every hour.

### 1f. Sky islands at clump scale

- [ ] **Intricate blocks** (`ITerrainShape.Intricate`) over a zone: decide by measurement between
  - clumps sampling at clump spacing, or
  - keeping surface nets for intricate blocks at L2.
  - Test: `FarKeepsArches`-style rays through known shards at L1 and L2. A shard that the near chunks draw must not vanish in the ring just past them.

### 1g. Extend the rings (your point 2)

The limit on how far a ring can reach is screen size, not only cost. A clump smaller than about 3 screen pixels shimmers, as the mosaic did at 2-3 pixels. At 1080p with a 70° view, one pixel is about 0.0013 x the distance (in world units).

| Ring | Today | Proposed | Size on screen at the far end |
|---|---|---|---|
| Short (real nodes) | dissolve 110-210 | dissolve 180-300 | 2-unit node ≈ 5 px |
| Medium (4-unit clumps) | to about 540 | to about 900 | ≈ 3.4 px |
| Long (8-unit clumps) | to about 770 | to about 1,800 | ≈ 3.4 px |
| Surface nets L3+ | to 4,000 | unchanged, fading from long | (smooth) |

**Chosen: the "Proposed" column**, detail first.

- [ ] **A split factor per level**, replacing the single `SplitFactor`. It supersedes TODO 1's raise of the split factor from 3 to 4.5.
- [ ] **Streamer reach for the short ring: dissolve over 180-300**, with a load radius of 7 and unload of 9 (today 5/7). That's about 2x the surface chunks resident, with their memory, meshing and collision.
  - Still measure the cheaper setting too (150-260, radius 6/8), so the efficiency pass has both numbers.
  - Only the chunk under the player gates spawning (`ReadyRadius` 1), so the time to the first playable frame should barely move. Check it.
- [ ] **Cost.** Each extended ring covers about 2.5-3x the area it covers today, on top of the clump costs above. Extend one ring at a time and measure after each, so the efficiency pass knows which ring costs what.
- [ ] **Experiment: coarser clumps.** You asked to hear if lower detail works well. After the long ring is in:
  - try 16-unit clumps for level 3 (`ClumpLevels = 3`), which draws clumps out to about 3.6 km;
  - try starting the long ring sooner.
  - Report triangles, frame time and side-by-side shots for each. If 16-unit clumps read well at their distance, they would replace the smooth surface to almost the horizon, and the far terrain would be one style all the way out.

**Gate 3 (report):** load times and frame times per spot, for every ring step, and for both near-ring settings. Stop only on the budgets (60 s load, 120 s in island zones) or on a frame rate that hurts play. Then tune the ring ends by eye.

### 1h. Tests

- [ ] Clump blocks at the same level meet without gaps (rays through seams, as in `PitIsWatertight`).
- [ ] No holes through any fade: every direction hits ground on every frame while blocks fade in and out (extend `NoHolesWhileStreaming`).
- [ ] Clump ground agrees with real ground: in the near band, the clump surface lies within one clump of the real chunks' surface.
- [ ] Clumps are deterministic and thread-safe: identical meshes from parallel and serial builds.
- [ ] Arches and shards survive the clump levels (1b, 1f).

### What becomes of TODO 1's open far-terrain items

- Faceted far rock, and detail further out: **replaced** by this part.
- Ridge sparkle, the low-angle limit of the push-behind, and the mosaic speckle: look again once clumps are in. All three come from smooth far rock against faceted near rock, which is what this removes.
- Frame rate over a zone: tied in with 1f and Part 3.

---

## Part 2: A planet twice as wide

`Planet.Radius` goes from 6,000 to 12,000. The diameter doubles and the surface area quadruples. The change is one number, but much depends on it:

- [ ] **Scale the counts, not the sizes** (decided: features stay the same size, and there are more of them).

  | Thing | Today | At radius 12,000 |
  |---|---|---|
  | `RegionCount` | 100 | 400 (regions stay 1-3 km) |
  | Oceans | 2-4 | 8-16 (each stays 5-15 km across) |
  | Canyons | 5-10 | 20-40 |
  | Island zones | 3-5 | 12-20 (each stays 700-1,100 in radius) |

  - Four times the surface means four times the counts. Check each against its range limit in `TerrainSettings`: `RegionCount` tops out at 400 and the zone count at 20, so raise those caps.
  - Check that the 15/30/25/30 mix still lands within a few percent.

- [ ] **Region lookup.** It is brute force over all region points, which was fine at 100. At 400 it is 4x per lookup, and every shape sample makes one. Add the cube-face bucket grid that TODO 1 left for "if profiling says so". Check whether the oceans' growth, canyon routing or zone placement are quadratic in the region count.
- [ ] **Hard-coded distances**:
  - zones "apart" by 4,000 (`SkyIslands`);
  - the zone radius clamp (700-1,100);
  - canyon lengths;
  - anything else in world units that was tuned against 6,000.
- [ ] **Horizon and far terrain.** At twice the radius the ground curves away half as fast. More far blocks pass the horizon test, so expect more far blocks and a longer first build. Measure at every spot. `DrawDistance` 4,000 still covers a 300-unit peak (visible from about 2.7 km at this radius).
- [ ] **Precision.** At 12,000, float spacing is about 0.001 units. The Voronoi lookup already works relative to the nearest lattice point, the noise is in doubles, and positions are planet-local. Check the camera and vertex shimmer at the surface anyway.
- [ ] **The sky.** `DayCycle` reads `Planet.Radius` for the horizon dip, so it follows on its own. Check `SkyCapture`.
- [ ] **Tests and captures.**
  - The headless tests build their own planets at 6,000. Leave those as they are; they test the mechanics.
  - `TerrainCapture`, `PlanetMap`, `StressCapture`, `LevelCapture`, `SkyCapture` and `ArtifactCapture` hard-code `6000f`. They must read the real `Planet` and its `TerrainSettings`, or they'd look for spots on a different planet from the one they load.
- [ ] **Docs**: README (planet section, terrain numbers) and the TODO decision table.

Do this **first**, before Part 1's measuring. All the tuning above should happen on the planet you'll keep.

---

## Part 3: Sky islands in fractal clusters

Decided: many islands, gathered in **fractal clusters**. A large island has medium islands around it, and each medium island has its own small ones. Islands may be a little misshapen. Zones keep their size (700-1,100 in radius); the bigger planet simply has more of them.

### 3a. The cluster tree

Each cluster grows from one root, the same rule applied at every scale:

| Generation | Across | Children each | Where the children sit |
|---|---|---|---|
| Large (root) | 250-600 | 3-6 medium | 0.6-1.6 parent radii from the parent's centre, out from its edge |
| Medium | 60-180 | 2-5 small | the same, at their own scale |
| Small | 15-50 | 0-4 debris | the same |
| Debris | 5-14 | none | |

- A child is about 0.2-0.35 of its parent's size, so each generation looks like the one above it, smaller.
- Children spread around their parent unevenly:
  - more on one side, so clusters lean and trail rather than ring the parent;
  - offset up or down by up to about a parent radius.
  - The result reads as one island breaking up rather than a target.
- **Some children are calved, not just placed.** They sit right at the parent's edge, cut from the parent's own outline (a Voronoi crack pattern, the code that cracks today's rims, `Border`/`Crack`). A gap of a few units lets them drift out. So a large island visibly sheds its edge into the mediums around it.
- **Roots are laid out through the heap's envelope** (it fills the crater low down and narrows to a point about 1,000 over the rim). Roots are larger low down and smaller higher up, and spaced so clusters don't overlap much.
  - Start at about 4-8 roots per zone and tune by eye.
- **Free debris stays** at the top of the heap: a thinned small-piece lattice, so the tip of the heap still drifts into space.
- **Rim floes and the cracked rim and floor stay as they are.**
- **Traversal stays as decided before:** gaps a few units wide near the rim and inside clusters (calved pieces are hoppable), widening between clusters and higher up.
- **Counts.** About 50-100 pieces per cluster, so a few hundred to about 800 per zone where today's zones have about 11,000. Far fewer pieces, each larger: that should also help the zone frame rate (TODO 1, open item).

### 3b. Misshapen shapes

All upright, as decided before; misshapen within that:

- **Outline**: the plan shape is a wandering blob (low-frequency noise on the radius, two or three octaves), sometimes two or three overlapping blobs joined, so some islands are kidney-shaped, lobed or long.
- **Top**: near-flat and sandy on large and medium islands, with a gentle lumpiness and an overall tilt of a few degrees. Not a perfect plane.
- **Underside**: a rock body tapering down to several hanging points of different lengths, not one cone. Larger islands get more points.
- **Small and debris generations** reuse today's kinds (slab, chunk, shard) for variety.
- **Every island is its own seed**, so no two match.

### 3c. The field

- **Islands are solids joined by union** (the greatest distance of them), as pieces are now. A calved child is its parent's shape cut to its Voronoi cell and moved by its drift.
- **Lookup.** Today pieces are filed in 32-unit buckets. A 600-unit island would be listed in thousands of them, so file the large and medium generations in a coarser grid (128 units) and the small ones in today's grid. A point checks both.
- **Bounds.** The slope-bound test (`SlopeBound` over zones) must still pass with the blob outlines and the cell cuts. `Within` culling must still list every island that can reach a block.
- **Load time.** Laying out a zone is now a tree walk of a few hundred islands, cheaper than today's 11,000 pieces.

### 3d. Tests and captures

- [ ] Cluster shape:
  - each root has 3-6 children, and each generation is 0.2-0.35 the size of the one above;
  - children stand clear of their parent (calved ones by a gap of a few units).
- [ ] Ground floats under every island.
- [ ] Spawning in a zone lands on an island top when one is under the spawn column (and fixes the open "spawn at a zone's middle misses the heap").
- [ ] The slope bound and culling tests over zones still pass.
- [ ] The load time over a zone, and the frame rate looking into one, against today's.
- [ ] A new capture `--spots=cluster` that looks across a large island at its calving edge, plus an aerial shot of a whole zone.

---

## Suggested order

1. Commit the sky work. Take baseline numbers with the current build (`TerrainCapture` at all spots, plus `StressCapture` on a towering range and over a zone).
2. **Part 2** (planet scaling), then measure again. That's the new baseline.
3. **1a-1b** offline, then Gate 1.
4. **1c** (medium only) and **1d** (distance fades), then Gate 2, judged in play.
5. **1e** (time fades).
6. Long clumps (`ClumpLevels = 2`) and **1f**.
7. **1g**, one ring at a time, then Gate 3.
8. **Part 3**, drawn by the clump rings from the start.
9. **1h** and the Part 3 tests are written alongside each step. Docs at the end: README streaming section, the far terrain's doc comment, and the TODO.

## Risks

- **GPU vertex load on rocky ground.** This is the one real cost (up to about 8x the vertices on exposed rock before the 1a fixes). With detail first, it is measured and carried into the efficiency pass. Only if play suffers, these are the fallbacks, in order:
  - shorter ring extensions (the cheaper near-ring setting first);
  - clumps for rock only where the slope is past 50°;
  - long clumps only (the medium ring keeps nets plus the mosaic).
- **Facet shimmer** in the outer part of each clump ring. The pixel-size limits in 1g are set against it; the outline filter may make it worse (1c).
- **Fades doubling the geometry in each band.** Keep bands narrow and measure.
- **The planet scaling multiplies everything** (regions, far blocks over the horizon). That's why its measurement comes first.
