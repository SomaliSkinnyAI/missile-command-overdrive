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
        // rolls the 3 perk cards the real clear site would.
        new(7.4f,  s => { s.Phase = GamePhase.Shop; s.ShopTimer = 8f; WaveSystem.BuildForecast(s); PerkSystem.BuildDraft(s); }),
        new(7.8f,  s => Shot(s, "demo_02c_shop")),
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
        new(19.0f, s => MothershipSystem.Summon(s)),
        new(23.0f, s => Shot(s, "demo_07_mothership")),
        new(28.0f, s => Shot(s, "demo_08_mothership_fight")),
        new(30.0f, s => DemonSystem.Summon(s)),
        new(33.5f, s => Shot(s, "demo_09_demon")),
        new(38.0f, s =>
        {
            // mirrors the old `s.Shop = false` exactly: leave GameOver untouched
            if (s.Phase == GamePhase.Shop) s.Phase = GamePhase.Playing;
            s.Level = 8;
            WaveSystem.StartWave(s, 0.5f);
        }),
        new(43.0f, s => Shot(s, "demo_10_wave8_chaos")),
        new(47.0f, s => Shot(s, "demo_11_wave8_late")),
        new(48.5f, s => Shot(s, "demo_12_final")),
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
