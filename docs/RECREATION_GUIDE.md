# Missile Command Overdrive: Recreation Guide

This guide is intended for recreating the game from scratch with behaviorally equivalent systems.

## 1. Target Outcome

A single-file HTML5 game with:

- responsive canvas rendering
- layered world and VFX
- procedural audio
- multiple enemy classes and vehicles
- wave/shop/progression loop
- advanced defenses (Phalanx + Hell Raiser)

## 2. Build Sequence (recommended)

1. Base shell
- Create `index.html` + canvas + minimal HUD.
- Add global state object and utility functions (`clamp`, `rand`, lerp/ease).

2. World setup
- Add `resize()` and world geometry (`groundY`, `horizonY`).
- Generate cities/bases and terrain profiles.

3. Main loop
- Implement `update(dt)` + `draw()` + RAF loop with `dt` cap.

4. Projectile fundamentals
- Add enemy/player missile structs and movement.
- Add timed detonation and blast radius model.

5. Collision + scoring
- Add explosion damage checks and score/combo accumulation.

6. Wave generation
- Implement weighted `wavePlan(level)` and timed spawns.
- Add wave completion and game-over checks.

7. Enemy diversity
- Add missile variants (`fast`, `zig`, `split`, etc.).
- Add vehicle entities: UFO, Raider, Daemon event.

8. Defensive systems
- Add base cooldown/ammo model.
- Add auto-defense intercept solver.
- Add dual Phalanx autonomous turrets (`PHALANX_L`, `PHALANX_R`) flanking center.
- Add Hell Raiser deploy/vulnerability/state machine.

9. VFX and weather
- Add sky layers, weather particles, shockwaves, bloom, grain.

10. Audio system
- Add WebAudio graph and event SFX APIs.
- Bind game telemetry to adaptive ambience.

11. UX + tooling
- Add controls/help text/HUD overlays.
- Add test shortcuts (level skip, secret triggers).
- Add debug controls (`F8` toggle, `F9` wave export, `F10` session export).
- Emit per-wave stats and event timelines for post-run analysis.

## 3. Behavioral Acceptance Checklist

- Opening game shows intro and starts on click.
- Base missiles fire from nearest/selected base and explode at target.
- Enemy projectiles vary by speed/path/behavior.
- City destruction can occur from direct or splash damage.
- Shop appears after clearing wave and allows upgrade purchase.
- Auto-defense can meaningfully engage late-game waves.
- Hell Raiser deploys, is vulnerable while surfaced, and retracts.
- Hell Raiser swarm missiles retarget individual targets.
- Phalanx pair uses predictive lead and can emergency-prioritize imminent threats to itself.
- Debug exports include lock/burst telemetry rich enough for AI-assisted diagnostics.
- Secret daemon trigger works.
- GitHub Pages root URL launches game.

## 4. Suggested Modular Decomposition (if moving off single-file)

- `core/loop.js`
- `core/state.js`
- `systems/spawn.js`
- `systems/collision.js`
- `systems/autoDefense.js`
- `systems/audio.js`
- `render/world.js`
- `render/entities.js`
- `render/effects.js`
- `ui/hud.js`

Then bundle into single-file release artifact for deployment parity.

## 5. Regression Test Focus

- wave-to-wave transitions
- shop purchases and clamping
- combo/score integrity
- base/phalanx/hellraiser ammo and cooldown behavior
- enemy split/deploy events
- mobile viewport scaling and HUD overlap
- debug export correctness (`wave` vs `session` scope)
- phalanx response time to `payload.type === "phalanx"` inbound threats (both units)

## 6. Telemetry Schema Essentials (for recreation parity)

- Session envelope:
  - `schema`, `generatedAt`, `session`, `scope`, `waves[]`
- Scopes:
  - `wave`: current/last wave only
  - `session`: all waves captured since debug-enabled/reset
- Per-wave:
  - `meta`, `stats`, `events[]`, `dropped`
- Minimum event set for balancing:
  - `enemy_spawn`, `enemy_impact`, `enemy_killed`, `enemy_split`
  - `defense_lock`, `defense_burst`, `defense_state`, `asset_destroyed`
- Phalanx-specific fields to preserve:
  - `defense_lock`: `phalanxId`, `payloadType`, `timeToImpact`, `terminalPriority`
  - `defense_burst`: `aimErr`, `targetDist`, `hitRadiusPeak`, `missPeak`,
    `phalanxId`, `payloadType`, `timeToImpact`, `terminalPriority`
