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
