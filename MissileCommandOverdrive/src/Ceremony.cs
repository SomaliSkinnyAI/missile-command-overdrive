using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>§5 6.3 end-of-run ceremony — the timeline + letter-grade authority.
///
/// On death the run enters <see cref="GamePhase.Ceremony"/>: a 120 ms freeze,
/// then a slow fade, then staged stat reveals every 0.6 s with odometer-style
/// count-up ticks, then a letter grade stamped with a Trauma pulse. If the run
/// made the top-10 the initials entry is sequenced AFTER the grade (the existing
/// game-over table/seed/retry tail is folded in — not duplicated).
///
/// CeremonyT counts UP on rawDt (GameUpdate). The stage and per-stat reveal
/// progress are pure functions of CeremonyT so the renderer never needs its own
/// timers — and every stage is skippable: a keypress jumps CeremonyT straight to
/// <see cref="SummaryAt"/> (the summary), and a second one confirms past the
/// table to GameOver.</summary>
public static class Ceremony
{
    // ----- timeline (seconds, on the unscaled rawDt clock) --------------------
    public const float Freeze = 0.12f;       // §6.3 120 ms hit-stop on death
    public const float FadeStart = Freeze;   // dim curtain begins easing in here
    public const float FadeDur = 0.55f;      // slow fade-to-ceremony window
    public const float StatsStart = 0.75f;   // first stat row reveals here
    public const float StatStep = 0.6f;      // §6.3 a new row every ~0.6 s
    public const float CountDur = 0.45f;     // each row's value counts up over this
    public const int StatCount = 4;          // SCORE, WAVE, ACCURACY, MAX COMBO

    // Grade stamps once every row has revealed (+ a beat to breathe).
    public const float GradeAt = StatsStart + StatStep * StatCount + 0.25f;
    public const float GradeStampDur = 0.5f; // scale 3→1 ease-out-back
    public const float GradePulse = 0.3f;    // §6.3 Trauma 0.3 on the grade stamp

    // Skip target: a keypress before the grade jumps straight to the summary
    // (grade landed, count-ups complete). The summary then dwells for TailDwell
    // so the player reads the verdict before the GameOver tail (table/seed/
    // initials/retry) auto-takes over — a second keypress skips the dwell.
    public const float SummaryAt = GradeAt + GradeStampDur + 0.2f;
    public const float TailDwell = 1.6f;
    public const float TailAt = SummaryAt + TailDwell;

    /// <summary>Per-stat reveal progress 0..1 (0 = not yet, 1 = fully counted up).
    /// Row i reveals at StatsStart + i·StatStep and counts up over CountDur.</summary>
    public static float StatReveal(float ceremonyT, int i)
    {
        float start = StatsStart + i * StatStep;
        return MathH.Clamp((ceremonyT - start) / CountDur, 0f, 1f);
    }

    /// <summary>Grade stamp progress 0..1 (drives the scale-3→1 ease-out-back).</summary>
    public static float GradeReveal(float ceremonyT) =>
        MathH.Clamp((ceremonyT - GradeAt) / GradeStampDur, 0f, 1f);

    /// <summary>Run-wide intercept rate 0..100 (kills vs kills+leaks). 100 with no
    /// engagements so a flawless early death still reads as a clean sheet rather
    /// than a div-by-zero. WaveStats resets per wave, so the ceremony reads the
    /// run-level RunKills/RunLeaks accumulators instead.</summary>
    public static int RunAccuracyPct(GameState s)
    {
        int shots = s.RunKills + s.RunLeaks;
        return shots <= 0 ? 100 : (int)MathF.Round(100f * s.RunKills / shots);
    }

    /// <summary>§5 6.3 letter grade — documented, sane across good and bad runs.
    ///
    /// The plan's <c>f(accuracy × waves reached × cities standing × max combo)</c>
    /// has one structural wrinkle: a game-over fires only when the last city
    /// falls, so "cities standing" is 0 at the grading instant. We fold that
    /// factor into wave depth (how far the defence held is the survival proxy)
    /// and grade on three live signals, each normalised to 0..1:
    ///
    ///   acc   = run intercept rate / 100                    (defensive precision)
    ///   wave  = clamp(level / 15)                           (survival depth; W15 maxes)
    ///   combo = clamp(maxCombo / 20)                        (aggressive play)
    ///
    ///   q = 0.45·acc + 0.35·wave + 0.20·combo   ∈ [0,1]
    ///
    /// Thresholds (tuned so a wave-1 wipe lands D, a competent mid run lands B,
    /// and only a deep, precise, high-combo run reaches S):
    ///   q ≥ 0.85 → S    ≥ 0.70 → A    ≥ 0.52 → B    ≥ 0.34 → C    else D
    /// </summary>
    public static char ComputeGrade(GameState s)
    {
        float acc = RunAccuracyPct(s) / 100f;
        float wave = MathH.Clamp(s.Level / 15f, 0f, 1f);
        float combo = MathH.Clamp(s.MaxCombo / 20f, 0f, 1f);
        float q = 0.45f * acc + 0.35f * wave + 0.20f * combo;
        if (q >= 0.85f) return 'S';
        if (q >= 0.70f) return 'A';
        if (q >= 0.52f) return 'B';
        if (q >= 0.34f) return 'C';
        return 'D';
    }
}
