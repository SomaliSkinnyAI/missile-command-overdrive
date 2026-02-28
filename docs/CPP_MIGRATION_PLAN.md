# Missile Command Overdrive: C++ Migration Plan

## 1. Motivation

The current single-file HTML5 Canvas 2D implementation (~8,600 lines) faces performance
constraints that will limit future expansion:

- Bloom pass uses two full-screen `ctx.filter = blur()` draws per frame (CPU-rasterized).
- Scanline post-FX executes ~200+ `fillRect` calls per frame.
- Particle arrays (sparks, smoke, trails, debris, weather) create GC pressure.
- O(N×M) collision loops (enemy × explosion) are interpreted, not compiled.
- 124+ `fillRect`, 60+ `arc`, and 30+ gradient creates per frame all pass through Canvas 2D state machine overhead.
- Procedural audio generation is constrained to Web Audio API thread limits.

Native compilation removes these ceilings and enables significant future feature growth.

## 2. Recommended Technology Stack

| Component | Recommendation | Rationale |
|---|---|---|
| Language | C++17 | Fast, Visual Studio native, industry standard |
| Window/Input | SDL2 or raylib | Trivial input mapping, cross-platform |
| 2D Rendering | raylib (preferred) or SDL2 + SDL_gpu | Both support gradients, blending modes, rotations, alpha, render targets |
| Post-FX | Custom GLSL shaders | Bloom, scanlines, vignette, chromatic aberration become single-pass GPU ops |
| Audio | miniaudio (header-only) | Procedural synth via buffer generation, oscillators + filters natively |
| Math | glm (header-only) | Built-in clamp, lerp, mix |
| Build | Visual Studio 2022 + CMake | Native .exe output |

raylib is recommended over SDL2 because it is closer to the current single-file coding style
(single-include, immediate-mode drawing, built-in shapes/text/audio) and requires less boilerplate.

## 3. Graphics Fidelity Assessment

Every Canvas 2D feature has a direct native equivalent:

| Canvas 2D Feature | Usage Count | Native Equivalent | Fidelity |
|---|---|---|---|
| `createLinearGradient` | 30 | GPU quad with vertex colors or 1D texture | Identical |
| `createRadialGradient` | 25 | Radial gradient shader or pre-baked texture | Identical |
| `arc()` / `ellipse()` | 80+ | DrawCircle/DrawEllipse | Identical |
| `fillRect` | 124 | DrawRectangle | Identical |
| `globalCompositeOperation` (lighter/screen/multiply) | 16 | glBlendFunc modes | Identical |
| `globalAlpha` | 10+ | Per-vertex alpha | Identical |
| `shadowBlur` / `shadowColor` | 4 | Gaussian blur shader on RT | Better (GPU) |
| `save()`/`restore()` + transforms | 100+ | Matrix push/pop stack | Identical |
| `clip()` | 2 | Stencil buffer or scissor rect | Identical |
| `ctx.filter = blur()` (bloom) | 3 | Single-pass Gaussian blur shader | Much better |
| `createImageData` / `putImageData` | 2 | CPU buffer → GPU texture upload (once) | Better (cached) |
| `fillText` | 27 | TTF via raylib DrawText | Identical |
| `drawImage` (compositing) | 5 | Render-to-texture + fullscreen quad | Better |

**Nothing gets worse. Bloom, scanlines, and chromatic aberration get dramatically better as GPU shader passes.**

## 4. Current System Inventory

### 4.1 Entity Systems (port as C++ structs + vectors)

- Enemy missiles (10 variants: standard, fast, zig, stealth, decoy, split, heavy, cruise, carrier, drone)
- Player missiles
- UFOs (normal + boss variant)
- Raiders
- Daemon boss (easter egg)
- Hell Raiser defense turret
- Phalanx CIWS turrets (dual, with Gatling barrel spin)

### 4.2 Particle/FX Systems (port as flat arrays)

- Explosions, smoke, sparks, trails, debris
- Shockwave rings, light bursts
- Muzzle flashes, ground scorches
- Shooting stars, floating text
- Weather particles (rain/snow/ash, up to ~600)

### 4.3 Rendering Systems (~3,000 lines of draw code)

- Sky + celestials (day/night cycle, stars, nebula, aurora, clouds, moon/sun)
- Mountains (parallax far + near layers)
- Ground plane with grid
- Cities (procedural buildings, 3 themes) + ruins
- Bases with radar dishes
- Phalanx with rotating Gatling assembly
- Hell Raiser with silo doors
- All vehicles (UFO, Raider, Daemon with wings/eyes/aura)
- Missiles with trail rendering (10+ variants)
- Post-processing: bloom, scanlines, vignette, chromatic aberration, grain, flash, danger tint

### 4.4 Audio System (~700 lines)

- All procedurally synthesized via Web Audio API (zero audio files)
- 14+ sound effects (launch, hit, impact, phalanx, hellRaiser, emp, city, thunder, etc.)
- Continuous ambient layers (drone, danger voice, phalanx hum, storm)
- Adaptive beat engine driven by danger level
- Reverb via procedural convolver impulse response

### 4.5 Game Logic

- Wave planning with weighted enemy selection
- Auto-defense AI with intercept prediction
- Phalanx targeting with predictive lead
- Shop/upgrade system (5 upgrades)
- Combo/scoring system
- 3 visual themes (modern, xbox, recharged)

## 5. Migration Steps

### Step 1: Project Skeleton
- Visual Studio 2022 C++ project with CMake
- Integrate raylib via vcpkg or submodule
- Window creation with dynamic resize (mirrors current `resize()`)
- Main loop: `while(!WindowShouldClose()) { dt = GetFrameTime(); update(dt); draw(); }`

### Step 2: State Layer
- Define `GameState` struct with all entity vectors
- Define entity structs: Enemy, PlayerMissile, UFO, Raider, Explosion, Spark, Smoke, Trail, Debris, Shock, LightBurst, MuzzleFlash, Scorch, ShootingStar, FloatingText, City, Base, Phalanx, HellRaiser, Demon
- Port utility functions (clamp, rand, lerp, angleDelta, easeOut, easeIn, mixRgb)

### Step 3: Input
- Map mousemove → GetMousePosition(), click → IsMouseButtonPressed(), keydown → IsKeyPressed()
- Port 20+ key bindings

### Step 4: Game Logic
- Port all `updXxx(dt)` functions (pure math, near line-for-line translation)
- Port `collisions()` (distance checks translate directly)
- Port wave planning, auto-defense AI, Phalanx targeting
- Port shop/upgrade/scoring systems

### Step 5: Rendering (~biggest task)
- Create a Canvas2D wrapper class mapping familiar API to raylib calls:
  - `fillRect()`, `arc()`, `linearGradient()`, `radialGradient()`
  - `pushMatrix()`/`popMatrix()`, `setBlendMode()`, `setAlpha()`
- Port draw functions in layer order: sky → mountains → ground → cities → bases → vehicles → missiles → explosions → particles → HUD
- Implement 3 themes as palette/branch switches

### Step 6: Post-Processing (biggest perf win)
- Write GLSL fragment shaders for: bloom, scanlines, vignette, chromatic aberration, flash, danger tint, grain
- Use render-to-texture for bloom pass (replaces expensive offscreen canvas approach)

### Step 7: Audio
- Use miniaudio for real-time procedural synthesis
- Port each SFX as buffer-filling function
- Port continuous ambient layers as persistent generators
- Port adaptive beat engine

### Step 8: Polish and Verification
- Screenshot comparison at multiple game states (intro, mid-wave, heavy combat, each theme)
- Verify all key bindings and mouse controls
- Play through levels 1-30+ confirming wave scaling, scoring, shop, all enemy types
- Profile with Visual Studio Profiler — target 60fps at 4K with 500+ entities

## 6. Effort Estimate

| Scope | Estimate |
|---|---|
| Playable core (rendering + input + game loop + basic audio) | 1-2 weeks |
| Full faithful port with all systems | 3-6 weeks |
| Post-processing shaders | 2-3 days |
| Audio system | 3-5 days |
| Testing and tuning | 3-5 days |

## 7. Key Decisions

- **raylib over SDL2**: less boilerplate, closer to current immediate-mode style
- **Canvas2D wrapper class**: enables near-mechanical porting of ~3,000 lines of draw code
- **Procedural audio preserved**: maintains zero-dependency philosophy and runtime control
- **No art assets**: everything remains procedurally generated

## 8. Risk Assessment

| Risk | Mitigation |
|---|---|
| Gradient rendering differences | Pre-bake complex gradients as textures; verify visually |
| Text rendering differences | Use same font families via TTF loading; match size/weight |
| Audio synthesis timing | miniaudio callback runs on dedicated thread; port oscillator math directly |
| Theme branching complexity | Keep same if/else structure as JS; themes are palette + geometry branches |
| Coordinate system differences | raylib uses same top-left origin as Canvas 2D |
