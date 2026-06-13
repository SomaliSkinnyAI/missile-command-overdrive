# Missile Command Overdrive (C# / Raylib)

A native C# / [Raylib-cs](https://github.com/ChrisDill/Raylib-cs) arcade roguelite — a
Missile Command–style fixed-screen defender with a GPU shader pipeline, a procedural
synth + adaptive music director, authored wave design, a perk-draft economy, multi-phase
bosses, and a full title → run → ceremony loop.

It began as a port of the original single-file HTML5 build
(**[missile-command-overdrive-html](https://github.com/SomaliSkinnyAI/missile-command-overdrive-html)**,
playable at `https://somaliskinnyai.github.io/missile-command-overdrive-html/`) and has since
been taken several magnitudes past it. The full design rationale and phased build history is in
**[PLAN-NEXT-LEVEL.md](PLAN-NEXT-LEVEL.md)**.

## Build & Run

Requires the **.NET 10 SDK**. On macOS: `brew install dotnet` (then `export DOTNET_ROOT=$(brew --prefix dotnet)/libexec`).

```bash
cd MissileCommandOverdrive
dotnet run -c Release
```

The game boots to a living **title screen** (auto-played backdrop, menu, high-score marquee);
press Start to play. Settings and high scores persist to `profile.json` under your platform's
application-data directory.

A framework-dependent publish (runs against an installed .NET 10 runtime, no extra toolchain):

```bash
dotnet publish -c Release -o publish-dev -p:PublishAot=false --self-contained false
```

> The `csproj` sets `PublishAot=true`; a true standalone AOT `dotnet publish` needs the platform
> native toolchain (MSVC "Desktop development with C++" on Windows; clang/Xcode on macOS). You do
> **not** need any of that to develop and play — `dotnet run -c Release` is enough.

## Features

- **Shader render pipeline** — FP16 HDR scene target with ACES filmic tonemap, GPU dual-Kawase
  threshold bloom, a dynamic 2D light buffer (explosions light the skyline), screen-space
  shockwave refraction / heat shimmer / EMP ripple, and a GLSL-330 composite uber-shader
  (chromatic aberration, vignette, grain, barrel warp). Falls back to RGBA8 automatically
  (or force it with `MCOD_NO_HDR=1`).
- **Three theme identities** (`T`) — Modern neo-noir, Xbox warm green-phosphor with a Lottes CRT
  branch, Recharged crushed-black neon — plus a colorblind-safe palette toggle.
- **Procedural audio** — a sample-accurate stereo synth engine (64-voice pool, SVF filters,
  four-bus mixer with reverb / compression / sidechain ducking / saturated sub-bass) and an
  intensity-driven **music director** whose stems build and strip with the action.
- **Run depth** — a Wave Director that authors tension (build / peak / lull / finale squads)
  with a next-wave intel forecast, behavioral enemies (carrier drone deploys, MIRV heavies,
  cloaking stealth, shield drones), a **15-perk draft armory**, and a scrap economy separate
  from your leaderboard score.
- **Bosses** — multi-phase encounters scheduled **every 5th wave** (Mothership with destructible
  shield pods, Daemon with telegraphed meteor / hell-fan / firewall phases), plus the classic
  cheat-code summons.
- **Presentation** — hit-stop + trauma camera, wave-intro stingers, a count-up report card, and
  an end-of-run **ceremony** with an S/A/B/C/D letter grade and arcade initials entry.
- **Seeded & daily runs** — `xoshiro256**` with stream-split determinism; press `D` on the title
  for a daily seed shared across machines.

## Controls

### In play
| Input | Action |
|---|---|
| `LMB` | Fire interceptor |
| `RMB` / `E` | EMP pulse |
| `C` | Toggle auto-defense AI |
| `H` | Deploy / retract HellRaiser |
| `T` | Cycle visual theme (Modern / Xbox / Recharged) |
| `M` | Mute audio |
| `]` / `[` (or `PgUp` / `PgDn`) | Skip level ±1 |
| `R` | Restart run |
| `Esc` | Pause / settings menu |
| `F8` | Toggle debug telemetry overlay |

### Between-wave strategy screen
| Input | Action |
|---|---|
| `1`–`3` | Install a perk draft card |
| `R` | Reroll the draft (150 scrap) |
| `4` | Rebuild a city (500) · `5` Buy EMP (250) |
| `6` | Warhead Yield (400) · `7` Reload Boost (350) · `8` EMP Amplifier (360) |
| `9` | Repair base (300) · `0` Repair phalanx (250) |
| `Space` | Skip the shop timer |

### Title & menus
| Input | Action |
|---|---|
| `↑`/`↓`, `Enter` | Navigate / select |
| `←`/`→` | Adjust a setting (volume, shake, UI scale, …) |
| `D` (title) | Start a daily-seeded run |
| `Esc` | Resume (pause) / return to title (game over) |

Pause-menu settings: volume, shake intensity, flash reduction, UI scale, theme, borderless
fullscreen, two assist toggles (slower enemies / auto-EMP at last city), colorblind mode,
restart, quit — all persisted.

## Easter eggs

- Type `666` during play to summon the **Daemon** boss instantly.
- Type `777` to summon a **Star Destroyer mothership** instantly.

(Both also appear as scheduled bosses every 5th wave; the codes just call them early.)

## Self-driving demo / evaluation harness

```bash
MCOD_DEMO=1 dotnet run -c Release         # screenshots + frame stats, then exits
MCOD_SELFTEST=1 dotnet run -c Release      # seeded-wave determinism check (prints PASS)
```

`MCOD_DEMO=1` plays a scripted ~50 s session via the auto-defense AI (title, pause, shop, perk
draft, a scheduled boss, the ceremony, wave 8), capturing `demo_*.png` and `demo_log.txt` via the
game's own `TakeScreenshot` — no OS screen-capture permission needed. See `src/DemoDriver.cs`.

Other env switches: `MCOD_NO_HDR=1` (RGBA8 fallback), `MCOD_LEGACY_WAVES=1` (pre-director wave
generator), `MCOD_AUDIO_POLLED=1` (polled audio fallback instead of the callback).

> On macOS the windowed demo needs the display awake (raylib's `InitWindow` fails on a sleeping
> display); prefix with `caffeinate -u -t 5; MCOD_DEMO=1 caffeinate -d -i dotnet run -c Release`.

## Project Layout

```
MissileCommandOverdrive/
├─ MissileCommandOverdrive.csproj   .NET 10 project file (PublishAot)
├─ PLAN-NEXT-LEVEL.md (repo root)   the multi-phase enhancement roadmap + design rationale
├─ assets/                          embedded JetBrains Mono fonts (+ OFL license)
└─ src/
   ├─ Program.cs                    top-level loop, input routing, env-switch harnesses
   ├─ GameState.cs                  world state container + GamePhase FSM + Settings
   ├─ GameInit.cs / GameUpdate.cs   world build + per-frame tick (sim/raw/fx time spine)
   ├─ Events.cs / FeelDirector.cs   event ring bus + hit-stop / trauma / slow-mo director
   ├─ WaveSystem.cs                 Wave Director: squad templates, tension envelope, forecast
   ├─ VariantStats.cs               enemy variant definitions + stat scaling
   ├─ Combat.cs                     collision, damage, scoring, scrap
   ├─ Perks.cs                      perk-draft armory + PerkFlags hooks
   ├─ AutoDefense.cs                targeting AI
   ├─ PhalanxSystem.cs              CIWS turret logic
   ├─ HellRaiserSystem.cs           underground homing-missile silo
   ├─ Bosses/                       BossBase (phases/pods) + BossSystem (unified damage loop)
   ├─ MothershipSystem.cs / DemonSystem.cs   the two boss entities (scheduled + cheat-summoned)
   ├─ WeatherSystem.cs              fog, rain, ash, lightning
   ├─ Menu.cs / Ceremony.cs / AttractSystem.cs   pause+title menus, end-run ceremony, attract mode
   ├─ Profile.cs                    source-gen JSON profile: settings, top-10, lifetime stats
   ├─ Entities/                     Enemy, UFO, Raider, Mothership, Fighter, particle structs
   ├─ Rendering/Renderer.cs         all draw routines (HDR scene, light buffer, bloom, HUD)
   ├─ Rendering/Shaders.cs          embedded GLSL-330 uber-shader + bloom/Kawase passes
   ├─ Rendering/ThemePalette.cs     the single color authority (sky/ground/grade/CRT per theme)
   ├─ Audio/SynthAudio.cs           synth engine, four-bus mixer, music director sequencer
   ├─ Util/Xoshiro.cs               xoshiro256** + seed helpers (deterministic runs)
   └─ DemoDriver.cs                 self-driving eval/screenshot harness (MCOD_DEMO)
```

## Notes

- **Determinism:** wave composition draws from a per-wave `xoshiro256**` stream
  (`MasterSeed ^ wave`); cosmetics and audio use separate streams. `MCOD_SELFTEST=1` asserts two
  independent generations match — daily seeds reproduce across machines.
- **Audio thread:** the synth runs on a raylib `AudioStream` callback that is strictly
  allocation/lock/raylib-free; the game thread feeds it through a single-producer command ring,
  and the music sequencer steps sample-accurately inside the callback.
- **HUD/UI** render to the backbuffer *after* the world composite, so post-FX (grade, CRT,
  refraction, camera shake) never distort the readouts. Hot-path draw code is zero-allocation.
- Fonts are embedded JetBrains Mono (no system-font dependency); the moon is still a procedurally
  generated 192×192 texture built once at startup.
