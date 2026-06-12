# Missile Command Overdrive (C# / Raylib)

A native C# / [Raylib-cs](https://github.com/ChrisDill/Raylib-cs) port of the original HTML5 build,
with an expanded visual pipeline (bloom, procedural moon texture, gradient-based atmosphere),
a fleshed-out between-wave shop, and a couple of easter-egg bosses.

The original single-file HTML5 version lives in its own repo:
**[missile-command-overdrive-html](https://github.com/SomaliSkinnyAI/missile-command-overdrive-html)**
(playable at `https://somaliskinnyai.github.io/missile-command-overdrive-html/`).

## Build & Run

Requires the .NET 10 SDK.

```bash
cd MissileCommandOverdrive
dotnet run -c Release
```

For a standalone AOT-compiled build you'll also need the MSVC C++ build tools
(Visual Studio "Desktop development with C++" workload); then:

```bash
dotnet publish -c Release -o publish
```

A non-AOT framework-dependent publish (runs against an installed .NET 10 runtime, no MSVC needed):

```bash
dotnet publish -c Release -o publish-dev -p:PublishAot=false --self-contained false
```

## Controls

| Input | Action |
|---|---|
| `LMB` | Fire interceptor |
| `RMB` / `E` | EMP pulse |
| `C` | Toggle auto-defense AI |
| `H` | Deploy / retract HellRaiser |
| `T` | Cycle visual theme (Modern / Xbox / Recharged) |
| `M` | Mute audio |
| `+` / `-` | Volume up / down |
| `]` / `[` (or `PgUp` / `PgDn`) | Skip level ±1 |
| `1`–`3` | Install a perk draft card (between-wave shop) |
| `4`–`0` | Shop upgrades & repairs (between-wave shop) |
| `Space` | Skip shop timer |
| `R` | Reroll the perk draft, 150 scrap (in shop) / Restart (in play) |
| `F8` | Toggle debug telemetry |
| `F9` / `F10` | Export current-wave / full-session telemetry JSON |

## Self-driving demo / evaluation harness

```bash
MCOD_DEMO=1 dotnet run -c Release
```

Plays a scripted ~50 s session via the auto-defense AI (themes, EMP, HellRaiser,
both bosses, wave 8), captures `demo_*.png` screenshots and `demo_log.txt`
frame stats using the game's own `TakeScreenshot`, then exits. See
`src/DemoDriver.cs`. Enhancement roadmap: [PLAN-NEXT-LEVEL.md](PLAN-NEXT-LEVEL.md).

## Easter eggs

- Type `666` during play to summon the **Daemon** boss.
- Type `777` to summon a **Star Destroyer mothership** — tanks heavy damage, randomly
  raises a deflector shield, and deploys TIE fighters when it reaches mid-screen.
  (All other enemy spawning pauses for the duration.)

## Project Layout

```
MissileCommandOverdrive/
├─ MissileCommandOverdrive.csproj   .NET 10 project file (PublishAot)
├─ PLAN-VISUAL-PARITY.md            ongoing notes on closing the HTML vs. C# visual gap
├─ PROGRESS.md                      rolling progress log
└─ src/
   ├─ Program.cs                    top-level input loop + window setup
   ├─ GameState.cs                  world state container
   ├─ GameInit.cs / GameUpdate.cs   world build + per-frame tick
   ├─ WaveSystem.cs                 wave planning + enemy spawning
   ├─ Combat.cs                     collision, damage, scoring
   ├─ AutoDefense.cs                targeting AI
   ├─ PhalanxSystem.cs              CIWS turret logic
   ├─ HellRaiserSystem.cs           underground missile silo
   ├─ WeatherSystem.cs              fog, rain, ash, lightning
   ├─ DemonSystem.cs                666 easter egg
   ├─ MothershipSystem.cs           777 easter egg (Star Destroyer + TIE fighters)
   ├─ VariantStats.cs               enemy stat scaling by variant/level
   ├─ Entities/                     Enemy, UFO, Raider, Daemon, Mothership, Fighter, etc.
   ├─ Rendering/Renderer.cs         all draw routines (sky, moon, cities, HUD, ships, bloom)
   ├─ Audio/SynthAudio.cs           procedural SFX
   └─ Util/MathHelpers.cs           Clamp/Lerp/Rand/TAU/etc.
```

## Notes

- Gameplay logic closely follows the HTML version; the visual layer is a rewrite on top of Raylib primitives.
- Bloom uses a quarter-res bloom target with a 5-tap separable Gaussian (proper weighted convolution, not a 3-offset pseudo-blur).
- The moon is a procedurally generated 192×192 texture — value-noise maria, crater-scale micro detail, limb shading, screen-blended rim highlight — generated once at startup.
- HUD font is Segoe UI loaded at 64px base with bilinear filtering (falls back to Raylib's bitmap font if the TTF is missing).
