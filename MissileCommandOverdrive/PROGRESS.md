# Missile Command Overdrive — HTML → C# Migration Progress

## Phase 1 — Scaffolding ✅
- [x] Project setup (csproj, Raylib-cs 7.0.2, .NET 10)
- [x] GameState.cs — all fields matching JS `S` object
- [x] Entity classes: Enemy, PlayerMissile, City, Base, Phalanx, HellRaiser, UFO, Raider, Daemon
- [x] Particle structs: Spark, Smoke, Trail, Debris, Shockwave, LightBurst, MuzzleFlash, Scorch, ShootingStar, FloatingText
- [x] Palette.cs — color definitions + VariantColor()
- [x] MathHelpers.cs — Clamp, Rand, Lerp, EaseOut, EaseIn, etc.
- [x] RandHelper.cs — Next, Chance, Pick, PickWeighted
- [x] Program.cs skeleton — window, game loop, basic input, basic draw, HUD, intro/gameover overlays

## Phase 2 — Core Game Logic ✅ PLAYABLE
- [x] VariantStats.cs — vSpeed, vValue, vRes lookup tables
- [x] GameInit.cs — BuildWorld (bases, cities, phalanxes, hellraiser placement)
- [x] GameInit.cs — ResetGame
- [x] WaveSystem.cs — WavePlan, StartWave, SpawnEnemy, ChooseTarget
- [x] PlayerActions.cs — LaunchPlayer, UseEMP
- [x] EnemyUpdate.cs — UpdEnemy, mPos, mVel, ImpactEnemy, SplitMissile
- [x] PlayerUpdate.cs — UpdPlayer
- [x] ExplosionUpdate.cs — UpdExpl, SpawnExpl, ExplRadius
- [x] ParticleUpdate.cs — sparks, smoke, trails, debris, shockwaves, light bursts, muzzle flashes, scorches
- [x] Collisions.cs — enemy/ufo/raider vs player explosions
- [x] CombatHelpers.cs — RegKill, DamageEnemyUnit, DestroyTarget, KillCity
- [x] UFO/Raider/Daemon update + spawn
- [x] Wire everything into Program.cs (HandleInput, Update, Draw calls)
- [x] Program.cs — full Update() with wave spawning, shop timer, game-over check
- [x] Program.cs — full HandleInput() with fire, EMP, restart, level skip

## Phase 3 — Advanced Rendering & Systems ✅ COMPLETE
- [x] Renderer.cs — full rendering pipeline replaces Program.cs inline drawing
- [x] Screen shake (Rlgl translate offset)
- [x] Flash overlay (screen-wide alpha tint)
- [x] Danger tint (red overlay at high danger)
- [x] Vignette (edge darkening)
- [x] Scanlines (retro CRT effect)
- [x] Multi-layer explosion rendering (outer glow, mid, core, ring — player=cyan, enemy=warm)
- [x] Enemy missile rendering (variant-sized warhead, glow, bright core, exhaust flicker, trail line)
- [x] Player missile rendering (glowing warhead, exhaust trail, bright core)
- [x] Procedural city rendering (multi-building skyline, neon windows with flicker, roofs, spires, antennas)
- [x] City ruin rendering (jagged fragments, floating embers)
- [x] Base rendering (platform, dome, oscillating barrel, ammo bar, radar blink)
- [x] UFO rendering (ellipse body, dome, pulsing glow, boss HP bar)
- [x] Raider rendering (triangle body, cockpit, engine glow)
- [x] Ground rendering (gradient, perspective grid, pulsing dots)
- [x] Twinkling procedural stars
- [x] Smoke rendering (soft expanding circles)
- [x] Debris rendering (rotating glowing rectangles)
- [x] Shockwave rendering (double ring, inner + outer)
- [x] Shooting star rendering (line + head)
- [x] Light burst soft glow
- [x] Muzzle flash glow
- [x] Scorch marks on ground
- [x] Trail particle rendering
- [x] Floating text (combo popups)
- [x] Improved crosshair (double ring, center dot, glow)
- [x] Improved HUD (background bar, color-coded warnings)
- [x] Improved intro screen (controls hint, pulsing text)
- [x] Improved game over screen (fade-in, max combo, pulsing restart prompt)
- [x] Shop/wave transition overlay
- [x] Auto-defense AI (runAuto — threat scoring, intercept prediction, UFO targeting)
- [x] Phalanx CIWS update & fire logic (target acquisition, aim tracking, tracer fire, hit/kill)
- [x] Phalanx rendering (turret tower, barrel aim, muzzle flash, ammo bar, CIWS label)
- [x] HellRaiser state machine (hidden→opening→rising→active→lowering→closing→cooldown)
- [x] HellRaiser barrage firing (rapid multi-target missiles)
- [x] HellRaiser rendering (door, rising body, turret, active glow, ammo bar)
- [x] H key toggles HellRaiser deploy/retract
- [x] Weather system (storm/ash/clear — per-wave random selection)
- [x] Weather particles (rain streaks, ash flakes, parallax depth)
- [x] Fog bands (animated translucent overlays)
- [x] Lightning bolts (segmented with branches, flash + shake)
- [x] Mountain silhouette scenery (far + near layers)
- [x] Weather init wired into wave start

## Phase 4 — Curved Trails & Procedural Audio ✅ COMPLETE
- [x] Per-missile trail buffers (Enemy.Trail, PlayerMissile.Trail) — 52/46 position ring buffers
- [x] Trail recording in GameUpdate (enemy + player missiles store positions each frame)
- [x] Curved trail rendering — segment-by-segment fading lines from trail buffer (replaces straight lines)
- [x] Variant-colored trails (cruise=green, carrier=orange, drone=cyan, default=variant color)
- [x] SynthAudio.cs — full procedural synthesizer using Raylib AudioStream
  - Ambient drone (sine+sawtooth+triangle oscillators)
  - Danger hum (triangle, scales with danger level)
  - Adaptive beat system (tempo increases with wave/danger)
  - Voice pool (28 concurrent voices with envelope shaping)
- [x] Audio triggers wired into all game events:
  - Player missile launch (sawtooth sweep + noise burst)
  - HellRaiser fire (chirp + zing + fizz)
  - Enemy launch (square wave drop)
  - Explosion hit (triangle drop + noise)
  - Ground impact (deep sub boom + noise, heavy variant)
  - City destroyed (cascading 4-tone sequence)
  - EMP (rising sweep + noise wash)
  - Wave cleared (ascending triangle fanfare)
  - Game over (descending sawtooth)
  - Incoming warning (triangle chirp)
  - Phalanx burst (mechanical rattle — square + sawtooth + noise)
  - Near miss whoosh (noise sweep + triangle)
  - Thunder (rumble noise + sub bass)
- [x] M key toggles mute
- [x] Phalanx SpinAngle driven by actual barrel spin logic (idle slow, firing fast)

## Phase 5 — Missile Shapes & Phalanx Polish ✅ COMPLETE
- [x] Phalanx spin rate fix — matches HTML: `targetSpin = firing ? 10 + fireMix*18 : 1.5`, asymmetric ramp (up=10, down=1.8)
- [x] Phalanx FireMix properly updated (ramps up +0.03/shot, decays -4.2/s) including all early-return paths
- [x] Enemy missile rendering overhaul — every variant has realistic missile anatomy:
  - Standard/fast/zig/stealth/decoy/split/shard: Body tube, tapered nosecone, bright tip, swept tail fins, engine nozzle, exhaust glow, core dot. Heavy gets larger dimensions + warning glow ring
  - Carrier: Wide hexagonal armored hull, nosecone housing, cockpit window, panel lines, HP bar, upper/lower tail fins, dual engine blocks with glowing exhausts
  - Cruise: Elongated fuselage tube, pointed nosecone, swept wings (upper/lower), tail fins, engine nozzle, exhaust plume ellipse
  - Drone: Delta-wing body, bay panel, bright nosecone, wing tips, sensor eye, engine block, pulsing exhaust
  - Spit/Hell: Bulbous organic body, bright warhead tip, stub fins, exhaust area, fiery plume

## Phase 6 — Visual Polish: Sky, Explosions, Ground ✅ COMPLETE
- [x] Day/night cycle — 840s cycle with smooth cosine wave + hermite smoothing (matches HTML skyCycle exactly)
- [x] Sky gradient — 32-band smooth interpolation: top→mid (0–36%) → mid→bot (36–100%), day/night palette blend
- [x] Horizon haze band — atmospheric glow at horizon line, warm twilight tint
- [x] Moon — arc path across sky, visibility fades with daylight, multi-ring radial glow, disc with terminator + craters
- [x] Sun — arc path, corona glow rings, bright disc, visibility fades at night
- [x] Nebula overhaul — replaced full-width bars with 9 positioned radial-gradient circle blobs (low opacity 0.06–0.16, fades with daylight)
- [x] Clouds — 7 ambient wispy ellipses, drifting/breathing, very subtle alpha (0.05–0.14), screen-blend
- [x] Stars fade with daylight (visibility = clamp(1 - day*1.25))
- [x] Mountains day/night — far: [30,40,78]→[82,106,150], near: [10,12,25]→[42,54,86]
- [x] Ground day/night palette — night=[42,37,68]→[9,10,22], day=[86,84,108]→[28,30,44]
- [x] Ground grid lines — subtle opacity matching HTML (0.08 + day*0.04)
- [x] Atmospheric haze bands — 5 animated translucent bands between horizon and ground
- [x] Grid dots — reduced to HTML-matching faintness (0.018 + danger*0.02 + day*0.012)
- [x] Explosion rendering — 14-ring radial gradient simulation (smooth center→edge color/alpha transitions):
  - Player: white-cyan center → cyan mid → blue-dark → transparent edge
  - Enemy: white-warm center → orange mid → dark red → transparent edge
  - EMP: white → bright cyan → medium blue → dark transparent
  - Outer expansion ring + EMP inner ring
- [x] Smoke rendering — 5-layer radial gradient (bright blue-grey center → dark transparent edge)
- [x] Explosion Emp field added to entity for correct EMP gradient rendering
- [x] Scanlines — changed from blue-tinted to dark/neutral for cleaner look
- [x] Vignette — reduced from 88→52 alpha, 30%→28% screen extent

## Phase 7 — Visual Fidelity Deep Comparison ✅ COMPLETE
- [x] **Atmospheric bokeh** — 12 large overlapping translucent blue radial-gradient circles in sky (additive blend, 10-ring smooth gradients, gentle drift animation)
- [x] **Starfield density** — increased from 300→650 stars, all rendered in single additive pass with glow halos on brighter stars
- [x] **Retro grid ground** — purple/blue perspective grid with quadratic line spacing (denser near horizon), 28 converging vertical lines, horizon edge line
- [x] **Mountains removed** — clean flat horizon transition (mountains removed from DrawAll)
- [x] **Texture filtering** — set default font texture to Point filtering for crisp pixel-art text rendering
- [x] **Ammo indicators** — large 26pt numbers centered above each base with backing panel, border, and glow line; red warning color when low
- [x] **City window brightness** — window colors boosted to full brightness (alpha 220-245), flicker range raised, all 5 color types saturated and vivid
- [x] **Base platform glow** — additive blend ellipse glow ring and soft floor light under each missile base
- [x] **HUD overhaul** — replaced tall info box with single slim 24px bottom bar spanning full width: `WAVE n | SCORE n | COMBO Xn | CITIES n/6 | EMP n/n | AUTO ON/OFF`, label/value color separation (dim labels, cyan values), pipe separators, debug info right-aligned
- [x] **Note position** — adjusted overlay note Y to avoid HUD overlap

## Phase 8 — Visual Parity Pass (HUD + Post-FX) ✅ COMPLETE
- [x] Render-to-texture pipeline for full-frame post-processing
- [x] Bloom pass using downscaled render target (explosions + trails)
- [x] Grain/noise overlay to match HTML film grain
- [x] Chromatic aberration on EMP/heavy impacts
- [x] Post-FX updates: vignette, scanlines, flash horizon gradient, ash tint
- [x] Theme-aware multi-line HUD panel matching HTML stats layout
- [x] Theme-specific crosshair with hot-target feedback

## Migration Status: ✅ COMPLETE
All core systems from the HTML original have been ported:
- Game logic, wave system, all enemy variants, scoring, combos
- Player firing, EMP, auto-defense AI
- Phalanx CIWS, HellRaiser underground launcher
- Weather (storm/ash/clear with particles, fog, lightning)
- Full rendering pipeline with post-processing effects
- Mountains, stars, procedural cities, particle systems
- Curved missile trails (per-missile position buffers)
- Full procedural audio (synthesizer with ambient drone, adaptive beat, 13+ SFX events)
- Detailed missile shapes (warhead, body, fins, exhaust per variant)

## Phase 9 — Visual Parity: Soft Glow & Bloom (in progress)
Goal: Match the HTML canvas look by replacing hard-edged solid circles with smooth radial gradients and adding real bloom blur.

- [x] Generated 128×128 radial gradient texture (white center → transparent edge, quadratic falloff)
- [x] `DrawGradientCircle()` helper — renders soft glow via `DrawTexturePro` with tint
- [x] Bloom blur — multi-pass ping-pong box blur on quarter-res bloom target (3 passes, H+V)
- [x] Bloom overlay — two-pass composite (full-size + slightly larger spread) matching HTML double-draw
- [x] Replaced explosion rendering — 3-layer gradient circles (outer/mid/core) + soft ring halo
- [x] Replaced smoke rendering — 3-layer gradient matching HTML radial gradient stops
- [x] Replaced light burst rendering — 3-layer gradient circles
- [x] Replaced bokeh rendering — single gradient circle per blob (was 10-ring solid circles)
- [x] Replaced cloud rendering — elliptical gradient texture (was 5-layer solid ellipses)
- [x] Nebula blobs (drawNeb) — 9 positioned gradient circles with HSL colors, parallax mouse offset, day/night fade
- [x] Aurora bands (drawAur) — 3 wavy horizontal gradient ribbons, 24-segment sine wave, additive blend
- [x] HSL→RGB conversion helper for nebula/aurora hue parameters
- [x] Nebula + aurora data generation in GameInit.BuildWorld()
- [x] Grain overlay reduced (alpha 0.02+danger*0.015, clamped to 0.08 max — was 0.04+0.03, clamped 0.20)
- [x] Mouse parallax on stars, bokeh, and nebula (subtle depth from mouse tracking)
- [x] Floating text overhaul — black outline (4-direction stroke), pop-in scale animation, sized combo (26pt) vs regular (22pt)
- [ ] Theme-specific explosion/trail palettes

## Phase 10 — HellRaiser Homing Missiles ✅ COMPLETE
Ported the HTML's homing HellRaiser missile behavior — missiles now actively seek enemies instead of flying to a fixed point.

- [x] `PlayerMissile` entity — added homing fields: `Hr`, `HrSpeed`, `HrTurn`, `HrRetarget`, `HrTargetId`, `HrTargetKind`, `SquiggleAmp/Freq/Phase`
- [x] `UpdPlayer` — HR missiles use velocity-based movement with turn rate steering toward target, squiggle oscillation, out-of-bounds expiry
- [x] `HrTargetAlive()` — checks if tracked enemy/ufo/raider still exists in game state
- [x] `GetHrTargetPoint()` — returns predicted target position with velocity lead
- [x] `PickNewHrTarget()` — weighted random selection (distance, threat, city-targeting priority)
- [x] `HellRaiserSystem.FireBarrage` — weighted target collection across enemies, UFOs, raiders (was enemies-only)
- [x] `LaunchHellRaiserMissile` — sets all homing fields, randomized spawn offset, extended duration with travel+overshoot matching HTML
- [x] Retargeting — missiles re-acquire new target every 0.06–0.18s if current target dies or timer expires

## Phase 11 — Landscape & Lightning Overhaul ✅ COMPLETE
Ported HTML's gradient-filled mountains and 3-layer lightning rendering.

- [x] Mountains: dual-palette gradient fill (top+bottom colors per layer), matching HTML's `createLinearGradient`
- [x] Mountains: proper HTML parameters (far: baseY=horizonY+70, amp=H*0.11, 16 segs; near: baseY=horizonY+130, amp=H*0.14, 20 segs)
- [x] Mountains: correct night/day palettes for both layers (far top/bot + near top/bot, 8 color values)
- [x] Mountains: mouse parallax (far layer ±20px, near layer ±45px)
- [x] Mountains: ambient light overlay during day/twilight (screen-blend tint between sky and ground)
- [x] Lightning: pre-generated bolt segments stored in `LightningBolt.Segments` (trunk + branches)
- [x] Lightning: 3-layer rendering matching HTML: (1) wide glow, (2) per-segment main bolt with trunk/branch thickness, (3) bright white core on trunk only
- [x] Lightning: bolt `Bright` field for per-bolt intensity variation
- [x] `MathH.Rand(float, float, Random)` overload for deterministic terrain generation
