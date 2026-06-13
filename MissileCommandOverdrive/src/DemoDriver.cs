using Raylib_cs;

namespace MissileCommandOverdrive;

// Self-driving evaluation harness, enabled with MCOD_DEMO=1.
// Plays the game via the auto-defense AI, walks through themes and both
// easter-egg bosses, captures numbered screenshots via the game's own
// TakeScreenshot (no OS screen-capture permission needed), and logs frame
// stats to demo_log.txt. The game exits when the script completes.
public static class DemoDriver
{
    public static readonly bool Active = Environment.GetEnvironmentVariable("MCOD_DEMO") == "1";

    private static float _t;
    private static int _step;
    private static readonly List<string> _log = [];

    private record struct Step(float At, Action<GameState> Act);

    private static readonly Step[] Script =
    [
        new(0.8f,  s => Shot(s, "demo_01_intro")),
        new(1.2f,  s => { GameInit.ResetGame(s); s.Auto = true; s.Debug.Enabled = true; }),
        // §5 6.2 wave-intro stinger: ResetGame opened wave 1 with a 2.5 s WavePause;
        // capture ~1.05 s in — the title ("WAVE 1 — …") has finished typing and the
        // threat-icon row is showing, but the stinger has not yet self-completed
        // (~1.6 s) and no input has collapsed it (the auto-defense AI never touches
        // the keyboard/mouse).
        new(2.25f, s => Shot(s, "demo_01b_waveintro")),
        new(4.5f,  s => Shot(s, "demo_02_wave1")),
        // §5 3.1 pause FSM check: freeze, capture the menu over the dimmed world, resume
        new(5.5f,  s => Menu.Open(s)),
        new(6.3f,  s => Shot(s, "demo_02b_pause")),
        new(6.8f,  s => Menu.Resume(s)),
        new(7.0f,  s => HellRaiserSystem.Toggle(s)),
        // §5 3.5 scrap-shop check: force the shop open (no wave-clear salvage/repair
        // side effects — those live at the real clear site), capture, close.
        // BuildForecast pins next-wave intel (§5 4.1) so the capture shows it;
        // the level-8 StartWave below discards the stale pin. BuildDraft (§5 4.3)
        // rolls the 3 perk cards the real clear site would. §5 6.2: stamp the
        // report-card tallies + arm the CLEARED stamp here so the captures show a
        // populated report card (the real clear site does this from the event bus).
        new(7.4f,  s =>
        {
            s.Phase = GamePhase.Shop; s.ShopTimer = 8f;
            WaveSystem.BuildForecast(s); PerkSystem.BuildDraft(s);
            s.Wave.Salvage = 96; s.Wave.CitiesSaved = s.AliveCities; s.WaveClearT = 2.0f;
        }),
        // §5 6.2: capture mid-animation — the WAVE CLEARED stamp has punched in
        // and the count-up tallies are ramping over the shop panel.
        new(7.7f,  s => Shot(s, "demo_02c1_cleared")),
        new(7.95f, s => Shot(s, "demo_02c_shop")),
        // §5 4.3: auto-pick card 1 so a perk is active for the later captures,
        // then capture the INSTALLED card state
        new(8.0f,  s => PerkSystem.TryPick(s, 0)),
        new(8.4f,  s => Shot(s, "demo_02d_draft")),
        new(8.6f,  s => { if (s.Phase == GamePhase.Shop) s.Phase = GamePhase.Playing; }),
        new(9.5f,  s => Shot(s, "demo_03_hellraiser")),
        new(10.5f, s => Combat.UseEMP(s)),
        new(11.1f, s => Shot(s, "demo_04_emp")),
        new(12.0f, s => s.Theme = "xbox"),
        new(14.5f, s => Shot(s, "demo_05_theme_xbox")),
        new(15.0f, s => s.Theme = "recharged"),
        new(17.5f, s => Shot(s, "demo_06_theme_recharged")),
        new(18.0f, s => s.Theme = "modern"),
        // §5 6.1: jump to wave 5 via the level-skip path — the wave scheduler
        // spawns the Mothership boss (with destructible shield pods) on its own.
        // The 666/777 cheats still exist; the demo now verifies the REAL path.
        new(19.0f, s =>
        {
            if (s.Phase == GamePhase.Shop) s.Phase = GamePhase.Playing;
            s.Level = 5;
            WaveSystem.StartWave(s, 0.5f);
        }),
        // capture early (pods + shield still up, boss prominent on-screen) then
        // again mid/late fight as the auto-defense erodes it
        new(21.3f, s => Shot(s, "demo_07_mothership")),
        new(24.5f, s => Shot(s, "demo_08_mothership_fight")),
        // §5 6.1: jump to wave 10 — the scheduler spawns the Daemon boss.
        new(30.0f, s =>
        {
            if (s.Phase == GamePhase.Shop) s.Phase = GamePhase.Playing;
            s.Level = 10;
            WaveSystem.StartWave(s, 0.5f);
        }),
        new(34.5f, s => Shot(s, "demo_09_demon")),
        new(38.0f, s =>
        {
            if (s.Phase == GamePhase.Shop) s.Phase = GamePhase.Playing;
            s.Level = 8;
            WaveSystem.StartWave(s, 0.5f);
        }),
        new(43.0f, s => Shot(s, "demo_10_wave8_chaos")),
        new(47.0f, s => Shot(s, "demo_11_wave8_late")),
        new(48.5f, s => Shot(s, "demo_12_final")),
        // §5 6.3: force a game-over to exercise the end-of-run ceremony. Drop back
        // to Playing (the death edge only fires from there), raze every city, and
        // let GameUpdate's aliveCities<=0 check open the ceremony on the next frame.
        new(49.0f, s =>
        {
            if (s.Phase != GamePhase.Playing) s.Phase = GamePhase.Playing;
            foreach (var c in s.Cities) c.Destroyed = true;
            s.AliveCities = 0;
        }),
        // Capture the settled ceremony: ~4.3 s in, the four stat rows have counted
        // up and the S/A/B/C/D grade has fully stamped (during the summary dwell,
        // before the hand-off to the folded-in GameOver tail). Confirms the grade +
        // stats render, not the old two-line GAME OVER.
        new(53.3f, s => Shot(s, "demo_13_ceremony")),
        new(57.0f, s => Shot(s, "demo_13b_tail")),
    ];

    // Demo captures must not depend on where the physical cursor happens to
    // sit (it's usually parked at a screen corner during unattended runs).
    // Pin the virtual crosshair mid-sky so the EMP step detonates on-screen
    // and the §5 4.4 combo ring is visible in mid-combat captures.
    public static void PinCursor(GameState s)
    {
        s.MouseX = s.W * 0.55f;
        s.MouseY = s.H * 0.42f;
    }

    // Returns false when the script is finished and the game should exit.
    public static bool Update(GameState s)
    {
        _t += Raylib.GetFrameTime();
        while (_step < Script.Length && _t >= Script[_step].At)
        {
            Script[_step].Act(s);
            _step++;
        }
        if (_step >= Script.Length)
        {
            File.WriteAllLines("demo_log.txt", _log);
            return false;
        }
        return true;
    }

    private static void Shot(GameState s, string name)
    {
        Raylib.TakeScreenshot($"{name}.png");
        _log.Add($"{name}: t={_t:F1}s fps={Raylib.GetFPS()} enemies={s.Enemies.Count} " +
                 $"explosions={s.Explosions.Count} sparks={s.Sparks.Count} smoke={s.SmokeParts.Count} " +
                 $"score={s.Score} scrap={s.Scrap} cities={s.Cities.Count(c => !c.Destroyed)} " +
                 $"theme={s.Theme} weather={s.Weather.Mode}");
    }
}
