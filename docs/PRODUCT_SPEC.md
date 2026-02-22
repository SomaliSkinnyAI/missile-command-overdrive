# Missile Command Overdrive: Product Specification

## 1. Game Identity

- Title: `Missile Command Overdrive`
- Delivery model: single-file browser game (`misslecommand_enhanced.html`)
- Core fantasy: high-pressure planetary defense with escalating enemy patterns, cinematic VFX, and layered defensive systems.

## 2. Design Pillars

- Immediate readability: player can start firing instantly with minimal onboarding.
- Escalation: each wave increases hostility, projectile complexity, and tactical pressure.
- Tactical variety: manual and auto defense, EMP timing, Phalanx suppression, Hell Raiser burst windows.
- Spectacle: strong sky/ground rendering, bloom, weather, shockwaves, and reactive audio.

## 3. Core Game Loop

1. Wave starts after brief delay.
2. Enemy projectiles/vehicles spawn from a weighted wave plan plus special quotas (UFO/Raider).
3. Player fires interceptors from bases; can use EMP and optional auto-defense.
4. Defensive systems (Phalanx/Hell Raiser) engage depending on state.
5. If cities remain alive and hostiles clear, enter shop phase.
6. Buy upgrades, skip timer or wait for next wave.
7. Lose condition: all cities destroyed.

## 4. Input and Controls

- `LMB`: fire interceptor
- `RMB` or `E`: EMP pulse
- `C`: toggle auto defense
- `H`: deploy/retract Hell Raiser
- `F8`: toggle debug telemetry
- `F9`: export current wave debug JSON
- `F10`: export full session debug JSON
- `M`: mute/unmute
- `+` / `-`: volume up/down
- `]` / `PageUp`: skip to next wave
- `[` / `PageDown`: move to previous wave (minimum wave 1)
- `1..5`: shop purchases (during shop phase)
- `R`: restart
- Secret: type `666` to summon daemon event

## 5. Win/Lose and Progression

### Lose

- Game over when no cities are alive.

### Progression

- Wave number increases automatically after shop timer or manual skip.
- Wave plan and specialty enemy quotas scale with level.
- Some defensive systems can partially recover between waves.

### Shop Upgrades

- Rebuild city
- Buy EMP charge
- Warhead yield
- Reload boost
- EMP amplifier (also improves Phalanx efficiency)

## 6. Defensive Arsenal

### 6.1 Base Interceptors

- 3 main bases with ammo and cooldown.
- Player-fired missiles detonate at target to form blast zones.

### 6.2 EMP

- Limited charges and cooldown.
- Large-radius pulse with high crowd-control value.

### 6.3 Phalanx

- Automated high-rate turret near center defense line.
- Has its own ammo pool, targeting logic, and heat/cool behavior.
- Uses predictive lead to engage high-velocity targets.
- Prioritizes high-threat inbound missiles and applies emergency terminal priority
  to protect itself when under imminent attack.
- Emits detailed per-lock and per-burst telemetry for AI-assisted tuning.

### 6.4 Hell Raiser

- Mid-map heavy defense unit with deploy/retract cycle.
- Uses opening door + rising launcher animation.
- Vulnerable only while surfaced/opening/lowering.
- Fires many small high-agility missiles with frequent retargeting.

## 7. Enemy Systems

## 7.1 Enemy projectile classes

- `standard`
- `fast`
- `zig`
- `stealth`
- `decoy`
- `split`
- `shard` (split child)
- `heavy`
- `cruise`
- `carrier`
- `drone`
- `ufoBomb`
- `spit` (raider output)
- `hell` (daemon output)

## 7.2 Enemy vehicles/entities

- UFO
- Boss UFO variant
- Raider (high-atmosphere fast-turning attacker)
- Daemon (easter egg chaos entity)

## 8. Scoring and Combo

- Kill value depends on projectile/entity class.
- Combo multiplier increases with consecutive kills within combo timer window.
- Max combo tracked separately.
- Periodic EMP charge reward tied to combo milestones.

## 9. Environmental and Presentation Systems

- Dynamic sky cycle (day/night transitions).
- Stars/nebula/aurora/cloud layers.
- Weather fronts (clear/ash/storm) affecting mood and some movement drift.
- Terrain layers and city profiles/ruins.
- Post-processing: bloom, grain, vignettes, flash overlays, threat tint.

## 10. Audio Experience

- Procedural synthesized audio (WebAudio API).
- Layered ambient bed + gameplay SFX + warning tones.
- Positional panning for directional feedback.
- Dedicated weapon signatures (Phalanx, Hell Raiser, EMP, etc.).

## 11. UX/HUD

- HUD includes wave/score/combo/cities/EMP/ammo/threat/weather/upgrades.
- Status panel intentionally moved to bottom center to reduce top-left obstruction.
- Help overlay lists controls and test shortcuts.

## 12. Non-Functional Constraints

- Primary constraint: keep game runtime in a single HTML file.
- Must run in modern desktop browsers without build tooling.
- Maintain responsive behavior for varied viewport sizes.

## 13. Debug and Observability

- Telemetry is operator-controlled and defaults to OFF at startup.
- Export modes:
  - `wave`: focused snapshot of active wave state/events.
  - `session`: full multi-wave timeline for balancing and diagnostics.
- Intended use:
  - diagnose auto-defense weaknesses
  - quantify Phalanx lock quality, timing, and conversion
  - support external analytics/AI post-run evaluation
