using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Squad template (§5 4.1): member k spawns at anchorTime + DtOffsets[k],
/// lanes spread evenly across ±LaneSpread/2 around the anchor lane (plus small
/// plan-stream jitter). Cost prices the squad against the wave's threat budget
/// (≈10 per standard-missile-equivalent; ≈Σ member threat, rounded up for
/// formation value).</summary>
public record SquadTemplate(string[] Variants, float[] DtOffsets, float LaneSpread, int Cost);

public static class WaveSystem
{
    // §5 4.1 A/B insurance: flip the const (or run with MCOD_LEGACY_WAVES=1)
    // to fall back to the flat legacy generator.
    const bool UseDirector = true;
    static readonly bool ForceLegacyWaves =
        Environment.GetEnvironmentVariable("MCOD_LEGACY_WAVES") == "1";

    /// <summary>Wave composition (§4.3): every draw comes from the caller's per-wave
    /// plan stream, so the same (seed, wave) always yields an identical plan —
    /// verified by the MCOD_SELFTEST=1 check in Program.cs.</summary>
    public static List<WavePlanEntry> BuildPlan(int level, ref Xoshiro rng)
    {
        if (UseDirector && !ForceLegacyWaves) return BuildPlanDirector(level, ref rng);
        return BuildPlanLegacy(level, ref rng);
    }

    // ---------- §5 4.1 Wave Director ----------

    // Tension envelope: fraction of the wave span and density multiplier per
    // segment — build / peak / lull / finale.
    static readonly float[] SegFrac = [0.30f, 0.25f, 0.15f, 0.30f];
    static readonly float[] SegMult = [0.7f, 1.2f, 0.45f, 1.6f];

    static readonly (float Value, float Weight)[] DirectorLanes =
        [(-0.68f, 1f), (-0.35f, 1.2f), (0f, 1.6f), (0.35f, 1.2f), (0.68f, 1f)];

    // ~10 authored squads. DtOffsets are seconds from the squad anchor; lane
    // offsets run leftmost→rightmost member, so timing offsets shape formations
    // (e.g. center-leads-flanks-trail = V).
    static readonly SquadTemplate[] Squads =
    [
        new(["standard"], [0f], 0f, 10),                                              // lone standard — the filler beat
        new(["fast", "fast", "fast"], [0.22f, 0f, 0.22f], 0.34f, 40),                 // V of fasts (center point leader)
        new(["fast", "heavy", "fast"], [0.14f, 0f, 0.2f], 0.3f, 52),                  // escorted heavy (MIRV-capable centerpiece)
        new(["carrier", "drone", "drone"], [0f, 0.32f, 0.46f], 0.26f, 52),            // carrier + escorts
        new(["zig", "zig", "zig", "zig"], [0f, 0.11f, 0.21f, 0.32f], 0.44f, 56),      // zig swarm
        new(["stealth", "stealth"], [0f, 0.55f], 0.22f, 34),                          // stealth pair
        new(["drone", "drone", "drone"], [0.16f, 0f, 0.16f], 0.3f, 38),               // drone wedge
        new(["standard", "shield", "standard"], [0.2f, 0f, 0.34f], 0.18f, 42),        // shield-drone pocket
        new(["decoy", "decoy", "standard", "standard"], [0f, 0.09f, 0.5f, 0.62f], 0.4f, 34), // decoy screen, warheads behind
        new(["cruise", "cruise", "spit", "spit"], [0f, 0.36f, 0.5f, 0.62f], 0.36f, 56), // cruise + spit — the low-altitude closer
    ];

    // Unlock gates lifted from the legacy weight table (fast at 2; zig/stealth/
    // decoy at 3; split/cruise at 4; heavy/drone at 5; carrier/shield at 6).
    // spit rides the cruise gate — it only ships in the cruise closer squad.
    static int UnlockLevel(string v) => v switch
    {
        "fast" => 2,
        "zig" or "stealth" or "decoy" => 3,
        "split" or "cruise" or "spit" => 4,
        "heavy" or "drone" => 5,
        "carrier" or "shield" => 6,
        _ => 1
    };

    /// <summary>Per-variant threat price (§5 4.1) — the budget unit (10 = one
    /// standard missile). Squad costs are authored from these; the shop intel
    /// meter sums the same values over the pinned plan.</summary>
    public static int ThreatOf(string v) => v switch
    {
        "fast" => 13,
        "zig" => 14,
        "stealth" => 17,
        "decoy" => 6,
        "split" => 18,
        "cruise" => 18,
        "drone" => 12,
        "spit" => 10,
        "heavy" => 22,
        "carrier" => 26,
        "shield" => 20,
        "hell" => 18,
        _ => 10
    };

    // Wave span grows with level like the legacy generator's spawn cadence did
    static float WaveDuration(int level) => MathF.Min(50f, 24f + level * 1.7f);

    /// <summary>Wave-time at which the finale segment begins — the §4.5
    /// spawn-hold exemption window (same envelope math as BuildPlanDirector).</summary>
    public static float FinaleStartTime(int level) => WaveDuration(level) * (1f - SegFrac[3]);

    // Squad selection weight for one pick. 0 = ineligible (locked variant or
    // unaffordable beyond the small overshoot slack that lets segments close).
    static float SquadWeight(SquadTemplate sq, int level, int seg, float remaining)
    {
        if (sq.Cost > remaining + 8f) return 0f;
        for (int i = 0; i < sq.Variants.Length; i++)
            if (level < UnlockLevel(sq.Variants[i])) return 0f;
        float w = sq.Variants.Length == 1 ? 2.6f : 1f; // lone standard stays the common beat
        if (seg == 3 && sq.Cost >= 45) w *= 2.2f;      // finale bias: the heavy closers
        return w;
    }

    /// <summary>§5 4.1 Wave Director: spends a level-derived threat budget
    /// (140 + 50·level — ≈10 threat per legacy enemy, matching the old
    /// 14 + 5·level count) across the four-segment tension envelope. Each
    /// segment's budget share is frac·mult normalized over all segments; squad
    /// anchors advance ∝ cost share so density inside a segment stays even.
    /// Every draw comes from the per-wave plan stream (§4.3) — same (seed,
    /// wave) ⇒ identical plan, MIRV tags included.</summary>
    static List<WavePlanEntry> BuildPlanDirector(int level, ref Xoshiro rng)
    {
        float budget = 140f + level * 50f;
        float waveT = WaveDuration(level);

        float wSum = 0f;
        for (int i = 0; i < SegFrac.Length; i++) wSum += SegFrac[i] * SegMult[i];

        var plan = new List<WavePlanEntry>();
        float segStart = 0f;
        for (int seg = 0; seg < SegFrac.Length; seg++)
        {
            float segDur = waveT * SegFrac[seg];
            float segBudget = budget * (SegFrac[seg] * SegMult[seg]) / wSum;
            float spent = 0f;
            float t = segStart + segDur * 0.12f * rng.NextSingle();
            while (spent < segBudget)
            {
                // Weighted squad pick — two passes, allocation-free
                float total = 0f;
                for (int q = 0; q < Squads.Length; q++)
                    total += SquadWeight(Squads[q], level, seg, segBudget - spent);
                if (total <= 0f) break; // budget exhausted below the cheapest squad
                float roll = rng.NextSingle() * total;
                var sq = Squads[0];
                for (int q = 0; q < Squads.Length; q++)
                {
                    float w = SquadWeight(Squads[q], level, seg, segBudget - spent);
                    if (w <= 0f) continue;
                    sq = Squads[q];
                    roll -= w;
                    if (roll <= 0f) break;
                }

                float anchorLane = RandHelper.PickWeighted(DirectorLanes, ref rng);
                int n = sq.Variants.Length;
                for (int k = 0; k < n; k++)
                {
                    string v = sq.Variants[k];
                    float laneOff = n > 1 ? sq.LaneSpread * (k / (float)(n - 1) - 0.5f) : 0f;
                    // §5 4.2 MIRV tagging — plan-time (§4.3): the draw count
                    // depends only on (variant, level), so tags can never shift
                    float mirvChance = VariantStats.Def(v).MirvChance;
                    bool mirv = mirvChance > 0 && level >= 4 && rng.NextSingle() < mirvChance;
                    plan.Add(new WavePlanEntry
                    {
                        Variant = v,
                        Mirv = mirv,
                        Time = MathF.Max(0f, t + sq.DtOffsets[k] + rng.NextFloat(-0.04f, 0.04f)),
                        Lane = MathH.Clamp(anchorLane + laneOff + rng.NextFloat(-0.05f, 0.05f), -0.85f, 0.85f)
                    });
                }

                spent += sq.Cost;
                t += segDur * (sq.Cost / segBudget) * rng.NextFloat(0.78f, 1.18f);
            }
            segStart += segDur;
        }

        plan.Sort((a, b) => a.Time.CompareTo(b.Time));
        return plan;
    }

    /// <summary>§5 4.1 intel forecast (§4.3 forecast contract): build the NEXT
    /// wave's plan from its own per-wave stream at shop-open and PIN it —
    /// StartWave for that level consumes this exact object, so the intel panel
    /// and the wave that arrives can never disagree.</summary>
    public static void BuildForecast(GameState s)
    {
        int next = s.Level + 1;
        var rng = new Xoshiro(s.MasterSeed ^ (ulong)next);
        s.PinnedPlan = BuildPlan(next, ref rng);
        s.PinnedPlanLevel = next;
    }

    // ---------- Legacy generator (pre-director; kept as the A/B fallback) ----------

    static List<WavePlanEntry> BuildPlanLegacy(int level, ref Xoshiro rng)
    {
        int total = 14 + level * 5;
        var plan = new List<WavePlanEntry>();
        float t = 0;

        var weights = new (string v, float w)[]
        {
            ("standard", 58),
            ("fast", level > 1 ? 20 + level * 1.8f : 6),
            ("zig", level > 2 ? 12 + level * 1.8f : 0),
            ("stealth", level > 2 ? 10 + level * 1.6f : 0),
            ("decoy", level > 2 ? 10 + level * 1.4f : 0),
            ("split", level > 3 ? 9 + level * 1.45f : 0),
            ("heavy", level > 4 ? 8 + level * 1.25f : 0),
            ("cruise", level > 3 ? 8 + level * 1.3f : 0),
            ("carrier", level > 5 ? 4 + level * 0.9f : 0),
            ("drone", level > 4 ? 5 + level * 1.1f : 0),
            ("shield", level > 5 ? 3 + level * 0.55f : 0),
        };

        var laneWeights = new (float v, float w)[]
        {
            (-0.68f, 1f), (-0.35f, 1.2f), (0f, 1.6f), (0.35f, 1.2f), (0.68f, 1f)
        };

        for (int i = 0; i < total && plan.Count < total; i++)
        {
            float salvoChance = MathH.Clamp(0.13f + level * 0.02f, 0.13f, 0.48f);
            int salvo = rng.NextSingle() < salvoChance ? (rng.NextSingle() < 0.25f ? 3 : 2) : 1;
            float lane = RandHelper.PickWeighted(laneWeights, ref rng);

            for (int s = 0; s < salvo && plan.Count < total; s++)
            {
                string variant = RandHelper.PickWeighted(weights, ref rng);
                // §5 4.2 MIRV tagging — plan-time (§4.3): the draw count depends
                // only on (variant, level), so same (seed, wave) ⇒ same tags
                float mirvChance = VariantStats.Def(variant).MirvChance;
                bool mirv = mirvChance > 0 && level >= 4 && rng.NextSingle() < mirvChance;
                plan.Add(new WavePlanEntry
                {
                    Variant = variant,
                    Mirv = mirv,
                    Time = t + s * rng.NextFloat(0.06f, 0.16f),
                    Lane = lane + rng.NextFloat(-0.12f, 0.12f)
                });
            }

            t += MathF.Max(0.28f, 1.26f - level * 0.05f) + rng.NextFloat(0.06f, 0.72f);
        }

        plan.Sort((a, b) => a.Time.CompareTo(b.Time));
        return plan;
    }

    public static void StartWave(GameState s, float delay)
    {
        s.UFOs.Clear();
        s.Raiders.Clear();
        s.Enemies.Clear();
        s.PlayerMissiles.Clear();
        s.Explosions.Clear();
        s.Sparks.Clear();
        s.SmokeParts.Clear();
        s.Trails.Clear();
        s.DebrisParts.Clear();
        s.Shockwaves.Clear();
        s.LightBursts.Clear();
        // §5 6.1: a lingering boss from the previous wave is cleared at wave start
        // so a scheduled boss is the only one on screen (and a level-skip is clean).
        s.Mothership = null;
        s.Demon = null;
        s.Fighters.Clear();

        // §5 3.5: the old 33/40/58% coin-flip self-repairs are gone — structures
        // only come back via the free repair (every 3 cleared waves, FreeRepair
        // below) or as scrap purchases in the shop. Stakes are deterministic.
        int waveBaseAmmo = Math.Min(155, (int)MathF.Round(20 + s.Level * 2.7f + MathF.Max(0, s.Level - 14) * 1.35f));
        foreach (var b in s.Bases)
        {
            b.Ammo = b.Destroyed ? 0 : waveBaseAmmo;
            b.MaxAmmo = waveBaseAmmo;
            b.Cooldown = 0;
        }

        float oldPhalanxMax = MathF.Min(1300, MathF.Round((620 + s.Level * 90) * (1 + (s.Upgrades.PhalanxEff - 1) * 0.55f)));
        int perUnitMax = (int)MathF.Max(340, MathF.Round(oldPhalanxMax * 0.62f));
        foreach (var p in s.Phalanxes)
        {
            p.Ammo = p.Destroyed ? 0 : perUnitMax;
            p.MaxAmmo = perUnitMax;
            p.Cool = 0;
            p.Heat = 0;
            p.FireAcc = 0;
            p.Target = null;
        }

        if (s.HellRaiser != null)
        {
            var hr = s.HellRaiser;
            // §5 4.3 DEEP MAGAZINES perk scales the magazine past the base cap
            hr.MaxAmmo = hr.Destroyed ? 0
                : (int)MathF.Round(MathF.Min(1100f, 460f + s.Level * 70f) * s.Perks.HrAmmoMult);
            hr.Ammo = hr.MaxAmmo;
            hr.State = hr.Destroyed ? "destroyed" : "hidden";
            hr.Lift = hr.Destroyed ? 0.45f : 0;
            hr.DoorOpen = hr.Destroyed ? 0.5f : 0;
            hr.FireCd = 0;
            hr.Cool = hr.Destroyed ? 0 : 0.95f;
        }

        if (s.Level > 1) s.Emp = (int)MathH.Clamp(s.Emp + 1, 0, s.EmpMax);

        // §4.3 per-wave plan stream: same MasterSeed & wave ⇒ identical plan.
        // UFO/raider jitter below stays on the cosmetic stream so player-
        // dependent draw counts can never shift the plan.
        s.PlanRng = new Xoshiro(s.MasterSeed ^ (ulong)s.Level);
        // §4.3 forecast contract (§5 4.1): a plan pinned at shop-open for THIS
        // level is consumed as-is — never rebuilt. Any other entry path (level
        // skip, restart, stale pin) discards the pin and builds fresh.
        if (s.PinnedPlan != null && s.PinnedPlanLevel == s.Level)
            s.WavePlan = s.PinnedPlan;
        else
            s.WavePlan = BuildPlan(s.Level, ref s.PlanRng);
        s.PinnedPlan = null;
        s.FinaleStart = FinaleStartTime(s.Level); // §4.5 spawn-hold exemption window
        s.WavePause = delay;
        s.WaveTime = 0;
        s.SpawnI = 0;
        s.UfoQuota = s.Level >= 2 ? Math.Min(3, 1 + (s.Level - 2) / 2) : 0;
        s.NextUfo = delay + MathH.Rand(5.8f, 10.6f);
        s.RaiderQuota = s.Level >= 4 ? Math.Min(2 + s.Level / 6, 4) : 0;
        s.NextRaider = delay + MathH.Rand(4.4f, 8.2f);

        // §5 4.3: the draft is shop-scoped; AEGIS DOME re-arms each wave
        PerkSystem.ClearDraft(s);
        s.Perks.CityShieldUsed = false;

        // §5 6.2 wave stinger: reset the report-card tallies for the new wave and
        // arm the intro typewriter (WeatherSystem.SetWaveWeather runs below, so the
        // flavour reads off the weather chosen for THIS wave).
        s.Wave = default;
        s.WaveClearT = 0f;
        s.WaveIntroT = 0f;
        s.WaveIntroDone = false;
        s.WaveIntroBoss = s.Level % 5 == 0;

        // §5 6.1 scheduled boss every 5th wave (deterministic, Level-keyed):
        // Mothership at 5/15/25…, Daemon at 10/20/30…. The 666/777 cheat codes
        // remain independent instant summons. The boss spawns into the wave-pause
        // window so its intro banner reads before regular spawns begin; the
        // Mothership additionally holds spawning (HoldSpawning) for the cinematic.
        if (s.Level % 5 == 0)
        {
            if (s.Level % 10 == 0) DemonSystem.Spawn(s, scheduled: true);
            else MothershipSystem.Spawn(s, scheduled: true);
            s.WavePause = MathF.Max(s.WavePause, 3.0f); // longer cinematic intro
            s.Note = $"WAVE {s.Level} — BOSS ENCOUNTER";
            s.NoteT = 2.6f;
        }
        else
        {
            s.Note = $"Wave {s.Level} incoming | {s.Weather.Mode.ToUpperInvariant()} FRONT";
            s.NoteT = 2.1f;
        }
        s.Events.Emit(EventKind.WaveStart, s.W * 0.5f, s.H * 0.5f, s.Level);
        SynthAudio.WaveStab(); // §5 4.5 wave-banner stab

        WeatherSystem.SetWaveWeather(s);

        // §5 6.2 typewriter title — composed ONCE here so the intro draws a
        // substring of this cached string (no per-frame concat). Boss waves keep
        // the boss banner (s.Note) and skip the typewriter, so build the title
        // only for regular waves.
        s.WaveTitle = s.WaveIntroBoss ? "" : $"WAVE {s.Level} — {WaveFlavor(s)}";
    }

    // §5 6.2 weather/level-flavoured wave name (deterministic, per-wave plan
    // stream — same seed ⇒ same name; picked from the slice matching the wave's
    // weather mode so the title and the sky agree).
    static readonly string[] FlavorClear =
        ["CLEAR SKIES", "NIGHT WATCH", "DEAD CALM", "FIRST LIGHT", "SILENT RUN", "OPEN FIELD"];
    static readonly string[] FlavorAsh =
        ["ASHFALL", "CINDER VEIL", "EMBER DRIFT", "GREY DAWN", "SCORCHED AIR", "FALLOUT"];
    static readonly string[] FlavorStorm =
        ["STORM FRONT", "THUNDERHEAD", "SQUALL LINE", "TEMPEST", "BLACK RAIN", "GALE WARNING"];

    static string WaveFlavor(GameState s)
    {
        var pool = s.Weather.Mode switch
        {
            "ash" => FlavorAsh,
            "storm" => FlavorStorm,
            _ => FlavorClear
        };
        // Deterministic index from (seed, level) — own derivation so it can't
        // perturb the plan stream (which is already consumed by this point).
        ulong h = (s.MasterSeed ^ ((ulong)s.Level * 0x9E3779B97F4A7C15UL));
        return pool[(int)(h % (ulong)pool.Length)];
    }

    /// <summary>§5 3.5 deterministic repairs: one free structure repair earned per
    /// 3 cleared waves (priority base > phalanx > HellRaiser) — replaces the old
    /// coin-flip resurrections. Ammo/state are fully restored by the next
    /// StartWave; returns false when nothing is damaged so the caller can bank
    /// the earned repair.</summary>
    public static bool FreeRepair(GameState s)
    {
        foreach (var b in s.Bases)
            if (b.Destroyed)
            {
                b.Destroyed = false;
                s.AliveBases++;
                s.Msg = "FIELD REPAIR: launch base restored";
                s.MsgT = 1.6f;
                return true;
            }
        foreach (var p in s.Phalanxes)
            if (p.Destroyed)
            {
                p.Destroyed = false;
                s.Msg = "FIELD REPAIR: Phalanx CIWS restored";
                s.MsgT = 1.6f;
                return true;
            }
        if (s.HellRaiser is { Destroyed: true } hr)
        {
            hr.Destroyed = false;
            hr.State = "hidden";
            hr.Lift = 0;
            hr.DoorOpen = 0;
            hr.FireCd = 0;
            hr.Cool = 0.95f;
            s.Msg = "FIELD REPAIR: HellRaiser restored";
            s.MsgT = 1.6f;
            return true;
        }
        return false;
    }

    public static TargetInfo? ChooseTarget(GameState s, string variant)
    {
        var candidates = new List<(TargetInfo value, float weight)>();

        foreach (var city in s.Cities)
        {
            if (city.Destroyed) continue;
            int neigh = s.Cities.Count(x => !x.Destroyed && MathF.Abs(x.X - city.X) < s.W * 0.14f);
            float w = 95 + neigh * 22 + MathH.Rand(0, 18);
            if (variant == "heavy") w += 44;
            if (variant == "split") w += 26;
            if (variant == "ufoBomb") w += 60;
            if (variant == "carrier") w += 28;
            if (variant == "drone") w += 14;
            if (variant == "hell") w += 64;
            if (variant == "spit") w += 22;
            candidates.Add((new TargetInfo
            {
                Type = "city",
                X = city.X + MathH.Rand(-city.W * 0.24f, city.W * 0.24f),
                Y = s.GroundY - 30,
                Id = city.Id
            }, w));
        }

        foreach (var b in s.Bases)
        {
            if (b.Destroyed) continue;
            float w = 72 + b.Ammo * 2.6f + MathH.Rand(0, 16);
            if (variant == "fast") w += 28;
            if (variant == "zig") w += 14;
            if (variant == "ufoBomb") w -= 14;
            if (variant == "cruise") w += 56;
            if (variant == "carrier") w += 18;
            if (variant == "drone") w += 26;
            if (variant == "hell") w += 42;
            if (variant == "spit") w += 14;
            candidates.Add((new TargetInfo
            {
                Type = "base",
                X = b.X,
                Y = s.GroundY - 14,
                Id = b.Id
            }, w));
        }

        foreach (var p in s.Phalanxes)
        {
            if (p.Destroyed) continue;
            float w = 28 + p.Ammo * 0.012f + MathH.Rand(0, 8);
            if (variant is "fast" or "heavy") w += 10;
            if (variant == "drone") w += 14;
            if (variant == "cruise") w += 13;
            if (variant == "hell") w += 10;
            candidates.Add((new TargetInfo
            {
                Type = "phalanx",
                X = p.X,
                Y = s.GroundY - 18,
                Id = p.Id
            }, w));
        }

        if (candidates.Count == 0) return null;
        return RandHelper.PickWeighted(candidates);
    }

    public static void SpawnEnemy(GameState s, WavePlanEntry e)
    {
        var t = ChooseTarget(s, e.Variant);
        // §5 6.3: no valid target = every city is gone — route through the single
        // death edge so the ceremony (freeze/grade/initials) plays here too, not
        // just on the GameUpdate aliveCities check.
        if (t == null) { if (s.Phase == GamePhase.Playing) GameUpdate.EnterCeremony(s); return; }

        bool cruise = e.Variant == "cruise";
        bool carrier = e.Variant == "carrier";
        float sx = cruise
            ? (RandHelper.Next01() < 0.5f ? -70 : s.W + 70)
            : MathH.Clamp(s.W * 0.5f + e.Lane * s.W * 0.44f + MathH.Rand(-140, 140), 14, s.W - 14);
        float sy = cruise
            ? MathH.Rand(s.HorizonY * 0.66f, s.GroundY * 0.52f)
            : carrier ? MathH.Rand(-220, -120) : MathH.Rand(-160, -40);

        Combat.CreateEnemyProjectile(s, e.Variant, sx, sy, t.Value, mirv: e.Mirv);
        SynthAudio.EnemyLaunch(MathH.Clamp(sx / s.W, 0, 1));
        // §5 4.2 shield drone announces itself — low hum swell on spawn
        if (e.Variant == "shield") SynthAudio.ShieldHum(MathH.Clamp(sx / s.W, 0, 1));
    }

    public static void SpawnUfo(GameState s)
    {
        int alive = s.Cities.Count(c => !c.Destroyed);
        if (alive == 0) return;

        bool left = RandHelper.Next01() < 0.5f;
        float baseY = MathH.Rand(s.HorizonY * 0.62f, s.HorizonY * 0.88f);
        float x = left ? -90 : s.W + 90;
        bool isBoss = s.Level >= 5 && RandHelper.Next01() < 0.2f + s.Level * 0.05f;
        float vxMult = isBoss ? 0.6f : 1f;
        float vx = left
            ? (MathH.Rand(58, 96) + s.Level * 3) * vxMult
            : -(MathH.Rand(58, 96) + s.Level * 3) * vxMult;

        s.UFOs.Add(new UFO
        {
            Id = s.NewId(),
            X = x,
            Y = baseY,
            Vx = vx,
            Speed = MathF.Abs(vx),
            BobPhase = RandHelper.Next01() * MathH.TAU,
            Boss = isBoss,
            Hp = isBoss ? 6 : 2,
            FireCd = MathH.Rand(1.25f, 2.35f)
        });

        s.Note = isBoss ? "WARNING: Boss UFO detected" : "UFO intruder detected";
        s.NoteT = 1.1f;
    }

    public static void SpawnRaider(GameState s)
    {
        bool left = RandHelper.Next01() < 0.5f;
        float x = left ? -95 : s.W + 95;
        float y = MathH.Rand(s.HorizonY * 0.14f, s.HorizonY * 0.34f);
        float dir = left ? 1 : -1;

        s.Raiders.Add(new Raider
        {
            Id = s.NewId(),
            X = x,
            Y = y,
            Vx = dir * MathH.Rand(150, 210),
            Speed = MathH.Rand(150, 210),
            FireCd = MathH.Rand(0.65f, 1.25f),
            Angle = 0,
            Hp = 5
        });

        s.Note = "Stratospheric Raider detected";
        s.NoteT = 0.95f;
    }
}
