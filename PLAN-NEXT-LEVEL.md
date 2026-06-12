# Missile Command Overdrive — NEXT LEVEL Plan

**Date:** 2026-06-11
**Scope:** C# / .NET 10 / Raylib-cs 7.0.2 (raylib 5.5), macOS (GL 3.3 core, GLSL 330) + Windows, `PublishAot=true`
**Sources:** 5 codebase survey agents, 2 research agents (juice techniques, Raylib binding verification by reflection over the shipped DLL), 4 lens designs (graphics, audio, gameplay, feel) each with an adversarial feasibility audit, and a 3-judge panel (player value, technical soundness, scope coherence). Every file:line anchor below was verified by at least one audit against the actual source.

---

## 1. Executive Summary

Missile Command Overdrive is a clean, faithful port with genuinely good bones: strict loop/sim/render separation, parametric flight physics, smart multi-factor targeting AI, a fully procedural synth, and a disciplined CPU renderer that fakes bloom and gradients better than it has any right to. It is solid **arcade-jam** software. The owner's brief is a **step change to premium-indie** — "a magnitude better" in graphics, sound, and gameplay depth.

The review converged on a clear diagnosis:

- **Graphics** have hit the absolute ceiling of shaderless rendering. Zero shaders are loaded anywhere; post-FX are approximations (additive "chromatic aberration" that just brightens, 360 `DrawLine` scanlines), 8-bit targets clip all additive light to flat white, and the bloom pass works by *hand-replaying the exact RNG call sequence* of the city renderer.
- **Audio** is a mono toy mixer: pan parameters are accepted and silently discarded at six call sites, filter sweeps were designed into the event API and never implemented (noise `Freq` fields do nothing), the voice clock is decoupled from the sample clock (audible warble at every buffer boundary), and the "sub-bass" reaches 22 Hz — physically inaudible on the laptop it is played on.
- **Gameplay** has no run structure: the shop spends *score* (so engaging with progression punishes the leaderboard), waves are flat statistical noise, 13 enemy variants are mostly stat-tints (the carrier's deploy behavior is literally dead code — `_DeployAt` is assigned and never read), nothing persists, and the best content (two bosses) is locked behind typing cheat codes.
- **Feel/UX** lacks the arcade table stakes: no pause, no settings, no saved high score, ESC disabled, a "Click to Start" text overlay for a title, and a two-line GAME OVER.

The plan below is **6 independently shippable phases**. Shared infrastructure ships first (event bus, time director, shader seam, audio engine, FSM, persistence), and every phase mixes graphics + sound + gameplay + feel so the game is *visibly and audibly better at every milestone*. All judges' must-keeps are honored; all consensus cuts are dropped (listed in §8 with reasons); every feasibility "partial" is shipped in its audited, corrected form.

---

## 2. North Star

> Transform Missile Command Overdrive from a faithful canvas port into a premium native arcade roguelite: every frame HDR-lit, graded, and physically reactive; every explosion felt in stereo and in the chest; every wave authored, every run seeded and persistent, and every death a ceremony that demands one more run.

---

## 3. Current State Assessment

### 3.1 Strengths (keep and build on)

| Area | Strength |
|---|---|
| Architecture | Strict loop/sim/render split; `GameState` as single mutable blackboard — ideal substrate for events, serialization, replays |
| Physics | Parametric missile motion (`pos = start + v·t`) — framerate-robust, deterministic impact timing |
| AI | `AutoDefense.FindIntercept` predicts along *actual* parametric paths incl. zigzag; HP-aware shot economy; HellRaiser weighted retargeting is genuinely good game math |
| Rendering | Best-possible shaderless bloom; Bezier sky gradients; batch-aware rlgl usage; dense characterful entity art at zero asset cost |
| Audio | Fully procedural, zero-asset, clean event API with intensity/pan params already plumbed from game code |
| GC awareness | Struct particles, reverse-iteration removals, LINQ-free particle hot loops |
| Boss design | Mothership has real choreography: shield phases, milestone triggers, hangar fighters with bounded-turn AI |

### 3.2 Weaknesses (the step-change targets)

| Severity | Weakness | Anchor |
|---|---|---|
| HIGH | Bloom replays city RNG sequence by hand — any city-art edit silently desyncs | `Renderer.cs:169-205` vs `1201-1234` |
| HIGH | Fonts Windows-only; macOS (primary platform) ships blocky bitmap fallback | `Renderer.cs:423-449` |
| HIGH | Mono audio; pan params discarded; no filters; no master bus; voice clock decoupled from sample clock | `SynthAudio.cs:32,101,153,157,168,177` |
| HIGH | Score doubles as shop currency — progression punishes engagement | `Program.cs:99-154` |
| HIGH | Wave plan is shapeless noise from one weight table | `WaveSystem.cs:34-51` |
| HIGH | Carrier deploy is dead code (`_DeployAt` assigned, never read) | `Combat.cs:83`, `Enemy.cs:35` |
| HIGH | No pause, no settings, ESC disabled, zero persistence | `Program.cs:12,56-58`, `GameState.cs:18-22` |
| HIGH | Three unseedable RNGs kill determinism | `MathHelpers.cs:11`, `RandHelper.cs:5`, `GameInit.cs` |
| MED | 8-bit targets clip additive light to white; CA is additive double-draw | `Renderer.cs:389, 3284-3299` |
| MED | Coin-flip resurrection of bases/Phalanx/HellRaiser (33/40/58%) removes stakes | `WaveSystem.cs:74,84,96` |
| MED | Per-frame LINQ in resupply, O(n) `Trail.Insert(0)`, closure allocs in homing loop | `GameUpdate.cs:48-67,227,341,363-398` |
| MED | RNG shield timer on Mothership is unreadable; Daemon is a screensaver with HP | `MothershipSystem.cs:59-69`, `DemonSystem.cs:41-80` |
| MED | Themes are 95% identical; weather is cosmetic; combo system shallow and copy-pasted ×5 | `Renderer.cs:103`, `Combat.cs:498-666` |

### 3.3 Performance baseline

57–59 fps at max particle load (demo log, Apple Silicon). The bottleneck risk is **batch flushes** (dozens-to-hundreds of blend-mode toggles per frame) and **Gen0 churn** (LINQ in resupply, trail memmoves, HUD string interpolation), not triangle count.

---

## 4. Cross-Cutting Decisions (binding for all phases)

These resolve every audit correction and every cross-lens collision the judges flagged. They are **non-negotiable contracts** for implementation:

1. **One composite blit.** The trauma camera (rotation/overscan), the post uber-shader, refraction sampling, and the light buffer all ride a single co-designed `DrawTexturePro` composite of `_frameTarget`. Roll capped at ±1.5° with 16 px overscan v1 (±3° needs ~48 px — revisit only if wanted). **HUD, crosshair, and overlays move out of `_frameTarget` to the backbuffer** so they don't rotate/refract with the world.
2. **AOT-safe persistence.** `PublishAot=true` is set in the csproj. All JSON uses **source-generated `JsonSerializerContext`** — reflection-based `System.Text.Json` throws in published builds. One profile file, one schema (gameplay's commander profile **absorbs** feel's initials/high-score table).
3. **Stream-split determinism.** A single shared RNG cannot deliver seed-comparable waves (per-frame consumers interleave with player-dependent calls). Design: master seed → **per-wave plan streams** (`seed ^ waveIndex`) for `BuildPlan`/mutations, a free-running **cosmetic stream**, and **audio stays on its own RNG** entirely. Full input-replay determinism is *out of scope* (variable dt); spawn-plan determinism is the contract for daily seeds and seed racing.
4. **One color authority.** `ThemePalette` is the single owner of color identity. The colorblind-safe variant set and any variant-color discipline ride that one sweep — feel's palette routing and gameplay's enum-ification do **not** perform parallel color refactors. Variant strings become a `VariantDef` lookup *incrementally*, not a big-bang ~140-literal sweep.
5. **One tension signal.** The Wave Director's `s.Intensity` is the *same* scalar consumed by the music director, the audio danger layers, and weather escalation. The spawner and the score must tell one story.
6. **Audio-thread rules.** After Phase 2, all sound triggering goes through the SPSC command ring (`AddVoice` survives as the producer API). Callback body: zero allocations, no raylib calls, top-level try/catch writing silence (escaping exceptions through `[UnmanagedCallersOnly]` abort the process). Polled-fill fallback is an **init-time mode**, not a runtime switch. `SynthAudio.Update` keeps receiving **rawDt** (never simDt) and keeps pumping in every phase including Paused.
7. **Time spine.** `rawDt` (clamped 1/30) drives UI, floaters, shake, menus, audio; `simDt = HitStop>0 ? 0 : rawDt * TimeScale` drives the sim. `SynthAudio._beatStep` is recomputed every frame, so time-scale is passed *into* `SynthAudio.Update` as a parameter, applied after the recompute.
8. **SVF stability.** Chamberlin filter clamped by `f < 1` (i.e. cutoff < SR/6 ≈ 7.35 kHz), **not** the design's SR·0.22. All existing sweep targets (max 3500 Hz) fit.
9. **HDR route.** No `Rlgl.SetFramebufferWidth/Height` exists in the binding. The FP16 FBO is hand-assembled, then **wrapped in a hand-assembled `RenderTexture2D` struct** (public mutable `Id/Texture/Depth`) and used through stock `BeginTextureMode/EndTextureMode` — zero custom projection/state management. RGBA8 fallback flag retained; tested on a weak Windows iGPU *early*.
10. **Resize discipline.** Resize events fire every drag frame (`Program.cs:30-35`). All render-target recreation (`EnsureFxTargets`, bloom mips, light buffer) and scenery rebuilds are **debounced** (rebuild only after size is stable for ~10 frames; scale in place meanwhile).

---

## 5. Phased Roadmap

Effort scale: S ≈ ≤2 days, M ≈ 3–7 days, L ≈ 1.5–3 weeks (solo developer). Impact 1–5 = perceived player value.

---

### Phase 1 — Seams & Instant Juice
**Goal:** Land the three seams everything else plugs into (event bus, time spine, shader composite) plus the cheapest transformative feel work. The judges' consensus: this week delivers ~70% of the perceived feel jump.
**Demo gate:** Side-by-side capture — first intercept and first city loss feel physically different (freeze, shake, flash, recoil, pitch-varied audio); frame shows true chromatic aberration and shader-grade post; crisp text on macOS.

| # | Feature | Cat | Effort | Impact |
|---|---|---|---|---|
| 1.1 | GameEvent ring buffer | architecture | S | 5 |
| 1.2 | Time Director: hit-stop + directed slow-mo | feel | S | 5 |
| 1.3 | Trauma camera + micro-feedback pass | feel | S | 5 |
| 1.4 | GLSL 330 composite uber-shader | graphics | M | 5 |
| 1.5 | Embedded cross-platform font + unified text | graphics | S | 4 |

**1.1 GameEvent ring buffer** — *the feel seam, first commit of the roadmap*
- **Approach:** `struct GameEvent { EventKind Kind; float X, Y, Magnitude; }` in a fixed `GameEvent[256]` ring (head/count ints) on `GameState`. Emit from `Combat.RegKill`/`SpawnExpl`, city destruction, `WaveSystem.StartWave`/clear. **First**: route the five duplicated combo/score blocks (`Combat.cs:498/534/590/634/666`) *and* the two the design missed (`PhalanxSystem.cs:152/176`) through `RegKill`. Drain after `UpdateAll` in `Program.cs` into FeelDirector / SynthAudio / StatsTracker consumers. Find stragglers with `grep "s.Combo++"` and `grep "s.Shake ="` (also hits `DemonSystem.cs:37`, `MothershipSystem.cs:44`, `WeatherSystem.cs:114`).
- **Files:** new `src/Events.cs`; `Combat.cs`, `PhalanxSystem.cs`, `WaveSystem.cs`, `GameUpdate.cs`, `Program.cs`, `GameState.cs`
- **Acceptance:** exactly one `s.Combo++` site remains (RegKill); kill/city/wave events visible in a debug overlay; zero per-frame allocation from the bus (dotnet-counters Gen0 flat).

**1.2 Time Director**
- **Approach:** In `Program.cs:36-37`: `rawDt` (clamp 1/30) vs `simDt` per §4.7. Add `s.HitStop`, `s.TimeScale`, `s.TimeScaleTarget` (cubic ease). Audit the `GameUpdate.cs:13-32` timer block — split sim timers from presentation timers (MsgT/Flash/FloatingTexts run on rawDt). FeelDirector sets values from events: city lost 90 ms freeze, 4+ multi-kill 60 ms, boss phase 120 ms; slow-mo 0.25× on wave-final kill and last-second city saves. Pass `timeScale` into `SynthAudio.Update` (applied after the `_beatStep` recompute at `SynthAudio.cs:58`); bend active voice freqs −20% during slow-mo. SynthAudio always receives rawDt for buffer pumping.
- **Files:** `Program.cs`, `GameState.cs`, `GameUpdate.cs`, new `src/FeelDirector.cs`, `Audio/SynthAudio.cs`
- **Acceptance:** city death freezes ~90 ms while particles/UI keep moving; audio never glitches during hit-stop; wave-final kill plays in eased slow-mo with pitch bend; no sim timer frozen accidentally (manual sweep of timer block).

**1.3 Trauma camera + micro-feedback pass** *(audited corrections applied)*
- **Approach:** Replace linear `s.Shake` with `Trauma∈[0,1]`, amplitude = trauma²; three baked 1D value-noise tables (x/y/roll, different seeds) sampled on an **unscaled clock** (not `s.Time`, which freezes in hit-stop). Roll capped ±1.5°, 16 px overscan (§4.1). Apply at composite via `DrawTexturePro(texture, srcNegHeight, dst, originCenter, rollDeg, tint)` replacing `Rlgl.Translatef` at `Renderer.cs:316-319`. **Move DrawHUD/DrawCrosshair/DrawOverlays after `EndTextureMode`** to the backbuffer; the CA re-draws at `Renderer.cs:3293-3295` are deleted (subsumed by 1.4). Directional kick: spring-damped Vector2 impulse from `Combat.LaunchPlayer`. Micro-feedback: 2-frame additive muzzle flash + 3 px base recoil spring (`Combat.cs:322`), crosshair scale-pop 1.3→1.0 (`Renderer.cs:3159`), `FlashT=0.05` white tint on damaged enemies (`Combat.cs:226`), ammo-pip flash in `DrawBases`, ±5% pitch jitter in `SynthAudio.AddVoice`.
- **Files:** `GameState.cs`, `GameUpdate.cs`, `Rendering/Renderer.cs`, `Combat.cs`, `Audio/SynthAudio.cs`
- **Acceptance:** shake reads as smooth camera motion with roll, no black corners at max trauma; every click answers with flash+recoil+pop within 2 frames; HUD does not rotate with the world; 20 successive hits sound varied.

**1.4 GLSL 330 composite uber-shader** — *the shader seam*
- **Approach:** `Raylib.LoadShaderFromMemory(null, fragSrc)` with embedded `#version 330` string (out vec4 finalColor, `texture()`, raylib default names `fragTexCoord/texture0/colDiffuse`). Wrap the final blit (`Renderer.cs:368-370`) in `BeginShaderMode`. One pass: vignette, scanlines (`fract(uv.y*H/3)`), hash film grain, **true RGB-split chromatic aberration**, subtle barrel distortion, directional screen flash (`mix(color, flashColor, amount*gradient(uv,dir))`), danger desaturation. Uniforms via generic `SetShaderValue<T>` (verified, no unsafe). Delete the CPU scanline/grain/CA blocks (`Renderer.cs:~3252-3315`, ~400 primitive calls). Guard with `IsShaderValid`; CPU path retained as fallback. The blit is the §4.1 shared composite (rotation + overscan from 1.3).
- **Files:** `Rendering/Renderer.cs`, new `Rendering/Shaders.cs` (embedded GLSL strings)
- **Acceptance:** shader path active on macOS GL3.3 and Windows; CA shows actual red/blue fringing; forcing shader-compile failure renders via the old CPU path; ~400 draws gone from the frame.

**1.5 Embedded font + unified text**
- **Approach:** Ship an OFL font (Inter or JetBrains Mono) as an `EmbeddedResource` (AOT-safe); `LoadFontFromMemory(".ttf", bytes, 64, null, 0)` + bilinear filter, existing Windows paths (`Renderer.cs:423-449`) as fallback. Route legacy `Raylib.DrawText` sites (ammo counters `Renderer.cs:1561-1579`, FloatingTexts `3134-3154`) through `DrawTextM`. **No SDF** — `LoadFontEx` has no SDF flag (audit); the unsafe `LoadFontData(FontType.Sdf)` path is deferred polish.
- **Files:** `Rendering/Renderer.cs`, csproj (EmbeddedResource), new `assets/` font
- **Acceptance:** crisp anti-aliased text on macOS at all sizes used; zero legacy bitmap-font call sites remain.

---

### Phase 2 — Light & Sound Substrate
**Goal:** The two heavy engines: GPU bloom + scene refraction on the new shader seam, and the sample-accurate stereo audio engine with filters and panning. Explosions become the product.
**Demo gate:** One nuke at night: blooms softly, bends the screen, shimmers heat — and lands in stereo with a filtered rumble instead of white-noise hiss. Frame-hitch test produces no audio crackle.

| # | Feature | Cat | Effort | Impact |
|---|---|---|---|---|
| 2.1 | GPU threshold bloom (dual-Kawase) | graphics | M | 5 |
| 2.2 | Shockwave refraction, heat shimmer, EMP ripple | graphics | M | 5 |
| 2.3 | Sample-accurate stereo audio engine core | audio | L | 5 |
| 2.4 | Per-voice SVF filter sweeps | audio | S | 5 |
| 2.5 | Stereo staging + constant-power panning | audio | S | 5 |
| 2.6 | Hot-path allocation purge | performance | S | 3 |

**2.1 GPU threshold bloom**
- **Approach:** Soft-knee bright-pass shader on `_frameTarget` → 4-mip chain (W/2…W/16, `SetTextureFilter` Bilinear) → dual-Kawase down/up (4-tap / 8-tap shaders) → additive composite using `BlendMode.CustomSeparate` + `Rlgl.SetBlendFactorsSeparate(SRC_ALPHA, ONE, ONE, ONE_MINUS_SRC_ALPHA, FUNC_ADD, FUNC_ADD)` to avoid alpha pollution. **Delete `RenderBloomPass` (`Renderer.cs:169-205`) and the CPU blur entirely** — this kills the RNG-replay landmine at the root. Explicit negative-height source rects per pass (don't rely on even-pass-count flip cancellation). RGBA8 for now; mips swap to FP16 when 5.1 lands.
- **Files:** `Rendering/Renderer.cs`, `Rendering/Shaders.cs`
- **Acceptance:** lightning, tracers, neon city trims bloom automatically with soft falloff; editing city art cannot desync bloom (the replaying consumer no longer exists); bloom chain <1 ms.

**2.2 Shockwave refraction + heat shimmer + EMP ripple**
- **Approach:** Copy live `Shockwaves` (struct, `Particles.cs:37` — strength derived from `Life/MaxLife`) into a `stackalloc Vector4[16]` each frame, upload via `SetShaderValueV` (Span overload, allocation-free; location of `"shockwaves[0]"`). In the uber-shader, **before** bloom/light sampling: radial UV displacement at the ring front (`smoothstep` band × strength × (1−age)); fbm-scrolled vertical shimmer masked above explosion centers; EMP = full-screen radial wave with chroma split at the front. Oldest-first eviction beyond 16. UVs clamped to overscan region.
- **Files:** `Rendering/Renderer.cs`, `Rendering/Shaders.cs`, `GameState.cs`
- **Acceptance:** every blast visibly bends the world; EMP reads as a screen-consuming event; no edge streaking; no visual pop at the 16-wave cap.

**2.3 Sample-accurate stereo audio engine core** *(audited corrections applied)*
- **Approach:** `LoadAudioStream(44100,16,2)`; `SetAudioStreamCallback` with a static `[UnmanagedCallersOnly]` method `(void* buf, uint frames)` writing `frames*2` interleaved shorts (the stream's native 16-bit format). Fixed `Voice[64]` struct pool (zero GC), priority stealing (lowest-priority-then-quietest, 3 ms fade) replacing FIFO `RemoveAt(0)`. Per-voice phase accumulators (`phase += freq/SR`, wrap via `-= floor`), sample-counted **exponential ADSR** (salvaged from the cut oscillator feature — decay shape is felt punch). Lock-free SPSC `VoiceCmd` ring from the game thread (replaces `lock(_voices)` at `SynthAudio.cs:153`); inline xorshift32 noise (replaces `Random.Shared` at line 168). `SetAudioStreamBufferSizeDefault` headroom **before** load; init-time polled-fill fallback mode (callback and polling are mutually exclusive per stream); top-level try/catch in the callback writing silence. Smoothed master/mute gains (one-pole ~5 ms).
- **Files:** `Audio/SynthAudio.cs` (substantial rewrite), `Program.cs` (init flag)
- **Acceptance:** sine-sweep test shows no buffer-boundary discontinuities; deliberately stalled frames (sleep injection) produce zero crackle; dotnet-counters shows zero allocation from the audio path; verified on macOS and Windows.

**2.4 Per-voice SVF filter sweeps**
- **Approach:** Chamberlin SVF per voice (~8 lines: `low+=f*band; high=in-low-q*band; band+=f*high`), modes LP/BP/HP, `f<1` clamp (§4.8). Repoint the **already-designed, currently discarded** noise sweeps: Hit 3500→600 (`SynthAudio.cs:236`), Impact 1800→160 (`:246`), NearMiss 1700→480 (`:310`) become cutoff sweeps in log space. Thunder gets dark LP.
- **Files:** `Audio/SynthAudio.cs`
- **Acceptance:** Hit cracks, Impact rumbles, NearMiss whooshes — audibly distinct; no self-oscillation at sweep extremes.

**2.5 Stereo staging + panning**
- **Approach:** `Voice.Pan` + constant-power gains (`gL=cos(p·π/2)`, `gR=sin`), smoothed per-sample. Wire the six call sites that already compute and pass pan: `Combat.cs:223/256/318/394`, `WaveSystem.cs:205`, `HellRaiserSystem.cs:207`. Per-turret Phalanx voices panned to turret X (replacing the averaged `FireMix` at `SynthAudio.cs:61-70`). Sub frequencies summed mono; drone gets ±0.3 Hz detuned L/R partners for width. Hard-pan ceiling ~80%.
- **Files:** `Audio/SynthAudio.cs`, `PhalanxSystem.cs`
- **Acceptance:** left-screen explosion is audibly left on headphones; dual Phalanx fire splits the stereo field; sub stays centered; no clicks on pan changes.

**2.6 Hot-path allocation purge**
- **Approach:** Cached `aliveCities/aliveBases` ints updated on destroy events (deletes the resupply LINQ at `GameUpdate.cs:48-67`); fixed ring-buffer trails with head index (deletes `Trail.Insert(0)` memmoves at `GameUpdate.cs:227/341`); direct object references for HellRaiser homing targets (deletes closures + string compares at `GameUpdate.cs:363-398`); index-iterate explosions instead of `ToArray` (`Combat.cs:553`); `HitBy` as int-ID set; debounced resize scenery rebuild (§4.10, `Program.cs:30-35`).
- **Files:** `GameUpdate.cs`, `Combat.cs`, `Entities/Enemy.cs`, `Entities/Explosion.cs`, `Program.cs`, `GameInit.cs`
- **Acceptance:** dotnet-counters shows ~0 Gen0 allocs/frame in steady play; window drag no longer flickers/re-randomizes the sky.

---

### Phase 3 — Game Flow, the Mix & Persistence
**Goal:** The structural floor of a premium game: pause/settings, a single persistent profile with seeded runs, an honest economy, and a master bus that makes the game sound *mixed*.
**Demo gate:** ESC pauses to a real settings menu; quit and relaunch shows your initials on a saved top-10; same seed twice produces identical waves; explosions duck the soundscape and thump on laptop speakers; kills shed scrap that the shop actually spends.

| # | Feature | Cat | Effort | Impact |
|---|---|---|---|---|
| 3.1 | GamePhase FSM + pause/settings menu (+ accessibility-lite) | feel | M | 4 |
| 3.2 | Unified persistent profile + seeded runs + initials | gameplay | M | 5 |
| 3.3 | Four-bus mixer: reverb, compressor, tanh limiter | audio | M | 5 |
| 3.4 | Sidechain ducking + saturated sub-bass | audio | S | 4 |
| 3.5 | Scrap economy + deterministic repairs | gameplay | S | 4 |

**3.1 GamePhase FSM + pause/settings**
- **Approach:** `enum GamePhase { Title, Playing, Shop, Paused, Ceremony, GameOver }` replacing the bools at `GameState.cs:18-22` (+`Shop` at :89); switch in `Program.cs` input and `GameUpdate.UpdateAll` (~35 guard sites). ESC (free since `SetExitKey(Null)` at `Program.cs:12`) → pause menu: volume, shake/flash-reduction sliders, UI scale, theme, fullscreen via `ToggleBorderlessWindowed()` (not `ToggleFullscreen` — macOS Retina issues), restart, quit. **Accessibility-lite ships here:** flash-reduction swaps full-screen white for an edge vignette; assist toggles (slower enemies, auto-EMP at last city) flag runs as Assisted. *(Colorblind palette work is NOT here — it rides ThemePalette in 5.4, per §4.4.)* `SynthAudio.Update` keeps pumping while Paused (§4.6); paused render = keep drawing the frozen state, dimmed.
- **Files:** `GameState.cs`, `Program.cs`, `GameUpdate.cs`, `Rendering/Renderer.cs`, new `src/Menu.cs`
- **Acceptance:** ESC pause/resume with zero audio pop; all settings live-apply and persist (via 3.2); grep shows no `s.Intro`/`s.Shop` bool reads outside the FSM.

**3.2 Unified persistent profile + seeded runs** *(merged feature: gameplay profile absorbs feel's initials table; audited corrections applied)*
- **Approach:** xoshiro256\*\* master seed on `GameState` (sealed class or accessed only as field — a struct copy silently forks the stream), with **stream-split** per §4.3: `BuildPlan` and wave mutations draw from `new Xoshiro(seed ^ waveIndex)`; cosmetics free-run; audio untouched. Replace the three RNG sites (`MathHelpers.cs:11`, `RandHelper.cs:5`, `GameInit.cs`) plus the audit's straggler list (`MothershipSystem.cs:17,125,145,167,200`, `Program.cs:~107`). Seed shown on game-over, enterable at start; Daily = FNV-1a of `yyyy-MM-dd` (never `string.GetHashCode` — per-process randomized). Profile record (top-10 `ScoreEntry{Initials,Score,Wave,MaxCombo,Date,Assisted}`, settings, lifetime stats, unlock flags, version field) via **source-generated `JsonSerializerContext`** (§4.2) to `ApplicationData/MissileCommandOverdrive/profile.json` (`Directory.CreateDirectory` first; try/catch with defaults on corrupt). Load at boot, save on game-over + shutdown (`Program.cs:57-59`). Arcade initials entry: three rotating A–Z slots, letter-roll animation, synth tick per step.
- **Files:** new `src/Profile.cs`, new `src/Util/Xoshiro.cs`, `Util/RandHelper.cs`, `Util/MathHelpers.cs`, `GameInit.cs`, `GameState.cs`, `Program.cs`, `WaveSystem.cs`, `MothershipSystem.cs`, `Rendering/Renderer.cs`
- **Acceptance:** same seed → identical wave composition across two runs (automated test); high score + settings survive restart **in a published AOT build**; top-10 finish triggers initials ceremony; daily seed matches across machines on the same date.

**3.3 Four-bus mixer**
- **Approach:** `Voice.Bus` byte (Music/Sfx/Ambient/Ui) + `ReverbSend`; in the callback after voice sum: Freeverb-lite (4 combs ~1116/1188/1277/1356 + 2 allpasses 556/441 per channel, +23-sample right offset for width), one-pole envelope-follower compressor for glue, `tanh(1.5x)` soft-clip replacing the hard clamp at `SynthAudio.cs:177`. Explosions/thunder send wet, UI/music mostly dry. **All buffers preallocated in Init; all `exp/pow` coefficients precomputed** — zero per-sample transcendentals beyond the oscillators.
- **Files:** `Audio/SynthAudio.cs`
- **Acceptance:** city death + thunder + Phalanx stack blooms instead of flat-topping; audible space on big events while UI stays dry; zero callback allocations (verified).

**3.4 Sidechain ducking + sub-bass weight**
- **Approach:** Duck envelope (5 ms attack, 180 ms exp release, precomputed coefficient, depth-capped, concurrent triggers **summed not stacked**) multiplying Music/Ambient gains, fired from kick/Impact/CityDestroyed via the ring. Sub rework at `Impact()` (`SynthAudio.cs:241`): 90→45 Hz sine exp decay through `tanh(3x)` drive (+2nd/3rd harmonics audible on laptop drivers — the current 22 Hz at `:244` is inaudible) + 2 ms click transient; **one retriggered mono sub voice** (stacking phase-cancels). Magnitude tied to the same value driving trauma for AV sync.
- **Files:** `Audio/SynthAudio.cs`
- **Acceptance:** explosions audibly punch through music/ambient; impacts thump on MacBook speakers; dense barrage shows no seasick pumping.

**3.5 Scrap economy + deterministic repairs**
- **Approach:** `s.Scrap` granted in `Combat.RegKill` (`Value/10`) + end-of-wave salvage per intact city/base; gold spark particles magnet-stream to the HUD counter (small homing additions to `Particles.cs` sparks). Shop block (`Program.cs:99-154`) spends Scrap; Score is leaderboard-pure. **Delete the 33/40/58% coin-flip self-repairs** (`WaveSystem.cs:74/84/96` — bases/Phalanx/HellRaiser): repairs become Scrap purchases + one free repair per 3 cleared waves. Retune the Danger-driven adaptive ammo (`GameUpdate.cs:48-67` logic, now cached ints) alongside.
- **Files:** `GameState.cs`, `Combat.cs`, `WaveSystem.cs`, `GameUpdate.cs`, `Program.cs`, `Entities/Particles.cs`, `Rendering/Renderer.cs`
- **Acceptance:** buying upgrades never lowers final score; structures never self-repair by luck; every kill sheds visible scrap that streams to the HUD.

---

### Phase 4 — Run Depth: Director, Behaviors, Drafts
**Goal:** The gameplay heart: authored waves, enemies with readable behaviors, a perk draft that creates build identity, and a scoring loop you can *see and hear*.
**Demo gate:** Two seeded runs produce visibly different builds against recognizably authored waves (formation squads, finale spikes); a carrier killed early denies its drones; combo chains climb in pitch under a live decay ring.

| # | Feature | Cat | Effort | Impact |
|---|---|---|---|---|
| 4.1 | Wave Director: tension envelope + squad templates + intel forecast | gameplay | M | 5 |
| 4.2 | Behavioral enemy roster | gameplay | M | 5 |
| 4.3 | Perk-draft armory (15-perk v1, MIRV as a perk) | gameplay | L | 5 |
| 4.4 | Score odometer + combo ring + pitch ladder | feel | M | 5 |
| 4.5 | Interaction-sound vocabulary + anti-repetition | audio | S | 4 |

**4.1 Wave Director**
- **Approach:** Replace flat sampling in `WaveSystem.BuildPlan` (`:34-51`; hoist the per-pick `.Select().ToList()`) with `record SquadTemplate(string[] Variants, float[] DtOffsets, float LaneSpread, int Cost)` and a threat budget spent across a build/peak/lull/finale envelope. Track `s.Intensity` in `GameUpdate` (recent city hits, inbound count, near-misses, ammo scarcity) — **the §4.5 shared tension signal**. Gate the spawn timer through the 4-phase FSM (stop spawning at peak stress, L4D-style). **Salvage from cut endless mode:** clamp variant speeds at level-18 values inside `VariantStats.Speed`; escalate past that via density/composition only. Old generator behind an A/B flag. Intel forecast: build next plan at shop-open from the seed-derived per-wave stream (§4.3) and **pin the displayed plan to the executed plan object**; render variant icons + threat meter on the shop panel.
- **Files:** `WaveSystem.cs`, `GameUpdate.cs`, `GameState.cs`, `VariantStats.cs`, `Rendering/Renderer.cs`
- **Acceptance:** waves show recognizable arcs ending in finales; formations (V-fasts, escorted heavy) readable on screen; forecast always matches the wave that arrives; A/B flag flips back to legacy generation.

**4.2 Behavioral enemy roster**
- **Approach:** Wire the dead intent: carrier spawns 2–3 drone children via `Combat.CreateEnemyProjectile` (`Combat.cs:11`) at `Progress >= _DeployAt` (`Combat.cs:83` — assigned, never read) while flying on; early kill denies the spawn. Heavy MIRV-splits below half altitude reusing the existing `SplitAt`/`SplitMissile` machinery (`Combat.cs:70/427`). Stealth decloak-pings inside `LightBursts` (`GameState.cs:61`). One new variant: Shield Drone (bubble radius checked in `Combat.RunCollisions` explosion overlap). **Every behavior telegraphs** (glow + synth ping ≥0.5 s before). Per §4.4: introduce a `VariantDef` lookup keyed by string *incrementally* — no big-bang enum sweep of ~140 literals in one commit.
- **Files:** `GameUpdate.cs` (UpdEnemies behavior switch), `Combat.cs`, `VariantStats.cs` (→ VariantDef table), `Rendering/Renderer.cs`, `Audio/SynthAudio.cs`
- **Acceptance:** carrier kill before deploy point spawns nothing; heavy visibly splits into 3 warheads; each behavior telegraphed audio-visually; playtest confirms behaviors read as intent, not RNG jank.

**4.3 Perk-draft armory**
- **Approach:** `record Perk(string Id, Rarity Rarity, string Name, string Desc, Func<GameState,bool> CanOffer, Action<GameState> Apply)` (delegates are AOT-safe) in new `src/Perks.cs`; 15-perk v1 pool, Common/Rare/Epic weights, Scrap reroll. Effects read from **one central `PerkFlags` struct** checked at fixed hooks (`Combat.SpawnExpl`/`RegKill`, `PhalanxSystem`, `GameUpdate`) — no delegate dispatch in hot paths, no hook sprawl. **MIRV interceptor ships as a perk** (per the scope judge): splits at `Progress~0.5` into 3–5 homing children reusing the `Hr*` fields already on `PlayerMissile` (`Enemy.cs:40-72`), shorter trail caps on children. Draft UI: three cards with rarity colors, keys 1–3 + R reroll (audit note: avoid double-handling with the `GetCharPressed` secret-code buffer). Replaces the fixed shop items; repairs (3.5) sit beside the draft.
- **Files:** new `src/Perks.cs`, `GameState.cs`, `Combat.cs`, `PhalanxSystem.cs`, `GameUpdate.cs`, `Program.cs`, `Rendering/Renderer.cs`
- **Acceptance:** two consecutive runs produce different active-perk sets; MIRV perk visibly splits into homing children; draft + reroll fully playable; all perk effects route through PerkFlags (grep: no perk conditionals outside the defined hooks).

**4.4 Score odometer + combo ring**
- **Approach:** `DisplayScore` float lerping toward `s.Score` (rate ∝ gap); per-digit vertical roll via `BeginScissorMode` + `DrawTextM` (~14 batch flushes/frame, negligible; **cache digit strings** — HUD already churns interpolated strings). Combo widget near crosshair: `DrawRing` arc = `ComboTimer/4`, squash-stretch pop per kill (ease-out-back), white→gold→red ramp, tremble when <1 s, faded out at combo 0. Semitone ladder in `SynthAudio.Hit`: `freq *= 2^(min(combo,24)/12)`.
- **Files:** `Rendering/Renderer.cs`, `Audio/SynthAudio.cs`, `GameState.cs`
- **Acceptance:** big bonuses visibly roll up; combo expiry readable at a glance from the ring; kill chains audibly climb in pitch and cap before dog-whistle range.

**4.5 Interaction-sound vocabulary + anti-repetition**
- **Approach:** New Ui-bus events: crosshair fire click, shop open/close whoosh, purchase arp, denied buzz, EMP-ready ping (`Combat.cs:212-217` charge grant), rate-limited low-ammo geiger, wave-banner stab, combo-break pitch drop. In `AddVoice`: ±4% freq / ±10% volume jitter via xorshift; 2–3 micro-variants for Hit/Impact (transient tick, varied decay).
- **Files:** `Audio/SynthAudio.cs`, `Program.cs`, `Combat.cs`
- **Acceptance:** every interaction answers audibly; 20 recorded hits sound organic, not stamped; low-ammo tick provably rate-capped.

---

### Phase 5 — Spectacle: HDR, Light & Identity
**Goal:** The premium-screenshot pass: true HDR with filmic tonemap, explosions that *light the world*, layered destruction with permanence, and three themes that finally look like three games — plus machines that sing their simulated state.
**Demo gate:** Night-wave screenshot: triple-overlapped blast shows graded warm core (not clipped white), orange light washing the skyline, smoke and debris history on the ground; theme switch produces three visibly distinct art directions; Phalanx spin-up is audible before it fires.

| # | Feature | Cat | Effort | Impact |
|---|---|---|---|---|
| 5.1 | HDR scene target + ACES tonemap | graphics | L | 5 |
| 5.2 | Dynamic 2D light buffer | graphics | M | 5 |
| 5.3 | Explosion anatomy + permanent destruction | graphics | M | 4 |
| 5.4 | Theme identity: ThemePalette + grading + CRT (+ colorblind set) | graphics | M | 4 |
| 5.5 | Mechanical identity audio | audio | M | 4 |

**5.1 HDR scene target + ACES** *(audited amendment — partial→feasible)*
- **Approach:** Per §4.9: `Rlgl.LoadFramebuffer()` + `Rlgl.LoadTexture(null, w, h, PixelFormat.UncompressedR16G16B16A16, 1)` + `FramebufferAttach(ColorChannel0, Texture2D)` + `FramebufferComplete` check, then **wrap in a hand-assembled `RenderTexture2D` struct and use stock `BeginTextureMode/EndTextureMode`** — no custom viewport/projection management, no missing `SetFramebufferWidth` problem. ACES fit in the uber-shader after light/refraction. Bloom mips (2.1) upgrade to FP16 so the threshold sees true >1.0 energy. RGBA8 fallback flag if `FramebufferComplete` fails or perf demands; **test on a weak Windows iGPU at the start of this phase, not the end** (FP16 doubles ROP/bandwidth under 57 verified additive call sites).
- **Files:** `Rendering/Renderer.cs` (`EnsureFxTargets`, scene pass), `Rendering/Shaders.cs`
- **Acceptance:** overlapping explosions show graded warm highlights instead of white clipping; fallback flag renders the Phase 2 baseline identically; 60 fps held on the Windows iGPU test machine.

**5.2 Dynamic 2D light buffer**
- **Approach:** `_lightTarget` at W/4; after the entity pass, write tinted radial gradients (existing `DrawGradientCircle`) additively for explosions, muzzle flashes, lightning trunks, the moon (positions already computed at `Renderer.cs:138-167`). Bind via `SetShaderValueTexture` as a second sampler (raylib batch supports 4 extra slots; rebind per frame). Composite: `scene.rgb * (ambient + light) + light * spill`, luminance-masked to avoid double-lighting bright FX, ambient floor keyed to day/night (`SkyCycle(s.Time)`). Sampled **after** refraction displacement so refracted pixels light correctly.
- **Files:** `Rendering/Renderer.cs`, `Rendering/Shaders.cs`
- **Acceptance:** night intercepts strobe orange across skyline/mountains/cloud undersides; daytime not washed out; light pass <0.5 ms.

**5.3 Explosion anatomy + permanent destruction**
- **Approach:** Startup soft-sprite atlas via `GenImageGradientRadial` (3–4 falloff variants; replaces ~55 layered-ring approximations). Layered recipe per blast: 1-frame white flash quad → fast high-drag white sparks (additive) → gravity embers → **dark alpha-blended smoke** (not additive — currently everything washes out) → lingering ground glow. Blend-class byte on `Particles.cs` structs; render grouped per blend mode (each `BeginBlendMode` flushes the batch). Permanence: city-palette debris chunks (`DrawRectanglePro`) fall, bounce once, persist as ground litter; scorch accumulation per wave. Pre-`Capacity` lists, hard pool caps, smoke max-alpha clamp.
- **Files:** `Entities/Particles.cs`, `Combat.cs`, `GameUpdate.cs`, `Rendering/Renderer.cs`, `GameState.cs`
- **Acceptance:** a blast reads as flash/sparks/smoke composition in slow-mo capture; wave-20 battlefield shows scars, litter, smoke columns telling the run's story; particle Gen0 churn ≈ 0; readability holds at 200 entities (smoke caps verified in dense waves).

**5.4 Theme identity: ThemePalette + grading + CRT**
- **Approach:** `ThemePalette` struct (sky stops, ground, grid, city hue, bloom tint, lift/gamma/gain) with three instances — **the single color authority (§4.4)**. Mechanical, default-to-current-values sweep of `DrawSky/DrawGround/DrawCityAlive/DrawMountains` color literals so the diff is reviewable. Per-theme analytic grade (6 vec3 uniforms + theme int) in the uber-shader: Modern = teal-shadow neo-noir, Xbox = warm green-phosphor + Lottes-derived CRT branch (public domain; hardScan/mask/warp), Recharged = crushed-black neon. State modifier: red-shift desaturation at one city left. **The colorblind-safe trail/explosion variant set rides this same sweep** (settings toggle from 3.1) — done once, here, not in a parallel feel refactor.
- **Files:** new `Rendering/ThemePalette.cs`, `Rendering/Renderer.cs`, `Rendering/Shaders.cs`, `Rendering/Palette.cs`
- **Acceptance:** three themes screenshot as three distinct games; Modern with grading disabled is diff-identical to pre-sweep; colorblind mode selectable and persisted.

**5.5 Mechanical identity audio**
- **Approach:** Loop-voice handles through the command ring with a **watchdog auto-fade** (0.25 s without a param update → fade out; covers entity death without note-off). Phalanx: polyBLEP-ish saw at `80 + SpinSpeed*14` Hz through SVF bandpass keyed to the already-simulated `SpinSpeed` (1.5–28 rad/s, `PhalanxSystem.cs:27-30`) + noise rattle gated by a 24 Hz LFO × FireMix. HellRaiser: filtered-noise hydraulics ∝ `DoorOpen/Lift` deltas (`HellRaiserSystem.cs:33-67` state machine), 140 Hz servo during Lift. Mothership `ShieldActive` (audit: correct field name) hum drops an octave when the shield falls. Param updates batched per frame.
- **Files:** `Audio/SynthAudio.cs`, `PhalanxSystem.cs`, `HellRaiserSystem.cs`, `MothershipSystem.cs`
- **Acceptance:** Phalanx whine tracks visible barrel spin-up/down; HellRaiser doors hiss in sync with animation; no orphaned loop voices after entity death (watchdog test).

---

### Phase 6 — Bosses, Ceremony & the Score
**Goal:** The run gains milestones and the game gains a voice: real multi-phase bosses every 5th wave, the full arcade ceremony around every run, and the procedural music director that turns tension into score.
**Demo gate:** A full run: living title screen → attract demo → authored waves with stingers → wave-5 boss with destructible shield pods → death ceremony with letter grade → initials entry → back to title. Music audibly builds and breaks with the Wave Director's envelope for a full 30-minute session.

| # | Feature | Cat | Effort | Impact |
|---|---|---|---|---|
| 6.1 | Multi-phase boss framework (every 5th wave) | gameplay | L | 5 |
| 6.2 | Wave stingers + report card | feel | M | 4 |
| 6.3 | End-of-run ceremony + letter grade | feel | M | 4 |
| 6.4 | Title screen + attract mode | feel | M | 4 |
| 6.5 | Procedural music director | audio | L | 5 |

**6.1 Multi-phase boss framework**
- **Approach:** New `src/Bosses/BossBase.cs` (Hp/MaxHp, ellipse hitbox, `List<BossPhase>` with HP thresholds + telegraph timers); `GameState.Bosses` list; **one generic damage loop replaces the three copy-pasted blocks at `Combat.cs:545-679`**. De-risk order: refactor **Mothership first** — destructible shield-generator pods (two glowing ellipse hitboxes at hull offsets) replace the unreadable RNG timer at `MothershipSystem.cs:59-69`; turbolaser volleys telegraphed by 0.5 s tracer lines. Then **Daemon rework** (`DemonSystem.cs`): phase 1 rune-telegraphed meteor impacts (1.2 s warning, interceptable), phase 2 (≤half HP) summons hell-variant fans, phase 3 firewall sweep; add `MaxHp` (kills the duplicated magic 6 in the renderer). Schedule at `Level % 5 == 0` in `WaveSystem.StartWave` reusing `HoldSpawning` (`MothershipSystem.cs:111`, consumed at `GameUpdate.cs:74`) for the cinematic pause. Boss kill → guaranteed Epic perk (4.3) + BossPhase events into hit-stop/trauma/music.
- **Files:** new `src/Bosses/`, `MothershipSystem.cs`, `DemonSystem.cs`, `Combat.cs`, `WaveSystem.cs`, `GameState.cs`, `Rendering/Renderer.cs`
- **Acceptance:** waves 5/10/15 are scheduled boss encounters with intro banners; Mothership shield drops only when both pods die; every Daemon attack telegraphed ≥1.2 s; exactly one boss damage loop exists in Combat; easter-egg codes still work as instant summons.

**6.2 Wave stingers + report card**
- **Approach:** Wave intro rides the existing 2.9 s `s.WavePause` (`WaveSystem.cs:109`): letterbox bars ease in, typewriter "WAVE 7 — STORM FRONT" with per-char synth tick, threat icons from the already-built `WavePlan` — **any input collapses it**. Outro: WaveCleared event → 0.25× slow-mo on the final kill (Time Director) → CLEARED stamp (scale 3→1 ease-out-back) → count-up tallies (accuracy, saves, salvage) from a new `struct WaveStats` fed by the event bus (**replaces the boxing `Dictionary<string,object>` telemetry at `GameState.cs:177`** — also the AOT-friendly move), flowing into the shop screen.
- **Files:** `Rendering/Renderer.cs`, `GameState.cs`, `WaveSystem.cs`, `GameUpdate.cs`
- **Acceptance:** every wave opens/closes with skippable ceremony; report card shows accuracy/saves/salvage; no `Dictionary<string,object>` telemetry remains.

**6.3 End-of-run ceremony + letter grade**
- **Approach:** `Ceremony` phase (FSM): 120 ms freeze → slow fade → staged stat reveals every 0.6 s with count-up ticks (reuse odometer code; cache strings per stage, not per frame) → S/A/B/C grade = f(accuracy × waves × cities standing) stamped with Trauma 0.3 pulse → initials entry if top-10 (3.2) → Retry/Title. World keeps burning behind at TimeScale 0.3 (the sim already updates during GameOver, `GameUpdate.cs:34-40`). Every stage skippable to a summary.
- **Files:** `Rendering/Renderer.cs`, `Program.cs`, `GameState.cs`, `src/FeelDirector.cs`
- **Acceptance:** death leads to a graded, skippable ceremony instead of two text lines (`Renderer.cs:3489-3506`); grade thresholds verified across good/bad runs; full flow to initials and back to title with no input traps.

**6.4 Title screen + attract mode**
- **Approach:** `Title` phase runs real scenery + a low-intensity seeded wave with `s.Auto=true`, HUD hidden, behind a bloom-fed layered logo; menu START/SCORES/SETTINGS/QUIT (reuses 3.1 menu rows) + top-10 marquee. 15 s idle → attract demo (auto-defense plays a competent seeded wave, "DEMO — PRESS ANY KEY" flashing) → title. Generalize `DemoDriver.cs`'s 70-line `Step[]` machinery into the attract script — **strip its screenshot/exit/log behavior** (it's currently an env-var eval harness). Requires the FSM's phase-routed updates (currently `Program.cs:45` skips `UpdateAll` wholesale during Intro).
- **Files:** `DemoDriver.cs` (generalized), `Program.cs`, `GameState.cs`, `Rendering/Renderer.cs`, `src/Menu.cs`
- **Acceptance:** idle title transitions to a competent self-playing demo and back; any input returns to menu; the first frame a new player sees is the new game, not a text overlay.

**6.5 Procedural music director** *(the identity bet — engineering is proven; quality is ear-time)*
- **Approach:** Replace the lone kick (`SynthAudio.cs:74-83`) with a 16-step sequencer advanced **sample-accurately inside the callback** (`stepSamples = beatStep*SR/4`; voices spawned at exact sample offsets). Stems gated by intensity: kick/hat skeleton always; minor-pentatonic sub-bass roots keyed to wave number; arp ≥0.5; lead stabs ≥0.8; boss stems (tritone ostinato Daemon, low brass pulse Mothership) keyed to boss state; Title/Shop strip to pad. **Intensity = the Wave Director's `s.Intensity` (§4.5)** — one tension story. Params cross threads via the command ring or a double-buffered seqlock (**not** bare struct writes — `Volatile` doesn't cover multi-field structs). Per-wave pattern/key rotation against 30-minute fatigue; music slider already shipped (3.1). Sidechain (3.4) ducks stems under impacts. **Budget explicit listening/tuning sessions — this feature's risk is compositional, not technical.**
- **Files:** `Audio/SynthAudio.cs`, `GameUpdate.cs` (intensity plumb), `WaveSystem.cs`
- **Acceptance:** music audibly builds toward wave finales and strips in lulls; boss arrivals change the score; a 30-minute session passes a listenability check (no grating loop); beats land sample-exact (no frame-quantized jitter); full mute/volume changes pop-free.

---

## 6. Performance Budget

Targets on the primary machine (Apple Silicon) and a low-end Windows Intel iGPU at 1280×720.

| Budget item | Target | Enforcement |
|---|---|---|
| Frame total | ≤16.6 ms (60 fps floor; high-refresh unlock is post-plan) | per-phase capture at fixed `__sim`-style demo states |
| Sim (UpdateAll) | ≤4 ms at 200 entities | dotnet-trace before/after Phases 2, 4 |
| Render CPU | ≤8 ms | batch-flush count tracked (blend toggles grouped per 5.3) |
| GPU post chain (uber-shader + bloom + light + refraction) | ≤2.5 ms total; bloom <1 ms, light <0.5 ms | `Rlgl.CheckErrors` + frame timing at phase gates |
| FP16 HDR overhead | ≤2 ms over RGBA8 on Intel iGPU, else fallback flag ships ON for that tier | tested at *start* of Phase 5 |
| Audio callback | ≤0.3 ms per buffer on the audio thread; **zero allocations, zero locks** | dotnet-counters during soak test |
| Steady-state GC | ~0 Gen0 allocs/frame in play (Phase 2.6 gate); no Gen0 spikes during barrages | dotnet-counters gen0 rate |
| Render-target memory | HDR scene + 4 bloom mips ×2 + light buffer ≈ <40 MB at 1080p | inventory in `EnsureFxTargets`; debounced recreation (§4.10) |
| Audio DSP CPU | ≤3% of one core (64 voices + Freeverb + sequencer) | profile during Phase 3/6 |

Known headroom facts from the audit: current build holds 57–59 fps under max particle load; the post chain *removes* ~400 CPU primitive calls (1.4) and ~1000 more if the sky shader is ever revived; the audio rewrite *reduces* GC pressure versus today's per-event class allocations.

---

## 7. Risk Register

| # | Risk | Phase | Likelihood | Severity | Mitigation |
|---|---|---|---|---|---|
| R1 | .NET GC stop-the-world suspends the managed audio callback → dropout | 2 | Med | High | `SetAudioStreamBufferSizeDefault` headroom; init-time polled-fill fallback mode; near-zero steady-state allocation (2.6) shrinks pauses; Windows soak test |
| R2 | `PublishAot=true` breaks reflection JSON in published builds | 3 | Certain if ignored | High | Source-generated `JsonSerializerContext` only (§4.2); CI check: publish + load profile |
| R3 | Manual FP16 FBO assembly (least-trodden binding surface) misbehaves; FP16 bandwidth tanks Intel iGPUs | 5 | Med | Med | Hand-assembled `RenderTexture2D` + stock BeginTextureMode (§4.9); `FramebufferComplete` + `Rlgl.CheckErrors`; RGBA8 fallback flag; Windows iGPU test first thing in Phase 5 |
| R4 | Stray RNG call sites silently break seed determinism | 3+ | Med | Med | Stream-split design (§4.3); grep audit incl. the audit's straggler list; automated same-seed wave-composition test; replay determinism explicitly out of scope |
| R5 | Procedural music is technically perfect but grating over 30 minutes | 6 | Med | Med | Sparse-by-default writing, per-wave pattern/key rotation, music slider, ducking under SFX; budget dedicated ear-time; game ships fine with stems muted-down if needed |
| R6 | Parallel color refactors collide (ThemePalette vs colorblind vs VariantDef) in the 3,608-line renderer | 5 | High if uncoordinated | Med | §4.4 one color authority: ThemePalette sweep done once, default-to-current values, colorblind rides it; VariantDef incremental |
| R7 | Composite blit contention: trauma rotation, overscan, refraction, HUD placement all touch one seam | 1 | Med | Med | §4.1 co-designed blit in Phase 1; HUD to backbuffer; roll capped ±1.5°/16 px v1; CA double-draw deleted |
| R8 | Wave Director budget mistuning creates unfair spikes; perk hooks sprawl across systems | 4 | Med | Med | Legacy generator behind A/B flag; telegraping mandate for every behavior; single PerkFlags struct at fixed hooks; 15-perk pool before growth |

---

## 8. Cut / Deferred (judge-consensus, with reasons)

| Item | Verdict | Reason |
|---|---|---|
| GPU-instanced particle fields (50–100k) | **Cut** | All three judges. The headline numbers were rejected by audit: raylib 5.5 `DrawMeshInstanced` mallocs + transposes + creates/destroys a fresh VBO every call (~6.4 MB/frame at 100k). Riskiest binding surface in the program for "denser rain." Light buffer + explosion anatomy deliver the felt atmosphere. |
| Screen-space god rays | **Cut** | All three judges. Impact-3 wallpaper; depends on cut baked-city occluders; player tunes it out by wave 3. |
| Procedural sky shader | **Cut** | Player judge. Upgrades the layer players stop seeing fastest; the resize flicker it fixed is fixed cheaper by debouncing (2.6). Revisit post-plan if desired. |
| Baked city skylines + emissive masks | **Cut** | All three judges. Its strongest justification (bloom RNG-replay) is mooted by 2.1 deleting the replaying consumer. Damage-state visuals fold into a future city-HP feature. |
| Meta-progression: doctrines + unlocks | **Deferred post-ship** | All three judges. Gates content (perks/weapons/bosses) the roadmap is still building; the unified profile already provides death-persistence. |
| Gamepad twin-stick + magnetized crosshair | **Deferred** | Player + tech judges. Zero value to the existing mouse player; `SetGamepadVibration` is a verified no-op stub on the shipped GLFW natives; needs unbudgeted virtual-cursor arbitration. Post-step-change port pass. |
| Living world ambience (rain/wind beds) | **Deferred** | Player + scope judges; lowest self-rated impact in its lens; risks masking the threat-tracking audio. Thunder distance filtering rides the mixer for free. |
| polyBLEP/FM oscillator upgrade | **Partial salvage** | Exponential ADSR shipped inside 2.3 (felt punch); anti-aliased oscillators + FM patches are connoisseur polish deferred behind the music director's ear-time. |
| Endless mode + wave mutators | **Partial salvage** | Tech judge: the "level-30 wall" premise was a misread (interceptor boost caps at 2.7× ~level 78). The one-line variant speed clamp ships inside 4.1; the mutator framework is deferred. |
| Weapon expansion (railgun, mines, MissileKind enum) | **Partial salvage** | Scope judge: MIRV ships as a perk (4.3) on the existing homing fields; railgun/mines deferred so the perk draft owns build identity v1. |
| In-wave drop pods / convoys / overcharge | **Deferred** | Scope judge: three mechanics with a hard dependency on director lull phases. Revisit a pods-only version one milestone after 4.1 proves its envelope. |
| City HP + population stakes | **Deferred (partial)** | No judge must-keep. The stakes-critical half (deterministic repairs) ships in 3.5; HP/damage-state visuals revisit post-plan together with the baked-city damage art. |
| SDF font rendering | **Deferred** | `LoadFontEx` has no SDF flag; the unsafe `LoadFontData(FontType.Sdf)` path is polish once `LoadFontFromMemory` (1.5) ships the 90% win. |

---

## 9. Dependency Spine (why this order)

```
P1  GameEvent bus ─┬─► every consumer (audio, feel, stats, ceremony, music)
    Time Director ─┼─► wave outros, ceremony, boss phases, slow-mo saves
    Composite blit ┴─► bloom (P2) ► refraction (P2) ► HDR (P5) ► light (P5) ► grading (P5)
P2  Audio engine core ─► SVF/stereo (P2) ► mixer (P3) ► ducking/sub (P3) ► mech-id (P5) ► music (P6)
P3  FSM ─► pause/settings ► title/attract (P6), ceremony (P6)
    Profile+seeds ─► initials (P3), forecast pinning (P4), daily seeds
    Scrap ─► perk draft (P4), repairs
P4  Wave Director (s.Intensity) ─► music director (P6); squads ◄─ behaviors
    Perk draft ◄─ scrap; boss Epic rewards (P6) ─► perk pool
P5  HDR ─► light buffer payoff; ThemePalette ─► grading + colorblind (one sweep)
P6  Bosses ◄─ event bus + Time Director + perk draft; ceremony ◄─ FSM + profile + stats
```

Every phase ends with a capture-verified demo gate (use a fixed-seed demo state for before/after comparison). If the schedule slips, the game is still strictly better at the end of whatever phase shipped last — that is the point of the ordering.

---

## 10. Appendix — Live Evaluation (2026-06-11, Apple Silicon M5, macOS, 1280×720)

The game was built (0 warnings) and play-tested via a scripted self-driving session: a new env-gated harness (`src/DemoDriver.cs`, `MCOD_DEMO=1`) starts a run with auto-defense, deploys the HellRaiser, fires an EMP, cycles all three themes, summons both bosses, jumps to wave 8, and captures 12 screenshots (`demo_*.png`) plus frame stats (`demo_log.txt`) using the game's own `TakeScreenshot` — no OS permissions required. Phase 6.4 generalizes this harness into the attract mode.

### Function: PASS
- Clean boot/shutdown, zero errors or warnings over the full session; clean GPU/audio teardown.
- 57–59 fps held at every sample point, including 37 simultaneous explosions + 655 sparks (worst observed).
- All systems triggered correctly: HellRaiser barrage, EMP, theme cycling, 666/777 summons, wave skip, debug overlay.
- Auto-defense AI is *competent to a fault*: 6/6 cities alive at wave 8, threat never above 10%, Mothership and Daemon both killed without player input (score 0 → 41,245 in 48 s).

### Quality: confirms the §3.2 diagnosis on sight
| Observation (screenshot) | Plan item |
|---|---|
| EMP/explosions read as clusters of hard-edged circles (`demo_04`) | 2.1 bloom, 2.2 refraction, 5.3 explosion anatomy |
| Theme switch changes little beyond HUD border color — xbox vs recharged near-identical in-world (`demo_05/06`) | 5.4 theme identity |
| Star Destroyer is a faint smudge clipped at the screen edge; no intro, no presence (`demo_07`) | 6.1 boss framework |
| Daemon is a tiny sprite in a corner (`demo_09`) | 6.1 boss framework |
| "Wave 8 chaos" is a near-empty sky — flat spawn cadence + auto-AI trivializes (`demo_10`) | 4.1 Wave Director, 4.2 behaviors |
| Title screen is plain text on near-black (`demo_01`) | 6.4 title + attract |
| Scene is dark with large dead sky; nothing lights the world (`demo_02/03`) | 5.1 HDR, 5.2 light buffer |
| 57–59 fps *under the 60 cap* even in sparse scenes — CPU-draw-bound, no headroom | 1.4/2.1 shader pipeline, 2.6 allocation purge |

The strongest visual in the session was the HellRaiser's homing interceptor swarm arcing across the sky (`demo_05/07`) — evidence that motion + trails are already the game's best asset, which is exactly what Phases 1–2 amplify.
