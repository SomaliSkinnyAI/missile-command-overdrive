using System.Runtime.InteropServices;
using Raylib_cs;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive.Audio;

/// <summary>
/// Procedural synthesizer audio system — faithful port of the HTML mkAudio().
/// Uses Raylib AudioStream to generate sounds in real-time via oscillators + noise.
/// </summary>
public static class SynthAudio
{
    const int SampleRate = 44100;
    const int BufferSize = 1024;
    static AudioStream _stream;
    static short[] _buffer = new short[BufferSize];
    static readonly List<Voice> _voices = new(32);
    static float _masterVol = 0.54f;
    static bool _muted;
    static bool _initialized;

    // Ambient oscillators (continuous)
    static float _ambPhase1, _ambPhase2, _ambPhase3;
    static float _beatTimer;
    static float _beatStep = 0.42f;
    static float _dangerLevel;

    public static void Init()
    {
        if (_initialized) return;
        Raylib.InitAudioDevice();
        _stream = Raylib.LoadAudioStream((uint)SampleRate, 16, 1);
        Raylib.PlayAudioStream(_stream);
        _initialized = true;
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        Raylib.UnloadAudioStream(_stream);
        Raylib.CloseAudioDevice();
        _initialized = false;
    }

    public static void ToggleMute() { _muted = !_muted; }
    public static bool IsMuted => _muted;
    public static void SetVolume(float v) { _masterVol = MathH.Clamp(v, 0, 1); }
    public static float Volume => _masterVol;

    /// <summary>Call every frame to fill audio buffers.</summary>
    public static void Update(GameState s, float dt)
    {
        if (!_initialized) return;

        // Update continuous state
        _dangerLevel = s.Danger;
        float inten = MathH.Clamp(s.Danger * 0.78f + s.Level * 0.035f, 0, 1);
        _beatStep = MathF.Max(0.14f, 0.42f - s.Level * 0.009f - inten * 0.08f);

        // Phalanx continuous level from FireMix
        float phLvl = 0;
        if (!s.GameOver)
        {
            int phCount = 0;
            foreach (var px in s.Phalanxes)
            {
                if (!px.Destroyed) { phLvl += px.FireMix; phCount++; }
            }
            if (phCount > 0) phLvl /= phCount;
        }
        SetPhalanxLevel(phLvl);

        // Beat timer
        _beatTimer -= dt;
        if (_beatTimer <= 0 && !s.GameOver && !s.Intro)
        {
            _beatTimer = _beatStep;
            AddVoice(new Voice { Type = WaveType.Sine, Freq = 92 + s.Level * 2, FreqEnd = 46,
                Volume = 0.08f + inten * 0.12f, Duration = 0.2f });
            if (inten > 0.58f)
                AddVoice(new Voice { Type = WaveType.Triangle, Freq = 220 + inten * 90, FreqEnd = 145,
                    Volume = 0.01f + inten * 0.015f, Duration = 0.13f });
        }

        // Fill audio stream
        if (Raylib.IsAudioStreamProcessed(_stream))
        {
            FillBuffer();
            unsafe
            {
                fixed (short* ptr = _buffer)
                {
                    Raylib.UpdateAudioStream(_stream, ptr, BufferSize);
                }
            }
        }

        // Expire voices
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            _voices[i].Elapsed += dt;
            if (_voices[i].Elapsed >= _voices[i].Duration)
                _voices.RemoveAt(i);
        }
    }

    static void FillBuffer()
    {
        float vol = _muted ? 0 : _masterVol;
        for (int i = 0; i < BufferSize; i++)
        {
            float t = i / (float)SampleRate;
            float sample = 0;

            // Ambient drone
            _ambPhase1 += 40f / SampleRate;
            _ambPhase2 += 60f / SampleRate;
            _ambPhase3 += 80f / SampleRate;
            sample += MathF.Sin(_ambPhase1 * MathF.PI * 2) * 0.035f;
            sample += Sawtooth(_ambPhase2) * 0.012f;
            sample += Triangle(_ambPhase3) * 0.012f;

            // Danger hum
            if (_dangerLevel > 0.34f)
            {
                float dv = MathH.Clamp((_dangerLevel - 0.34f) * 0.25f, 0, 0.18f);
                sample += Triangle(_ambPhase1 * 4.25f) * dv;
            }

            // Phalanx CIWS continuous BRRRRT
            if (_phLevel > 0.001f)
            {
                // LFO modulates buzz frequency: 28Hz triangle, ±62Hz
                _phPhaseLfo += 28f / SampleRate;
                float lfo = Triangle(_phPhaseLfo) * 62f;

                // Square hum at 108Hz
                _phPhaseHum += 108f / SampleRate;
                float hum = (MathF.Sin(_phPhaseHum * MathF.PI * 2) >= 0 ? 1f : -1f) * 0.24f;

                // Sawtooth buzz at 610Hz + LFO
                _phPhaseBuzz += (610f + lfo) / SampleRate;
                float buzz = Sawtooth(_phPhaseBuzz) * 0.22f;

                // High-frequency noise
                float noise = (Random.Shared.NextSingle() * 2 - 1) * 0.28f;

                float phMix = (hum + buzz + noise) * _phLevel * 0.85f;
                sample += phMix;
            }

            // Active voices
            lock (_voices)
            {
                foreach (var v in _voices)
                {
                    float vt = v.Elapsed + t;
                    float prog = MathH.Clamp(vt / v.Duration, 0, 1);
                    float freq = MathH.Lerp(v.Freq, v.FreqEnd, prog);
                    float phase = vt * freq;
                    float env = Envelope(prog, v.Attack, v.Decay);
                    float s = v.Type switch
                    {
                        WaveType.Sine => MathF.Sin(phase * MathF.PI * 2),
                        WaveType.Square => MathF.Sin(phase * MathF.PI * 2) >= 0 ? 1 : -1,
                        WaveType.Sawtooth => Sawtooth(phase),
                        WaveType.Triangle => Triangle(phase),
                        WaveType.Noise => (Random.Shared.NextSingle() * 2 - 1),
                        _ => 0
                    };
                    sample += s * env * v.Volume;
                }
            }

            // Soft clip
            sample *= vol;
            sample = MathH.Clamp(sample * 0.8f, -0.95f, 0.95f);
            _buffer[i] = (short)(sample * 32000);
        }
    }

    static float Sawtooth(float phase) => (phase % 1f) * 2 - 1;
    static float Triangle(float phase) { float p = phase % 1f; return p < 0.5f ? p * 4 - 1 : 3 - p * 4; }
    static float Envelope(float t, float attack, float decay)
    {
        if (t < attack) return t / MathF.Max(0.001f, attack);
        float rel = (t - attack) / MathF.Max(0.001f, 1 - attack);
        return MathF.Max(0, 1 - rel * (1 + decay));
    }

    static void AddVoice(Voice v)
    {
        lock (_voices)
        {
            if (_voices.Count >= 28)
                _voices.RemoveAt(0);
            _voices.Add(v);
        }
    }

    // ── Sound Events ──

    /// <summary>Player missile launch — sawtooth sweep down + noise burst.</summary>
    public static void Launch(float pan)
    {
        AddVoice(new Voice { Type = WaveType.Sawtooth, Freq = 850, FreqEnd = 120,
            Volume = 0.25f, Duration = 0.28f, Attack = 0.02f, Decay = 1.5f });
        AddVoice(new Voice { Type = WaveType.Noise, Freq = 2400, FreqEnd = 2400,
            Volume = 0.3f, Duration = 0.35f, Attack = 0.02f, Decay = 1.5f });
    }

    /// <summary>HellRaiser rapid fire — chirp + zing.</summary>
    public static void HellRaiserFire(float pan, float intensity = 0.8f)
    {
        float p = MathH.Clamp(intensity, 0.2f, 1.4f);
        AddVoice(new Voice { Type = WaveType.Triangle, Freq = 1800 + p * 620, FreqEnd = 820 + p * 180,
            Volume = 0.16f * p, Duration = 0.085f, Attack = 0.004f, Decay = 2f });
        AddVoice(new Voice { Type = WaveType.Square, Freq = 2500 + p * 700, FreqEnd = 1400,
            Volume = 0.1f * p, Duration = 0.07f, Attack = 0.003f, Decay = 2f });
        AddVoice(new Voice { Type = WaveType.Noise, Freq = 1900, FreqEnd = 1900,
            Volume = 0.12f * p, Duration = 0.09f, Attack = 0.004f, Decay = 2f });
    }

    /// <summary>Enemy launch — square wave drop.</summary>
    public static void EnemyLaunch(float pan)
    {
        AddVoice(new Voice { Type = WaveType.Square, Freq = 140, FreqEnd = 90,
            Volume = 0.15f, Duration = 0.24f, Attack = 0.01f, Decay = 1.2f });
    }

    /// <summary>Explosion hit — triangle drop + noise.</summary>
    public static void Hit(float pan, float size = 0.6f)
    {
        AddVoice(new Voice { Type = WaveType.Triangle, Freq = 320, FreqEnd = 80,
            Volume = 0.28f + size * 0.1f, Duration = 0.4f, Attack = 0.01f, Decay = 1.0f });
        AddVoice(new Voice { Type = WaveType.Noise, Freq = 3500, FreqEnd = 600,
            Volume = 0.18f + size * 0.08f, Duration = 0.38f, Attack = 0.01f, Decay = 1.2f });
    }

    /// <summary>Ground impact — deep sub boom + noise.</summary>
    public static void Impact(float pan, bool heavy = false)
    {
        float f = heavy ? 70 : 105;
        AddVoice(new Voice { Type = WaveType.Sine, Freq = f, FreqEnd = heavy ? 22 : 36,
            Volume = heavy ? 0.42f : 0.3f, Duration = 1.1f, Attack = 0.01f, Decay = 0.8f });
        AddVoice(new Voice { Type = WaveType.Noise, Freq = heavy ? 600 : 1800, FreqEnd = heavy ? 80 : 160,
            Volume = heavy ? 0.4f : 0.32f, Duration = 0.95f, Attack = 0.01f, Decay = 1f });
    }

    /// <summary>City destroyed — cascading tones.</summary>
    public static void CityDestroyed(float pan)
    {
        for (int i = 0; i < 4; i++)
        {
            float f = 260 - i * 22;
            AddVoice(new Voice { Type = i % 2 == 0 ? WaveType.Square : WaveType.Sawtooth,
                Freq = f, FreqEnd = MathF.Max(40, f * 0.24f),
                Volume = 0.14f, Duration = 0.3f, Attack = 0.01f, Decay = 1.2f });
        }
    }

    /// <summary>EMP — rising sweep + noise wash.</summary>
    public static void EMP()
    {
        AddVoice(new Voice { Type = WaveType.Triangle, Freq = 220, FreqEnd = 1320,
            Volume = 0.32f, Duration = 0.45f, Attack = 0.03f, Decay = 0.5f });
        AddVoice(new Voice { Type = WaveType.Triangle, Freq = 1320, FreqEnd = 150,
            Volume = 0.22f, Duration = 0.5f, Attack = 0.01f, Decay = 1f });
        AddVoice(new Voice { Type = WaveType.Noise, Freq = 1600, FreqEnd = 1600,
            Volume = 0.22f, Duration = 0.8f, Attack = 0.01f, Decay = 1f });
    }

    /// <summary>Wave cleared fanfare — ascending tones.</summary>
    public static void WaveCleared()
    {
        for (int i = 0; i < 3; i++)
            AddVoice(new Voice { Type = WaveType.Triangle, Freq = 320 + i * 100, FreqEnd = 220 + i * 50,
                Volume = 0.17f, Duration = 0.25f, Attack = 0.01f, Decay = 1.2f });
    }

    /// <summary>Game over — descending sawtooth.</summary>
    public static void GameOver()
    {
        AddVoice(new Voice { Type = WaveType.Sawtooth, Freq = 170, FreqEnd = 42,
            Volume = 0.22f, Duration = 1.2f, Attack = 0.01f, Decay = 0.7f });
    }

    /// <summary>Incoming warning — triangle chirp.</summary>
    public static void Incoming(float pan, float intensity = 0.55f)
    {
        AddVoice(new Voice { Type = WaveType.Triangle, Freq = 520 + intensity * 200, FreqEnd = 260,
            Volume = 0.1f + intensity * 0.09f, Duration = 0.22f, Attack = 0.02f, Decay = 1.5f });
    }

    // Continuous phalanx oscillator state
    static float _phLevel; // smoothed gain target (0..1)
    static float _phPhaseHum;  // square 108Hz
    static float _phPhaseBuzz; // sawtooth 610Hz (LFO-modulated)
    static float _phPhaseLfo;  // triangle 28Hz LFO

    /// <summary>Set phalanx level from FireMix (called every frame from Update).</summary>
    public static void SetPhalanxLevel(float level)
    {
        _phLevel += (MathH.Clamp(level, 0, 1) - _phLevel) * 0.15f;
    }

    /// <summary>Near miss whoosh.</summary>
    public static void NearMiss(float pan, float intensity = 0.75f)
    {
        AddVoice(new Voice { Type = WaveType.Noise, Freq = 1700 + intensity * 600, FreqEnd = 480,
            Volume = 0.16f + intensity * 0.12f, Duration = 0.28f, Attack = 0.03f, Decay = 1.5f });
        AddVoice(new Voice { Type = WaveType.Triangle, Freq = 760 + intensity * 260, FreqEnd = 120,
            Volume = 0.06f + intensity * 0.06f, Duration = 0.24f, Attack = 0.01f, Decay = 1.5f });
    }

    /// <summary>Thunder rumble for storm weather.</summary>
    public static void Thunder(float pan = 0.5f, float intensity = 0.7f)
    {
        AddVoice(new Voice { Type = WaveType.Noise, Freq = 620, FreqEnd = 120,
            Volume = 0.24f + intensity * 0.22f, Duration = 1.8f, Attack = 0.09f, Decay = 0.6f });
        AddVoice(new Voice { Type = WaveType.Sine, Freq = 58 + intensity * 18, FreqEnd = 31,
            Volume = 0.16f + intensity * 0.14f, Duration = 1.9f, Attack = 0.01f, Decay = 0.6f });
    }

    class Voice
    {
        public WaveType Type;
        public float Freq, FreqEnd;
        public float Volume;
        public float Duration;
        public float Elapsed;
        public float Attack = 0.01f;
        public float Decay = 1f;
    }

    enum WaveType { Sine, Square, Sawtooth, Triangle, Noise }
}
