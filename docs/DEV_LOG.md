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
