# Missile Command Overdrive: Native Migration Plan

## 1. Motivation

The current single-file HTML5 Canvas 2D implementation (~8,600 lines) faces performance
constraints that will limit future expansion:

- Bloom pass uses two full-screen ctx.filter = blur() draws per frame (CPU-rasterized).
- Scanline post-FX executes ~200+ fillRect calls per frame.
- Particle arrays (sparks, smoke, trails, debris, weather) create GC pressure.
- O(NxM) collision loops (enemy x explosion) are interpreted, not compiled.
- 124+ fillRect, 60+ arc, and 30+ gradient creates per frame all pass through Canvas 2D state machine overhead.
- Procedural audio generation is constrained to Web Audio API thread limits.

Native compilation removes these ceilings and enables significant future feature growth.

## 2. Language Decision: C# (revised from C++)

### Why C++ was originally considered

C++ offers maximum runtime performance, the smallest binary, and direct access to
miniaudio and GLSL shaders. It remains a valid choice for a human developer fluent in C++.

### Why C# is now the better choice

All code for this migration will be AI-authored (GitHub Copilot / Claude). This changes
the cost-benefit analysis:

| Factor | C++ | C# | Impact |
|---|---|---|---|
| AI bug profile | Silent memory corruption (use-after-free, iterator invalidation, uninitialized state) | Loud exceptions with stack traces | **C# bugs are diagnosable; C++ bugs are dangerous** |
| Code volume | ~1.3x (headers, forward declarations, manual memory) | 1x baseline | Less code = less surface area for AI error |
| Build system | CMake (fragile, platform-specific edge cases) | dotnet CLI (deterministic, single command) | C# build never breaks silently |
| Procedural audio | miniaudio (native, clean) | miniaudio via P/Invoke (extra layer) | C++ has slight edge; C# is fully workable |
| Runtime performance | Overkill for 2D/500 entities | More than sufficient with Native AOT | No practical difference at this scale |
| Single .exe | ~2 MB static linked | ~10-15 MB Native AOT | Both achieve the goal |
| Cross-platform | CMake + per-platform testing | dotnet publish -r {rid} | C# is simpler |
| Visual Studio experience | Great | Best-in-class | Debugging, refactoring, IntelliSense all favor C# |
| Compiler safety net | Permissive (few AI mistakes caught) | Strict (nullability, type safety, bounds checks) | C# compiler catches more AI errors before runtime |

### .NET Native AOT eliminates the traditional C# tradeoff

    dotnet publish -r win-x64 -c Release -p:PublishAot=true

This compiles C# directly to native machine code. No JIT, no CLR bundled at runtime,
no managed execution overhead. The result is a single .exe that behaves like a C++ binary.

**Decision: C# 12 / .NET 8+ with Raylib-cs and Native AOT publishing.**

## 3. Recommended Technology Stack

| Component | Recommendation | Rationale |
|---|---|---|
| Language | C# 12 (.NET 8+) | Type-safe, GC-managed, Native AOT for compiled output |
| Window/Input | Raylib-cs (NuGet) | C# bindings for raylib; same immediate-mode API as C version |
| 2D Rendering | Raylib-cs | DrawCircle, DrawRectangle, DrawText -- near 1:1 Canvas 2D mapping |
| Post-FX | Custom GLSL shaders via raylib | Bloom, scanlines, vignette, chromatic aberration as GPU passes |
| Audio | miniaudio via P/Invoke | Procedural synth via buffer callback; oscillator math in C# |
| Math | System.Numerics / manual helpers | Clamp, Lerp, Vector2 built-in |
| Build | dotnet CLI + .csproj | Single command build/publish, no CMake |
| IDE | Visual Studio 2022+ | First-class C# support |

### Audio approach detail

The game synthesizes all audio procedurally (14+ SFX, ambient layers, adaptive beat engine,
convolver reverb). The approach:

1. Wrap miniaudio callback API via a thin P/Invoke layer (~50 lines of interop).
2. Write all oscillator, filter, envelope, and mixing math in C# (pure float arithmetic,
   identical to the JS implementation).
3. The callback fills PCM buffers on a dedicated audio thread, same model as the C version.

Alternative: use Silk.NET OpenAL bindings if P/Invoke maintenance becomes burdensome.

## 4. Graphics Fidelity Assessment

Every Canvas 2D feature has a direct native equivalent:

| Canvas 2D Feature | Usage Count | Native Equivalent | Fidelity |
|---|---|---|---|
| createLinearGradient | 30 | GPU quad with vertex colors or 1D texture | Identical |
| createRadialGradient | 25 | Radial gradient shader or pre-baked texture | Identical |
| arc() / ellipse() | 80+ | Raylib.DrawCircle / DrawEllipse | Identical |
| fillRect | 124 | Raylib.DrawRectangle | Identical |
| globalCompositeOperation (lighter/screen/multiply) | 16 | Rlgl.SetBlendMode | Identical |
| globalAlpha | 10+ | Per-vertex alpha via Color with alpha channel | Identical |
| shadowBlur / shadowColor | 4 | Gaussian blur shader on RenderTexture | Better (GPU) |
| save()/restore() + transforms | 100+ | Rlgl.PushMatrix / PopMatrix | Identical |
| clip() | 2 | Scissor rect via Raylib.BeginScissorMode | Identical |
| ctx.filter = blur() (bloom) | 3 | Single-pass Gaussian blur shader | Much better |
| createImageData / putImageData | 2 | Texture upload from CPU buffer | Better (cached) |
| fillText | 27 | Raylib.DrawText / DrawTextEx | Identical |
| drawImage (compositing) | 5 | RenderTexture2D + fullscreen quad | Better |

**Nothing gets worse. Bloom, scanlines, and chromatic aberration get dramatically better as GPU shader passes.**

## 5. Current System Inventory

### 5.1 Entity Systems (port as C# classes + List<T>)

- Enemy missiles (10 variants: standard, fast, zig, stealth, decoy, split, heavy, cruise, carrier, drone)
- Player missiles
- UFOs (normal + boss variant)
- Raiders
- Daemon boss (easter egg)
- Hell Raiser defense turret
- Phalanx CIWS turrets (dual, with Gatling barrel spin)

### 5.2 Particle/FX Systems (port as List<T> with pooling where beneficial)

- Explosions, smoke, sparks, trails, debris
- Shockwave rings, light bursts
- Muzzle flashes, ground scorches
- Shooting stars, floating text
- Weather particles (rain/snow/ash, up to ~600)

### 5.3 Rendering Systems (~3,000 lines of draw code)

- Sky + celestials (day/night cycle, stars, nebula, aurora, clouds, moon/sun)
- Mountains (parallax far + near layers)
- Ground plane with grid
- Cities (procedural buildings, 3 themes) + ruins
- Bases with radar dishes (parabolic, oscillating, tracking indicator)
- Phalanx with rotating 6-barrel Gatling assembly
- Hell Raiser with silo doors
- All vehicles (UFO, Raider, Daemon with wings/eyes/aura)
- Missiles with trail rendering (10+ variants)
- Post-processing: bloom, scanlines, vignette, chromatic aberration, grain, flash, danger tint

### 5.4 Audio System (~700 lines)

- All procedurally synthesized via Web Audio API (zero audio files)
- 14+ sound effects (launch, hit, impact, phalanx, hellRaiser, emp, city, thunder, etc.)
- Continuous ambient layers (drone, danger voice, phalanx hum, storm)
- Adaptive beat engine driven by danger level
- Reverb via procedural convolver impulse response

### 5.5 Game Logic

- Wave planning with weighted enemy selection
- Auto-defense AI with intercept prediction and timed reservation
- Phalanx targeting with predictive lead and terminal priority override
- Shop/upgrade system (5 upgrades)
- Combo/scoring system
- 3 visual themes (modern, xbox, recharged)

### 5.6 Debug/Telemetry System

- F8 toggle, F9 wave export, F10 session export
- Per-wave records with meta, stats, events[], dropped counters
- Phalanx instrumentation: lock/burst/state events with phalanxId

## 6. Migration Steps

### Step 1: Project Skeleton

    dotnet new console -n MissileCommandOverdrive
    cd MissileCommandOverdrive
    dotnet add package Raylib-cs

- Configure .csproj for Native AOT:

      <PublishAot>true</PublishAot>
      <InvariantGlobalization>true</InvariantGlobalization>

- Window creation with dynamic resize (mirrors current resize())
- Main loop:

      while (!Raylib.WindowShouldClose())
      {
          float dt = Raylib.GetFrameTime();
          Update(dt);
          Raylib.BeginDrawing();
          Draw();
          Raylib.EndDrawing();
      }

### Step 2: State Layer
- Define GameState class with all entity lists
- Define entity classes/structs: Enemy, PlayerMissile, UFO, Raider, Explosion, Spark, Smoke,
  Trail, Debris, Shock, LightBurst, MuzzleFlash, Scorch, ShootingStar, FloatingText,
  City, Base, Phalanx, HellRaiser, Daemon
- Port utility functions (Clamp, Rand, Lerp, AngleDelta, EaseOut, EaseIn, MixRgb)
- Use struct for high-volume short-lived types (Spark, Smoke, Debris) to reduce GC pressure

### Step 3: Input
- Map mouse/keyboard:
  - Raylib.GetMousePosition(), Raylib.IsMouseButtonPressed()
  - Raylib.IsKeyPressed(), Raylib.IsKeyDown()
- Port 20+ key bindings (identical mapping)

### Step 4: Game Logic
- Port all updXxx(dt) functions (pure math, near line-for-line translation)
- Port collisions() (distance checks translate directly)
- Port wave planning, auto-defense AI, Phalanx targeting
- Port shop/upgrade/scoring systems
- Port debug telemetry capture and JSON export (System.Text.Json)

### Step 5: Rendering (~biggest task)
- Create a Canvas2D helper class mapping familiar API to Raylib-cs calls:
  - FillRect(), DrawArc(), LinearGradient(), RadialGradient()
  - Rlgl.PushMatrix() / PopMatrix(), blend modes, alpha
- Port draw functions in layer order:
  sky -> mountains -> ground -> cities -> bases -> vehicles -> missiles ->
  explosions -> particles -> HUD
- Implement 3 themes as palette/branch switches
- Port Gatling assembly rendering (spin state, alternating barrels, hub disc)
- Port radar dish rendering (parabolic shape, oscillation, tracking light)

### Step 6: Post-Processing (biggest perf win)
- Write GLSL fragment shaders for:
  bloom, scanlines, vignette, chromatic aberration, flash, danger tint, grain
- Use Raylib.LoadRenderTexture() for bloom pass
  (replaces expensive offscreen canvas approach)

### Step 7: Audio
- Create miniaudio P/Invoke wrapper (~50 lines of interop declarations)
- Implement audio callback that fills PCM float buffers
- Port each SFX as buffer-filling method (oscillators, noise, envelopes)
- Port continuous ambient layers as persistent generator instances
- Port adaptive beat engine
- Port convolver reverb (procedural impulse response)

### Step 8: Polish and Verification
- Screenshot comparison at multiple game states (intro, mid-wave, heavy combat, each theme)
- Verify all key bindings and mouse controls
- Play through levels 1-30+ confirming wave scaling, scoring, shop, all enemy types
- Profile with Visual Studio Profiler -- target 60fps at 4K with 500+ entities
- Verify Native AOT publish produces working single .exe
- Test debug telemetry export (F9/F10) matches schema from JS version

## 7. Project Structure

    MissileCommandOverdrive/
    +-- MissileCommandOverdrive.csproj
    +-- src/
    |   +-- Program.cs              // Entry point, window init, main loop
    |   +-- GameState.cs            // Global state class + constants
    |   +-- Entities/
    |   |   +-- Enemy.cs
    |   |   +-- PlayerMissile.cs
    |   |   +-- Explosion.cs
    |   |   +-- UFO.cs
    |   |   +-- Raider.cs
    |   |   +-- Daemon.cs
    |   |   +-- City.cs
    |   |   +-- Base.cs
    |   |   +-- Phalanx.cs
    |   |   +-- HellRaiser.cs
    |   |   +-- Particles.cs       // Spark, Smoke, Trail, Debris, etc.
    |   +-- Systems/
    |   |   +-- WavePlanner.cs
    |   |   +-- Collision.cs
    |   |   +-- AutoDefense.cs
    |   |   +-- PhalanxAI.cs
    |   |   +-- Shop.cs
    |   |   +-- Scoring.cs
    |   +-- Rendering/
    |   |   +-- Canvas2D.cs         // Helper class wrapping Raylib calls
    |   |   +-- WorldRenderer.cs    // Sky, mountains, ground, weather
    |   |   +-- EntityRenderer.cs   // All entity/vehicle draw methods
    |   |   +-- EffectsRenderer.cs  // Particles, shockwaves, muzzle flash
    |   |   +-- PostFX.cs           // Bloom, scanlines, vignette, etc.
    |   |   +-- HUD.cs
    |   |   +-- Themes.cs           // Modern, Xbox, Recharged palettes
    |   +-- Audio/
    |   |   +-- MiniAudioInterop.cs // P/Invoke declarations
    |   |   +-- AudioEngine.cs      // Callback, mixer, bus routing
    |   |   +-- Oscillators.cs      // Sine, square, saw, noise generators
    |   |   +-- Effects.cs          // Reverb, filter, envelope
    |   |   +-- SoundBank.cs        // 14+ procedural SFX definitions
    |   +-- Debug/
    |   |   +-- Telemetry.cs        // Event capture, per-wave records
    |   |   +-- TelemetryExport.cs  // JSON export (wave/session scope)
    |   +-- Util/
    |       +-- MathHelpers.cs      // Lerp, EaseOut, AngleDelta, MixRgb
    |       +-- RandHelper.cs       // Seeded random with game-specific distributions
    +-- shaders/
        +-- bloom.frag
        +-- scanlines.frag
        +-- vignette.frag
        +-- chromatic.frag
        +-- composite.frag

## 8. Cross-Platform Build Commands

    # Windows (primary target)
    dotnet publish -r win-x64 -c Release -p:PublishAot=true

    # Linux
    dotnet publish -r linux-x64 -c Release -p:PublishAot=true

    # macOS (Apple Silicon)
    dotnet publish -r osx-arm64 -c Release -p:PublishAot=true

    # macOS (Intel)
    dotnet publish -r osx-x64 -c Release -p:PublishAot=true

No CMake, no vcpkg, no platform-specific build scripts. Same codebase, same command.

## 9. Cross-Platform Ground Rules

These rules cost nothing now but prevent costly rework later:

1. **No Windows-specific APIs** -- no DllImport kernel32, no System.Windows.Forms,
   no registry access. Raylib and miniaudio handle all platform abstraction.
2. **Use Path.Combine() or System.IO** -- never hardcode backslash path separators.
3. **UTF-8 everywhere** -- raylib expects UTF-8; .NET strings are UTF-16 internally
   but Encoding.UTF8 for any file I/O.
4. **Embed shaders as resources or load via relative path** -- no absolute paths.
5. **Test on a second platform early** -- one successful WSL/Linux build after Step 1
   confirms the setup is clean.

## 10. Effort Estimate

| Scope | Estimate |
|---|---|
| Playable core (rendering + input + game loop + basic audio) | 1-2 weeks |
| Full faithful port with all systems | 3-5 weeks |
| Post-processing shaders | 2-3 days |
| Audio system (including P/Invoke layer) | 4-6 days |
| Debug/telemetry system | 1-2 days |
| Testing and tuning | 3-5 days |

Effort is slightly reduced vs. original C++ estimate due to elimination of manual memory
management, header/source splitting, and CMake configuration.

## 11. Key Decisions

- **C# over C++**: AI-authored codebase benefits from GC safety, loud failure modes,
  and simpler build tooling. Native AOT eliminates the traditional performance/binary tradeoff.
- **Raylib-cs over MonoGame/FNA**: closest API match to Canvas 2D; immediate-mode;
  same underlying C library as the original C++ plan.
- **Canvas2D helper class**: enables near-mechanical porting of ~3,000 lines of draw code.
- **miniaudio via P/Invoke over NAudio/OpenAL**: preserves the procedural synthesis model;
  callback-based buffer filling is the same pattern as Web Audio ScriptProcessor.
- **Procedural audio preserved**: maintains zero-dependency philosophy and runtime control.
- **No art assets**: everything remains procedurally generated.
- **struct for hot particles**: Spark, Smoke, Trail, Debris as value types to minimize GC
  pressure in the highest-volume entity categories.

## 12. Risk Assessment

| Risk | Mitigation |
|---|---|
| Native AOT reflection limitations | Avoid reflection; use source generators for JSON serialization |
| Gradient rendering differences | Pre-bake complex gradients as textures; verify visually |
| Text rendering differences | Load TTF via Raylib.LoadFont; match size/weight |
| Audio synthesis timing | miniaudio callback runs on dedicated thread; port oscillator math directly |
| P/Invoke overhead in audio callback | Callback is registered once; buffer fill is pure C#; no per-sample interop |
| Theme branching complexity | Keep same if/else structure as JS; themes are palette + geometry branches |
| Coordinate system differences | raylib uses same top-left origin as Canvas 2D |
| GC pauses during heavy particle frames | Use structs for high-volume types; pre-allocate lists; profile if needed |
| Native AOT binary size | ~10-15 MB acceptable; trim unused assemblies if needed |

## 13. Superseded Plan

The original plan (C++17 + raylib + miniaudio + CMake) remains technically valid for a
human C++ developer. It was superseded because the AI-authored development model changes the
risk profile: C# compiler safety, garbage collection, and simpler toolchain reduce the
probability and severity of bugs introduced during automated code generation, with no
meaningful sacrifice in runtime performance for a 2D game of this scope.
