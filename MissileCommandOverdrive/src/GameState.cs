using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Game flow FSM (§5 3.1) — replaces the old Intro/GameOver/Shop bools.</summary>
public enum GamePhase
{
    Title,
    Playing,
    Shop,
    Paused,
    // §5 6.3 end-of-run ceremony: the staged stat reveal + letter grade that
    // precedes the (folded-in) top-10/initials tail. The run is already "dead"
    // here — the GameOver bridge below reports true for BOTH Ceremony and
    // GameOver so every score-freeze/auto-defense/boss/audio bail keeps working.
    Ceremony,
    GameOver
}

/// <summary>Player settings (§5 3.1). Flat auto-properties — persisted inside the
/// profile via source-generated JSON (§4.2); keep it serializer-friendly.</summary>
public class Settings
{
    public float Volume { get; set; } = 0.54f;       // master volume 0-1 (mirrors the SynthAudio default)
    public float ShakeIntensity { get; set; } = 1f;  // scales trauma amplitude, 0-1
    public bool FlashReduction { get; set; }         // full-screen flashes render as edge vignette pulses
    public float UiScale { get; set; } = 1f;         // HUD font multiplier, 0.8-1.3
    public string Theme { get; set; } = "modern";    // modern, xbox, recharged
    public bool AssistEnemySlow { get; set; }        // enemy speed ×0.8 (flags AssistedRun)
    public bool AssistAutoEmp { get; set; }          // auto-EMP when the last city is threatened
    public bool ColorblindMode { get; set; }         // §5 5.4: blue/orange/white variant hues
}

public class GameState
{
    // Settings + theme (single source of truth is Settings.Theme; the property
    // keeps the ~100 renderer/theme-toggle sites compiling unchanged)
    public Settings Settings = new();
    public string Theme { get => Settings.Theme; set => Settings.Theme = value; }

    // Viewport
    public float W = 1280, H = 720;
    public float GroundY, HorizonY;

    // Timing
    public float Time, Last;

    // §4.3 stream-split RNG — fields, never properties: Xoshiro is a struct and a
    // property getter would hand out a copy, silently forking the stream.
    public ulong MasterSeed;   // identifies the run's spawn plans; shown on game-over
    public ulong? PendingSeed; // set by the Title daily-seed key; consumed by ResetGame
    public Xoshiro Cosmetic = new(unchecked((ulong)DateTime.UtcNow.Ticks)); // free-running FX/cosmetic stream
    public Xoshiro PlanRng;    // per-wave plan stream, re-seeded in StartWave (MasterSeed ^ wave)

    // Time director (§4.7) — written by FeelDirector. HitStop > 0 zeroes simDt;
    // TimeScale eases toward TimeScaleTarget on the raw clock.
    public float HitStop;
    public float TimeScale = 1f;
    public float TimeScaleTarget = 1f;

    // Game flow (§5 3.1)
    public GamePhase Phase = GamePhase.Title;
    public GamePhase PhaseBeforePause = GamePhase.Playing; // restored on ESC/RESUME
    public bool QuitRequested;  // set by the pause menu QUIT item; breaks the main loop
    public bool AssistedRun;    // an accessibility assist was active during this run

    // Compatibility bridges for non-owned readers (Combat, FeelDirector, the
    // entity systems, SynthAudio) — owned call sites read Phase directly.
    // All three are get-only: the last bool writer (WaveSystem.SpawnEnemy's
    // no-target bail-out) now sets Phase directly.
    public bool Intro => Phase == GamePhase.Title;
    public bool Shop => Phase == GamePhase.Shop;
    // §5 6.3: the run is "dead" the moment the ceremony opens — score freeze
    // (Combat.RegKill), auto-defense, boss spawns, mechanical-loop audio and the
    // FeelDirector regular-event bail all key off this, so it must cover Ceremony
    // AND the GameOver tail. The two phases differ only in presentation.
    public bool GameOver => Phase == GamePhase.Ceremony || Phase == GamePhase.GameOver;

    public bool GameOverSfx;
    public float GameOverTime;

    // §5 6.3 end-of-run ceremony: CeremonyT counts UP on rawDt from the death
    // freeze; the stage + per-stat reveal are pure functions of it (the Ceremony
    // helper; consumed by GameUpdate / Renderer). Grade is computed once
    // (CeremonyGraded) and stamped with a Trauma pulse. Run-level intercept totals
    // (run-wide accuracy — WaveStats resets per wave) feed the grade. All reset in
    // GameInit.ResetGame.
    public float CeremonyT;
    public bool CeremonyGraded;     // grade computed + Trauma pulse fired (once)
    public char CeremonyGrade = 'C';
    public int RunKills;            // run-wide intercepts (EventKind.Kill in DrainEvents)
    public int RunLeaks;            // run-wide leaks (GroundImpact in DrainEvents)
    public bool CeremonyInitialsArmed; // initials entry sequenced AFTER the grade reveal
    public int Level = 1;
    public int Score;
    // §5 3.5 scrap economy: the shop currency (Score is leaderboard-pure).
    // Backed by Upgrades so GameInit.ResetGame's `s.Upgrades = new Upgrades()`
    // resets it with the run — every reset path (incl. the non-owned pause-menu
    // RESTART) goes through there. Int property: no struct-copy hazard.
    public int Scrap { get => Upgrades.Scrap; set => Upgrades.Scrap = value; }
    public int Combo;
    public int MaxCombo;
    public float ComboTimer;
    // §5 4.4 presentation state (rawDt timers in GameUpdate):
    // DisplayScore chases Score (rate ∝ gap, min step) and feeds the HUD's
    // per-digit odometer; ComboPop is the 1→0 squash-stretch kill pop read by
    // the crosshair combo ring.
    public float DisplayScore;
    public float ComboPop;
    // §5 4.5 low-ammo geiger rate limiter (provably capped — ticks only re-arm it)
    public float LowAmmoTickCd;
    public float Danger;
    // §4.5 THE shared tension scalar (0..1) — one signal for spawner, music
    // director (Phase 6), audio danger layers and weather escalation. Weighted
    // blend of recent city hits (exp-decay), inbound count vs defenses,
    // terminal-approach near-misses and ammo scarcity, one-pole smoothed
    // (τ≈2 s). Formula lives in GameUpdate.UpdateIntensity.
    public float Intensity;
    public float RecentCityHits; // decaying city-loss accumulator (+1 per loss in DrainEvents, τ≈6 s)

    // Wave
    public float WavePause = 2f;
    public float WaveTime;
    public List<WavePlanEntry> WavePlan = [];
    // §5 6.2 wave intro stinger: typewriter reveal of WaveTitle over the
    // WavePause window. IntroT counts UP from 0 while bars/title ease in; any
    // input zeroes IntroSkip-style by jumping it past the reveal. WaveTitle is
    // composed once in StartWave (cached — the typewriter draws a substring, no
    // per-frame concat). Boss waves suppress the typewriter (the boss banner owns
    // the screen) — IntroBoss flags that.
    public float WaveIntroT;
    public bool WaveIntroDone; // collapsed by input OR fully elapsed
    public bool WaveIntroBoss; // this wave's intro is a boss encounter
    public string WaveTitle = "";
    // §5 6.2 report card (replaces the old boxing Dictionary<string,object>
    // telemetry): per-wave tallies fed by the event bus (DrainEvents) +
    // salvage at clear. Reset in StartWave; the cleared-stamp count-up and the
    // shop strategy panel both read it. Struct = zero heap, AOT-clean.
    public WaveStats Wave;
    // §5 6.2 cleared-stamp animation: armed at WaveCleared, drives the
    // scale-3→1 ease-out-back stamp + the tally count-up (rawDt timer).
    public float WaveClearT;
    public float FinaleStart; // wave-time where the finale segment begins (§4.5 spawn-hold exempt)
    // §4.3 forecast contract (§5 4.1): plan built & pinned at shop-open for
    // Level+1; StartWave for that level consumes this exact object — never
    // rebuilds — so the intel panel always matches the wave that arrives.
    public List<WavePlanEntry>? PinnedPlan;
    public int PinnedPlanLevel;
    public int SpawnI;
    public int UfoQuota;
    public float NextUfo;
    public int RaiderQuota;
    public float NextRaider;

    // Entities
    public List<Enemy> Enemies = [];
    public List<PlayerMissile> PlayerMissiles = [];
    public List<Explosion> Explosions = [];
    public List<UFO> UFOs = [];
    public List<Raider> Raiders = [];
    public Daemon? Demon;
    public Mothership? Mothership;
    public List<Fighter> Fighters = [];

    // Defenses
    public List<Base> Bases = [];
    public List<City> Cities = [];
    // Cached alive counts (§5 2.6) — recounted at the top of GameUpdate.UpdateAll and
    // kept fresh at the owned destroy/rebuild sites; replaces per-frame LINQ Count().
    public int AliveCities;
    public int AliveBases;
    public List<Phalanx> Phalanxes = [];
    public HellRaiser? HellRaiser;

    // Particles / FX — §5 5.3: capacities pre-sized to the pool caps in
    // Combat/GameUpdate so the lists never re-grow in steady state
    public List<Spark> Sparks = new(Combat.MaxSparks);
    public List<Smoke> SmokeParts = new(Combat.MaxSmoke);
    public List<Trail> Trails = new(256);
    public List<Debris> DebrisParts = new(Combat.MaxDebris + 8);
    public List<Shockwave> Shockwaves = new(32);
    public List<LightBurst> LightBursts = new(64);
    public List<MuzzleFlash> MuzzleFlashes = new(32);
    public List<Scorch> Scorches = new(48);
    public List<ShootingStar> ShootingStars = new(8);
    public List<BlastFlash> BlastFlashes = new(Combat.MaxBlastFlashes);
    public List<FloatingText> FloatingTexts = [];

    // §5 5.3 permanence timers: ScorchFadeT armed at shop close — the wave's
    // scorch history fades out across the wave pause; RuinSmokeCd rate-limits
    // the continuous smoke wisps rising from city ruins.
    public float ScorchFadeT;
    public float RuinSmokeCd;

    // Screen FX
    public float Chromatic;
    // Trauma camera (§5 1.3): writers ADD (clamped to 1); renderer applies
    // amplitude = Trauma² via noise-sampled offset/roll at the composite blit.
    public float Trauma;
    public float Flash;
    public float CrosshairPop; // 1 → 0 after each player fire (scale-pop)

    // §5 3.5: live HUD scrap-counter position (written by Renderer.DrawHUD every
    // frame) — homing scrap sparks magnet-stream toward it. Defaults approximate
    // the bottom-left panel for the frames before the first HUD draw.
    public float ScrapHudX = 160, ScrapHudY = 620;
    public float ScrapTickCd; // rate limit for the scrap-pickup audio tick

    public void AddTrauma(float amount) => Trauma = MathF.Min(1f, Trauma + amount);

    // Input
    public float MouseX, MouseY;

    // Player systems
    public bool Auto; // auto-defense
    public int Emp = 1;
    // §5 4.3: EMP RESERVE perk extends the base 3 slots (PerkFlags hook)
    public int EmpMax => 3 + Perks.EmpMaxBonus;
    public float EmpCd;
    public int? SelectedBase;

    // Messages
    public string Msg = "";
    public float MsgT;
    public string Note = "";
    public float NoteT;

    // Shop
    public float ShopTimer;
    public Upgrades Upgrades = new();

    // §5 4.3 perk-draft armory: central effect flags (grep contract on
    // PerkFlags) + the shop-scoped 3-card draft state. Field, not property —
    // hooks mutate runtime scratch (ChainT, CityShieldUsed) in place.
    public PerkFlags Perks = PerkFlags.Defaults;
    public readonly Perk?[] Draft = new Perk?[3];
    public int DraftPicked = -1; // installed card index; -1 = still choosing
    public bool DraftRerolled;   // one reroll per shop
    public Xoshiro DraftRng;     // §4.3: own stream (MasterSeed ^ Level ^ 0xD12AF7) — never touches plan draws
    public List<Perk> OwnedPerks = [];

    // Event bus (emitted during update, drained + cleared each frame in Program.cs)
    public readonly EventRing Events = new();

    // ID counter
    public int NextId = 1;
    public int NewId() => NextId++;

    // World scenery (generated once)
    public List<float[]> StarsA = [];
    public List<float[]> StarsB = [];
    public List<float[]> Nebula = [];
    public List<float[]> Aurora = [];
    public List<float[]> Clouds = [];
    public List<float[]> Haze = [];
    public List<float[]> MountFar = [];
    public List<float[]> MountNear = [];

    // Weather
    public WeatherState Weather = new();

    // Debug
    public DebugState Debug = new();
}

public class WavePlanEntry
{
    public string Variant = "standard";
    public float Time;
    public float Lane;
    // §5 4.2: MIRV tag is a PLAN-time decision (§4.3 plan stream) so the wave
    // director can compose it — never rolled at spawn/runtime
    public bool Mirv;
}

/// <summary>§5 6.2 per-wave report-card tallies — the AOT-friendly replacement
/// for the old boxing <c>Dictionary&lt;string,object&gt;</c> telemetry. Fed from
/// the event bus in DrainEvents (Kills, Leaks via GroundImpact, City/Base losses)
/// plus the salvage figure stamped at wave-clear and the city count snapshot.
/// All ints — no allocation, copy-by-value. Reset in StartWave.</summary>
public struct WaveStats
{
    public int Kills;          // enemies intercepted this wave (EventKind.Kill)
    public int Leaks;          // enemy missiles that reached the ground (GroundImpact)
    public int CitiesLost;     // CityDestroyed this wave
    public int BasesLost;      // BaseDestroyed this wave
    public int Salvage;        // scrap salvaged at clear (stamped once)
    public int CitiesSaved;    // alive-city snapshot at clear (stamped once)

    /// <summary>Intercept rate 0..100 (kills vs kills+leaks). 100 with no
    /// engagements — a quiet wave reads as a perfect defense, not a div-by-zero.</summary>
    public readonly int AccuracyPct
    {
        get
        {
            int shots = Kills + Leaks;
            return shots <= 0 ? 100 : (int)MathF.Round(100f * Kills / shots);
        }
    }
}

public class Upgrades
{
    public float BlastScale = 1.0f;
    public float ReloadMult = 1.0f;
    public float EmpScale = 1.0f;
    public float PhalanxEff = 1.0f;
    // §5 3.5 run-scoped economy state — lives here (not as GameState fields) so
    // GameInit.ResetGame's `s.Upgrades = new Upgrades()` resets it with the run.
    public int Scrap;
    public int WavesSinceFreeRepair; // free repair earned at 3; banked until something is damaged
}

public class WeatherState
{
    public string Mode = "clear"; // clear, ash, storm
    public float Intensity;
    public float Wind;
    public List<WeatherParticle> Particles = [];
    public List<FogBand> FogBands = [];
    public float LightningTimer;
    public float ThunderCd;
    public List<LightningBolt> Bolts = [];
}

public struct WeatherParticle
{
    public float X, Y, Vx, Vy;
    public float Z; // parallax depth
    public float Alpha;
    public float Len; // rain streak length / ash size
    public float Hue;
}

public struct FogBand
{
    public float Y, Thickness, Alpha, Speed, Phase;
}

public struct LightningBolt
{
    public float X, Y0, Y1;
    public float Life, MaxLife;
    public float Bright;
    public int Branches;
    public List<LightningSegment> Segments;
}

public struct LightningSegment
{
    public float X1, Y1, X2, Y2;
    public bool Branch;
}

public class DebugState
{
    public bool Enabled;
    // §5 6.2: the F8 overlay's per-kind event tallies — the only live debug
    // telemetry. The old boxing Dictionary<string,object> Waves (+ the never-read
    // session/wave-sequence scratch) was removed in favour of the AOT-clean
    // WaveStats struct that drives the report card.
    public int[] EventCounts = new int[EventRing.KindCount];
}
