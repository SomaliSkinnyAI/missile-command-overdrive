# Development Log

Purpose: maintain a running, chronological technical record to support future maintenance and full recreation.

## 2026-02-22

### Major Gameplay and Feature Work

- Expanded enemy diversity (UFO, Boss UFO, Raider, Daemon event, additional projectile classes).
- Added skip-level test controls (`]` / `[` and PageUp/PageDown).
- Fixed moon/star overlap artifact (stars visible through moon).
- Moved HUD/status to bottom-center for improved visibility.
- Added Hell Raiser defense system:
  - deploy/retract state machine
  - vulnerable surfaced window
  - silo-door style animation
  - rapid swarm fire with retargeting and homing behavior
- Increased late-game defensive viability by scaling interceptor speed and auto-defense throughput.
- Performed high-fidelity render pass for UFO/Boss UFO/Raider/Daemon/carrier/drone.

### Visual Enhancements

- Implemented a dynamic theme-switching system supporting 'Modern' (default cyberpunk), 'Xbox' (faux-3D desert), and 'Recharged' (neon vector) aesthetics.
- Added 'T' key toggle to cycle visual themes during gameplay.
- Synchronized theme state with CSS UI styling, environment color palettes, object geometry generation (bases/cities), and projectile particle effects.


### Phalanx Intercept and Telemetry Tuning

- Resolved Level-20 Phalanx failure mode where sustained fire could produce zero hits.
  - replaced static lead assumptions with predictive target-point math
  - aligned hit validation to predicted target position
  - added velocity-aware miss envelope for fast targets
- Added anti-collapse targeting bias:
  - raised threat weighting for projectiles targeting `PHALANX`
  - added terminal-priority override for imminent `PHALANX` inbound threats
  - widened emergency engagement gate (range/alignment) for terminal defense
- Expanded debug payload for analysis:
  - `defense_lock`: `payloadType`, `timeToImpact`, `terminalPriority`
  - `defense_burst`: `payloadType`, `timeToImpact`, `terminalPriority`,
    plus aim/dispersion metrics (`aimErr`, `targetDist`, `hitRadiusPeak`, `missPeak`)

### Auto Defense Retry Intelligence

- Identified leak pattern in wave telemetry: enemy targets could receive exactly one auto assignment
  and then never be reconsidered if the first intercept failed.
- Replaced one-way reservation behavior with timed reservation (`reserveUntil`).
- Added city-protection retry logic:
  - retry eligible for surviving city-targeted missiles
  - stronger terminal weighting near city impact window
  - added assignment telemetry (`attempt`, `retry`, `timeToImpact`, `reserveUntil`,
    `predictedInterceptAt`, `payloadType`)

### Launcher Ammo Sustain Tuning

- Increased per-wave base launcher ammo scaling for high levels.
- Added dynamic mid-wave resupply for low-ammo bases when city infrastructure is intact.
- Resupply logic now reacts to threat and total-ammo pressure, with emergency boosts when
  launcher inventory drops critically low.

### Deployment and GitHub

- Initialized repository and pushed initial release.
- Added `index.html` launcher for root URL startup.
- Configured GitHub Pages from `main` root.
- Published live URL:
  - `https://somaliskinnyai.github.io/missile-command-overdrive/`

## 2026-02-25

### Phalanx Topology Refactor

- Replaced single center Phalanx with dual units:
  - `PHALANX_L` between base lanes B1/B2
  - `PHALANX_R` between base lanes B2/B3
- Removed center-map Phalanx placement to clear visual space around Hell Raiser and HUD.
- Updated enemy targeting so either Phalanx can be selected/damaged independently.
- Updated splash-damage resolution to destroy impacted Phalanx units independently.
- Updated wave reset logic to rearm both Phalanx units with per-unit balancing.
- Updated Phalanx update/render loops to process both units each frame.

### HUD, Audio, and Telemetry Alignment

- HUD now reports left/right Phalanx ammo independently.
- `Ammo Left` now includes total ammo from both Phalanx units (plus base launchers).
- Audio telemetry now aggregates Phalanx fire mix/pan across live units.
- Debug records now include `phalanxId` in lock/burst/state events for per-unit analysis.

## 2026-02-27

### Visual Effects Overhaul

- Added 11 new visual effects systems:
  - Muzzle flashes with directional radial gradients on player missile launches
  - Ground scorch marks at impact sites (capped at 40, fade over time)
  - Chromatic aberration post-FX (RGB channel offset on screen shake)
  - Lightning bolt rendering during storms (jagged procedural paths)
  - Xbox theme window strobing fix (deterministic hash replacing random flicker)
  - Theme-specific explosion color palettes (modern=blue/cyan, xbox=orange/brown, recharged=neon)
  - Theme-specific crosshair variants (modern=detailed, xbox=minimal, recharged=neon ring)
  - Background shooting stars (random streaks in night sky)
  - Floating combo/score text with pop animation
  - Overlay fade transitions between game states

### Radar Dish Improvements

- Rewrote missile launcher radar dishes across all themes:
  - Parabolic dish shape replacing flat ellipse (bowed rim path helper)
  - Horizontal oscillating rotation with front/back face distinction
  - Tracking indicator light on dish face
  - Feed horn with fade gradient
  - Rim highlight differentiating front vs back of dish rotation
  - Xbox theme: smaller scale variant with same features

### Phalanx CIWS Gatling Conversion

- Replaced dual-barrel Phalanx rendering with 6-barrel rotating Gatling assembly:
  - New `drawGatlingAssembly()` function for all 3 themes
  - Barrel spin state (`spinAngle`, `spinSpeed`) added to Phalanx objects
  - Idle rotation at 1.5 rad/sec, ramping to 10-28 rad/sec when firing
  - Alternating dark/light barrel colors for visible rotation tracking
  - Rotating hub disc with spoke marks at breech end
  - Barrel clamp bands at midpoint for rotation reference
  - Theme variants: modern (gradient barrels + shroud), xbox (desert green), recharged (neon wireframe)
- Fixed muzzle flash visibility: draw heat now uses `max(heat, fireMix)` so flash actually triggers when firing
- Fixed tracer origin: per-theme pivot height (modern=y-78, xbox=y-45, recharged=y-55) with correct muzzle distance projection along aim angle

### Base Launcher Repositioning

- Moved left-most launcher (B1) to midpoint between cities C1 and C2
- Moved right-most launcher (B3) to midpoint between cities C5 and C6
- Center launcher (B2) remains at original position

### Documentation

- Updated all docs to reflect visual and mechanical changes
- Created C++ migration plan document (`docs/CPP_MIGRATION_PLAN.md`)

## Logging Standard (for future updates)

For each new session, add:

1. Date
2. Gameplay changes
3. Technical architecture/data changes
4. Balancing impact
5. Deployment/release changes
6. Outstanding issues / follow-ups

## Open Follow-ups

- Add a lightweight automated smoke test strategy for core loop regressions.
- Consider source modularization + bundling while preserving single-file release artifact.
- Add tagged release cadence and changelog versioning.
