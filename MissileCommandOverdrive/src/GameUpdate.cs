using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>All per-frame update logic.</summary>
public static class GameUpdate
{
    // §4.7: simDt drives the sim (0 during hit-stop, scaled in slow-mo);
    // rawDt drives presentation so UI/floaters/shake never freeze.
    public static void UpdateAll(GameState s, float simDt, float rawDt)
    {
        s.Time += simDt;
        // Cosmetic particle clock: slows with slow-mo but keeps moving in hit-stop
        float fxDt = rawDt * s.TimeScale;

        // Timers — presentation on rawDt, sim timers on simDt
        if (s.MsgT > 0) s.MsgT = MathF.Max(0, s.MsgT - rawDt);
        if (s.NoteT > 0) s.NoteT = MathF.Max(0, s.NoteT - rawDt);
        if (s.ComboTimer > 0)
        {
            s.ComboTimer = MathF.Max(0, s.ComboTimer - simDt);
            if (s.ComboTimer == 0)
            {
                // §5 4.5 combo-break: only a real chain (4+) earns the pitch-drop
                // womp — single-kill expiries stay silent
                if (s.Combo >= 4 && s.Phase == GamePhase.Playing) Audio.SynthAudio.ComboBreak();
                s.Combo = 0;
            }
        }
        if (s.Trauma > 0) s.Trauma = MathF.Max(0, s.Trauma - rawDt * 1.2f);
        if (s.CrosshairPop > 0) s.CrosshairPop = MathF.Max(0, s.CrosshairPop - rawDt * 6.5f);
        if (s.ComboPop > 0) s.ComboPop = MathF.Max(0, s.ComboPop - rawDt * 3.4f);
        if (s.LowAmmoTickCd > 0) s.LowAmmoTickCd = MathF.Max(0, s.LowAmmoTickCd - rawDt);
        // §5 6.2 wave stinger timers (presentation — rawDt, so they ease through
        // any hit-stop). The intro runs while the wave-pause letterbox holds and
        // self-completes once the title has finished typing + lingered; the
        // cleared-stamp count-up runs after the wave-clear arms WaveClearT.
        UpdateWaveStinger(s, rawDt);
        // §5 4.4 odometer: DisplayScore chases Score — rate ∝ gap with a min step
        // so small awards still visibly roll; Score only ever drops on reset, so
        // a downward gap snaps (no reverse-rolling wheels)
        if (s.DisplayScore < s.Score)
            s.DisplayScore = MathF.Min(s.Score,
                s.DisplayScore + MathF.Max((s.Score - s.DisplayScore) * 4f, 120f) * rawDt);
        else if (s.DisplayScore > s.Score) s.DisplayScore = s.Score;
        if (s.Flash > 0) s.Flash = MathF.Max(0, s.Flash - rawDt * 1.7f);
        if (s.EmpCd > 0) s.EmpCd = MathF.Max(0, s.EmpCd - simDt);
        // §5 4.3 CHAIN PULSE perk: fire the echo EMP scheduled by Combat.SpawnExpl
        if (s.Perks.ChainT > 0 && s.Phase == GamePhase.Playing)
        {
            s.Perks.ChainT = MathF.Max(0, s.Perks.ChainT - simDt);
            if (s.Perks.ChainT == 0)
                Combat.SpawnExpl(s, s.Perks.ChainX, s.Perks.ChainY,
                    228f * 0.6f * s.Upgrades.EmpScale, 1.15f, 0.42f,
                    player: true, emp: true, flash: 0.18f, chainChild: true);
        }
        if (s.Chromatic > 0) s.Chromatic = MathF.Max(0, s.Chromatic - rawDt * 1.8f);
        if (s.ScrapTickCd > 0) s.ScrapTickCd = MathF.Max(0, s.ScrapTickCd - rawDt);

        // Micro-feedback timers (presentation — rawDt)
        foreach (var b in s.Bases)
        {
            if (b.MuzzleT > 0) b.MuzzleT = MathF.Max(0, b.MuzzleT - rawDt);
            if (b.ResupplyFlash > 0) b.ResupplyFlash = MathF.Max(0, b.ResupplyFlash - rawDt * 2.4f);
            if (b.Recoil != 0 || b.RecoilV != 0)
            {
                b.RecoilV += (-400f * b.Recoil - 16f * b.RecoilV) * rawDt;
                b.Recoil += b.RecoilV * rawDt;
                if (MathF.Abs(b.Recoil) < 0.02f && MathF.Abs(b.RecoilV) < 0.5f) { b.Recoil = 0; b.RecoilV = 0; }
            }
        }
        foreach (var m in s.Enemies)
        {
            if (m.FlashT > 0) m.FlashT = MathF.Max(0, m.FlashT - rawDt);
            if (m.ShieldFlashT > 0) m.ShieldFlashT = MathF.Max(0, m.ShieldFlashT - rawDt);
        }
        foreach (var u in s.UFOs) if (u.FlashT > 0) u.FlashT = MathF.Max(0, u.FlashT - rawDt);
        foreach (var r in s.Raiders) if (r.FlashT > 0) r.FlashT = MathF.Max(0, r.FlashT - rawDt);
        foreach (var f in s.Fighters) if (f.FlashT > 0) f.FlashT = MathF.Max(0, f.FlashT - rawDt);
        if (s.Demon != null && s.Demon.FlashT > 0) s.Demon.FlashT = MathF.Max(0, s.Demon.FlashT - rawDt);

        // Floating texts
        for (int i = s.FloatingTexts.Count - 1; i >= 0; i--)
        {
            s.FloatingTexts[i].Life -= rawDt;
            s.FloatingTexts[i].Y -= rawDt * 38;
            if (s.FloatingTexts[i].Life <= 0) s.FloatingTexts.RemoveAt(i);
        }

        // §5 6.3 ceremony + the folded-in GameOver tail: the world keeps burning
        // behind the overlay (the sim already updated during GameOver). simDt is
        // held at TimeScale 0.3 during the ceremony (set on the death edge below)
        // so the backdrop smoulders rather than races; score is frozen in RegKill.
        if (s.Phase == GamePhase.Ceremony || s.Phase == GamePhase.GameOver)
        {
            // §4.5: UpdateIntensity is unreachable past the early-return below, so
            // the shared tension signal would otherwise freeze at the (usually
            // high) value it held at death and keep the music director banging
            // under the reflective ceremony. Wind it down here on rawDt (simDt is
            // hit-stopped to 0 during the death freeze) so the score strips out.
            s.Intensity *= MathF.Exp(-rawDt / 0.8f);
            if (s.Intensity < 0.01f) s.Intensity = 0;
            if (s.Phase == GamePhase.Ceremony) UpdateCeremony(s, rawDt);
            else s.GameOverTime += rawDt;
            UpdEnemies(s, simDt);
            UpdUfo(s, simDt);
            UpdRaiders(s, simDt);
            UpdPlayer(s, simDt);
            UpdExplosions(s, simDt);
            UpdParticles(s, fxDt);
            // Damage is per-frame-of-overlap: resolving while simDt == 0 deals
            // free ticks to multi-HP units inside frozen explosions
            if (simDt > 0) Combat.RunCollisions(s);
            return;
        }

        // Cached alive counts (§5 2.6): plain loops, recounted each frame so non-owned
        // mutate sites (e.g. WaveSystem base resurrection) can never leave them stale
        int aliveCities = 0;
        for (int i = 0; i < s.Cities.Count; i++) if (!s.Cities[i].Destroyed) aliveCities++;
        int aliveBases = 0;
        for (int i = 0; i < s.Bases.Count; i++) if (!s.Bases[i].Destroyed) aliveBases++;
        s.AliveCities = aliveCities;
        s.AliveBases = aliveBases;

        // Ammo resupply for alive bases (allocation-free; same RNG call order as before)
        if (aliveCities > 0 && aliveBases > 0)
        {
            int targetLow = s.Auto ? 44 : 36;
            int totalAmmo = 0;
            Base? resupply = null; // alive base with the least ammo below targetLow
            foreach (var b in s.Bases)
            {
                if (b.Destroyed) continue;
                totalAmmo += b.Ammo;
                if (b.Ammo < targetLow && (resupply == null || b.Ammo < resupply.Ammo))
                    resupply = b;
            }
            bool emergency = totalAmmo <= Math.Max(24, 8 + s.Level * 0.9f);
            float supportRate = 0.18f + MathH.Clamp((s.Level - 12) * 0.012f, 0, 0.28f)
                + (s.Auto ? 0.09f : 0) + s.Danger * 0.16f + (emergency ? 0.32f : 0);
            if (resupply != null && RandHelper.Next01() < simDt * supportRate)
            {
                int grant = emergency && RandHelper.Next01() < 0.4f ? 2 : 1;
                resupply.Ammo = Math.Min(180, resupply.Ammo + grant);
                resupply.ResupplyFlash = 1f;
            }
        }

        // Base cooldowns
        foreach (var b in s.Bases)
            if (b.Cooldown > 0) b.Cooldown = MathF.Max(0, b.Cooldown - simDt);

        // §5 4.5 low-ammo geiger: the base that would answer the next click
        // (selected, else nearest to the crosshair — same pick order as
        // Combat.LaunchPlayer) crackles when below 25% magazine. Provably
        // rate-capped: a tick always re-arms LowAmmoTickCd ≥ 0.45 s.
        if (s.Phase == GamePhase.Playing && simDt > 0 && s.LowAmmoTickCd <= 0)
        {
            Base? next = null;
            if (s.SelectedBase is int si && si >= 0 && si < s.Bases.Count
                && !s.Bases[si].Destroyed && s.Bases[si].Ammo > 0)
                next = s.Bases[si];
            if (next == null)
            {
                float bestD = float.MaxValue;
                foreach (var b in s.Bases)
                {
                    if (b.Destroyed || b.Ammo <= 0) continue;
                    float d = MathF.Abs(b.X - s.MouseX);
                    if (d < bestD) { bestD = d; next = b; }
                }
            }
            if (next != null && next.MaxAmmo > 0 && next.Ammo * 4 < next.MaxAmmo)
            {
                // ticks quicken as the magazine drains (0.9 s → 0.45 s floor)
                float fill = next.Ammo / (next.MaxAmmo * 0.25f); // 1 → 0 toward empty
                s.LowAmmoTickCd = 0.45f + 0.45f * fill;
                Audio.SynthAudio.GeigerTick(MathH.Clamp(next.X / s.W, 0, 1));
            }
        }

        // Wave spawning (suppressed while mothership is active — cinematic pause)
        if (s.Phase == GamePhase.Playing && !MothershipSystem.HoldSpawning(s))
        {
            if (s.WavePause > 0)
            {
                s.WavePause -= simDt;
            }
            else
            {
                // §4.5 spawn gating (L4D-style breathing room): at peak stress
                // the spawn clock holds so no new pressure lands — except inside
                // the finale segment, the authored climax, which always arrives
                // on schedule. No deadlock: with the sky empty the inbound and
                // near-miss terms are 0 and Intensity decays well below the gate.
                if (s.Intensity <= SpawnHoldIntensity || s.WaveTime >= s.FinaleStart)
                    s.WaveTime += simDt;
                while (s.SpawnI < s.WavePlan.Count && s.WavePlan[s.SpawnI].Time <= s.WaveTime)
                {
                    WaveSystem.SpawnEnemy(s, s.WavePlan[s.SpawnI]);
                    s.SpawnI++;
                }
                if (s.UfoQuota > 0 && s.WaveTime >= s.NextUfo)
                {
                    WaveSystem.SpawnUfo(s);
                    s.UfoQuota--;
                    s.NextUfo = s.WaveTime + MathH.Rand(8.1f, 13.8f) - MathF.Min(2.4f, s.Level * 0.14f);
                }
                if (s.RaiderQuota > 0 && s.WaveTime >= s.NextRaider)
                {
                    WaveSystem.SpawnRaider(s);
                    s.RaiderQuota--;
                    s.NextRaider = s.WaveTime + MathH.Rand(9.5f, 15.5f) - MathF.Min(2.1f, s.Level * 0.1f);
                }
            }
        }

        // Assist (§5 3.1): enemies advance on a slowed clock (×0.8); player
        // missiles, timers and spawn schedule stay at full rate
        float enemyDt = s.Settings.AssistEnemySlow ? simDt * 0.8f : simDt;
        UpdEnemies(s, enemyDt);
        UpdUfo(s, enemyDt);
        UpdRaiders(s, enemyDt);
        DemonSystem.Update(s, enemyDt);
        MothershipSystem.Update(s, enemyDt);
        MothershipSystem.UpdateFighters(s, enemyDt);
        UpdPlayer(s, simDt);
        UpdExplosions(s, simDt);
        UpdParticles(s, fxDt);
        // Same gate as the GameOver branch: no collision resolution on frozen frames
        if (simDt > 0) Combat.RunCollisions(s);

        // Auto-defense AI (gated so it never decides on frozen state)
        if (s.Auto && simDt > 0) AutoDefense.RunAuto(s);

        // Phalanx CIWS turrets
        PhalanxSystem.UpdateAll(s, simDt);

        // HellRaiser underground launcher
        HellRaiserSystem.Update(s, simDt);

        // Weather
        WeatherSystem.Update(s, simDt);

        // Accessibility assists (§5 3.1) — any active assist marks the run
        if (s.Settings.AssistEnemySlow || s.Settings.AssistAutoEmp) s.AssistedRun = true;
        if (s.Settings.AssistAutoEmp && simDt > 0 && aliveCities == 1
            && s.Emp > 0 && s.EmpCd <= 0)
        {
            City? last = null;
            for (int i = 0; i < s.Cities.Count && last == null; i++)
                if (!s.Cities[i].Destroyed) last = s.Cities[i];
            if (last != null && AssistThreatNear(s, last))
            {
                // Combat.UseEMP detonates at the cursor — borrow it for one call
                float mx = s.MouseX, my = s.MouseY;
                s.MouseX = last.X;
                s.MouseY = s.GroundY - 90;
                if (Combat.UseEMP(s))
                {
                    s.Note = "ASSIST: auto-EMP deployed";
                    s.NoteT = 1.4f;
                }
                s.MouseX = mx;
                s.MouseY = my;
            }
        }

        // Wave cleared → shop. §5 5.3: DebrisParts no longer gate the clear —
        // debris is inert persistent ground litter now and would deadlock it.
        if (s.Phase == GamePhase.Playing
            && s.SpawnI >= s.WavePlan.Count
            && s.Enemies.Count == 0 && s.UFOs.Count == 0 && s.Raiders.Count == 0
            && s.Explosions.Count == 0 && s.Shockwaves.Count == 0
            // §5 6.1: a live boss holds the wave open. The Mothership masks this
            // via HoldSpawning (SpawnI never reaches WavePlan.Count), but the
            // Daemon has no such hold — without this gate a Daemon wave clears to
            // the Shop with the boss still alive, attacking, and unkillable
            // (LaunchPlayer is disabled in the Shop). Mirrors FeelDirector's
            // wave-final gate (FeelDirector.cs).
            && s.Demon == null && s.Mothership == null && s.Fighters.Count == 0)
        {
            s.Phase = GamePhase.Shop;
            s.ShopTimer = 18.0f;
            // §5 3.5 end-of-wave salvage: intact structures shed scrap for the shop
            // (recount here — the top-of-frame cache predates this frame's impacts)
            int salvage = 0;
            for (int i = 0; i < s.Cities.Count; i++) if (!s.Cities[i].Destroyed) salvage += 15;
            for (int i = 0; i < s.Bases.Count; i++) if (!s.Bases[i].Destroyed) salvage += 8;
            salvage = (int)MathF.Round(salvage * s.Perks.SalvageMult); // §5 4.3 SALVAGE RIGS
            if (salvage > 0)
            {
                s.Scrap += salvage;
                s.Note = $"Salvage recovered: +{salvage} scrap"; // once per wave — not a hot path
                s.NoteT = 2.0f;
            }
            // §5 6.2 report card: stamp the once-per-clear tallies and arm the
            // CLEARED stamp + count-up. AliveCities is fresh (recounted at the top
            // of UpdateAll and kept at destroy sites). The stamp animates over the
            // shop panel's opening beat.
            s.Wave.Salvage = salvage;
            s.Wave.CitiesSaved = s.AliveCities;
            s.WaveClearT = WaveClearHold;
            // §5 3.5 deterministic repairs: one free repair earned per 3 cleared
            // waves; banked (counter holds) while nothing is damaged
            s.Upgrades.WavesSinceFreeRepair++;
            if (s.Upgrades.WavesSinceFreeRepair >= 3 && WaveSystem.FreeRepair(s))
                s.Upgrades.WavesSinceFreeRepair = 0;
            // §5 4.1 intel forecast: build and pin the NEXT wave's plan (§4.3)
            WaveSystem.BuildForecast(s);
            // §5 4.3 perk draft: 3 seeded cards (own stream — never plan draws)
            PerkSystem.BuildDraft(s);
            s.Events.Emit(EventKind.WaveCleared, s.W * 0.5f, s.H * 0.5f, s.Level);
            Audio.SynthAudio.WaveCleared();
            Audio.SynthAudio.ShopWhoosh(open: true); // §5 4.5 shop-open answer
        }

        // Shop timer (UI countdown — real time)
        if (s.Phase == GamePhase.Shop)
        {
            s.ShopTimer -= rawDt;
            if (s.ShopTimer <= 0)
            {
                s.Phase = GamePhase.Playing;
                s.Level++;
                // §5 5.3: the wave's scorch history fades out across the wave
                // pause (the only decay path — marks accumulate otherwise)
                s.ScorchFadeT = 2.4f;
                Audio.SynthAudio.ShopWhoosh(open: false); // §5 4.5 close answer
                WaveSystem.StartWave(s, 2.9f);
            }
        }

        // Game over check → §5 6.3 ceremony (the GameOver tail is folded into it).
        // The run is dead: open the ceremony, slam a 120 ms freeze, and bend the
        // smouldering backdrop into slow-mo so the reveals breathe.
        if (aliveCities <= 0 && s.Phase == GamePhase.Playing)
            EnterCeremony(s);

        UpdateDanger(s);
        UpdateIntensity(s, simDt);
    }

    // §5 6.3: the single death edge (this and WaveSystem's no-target bail-out both
    // call here). Profile.OnGameOver still fires from Program.cs on the phase edge;
    // the initials ENTRY is gated until after the grade reveals (CeremonyInitialsArmed).
    public static void EnterCeremony(GameState s)
    {
        s.Phase = GamePhase.Ceremony;
        s.CeremonyT = 0;
        s.CeremonyGraded = false;
        s.CeremonyInitialsArmed = false;
        s.GameOverTime = 0;
        s.HitStop = MathF.Max(s.HitStop, Ceremony.Freeze); // §6.3 120 ms death freeze
        s.TimeScale = 0.3f;                                 // backdrop smoulders, not races
        s.TimeScaleTarget = 0.3f;
        Audio.SynthAudio.GameOver();
        s.Note = "Defense grid collapsed";
        s.NoteT = 2.2f;
    }

    // §5 6.3 ceremony timeline (rawDt — advances through the death freeze). Computes
    // the grade once at Ceremony.GradeAt with a Trauma pulse, then arms initials.
    static void UpdateCeremony(GameState s, float rawDt)
    {
        s.CeremonyT += rawDt;
        if (!s.CeremonyGraded && s.CeremonyT >= Ceremony.GradeAt)
        {
            s.CeremonyGraded = true;
            s.CeremonyGrade = Ceremony.ComputeGrade(s);
            s.AddTrauma(Ceremony.GradePulse); // §6.3 grade-stamp punch
        }
        // Once the grade has landed and the summary has dwelled, hand off to the
        // GameOver tail — the existing top-10 table / seed / initials / retry screen
        // (Profile.OnGameOver fires on that phase edge in Program.cs; it inserts the
        // row and arms the initials ceremony AFTER the grade, per the plan). Folded
        // in, not duplicated.
        if (s.CeremonyGraded && s.CeremonyT >= Ceremony.TailAt)
            HandoffToTail(s);
    }

    static void HandoffToTail(GameState s)
    {
        s.CeremonyInitialsArmed = true;
        s.Phase = GamePhase.GameOver;
        s.GameOverTime = 0;
    }

    /// <summary>§5 6.3: any keypress skips the current ceremony stage. Before the
    /// grade lands it fast-forwards to the summary (grade shown, count-ups
    /// complete); on/after the summary it advances straight to the GameOver tail.
    /// Called from Program.HandleInput.</summary>
    public static void SkipCeremony(GameState s)
    {
        if (s.CeremonyT < Ceremony.SummaryAt)
        {
            // First skip: land the grade immediately and jump to the summary —
            // the verdict (grade + counted-up stats) is shown and dwells there.
            if (!s.CeremonyGraded)
            {
                s.CeremonyGraded = true;
                s.CeremonyGrade = Ceremony.ComputeGrade(s);
                s.AddTrauma(Ceremony.GradePulse);
            }
            s.CeremonyT = Ceremony.SummaryAt;
        }
        else
        {
            // Second skip (already at the summary): advance to the GameOver tail.
            HandoffToTail(s);
        }
    }

    // §4.5 spawn-hold gate — tuned so only sustained, multi-source stress trips it
    const float SpawnHoldIntensity = 0.85f;

    /// <summary>§4.5 — THE shared tension scalar. Every term is clamped 0..1:
    ///   city     = RecentCityHits · 0.5      (DrainEvents adds 1 per city lost; exp decay τ≈6 s — 2 fresh losses saturate)
    ///   inbound  = hostiles / (6 + 2·aliveBases)   with hostiles = enemies + 2·(UFOs + raiders)
    ///   nearMiss = terminal · 0.25            (enemies past 80% of their flight — 4 about-to-land saturate)
    ///   scarcity = 1 − totalBaseAmmo / 60     (ready interceptor stock)
    ///   raw      = 0.34·city + 0.30·inbound + 0.20·nearMiss + 0.16·scarcity
    ///   Intensity → one-pole toward raw, τ ≈ 2 s. Runs on simDt only — frozen
    /// (hit-stop) frames never advance tension.</summary>
    static void UpdateIntensity(GameState s, float dt)
    {
        if (dt <= 0) return;
        s.RecentCityHits *= MathF.Exp(-dt / 6f);
        if (s.RecentCityHits < 0.01f) s.RecentCityHits = 0;

        float city = MathH.Clamp(s.RecentCityHits * 0.5f, 0, 1);
        int hostiles = s.Enemies.Count + (s.UFOs.Count + s.Raiders.Count) * 2;
        float inbound = MathH.Clamp(hostiles / (6f + 2f * s.AliveBases), 0, 1);
        int terminal = 0;
        for (int i = 0; i < s.Enemies.Count; i++)
        {
            var m = s.Enemies[i];
            if (m._Dur > 0 && m._Elapsed > m._Dur * 0.8f) terminal++;
        }
        float nearMiss = MathH.Clamp(terminal * 0.25f, 0, 1);
        int totalAmmo = 0;
        for (int i = 0; i < s.Bases.Count; i++)
            if (!s.Bases[i].Destroyed) totalAmmo += s.Bases[i].Ammo;
        float scarcity = 1f - MathH.Clamp(totalAmmo / 60f, 0, 1);

        float raw = 0.34f * city + 0.30f * inbound + 0.20f * nearMiss + 0.16f * scarcity;
        float k = 1f - MathF.Exp(-dt / 2f);
        s.Intensity += (MathH.Clamp(raw, 0, 1) - s.Intensity) * k;
    }

    // §5 6.2 wave-stinger timings (presentation seconds). The typewriter reveals
    // one glyph every IntroCharStep; the title finishes well inside the regular
    // 2.9 s WavePause, then lingers before self-completing. The cleared stamp's
    // count-up runs over WaveClearCountDur after the WaveClear arms WaveClearT.
    public const float IntroCharStep = 0.045f;
    public const float IntroLinger = 0.7f;
    public const float WaveClearCountDur = 1.1f;
    public const float WaveClearHold = 2.0f; // stamp + tallies stay up this long total

    /// <summary>§5 6.2 — advance the wave-intro typewriter (per-char synth tick)
    /// and the wave-cleared count-up. Both run on rawDt. The intro plays only on
    /// regular (non-boss) waves while the WavePause letterbox holds; boss waves
    /// keep their 6.1 banner and leave the typewriter idle. Any input collapse is
    /// applied in HandleInput via CollapseWaveIntro.</summary>
    static void UpdateWaveStinger(GameState s, float rawDt)
    {
        // Intro: arm only while the wave-pause window is open and the player has
        // not collapsed it. Boss waves (WaveIntroBoss) own the screen with the
        // boss banner, so the typewriter is suppressed there.
        if (s.Phase == GamePhase.Playing && s.WavePause > 0f
            && !s.WaveIntroBoss && !s.WaveIntroDone && s.WaveTitle.Length > 0)
        {
            int prevChars = (int)(s.WaveIntroT / IntroCharStep);
            s.WaveIntroT += rawDt;
            int nowChars = (int)(s.WaveIntroT / IntroCharStep);
            // One synth tick per newly-revealed NON-space glyph (spaces type
            // silently — reads as a real typewriter, not a metronome).
            int total = s.WaveTitle.Length;
            for (int c = prevChars; c < nowChars && c < total; c++)
                if (s.WaveTitle[c] != ' ') Audio.SynthAudio.UiClick();
            // Self-complete once the full title has typed and lingered.
            if (s.WaveIntroT >= total * IntroCharStep + IntroLinger)
                s.WaveIntroDone = true;
        }

        // Cleared stamp + count-up: armed at the wave-clear site; runs while the
        // shop is open (the stamp animates over the shop panel's first beat).
        if (s.WaveClearT > 0f)
            s.WaveClearT = MathF.Max(0f, s.WaveClearT - rawDt);
    }

    /// <summary>§5 6.2 — collapse the wave intro instantly on any input. Idempotent;
    /// marks it done so DrawWaveIntro stops rendering this frame (the stinger
    /// vanishes at once — "any input collapses it instantly"). The clamped
    /// WaveIntroT keeps the self-complete predicate consistent if re-queried.</summary>
    public static void CollapseWaveIntro(GameState s)
    {
        if (s.WaveIntroDone) return;
        s.WaveIntroT = s.WaveTitle.Length * IntroCharStep + IntroLinger;
        s.WaveIntroDone = true;
    }

    // --- Enemy Update ---
    static void UpdEnemies(GameState s, float dt)
    {
        for (int i = s.Enemies.Count - 1; i >= 0; i--)
        {
            var m = s.Enemies[i];
            m._Elapsed += dt;
            float p = m._Dur > 0 ? m._Elapsed / m._Dur : 1;

            // Split check — "split" variant and §5 4.2 plan-tagged MIRV heavies
            if ((m.Split || m.Mirv) && !m.HasSplit && p >= m.SplitAt)
            {
                Combat.SplitMissile(s, m);
                m.Dead = true;
                s.Enemies.RemoveAt(i);
                continue;
            }

            // Reached target
            if (p >= 1)
            {
                m.Dead = true;
                s.Enemies.RemoveAt(i);
                Combat.ImpactEnemy(s, m, m.Tx, m.Ty);
                continue;
            }

            // §5 4.2 behavioral roster — every act telegraphs ≥0.5 s ahead
            UpdBehaviors(s, m, p, dt);

            // Homing
            if (m.HomingFactor > 0 && m.Target != null)
            {
                float tx = m.Target.Value.X, ty = m.Target.Value.Y;
                float desired = MathF.Atan2(ty - m.Y, tx - m.X);
                float cur = MathF.Atan2(m._Vy, m._Vx);
                float diff = MathF.Atan2(MathF.Sin(desired - cur), MathF.Cos(desired - cur));
                float turn = MathH.Clamp(diff, -1, 1) * m.HomingFactor * dt * 2.2f;
                float sp = MathF.Sqrt(m._Vx * m._Vx + m._Vy * m._Vy);
                float na = cur + turn;
                m._Vx = MathF.Cos(na) * sp;
                m._Vy = MathF.Sin(na) * sp;
            }

            // Position from mPos logic
            float local = MathH.Clamp(m._Elapsed, 0, m._Dur);
            float pp = m._Dur > 0 ? local / m._Dur : 1;
            float x = m.Sx + m._Vx * local;
            float y = m.Sy + m._Vy * local;

            if (m.ZigAmp > 0)
                x += MathF.Sin(pp * m._Fq * MathH.TAU + m.ZigPhase) * m.ZigAmp * (1 - pp * 0.5f);
            if (m.Variant == "heavy")
                x += MathF.Sin(pp * MathH.TAU * 0.6f + m.Id) * 7;
            if (m.Variant == "cruise")
                y += MathF.Sin(pp * MathH.TAU * 1.2f + m.ZigPhase) * 18 * (1 - pp * 0.32f);
            if (m.Variant == "drone")
            {
                x += MathF.Sin(pp * MathH.TAU * 3.4f + m.ZigPhase) * 12 * (1 - pp * 0.16f);
                y += MathF.Cos(pp * MathH.TAU * 2.7f + m.ZigPhase * 0.8f) * 9 * (1 - pp * 0.22f);
            }

            m.X = x;
            m.Y = y;

            // Record trail position for curved trail rendering
            m.Trail.Push(m.X, m.Y);

            // Hit ground
            if (m.Y >= s.GroundY - 4)
            {
                m.Dead = true;
                s.Enemies.RemoveAt(i);
                Combat.ImpactEnemy(s, m, m.X, s.GroundY - 2);
            }
        }
    }

    // §5 4.2 telegraph lead — every behavior cues glow + audio this far ahead
    const float TelegraphLead = 0.6f;

    /// <summary>§5 4.2 behavioral roster: carrier deploy, MIRV warning, stealth
    /// decloak pings. Telegraph one-shots only fire while the sim advances
    /// (p is frozen at dt == 0, so a crossing always lands on a live frame).</summary>
    static void UpdBehaviors(GameState s, Enemy m, float p, float dt)
    {
        // Carrier: bay glow + servo ping ≥0.5 s before releasing 2-3 drones.
        // An early kill removes the carrier before the deploy point — spawn denied.
        if (m.Variant == "carrier" && !m._Deployed && m._Dur > 0)
        {
            if (!m.TelegraphPinged && (m.DeployAt - p) * m._Dur <= TelegraphLead)
            {
                m.TelegraphPinged = true;
                Audio.SynthAudio.CarrierBay(MathH.Clamp(m.X / s.W, 0, 1));
            }
            if (m.TelegraphPinged) m.TelegraphT += dt;
            if (p >= m.DeployAt)
            {
                m._Deployed = true;
                m.TelegraphT = 0;
                Combat.DeployDrones(s, m);
            }
        }

        // MIRV heavy: pulsing warning glow + warble ≥0.5 s before the split
        // (the split itself rides the shared SplitAt check in UpdEnemies)
        if (m.Mirv && !m.HasSplit && m._Dur > 0)
        {
            if (!m.TelegraphPinged && (m.SplitAt - p) * m._Dur <= TelegraphLead)
            {
                m.TelegraphPinged = true;
                Audio.SynthAudio.MirvWarble(MathH.Clamp(m.X / s.W, 0, 1));
            }
            if (m.TelegraphPinged) m.TelegraphT += dt;
        }

        // Stealth: periodic decloak ping (~1.4 s, cosmetic jitter) while cloaked —
        // a brief light burst + sonar blip gives skilled players a track
        if (m.Variant == "stealth")
        {
            m.PingT -= dt;
            if (m.PingT <= 0)
            {
                m.PingT = VariantStats.Def(m.Variant).CloakPing + MathH.Rand(-0.25f, 0.25f);
                // Same visibility curve the renderer uses — ping only while faded
                float vis = MathF.Pow(MathH.Clamp((m.Y + 100) / (s.GroundY + 100), 0, 1), 3) * 0.55f + 0.05f;
                if (vis < 0.4f)
                {
                    s.LightBursts.Add(new LightBurst
                    {
                        X = m.X, Y = m.Y,
                        Radius = 46, Life = 0.32f, MaxLife = 0.32f
                    });
                    Audio.SynthAudio.SonarBlip(MathH.Clamp(m.X / s.W, 0, 1));
                }
            }
        }
    }

    // --- UFO Update ---
    static void UpdUfo(GameState s, float dt)
    {
        for (int i = s.UFOs.Count - 1; i >= 0; i--)
        {
            var u = s.UFOs[i];
            u.X += u.Vx * dt;
            u.Y += MathF.Sin(s.Time * 2f + u.BobPhase) * dt * 12;
            u.FireCd -= dt;
            if (u.FireCd <= 0)
            {
                // Spawn UFO bomb
                var t = WaveSystem.ChooseTarget(s, "ufoBomb");
                if (t != null)
                    Combat.CreateEnemyProjectile(s, "ufoBomb", u.X + MathH.Rand(-20, 20), u.Y + 8, t.Value);
                u.FireCd = MathH.Rand(1.15f, 2.2f);
            }
            if ((u.Vx > 0 && u.X > s.W + 130) || (u.Vx < 0 && u.X < -130))
            {
                u.Dead = true;
                s.UFOs.RemoveAt(i);
            }
        }
    }

    // --- Raider Update ---
    static void UpdRaiders(GameState s, float dt)
    {
        for (int i = s.Raiders.Count - 1; i >= 0; i--)
        {
            var r = s.Raiders[i];
            r.FireCd -= dt;
            if (r.FireCd <= 0)
            {
                r.FireCd = MathH.Rand(0.55f, 1.25f);
                r.Vx = -r.Vx * MathH.Rand(0.9f, 1.22f);
                // Spit burst
                int burst = 3 + (RandHelper.Next01() < 0.45f ? 2 : 1);
                for (int j = 0; j < burst; j++)
                {
                    var t = WaveSystem.ChooseTarget(s, "spit");
                    if (t != null)
                        Combat.CreateEnemyProjectile(s, "spit", r.X + MathH.Rand(-20, 20), r.Y + 10, t.Value,
                            blastOverride: MathH.Rand(46, 78), ampOverride: MathH.Rand(10, 28), fqOverride: MathH.Rand(1.2f, 2.4f));
                }
            }
            r.X += r.Vx * dt;
            r.Y += MathF.Sin(s.Time * 2.7f + r.Angle) * dt * 24;
            r.Angle = MathF.Atan2(MathF.Cos(s.Time * 2.7f + r.Angle) * 24, r.Vx);

            if (r.X < -180 || r.X > s.W + 180)
            {
                r.Dead = true;
                s.Raiders.RemoveAt(i);
            }
        }
    }

    // --- Player Missiles Update ---
    static void UpdPlayer(GameState s, float dt)
    {
        for (int i = s.PlayerMissiles.Count - 1; i >= 0; i--)
        {
            var m = s.PlayerMissiles[i];
            m._Elapsed += dt;

            if (m.Hr)
            {
                // HellRaiser homing missile — velocity-based with turn rate
                m.HrRetarget -= dt;
                if (m.HrRetarget <= 0 || !HrTargetAlive(s, m))
                {
                    PickNewHrTarget(s, m);
                    m.HrRetarget = MathH.Rand(0.06f, 0.18f);
                }

                float ang = MathF.Atan2(m._Vy, m._Vx);

                // Steer toward target
                var aim = GetHrTargetPoint(s, m, MathH.Rand(0.03f, 0.12f));
                if (aim != null)
                {
                    float desired = MathF.Atan2(aim.Value.Y - m.Y, aim.Value.X - m.X);
                    float diff = MathF.Atan2(MathF.Sin(desired - ang), MathF.Cos(desired - ang));
                    ang += MathH.Clamp(diff, -m.HrTurn * dt, m.HrTurn * dt);
                }

                // Squiggle
                ang += MathF.Sin((s.Time + m.Id * 0.013f) * m.SquiggleFreq + m.SquigglePhase) * dt * 1.35f;

                float sp = m.HrSpeed;
                m._Vx = MathF.Cos(ang) * sp;
                m._Vy = MathF.Sin(ang) * sp;
                m.X += m._Vx * dt;
                m.Y += m._Vy * dt;

                // Out of bounds → expire
                if (m.X < -60 || m.X > s.W + 60 || m.Y < -40 || m.Y > s.GroundY + 28)
                    m._Elapsed = m._Dur;
            }
            else
            {
                // Normal player missile — parametric
                m.X = m.Sx + m._Vx * m._Elapsed;
                m.Y = m.Sy + m._Vy * m._Elapsed;
            }

            // Record trail position for curved trail rendering
            m.Trail.Push(m.X, m.Y);

            float p = m._Dur > 0 ? m._Elapsed / m._Dur : 1;
            if (p >= 1)
            {
                s.PlayerMissiles.RemoveAt(i);
                if (m.Hr)
                {
                    float ex = MathH.Clamp(m.X, 0, s.W);
                    float ey = MathH.Clamp(m.Y, 18, s.GroundY - 4);
                    Combat.SpawnExpl(s, ex, ey, m._Blast, 0.8f, 0.36f, player: true, flash: 0.05f);
                }
                else
                {
                    Combat.SpawnExpl(s, m.Tx, m.Ty, m._Blast, 1.28f, 0.36f, player: true, flash: 0.08f);
                }
            }
            // §5 4.3 MIRV INTERCEPTOR perk: base-launched interceptors shed 3
            // homing children at mid-flight (Hr missiles never re-split;
            // point-blank shots with sub-0.5 s flights don't split). Children
            // append past the loop start, so they update next frame.
            else if (s.Perks.MirvInterceptor && !m.Hr && p >= 0.5f && m._Dur >= 0.5f
                     && s.Phase == GamePhase.Playing)
            {
                s.PlayerMissiles.RemoveAt(i);
                Combat.SplitPlayerMissile(s, m);
            }
        }
    }

    /// <summary>Check if a HellRaiser missile's target is still alive (§5 2.6: direct references).</summary>
    static bool HrTargetAlive(GameState s, PlayerMissile m)
    {
        // Every Enemy removal path sets Dead; UFOs/Raiders can be removed by
        // PhalanxSystem without a flag, so verify membership too (lists are tiny).
        if (m.HrTargetEnemy != null) return !m.HrTargetEnemy.Dead;
        if (m.HrTargetUfo != null) return !m.HrTargetUfo.Dead && s.UFOs.Contains(m.HrTargetUfo);
        if (m.HrTargetRaider != null) return !m.HrTargetRaider.Dead && s.Raiders.Contains(m.HrTargetRaider);

        // Launch handoff: HellRaiserSystem passes kind+id once; adopt as a direct reference.
        if (m.HrTargetKind.Length == 0) return false;
        switch (m.HrTargetKind)
        {
            case "enemy":
                foreach (var e in s.Enemies)
                    if (e.Id == m.HrTargetId) { m.HrTargetEnemy = e; m.HrTargetKind = ""; return true; }
                break;
            case "ufo":
                foreach (var u in s.UFOs)
                    if (u.Id == m.HrTargetId) { m.HrTargetUfo = u; m.HrTargetKind = ""; return true; }
                break;
            case "raider":
                foreach (var r in s.Raiders)
                    if (r.Id == m.HrTargetId) { m.HrTargetRaider = r; m.HrTargetKind = ""; return true; }
                break;
        }
        m.HrTargetKind = "";
        return false;
    }

    /// <summary>Get the predicted position of a HellRaiser missile's target with lead.</summary>
    static (float X, float Y)? GetHrTargetPoint(GameState s, PlayerMissile m, float lead)
    {
        if (m.HrTargetEnemy is { } e) return (e.X + e._Vx * lead, e.Y + e._Vy * lead);
        if (m.HrTargetUfo is { } u) return (u.X + u.Vx * lead, u.Y);
        if (m.HrTargetRaider is { } r) return (r.X + r.Vx * lead, r.Y + MathF.Sin((s.Time + lead) * 2.7f + r.Angle) * 10);
        return null;
    }

    // Candidate weights for HellRaiser retargeting; 0 = excluded. Kept as separate
    // helpers so the two-pass pick below computes identical values in both passes.
    static float HrWeightEnemy(GameState s, PlayerMissile m, Enemy e)
    {
        if (e.Y > s.GroundY + 18) return 0;
        float dx = e.X - m.X, dy = e.Y - m.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist > 900) return 0;
        float distW = 1f / (0.38f + dist * 0.0034f);
        float baseW = 80 + (e.Target?.Type == "city" ? 46 : 0);
        return MathF.Max(1, baseW * distW);
    }

    static float HrWeightUfo(PlayerMissile m, UFO u)
    {
        float dx = u.X - m.X, dy = u.Y - m.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist > 900) return 0;
        float distW = 1f / (0.38f + dist * 0.0034f);
        return MathF.Max(1, (u.Boss ? 200 : 120) * distW);
    }

    static float HrWeightRaider(PlayerMissile m, Raider r)
    {
        float dx = r.X - m.X, dy = r.Y - m.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist > 900) return 0;
        float distW = 1f / (0.38f + dist * 0.0034f);
        return MathF.Max(1, 228 * distW);
    }

    /// <summary>Pick a new target for a HellRaiser homing missile using weighted selection.
    /// Two passes over the candidate lists replace the old per-retarget pool list —
    /// zero allocation, identical pick order/RNG consumption.</summary>
    static void PickNewHrTarget(GameState s, PlayerMissile m)
    {
        m.HrTargetEnemy = null;
        m.HrTargetUfo = null;
        m.HrTargetRaider = null;
        m.HrTargetKind = "";
        m.HrTargetId = -1;

        float total = 0;
        foreach (var e in s.Enemies) total += HrWeightEnemy(s, m, e);
        foreach (var u in s.UFOs) total += HrWeightUfo(m, u);
        foreach (var r in s.Raiders) total += HrWeightRaider(m, r);
        if (total <= 0) return;

        // Weighted random pick
        float roll = RandHelper.Next01() * total;
        float acc = 0;
        Enemy? lastE = null;
        UFO? lastU = null;
        Raider? lastR = null;
        foreach (var e in s.Enemies)
        {
            float w = HrWeightEnemy(s, m, e);
            if (w <= 0) continue;
            lastE = e;
            acc += w;
            if (roll <= acc) { m.HrTargetEnemy = e; return; }
        }
        foreach (var u in s.UFOs)
        {
            float w = HrWeightUfo(m, u);
            if (w <= 0) continue;
            lastU = u;
            acc += w;
            if (roll <= acc) { m.HrTargetUfo = u; return; }
        }
        foreach (var r in s.Raiders)
        {
            float w = HrWeightRaider(m, r);
            if (w <= 0) continue;
            lastR = r;
            acc += w;
            if (roll <= acc) { m.HrTargetRaider = r; return; }
        }
        // Float-rounding fallback — the last candidate in pool order (raiders last)
        if (lastR != null) m.HrTargetRaider = lastR;
        else if (lastU != null) m.HrTargetUfo = lastU;
        else m.HrTargetEnemy = lastE;
    }

    // --- Explosions Update ---
    static void UpdExplosions(GameState s, float dt)
    {
        for (int i = s.Explosions.Count - 1; i >= 0; i--)
        {
            var e = s.Explosions[i];
            e.Life -= dt;
            if (e.Life <= 0)
            {
                s.Explosions.RemoveAt(i);
                continue;
            }
            float elapsed = e.MaxLife - e.Life;
            e.Radius = Combat.ExplRadius(elapsed, e.MaxRadius, e.Shake, e.MaxLife);
        }
    }

    // --- Particles Update ---
    static void UpdParticles(GameState s, float dt)
    {
        // Sparks
        int scrapPickups = 0;
        for (int i = s.Sparks.Count - 1; i >= 0; i--)
        {
            var sp = s.Sparks[i];
            sp.Life -= dt;
            if (sp.Life <= 0)
            {
                if (sp.Target) scrapPickups++; // expiry lands on the counter (see below)
                s.Sparks.RemoveAt(i);
                continue;
            }
            if (sp.Target)
            {
                // §5 3.5 magnet-stream: scatter velocity fades out while an
                // accelerating position pull (∝ t²) takes over — converges on the
                // HUD counter within the spark's ~0.7 s life
                float t = 1 - sp.Life / sp.MaxLife;
                sp.X += sp.Vx * dt * (1 - t);
                sp.Y += sp.Vy * dt * (1 - t);
                float k = MathF.Min(1f, t * t * 22f * dt);
                sp.X += (s.ScrapHudX - sp.X) * k;
                sp.Y += (s.ScrapHudY - sp.Y) * k;
                float ddx = s.ScrapHudX - sp.X, ddy = s.ScrapHudY - sp.Y;
                if (ddx * ddx + ddy * ddy < 12f * 12f)
                {
                    scrapPickups++;
                    s.Sparks.RemoveAt(i);
                    continue;
                }
            }
            else if (sp.Kind == SparkKind.Hot)
            {
                // §5 5.3 white-hot core: violent start, high drag on both axes
                sp.X += sp.Vx * dt;
                sp.Y += sp.Vy * dt;
                sp.Vx *= 0.86f;
                sp.Vy *= 0.86f;
            }
            else if (sp.Kind == SparkKind.Ember)
            {
                // §5 5.3 ember: heavier gravity; settles and cools where it lands
                sp.Vy += 260 * dt;
                sp.X += sp.Vx * dt;
                sp.Y += sp.Vy * dt;
                sp.Vx *= 0.988f;
                if (sp.Y > s.GroundY - 2)
                {
                    sp.Y = s.GroundY - 2;
                    sp.Vx *= 0.6f;
                    sp.Vy = 0;
                }
            }
            else
            {
                sp.Vy += 140 * dt;
                sp.X += sp.Vx * dt;
                sp.Y += sp.Vy * dt;
                sp.Vx *= 0.994f;
            }
            s.Sparks[i] = sp;
        }
        // Subtle pickup tick per arrival batch (existing voice, rate-limited,
        // panned toward the bottom-left HUD)
        if (scrapPickups > 0 && s.ScrapTickCd <= 0)
        {
            s.ScrapTickCd = 0.1f;
            Audio.SynthAudio.Incoming(0.12f, 0.2f);
        }

        // Smoke
        for (int i = s.SmokeParts.Count - 1; i >= 0; i--)
        {
            var sm = s.SmokeParts[i];
            sm.Life -= dt;
            if (sm.Life <= 0) { s.SmokeParts.RemoveAt(i); continue; }
            float p = 1 - sm.Life / sm.MaxLife;
            sm.X += sm.Vx * dt * (0.5f + p * 0.7f);
            sm.Y += sm.Vy * dt * (0.7f + p * 0.45f);
            sm.Vx *= 0.99f;
            s.SmokeParts[i] = sm;
        }

        // Trails
        for (int i = s.Trails.Count - 1; i >= 0; i--)
        {
            var tr = s.Trails[i];
            tr.Life -= dt;
            if (tr.Life <= 0) { s.Trails.RemoveAt(i); continue; }
            tr.X += tr.Vx * dt;
            tr.Y += tr.Vy * dt;
            tr.Vy += 35 * dt;
            tr.Vx *= 0.99f;
            s.Trails[i] = tr;
        }

        // Debris (§5 5.3 permanence): falls, bounces ONCE (restitution ~0.3),
        // then rests as ground litter for the rest of the wave. Life is only
        // the cooling clock — removal is wave start or the oldest-evicted cap.
        for (int i = s.DebrisParts.Count - 1; i >= 0; i--)
        {
            var d = s.DebrisParts[i];
            if (d.Life > 0)
            {
                d.Life = MathF.Max(0, d.Life - dt);
                if (d.Resting) { s.DebrisParts[i] = d; continue; }
            }
            else if (d.Resting) continue; // cooled litter: fully inert
            d.Vy += 360 * dt;
            d.X += d.Vx * dt;
            d.Y += d.Vy * dt;
            d.Rot += d.RotSpeed * dt;
            d.Vx *= 0.992f;
            if (d.Y > s.GroundY - 2 && d.Vy > 0)
            {
                d.Y = s.GroundY - 2;
                if (!d.Bounced)
                {
                    d.Bounced = true;
                    d.Vy *= -0.3f;
                    d.Vx *= 0.7f;
                    d.RotSpeed *= 0.5f;
                }
                else
                {
                    d.Resting = true;
                    d.Vx = 0; d.Vy = 0; d.RotSpeed = 0;
                }
            }
            s.DebrisParts[i] = d;
        }

        // Shockwaves
        for (int i = s.Shockwaves.Count - 1; i >= 0; i--)
        {
            var sw = s.Shockwaves[i];
            sw.Life -= dt;
            if (sw.Life <= 0) { s.Shockwaves.RemoveAt(i); continue; }
            float p = 1 - sw.Life / sw.MaxLife;
            sw.Radius = MathH.Lerp(8, sw.MaxRadius, MathH.EaseOut(p));
            s.Shockwaves[i] = sw;
        }

        // Light bursts
        for (int i = s.LightBursts.Count - 1; i >= 0; i--)
        {
            var lb = s.LightBursts[i];
            lb.Life -= dt;
            if (lb.Life <= 0) { s.LightBursts.RemoveAt(i); continue; }
            s.LightBursts[i] = lb;
        }

        // Muzzle flashes
        for (int i = s.MuzzleFlashes.Count - 1; i >= 0; i--)
        {
            var mf = s.MuzzleFlashes[i];
            mf.Life -= dt;
            if (mf.Life <= 0) { s.MuzzleFlashes.RemoveAt(i); continue; }
            s.MuzzleFlashes[i] = mf;
        }

        // Scorches (§5 5.3): marks hold all wave (only Heat — the lingering
        // ground glow — cools); the shop-close ScorchFadeT window is the one
        // place Life drains, staggering them out across the wave pause.
        bool scorchFade = s.ScorchFadeT > 0;
        if (scorchFade) s.ScorchFadeT = MathF.Max(0, s.ScorchFadeT - dt);
        for (int i = s.Scorches.Count - 1; i >= 0; i--)
        {
            var sc = s.Scorches[i];
            if (sc.Heat > 0) sc.Heat = MathF.Max(0, sc.Heat - dt * 0.4f);
            if (scorchFade)
            {
                sc.Life -= dt * 5f;
                if (sc.Life <= 0) { s.Scorches.RemoveAt(i); continue; }
            }
            s.Scorches[i] = sc;
        }

        // Blast flash quads (§5 5.3) — die within a frame or two
        for (int i = s.BlastFlashes.Count - 1; i >= 0; i--)
        {
            var bf = s.BlastFlashes[i];
            bf.Life -= dt;
            if (bf.Life <= 0) { s.BlastFlashes.RemoveAt(i); continue; }
            s.BlastFlashes[i] = bf;
        }

        // §5 5.3 smoke columns: city ruins wisp continuously. Rate-limited by
        // RuinSmokeCd; the cap below MaxSmoke reserves headroom for blasts.
        s.RuinSmokeCd -= dt;
        if (s.RuinSmokeCd <= 0)
        {
            s.RuinSmokeCd = 0.6f;
            const int ruinSmokeCap = 120;
            foreach (var c in s.Cities)
            {
                if (!c.Destroyed || s.SmokeParts.Count >= ruinSmokeCap) continue;
                float life = MathH.Rand(3.2f, 5.6f);
                s.SmokeParts.Add(new Smoke
                {
                    X = c.X + MathH.Rand(-0.28f, 0.28f) * c.W,
                    Y = c.Y - MathH.Rand(4, 24),
                    Vx = MathH.Rand(-6, 6) + s.Weather.Wind * 0.08f,
                    Vy = -MathH.Rand(10, 22),
                    Life = life, MaxLife = life,
                    Size = MathH.Rand(9, 16),
                    Alpha = MathH.Rand(0.28f, 0.42f), // column reads, blasts stay darker
                    Blend = BlendClass.Alpha
                });
            }
        }

        // Shooting stars
        for (int i = s.ShootingStars.Count - 1; i >= 0; i--)
        {
            var ss = s.ShootingStars[i];
            ss.Life -= dt;
            ss.X += ss.Vx * dt;
            ss.Y += ss.Vy * dt;
            if (ss.Life <= 0 || ss.Y > s.HorizonY) { s.ShootingStars.RemoveAt(i); continue; }
            s.ShootingStars[i] = ss;
        }
        // Spawn new shooting stars occasionally
        if (s.ShootingStars.Count < 2 && RandHelper.Next01() < dt * 0.04f)
        {
            float sx = MathH.Rand(s.W * 0.1f, s.W * 0.9f);
            float sy = MathH.Rand(20, s.HorizonY * 0.4f);
            float ang = MathH.Rand(0.2f, 0.8f);
            float speed = MathH.Rand(400, 800);
            s.ShootingStars.Add(new ShootingStar
            {
                X = sx, Y = sy,
                Vx = MathF.Cos(ang) * speed,
                Vy = MathF.Sin(ang) * speed,
                Life = MathH.Rand(0.4f, 0.9f),
                MaxLife = MathH.Rand(0.4f, 0.9f),
                Length = speed * 0.02f
            });
        }
    }

    /// <summary>True when an enemy projectile is close enough to the last city to
    /// justify the assist auto-EMP (§5 3.1).</summary>
    static bool AssistThreatNear(GameState s, City c)
    {
        const float reach = 250f;
        foreach (var m in s.Enemies)
        {
            float dx = m.X - c.X, dy = m.Y - (s.GroundY - 30);
            if (dx * dx + dy * dy < reach * reach) return true;
        }
        return false;
    }

    static void UpdateDanger(GameState s)
    {
        if (s.Phase != GamePhase.Playing && s.Phase != GamePhase.Shop) { s.Danger = 0; return; }
        // Fresh post-collision count (plain loop) — the top-of-frame cache predates this frame's kills
        int aliveC = 0;
        for (int i = 0; i < s.Cities.Count; i++) if (!s.Cities[i].Destroyed) aliveC++;
        s.AliveCities = aliveC;
        int alive = Math.Max(1, aliveC);
        float d = 0;
        d += s.Enemies.Count * 12;
        d += (s.WavePlan.Count - s.SpawnI) * 1.8f;
        d += s.UFOs.Count * 52;
        d += s.Raiders.Count * 66;
        if (s.Demon != null) d += 82;
        s.Danger = MathH.Clamp(d / (alive * 170), 0, 1);
    }
}
