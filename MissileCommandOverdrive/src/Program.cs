using Raylib_cs;
using MissileCommandOverdrive;
using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

const int InitialWidth = 1280;
const int InitialHeight = 720;

// §5 3.2 determinism self-check (MCOD_SELFTEST=1): runs headless before
// InitWindow, prints PASS/FAIL, exits.
if (Environment.GetEnvironmentVariable("MCOD_SELFTEST") == "1")
{
    Environment.Exit(SelfTest.Run() ? 0 : 1);
}

Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.Msaa4xHint);
Raylib.InitWindow(InitialWidth, InitialHeight, "Missile Command Overdrive");
Raylib.SetTargetFPS(60);
Raylib.SetExitKey(KeyboardKey.Null); // prevent ESC from closing

// Use nearest-neighbor filtering for crisp pixel art rendering
Raylib.SetTextureFilter(Raylib.GetFontDefault().Texture, TextureFilter.Point);

var S = new GameState
{
    W = InitialWidth,
    H = InitialHeight
};

RandHelper.Bind(S); // §4.3: the cosmetic stream lives on GameState
Profile.Load();     // §4.2: profile (settings/top-10/lifetime) loads before world init
S.Settings = Profile.Data.Settings; // same object — live settings edits persist on save

Resize(S);
GameInit.BuildWorld(S);
SynthAudio.Init();
SynthAudio.SetVolume(S.Settings.Volume); // Settings owns the master level (§5 3.1)

// §4.10 resize debounce: world geometry tracks the drag every frame (cheap),
// but the RNG scenery/weather rebuild waits until the size has been stable.
const float ResizeSettleDelay = 0.25f;
float resizeSettleT = 0f;
var prevPhase = S.Phase; // for the game-over edge (profile insert + initials arm)

while (!Raylib.WindowShouldClose() && !S.QuitRequested)
{
    // Handle resize
    if (Raylib.IsWindowResized())
    {
        S.W = Raylib.GetScreenWidth();
        S.H = Raylib.GetScreenHeight();
        Resize(S);
        resizeSettleT = ResizeSettleDelay;
    }

    // §4.7 time spine: rawDt drives UI/floaters/shake/audio, simDt the sim
    float rawDt = Raylib.GetFrameTime();
    if (rawDt > 1f / 30f) rawDt = 1f / 30f;

    if (resizeSettleT > 0)
    {
        resizeSettleT -= rawDt;
        if (resizeSettleT <= 0) GameInit.RebuildScenery(S);
    }

    // Input
    var mp = Raylib.GetMousePosition();
    S.MouseX = mp.X;
    S.MouseY = mp.Y;
    if (DemoDriver.Active) DemoDriver.PinCursor(S); // scripted runs ignore the OS cursor

    HandleInput(S, rawDt);
    // §5 3.1 phase gate: Paused freezes the sim AND the feel clocks (hit-stop,
    // trauma noise, time-scale ease all hold), never via HitStop.
    switch (S.Phase)
    {
        case GamePhase.Title:
            // §5 6.4: the title is a living, auto-played seeded backdrop wave —
            // AttractSystem promotes to Playing for the UpdateAll call, restores
            // Title, and re-seeds endlessly so the screen never stalls.
            FeelDirector.Tick(S, rawDt);
            AttractSystem.UpdateTitle(S, rawDt);
            break;
        case GamePhase.Paused:
            break; // world frozen; audio still pumps below (§4.6)
        default:
            FeelDirector.Tick(S, rawDt);
            float simDt = S.HitStop > 0 ? 0f : rawDt * S.TimeScale;
            GameUpdate.UpdateAll(S, simDt, rawDt);
            break;
    }
    DrainEvents(S);
    // §5 3.2 / 6.3: run-ended edge — fires when the ceremony hands off to the
    // GameOver tail (Ceremony → GameOver). Inserts the top-10 row and arms the
    // initials ceremony, so initials are sequenced AFTER the letter grade.
    if (S.Phase == GamePhase.GameOver && prevPhase != GamePhase.GameOver)
        Profile.OnGameOver(S);
    prevPhase = S.Phase;
    SynthAudio.Update(S, rawDt, S.TimeScale);

    Raylib.BeginDrawing();
    Raylib.ClearBackground(new Color(2, 5, 10, 255));
    MissileCommandOverdrive.Rendering.Renderer.DrawAll(S);
    Raylib.EndDrawing();

    if (DemoDriver.Active && !DemoDriver.Update(S)) break;
}

Profile.Save(); // §4.2: settings + lifetime stats persist on shutdown
MissileCommandOverdrive.Rendering.Renderer.Shutdown();
SynthAudio.Shutdown();
Raylib.CloseWindow();

// --- Core functions ---

// Drain the per-frame event ring — the single consumer loop. Later features
// (SynthAudio reactions, StatsTracker) plug in here, before Clear().
static void DrainEvents(GameState s)
{
    var ring = s.Events;
    for (int i = 0; i < ring.Count; i++)
    {
        ref readonly var e = ref ring.At(i);
        s.Debug.EventCounts[(int)e.Kind]++;
        // §5 6.2 report-card tallies — fed straight off the bus (the AOT-clean
        // WaveStats struct that replaced the boxing telemetry dictionary).
        switch (e.Kind)
        {
            // §5 6.3: RunKills/RunLeaks are the run-wide totals the ceremony grades
            // on (WaveStats resets every wave; the grade needs the whole run).
            case EventKind.Kill: s.Wave.Kills++; s.RunKills++; break;
            case EventKind.GroundImpact: s.Wave.Leaks++; s.RunLeaks++; break;
            case EventKind.CityDestroyed: s.Wave.CitiesLost++; break;
            case EventKind.BaseDestroyed: s.Wave.BasesLost++; break;
        }
        // Lifetime stats (§5 3.2) — persisted with the profile. §5 6.4: the
        // Title backdrop is an auto-played attract wave, NOT a real run — its
        // kills/clears must never inflate the saved lifetime totals.
        if (!s.Intro)
        {
            if (e.Kind == EventKind.Kill) Profile.Data.Lifetime.Kills++;
            else if (e.Kind == EventKind.WaveCleared) Profile.Data.Lifetime.WavesCleared++;
        }
        // Tension input + boss cues are INDEPENDENT of the lifetime gate above
        // (they must fire during real play, where s.Intro is false) — keep them
        // out of that if/else chain.
        switch (e.Kind)
        {
            // §4.5 tension input: city losses feed the decaying accumulator behind
            // s.Intensity (GameUpdate.UpdateIntensity owns the decay + blend)
            case EventKind.CityDestroyed:
                s.RecentCityHits += 1f;
                break;
            // §5 6.1 boss audio cues (calling the synth — not editing it)
            case EventKind.BossPhase:
                SynthAudio.Thunder(MathH.Clamp(e.X / s.W, 0, 1), 0.7f);
                break;
            case EventKind.BossDeath:
                SynthAudio.Thunder(MathH.Clamp(e.X / s.W, 0, 1), 1.0f);
                SynthAudio.Impact(MathH.Clamp(e.X / s.W, 0, 1), heavy: true);
                break;
        }
        FeelDirector.OnEvent(s, in e);
    }
    ring.Clear();
}

// §5 4.5: one denied-buzz path for every refused shop interaction
static void ShopDeny(GameState s, string msg)
{
    SynthAudio.UiDeny();
    s.Msg = msg;
    s.MsgT = 1.0f;
}

static void Resize(GameState s)
{
    s.GroundY = s.H * 0.82f;
    s.HorizonY = s.H * 0.38f;
    // Reposition defenses for new dimensions (scenery rebuild is debounced — §4.10)
    if (s.Bases.Count > 0)
        GameInit.Reposition(s);
}

static void HandleInput(GameState s, float rawDt)
{
    // ----- INITIALS ENTRY (§5 3.2) — swallows all input until confirmed -----
    if (s.Phase == GamePhase.GameOver && Profile.PendingInitials)
    {
        Profile.UpdateInitialsEntry(s, rawDt);
        return;
    }

    // ----- §5 6.3 CEREMONY — every stage is skippable: any key fast-forwards to
    // the summary, then to the GameOver tail. Mouse is ignored so a click that
    // killed the last city doesn't instantly blow past the grade reveal.
    if (s.Phase == GamePhase.Ceremony)
    {
        if (Raylib.GetKeyPressed() != 0) GameUpdate.SkipCeremony(s);
        return; // no gameplay input while the run grades out
    }

    // ----- TITLE / ATTRACT (§5 6.4) — the title menu + idle demo own all input;
    // there is no gameplay underneath to fall through to (a left-click here is a
    // menu confirm, not a fire). START/D begin a run, SETTINGS opens the pause
    // panel, QUIT exits, any input wakes the demo back to the menu. -----
    if (s.Phase == GamePhase.Title)
    {
        AttractSystem.HandleInput(s);
        return;
    }

    // ----- PAUSE (§5 3.1; ESC is free — SetExitKey(Null) above) -----
    if (s.Phase == GamePhase.Paused)
    {
        Menu.Update(s); // swallows all input, incl. ESC-to-resume
        return;
    }
    if (Raylib.IsKeyPressed(KeyboardKey.Escape)
        && (s.Phase == GamePhase.Playing || s.Phase == GamePhase.Shop))
    {
        Menu.Open(s);
        return;
    }
    // ----- GAME OVER → TITLE (§5 6.4) — ESC from the death tail returns to the
    // living title/attract (R still restarts straight into a fresh run). The
    // initials ceremony swallows input above until confirmed, so this only fires
    // once the run has fully graded out. -----
    if (s.Phase == GamePhase.GameOver && Raylib.IsKeyPressed(KeyboardKey.Escape))
    {
        AttractSystem.Enter(s);
        return;
    }

    // ----- SECRET CODE BUFFER (666 -> summon demon) -----
    // Accept digits 0-9 and letters a-z, keep last 8 chars. Playing only —
    // §5 4.3 audit note: shop draft/buy digits must never type a summon code.
    if (s.Phase == GamePhase.Playing)
    {
        int keyCh = Raylib.GetCharPressed();
        while (keyCh > 0)
        {
            if ((keyCh >= '0' && keyCh <= '9') || (keyCh >= 'a' && keyCh <= 'z') || (keyCh >= 'A' && keyCh <= 'Z'))
            {
                SecretCode.Buffer += char.ToLowerInvariant((char)keyCh);
                if (SecretCode.Buffer.Length > 8) SecretCode.Buffer = SecretCode.Buffer.Substring(SecretCode.Buffer.Length - 8);
                if (SecretCode.Buffer.EndsWith("666"))
                {
                    DemonSystem.Summon(s);
                    SecretCode.Buffer = "";
                }
                else if (SecretCode.Buffer.EndsWith("777"))
                {
                    MothershipSystem.Summon(s);
                    SecretCode.Buffer = "";
                }
            }
            keyCh = Raylib.GetCharPressed();
        }
    }

    // ----- WAVE INTRO COLLAPSE (§5 6.2) — any input skips the typewriter
    // stinger instantly. Checked before the gameplay actions below so the very
    // click/key that collapses it also performs its normal action (fire, EMP…).
    if (s.Phase == GamePhase.Playing && !s.WaveIntroDone
        && (Raylib.IsMouseButtonPressed(MouseButton.Left)
            || Raylib.IsMouseButtonPressed(MouseButton.Right)
            || Raylib.GetKeyPressed() != 0))
    {
        GameUpdate.CollapseWaveIntro(s);
    }

    // ----- SHOP INPUT (between waves; §5 3.5 — spends SCRAP, Score stays
    // leaderboard-pure: buying upgrades can never lower the final score) -----
    if (s.Phase == GamePhase.Shop)
    {
        // §5 4.3 perk draft: 1-3 install a card, R rerolls once for 150 scrap.
        // This block RETURNS below, before the global R-restart handler — a
        // shop reroll can never restart the run. The seven scrap buys moved to
        // 4-0 so the draft owns the 1-3 muscle-memory row (README updated).
        if (Raylib.IsKeyPressed(KeyboardKey.One)) PerkSystem.TryPick(s, 0);
        else if (Raylib.IsKeyPressed(KeyboardKey.Two)) PerkSystem.TryPick(s, 1);
        else if (Raylib.IsKeyPressed(KeyboardKey.Three)) PerkSystem.TryPick(s, 2);
        else if (Raylib.IsKeyPressed(KeyboardKey.R)) PerkSystem.TryReroll(s);
        // §5 4.5 vocabulary: every buy key answers — confirm arp on success,
        // denied buzz when scrap is short / the item is capped or pointless.
        else if (Raylib.IsKeyPressed(KeyboardKey.Four))
        {
            if (s.Scrap < 500) ShopDeny(s, "Insufficient scrap");
            else
            {
                var dead = s.Cities.Where(c => c.Destroyed).ToList();
                if (dead.Count > 0)
                {
                    s.Scrap -= 500;
                    var c = RandHelper.Pick(dead); // §4.3: cosmetic stream
                    c.Destroyed = false;
                    s.AliveCities++;
                    SynthAudio.UiConfirm();
                    s.Msg = "City rebuilt"; s.MsgT = 1.2f;
                }
                else ShopDeny(s, "All cities intact");
            }
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Five))
        {
            if (s.Scrap < 250) ShopDeny(s, "Insufficient scrap");
            else if (s.Emp < s.EmpMax)
            {
                s.Scrap -= 250;
                s.Emp++;
                SynthAudio.UiConfirm();
                s.Msg = "EMP +1"; s.MsgT = 1.0f;
            }
            else ShopDeny(s, "EMP at maximum capacity");
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Six))
        {
            if (s.Scrap < 400) ShopDeny(s, "Insufficient scrap");
            else if (s.Upgrades.BlastScale < 2.8f - 0.001f)
            {
                s.Scrap -= 400;
                s.Upgrades.BlastScale = MathF.Min(2.8f, s.Upgrades.BlastScale + 0.2f);
                SynthAudio.UiConfirm();
                s.Msg = $"Warhead Yield x{s.Upgrades.BlastScale:F1}"; s.MsgT = 1.2f;
            }
            else ShopDeny(s, "Warhead yield at maximum");
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Seven))
        {
            if (s.Scrap < 350) ShopDeny(s, "Insufficient scrap");
            else if (s.Upgrades.ReloadMult < 2.2f - 0.001f)
            {
                s.Scrap -= 350;
                s.Upgrades.ReloadMult = MathF.Min(2.2f, s.Upgrades.ReloadMult + 0.12f);
                SynthAudio.UiConfirm();
                s.Msg = $"Reload Boost x{s.Upgrades.ReloadMult:F2}"; s.MsgT = 1.2f;
            }
            else ShopDeny(s, "Reload boost at maximum");
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Eight))
        {
            if (s.Scrap < 360) ShopDeny(s, "Insufficient scrap");
            else if (s.Upgrades.EmpScale < 2.4f - 0.001f)
            {
                s.Scrap -= 360;
                s.Upgrades.EmpScale = MathF.Min(2.4f, s.Upgrades.EmpScale + 0.14f);
                s.Upgrades.PhalanxEff = MathF.Min(2.0f, s.Upgrades.PhalanxEff + 0.08f);
                SynthAudio.UiConfirm();
                s.Msg = "EMP/Phalanx Boost"; s.MsgT = 1.2f;
            }
            else ShopDeny(s, "Amplifier at maximum");
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Nine))
        {
            if (s.Scrap < 300) ShopDeny(s, "Insufficient scrap");
            else
            {
                // §5 3.5 purchasable repair — ammo restored by the next StartWave
                Base? deadBase = null;
                foreach (var b in s.Bases) if (b.Destroyed) { deadBase = b; break; }
                if (deadBase != null)
                {
                    s.Scrap -= 300;
                    deadBase.Destroyed = false;
                    s.AliveBases++;
                    SynthAudio.UiConfirm();
                    s.Msg = "Launch base repaired"; s.MsgT = 1.2f;
                }
                else ShopDeny(s, "All bases operational");
            }
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Zero))
        {
            if (s.Scrap < 250) ShopDeny(s, "Insufficient scrap");
            else
            {
                Phalanx? deadPhx = null;
                foreach (var p in s.Phalanxes) if (p.Destroyed) { deadPhx = p; break; }
                if (deadPhx != null)
                {
                    s.Scrap -= 250;
                    deadPhx.Destroyed = false;
                    SynthAudio.UiConfirm();
                    s.Msg = "Phalanx CIWS repaired"; s.MsgT = 1.2f;
                }
                else ShopDeny(s, "Phalanx batteries operational");
            }
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Space))
        {
            s.Phase = GamePhase.Playing;
            s.Level++;
            SynthAudio.ShopWhoosh(open: false); // §5 4.5 close answer
            WaveSystem.StartWave(s, 2.9f);
        }
        return; // while shop open, suppress other gameplay inputs
    }

    // Fire interceptor
    if (Raylib.IsMouseButtonPressed(MouseButton.Left) && s.Phase == GamePhase.Playing)
    {
        Combat.LaunchPlayer(s, s.MouseX, s.MouseY);
    }

    // EMP
    if ((Raylib.IsMouseButtonPressed(MouseButton.Right) || Raylib.IsKeyPressed(KeyboardKey.E))
        && s.Phase == GamePhase.Playing)
    {
        Combat.UseEMP(s);
    }

    // Toggle auto defense
    if (Raylib.IsKeyPressed(KeyboardKey.C))
    {
        s.Auto = !s.Auto;
        s.Msg = s.Auto ? "Auto Defense ON" : "Auto Defense OFF";
        s.MsgT = 1.2f;
    }

    // Hell Raiser
    if (Raylib.IsKeyPressed(KeyboardKey.H))
    {
        HellRaiserSystem.Toggle(s);
    }

    // Theme toggle
    if (Raylib.IsKeyPressed(KeyboardKey.T))
    {
        s.Theme = s.Theme switch
        {
            "modern" => "xbox",
            "xbox" => "recharged",
            _ => "modern"
        };
        s.Msg = $"Theme: {s.Theme.ToUpperInvariant()}";
        s.MsgT = 1.2f;
    }

    // Restart
    if (Raylib.IsKeyPressed(KeyboardKey.R))
    {
        GameInit.ResetGame(s);
    }

    // Level skip
    if (Raylib.IsKeyPressed(KeyboardKey.RightBracket) || Raylib.IsKeyPressed(KeyboardKey.PageUp))
    {
        if (s.Phase is GamePhase.Title or GamePhase.GameOver) GameInit.ResetGame(s);
        s.Phase = GamePhase.Playing;
        s.Level = Math.Max(1, s.Level + 1);
        WaveSystem.StartWave(s, 0.7f);
        s.Msg = $"Jumped to Wave {s.Level}";
        s.MsgT = 1.2f;
    }
    if (Raylib.IsKeyPressed(KeyboardKey.LeftBracket) || Raylib.IsKeyPressed(KeyboardKey.PageDown))
    {
        if (s.Phase is GamePhase.Title or GamePhase.GameOver) GameInit.ResetGame(s);
        s.Phase = GamePhase.Playing;
        s.Level = Math.Max(1, s.Level - 1);
        WaveSystem.StartWave(s, 0.7f);
        s.Msg = $"Jumped to Wave {s.Level}";
        s.MsgT = 1.2f;
    }

    // Debug
    if (Raylib.IsKeyPressed(KeyboardKey.F8))
    {
        s.Debug.Enabled = !s.Debug.Enabled;
        s.Msg = s.Debug.Enabled ? "Debug telemetry ON" : "Debug telemetry OFF";
        s.MsgT = 1.0f;
    }

    // Mute toggle
    if (Raylib.IsKeyPressed(KeyboardKey.M))
    {
        SynthAudio.ToggleMute();
        s.Msg = SynthAudio.IsMuted ? "Audio MUTED" : "Audio ON";
        s.MsgT = 1.0f;
    }

    // §5 6.4: the title menu, daily-seed (D) and start-run handling moved into
    // AttractSystem.HandleInput (the Title block returns early above).
}

// Update is now handled by GameUpdate.UpdateAll()

// Drawing is now handled by Rendering.Renderer.DrawAll()

/// <summary>MCOD_SELFTEST=1 (§5 3.2 acceptance): same (seed, wave) must produce an
/// identical wave plan — guards against stray RNG calls leaking into BuildPlan (R4).</summary>
static class SelfTest
{
    public static bool Run()
    {
        bool ok = true;

        ulong[] seeds = [0xDEADBEEFCAFEBABEUL, 0x12345678UL, SeedUtil.DailySeed()];
        int[] waves = [1, 3, 8, 16];
        foreach (ulong seed in seeds)
        {
            foreach (int wave in waves)
            {
                var r1 = new Xoshiro(seed ^ (ulong)wave);
                var r2 = new Xoshiro(seed ^ (ulong)wave);
                var p1 = WaveSystem.BuildPlan(wave, ref r1);
                var p2 = WaveSystem.BuildPlan(wave, ref r2);
                if (!PlansEqual(p1, p2))
                {
                    Console.WriteLine($"FAIL: plan mismatch seed={seed:X16} wave={wave}");
                    ok = false;
                }
            }
        }

        // Different seeds must not collapse to one plan (sanity, not exhaustive)
        var ra = new Xoshiro(1UL ^ 5UL);
        var rb = new Xoshiro(2UL ^ 5UL);
        if (PlansEqual(WaveSystem.BuildPlan(5, ref ra), WaveSystem.BuildPlan(5, ref rb)))
        {
            Console.WriteLine("FAIL: different seeds produced identical plans");
            ok = false;
        }

        // Daily seed: FNV-1a64, stable within the same UTC day, never zero
        if (SeedUtil.DailySeed() != SeedUtil.DailySeed() || SeedUtil.DailySeed() == 0)
        {
            Console.WriteLine("FAIL: daily seed unstable");
            ok = false;
        }

        Console.WriteLine(ok ? "SELFTEST PASS" : "SELFTEST FAIL");
        return ok;
    }

    static bool PlansEqual(List<WavePlanEntry> a, List<WavePlanEntry> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Variant != b[i].Variant || a[i].Time != b[i].Time || a[i].Lane != b[i].Lane
                || a[i].Mirv != b[i].Mirv)
                return false;
        }
        return true;
    }
}
