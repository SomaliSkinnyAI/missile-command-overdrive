using Raylib_cs;
using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>§5 6.4 title screen + attract mode.
///
/// The <see cref="GamePhase.Title"/> phase is no longer a dead text overlay: it
/// runs a REAL, low-intensity, seeded wave with <c>s.Auto = true</c> behind the
/// bloom-fed logo and a START / SCORES / SETTINGS / QUIT menu (the menu rows are
/// the §5 3.1 <see cref="Menu"/> rows, reused — SETTINGS opens the same panel).
///
/// The living backdrop is driven by the existing gameplay sim. Rather than thread
/// the Title phase through the ~35 <c>GamePhase.Playing</c> guard sites, the
/// update PROMOTES the phase to Playing for one <see cref="GameUpdate.UpdateAll"/>
/// call and restores Title immediately after — so the event drain in Program.cs
/// (which runs after this returns, with the phase back to Title) sees Title and
/// the FeelDirector / score-freeze / consumers behave exactly as on the old
/// title. The backdrop run is endless: a wipe or a wave-clear quietly re-seeds a
/// fresh attract wave instead of dying or opening the shop.
///
/// After <see cref="IdleToAttract"/> seconds without input, the menu yields to an
/// ATTRACT presentation: the same self-playing wave, full-screen, with a flashing
/// "DEMO — PRESS ANY KEY". Any key/click returns to the menu (and re-arms idle).
///
/// This is INDEPENDENT of the MCOD_DEMO eval harness (<see cref="DemoDriver"/>):
/// the attract auto-player is the in-game auto-defense, never the screenshot/log
/// script. DemoDriver behavior is unchanged.</summary>
public static class AttractSystem
{
    // Menu items — START reuses ResetGame, SETTINGS reuses the pause panel.
    public const int ItemStart = 0;
    public const int ItemScores = 1;
    public const int ItemSettings = 2;
    public const int ItemQuit = 3;
    public const int ItemCount = 4;

    public static readonly string[] Labels = ["START", "SCORES", "SETTINGS", "QUIT"];

    public static int Sel;
    public static bool ScoresOpen;       // SCORES toggles the top-10 panel over the marquee
    public static bool Demo;             // idle elapsed → ATTRACT demo presentation
    public static float Idle;            // seconds since the last title input (menu only)
    public static float MarqueeT;        // marquee scroll clock (rawDt)

    public const float IdleToAttract = 15f; // §5 6.4: ~15 s idle on the title → demo

    // The Title backdrop run is deterministic but rotates each session so the
    // attract demo isn't identical every launch (cosmetic only — never touches
    // the SELFTEST plan-stream contract, which seeds from MasterSeed ^ wave).
    static ulong _seed;
    static bool _entered;

    /// <summary>Boot/return-to-title setup: seed and start a quiet auto-played
    /// wave so the very first frame a player sees is a living title, not a text
    /// overlay. Idempotent re-entry resets the menu/idle state.</summary>
    public static void Enter(GameState s)
    {
        Sel = 0;
        ScoresOpen = false;
        Demo = false;
        Idle = 0f;
        s.Phase = GamePhase.Title;
        StartBackdropRun(s);
        _entered = true;
    }

    // Seed + arm a fresh low-intensity attract wave. The world is rebuilt intact
    // and the wave runs at level 1 (lowest threat budget) so the backdrop reads
    // as calm ambient combat. Auto-defense flies it; the player never inputs.
    static void StartBackdropRun(GameState s)
    {
        // Rotate the cosmetic seed each (re)start; never the daily/plan seed.
        if (_seed == 0) _seed = s.Cosmetic.NextULong() | 1UL;
        else _seed = _seed * 6364136223846793005UL + 1442695040888963407UL;

        s.MasterSeed = _seed;
        s.PendingSeed = null;
        Profile.CancelInitialsEntry();
        s.Level = 1;
        s.Score = 0;
        s.Combo = 0;
        s.MaxCombo = 0;
        s.ComboTimer = 0;
        s.DisplayScore = 0;
        s.ComboPop = 0;
        s.Auto = true;            // §5 6.4: auto-defense plays the backdrop wave
        s.Danger = 0;
        s.Intensity = 0;
        s.RecentCityHits = 0;
        s.PinnedPlan = null;
        s.Emp = 1;
        s.EmpCd = 0;
        s.Upgrades = new Upgrades();
        s.Perks = PerkFlags.Defaults;
        s.OwnedPerks.Clear();
        PerkSystem.ClearDraft(s);
        s.AssistedRun = false;

        // Clear any residue from a previous (real or attract) run.
        s.UFOs.Clear();
        s.Raiders.Clear();
        s.Demon = null;
        s.Mothership = null;
        s.Fighters.Clear();
        s.Enemies.Clear();
        s.PlayerMissiles.Clear();
        s.Explosions.Clear();
        s.Sparks.Clear();
        s.SmokeParts.Clear();
        s.Trails.Clear();
        s.DebrisParts.Clear();
        s.Shockwaves.Clear();
        s.LightBursts.Clear();
        s.MuzzleFlashes.Clear();
        s.Scorches.Clear();
        s.FloatingTexts.Clear();
        s.Trauma = 0;
        s.Flash = 0;
        s.Chromatic = 0;
        s.RunKills = 0;
        s.RunLeaks = 0;
        s.Events.Clear();
        FeelDirector.Reset(s);

        GameInit.BuildWorld(s);
        WaveSystem.StartWave(s, 2.0f);
        // StartWave posts an incoming-wave Note/stab; the title backdrop stays
        // clean (no HUD text), so clear the transient messages it set.
        s.Note = ""; s.NoteT = 0f;
        s.Msg = ""; s.MsgT = 0f;
    }

    /// <summary>Per-frame Title update (Program.cs Title case). Advances the
    /// living backdrop sim, the menu/idle/demo state, and the marquee clock.
    /// rawDt only (no hit-stop/slow-mo on the title — FeelDirector bails on
    /// Intro), but the backdrop still respects s.TimeScale for visual continuity.</summary>
    public static void UpdateTitle(GameState s, float rawDt)
    {
        if (!_entered) Enter(s);
        // Bounded accumulator: the marquee scroll wraps on a large period so the
        // float never grows past single-precision smoothness on long idle.
        MarqueeT += rawDt;
        if (MarqueeT > 100000f) MarqueeT -= 100000f;

        // ----- drive the living backdrop sim -----
        // Promote to Playing so every Playing-gated system (spawning, collisions,
        // auto-defense, phalanx, weather) runs, then restore Title before we
        // return — the event drain + all phase-routed consumers see Title.
        float simDt = s.HitStop > 0 ? 0f : rawDt * s.TimeScale;
        s.Phase = GamePhase.Playing;
        GameUpdate.UpdateAll(s, simDt, rawDt);

        // The backdrop is endless. UpdateAll may have flipped the phase to Shop
        // (wave cleared) or Ceremony (defense wiped) — in either case re-seed a
        // fresh attract wave so the title never stalls on a shop/death screen.
        if (s.Phase == GamePhase.Shop || s.Phase == GamePhase.Ceremony
            || s.Phase == GamePhase.GameOver)
            StartBackdropRun(s);
        s.Phase = GamePhase.Title;

        // ----- idle → attract demo -----
        if (!Demo)
        {
            Idle += rawDt;
            if (Idle >= IdleToAttract) { Demo = true; ScoresOpen = false; }
        }
    }

    /// <summary>Per-frame Title input (Program.HandleInput, before the gameplay
    /// handlers). Returns true if it consumed the frame's input (the caller then
    /// returns — Title swallows fire/EMP/etc.).</summary>
    public static bool HandleInput(GameState s)
    {
        if (s.Phase != GamePhase.Title) return false;

        // In the demo presentation, ANY key or click returns to the menu.
        if (Demo)
        {
            if (Raylib.GetKeyPressed() != 0
                || Raylib.IsMouseButtonPressed(MouseButton.Left)
                || Raylib.IsMouseButtonPressed(MouseButton.Right))
            {
                Demo = false;
                Idle = 0f;
                SynthAudio.UiClick();
            }
            return true;
        }

        // ----- menu navigation (any input re-arms the idle timer) -----
        bool up = Raylib.IsKeyPressed(KeyboardKey.Up) || Raylib.IsKeyPressed(KeyboardKey.W);
        bool down = Raylib.IsKeyPressed(KeyboardKey.Down) || Raylib.IsKeyPressed(KeyboardKey.S);
        bool enter = Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter)
                     || Raylib.IsKeyPressed(KeyboardKey.Space);

        if (up) { Sel = (Sel + ItemCount - 1) % ItemCount; Idle = 0f; SynthAudio.UiClick(); }
        if (down) { Sel = (Sel + 1) % ItemCount; Idle = 0f; SynthAudio.UiClick(); }

        // §5 3.2: D on the title still arms a daily-seed run; mouse can pick a row.
        if (Raylib.IsKeyPressed(KeyboardKey.D))
        {
            Idle = 0f;
            s.PendingSeed = SeedUtil.DailySeed();
            GameInit.ResetGame(s); // → Playing
            s.Msg = "DAILY SEED RUN";
            s.MsgT = 1.6f;
            return true;
        }

        // ESC closes the SCORES panel, else quits the demo loop from the title.
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            Idle = 0f;
            if (ScoresOpen) ScoresOpen = false;
            else s.QuitRequested = true;
            return true;
        }

        if (enter)
        {
            Idle = 0f;
            Activate(s);
            return true;
        }

        // Click also activates the highlighted row (the title hides the crosshair,
        // so a left-click is a menu confirm, not a fire).
        if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            Idle = 0f;
            Activate(s);
            return true;
        }

        return true; // Title always owns its input frame (no gameplay underneath)
    }

    static void Activate(GameState s)
    {
        switch (Sel)
        {
            case ItemStart:
                SynthAudio.UiConfirm();
                GameInit.ResetGame(s); // → Playing (rolls a fresh master seed)
                break;
            case ItemScores:
                SynthAudio.UiClick();
                ScoresOpen = !ScoresOpen;
                break;
            case ItemSettings:
                SynthAudio.UiClick();
                Menu.Open(s); // §5 3.1 settings panel — RESUME returns to Title
                break;
            case ItemQuit:
                SynthAudio.UiClick();
                s.QuitRequested = true;
                break;
        }
    }
}
