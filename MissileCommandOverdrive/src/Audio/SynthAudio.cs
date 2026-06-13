using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Raylib_cs;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive.Audio;

/// <summary>
/// Procedural synthesizer — sample-accurate stereo engine (plan §5 2.3/2.4/2.5).
/// Default mode renders on raylib's audio thread via SetAudioStreamCallback;
/// MCOD_AUDIO_POLLED=1 selects the game-thread IsAudioStreamProcessed pump instead
/// (callback and polling are mutually exclusive per stream — chosen once at Init, §4.6).
/// All game→audio crossings ride a lock-free SPSC command ring; the render path
/// performs zero allocations, takes no locks, and calls no raylib functions.
/// Mix architecture (§5 3.3/3.4): four buses (Music/Sfx/Ambient/Ui) → Freeverb-lite
/// send return → sidechain duck on Music+Ambient → glue compressor → tanh soft clip.
/// Mechanical-identity loops (§5 5.5) live in a generic LoopChannel pool fed by
/// per-frame param sends; a render-side watchdog fades any channel whose params
/// go quiet for 0.25 s, so entity death never needs an explicit note-off.
/// </summary>
public static class SynthAudio
{
    const int SampleRate = 44100;
    const float InvSR = 1f / SampleRate;
    const int MaxVoices = 64;
    const int MaxTails = 16;         // 3 ms fade-out splices for stolen voices
    const int CmdRingSize = 256;     // power of two (ring index mask)

    // §5 5.5 mechanical-identity loop pool: fixed channel assignments
    const int MaxLoopCh = 8;
    const int MaxPhalanxCh = 4;      // channels 0–3: per-turret CIWS
    const int LoopChHellRaiser = 4;  // hydraulic hiss + lift servo
    const int LoopChMsShield = 5;    // mothership deflector hum
    const int LoopChMsEngine = 6;    // mothership hull drone (7 spare)
    const int PolledFrames = 4096;   // matches SetAudioStreamBufferSizeDefault
    const float SubMonoHz = 120f;    // voices below this stay centered (mono sub)
    const float PanCeiling = 0.8f;   // hard-pan limit: pan compressed to 0.5 ± 0.4
    const float Sqrt2 = 1.41421356f; // pan-law makeup so center == legacy mono level

    const byte PrioBeat = 0, PrioLow = 1, PrioMed = 2, PrioHigh = 3;

    // §5 3.3 mix buses — Music+Ambient are sidechain-duck targets; Sfx/Ui never duck
    const byte BusMusic = 0, BusSfx = 1, BusAmbient = 2, BusUi = 3;
    const int BusCount = 4;

    // One-pole smoothing coefficients (per sample) and the 3 ms → −60 dB steal fade
    static readonly float MasterCoef = 1f - MathF.Exp(-1f / (0.005f * SampleRate));
    static readonly float PanCoef = 1f - MathF.Exp(-1f / (0.004f * SampleRate));
    static readonly float PitchCoef = 1f - MathF.Exp(-1f / (0.010f * SampleRate));
    static readonly float DangerCoef = 1f - MathF.Exp(-1f / (0.030f * SampleRate));
    static readonly float TailFadeMul = MathF.Exp(MathF.Log(0.001f) / (0.003f * SampleRate));

    // §5 5.5 loop-channel smoothing: per-kind level speeds (slow for the shield
    // hum so the fade-out outlives its octave-drop freq glide) + the watchdog —
    // a channel not param-updated for 0.25 s ramps to −60 dB over ~80 ms.
    static readonly float LoopFastCoef = 1f - MathF.Exp(-1f / (0.060f * SampleRate));
    static readonly float LoopMedCoef = 1f - MathF.Exp(-1f / (0.120f * SampleRate));
    static readonly float LoopSlowCoef = 1f - MathF.Exp(-1f / (0.180f * SampleRate));
    static readonly float LoopFreqCoef = 1f - MathF.Exp(-1f / (0.060f * SampleRate));
    const int WatchdogSamples = (int)(0.25f * SampleRate);
    static readonly float WatchdogMul = MathF.Exp(MathF.Log(0.001f) / (0.080f * SampleRate));

    // 3.3 Freeverb-lite: comb feedback/damping, mono input gain, wet return level
    const float CombFb = 0.84f;
    const float RevDamp = 0.25f;
    const float RevInGain = 0.15f;
    const float WetGain = 0.7f;

    // 3.3 glue compressor (peak follower, soft 3:1 over threshold) + tanh stage.
    // ClipDrive folds the legacy 0.8 pre-gain into the plan's tanh(1.5x); scale
    // keeps small-signal loudness ≈ legacy while transient stacks bloom, never flat-top.
    const float CompThresh = 0.5f;
    const float CompInvRatio = 1f / 3f;
    const float CompMakeup = 1.1f;
    static readonly float CompAtkCoef = 1f - MathF.Exp(-1f / (0.005f * SampleRate));
    static readonly float CompRelCoef = 1f - MathF.Exp(-1f / (0.120f * SampleRate));
    const float ClipDrive = 1.2f;
    const float ClipScale = 0.72f;

    // 3.4 sidechain duck: 5 ms one-pole attack window to ~99 % of target, then
    // exponential release (τ 60 ms ⇒ ~95 % recovered at the plan's 180 ms)
    const float DuckCap = 0.6f;
    const int DuckAtkSamples = (int)(0.005f * SampleRate);
    static readonly float DuckAtkCoef = 1f - MathF.Exp(-4.6f / DuckAtkSamples);
    static readonly float DuckRelMul = MathF.Exp(-1f / (0.060f * SampleRate));

    // 3.4 dedicated mono sub voice: 90→45 Hz glide, −60 dB amp decay over 0.55 s,
    // fixed tanh(3x) drive, 2 ms broadband click transient
    const float SubGain = 0.5f;
    const float SubClickGain = 0.35f;
    static readonly float SubGlideCoef = 1f - MathF.Exp(-1f / (0.090f * SampleRate));
    static readonly float SubDecayMul = MathF.Exp(-6.907755f / (0.55f * SampleRate));
    static readonly float SubClickMul = MathF.Exp(MathF.Log(0.001f) / (0.002f * SampleRate));

    static AudioStream _stream;
    static bool _initialized;
    static bool _polled;
    static float _masterVol = 0.54f;
    static bool _muted;

    // ── §5 6.5 procedural music director ──
    // 16-step sequencer advanced SAMPLE-ACCURATELY inside the callback. Stems are
    // gated by s.Intensity (§4.5, the ONE tension signal) and spawned directly
    // into the voice pool at the exact sample offset of each step boundary, all on
    // BusMusic so the existing sidechain duck pulls them under impacts and the
    // music volume slider/mute govern level. Anti-fatigue (§R5): sparse by default,
    // per-wave pattern + key rotation, swing + velocity humanization via _prodRand.
    const int SeqSteps = 16;
    // Tempo floor/ceiling in seconds-per-16th. The director is intentionally slower
    // and steadier than the old kick clock so a 30-min session never feels frantic.
    const float SeqStepHi = 0.20f;   // ~75 BPM (16ths), low intensity / Title-Shop
    const float SeqStepLo = 0.125f;  // ~120 BPM at peak intensity
    const float SeqSwing = 0.012f;    // base off-beat delay (s); humanized per hit
    // Minor-pentatonic scale degrees (semitones) — the harmonic palette for the
    // whole director. Roots transpose by wave; bass/arp/lead pick from this set.
    static readonly int[] PentaSemis = { 0, 3, 5, 7, 10 };
    // Per-pattern step bitmasks tested as (mask & (1 << step)), so the RIGHTMOST
    // bit is step 0 — the downbeat. Read each literal right-to-left for the groove.
    // Two rotating variants per stem (selected by wave parity) keep the loop from
    // being audibly periodic across a long session.
    // Kick: four-on-the-floor (0,4,8,12) / a syncopated variant.
    static readonly ushort[] KickPat = { 0b0001_0001_0001_0001, 0b0001_0001_0010_0001 };
    // Hat: straight 8ths on the off-steps / a busier variant.
    static readonly ushort[] HatPat = { 0b0101_0101_0101_0101, 0b0101_0100_1101_0100 };
    // Bass: roots on 0 and the &-of-3 / a walking variant.
    static readonly ushort[] BassPat = { 0b0001_0100_0100_0001, 0b0010_0001_0001_1001 };
    // Arp: 16th texture on the up-steps / a sparser variant.
    static readonly ushort[] ArpPat = { 0b1010_1010_1010_1010, 0b0100_1010_0101_0010 };
    // Lead: a couple of stabs per bar, off the downbeat.
    static readonly ushort[] LeadPat = { 0b0100_0000_0001_0000, 0b0000_0100_0000_0001 };
    // Boss ostinato: a heavy two-against-three pulse / an alternating variant.
    static readonly ushort[] BossPat = { 0b0100_0001_0100_0001, 0b0000_0101_0000_0101 };
    // Arp/lead degree walks (indices into PentaSemis), one per pattern variant.
    static readonly int[][] ArpWalk = {
        new[] { 0, 2, 4, 2, 1, 3, 4, 3 },
        new[] { 0, 3, 2, 4, 1, 4, 2, 3 },
    };

    // §6.5 cross-thread music params via a DOUBLE-BUFFERED SEQLOCK (§4.5: NOT bare
    // multi-field struct writes — Volatile can't make a multi-field publish atomic).
    // Producer (game thread) writes the back buffer, then bumps an odd→even version
    // with release fences; consumer (audio thread) snapshots between two equal even
    // reads. These are continuous "latest-state" params (intensity/key/phase), which
    // is exactly what a seqlock is for — the discrete-event ring stays for spawns.
    struct MusicParams
    {
        public float Intensity;  // s.Intensity, [0,1] — the master gate + tempo driver
        public float TempoScale; // slow-mo factor [0.25,1] — stretches the step period
        public int Root;         // transpose in semitones, keyed to wave number
        public byte PatSel;      // pattern-variant select (0/1), rotates per wave
        public bool Boss;        // (s.Mothership != null || s.Demon != null)
        public bool Quiet;       // Title/Shop/GameOver — strip to a pad only
    }
    static MusicParams _mpA, _mpB;       // double buffer
    static int _mpVersion;               // even = stable; odd = write in progress
    // Audio-thread sequencer state (only the render path touches these post-Init)
    static int _seqStep;                 // 0..15 current step
    static float _seqSamplesToStep;      // fractional sample countdown to next step
    static MusicParams _mpLive;          // last consistent snapshot (render thread)

    enum WaveType : byte { Sine, Square, Sawtooth, Triangle, Noise }
    enum FilterMode : byte { None, LP, BP, HP }
    enum CmdKind : byte { Spawn, Params, Loop, Duck, Sub }
    enum LoopKind : byte { None, Phalanx, HrHydraulic, MsShield, MsEngine }

    // ── Audio-thread state (only the render thread touches these after Init) ──

    /// <summary>Live voice. Fully precomputed producer-side and shipped by value.</summary>
    struct Voice
    {
        public bool Active;
        public byte Type;        // WaveType
        public byte Priority;
        public byte FilterMode;  // FilterMode (None = bypass)
        public float Phase;      // accumulated oscillator phase, wrapped to [0,1)
        public float Freq;       // current Hz (linear sweep)
        public float FreqStep;   // Hz per sample
        public float Volume;
        public float Env;
        public int AttackLeft;   // samples remaining in attack
        public float AttackMul;  // exp-rise coefficient: env = 1 − (1−env)·mul
        public float DecayMul;   // exp-decay multiplier per sample
        public int SamplesLeft;
        public float GainL, GainR;   // smoothed constant-power gains
        public float TGainL, TGainR;
        public float FilterF;    // Chamberlin f coefficient, clamped < 1 (§4.8)
        public float FilterFMul; // per-sample log-space cutoff sweep multiplier
        public float FilterQ;    // damping (1/Q)
        public float FLow, FBand;
        public byte Bus;         // mix bus (BusMusic/Sfx/Ambient/Ui)
        public float Reverb;     // Freeverb-lite send 0..1 (0 = dry)
    }

    /// <summary>3 ms fade splice keeping a stolen voice's output click-free.</summary>
    struct Tail
    {
        public bool Active;
        public byte Type;
        public byte FilterMode;
        public byte Bus;         // splice stays on the stolen voice's bus (duck consistency)
        public float Phase, PhaseInc;
        public float Amp;        // env·volume at steal time, decays by TailFadeMul
        public float GainL, GainR;
        public float FilterF, FilterQ, FLow, FBand;
    }

    /// <summary>§5 5.5 mechanical-identity loop voice. The game thread re-sends
    /// params every frame (the original phalanx-channel pattern, generalized);
    /// the render path watchdog-fades any channel quiet for 0.25 s.</summary>
    struct LoopChannel
    {
        public byte Kind;            // LoopKind — channel state resets when it changes
        public byte Bus;
        public bool Stale;           // watchdog flag, recomputed once per block
        public float LevelCoef;      // per-kind one-pole speed (set at kind bind)
        public float Level, TLevel;
        public float Aux, TAux;      // kind-specific second level (rattle/servo)
        public float Freq, TFreq;    // primary Hz
        public float FilterF, TFilterF; // SVF f coefficient (producer-precomputed)
        public float GainL, GainR, TGainL, TGainR;
        public int Stamp;            // _loopClock at last param update (watchdog)
        public float Ph0, Ph1, Ph2;
        public float FLow, FBand;
    }

    struct AudioCmd
    {
        public CmdKind Kind;
        public byte B0;              // Loop: LoopKind
        public int I0;               // Loop: channel index
        public float P0, P1, P2;     // Params: danger/pitch/master · Loop: level/freq/filterF
        public float P3, P4, P5;     // Loop: aux/gL/gR
        public Voice Voice;          // Spawn payload
    }

    static readonly Voice[] _voices = new Voice[MaxVoices];
    static readonly Tail[] _tails = new Tail[MaxTails];
    static readonly LoopChannel[] _loops = new LoopChannel[MaxLoopCh];
    static int _loopClock;           // render-path sample counter (watchdog timebase)
    static readonly short[] _polledBuffer = new short[PolledFrames * 2];

    // Per-sample bus accumulators (audio thread only; cleared every sample)
    static readonly float[] _busL = new float[BusCount];
    static readonly float[] _busR = new float[BusCount];

    // 3.3 Freeverb-lite delay lines, tuned for 44.1 kHz; right bank +23 samples
    // for stereo width. Allocated at type init — before the stream callback can
    // exist — and never resized, so the callback path stays allocation-free.
    struct Comb { public float[] Buf; public int Idx; public float Store; }
    struct Allpass { public float[] Buf; public int Idx; }
    static readonly Comb[] _combs =
    {
        new() { Buf = new float[1116] }, new() { Buf = new float[1188] },
        new() { Buf = new float[1277] }, new() { Buf = new float[1356] },
        new() { Buf = new float[1139] }, new() { Buf = new float[1211] },
        new() { Buf = new float[1300] }, new() { Buf = new float[1379] },
    };
    static readonly Allpass[] _allpasses =
    {
        new() { Buf = new float[556] }, new() { Buf = new float[441] },
        new() { Buf = new float[579] }, new() { Buf = new float[464] },
    };

    // 3.4 sidechain duck (audio-thread): max-of-triggers target, attack window countdown
    static float _duckEnv, _duckTarget;
    static int _duckAtkLeft;

    // 3.4 dedicated retriggered mono sub voice (stacked pool subs phase-cancel)
    static float _subPhase, _subFreq = 45f, _subEnv, _subClick;

    // 3.3 glue-compressor peak envelope
    static float _compEnv;

    // SPSC ring: game thread produces (tail), render thread consumes (head).
    // Payload is written before the Volatile tail publish (release/acquire pair).
    static readonly AudioCmd[] _cmdRing = new AudioCmd[CmdRingSize];
    static int _cmdHead;
    static int _cmdTail;

    // Smoothed cross-thread params (render-thread copies)
    static float _atMaster = 0.54f, _atMasterT = 0.54f;
    static float _atPitch = 1f, _atPitchT = 1f;
    static float _atDanger, _atDangerT;

    // Drone/hum phases. The 40 Hz root stays centered (mono sub); the saw/tri
    // partners are detuned ±0.3 Hz L/R for stereo width (§5 2.5).
    static float _ambPhase1, _ambPhase2L, _ambPhase2R, _ambPhase3L, _ambPhase3R, _humPhase;

    static uint _noiseState = 0x9E3779B9u; // inline xorshift32 — no Random.Shared here

    // §5 4.5 producer-side anti-repetition RNG (game thread only — never shared
    // with the audio thread's _noiseState). Feeds spawn jitter + micro-variants.
    static uint _prodRand = 0x243F6A88u;

    // §5 5.5 game-thread previous door/lift samples (hydraulic stroke-rate deltas)
    static float _hrPrevDoor, _hrPrevLift;
    // Was the hydraulic channel driven last frame? Reseed prev=current across a
    // destroyed/paused gap so the first live frame yields a 0 stroke rate — a
    // field-repair snaps DoorOpen/Lift to 0 from the parked 0.5/0.45 and would
    // otherwise read as a full-level hiss burst keyed to no actual motion.
    static bool _hrWasLive;

    static float Prod01()
    {
        uint x = _prodRand;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _prodRand = x;
        return (x >> 8) * (1f / 16777216f);
    }

    // ── Lifecycle ──

    public static void Init()
    {
        if (_initialized) return;
        Raylib.InitAudioDevice();
        _polled = Environment.GetEnvironmentVariable("MCOD_AUDIO_POLLED") == "1";
        // 2×4096-frame internal buffers: GC-pause headroom. Must precede LoadAudioStream.
        Raylib.SetAudioStreamBufferSizeDefault(PolledFrames);
        _stream = Raylib.LoadAudioStream(SampleRate, 16, 2);
        if (!_polled)
        {
            unsafe
            {
                Raylib.SetAudioStreamCallback(_stream, &AudioCallback);
            }
        }
        Raylib.PlayAudioStream(_stream);
        _initialized = true;
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        Raylib.StopAudioStream(_stream);
        Raylib.UnloadAudioStream(_stream);
        Raylib.CloseAudioDevice();
        _initialized = false;
    }

    public static void ToggleMute() { _muted = !_muted; }
    public static bool IsMuted => _muted;
    public static void SetVolume(float v) { _masterVol = MathH.Clamp(v, 0, 1); }
    public static float Volume => _masterVol;

    /// <summary>Call every frame. dt must be rawDt (§4.6 — pumping/params never stall);
    /// timeScale only bends beat tempo and voice pitch.</summary>
    // Game-thread cache: the title attract backdrop runs a live auto-played sim
    // that can fire run-level stings (GameOver/WaveStab); suppress them so the
    // title screen stays musically quiet.
    static bool _titleSilent;

    public static void Update(GameState s, float dt, float timeScale = 1f)
    {
        if (!_initialized) return;
        _titleSilent = s.Phase == GamePhase.Title;

        // §4.7: slow-mo only bends voice pitch + (via the seqlock) the sequencer
        // tempo; it is applied as a parameter, never as a recompute on this thread.
        float ts = MathH.Clamp(timeScale, 0.25f, 1f);

        // Danger hum, slow-mo pitch bend, master/mute — cross as one command
        Enqueue(new AudioCmd
        {
            Kind = CmdKind.Params,
            P0 = s.Danger,
            P1 = MathH.Lerp(0.8f, 1f, (ts - 0.25f) / 0.75f),
            P2 = _muted ? 0f : _masterVol,
        });

        // §5 5.5 mechanical-identity loops, batched per frame. Paused is
        // excluded: the sim gate freezes the source fields, so phalanx channels
        // get explicit zeros (one-pole fade; preserved state re-sent on resume)
        // while HellRaiser/Mothership simply stop sending and the render-side
        // watchdog fades them — the same path that covers entity death.
        bool live = !s.GameOver && s.Phase != GamePhase.Paused;

        // Per-turret CIWS channels panned by turret X: saw whine at
        // 80 + SpinSpeed·14 Hz through a BP keyed to the same spin, plus a
        // 24 Hz-gated feed rattle scaled by FireMix. Idle spin (1.5) is silent.
        int ch = 0;
        if (live)
        {
            foreach (var px in s.Phalanxes)
            {
                if (ch >= MaxPhalanxCh) break;
                float spin = px.Destroyed ? 0f : MathH.Clamp(px.SpinSpeed, 0f, 28f);
                float toneHz = 80f + spin * 14f;
                SendLoop(ch++, LoopKind.Phalanx,
                    level: MathH.Clamp((spin - 1.6f) * (1f / 26.4f), 0f, 1f),
                    freq: toneHz,
                    filterHz: toneHz * 3f,
                    aux: px.Destroyed ? 0f : MathH.Clamp(px.FireMix, 0f, 1f),
                    pan: MathH.Clamp(px.X / s.W, 0f, 1f));
            }
        }
        for (; ch < MaxPhalanxCh; ch++) SendLoop(ch, LoopKind.Phalanx, 0f, 80f, 240f, 0f, 0.5f);

        // HellRaiser hydraulics: BP-noise hiss ∝ door/lift stroke rate
        // (2.7/s = full door speed = loudest), 140 Hz servo while the lift moves
        var hr = s.HellRaiser;
        if (live && hr != null && !hr.Destroyed)
        {
            // First live frame after a destroyed/paused gap: reseed the baseline
            // to the current pose so this frame's delta is 0. The destroyed visual
            // is parked off-pose (Lift 0.45 / DoorOpen 0.5), and field-repair snaps
            // it to 0 the same frame it clears Destroyed — without this the stroke
            // rate would spike to ~0.95/dt and pop a full-level hiss with no motion.
            if (!_hrWasLive) { _hrPrevDoor = hr.DoorOpen; _hrPrevLift = hr.Lift; }
            float strokeRate = dt > 1e-5f
                ? (MathF.Abs(hr.DoorOpen - _hrPrevDoor) + MathF.Abs(hr.Lift - _hrPrevLift)) / dt
                : 0f;
            bool lifting = hr.State == "rising" || hr.State == "lowering";
            SendLoop(LoopChHellRaiser, LoopKind.HrHydraulic,
                level: MathH.Clamp(strokeRate * (1f / 2.7f), 0f, 1f),
                freq: 140f,
                filterHz: 1150f,
                aux: lifting ? 1f : 0f,
                pan: MathH.Clamp(hr.X / s.W, 0f, 1f));
            // Advance the baseline only while driven; idle/destroyed frames leave
            // it frozen, so the next live frame reseeds rather than diffing stale.
            _hrPrevDoor = hr.DoorOpen;
            _hrPrevLift = hr.Lift;
            _hrWasLive = true;
        }
        else _hrWasLive = false;

        // Mothership: deflector hum while ShieldActive — the target freq drops
        // an octave the frame the shield falls, so the slow fade-out glides down
        // (reads as power loss) — plus a hull drone on the Ambient bus, where
        // the sidechain ducks it under impacts/music accents.
        var ms = s.Mothership;
        if (live && ms != null && ms.Active)
        {
            float msPan = MathH.Clamp(ms.X / s.W, 0f, 1f);
            SendLoop(LoopChMsShield, LoopKind.MsShield,
                level: ms.ShieldActive ? 1f : 0f,
                freq: ms.ShieldActive ? 164f : 82f,
                filterHz: 0f, aux: 0f, pan: msPan);
            SendLoop(LoopChMsEngine, LoopKind.MsEngine,
                level: MathH.Clamp(ms.AppearTime * 0.5f, 0f, 1f), // swells with the fade-in
                freq: 47f,
                filterHz: 0f, aux: 0f,
                pan: 0.5f); // <120 Hz mono rule — detuned partials carry the width
        }

        // §6.5: publish the music-director state to the audio thread (seqlock).
        // The 16-step sequencer itself runs sample-accurately inside the callback;
        // here we only cross the latest tension/key/phase. Boss-active follows the
        // plan's contract: any boss instance present (not just .Active).
        PublishMusic(s, ts);

        // Polled fallback: pump the same synth core from the game thread
        if (_polled && Raylib.IsAudioStreamProcessed(_stream))
        {
            unsafe
            {
                fixed (short* p = _polledBuffer)
                {
                    RenderBlock(p, PolledFrames);
                    Raylib.UpdateAudioStream(_stream, p, PolledFrames);
                }
            }
        }
    }

    // ── Game-thread producers ──

    /// <summary>Spawn parameters. Pan 0..1 (0.5 center); Attack is a fraction of
    /// Duration; noise voices reinterpret Freq/FreqEnd as a log-space cutoff sweep.</summary>
    struct VoiceDesc
    {
        public WaveType Type;
        public float Freq, FreqEnd;
        public float Volume;
        public float Duration;
        public float Attack;
        public float Decay;
        public float Pan;
        public byte Priority;
        public FilterMode Filter; // noise defaults to LP when left at None
        public float Q;           // SVF damping (1/Q)
        public byte Bus;          // mix bus; defaults to Sfx
        public float Reverb;      // Freeverb-lite send 0..1

        public VoiceDesc()
        {
            Type = WaveType.Sine;
            Freq = 440; FreqEnd = 440;
            Volume = 0; Duration = 0.1f;
            Attack = 0.01f; Decay = 1f;
            Pan = 0.5f; Priority = PrioLow;
            Filter = FilterMode.None; Q = 1f;
            Bus = BusSfx; Reverb = 0f;
        }
    }

    static void AddVoice(in VoiceDesc d)
    {
        // §5 4.5 anti-repetition: ±4% pitch / ±10% volume jitter applied at
        // spawn on the game thread — never per sample
        float jitter = 0.96f + Prod01() * 0.08f;
        float f0 = d.Freq * jitter;
        float f1 = d.FreqEnd * jitter;
        float volume = d.Volume * (0.9f + Prod01() * 0.2f);

        float pan = d.Pan;
        if (f0 < SubMonoHz) pan = 0.5f; // sub stays centered
        pan = 0.5f + (MathH.Clamp(pan, 0f, 1f) - 0.5f) * PanCeiling;
        float a = pan * (MathF.PI * 0.5f);

        int dur = (int)(d.Duration * SampleRate);
        if (dur < 16) dur = 16;
        int atk = (int)(d.Attack * dur);
        if (atk < 0) atk = 0;
        if (atk > dur - 1) atk = dur - 1;
        // Old linear envelope hit zero at 1/(1+Decay) of the post-attack window;
        // map that to an exponential reaching −60 dB over the same span
        float decaySamples = MathF.Max(1f, (dur - atk) / (1f + MathF.Max(0f, d.Decay)));

        var v = new Voice
        {
            Active = true,
            Type = (byte)d.Type,
            Priority = d.Priority,
            Volume = volume,
            Freq = f0,
            FreqStep = (f1 - f0) / dur,
            Env = atk == 0 ? 1f : 0f,
            AttackLeft = atk,
            AttackMul = atk > 0 ? MathF.Exp(-4.6f / atk) : 0f,
            DecayMul = MathF.Exp(-6.907755f / decaySamples),
            SamplesLeft = dur,
            TGainL = MathF.Cos(a) * Sqrt2,
            TGainR = MathF.Sin(a) * Sqrt2,
            Bus = d.Bus,
            Reverb = d.Reverb,
        };
        v.GainL = v.TGainL;
        v.GainR = v.TGainR;

        // 2.4: noise Freq/FreqEnd become SVF cutoff sweeps (filters are noise-only;
        // tonal voices already sweep their oscillator)
        if (d.Type == WaveType.Noise)
        {
            var mode = d.Filter == FilterMode.None ? FilterMode.LP : d.Filter;
            // §4.8: f < 1 ⇒ cutoff < SR/6; all sweep targets (max 3500 Hz) fit
            float fs = MathH.Clamp(MathF.Tau * f0 * InvSR, 1e-4f, 0.95f);
            float fe = MathH.Clamp(MathF.Tau * f1 * InvSR, 1e-4f, 0.95f);
            v.FilterMode = (byte)mode;
            v.FilterF = fs;
            v.FilterFMul = MathF.Exp(MathF.Log(fe / fs) / dur);
            v.FilterQ = d.Q;
        }

        Enqueue(new AudioCmd { Kind = CmdKind.Spawn, Voice = v });
    }

    /// <summary>3.4 sidechain: duck the Music+Ambient buses. Concurrent triggers
    /// take the max envelope, never multiply (no seasick pumping in a barrage).</summary>
    static void Duck(float depth) =>
        Enqueue(new AudioCmd { Kind = CmdKind.Duck, P0 = MathH.Clamp(depth, 0f, DuckCap) });

    /// <summary>3.4: (re)trigger the single dedicated mono sub-bass voice.</summary>
    static void TriggerSub(float magnitude) =>
        Enqueue(new AudioCmd { Kind = CmdKind.Sub, P0 = MathH.Clamp(magnitude, 0f, 1f) });

    /// <summary>§5 5.5: per-frame loop-channel param send. filterHz is converted
    /// to the Chamberlin f coefficient here (producer side — no per-sample
    /// transcendentals on the render path).</summary>
    static void SendLoop(int ch, LoopKind kind, float level, float freq, float filterHz,
        float aux, float pan)
    {
        float a = (0.5f + (MathH.Clamp(pan, 0f, 1f) - 0.5f) * PanCeiling) * (MathF.PI * 0.5f);
        Enqueue(new AudioCmd { Kind = CmdKind.Loop, B0 = (byte)kind, I0 = ch,
            P0 = level, P1 = freq,
            P2 = MathH.Clamp(MathF.Tau * filterHz * InvSR, 1e-4f, 0.95f), // §4.8 f < 1
            P3 = aux, P4 = MathF.Cos(a) * Sqrt2, P5 = MathF.Sin(a) * Sqrt2 });
    }

    /// <summary>§6.5 seqlock publish (game thread). Writes the inactive buffer,
    /// then flips the version odd→even so the consumer only ever reads a fully
    /// consistent snapshot. Per-wave pattern + key rotation derives from s.Level
    /// (a deterministic transpose so a 30-min session never loops audibly — and
    /// drawn from neither the plan stream nor _prodRand, so SELFTEST is untouched).</summary>
    static void PublishMusic(GameState s, float tempoScale)
    {
        // Root walks a perfect-fourth circle by wave so consecutive waves modulate;
        // ±7 semitone span keeps it in a comfortable register over a long session.
        int root = ((s.Level * 5) % 13) - 6;
        bool boss = s.Mothership != null || s.Demon != null;
        // Title/Shop/GameOver strip to a pad. (Paused keeps the current pad too —
        // Update still pumps every phase per §4.6, and a frozen pad reads as "held".)
        bool quiet = s.Phase == GamePhase.Title || s.Phase == GamePhase.Shop
            || s.Phase == GamePhase.GameOver || s.Phase == GamePhase.Paused
            || s.Phase == GamePhase.Ceremony; // strip to the pad during the death ceremony

        var p = new MusicParams
        {
            Intensity = MathH.Clamp(s.Intensity, 0f, 1f),
            TempoScale = tempoScale,
            Root = root,
            PatSel = (byte)(s.Level & 1),
            Boss = boss,
            Quiet = quiet,
        };

        // Seqlock over a double buffer. Versions are always even when stable; bit
        // (version>>1)&1 selects the CURRENTLY-PUBLISHED buffer, so the producer
        // writes the OTHER one, then advances the version by 2 — which both flips
        // that select bit (publishing the fresh buffer) and signals readers that
        // raced an in-progress write to retry. The odd intermediate value is the
        // "write in progress" marker the consumer spins on.
        int ver = _mpVersion;                  // even (single producer)
        Volatile.Write(ref _mpVersion, ver + 1); // odd — write in progress
        if ((((ver >> 1) & 1)) == 0) _mpB = p; else _mpA = p; // write the idle buffer
        Volatile.Write(ref _mpVersion, ver + 2); // even — publishes the buffer just written
    }

    static void Enqueue(in AudioCmd cmd)
    {
        int tail = _cmdTail; // single producer: game thread
        int next = (tail + 1) & (CmdRingSize - 1);
        if (next == Volatile.Read(ref _cmdHead)) return; // full — drop
        _cmdRing[tail] = cmd;                  // write payload…
        Volatile.Write(ref _cmdTail, next);    // …then publish (release)
    }

    // ── Render core (audio thread in callback mode; game thread when polled) ──

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static unsafe void AudioCallback(void* buffer, uint frames)
    {
        // An exception escaping UnmanagedCallersOnly aborts the process — never let one out
        try
        {
            RenderBlock((short*)buffer, (int)frames);
        }
        catch
        {
            short* p = (short*)buffer;
            for (uint i = 0; i < frames * 2; i++) p[i] = 0;
        }
    }

    static unsafe void RenderBlock(short* dst, int frames)
    {
        ProcessCommands();

        // §6.5 snapshot the music-director params once per block (tear-free), then
        // derive this block's step period in samples. Intensity drives tempo; the
        // slow-mo TempoScale stretches it. The per-step countdown is fractional and
        // carried across blocks, so beats land sample-exact regardless of block size.
        SnapshotMusic();
        float stepSec = MathH.Lerp(SeqStepHi, SeqStepLo, _mpLive.Intensity)
            / MathH.Clamp(_mpLive.TempoScale, 0.25f, 1f);
        float stepSamples = stepSec * SampleRate;

        // §5 5.5 watchdog: flag loop channels whose params went quiet for 0.25 s.
        // _loopClock is constant within the block, so this is a per-block check;
        // a fresh stamp from ProcessCommands above clears the flag.
        for (int c = 0; c < MaxLoopCh; c++)
            _loops[c].Stale = _loopClock - _loops[c].Stamp > WatchdogSamples;

        for (int i = 0; i < frames; i++)
        {
            // §6.5 sample-accurate sequencer: when the countdown elapses, advance to
            // the next step and fire its stems AT THIS SAMPLE — the voices spawned
            // below begin rendering from index i, so there is zero frame-quantized
            // jitter. The fractional remainder rolls into the next step period.
            _seqSamplesToStep -= 1f;
            if (_seqSamplesToStep <= 0f)
            {
                _seqStep = (_seqStep + 1) & (SeqSteps - 1);
                FireStep(_seqStep);
                // Swing: delay odd 16ths a hair, humanized so it never sounds robotic.
                float swing = (_seqStep & 1) == 1
                    ? (SeqSwing * (0.6f + (NextNoise() * 0.5f + 0.5f) * 0.8f)) * SampleRate
                    : 0f;
                _seqSamplesToStep += stepSamples + swing;
                if (_seqSamplesToStep < 1f) _seqSamplesToStep = 1f; // guard tiny periods
            }

            _atMaster += (_atMasterT - _atMaster) * MasterCoef;
            _atPitch += (_atPitchT - _atPitch) * PitchCoef;
            _atDanger += (_atDangerT - _atDanger) * DangerCoef;

            // 3.4 duck envelope: one-pole rise toward the max-of-triggers target
            // for the 5 ms window, then exponential release
            if (_duckAtkLeft > 0)
            {
                _duckAtkLeft--;
                _duckEnv += (_duckTarget - _duckEnv) * DuckAtkCoef;
            }
            else
            {
                _duckEnv *= DuckRelMul;
                _duckTarget = _duckEnv; // keep target synced so max-merge stays correct
            }

            _busL[0] = 0f; _busL[1] = 0f; _busL[2] = 0f; _busL[3] = 0f;
            _busR[0] = 0f; _busR[1] = 0f; _busR[2] = 0f; _busR[3] = 0f;
            float revIn = 0f;

            // Ambient drone: centered 40 Hz root + ±0.3 Hz detuned saw/tri partners
            _ambPhase1 += 40f * InvSR; if (_ambPhase1 >= 1f) _ambPhase1 -= 1f;
            float droneC = MathF.Sin(_ambPhase1 * MathF.Tau) * 0.035f;
            _ambPhase2L += 59.7f * InvSR; if (_ambPhase2L >= 1f) _ambPhase2L -= 1f;
            _ambPhase2R += 60.3f * InvSR; if (_ambPhase2R >= 1f) _ambPhase2R -= 1f;
            _ambPhase3L += 79.7f * InvSR; if (_ambPhase3L >= 1f) _ambPhase3L -= 1f;
            _ambPhase3R += 80.3f * InvSR; if (_ambPhase3R >= 1f) _ambPhase3R -= 1f;
            _busL[BusAmbient] += droneC + Sawtooth(_ambPhase2L) * 0.012f + Triangle(_ambPhase3L) * 0.012f;
            _busR[BusAmbient] += droneC + Sawtooth(_ambPhase2R) * 0.012f + Triangle(_ambPhase3R) * 0.012f;

            // Danger hum (170 Hz triangle, centered) → Ambient
            if (_atDanger > 0.34f)
            {
                float dv = MathH.Clamp((_atDanger - 0.34f) * 0.25f, 0, 0.18f);
                _humPhase += 170f * InvSR; if (_humPhase >= 1f) _humPhase -= 1f;
                float hum = Triangle(_humPhase) * dv;
                _busL[BusAmbient] += hum; _busR[BusAmbient] += hum;
            }

            // §5 5.5 mechanical-identity loops → Sfx (MsEngine → Ambient).
            // Per-sample cost vs the old 4-channel phalanx block: each phalanx
            // channel trades the LFO-saw/square pair for saw+SVF+gate (≈ equal);
            // the three new kinds add ≈40 flops + 1 sin worst-case — well under
            // one Freeverb comb.
            for (int c = 0; c < MaxLoopCh; c++)
            {
                ref LoopChannel lc = ref _loops[c];
                if (lc.Stale)
                {
                    // Watchdog auto-fade: −60 dB over ~80 ms, no note-off needed
                    lc.Level *= WatchdogMul;
                    lc.Aux *= WatchdogMul;
                }
                else
                {
                    lc.Level += (lc.TLevel - lc.Level) * lc.LevelCoef;
                    lc.Aux += (lc.TAux - lc.Aux) * lc.LevelCoef;
                }
                if (lc.Level <= 0.0015f && lc.Aux <= 0.0015f) continue;

                lc.Freq += (lc.TFreq - lc.Freq) * LoopFreqCoef;
                lc.GainL += (lc.TGainL - lc.GainL) * PanCoef;
                lc.GainR += (lc.TGainR - lc.GainR) * PanCoef;

                float oL, oR;
                switch (lc.Kind)
                {
                    case (byte)LoopKind.Phalanx:
                    {
                        // Saw whine at 80 + spin·14 Hz through a BP keyed to the
                        // same spin — the barrel pitch IS the filter track
                        lc.FilterF += (lc.TFilterF - lc.FilterF) * LoopFreqCoef;
                        lc.Ph0 += lc.Freq * InvSR; if (lc.Ph0 >= 1f) lc.Ph0 -= 1f;
                        float whine = Svf((byte)FilterMode.BP, lc.FilterF, 0.7f,
                            ref lc.FLow, ref lc.FBand, Sawtooth(lc.Ph0));
                        // Feed rattle: noise chopped by a 24 Hz gate × FireMix
                        lc.Ph1 += 24f * InvSR; if (lc.Ph1 >= 1f) lc.Ph1 -= 1f;
                        float gate = lc.Ph1 < 0.42f ? 1f : 0.12f;
                        oL = oR = whine * 0.55f * lc.Level
                            + NextNoise() * gate * 0.34f * lc.Aux;
                        break;
                    }
                    case (byte)LoopKind.HrHydraulic:
                    {
                        // Hydraulic hiss: BP noise ∝ door/lift stroke rate
                        lc.FilterF += (lc.TFilterF - lc.FilterF) * LoopFreqCoef;
                        float hiss = Svf((byte)FilterMode.BP, lc.FilterF, 0.6f,
                            ref lc.FLow, ref lc.FBand, NextNoise()) * 0.5f * lc.Level;
                        // 140 Hz triangle+square servo while the lift moves
                        lc.Ph0 += lc.Freq * InvSR; if (lc.Ph0 >= 1f) lc.Ph0 -= 1f;
                        float servo = (Triangle(lc.Ph0) * 0.7f + (lc.Ph0 < 0.5f ? 0.3f : -0.3f))
                            * 0.16f * lc.Aux;
                        oL = oR = hiss + servo;
                        break;
                    }
                    case (byte)LoopKind.MsShield:
                    {
                        // Deflector hum: sine + 1.5× partial under a slow tremolo;
                        // the Freq one-pole carries the octave power-down glide
                        lc.Ph0 += lc.Freq * InvSR; if (lc.Ph0 >= 1f) lc.Ph0 -= 1f;
                        lc.Ph1 += lc.Freq * 1.5f * InvSR; if (lc.Ph1 >= 1f) lc.Ph1 -= 1f;
                        lc.Ph2 += 4.3f * InvSR; if (lc.Ph2 >= 1f) lc.Ph2 -= 1f;
                        float trem = 0.84f + Triangle(lc.Ph2) * 0.16f;
                        oL = oR = (MathF.Sin(lc.Ph0 * MathF.Tau) * 0.75f
                            + Triangle(lc.Ph1) * 0.25f) * trem * 0.17f * lc.Level;
                        break;
                    }
                    case (byte)LoopKind.MsEngine:
                    {
                        // Hull drone: centered 47 Hz saw root (mono-sub rule) +
                        // ±0.4 Hz detuned octave partials for stereo width
                        lc.Ph0 += lc.Freq * InvSR; if (lc.Ph0 >= 1f) lc.Ph0 -= 1f;
                        float f2 = lc.Freq * 2f;
                        lc.Ph1 += (f2 - 0.4f) * InvSR; if (lc.Ph1 >= 1f) lc.Ph1 -= 1f;
                        lc.Ph2 += (f2 + 0.4f) * InvSR; if (lc.Ph2 >= 1f) lc.Ph2 -= 1f;
                        float root = Sawtooth(lc.Ph0) * 0.10f * lc.Level;
                        oL = root + Triangle(lc.Ph1) * 0.05f * lc.Level;
                        oR = root + Triangle(lc.Ph2) * 0.05f * lc.Level;
                        break;
                    }
                    default:
                        continue;
                }
                _busL[lc.Bus] += oL * lc.GainL;
                _busR[lc.Bus] += oR * lc.GainR;
            }

            // Voices → their bus + mono reverb send
            for (int vi = 0; vi < MaxVoices; vi++)
            {
                ref Voice v = ref _voices[vi];
                if (!v.Active) continue;

                if (v.AttackLeft > 0)
                {
                    v.AttackLeft--;
                    v.Env = 1f - (1f - v.Env) * v.AttackMul;
                }
                else
                {
                    v.Env *= v.DecayMul;
                    if (v.Env < 0.0008f) { v.Active = false; continue; }
                }

                v.Freq += v.FreqStep;
                v.Phase += v.Freq * _atPitch * InvSR;
                if (v.Phase >= 1f) v.Phase -= 1f;

                float os = Osc(v.Type, v.Phase);
                if (v.FilterMode != (byte)FilterMode.None)
                {
                    v.FilterF *= v.FilterFMul;
                    os = Svf(v.FilterMode, v.FilterF, v.FilterQ, ref v.FLow, ref v.FBand, os);
                }

                v.GainL += (v.TGainL - v.GainL) * PanCoef;
                v.GainR += (v.TGainR - v.GainR) * PanCoef;
                float vs = os * v.Env * v.Volume;
                _busL[v.Bus] += vs * v.GainL;
                _busR[v.Bus] += vs * v.GainR;
                revIn += vs * v.Reverb;

                if (--v.SamplesLeft <= 0) v.Active = false;
            }

            // Steal-fade tails (no reverb send — 3 ms splices)
            for (int ti = 0; ti < MaxTails; ti++)
            {
                ref Tail t = ref _tails[ti];
                if (!t.Active) continue;
                t.Amp *= TailFadeMul;
                if (t.Amp < 0.0008f) { t.Active = false; continue; }
                t.Phase += t.PhaseInc;
                if (t.Phase >= 1f) t.Phase -= 1f;
                float os = Osc(t.Type, t.Phase);
                if (t.FilterMode != (byte)FilterMode.None)
                    os = Svf(t.FilterMode, t.FilterF, t.FilterQ, ref t.FLow, ref t.FBand, os);
                _busL[t.Bus] += os * t.Amp * t.GainL;
                _busR[t.Bus] += os * t.Amp * t.GainR;
            }

            // 3.4 dedicated mono sub: 90→45 Hz glide, fixed tanh(3x) drive so the
            // 3rd/5th harmonics (135/225 Hz — what laptop drivers actually
            // reproduce) ride the whole tail; env applied post-drive keeps the
            // heavy/standard magnitude split audible. +2 ms broadband click edge.
            if (_subEnv > 0.0006f || _subClick > 0.0008f)
            {
                _subFreq += (45f - _subFreq) * SubGlideCoef;
                _subPhase += _subFreq * InvSR; if (_subPhase >= 1f) _subPhase -= 1f;
                float sub = MathF.Tanh(MathF.Sin(_subPhase * MathF.Tau) * 3f) * _subEnv * SubGain;
                _subEnv *= SubDecayMul;
                if (_subClick > 0.0008f)
                {
                    sub += NextNoise() * _subClick * SubClickGain;
                    _subClick *= SubClickMul;
                }
                _busL[BusSfx] += sub; _busR[BusSfx] += sub;
            }

            // 3.3 Freeverb-lite: mono in → 4 parallel combs + 2 series allpasses
            // per channel; right bank is +23 samples for width
            float rin = revIn * RevInGain;
            float wetL = 0f, wetR = 0f;
            for (int k = 0; k < 4; k++) wetL += CombStep(ref _combs[k], rin);
            for (int k = 4; k < 8; k++) wetR += CombStep(ref _combs[k], rin);
            wetL = AllpassStep(ref _allpasses[1], AllpassStep(ref _allpasses[0], wetL)) * WetGain;
            wetR = AllpassStep(ref _allpasses[3], AllpassStep(ref _allpasses[2], wetR)) * WetGain;

            // Bus mix — duck rides Music+Ambient only; SFX sends keep their reverb undocked
            float duckG = 1f - _duckEnv;
            float l = (_busL[BusMusic] + _busL[BusAmbient]) * duckG + _busL[BusSfx] + _busL[BusUi] + wetL;
            float r = (_busR[BusMusic] + _busR[BusAmbient]) * duckG + _busR[BusSfx] + _busR[BusUi] + wetR;

            // 3.3 glue compressor: peak follower, soft 3:1 above threshold.
            // Pre-master so dynamics are volume-knob independent (one fdiv when over).
            float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            _compEnv += (peak - _compEnv) * (peak > _compEnv ? CompAtkCoef : CompRelCoef);
            float cg = CompMakeup;
            if (_compEnv > CompThresh)
                cg = CompMakeup * (CompThresh + (_compEnv - CompThresh) * CompInvRatio) / _compEnv;
            l *= cg * _atMaster;
            r *= cg * _atMaster;

            // tanh soft clip replaces the hard clamp — stacked transients (city
            // death + thunder + Phalanx) bloom instead of flat-topping
            l = MathF.Tanh(l * ClipDrive) * ClipScale;
            r = MathF.Tanh(r * ClipDrive) * ClipScale;
            dst[i * 2] = (short)(l * 32000);
            dst[i * 2 + 1] = (short)(r * 32000);
        }

        // Watchdog timebase: wrapping int subtraction stays correct at rollover
        _loopClock += frames;
    }

    /// <summary>§6.5 seqlock snapshot (render thread). Acquire-reads the version,
    /// copies the published buffer, then re-reads: an even, unchanged version means
    /// the copy was tear-free. Bounded retries (never spin in real-time); on a lost
    /// race the previous consistent snapshot is kept — one stale block is inaudible.</summary>
    static void SnapshotMusic()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int v1 = Volatile.Read(ref _mpVersion); // acquire
            if ((v1 & 1) != 0) continue;            // writer mid-flight — retry
            MusicParams snap = (((v1 >> 1) & 1) == 0) ? _mpA : _mpB;
            int v2 = Volatile.Read(ref _mpVersion); // re-acquire
            if (v1 == v2) { _mpLive = snap; return; } // consistent → commit
        }
        // Lost every race: keep _mpLive (last good). Steady-state this never trips.
    }

    /// <summary>§6.5 audio-thread stem spawn. Builds a Voice in place and hands it
    /// to the pooled SpawnVoice — NO ring, NO allocation, NO producer RNG. Spawned
    /// inside the per-sample loop, so the voice's first rendered sample IS the step
    /// boundary: stepping is sample-exact with no frame quantization. Humanization
    /// (velocity/detune) uses the audio-thread noise (NextNoise), never _prodRand.</summary>
    static void SpawnStem(WaveType type, float freq, float freqEnd, float volume,
        float duration, float attack, float decay, byte priority, float pan,
        FilterMode filter, float q)
    {
        float pan2 = freq < SubMonoHz ? 0.5f : 0.5f + (MathH.Clamp(pan, 0f, 1f) - 0.5f) * PanCeiling;
        float a = pan2 * (MathF.PI * 0.5f);

        int dur = (int)(duration * SampleRate);
        if (dur < 16) dur = 16;
        int atk = (int)(attack * dur);
        if (atk < 0) atk = 0;
        if (atk > dur - 1) atk = dur - 1;
        float decaySamples = MathF.Max(1f, (dur - atk) / (1f + MathF.Max(0f, decay)));

        var v = new Voice
        {
            Active = true,
            Type = (byte)type,
            Priority = priority,
            Volume = volume,
            Freq = freq,
            FreqStep = (freqEnd - freq) / dur,
            Env = atk == 0 ? 1f : 0f,
            AttackLeft = atk,
            AttackMul = atk > 0 ? MathF.Exp(-4.6f / atk) : 0f,
            DecayMul = MathF.Exp(-6.907755f / decaySamples),
            SamplesLeft = dur,
            Bus = BusMusic, // §6.5: all stems duck under impacts via the existing sidechain
            Reverb = 0f,
        };
        v.TGainL = MathF.Cos(a) * Sqrt2;
        v.TGainR = MathF.Sin(a) * Sqrt2;
        v.GainL = v.TGainL;
        v.GainR = v.TGainR;
        if (type == WaveType.Noise)
        {
            var mode = filter == FilterMode.None ? FilterMode.LP : filter;
            float fs = MathH.Clamp(MathF.Tau * freq * InvSR, 1e-4f, 0.95f);
            float fe = MathH.Clamp(MathF.Tau * freqEnd * InvSR, 1e-4f, 0.95f);
            v.FilterMode = (byte)mode;
            v.FilterF = fs;
            v.FilterFMul = MathF.Exp(MathF.Log(fe / fs) / dur);
            v.FilterQ = q;
        }
        SpawnVoice(in v);
    }

    /// <summary>§6.5 semitone → Hz (equal temperament, A440-relative octaves).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float SemiToHz(int semi) => 440f * MathF.Pow(2f, semi / 12f);

    /// <summary>§6.5 RENDER-THREAD duck trigger. The kick fires the sidechain from
    /// inside the callback, so it must NOT use Enqueue() (that is the single-producer
    /// game-thread ring). It writes the same _duckTarget/_duckAtkLeft state the
    /// CmdKind.Duck handler does — summed-not-stacked via max — directly.</summary>
    static void DuckAudioThread(float depth)
    {
        _duckTarget = MathF.Min(DuckCap, MathF.Max(MathF.Max(_duckTarget, _duckEnv),
            MathH.Clamp(depth, 0f, DuckCap)));
        _duckAtkLeft = DuckAtkSamples;
    }

    /// <summary>§6.5 fire one sequencer step's stems (audio thread). Reads _mpLive
    /// (already snapshotted this block). Stems gate on intensity: kick+hat skeleton
    /// always; sub-bass ≥0.3; arp ≥0.5; lead ≥0.8; a boss tritone/brass ostinato
    /// supersedes the melodic stems while a boss is present; Quiet phases play only
    /// a held pad on downbeats. Velocity/timing humanized via NextNoise.</summary>
    static void FireStep(int step)
    {
        ref MusicParams m = ref _mpLive;
        float inten = m.Intensity;
        int sel = m.PatSel & 1;
        int root = m.Root;
        // Velocity humanization: ±12% per hit so repeated steps never sound stamped.
        float vel = 0.88f + (NextNoise() * 0.5f + 0.5f) * 0.24f;

        if (m.Quiet)
        {
            // Title/Shop/Paused/GameOver: a sparse held pad on the downbeat only.
            if (step == 0)
            {
                int pr = -24 + root; // deep, calm
                SpawnStem(WaveType.Triangle, SemiToHz(pr), SemiToHz(pr),
                    0.05f, 2.6f, 0.5f, 0.4f, PrioBeat, 0.45f, FilterMode.None, 1f);
                SpawnStem(WaveType.Sine, SemiToHz(pr + 7), SemiToHz(pr + 7),
                    0.035f, 2.6f, 0.55f, 0.4f, PrioBeat, 0.55f, FilterMode.None, 1f);
            }
            return;
        }

        if (m.Boss)
        {
            // Boss ostinato: low-brass pulse + a tritone above on the BossPat steps.
            if ((BossPat[sel] & (1 << step)) != 0)
            {
                int br = -22 + root;
                SpawnStem(WaveType.Sawtooth, SemiToHz(br), SemiToHz(br - 2),
                    (0.11f + inten * 0.05f) * vel, 0.34f, 0.01f, 1.1f, PrioBeat, 0.5f,
                    FilterMode.None, 1f);
                SpawnStem(WaveType.Square, SemiToHz(br + 6), SemiToHz(br + 6), // +6 = tritone
                    (0.05f + inten * 0.03f) * vel, 0.26f, 0.02f, 1.3f, PrioBeat, 0.5f,
                    FilterMode.None, 1f);
            }
            // The skeleton still drives the boss groove underneath.
        }

        // ── Always-on skeleton: kick + hat ──
        if ((KickPat[sel] & (1 << step)) != 0)
        {
            SpawnStem(WaveType.Sine, 92f, 44f, (0.10f + inten * 0.10f) * vel,
                0.20f, 0.0f, 1.2f, PrioBeat, 0.5f, FilterMode.None, 1f);
            // Light sidechain accent so Ambient/Music pump with the kick (§5 3.4).
            // Render-thread path — must not touch the game-thread command ring.
            DuckAudioThread(0.08f + inten * 0.06f);
        }
        if ((HatPat[sel] & (1 << step)) != 0)
        {
            // BP-noise hat; off-beats slightly quieter for groove.
            float hv = ((step & 1) == 0 ? 0.06f : 0.04f) * vel;
            SpawnStem(WaveType.Noise, 7200f, 6400f, hv, 0.045f, 0.0f, 2.2f,
                PrioBeat, 0.52f, FilterMode.BP, 0.6f);
        }

        if (m.Boss) return; // boss ostinato replaces the melodic stems

        // ── Sub-bass roots (minor pentatonic), ≥0.3 intensity ──
        if (inten >= 0.30f && (BassPat[sel] & (1 << step)) != 0)
        {
            int deg = PentaSemis[(step >> 1) % PentaSemis.Length];
            int note = -24 + root + deg; // deep root (mono-sub register)
            SpawnStem(WaveType.Triangle, SemiToHz(note), SemiToHz(note),
                (0.12f + inten * 0.05f) * vel, 0.26f, 0.005f, 1.0f, PrioBeat, 0.5f,
                FilterMode.None, 1f);
        }

        // ── Arp, ≥0.5 intensity ──
        if (inten >= 0.50f && (ArpPat[sel] & (1 << step)) != 0)
        {
            int[] walk = ArpWalk[sel];
            int deg = PentaSemis[walk[step % walk.Length] % PentaSemis.Length];
            int note = root + deg; // mid register
            // Pan the arp gently by step for a touch of stereo movement.
            float pan = 0.42f + ((step & 3) * 0.04f);
            SpawnStem(WaveType.Square, SemiToHz(note), SemiToHz(note),
                (0.045f + inten * 0.03f) * vel, 0.13f, 0.004f, 1.6f, PrioBeat, pan,
                FilterMode.None, 1f);
        }

        // ── Lead stabs, ≥0.8 intensity ──
        if (inten >= 0.80f && (LeadPat[sel] & (1 << step)) != 0)
        {
            int deg = PentaSemis[(step >> 1) % PentaSemis.Length];
            int note = 12 + root + deg; // an octave up — a bright stab
            SpawnStem(WaveType.Sawtooth, SemiToHz(note), SemiToHz(note - 2),
                0.06f * vel, 0.22f, 0.006f, 1.0f, PrioBeat, 0.5f, FilterMode.None, 1f);
        }
    }

    static void ProcessCommands()
    {
        int head = _cmdHead; // single consumer: the render path
        int tail = Volatile.Read(ref _cmdTail); // acquire — payload visible below
        while (head != tail)
        {
            ref AudioCmd c = ref _cmdRing[head];
            switch (c.Kind)
            {
                case CmdKind.Spawn:
                    SpawnVoice(in c.Voice);
                    break;
                case CmdKind.Params:
                    _atDangerT = c.P0;
                    _atPitchT = c.P1;
                    _atMasterT = c.P2;
                    break;
                case CmdKind.Loop:
                {
                    if ((uint)c.I0 >= MaxLoopCh) break; // bad producer index can never escape onto the render thread
                    ref LoopChannel lc = ref _loops[c.I0];
                    if (lc.Kind != c.B0)
                    {
                        // (Re)bind the channel: reset synth state, snap params so
                        // nothing glides from a previous occupant, pick the
                        // per-kind bus + level-fade speed
                        lc.Kind = c.B0;
                        lc.Bus = c.B0 == (byte)LoopKind.MsEngine ? BusAmbient : BusSfx;
                        lc.LevelCoef = c.B0 switch
                        {
                            (byte)LoopKind.MsShield => LoopSlowCoef, // outlives the freq glide
                            (byte)LoopKind.MsEngine => LoopMedCoef,
                            _ => LoopFastCoef,
                        };
                        lc.Level = 0f; lc.Aux = 0f;
                        lc.Freq = c.P1; lc.FilterF = c.P2;
                        lc.GainL = c.P4; lc.GainR = c.P5;
                        lc.Ph0 = 0f; lc.Ph1 = 0f; lc.Ph2 = 0.25f;
                        lc.FLow = 0f; lc.FBand = 0f;
                    }
                    lc.TLevel = c.P0; lc.TFreq = c.P1; lc.TFilterF = c.P2;
                    lc.TAux = c.P3; lc.TGainL = c.P4; lc.TGainR = c.P5;
                    lc.Stamp = _loopClock;
                    break;
                }
                case CmdKind.Duck:
                    // Summed-not-stacked (§5 3.4): running duck + new trigger = max
                    _duckTarget = MathF.Min(DuckCap,
                        MathF.Max(MathF.Max(_duckTarget, _duckEnv), c.P0));
                    _duckAtkLeft = DuckAtkSamples;
                    break;
                case CmdKind.Sub:
                    // Retrigger in place: env/click jump to max, freq snaps to 90 Hz.
                    // Phase is NOT reset (continuity — the click supplies the edge).
                    _subEnv = MathF.Max(_subEnv, c.P0);
                    _subClick = MathF.Max(_subClick, c.P0);
                    _subFreq = 90f;
                    break;
            }
            head = (head + 1) & (CmdRingSize - 1);
        }
        Volatile.Write(ref _cmdHead, head);
    }

    static void SpawnVoice(in Voice proto)
    {
        int slot = -1;
        for (int i = 0; i < MaxVoices; i++)
        {
            if (!_voices[i].Active) { slot = i; break; }
        }

        if (slot < 0)
        {
            // Steal lowest-priority-then-quietest; splice its output into a 3 ms tail
            int victim = 0;
            float bestKey = float.MaxValue;
            for (int i = 0; i < MaxVoices; i++)
            {
                ref Voice v = ref _voices[i];
                float key = v.Priority * 1000f + v.Env * v.Volume;
                if (key < bestKey) { bestKey = key; victim = i; }
            }
            if (_voices[victim].Priority > proto.Priority) return; // newcomer outranked
            StartTail(in _voices[victim]);
            slot = victim;
        }

        _voices[slot] = proto;
    }

    static void StartTail(in Voice v)
    {
        int slot = -1, quiet = 0;
        float qAmp = float.MaxValue;
        for (int i = 0; i < MaxTails; i++)
        {
            if (!_tails[i].Active) { slot = i; break; }
            if (_tails[i].Amp < qAmp) { qAmp = _tails[i].Amp; quiet = i; }
        }
        if (slot < 0) slot = quiet;

        ref Tail t = ref _tails[slot];
        t.Active = true;
        t.Type = v.Type;
        t.Bus = v.Bus;
        t.Phase = v.Phase;
        t.PhaseInc = v.Freq * _atPitch * InvSR; // freeze the sweep for the 3 ms splice
        t.Amp = MathH.Clamp(v.Env, 0f, 1f) * v.Volume;
        t.GainL = v.GainL;
        t.GainR = v.GainR;
        t.FilterMode = v.FilterMode;
        t.FilterF = v.FilterF;
        t.FilterQ = v.FilterQ;
        t.FLow = v.FLow;
        t.FBand = v.FBand;
    }

    // ── DSP primitives ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Osc(byte type, float phase) => (WaveType)type switch
    {
        WaveType.Sine => MathF.Sin(phase * MathF.Tau),
        WaveType.Square => phase < 0.5f ? 1f : -1f,
        WaveType.Sawtooth => Sawtooth(phase),
        WaveType.Triangle => Triangle(phase),
        _ => NextNoise(),
    };

    /// <summary>Chamberlin SVF step. Stable for f &lt; 1 (§4.8); q is damping (1/Q).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Svf(byte mode, float f, float q, ref float low, ref float band, float input)
    {
        low += f * band;
        float high = input - low - q * band;
        band += f * high;
        return mode == (byte)FilterMode.LP ? low : mode == (byte)FilterMode.BP ? band : high;
    }

    /// <summary>Freeverb comb: delay line with one-pole-damped feedback.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float CombStep(ref Comb c, float input)
    {
        float[] buf = c.Buf;
        float y = buf[c.Idx];
        c.Store = y * (1f - RevDamp) + c.Store * RevDamp;
        buf[c.Idx] = input + c.Store * CombFb;
        if (++c.Idx >= buf.Length) c.Idx = 0;
        return y;
    }

    /// <summary>Freeverb allpass diffuser (feedback 0.5).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float AllpassStep(ref Allpass a, float input)
    {
        float[] buf = a.Buf;
        float y = buf[a.Idx];
        buf[a.Idx] = input + y * 0.5f;
        if (++a.Idx >= buf.Length) a.Idx = 0;
        return y - input;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float NextNoise()
    {
        uint x = _noiseState;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _noiseState = x;
        return (int)x * (1f / 2147483648f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Sawtooth(float phase) => phase * 2f - 1f; // expects wrapped [0,1)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Triangle(float phase) => phase < 0.5f ? phase * 4f - 1f : 3f - phase * 4f;

    // ── Sound Events (public surface unchanged; pan params now actually wired) ──

    /// <summary>Player missile launch — sawtooth sweep down + noise burst.</summary>
    public static void Launch(float pan)
    {
        AddVoice(new VoiceDesc { Type = WaveType.Sawtooth, Freq = 850, FreqEnd = 120,
            Volume = 0.25f, Duration = 0.28f, Attack = 0.02f, Decay = 1.5f,
            Pan = pan, Priority = PrioMed });
        AddVoice(new VoiceDesc { Type = WaveType.Noise, Freq = 2400, FreqEnd = 2400,
            Volume = 0.3f, Duration = 0.35f, Attack = 0.02f, Decay = 1.5f,
            Pan = pan, Priority = PrioMed });
    }

    /// <summary>HellRaiser rapid fire — chirp + zing.</summary>
    public static void HellRaiserFire(float pan, float intensity = 0.8f)
    {
        float p = MathH.Clamp(intensity, 0.2f, 1.4f);
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 1800 + p * 620, FreqEnd = 820 + p * 180,
            Volume = 0.16f * p, Duration = 0.085f, Attack = 0.004f, Decay = 2f, Pan = pan });
        AddVoice(new VoiceDesc { Type = WaveType.Square, Freq = 2500 + p * 700, FreqEnd = 1400,
            Volume = 0.1f * p, Duration = 0.07f, Attack = 0.003f, Decay = 2f, Pan = pan });
        AddVoice(new VoiceDesc { Type = WaveType.Noise, Freq = 1900, FreqEnd = 1900,
            Volume = 0.12f * p, Duration = 0.09f, Attack = 0.004f, Decay = 2f, Pan = pan });
    }

    /// <summary>Enemy launch — square wave drop.</summary>
    public static void EnemyLaunch(float pan)
    {
        AddVoice(new VoiceDesc { Type = WaveType.Square, Freq = 140, FreqEnd = 90,
            Volume = 0.15f, Duration = 0.24f, Attack = 0.01f, Decay = 1.2f, Pan = pan });
    }

    /// <summary>Explosion hit — triangle drop + LP noise crack (3500→600 Hz), wet send.
    /// combo (§5 4.4): kill chains climb a semitone ladder, freq × 2^(min(combo,24)/12)
    /// — capped at +2 octaves so the 320 Hz root tops out at 1280 Hz, far below
    /// dog-whistle range. Three micro-variants (§5 4.5) vary transient/decay so a
    /// barrage of kills never sounds stamped (the cutoff brightens at half strength —
    /// sqrt of the ladder — so it stays a crack, not hiss).</summary>
    public static void Hit(float pan, float size = 0.6f, int combo = 0)
    {
        float ladder = MathF.Pow(2f, Math.Min(combo, 24) / 12f);
        float bright = MathF.Sqrt(ladder);
        int pick = (int)(Prod01() * 3f); // 0 baseline / 1 snappy / 2 boomy
        float atk = pick == 1 ? 0.004f : pick == 2 ? 0.02f : 0.01f;
        float dec = pick == 1 ? 1.5f : pick == 2 ? 0.8f : 1.0f;
        AddVoice(new VoiceDesc { Type = WaveType.Triangle,
            Freq = 320 * ladder, FreqEnd = (pick == 2 ? 70 : 80) * ladder,
            Volume = 0.28f + size * 0.1f, Duration = 0.4f, Attack = atk, Decay = dec,
            Pan = pan, Priority = PrioMed, Reverb = 0.25f });
        AddVoice(new VoiceDesc { Type = WaveType.Noise,
            Freq = (pick == 1 ? 3900 : pick == 2 ? 3000 : 3500) * bright,
            FreqEnd = (pick == 2 ? 500 : 600) * bright,
            Volume = 0.18f + size * 0.08f, Duration = 0.38f, Attack = atk, Decay = dec + 0.2f,
            Pan = pan, Priority = PrioMed, Reverb = 0.25f });
        if (pick == 2) // the boomy variant gets a tiny transient tick edge
            AddVoice(new VoiceDesc { Type = WaveType.Square, Freq = 1500 * bright, FreqEnd = 900,
                Volume = 0.05f, Duration = 0.03f, Attack = 0.1f, Decay = 2f, Pan = pan });
    }

    /// <summary>Ground impact — saturated 90→45 Hz thump on the dedicated mono sub
    /// voice (§5 3.4; replaces the inaudible 22 Hz sine) + LP noise rumble.
    /// Ducks Music/Ambient; magnitude mirrors the Combat.cs trauma adds
    /// (heavy 0.4 / standard 0.25, normalized to heavy = 1) for AV sync.</summary>
    public static void Impact(float pan, bool heavy = false)
    {
        float mag = heavy ? 1f : 0.625f;
        TriggerSub(mag);
        Duck(heavy ? 0.5f : 0.35f);
        // §5 4.5 micro-variants: the sub thump stays fixed (AV-sync contract);
        // only the noise rumble varies — tighter crack / baseline / longer roll
        int pick = (int)(Prod01() * 3f);
        float dur = pick == 1 ? 0.8f : pick == 2 ? 1.1f : 0.95f;
        float dec = pick == 1 ? 1.3f : pick == 2 ? 0.85f : 1f;
        float fe = (heavy ? 80f : 160f) * (pick == 2 ? 0.8f : 1f);
        AddVoice(new VoiceDesc { Type = WaveType.Noise, Freq = heavy ? 600 : 1800, FreqEnd = fe,
            Volume = heavy ? 0.4f : 0.32f, Duration = dur, Attack = 0.01f, Decay = dec,
            Pan = pan, Priority = PrioHigh, Reverb = 0.25f });
    }

    /// <summary>City destroyed — cascading tones; full-depth duck + wet send.</summary>
    public static void CityDestroyed(float pan)
    {
        Duck(DuckCap);
        for (int i = 0; i < 4; i++)
        {
            float f = 260 - i * 22;
            AddVoice(new VoiceDesc { Type = i % 2 == 0 ? WaveType.Square : WaveType.Sawtooth,
                Freq = f, FreqEnd = MathF.Max(40, f * 0.24f),
                Volume = 0.14f, Duration = 0.3f, Attack = 0.01f, Decay = 1.2f,
                Pan = pan, Priority = PrioHigh, Reverb = 0.3f });
        }
    }

    /// <summary>EMP — rising sweep + noise wash.</summary>
    public static void EMP()
    {
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 220, FreqEnd = 1320,
            Volume = 0.32f, Duration = 0.45f, Attack = 0.03f, Decay = 0.5f, Priority = PrioHigh,
            Reverb = 0.15f });
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 1320, FreqEnd = 150,
            Volume = 0.22f, Duration = 0.5f, Attack = 0.01f, Decay = 1f, Priority = PrioHigh,
            Reverb = 0.15f });
        AddVoice(new VoiceDesc { Type = WaveType.Noise, Freq = 1600, FreqEnd = 1600,
            Volume = 0.22f, Duration = 0.8f, Attack = 0.01f, Decay = 1f, Priority = PrioHigh,
            Reverb = 0.25f });
    }

    /// <summary>Wave cleared fanfare — ascending tones.</summary>
    public static void WaveCleared()
    {
        for (int i = 0; i < 3; i++)
            AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 320 + i * 100, FreqEnd = 220 + i * 50,
                Volume = 0.17f, Duration = 0.25f, Attack = 0.01f, Decay = 1.2f, Priority = PrioMed });
    }

    /// <summary>Game over — descending sawtooth.</summary>
    public static void GameOver()
    {
        if (_titleSilent) return; // don't sting on attract-backdrop wipes
        AddVoice(new VoiceDesc { Type = WaveType.Sawtooth, Freq = 170, FreqEnd = 42,
            Volume = 0.22f, Duration = 1.2f, Attack = 0.01f, Decay = 0.7f, Priority = PrioHigh });
    }

    /// <summary>Incoming warning — triangle chirp.</summary>
    public static void Incoming(float pan, float intensity = 0.55f)
    {
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 520 + intensity * 200, FreqEnd = 260,
            Volume = 0.1f + intensity * 0.09f, Duration = 0.22f, Attack = 0.02f, Decay = 1.5f, Pan = pan });
    }

    /// <summary>Near miss — BP noise whoosh (1700→480 Hz) + triangle drop.</summary>
    public static void NearMiss(float pan, float intensity = 0.75f)
    {
        AddVoice(new VoiceDesc { Type = WaveType.Noise, Freq = 1700 + intensity * 600, FreqEnd = 480,
            Volume = 0.16f + intensity * 0.12f, Duration = 0.28f, Attack = 0.03f, Decay = 1.5f,
            Pan = pan, Filter = FilterMode.BP, Q = 0.75f });
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 760 + intensity * 260, FreqEnd = 120,
            Volume = 0.06f + intensity * 0.06f, Duration = 0.24f, Attack = 0.01f, Decay = 1.5f, Pan = pan });
    }

    /// <summary>Thunder rumble — dark LP noise (620→120 Hz) + centered sub sine;
    /// ducks Music/Ambient and sends the rumble wet.</summary>
    public static void Thunder(float pan = 0.5f, float intensity = 0.7f)
    {
        Duck(0.2f + intensity * 0.25f);
        AddVoice(new VoiceDesc { Type = WaveType.Noise, Freq = 620, FreqEnd = 120,
            Volume = 0.24f + intensity * 0.22f, Duration = 1.8f, Attack = 0.09f, Decay = 0.6f,
            Pan = pan, Q = 1.2f, Priority = PrioMed, Reverb = 0.3f });
        AddVoice(new VoiceDesc { Type = WaveType.Sine, Freq = 58 + intensity * 18, FreqEnd = 31,
            Volume = 0.16f + intensity * 0.14f, Duration = 1.9f, Attack = 0.01f, Decay = 0.6f,
            Priority = PrioMed });
    }

    // ── §5 4.2 behavioral-roster telegraphs ──

    /// <summary>Carrier deploy telegraph — bay-servo ping: low servo drop + a
    /// second chirp whose long attack stages it ~0.1 s later.</summary>
    public static void CarrierBay(float pan)
    {
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 620, FreqEnd = 660,
            Volume = 0.1f, Duration = 0.1f, Attack = 0.02f, Decay = 1.4f,
            Pan = pan, Priority = PrioMed, Reverb = 0.2f });
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 930, FreqEnd = 990,
            Volume = 0.12f, Duration = 0.26f, Attack = 0.4f, Decay = 1.2f,
            Pan = pan, Priority = PrioMed, Reverb = 0.25f });
        AddVoice(new VoiceDesc { Type = WaveType.Square, Freq = 165, FreqEnd = 105,
            Volume = 0.05f, Duration = 0.32f, Attack = 0.06f, Decay = 1f, Pan = pan });
    }

    /// <summary>MIRV split telegraph — crossing up/down sweeps read as an alarm
    /// warble over a low square undertone.</summary>
    public static void MirvWarble(float pan)
    {
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 480, FreqEnd = 640,
            Volume = 0.09f, Duration = 0.5f, Attack = 0.08f, Decay = 0.9f,
            Pan = pan, Priority = PrioMed, Reverb = 0.2f });
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 640, FreqEnd = 480,
            Volume = 0.09f, Duration = 0.5f, Attack = 0.08f, Decay = 0.9f,
            Pan = pan, Priority = PrioMed, Reverb = 0.2f });
        AddVoice(new VoiceDesc { Type = WaveType.Square, Freq = 210, FreqEnd = 160,
            Volume = 0.04f, Duration = 0.4f, Attack = 0.05f, Decay = 1f, Pan = pan });
    }

    /// <summary>Stealth decloak ping — short wet sonar blip.</summary>
    public static void SonarBlip(float pan)
    {
        AddVoice(new VoiceDesc { Type = WaveType.Sine, Freq = 1180, FreqEnd = 1120,
            Volume = 0.07f, Duration = 0.16f, Attack = 0.05f, Decay = 1.8f,
            Pan = pan, Reverb = 0.45f });
    }

    /// <summary>Shield drone spawn — low hum swell (the sub root stays centered
    /// via the &lt;120 Hz mono rule; the octave partner carries the pan).</summary>
    public static void ShieldHum(float pan)
    {
        AddVoice(new VoiceDesc { Type = WaveType.Sine, Freq = 98, FreqEnd = 84,
            Volume = 0.14f, Duration = 0.9f, Attack = 0.25f, Decay = 0.6f, Reverb = 0.2f });
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 196, FreqEnd = 176,
            Volume = 0.07f, Duration = 0.8f, Attack = 0.22f, Decay = 0.7f,
            Pan = pan, Reverb = 0.3f });
    }

    // ── §5 4.5 interaction-sound vocabulary (Ui bus, dry — never ducked) ──

    /// <summary>Crosshair fire click — the tactile answer to a manual launch.</summary>
    public static void UiClick()
    {
        AddVoice(new VoiceDesc { Type = WaveType.Square, Freq = 2200, FreqEnd = 1500,
            Volume = 0.055f, Duration = 0.035f, Attack = 0.08f, Decay = 2.2f, Bus = BusUi });
    }

    /// <summary>Shop open/close — BP noise whoosh sweeping up (open) / down (close).</summary>
    public static void ShopWhoosh(bool open)
    {
        AddVoice(new VoiceDesc { Type = WaveType.Noise,
            Freq = open ? 320 : 1900, FreqEnd = open ? 1900 : 320,
            Volume = 0.17f, Duration = 0.45f, Attack = 0.2f, Decay = 1.1f,
            Filter = FilterMode.BP, Q = 0.8f, Priority = PrioMed, Bus = BusUi });
    }

    /// <summary>Purchase confirm — ascending major-triad arp. The steps are staged
    /// by long exponential attacks (the CarrierBay trick) — no scheduler needed.</summary>
    public static void UiConfirm()
    {
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 523, FreqEnd = 523,
            Volume = 0.11f, Duration = 0.14f, Attack = 0.03f, Decay = 1.6f, Bus = BusUi });
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 659, FreqEnd = 659,
            Volume = 0.11f, Duration = 0.22f, Attack = 0.4f, Decay = 1.6f, Bus = BusUi });
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 784, FreqEnd = 784,
            Volume = 0.12f, Duration = 0.3f, Attack = 0.55f, Decay = 1.5f, Bus = BusUi });
    }

    /// <summary>Denied buzz — beating minor-second square pair.</summary>
    public static void UiDeny()
    {
        AddVoice(new VoiceDesc { Type = WaveType.Square, Freq = 220, FreqEnd = 196,
            Volume = 0.09f, Duration = 0.2f, Attack = 0.04f, Decay = 1.1f, Bus = BusUi });
        AddVoice(new VoiceDesc { Type = WaveType.Square, Freq = 233, FreqEnd = 208,
            Volume = 0.07f, Duration = 0.2f, Attack = 0.04f, Decay = 1.1f, Bus = BusUi });
    }

    /// <summary>EMP charge granted — bright two-stage ping.</summary>
    public static void EmpReady()
    {
        AddVoice(new VoiceDesc { Type = WaveType.Sine, Freq = 1568, FreqEnd = 1568,
            Volume = 0.1f, Duration = 0.28f, Attack = 0.04f, Decay = 1.8f, Bus = BusUi });
        AddVoice(new VoiceDesc { Type = WaveType.Sine, Freq = 2093, FreqEnd = 2093,
            Volume = 0.06f, Duration = 0.34f, Attack = 0.4f, Decay = 1.6f, Bus = BusUi });
    }

    /// <summary>Low-ammo geiger tick — rate-capped by GameState.LowAmmoTickCd
    /// (GameUpdate owns the trigger; this is just the 30 ms BP crackle).</summary>
    public static void GeigerTick(float pan)
    {
        AddVoice(new VoiceDesc { Type = WaveType.Noise, Freq = 2500, FreqEnd = 2100,
            Volume = 0.07f, Duration = 0.03f, Attack = 0.1f, Decay = 2.4f,
            Filter = FilterMode.BP, Q = 0.7f, Pan = pan, Bus = BusUi });
    }

    /// <summary>Wave-banner stab — saw fifth over a square sub edge (StartWave).</summary>
    public static void WaveStab()
    {
        if (_titleSilent) return; // attract backdrop loops waves; keep the title quiet
        AddVoice(new VoiceDesc { Type = WaveType.Sawtooth, Freq = 196, FreqEnd = 185,
            Volume = 0.13f, Duration = 0.3f, Attack = 0.03f, Decay = 1.5f,
            Priority = PrioMed, Bus = BusUi });
        AddVoice(new VoiceDesc { Type = WaveType.Sawtooth, Freq = 294, FreqEnd = 277,
            Volume = 0.1f, Duration = 0.3f, Attack = 0.03f, Decay = 1.5f,
            Priority = PrioMed, Bus = BusUi });
        AddVoice(new VoiceDesc { Type = WaveType.Square, Freq = 98, FreqEnd = 92,
            Volume = 0.07f, Duration = 0.34f, Attack = 0.05f, Decay = 1.2f, Bus = BusUi });
    }

    /// <summary>Combo-break — falling-pitch womp (ComboTimer expiry, chains ≥ 4).</summary>
    public static void ComboBreak()
    {
        AddVoice(new VoiceDesc { Type = WaveType.Triangle, Freq = 660, FreqEnd = 220,
            Volume = 0.12f, Duration = 0.38f, Attack = 0.02f, Decay = 1.1f, Bus = BusUi });
        AddVoice(new VoiceDesc { Type = WaveType.Square, Freq = 330, FreqEnd = 124,
            Volume = 0.06f, Duration = 0.34f, Attack = 0.03f, Decay = 1.2f, Bus = BusUi });
    }
}
