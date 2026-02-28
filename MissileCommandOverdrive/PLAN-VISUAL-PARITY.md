# Missile Command Overdrive — Visual Parity Gap Analysis (HTML vs C#)

## Root Cause: Why the C# version looks worse

The HTML canvas has three capabilities the C# Raylib port does not faithfully replicate:

1. **Real radial/linear gradients** — HTML uses `createRadialGradient` (55 calls) and `createLinearGradient` for smooth soft glows on explosions, smoke, nebulae, cities, bases. C# approximates these with layered solid circles (hard-edged rings), which look chunky.

2. **`shadowBlur` / glow halos** — HTML uses `shadowBlur` (35 uses) for soft light halos around windows, antenna lights, crosshairs, explosion rings, ruin embers. C# has no equivalent — elements appear flat and hard-edged.

3. **Blend modes** — HTML uses `globalCompositeOperation` (41 uses: `lighter`, `screen`, `multiply`) for atmospheric layering. C# only uses `BlendMode.Additive` in some places but misses `screen` and `multiply` blending entirely.

---

## Feature-by-Feature Gap List

### ❌ MISSING entirely from C#
| Feature | HTML function | Impact |
|---------|--------------|--------|
| Nebula sky blobs | `drawNeb()` | Large colored radial gradient circles in sky, parallax mouse offset, day/night fade |
| Aurora bands | `drawAur()` | Wavy horizontal HSL gradient bands, screen blend |
| Demon boss | `drawDemon()` | Entire boss entity rendering |
| Theme-specific cities | `drawCity()` — xbox + recharged branches | Xbox: faux-3D desert outposts. Recharged: neon wireframe. C# only has modern theme |
| Theme-specific bases | `drawBases()` — xbox + recharged branches | Xbox: sand pyramid. Recharged: neon trapezoid. C# only has modern theme |
| Theme-specific explosions | `drawExpl()` — per-theme gradient stops | Each theme has unique explosion palette. C# uses one palette |
| Theme-specific trails | `drawTrails()` — per-theme | Recharged: red/cyan bars. Xbox: orange/green dots. C# uses one style |
| City building profiles | `c.profile` with `wins`, `trims`, `roofType`, `spire`, `ant`, `stepW`, `ledge` | HTML generates rich building geometry per-city; C# may be simpler |
| City ruin crater glow | `drawRuin()` — radial gradient crater, ember pockets with `shadowBlur` | C# ruins lack the glowing impact crater and ember halos |
| Base destroyed "OFFLINE" text | `drawBases()` destroyed branch | HTML shows styled "OFFLINE" label on destroyed bases |
| Mouse parallax on stars/nebula | `mx`, `my` offsets in `drawStars`, `drawNeb` | Subtle mouse-tracking parallax gives depth |
| Floating text outline/shadow | `drawOver()` — `strokeText` + `fillText` | HTML combo text has black outline + themed fill; C# likely plain |
| Help overlay panel | HTML `#help` div | Persistent key binding hints at bottom-left |

### ⚠️ PRESENT but visually degraded in C#
| Feature | HTML approach | C# approach | Gap |
|---------|-------------|-------------|-----|
| Explosions | Smooth `createRadialGradient` with 3-4 color stops, `lighter` blend, `shadowBlur` ring | Layered solid circles (rings) | Hard edges, no smooth gradient falloff |
| Smoke | Smooth `createRadialGradient` (3 stops: bright center → dark edge) | Layered solid circles | Missing the soft cloud look |
| Bloom | Full-res canvas, `blur()` CSS filter at dynamic blur radius, drawn twice at different blur+alpha | Quarter-res render target, no actual blur, layered solid circles | No real blur = bloom looks like colored dots not soft glow |
| Vignette | Smooth `createRadialGradient` (center transparent → edge dark) | `DrawCircleGradient` | Should be close but may not match radial shape |
| Scanlines | `fillStyle = '#6ab5ff'` (blue) at `globalAlpha .06+danger*.05` | Blue-tinted `DrawLine` | Close but check alpha values |
| Cloud rendering | `createRadialGradient` with ellipse shape, `screen` blend | Solid ellipses | Missing gradient softness and screen blend |
| Star glow halos | Bright stars get a second larger circle draw at lower alpha | Check if implemented | May be present from Phase 7 |
| Bokeh | Radial gradient circles, additive blend | Solid layered circles | Missing gradient softness |
| Ground grid | Perspective convergence + pulsing dots | Present | Should be close |
| Crosshair | Theme glow via `shadowBlur` | No glow, just lines | Missing the soft halo around crosshair lines |

---

## Priority Fixes (highest visual impact first)

### P0 — Bloom (biggest single visual difference)
The HTML bloom is THE signature look. It creates the soft dreamy glow over the entire scene.
- HTML: renders bright elements to offscreen canvas → applies CSS `blur()` filter at 6-22px → composites twice with `screen` blend
- C#: renders to quarter-res target with solid circles → draws unblurred at full size
- **Fix**: Generate a proper blurred bloom texture. Options:
  - Multi-pass box blur on the bloom render target (ping-pong between two small textures)
  - Or use Raylib shader with Gaussian blur
  - The bloom target is already quarter-res (good) but needs actual blur applied

### P1 — Soft gradients for explosions and smoke
- **Fix**: Pre-generate a set of radial gradient textures (white center → transparent edge, 64x64 or 128x128) and draw them scaled with tint colors instead of layered rings
- This single change would make explosions, smoke, light bursts, and bokeh all look correct

### P2 — Nebula + Aurora (missing atmosphere)
- Port `drawNeb()` — 9 positioned radial gradient blobs, additive blend, parallax
- Port `drawAur()` — wavy horizontal bands, screen blend
- These fill the sky with color and life

### P3 — Theme-specific rendering
- Add xbox and recharged branches to: cities, bases, explosions, trails, crosshair glow
- Currently the game looks the same regardless of theme toggle

### P4 — City detail (building profiles, neon trims, ruins)
- Ensure city profile generation matches HTML (`profile` array with `wins`, `trims`, `roofType`, etc.)
- Add neon trim lines, antenna warning lights with glow, building edge highlights
- Ruin crater with radial gradient and ember `shadowBlur`

### P5 — Mouse parallax
- Offset stars and nebula by `(mouseX - W/2) / W * amount`
- Very cheap, adds significant depth feel

### P6 — Floating text styling
- Add black outline (`DrawText` offset by 1px in 4 directions) + themed color fill
- Add pop-in scale animation

---

## Implementation Approach

### Gradient Texture Atlas
Generate once at startup:
1. **Radial soft circle** (128×128): white center → transparent edge, smooth quadratic falloff
2. **Radial warm circle** (128×128): warm variant for enemy explosions
3. **Radial nebula** (64×64): for nebula blobs with HSL tinting

Use `Raylib.DrawTexturePro()` with tint color to replace ALL layered-ring approximations:
- Explosions, smoke, light bursts, bokeh, nebula, cloud cores, bloom elements

This single technique replaces ~55 `createRadialGradient` calls from HTML.

### Bloom Blur
Option A (simple): Multi-pass box blur on bloom render target
- Render bloom elements to `_bloomTarget` (already done)
- Ping-pong blur: draw `_bloomTarget` → temp target with horizontal offset, then back with vertical offset
- 2-3 passes at quarter res = cheap and effective

Option B (shader): Load a Gaussian blur fragment shader
- More correct but adds shader file dependency

### Estimated work
| Fix | Sessions | Visual impact |
|-----|----------|--------------|
| P0 Bloom blur | 1 | ★★★★★ |
| P1 Gradient textures | 1 | ★★★★★ |
| P2 Nebula + Aurora | 1 | ★★★★ |
| P3 Theme rendering | 1-2 | ★★★ |
| P4 City detail | 1 | ★★★ |
| P5 Mouse parallax | 0.5 | ★★ |
| P6 Text styling | 0.5 | ★ |
| **Total** | **~5-7** | |
