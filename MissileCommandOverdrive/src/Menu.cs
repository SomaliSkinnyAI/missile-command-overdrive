using Raylib_cs;
using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Pause/settings menu (§5 3.1). Keyboard-driven (Up/Down/Left/Right/Enter);
/// owns the Playing/Shop ↔ Paused transitions. Display strings are rebuilt only on
/// key presses so the renderer draws the menu with zero per-frame allocation.</summary>
public static class Menu
{
    public const int ItemResume = 0;
    public const int ItemVolume = 1;
    public const int ItemShake = 2;
    public const int ItemFlashReduction = 3;
    public const int ItemUiScale = 4;
    public const int ItemTheme = 5;
    public const int ItemFullscreen = 6;
    public const int ItemAssistSlow = 7;
    public const int ItemAssistAutoEmp = 8;
    public const int ItemColorblind = 9;
    public const int ItemRestart = 10;
    public const int ItemQuit = 11;
    public const int ItemCount = 12;

    public static readonly string[] Labels =
    [
        "RESUME",
        "VOLUME",
        "SHAKE INTENSITY",
        "FLASH REDUCTION",
        "UI SCALE",
        "THEME",
        "BORDERLESS FULLSCREEN",
        "ASSIST: SLOW ENEMIES",
        "ASSIST: AUTO-EMP",
        "COLORBLIND MODE",
        "RESTART",
        "QUIT",
    ];

    // Value column ("" = action item) — refreshed on open and on change only.
    public static readonly string[] Values = new string[ItemCount];

    public static int Sel;

    const string On = "ON";
    const string Off = "OFF";
    static readonly System.Text.StringBuilder _sb = new(32);

    static Menu()
    {
        for (int i = 0; i < ItemCount; i++) Values[i] = "";
    }

    public static void Open(GameState s)
    {
        // §5 6.4: also openable from the Title menu (SETTINGS row) — RESUME then
        // returns to the title rather than into a run.
        if (s.Phase != GamePhase.Playing && s.Phase != GamePhase.Shop && s.Phase != GamePhase.Title) return;
        s.PhaseBeforePause = s.Phase;
        s.Phase = GamePhase.Paused;
        Sel = 0;
        RefreshAll(s);
    }

    public static void Resume(GameState s)
    {
        if (s.Phase != GamePhase.Paused) return;
        s.Phase = s.PhaseBeforePause;
        // §5 6.4: returning to the title re-arms the idle→attract timer so the
        // demo doesn't trigger the instant settings closes.
        if (s.Phase == GamePhase.Title) AttractSystem.Idle = 0f;
        // §5 3.2: GameState.Settings IS Profile.Data.Settings — one disk write on
        // menu close persists every slider/toggle (plus game-over/shutdown saves).
        Profile.Save();
    }

    /// <summary>Per-frame input while Paused. The caller returns right after, so the
    /// menu swallows all other input.</summary>
    public static void Update(GameState s)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { Resume(s); return; }

        if (Raylib.IsKeyPressed(KeyboardKey.Up)) Sel = (Sel + ItemCount - 1) % ItemCount;
        if (Raylib.IsKeyPressed(KeyboardKey.Down)) Sel = (Sel + 1) % ItemCount;

        int dir = 0;
        if (Raylib.IsKeyPressed(KeyboardKey.Left)) dir = -1;
        if (Raylib.IsKeyPressed(KeyboardKey.Right)) dir = 1;
        bool enter = Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter);
        if (dir == 0 && !enter) return;

        switch (Sel)
        {
            case ItemResume:
                if (enter) Resume(s);
                break;

            case ItemVolume:
                if (dir != 0)
                {
                    float v = SnapStep(s.Settings.Volume + dir * 0.05f, 0.05f, 0f, 1f);
                    s.Settings.Volume = v;
                    SynthAudio.SetVolume(v);
                    SynthAudio.Launch(0.5f); // audition the new level
                    RefreshValue(s, ItemVolume);
                }
                break;

            case ItemShake:
                if (dir != 0)
                {
                    s.Settings.ShakeIntensity = SnapStep(s.Settings.ShakeIntensity + dir * 0.1f, 0.1f, 0f, 1f);
                    RefreshValue(s, ItemShake);
                }
                break;

            case ItemFlashReduction:
                s.Settings.FlashReduction = !s.Settings.FlashReduction;
                RefreshValue(s, ItemFlashReduction);
                break;

            case ItemUiScale:
                if (dir != 0)
                {
                    s.Settings.UiScale = SnapStep(s.Settings.UiScale + dir * 0.05f, 0.05f, 0.8f, 1.3f);
                    RefreshValue(s, ItemUiScale);
                }
                break;

            case ItemTheme:
                s.Theme = dir < 0
                    ? s.Theme switch { "modern" => "recharged", "recharged" => "xbox", _ => "modern" }
                    : s.Theme switch { "modern" => "xbox", "xbox" => "recharged", _ => "modern" };
                RefreshValue(s, ItemTheme);
                break;

            case ItemFullscreen:
                // §5 3.1: borderless windowed, NOT ToggleFullscreen (macOS Retina issues)
                Raylib.ToggleBorderlessWindowed();
                RefreshValue(s, ItemFullscreen);
                break;

            case ItemAssistSlow:
                s.Settings.AssistEnemySlow = !s.Settings.AssistEnemySlow;
                RefreshValue(s, ItemAssistSlow);
                break;

            case ItemAssistAutoEmp:
                s.Settings.AssistAutoEmp = !s.Settings.AssistAutoEmp;
                RefreshValue(s, ItemAssistAutoEmp);
                break;

            case ItemColorblind:
                s.Settings.ColorblindMode = !s.Settings.ColorblindMode;
                Rendering.Palette.Colorblind = s.Settings.ColorblindMode; // live-apply
                RefreshValue(s, ItemColorblind);
                break;

            case ItemRestart:
                if (enter) GameInit.ResetGame(s); // sets Phase = Playing
                break;

            case ItemQuit:
                if (enter) s.QuitRequested = true;
                break;
        }
    }

    static float SnapStep(float v, float step, float min, float max)
        => MathH.Clamp(MathF.Round(v / step) * step, min, max);

    static void RefreshAll(GameState s)
    {
        RefreshValue(s, ItemVolume);
        RefreshValue(s, ItemShake);
        RefreshValue(s, ItemFlashReduction);
        RefreshValue(s, ItemUiScale);
        RefreshValue(s, ItemTheme);
        RefreshValue(s, ItemFullscreen);
        RefreshValue(s, ItemAssistSlow);
        RefreshValue(s, ItemAssistAutoEmp);
        RefreshValue(s, ItemColorblind);
    }

    static void RefreshValue(GameState s, int item)
    {
        switch (item)
        {
            case ItemVolume:
                Values[item] = BarText(s.Settings.Volume);
                break;
            case ItemShake:
                Values[item] = BarText(s.Settings.ShakeIntensity);
                break;
            case ItemFlashReduction:
                Values[item] = s.Settings.FlashReduction ? On : Off;
                break;
            case ItemUiScale:
                _sb.Clear();
                _sb.Append('x').Append(s.Settings.UiScale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                Values[item] = _sb.ToString();
                break;
            case ItemTheme:
                Values[item] = s.Theme switch
                {
                    "xbox" => "XBOX",
                    "recharged" => "RECHARGED",
                    _ => "MODERN"
                };
                break;
            case ItemFullscreen:
                Values[item] = Raylib.IsWindowState(ConfigFlags.BorderlessWindowMode) ? On : Off;
                break;
            case ItemAssistSlow:
                Values[item] = s.Settings.AssistEnemySlow ? On : Off;
                break;
            case ItemAssistAutoEmp:
                Values[item] = s.Settings.AssistAutoEmp ? On : Off;
                break;
            case ItemColorblind:
                Values[item] = s.Settings.ColorblindMode ? On : Off;
                break;
        }
    }

    // "[#####-----] 50%" — same dialect as the HUD threat bar.
    static string BarText(float v)
    {
        const int bars = 10;
        int fill = (int)MathF.Round(MathH.Clamp(v, 0, 1) * bars);
        _sb.Clear();
        _sb.Append('[').Append('#', fill).Append('-', bars - fill).Append("] ")
           .Append((int)MathF.Round(v * 100)).Append('%');
        return _sb.ToString();
    }
}
