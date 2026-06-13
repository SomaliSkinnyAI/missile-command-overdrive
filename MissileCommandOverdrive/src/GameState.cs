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
    // Ceremony, — Phase 6 (initials/score ceremony) slots in here
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
    public bool GameOver => Phase == GamePhase.GameOver;

    public bool GameOverSfx;
    public float GameOverTime;
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
    public int[] EventCounts = new int[EventRing.KindCount];
    public int MaxEventsPerWave = 120000;
    public string SessionStartedAt = DateTime.UtcNow.ToString("o");
    public string? CurrentWave;
    public int WaveSeq;
    public Dictionary<string, object> Waves = [];
    public int SessionDrops;
}
