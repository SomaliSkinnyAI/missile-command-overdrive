using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Time director (§4.7): consumes the per-frame event ring (from the
/// Program.cs drain, before it is cleared) and drives s.HitStop / s.TimeScale.
/// All internal state advances on rawDt so effects recover in real time even
/// while the sim is frozen.</summary>
public static class FeelDirector
{
    const float CityHitStop = 0.09f;
    const float CityScale = 0.4f;
    const float CityEase = 0.5f;
    const int MultiKillCount = 4;
    const float MultiKillWindow = 0.7f;
    const float MultiKillHitStop = 0.06f;
    const float MultiKillRearm = 0.8f;
    const float WaveFinalScale = 0.25f;
    const float WaveFinalEase = 0.8f;
    // §5 6.1 boss feel: phase crossings punch with a short freeze, the killing
    // blow gets a heavy freeze + directed slow-mo.
    const float BossPhaseHitStop = 0.12f;
    const float BossDeathHitStop = 0.18f;
    const float BossDeathScale = 0.3f;
    const float BossDeathEase = 1.0f;

    static float _clock; // real-time clock (rawDt), never frozen

    /// <summary>Shared unscaled clock — the trauma camera samples its noise
    /// tables on this (s.Time freezes during hit-stop).</summary>
    public static float Clock => _clock;
    static readonly float[] _killTimes = [-1000f, -1000f, -1000f, -1000f];
    static int _killIdx;
    static float _multiKillReadyAt;
    static bool _waveSlowmoFired;

    // TimeScale recovery ease toward TimeScaleTarget (cubic ease-in: stays
    // deep in slow-mo early, accelerates back to full speed)
    static float _easeFrom = 1f, _easeT, _easeDur;

    public static void Reset(GameState s)
    {
        s.HitStop = 0;
        s.TimeScale = 1f;
        s.TimeScaleTarget = 1f;
        _easeFrom = 1f;
        _easeT = 0;
        _easeDur = 0;
        for (int i = 0; i < _killTimes.Length; i++) _killTimes[i] = -1000f;
        _killIdx = 0;
        _multiKillReadyAt = 0;
        _waveSlowmoFired = false;
    }

    /// <summary>Decay hit-stop and ease TimeScale. Runs on rawDt in Program.cs
    /// before simDt is computed.</summary>
    public static void Tick(GameState s, float rawDt)
    {
        _clock += rawDt;
        if (s.HitStop > 0) s.HitStop = MathF.Max(0, s.HitStop - rawDt);
        if (s.TimeScale != s.TimeScaleTarget)
        {
            _easeT += rawDt;
            if (_easeT >= _easeDur)
            {
                s.TimeScale = s.TimeScaleTarget;
            }
            else
            {
                float k = _easeT / _easeDur;
                s.TimeScale = MathH.Lerp(_easeFrom, s.TimeScaleTarget, k * k * k);
            }
        }
    }

    /// <summary>Called per event from the Program.cs drain loop, before Clear().</summary>
    public static void OnEvent(GameState s, in GameEvent e)
    {
        if (s.Intro || s.GameOver) return;
        switch (e.Kind)
        {
            case EventKind.CityDestroyed:
                // Max, not sum: simultaneous losses must not stack into a long stall
                s.HitStop = MathF.Max(s.HitStop, CityHitStop);
                SlowMo(s, CityScale, CityEase);
                break;

            case EventKind.Kill:
                _killTimes[_killIdx] = _clock;
                _killIdx = (_killIdx + 1) % MultiKillCount;
                // After the advance, _killIdx holds the 4th-most-recent kill
                if (_clock >= _multiKillReadyAt && _clock - _killTimes[_killIdx] <= MultiKillWindow)
                {
                    s.HitStop = MathF.Max(s.HitStop, MultiKillHitStop);
                    _multiKillReadyAt = _clock + MultiKillRearm;
                }
                // Wave-final kill: drain runs post-update, so empty lists here mean
                // this kill cleared the sky. (The WaveCleared event itself fires only
                // after explosions/debris fade — too late to dramatize the kill.)
                if (!_waveSlowmoFired && !s.Shop
                    && s.SpawnI >= s.WavePlan.Count
                    && s.Enemies.Count == 0 && s.UFOs.Count == 0 && s.Raiders.Count == 0
                    && s.Demon == null && s.Mothership == null && s.Fighters.Count == 0)
                {
                    _waveSlowmoFired = true;
                    SlowMo(s, WaveFinalScale, WaveFinalEase);
                }
                break;

            case EventKind.BossPhase:
                // A phase threshold crossed — short freeze so the escalation reads.
                s.HitStop = MathF.Max(s.HitStop, BossPhaseHitStop);
                break;

            case EventKind.BossDeath:
                // The milestone kill: heavy freeze + directed slow-mo to savor it.
                s.HitStop = MathF.Max(s.HitStop, BossDeathHitStop);
                SlowMo(s, BossDeathScale, BossDeathEase);
                break;

            case EventKind.WaveStart:
                _waveSlowmoFired = false;
                break;
        }
    }

    static void SlowMo(GameState s, float scale, float easeBack)
    {
        s.TimeScale = MathF.Min(s.TimeScale, scale);
        s.TimeScaleTarget = 1f;
        _easeFrom = s.TimeScale;
        _easeT = 0;
        _easeDur = easeBack;
    }
}
