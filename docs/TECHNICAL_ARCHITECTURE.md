# Missile Command Overdrive: Technical Architecture

## 1. Runtime Architecture

- Platform: browser, Canvas2D + WebAudio.
- Main file: `misslecommand_enhanced.html`.
- Entry flow:
  1. Initialize canvas, HUD, state object `S`.
  2. Build world/sky/weather.
  3. Start `requestAnimationFrame(loop)`.

## 2. Core State Model

Global state object `S` stores all simulation data, including:

- Meta/game flow: `intro`, `gameOver`, `level`, `shop`, timers.
- World geometry: `w`, `h`, `groundY`, `horizonY`.
- Defenders: `bases`, `phalanx`, `hellRaiser`, EMP counters.
- Enemies: `enemy`, `ufo`, `raiders`, `demon`.
- Player ordnance: `player`.
- FX arrays: `expl`, `smoke`, `sparks`, `trails`, `debris`, `shock`, `lightBursts`.
- Atmospherics: stars, nebula, aurora, weather particles/fog.
- UI: messages, notes, floating texts, selected base.

## 3. Game Loop

Function `loop(ts)`:

1. Compute `dt` (capped to avoid giant steps).
2. `update(dt)`:
   - advance timers and game flow
   - spawn enemies per plan/quotas
   - update all actors and FX
   - resolve collisions
   - evaluate wave completion or game over
   - update audio state
3. `draw()`:
   - render world layers back-to-front
   - render entities/projectiles/fx
   - apply post-processing
   - draw HUD and overlays

## 4. Rendering Pipeline (layer order)

1. Sky + celestials
2. Deep atmosphere (nebula/aurora/clouds/stars/weather back)
3. Mountains + ground
4. World objects (cities, bases, Hell Raiser, Phalanx)
5. Vehicles/entities (UFO/Raider/Daemon)
6. Trails, weather front, smoke
7. Projectiles and explosions
8. Sparks/debris/shockwaves
9. Bloom pass + crosshair + HUD + overlays + postFX

## 5. Projectile and Collision Model

- Enemy missiles use parametric movement and optional behavior modifiers:
  - sinusoidal offsets (`zig`, `drone`, `cruise`, etc.)
  - homing factors (`cruise`, `drone`, `hell`)
  - split events (`split` -> `shard`)
- Player missiles detonate at intercept target.
- Explosions produce area-of-effect checks against enemy objects.
- Damage/resistance model uses type-specific values (`vRes`, hp per class).

## 6. Defensive AI

### Auto Defense

- Computes intercept opportunities from active bases.
- Prioritizes by threat score and feasible intercept timing.
- Uses coverage prediction to reduce wasted overlapping shots.
- Interceptor speed scales with level via `interceptorSpeed()`.

### Phalanx

- Autonomous turret with threat targeting and high fire rate.
- Tracks heat, ammo, cooldown, and audio mix.

### Hell Raiser

- State machine: hidden -> opening -> rising -> active -> lowering -> closing -> cooldown.
- Vulnerable while surfaced/opening/lowering.
- Uses per-shot target distribution and in-flight retargeting/homing for swarm behavior.

## 7. Enemy Orchestration

- `wavePlan(level)` generates weighted projectile composition and timed salvos.
- Special quotas/timers inject UFOs and Raiders during wave.
- Daemon is event-triggered by secret input sequence.

## 8. Audio Architecture

- `mkAudio()` builds WebAudio graph:
  - master/music/sfx buses
  - compressor and convolution reverb
  - procedural oscillators/noise sources
- Public API includes launch/hit/impact/wave/emp/nearMiss/phalanx/thunder/hellRaiser.
- `audio.update()` receives game telemetry (`danger`, `weather`, `phalanxLevel`, etc.).

## 9. Performance Notes

- Frequent object arrays are manually updated and pruned each frame.
- Trail and particle lengths are capped.
- Bloom buffer is reused and resized with viewport.
- Most effects are approximated for speed over physically-based simulation.

## 10. Rebuild-Critical Constants and Behaviors

- Base interceptor baseline speed constant: `BASE_PLAYER_SPEED = 640`.
- Interceptor runtime speed: `interceptorSpeed(mult)` with level/weather scaling.
- Threat scaling drives both audio intensity and AI urgency.
- Large explosions can splash-destroy multiple ground assets.

## 11. Known Naming/Compatibility Notes

- File name intentionally uses `misslecommand_enhanced.html` (legacy typo retained).
- `index.html` exists as launcher/redirect entry for Pages.
