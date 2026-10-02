# Day–Night Sky Cycle: Implementation Spec

Sep 28, 2026 · @Dylan

## Purpose and instructions

Add a full 24-hour day–night sky cycle to a game that currently renders only a static plum night sky (`#1E1024`). Keep that plum as the night anchor; everything else in this spec is built around it.

Instructions for the implementing AI:

1. Treat the sky as **two colors at once**: an overhead (zenith) color and a horizon color, blended vertically. Never fill the sky with one flat color.
2. Drive everything from one value: `hour` in the range 0–24 (float). Interpolate keyframes linearly between the two surrounding stops.
3. Layer the effects in this draw order: sky gradient → sun-side warm glow → purple light → Belt of Venus → stars → sun → terrain (far to near) → UI.
4. Use the palette and keyframe hex values exactly as given unless the game's engine needs a different color space; if so, convert, don't re-pick.
5. Terrain is teal. It is lit by the sky, not a fixed color (see the terrain formulas).
6. The UI stays off-white `#F4EEDC` and gold `#C9A45C`. Put text on a translucent plum scrim during daylight (see the contrast section).
7. Match the game's own time scale. The reference runs a full day in 30 seconds; that is only a demo speed.

Sunrise is fixed at 06:45 and sunset at 18:15 in this spec. If the game has seasons or latitude, shift the keyframes with those two times and keep the spacing between stops.

## Research: how real skies behave

Each finding below maps to a specific feature in the game.

| Finding | What it means for the game | Source |
| --- | --- | --- |
| Light from the horizon passes through up to about 38× more air than light from overhead, so the sky is vivid overhead and pale at the horizon. | Render a vertical gradient: zenith color at the top, horizon color at the horizon line. Midday horizon is a pale lilac haze. | [Sky – Wikipedia](https://en.wikipedia.org/wiki/Sky) |
| At sunset the long light path strips out blue and green, leaving orange and red near the horizon. Overhead stays blue or violet. | Warm colors appear only in the horizon color and a glow around the sun's side, never across the whole sky. | [Sunrise – Wikipedia](https://en.wikipedia.org/wiki/Sunrise) |
| Evening air holds more particles than morning air, so sunsets are usually more vivid than sunrises. | Sunrise uses pale apricot at 70% glow strength; sunset uses stronger coral at 100%. | [Sunrise – Wikipedia](https://en.wikipedia.org/wiki/Sunrise) |
| Purple light appears with the sun 3–4° below the horizon, as a glowing disc segment rising from the horizon toward the sun. It fades by about 6° below. | A rosy-mauve radial glow above the horizon on the sun's side, just after sunset and just before sunrise. | [Purple light – WMO Cloud Atlas](https://cloudatlas.wmo.int/en/purple-light.html) |
| Blue hour peaks with the sun 4–8° below the horizon. The deep blue comes from ozone absorption (the Chappuis band), not ordinary scattering. It lasts roughly 20–96 minutes. | Deep indigo step between purple light and full night, on both ends of the day. | [Blue hour – Wikipedia](https://en.wikipedia.org/wiki/Blue_hour); [Applied Optics, Antarctic twilight](https://opg.optica.org/ao/abstract.cfm?uri=ao-50-28-F162) |
| Opposite the sun during civil twilight, a pink band (Belt of Venus) sits above a dull bluish band (Earth's shadow). They blend without a hard edge. | A soft pink-over-blue band low on the side away from the sun, fading toward the sun. | [Sky – Wikipedia](https://en.wikipedia.org/wiki/Sky) |
| Distance reads as cooler and paler; the sky's color and temperature tint the land and water below it. | Far hills blend more toward the horizon color (aerial perspective); all terrain picks up warm light at golden hour. | [Artists Network – color temperature in landscapes](https://www.artistsnetwork.com/art-subjects/plein-air/build-atmosphere-in-landscapes-with-color-temperature/) |

Twilight stage order, from sunset into night: sunset glow (sun 0° to about −3°) → purple light (−3° to −6°) → blue hour (−4° to −8°) → darker twilight → night (below −18°). Dawn runs the same stages in reverse.

## Color theory: why these colors

The sky is an analogous scheme (violet → indigo → periwinkle → lilac), warm colors are short accents, and teal is moved to the ground where it contrasts with the sunset.

- **Sky family is analogous.** Plum, indigo, periwinkle and lilac sit next to each other on the color wheel. Analogous schemes are the most stable harmony, so the sky feels like one world across the whole day. ([Harmony (color) – Wikipedia](<https://en.wikipedia.org/wiki/Harmony_(color)>))
- **Apricot and coral are accents, not a sky color.** They appear only near the horizon at sunrise and sunset. This matches real skies and keeps the warm hues from competing with the blues.
- **Gold UI contrasts with the plum night.** Gold (hue ≈ 40°) is warm and light; plum (hue ≈ 282°) is cool and very dark. They sit about 120° apart, and the lightness gap gives 7.75:1 contrast, so the UI stands out most at night.
- **Teal belongs on the terrain.** In the sky, teal broke the violet-to-blue family and read as the odd one out. On the ground it sits roughly opposite the coral sunset (teal ≈ 178°, coral ≈ 17°, 161° apart), a near-complementary pair, so golden hour and sunset have the strongest land-to-sky contrast of the day.
- **Split-complementary overall.** Blue-violet sky as the dominant, with warm gold/apricot/coral on one side and teal on the other. This keeps complementary tension while adding variety, which is how split-complementary schemes are described.

Approximate hues (HSL degrees): plum 282, indigo 235, periwinkle 223, lilac 229, purple light 324, apricot 25, coral 17, gold 40, teal 178.

## Palette

Fourteen named colors: nine for the sky, three for terrain, two for UI. Keyframes below reuse and blend these.

| Group | Name | Hex | Role |
| --- | --- | --- | --- |
| Sky | Plum night | `#1E1024` | Night horizon; existing game sky. Night zenith is darker `#120A18`. |
| Sky | Blue hour indigo | `#363C78` | Horizon during blue hour (sun 4–8° below) |
| Sky | Purple light | `#A9618C` | Horizon just after sunset; glow color `#C488B4` |
| Sky | Sunrise apricot | `#F6C9A8` | Horizon and glow at sunrise (paler, weaker) |
| Sky | Sunset coral | `#F0936E` | Horizon and glow at sunset (stronger) |
| Sky | Golden-hour haze | `#F2CFA0` | Horizon about 40 min before sunset |
| Sky | Day overhead | `#7C98E0` | Midday zenith (periwinkle) |
| Sky | Horizon lilac | `#D4DAF4` | Midday horizon haze |
| Sky | Belt of Venus | `#E3A9BD` | Pink band opposite the sun; Earth's shadow band below it `#4A4F85` |
| Terrain | Far teal | `#6FA8A6` | Distant hills (most aerial blending) |
| Terrain | Mid teal | `#3F8482` | Middle hills |
| Terrain | Near teal | `#245E5E` | Foreground |
| UI | Off-white | `#F4EEDC` | Text, outlines, stars, moon |
| UI | Gold | `#C9A45C` | Buttons and highlights |

Extra working colors: night terrain `#101722`; golden-hour warm tint on terrain `#F0A57E`; sun disc low `#FFB98A` → high `#FFF4DC`; UI scrim `rgba(20, 10, 24, 0.55)`.

## Keyframes

Fifteen stops across 24 hours. Interpolate zenith and horizon separately between the two stops around the current hour. The 0:00 and 24:00 rows are identical so the loop is seamless.

| Hour | Clock | Zenith (overhead) | Horizon | Phase starting here | Sun elevation (approx.) |
| --- | --- | --- | --- | --- | --- |
| 0.00 | 0:00 | `#120A18` | `#1E1024` | Night | below −18° |
| 4.50 | 4:30 | `#150C21` | `#2B1D40` | Early twilight | −18° to −12° |
| 5.30 | 5:18 | `#1C2150` | `#363C78` | Blue hour | −8° to −4° |
| 5.80 | 5:48 | `#283069` | `#8E6A98` | Purple light | −4° to −3° |
| 6.25 | 6:15 | `#45559A` | `#E3B19E` | Dawn glow | −3° to 0° |
| 6.75 | 6:45 | `#6A80C4` | `#F6C9A8` | Sunrise → golden hour | 0° |
| 7.75 | 7:45 | `#7892D6` | `#DCD4E6` | Day | above \~6° |
| 12.50 | 12:30 | `#7C98E0` | `#D4DAF4` | Midday | highest |
| 16.75 | 16:45 | `#7890D8` | `#DCD6EC` | Golden hour | \~6° |
| 17.60 | 17:36 | `#6E86CC` | `#F2CFA0` | Golden hour (late) | \~3° |
| 18.25 | 18:15 | `#5763AA` | `#F0936E` | Sunset glow | 0° |
| 18.75 | 18:45 | `#353B79` | `#A9618C` | Purple light | −3° to −4° |
| 19.25 | 19:15 | `#1D2252` | `#3A3F7A` | Blue hour | −4° to −8° |
| 20.20 | 20:12 | `#160D24` | `#2A1A3C` | Night | past −12° |
| 24.00 | 24:00 | `#120A18` | `#1E1024` | Night (loops to 0:00) | below −18° |

Phase boundaries used for labels: Night 0–4.5, Early twilight 4.5–5.3, Blue hour 5.3–5.8, Purple light 5.8–6.25, Dawn glow 6.25–6.75, Golden hour 6.75–7.75, Day 7.75–16.75, Golden hour 16.75–18.25, Sunset glow 18.25–18.75, Purple light 18.75–19.25, Blue hour 19.25–20.2, Night 20.2–24.

## Calculations and formulas

Every effect is a function of `hour` h (0–24). Screen size is W × H; the horizon line sits at `horizonY = 0.70 × H`. Colors blend in sRGB with linear interpolation, which is fine because neighboring stops are close.

**Helpers**

```latex
\text{mix}(a,b,t) = a + (b-a)\,t
```

```latex
\text{bump}(x,c,w) = e^{-\left(\frac{x-c}{w}\right)^2}
```

```latex
\text{smoothstep}(e_0,e_1,x) = s^2(3-2s),\quad s = \text{clamp}\!\left(\frac{x-e_0}{e_1-e_0},0,1\right)
```

**1. Keyframe interpolation.** Find stops i and i+1 with hᵢ ≤ h ≤ hᵢ₊₁.

```latex
t = \frac{h-h_i}{h_{i+1}-h_i},\qquad Z = \text{mix}(Z_i,Z_{i+1},t),\qquad Hz = \text{mix}(Hz_i,Hz_{i+1},t)
```

**2. Sky gradient.** Vertical, from y = 0 to horizonY: stop 0.00 = Z, stop 0.55 = mix(Z, Hz, 0.38), stop 1.00 = Hz. The middle stop keeps the color deep for most of the sky and lets it pale quickly near the horizon.

**3. Sun-side warm glow.** Sunset strength is 1.0, sunrise 0.7 (sunsets are more vivid).

```latex
k_{dawn} = 0.7\,\text{bump}(h, 6.5, 0.6),\qquad k_{dusk} = \text{bump}(h, 18.45, 0.6)
```

Radial gradient centered at (0.06W, horizonY) for dawn and (0.94W, horizonY) for dusk, radius 0.7W. Color `#F6C9A8` at dawn, `#F0936E` at dusk. Alpha stops: 0.60k at 0, 0.22k at 0.35, 0 at 1. Skip when k < 0.01.

**4. Purple light.** A rosy disc above the horizon on the sun's side.

```latex
p_{dawn} = 0.8\,\text{bump}(h, 5.95, 0.28),\qquad p_{dusk} = \text{bump}(h, 18.9, 0.30)
```

Radial gradient at (0.10W or 0.90W, horizonY − 0.22H), radius 0.42W, color `#C488B4`, alpha 0.5p at center fading to 0. Draw only above the horizon.

**5. Belt of Venus and Earth's shadow.** On the side away from the sun: right side at dawn, left side at dusk.

```latex
b_{dawn} = 0.8\,\text{bump}(h, 6.4, 0.45),\qquad b_{dusk} = \text{bump}(h, 18.6, 0.45)
```

A band from horizonY − 0.20H down to horizonY. Vertical stops: 0.00 pink `#E3A9BD` α0 → 0.35 pink α0.50 → 0.60 pink α0.30 → 0.85 shadow blue `#4A4F85` α0.45 → 1.00 shadow blue α0.60. Horizontal mask: fully visible at the edge away from the sun, fading to transparent 35% of the width in from the sun's side. Multiply the whole band by b.

**6. Stars.** 160 stars at fixed seeded positions in the top 62% of the screen, radius 0.4–1.5 px.

```latex
v = \text{clamp}\big(1 - \text{smoothstep}(4.6, 5.5, h) + \text{smoothstep}(19.5, 20.4, h),\,0,1\big)
```

```latex
\alpha_{star} = v \cdot \big(0.65 + 0.35\sin(t/700 + \phi)\big)\cdot(1 - 0.9\,y_{norm})
```

t is milliseconds, φ a random phase per star, y\_norm the star's height from 0 (top) to 1. Stars fade toward the horizon. Color `#F4EEDC`.

**7. Sun path.** Sunrise 6.75, sunset 18.25, a 11.5-hour arc.

```latex
f = \frac{h - 6.75}{11.5},\quad x = W(0.06 + 0.88f),\quad y = \text{horizonY} - \sin(\pi f)\cdot 0.78\cdot\text{horizonY}
```

Draw while −0.04 < f < 1.04 (the terrain hides it as it sets). Radius R = max(14 px, 0.022W), halo radius 5R at α0.55 fading to 0. Color = mix(`#FFB98A`, `#FFF4DC`, smoothstep(0, 0.5, sin πf)): orange-peach near the horizon, cream when high.

**8. Terrain.** Three teal hill layers drawn far to near. Each hill edge is a sum of two sines:

```latex
y(u) = H\Big(\text{base} - \text{amp}\big(0.6\sin(f_1u + p_1) + 0.4\sin(f_2u + p_2)\big)\Big),\quad u = x/W
```

| Layer | Base color | base | amp | f₁, p₁, f₂, p₂ | Aerial blend (aer) |
| --- | --- | --- | --- | --- | --- |
| Far | `#6FA8A6` | 0.72 | 0.035 | 5.1, 0.4, 11.3, 2.1 | 0.50 |
| Mid | `#3F8482` | 0.81 | 0.050 | 3.7, 1.7, 8.9, 0.3 | 0.22 |
| Near | `#245E5E` | 0.92 | 0.050 | 2.9, 3.3, 7.1, 1.2 | 0.08 |

Lighting per layer:

```latex
L = \text{smoothstep}(5.4, 7.6, h)\cdot\big(1 - \text{smoothstep}(17.4, 19.6, h)\big)
```

```latex
w = \max(0.8\,k_{dawn},\,k_{dusk}),\qquad aer' = aer\,(1 - 0.4w)
```

```latex
c_1 = \text{mix}(\text{base}, Hz, aer'),\qquad c_2 = \text{mix}(c_1, \#F0A57E, 0.14\,w\,(1-aer))
```

```latex
c_{night} = \text{mix}(\#101722, Hz, 0.6\,aer),\qquad c = \text{mix}(c_{night}, c_2, 0.12 + 0.88L)
```

Far hills take more of the horizon color (aerial perspective). At golden hour the aerial blend drops by up to 40% so the teal still reads against the warm horizon, and near layers pick up a warm tint. At night terrain never goes below 12% of its lit color, so the silhouettes stay visible against the plum.

## UI readability

Off-white text is readable on the sky only from blue hour through night; in daylight it needs a plum scrim behind it. Ratios use the WCAG formula; 4.5:1 is the target for small text, 3:1 for large text and icons.

```latex
\text{ratio} = \frac{L_{light} + 0.05}{L_{dark} + 0.05}
```

L is relative luminance: each sRGB channel c (0–1) becomes c/12.92 if c ≤ 0.03928, else ((c + 0.055)/1.055)^2.4, then L = 0.2126R + 0.7152G + 0.0722B.

| Background | Off-white `#F4EEDC` | Gold `#C9A45C` |
| --- | --- | --- |
| Plum night `#1E1024` | 15.68 | 7.75 |
| Blue hour `#363C78` | 8.73 | 4.31 |
| Purple light `#A9618C` | 3.79 | 1.87 |
| Day overhead `#7C98E0` | 2.44 | 1.21 |
| Sunset coral `#F0936E` | 1.99 | 1.02 |
| Sunrise apricot `#F6C9A8` | 1.31 | 1.55 |
| Horizon lilac `#D4DAF4` | 1.20 | 1.69 |
| Scrim over day overhead (`#434A72`) | 7.36 | 3.64 |
| Scrim over sunrise apricot (`#7A6059`) | 4.97 | 2.45 |
| Scrim over horizon lilac (`#6A687B`) | 4.67 | 2.31 |

Rules that follow from these numbers:

- Put all HUD text on a panel of `rgba(20, 10, 24, 0.55)` with a light blur. Off-white on that scrim stays at 4.67:1 or better at every time of day.
- Use gold for filled buttons and accents, not for small text on light sky. Dark plum text on a gold button is 7.75:1.
- Keep UI panels in the upper sky, away from the horizon, where the background is darkest.

## Reference code and integration checklist

The core math below is engine-neutral JavaScript from a working prototype (Canvas 2D). Port the functions; the drawing calls map to any engine's gradient or shader tools.

```javascript
const K = [
  { h: 0,     z: "#120A18", hz: "#1E1024" },
  { h: 4.5,   z: "#150C21", hz: "#2B1D40" },
  { h: 5.3,   z: "#1C2150", hz: "#363C78" },
  { h: 5.8,   z: "#283069", hz: "#8E6A98" },
  { h: 6.25,  z: "#45559A", hz: "#E3B19E" },
  { h: 6.75,  z: "#6A80C4", hz: "#F6C9A8" },
  { h: 7.75,  z: "#7892D6", hz: "#DCD4E6" },
  { h: 12.5,  z: "#7C98E0", hz: "#D4DAF4" },
  { h: 16.75, z: "#7890D8", hz: "#DCD6EC" },
  { h: 17.6,  z: "#6E86CC", hz: "#F2CFA0" },
  { h: 18.25, z: "#5763AA", hz: "#F0936E" },
  { h: 18.75, z: "#353B79", hz: "#A9618C" },
  { h: 19.25, z: "#1D2252", hz: "#3A3F7A" },
  { h: 20.2,  z: "#160D24", hz: "#2A1A3C" },
  { h: 24,    z: "#120A18", hz: "#1E1024" }
];
const SUNRISE = 6.75, SUNSET = 18.25;

const rgb = hex => { const n = parseInt(hex.slice(1), 16); return [(n >> 16) & 255, (n >> 8) & 255, n & 255]; };
const mix = (a, b, t) => a.map((v, i) => v + (b[i] - v) * t);
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const smooth = (a, b, x) => { const t = clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); };
const bump = (x, c, w) => Math.exp(-Math.pow((x - c) / w, 2));

function skyAt(h) {
  for (let i = 0; i < K.length - 1; i++) {
    const a = K[i], b = K[i + 1];
    if (h >= a.h && h <= b.h) {
      const t = (h - a.h) / (b.h - a.h);
      return { zenith: mix(rgb(a.z), rgb(b.z), t), horizon: mix(rgb(a.hz), rgb(b.hz), t) };
    }
  }
}

function effectsAt(h) {
  const glowDawn = bump(h, 6.5, 0.6) * 0.7, glowDusk = bump(h, 18.45, 0.6);
  return {
    glowDawn, glowDusk,
    purpleDawn: bump(h, 5.95, 0.28) * 0.8, purpleDusk: bump(h, 18.9, 0.3),
    beltDawn: bump(h, 6.4, 0.45) * 0.8,  beltDusk: bump(h, 18.6, 0.45),
    stars: clamp(1 - smooth(4.6, 5.5, h) + smooth(19.5, 20.4, h), 0, 1),
    daylight: smooth(5.4, 7.6, h) * (1 - smooth(17.4, 19.6, h)),
    warm: Math.max(glowDawn * 0.8, glowDusk)
  };
}

function sunAt(h) { // x, y as 0–1 of screen width and horizon height
  const f = (h - SUNRISE) / (SUNSET - SUNRISE);
  const alt = Math.sin(clamp(f, 0, 1) * Math.PI);
  return {
    visible: f > -0.04 && f < 1.04,
    x: 0.06 + f * 0.88,
    yAboveHorizon: alt * 0.78,
    color: mix(rgb("#FFB98A"), rgb("#FFF4DC"), smooth(0, 0.5, alt))
  };
}

const TERRAIN = [
  { base: "#6FA8A6", aer: 0.50 },  // far
  { base: "#3F8482", aer: 0.22 },  // mid
  { base: "#245E5E", aer: 0.08 }   // near
];
function terrainColor(layer, h) {
  const { horizon } = skyAt(h), e = effectsAt(h);
  const aer = layer.aer * (1 - 0.4 * e.warm);
  let c = mix(rgb(layer.base), horizon, aer);
  c = mix(c, rgb("#F0A57E"), 0.14 * e.warm * (1 - layer.aer));
  const night = mix(rgb("#101722"), horizon, layer.aer * 0.6);
  return mix(night, c, 0.12 + 0.88 * e.daylight);
}
```

Integration checklist:

- [ ] Add a global `hour` value (0–24, float) and a clock that advances it at the game's chosen speed.
- [ ] Replace the flat plum fill with a vertical zenith-to-horizon gradient from `skyAt(hour)`.
- [ ] Draw the dawn and dusk warm glows, purple light and Belt of Venus as additive or alpha overlays using the strengths from `effectsAt(hour)`.
- [ ] Fade existing stars (if any) with `effectsAt(hour).stars`; keep them fixed in position.
- [ ] Add the sun from `sunAt(hour)`, drawn before terrain so hills hide it at the horizon.
- [ ] Recolor terrain layers every frame (or on a throttled tick) with `terrainColor()`.
- [ ] Put HUD text on the plum scrim; check off-white stays at 4.5:1 or better at 12:30 and 18:15.
- [ ] Test the loop across 23:59 → 0:00 for a visible jump; there should be none.
- [ ] Test these checkpoints: 5:30 (blue hour), 6:30 (dawn glow + belt on the right), 12:30 (midday), 18:15 (sunset), 18:55 (purple light), 22:00 (night).

### Sources

- [Sky – Wikipedia](https://en.wikipedia.org/wiki/Sky)
- [Sunrise – Wikipedia](https://en.wikipedia.org/wiki/Sunrise)
- [Blue hour – Wikipedia](https://en.wikipedia.org/wiki/Blue_hour)
- [Purple light – WMO International Cloud Atlas](https://cloudatlas.wmo.int/en/purple-light.html)
- [Atmospheric ozone and colors of the Antarctic twilight sky – Applied Optics](https://opg.optica.org/ao/abstract.cfm?uri=ao-50-28-F162)
- [Harmony (color) – Wikipedia](<https://en.wikipedia.org/wiki/Harmony_(color)>)
- [Build Atmosphere in Landscapes with Color Temperature – Artists Network](https://www.artistsnetwork.com/art-subjects/plein-air/build-atmosphere-in-landscapes-with-color-temperature/)
