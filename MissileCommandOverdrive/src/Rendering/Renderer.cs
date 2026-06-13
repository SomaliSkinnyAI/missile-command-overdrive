using System.Numerics;
using Raylib_cs;
using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive.Rendering;

/// <summary>Faithful port of the HTML canvas draw routines using Raylib primitives.</summary>
public static class Renderer
{
    const float TAU = MathF.PI * 2;
    const int GrainSize = 160;
    const int GradientSize = 128;
    const int MoonSize = 192;

    static RenderTexture2D _frameTarget;
    static Texture2D _grainTexture;
    static Texture2D _gradientTex; // smooth radial gradient: white center ? transparent edge
    static Texture2D _moonTex; // procedural moon with maria + craters + terminator shading
    static bool _fxReady;
    static bool _grainReady;
    static bool _gradientReady;
    static bool _moonReady;

    // Post uber-shader (§5 1.4): one-pass vignette/scanlines/grain/CA/barrel/flash/
    // desat over the §4.1 composite blit. If the GLSL fails to compile the legacy
    // CPU draws in DrawPostFx/DrawGrain stay active as the fallback.
    static Shader _postShader;
    static bool _postShaderTried;
    static bool _postShaderActive;
    static int _locTime, _locDanger, _locChromatic, _locFlashAmount;
    static int _locFlashDir, _locFlashColor, _locResolution, _locHdrActive;

    // Shockwave refraction / heat shimmer / EMP ripple (§5 2.2): the strongest
    // ≤16 live shockwaves go up each frame as vec4(center texcoord, radius in
    // screen-height units, strength); EMP waves are flagged in-band as
    // w = 1 + strength. The 4 hottest live non-EMP explosions feed the shimmer
    // mask through a second small array (see Shaders.cs for why not a w-flag).
    const int MaxShaderShockwaves = 16;
    const int MaxShaderHeatSources = 4;
    static int _locShockwaves, _locShockwaveCount;
    static int _locHeatSources, _locHeatCount;
    // No gameplay writer feeds flash direction/color yet — constants match the
    // legacy full-screen cyan flash with its ground-side emphasis (y-down dir).
    static readonly Vector2 FlashDirDefault = new(0f, 1f);
    static readonly Vector3 FlashColorDefault = new(160 / 255f, 218 / 255f, 255 / 255f);

    // GPU threshold bloom (§5 2.1): soft-knee bright pass → 4-mip dual-Kawase
    // chain → additive composite back into _frameTarget, so the uber-shader
    // grades on top of the bloomed scene. No CPU fallback — on compile failure
    // the scene renders unbloomed.
    const int BloomMipCount = 4; // W/2, W/4, W/8, W/16
    const float BloomThreshold = 0.6f;
    const float BloomKnee = 0.3f;
    const float BloomIntensity = 0.9f;

    // HDR scene target (§5 5.1 / §4.9): _frameTarget and the bloom mips become
    // hand-assembled FP16 FBOs (the binding has no Rlgl.SetFramebufferWidth/
    // Height, so stock LoadRenderTexture can't be re-formatted) wrapped in
    // stock RenderTexture2D structs — BeginTextureMode and every existing
    // blit work unchanged. MCOD_NO_HDR=1 or any incomplete FBO drops the whole
    // chain back to stock RGBA8, which with hdrActive=0 in the uber-shader
    // reproduces the Phase 2-4 baseline exactly.
    const float HdrExposure = 1.15f; // pre-ACES lift — see the shader comment
    static bool _hdrDisabled = Environment.GetEnvironmentVariable("MCOD_NO_HDR") == "1";
    static bool _hdrActive;
    static readonly RenderTexture2D[] _bloomMips = new RenderTexture2D[BloomMipCount];
    static Shader _bloomPrefilter;
    static Shader _bloomDown;
    static Shader _bloomUp;
    static bool _bloomShaderTried;
    static bool _bloomActive;
    static int _locBloomDownHalfPixel, _locBloomUpHalfPixel;

    // Dynamic 2D light buffer (§5 5.2): quarter-res RGBA8 target filled with
    // additive gradient blobs collected during the world pass (positions are
    // already computed in the draw functions — never recomputed), then bound
    // as a second sampler on the uber-shader and composited at the refracted
    // uv. Always stock RGBA8 even on the HDR path: light values live in 0..1.
    const int MaxLights = 48;
    static RenderTexture2D _lightTarget;
    static readonly (float X, float Y, float R, byte Cr, byte Cg, byte Cb, byte Ca)[] _lights =
        new (float, float, float, byte, byte, byte, byte)[MaxLights];
    static int _lightCount;
    static bool _lightsOn; // post shader live and not env-disabled, latched per frame
    static bool _lightDisabled = Environment.GetEnvironmentVariable("MCOD_NO_LIGHT") == "1";
    static int _locLightTex, _locDayFactor, _locLightActive;
    // §5 5.4 theme grade + CRT + last-city state modifier (uploaded per frame; the
    // grade triples are cheap vec3s, the rest scalars — all zero-alloc).
    static int _locGradeLift, _locGradeGamma, _locGradeGain, _locCrtAmount, _locLastCity;
    // GL blend enums for Rlgl.SetBlendFactorsSeparate (raylib doesn't re-export them)
    const int GlSrcAlpha = 0x0302;
    const int GlOne = 1;
    const int GlOneMinusSrcAlpha = 0x0303;
    const int GlFuncAdd = 0x8006;

    // Moon disc mask for star-occlusion (matches HTML: stars inside the moon circle are skipped)
    static float _moonMaskX, _moonMaskY, _moonMaskR;
    static bool _moonMaskActive;

    // Trauma camera (§5 1.3): baked 1D value-noise tables sampled on the
    // unscaled FeelDirector.Clock (s.Time freezes during hit-stop).
    const int NoiseLen = 1024;       // power of two — wrap via mask
    const int NoiseCtrlStep = 16;    // control point spacing inside the table
    const float NoiseRate = 280f;    // table entries traversed per second (~17 Hz wobble)
    const float MaxShakePx = 16f;
    const float MaxRollDeg = 1.5f;
    static readonly float[] _shakeNoiseX = BakeNoise(101);
    static readonly float[] _shakeNoiseY = BakeNoise(797);
    static readonly float[] _shakeNoiseRoll = BakeNoise(523);

    static float[] BakeNoise(int seed)
    {
        var rng = new Random(seed);
        int ctrlCount = NoiseLen / NoiseCtrlStep;
        var ctrl = new float[ctrlCount];
        for (int i = 0; i < ctrlCount; i++) ctrl[i] = (float)(rng.NextDouble() * 2 - 1);
        var table = new float[NoiseLen];
        for (int i = 0; i < NoiseLen; i++)
        {
            int c0 = i / NoiseCtrlStep;
            int c1 = (c0 + 1) % ctrlCount;
            float f = (i % NoiseCtrlStep) / (float)NoiseCtrlStep;
            float k = (1 - MathF.Cos(f * MathF.PI)) * 0.5f; // cosine-smoothed
            table[i] = ctrl[c0] + (ctrl[c1] - ctrl[c0]) * k;
        }
        return table;
    }

    static float SampleNoise(float[] table, float t)
    {
        float idx = t % NoiseLen;
        int i0 = (int)idx;
        float f = idx - i0;
        int i1 = (i0 + 1) & (NoiseLen - 1);
        return table[i0] + (table[i1] - table[i0]) * f;
    }

    // Fixed-seed scenery randoms (§5 2.6): the draw path used to run
    // `new Random(seed)` every frame (56-int Knuth init per ctor) just to replay
    // the same sequence. The unit draws are baked once and indexed instead,
    // preserving the exact pre-existing layouts.
    static float[] RandTable(int seed, int count)
    {
        var rng = new Random(seed);
        var t = new float[count];
        for (int i = 0; i < count; i++) t[i] = rng.NextSingle();
        return t;
    }
    static readonly float[] _bokehRand = RandTable(271, 12 * 6);  // 6 draws per bokeh circle
    static readonly float[] _starRand = RandTable(42, 950 * 3);   // x/y/size per star
    static readonly float[] _ridgeFarRand = RandTable(777, 17);   // segs+1 ridge offsets
    static readonly float[] _ridgeNearRand = RandTable(888, 21);
    static readonly float[] _hazeRand = RandTable(555, 5 * 5);    // 5 draws per haze band

    // Zero-alloc xorshift PRNG for per-frame procedural drawing (§5 2.6) —
    // replaces per-frame `new Random(seed)` in the city/ruin draws. Not
    // sequence-compatible with System.Random, which is fine here: city seeds
    // come from string.GetHashCode and already re-roll every process.
    struct DrawRand
    {
        uint _s;
        public DrawRand(int seed)
        {
            // SplitMix32 scramble so adjacent seeds give unrelated streams.
            uint z = (uint)seed + 0x9E3779B9u;
            z = (z ^ (z >> 16)) * 0x85EBCA6Bu;
            z = (z ^ (z >> 13)) * 0xC2B2AE35u;
            _s = z ^ (z >> 16);
            if (_s == 0) _s = 0x9E3779B9u;
        }
        uint NextU() { uint x = _s; x ^= x << 13; x ^= x >> 17; x ^= x << 5; return _s = x; }
        public float NextSingle() => (NextU() >> 8) * (1f / 16777216f);
        public int Next(int maxExclusive) => (int)((ulong)NextU() * (uint)maxExclusive >> 32);
        public int Next() => (int)(NextU() >> 1);
    }

    // Per-frame scratch buffers (§5 2.6) — the draw path must not allocate.
    static readonly (float X, float Y)[] _ridgePts = new (float, float)[24]; // max segs+1 = 21
    static readonly (float bx, float bw, float bh, byte r, byte g, byte b, int rt, float rh,
        int seed, float stepW, float stepH, float spireH, float antH, int antCol)[] _cityBuildings =
        new (float, float, float, byte, byte, byte, int, float, int, float, float, float, float, int)[16]; // n ≤ 15
    static readonly (float fx, float fw, float fh, float tip, float t)[] _ruinFrags =
        new (float, float, float, float, float)[11]; // n ≤ 10

    // Modern font (embedded JetBrains Mono, Windows system TTFs as fallback)
    // — replaces raylib's blocky bitmap default
    static Font _uiFont;
    static Font _uiFontBold;
    static bool _uiFontTried;
    static bool _uiFontReady;

    public static Font UiFont => _uiFontReady ? _uiFont : Raylib.GetFontDefault();
    public static Font UiFontBold => _uiFontReady ? _uiFontBold : Raylib.GetFontDefault();

    /// <summary>Modern text draw (TrueType). Falls back to bitmap if font failed to load.</summary>
    public static void DrawTextM(string text, float x, float y, float size, Color color, bool bold = false)
    {
        if (_uiFontReady)
        {
            var f = bold ? _uiFontBold : _uiFont;
            Raylib.DrawTextEx(f, text, new Vector2(x, y), size, 0.5f, color);
        }
        else Raylib.DrawText(text, (int)x, (int)y, (int)size, color);
    }

    public static int MeasureTextM(string text, float size, bool bold = false)
    {
        if (_uiFontReady)
        {
            var f = bold ? _uiFontBold : _uiFont;
            return (int)Raylib.MeasureTextEx(f, text, size, 0.5f).X;
        }
        return Raylib.MeasureText(text, (int)size);
    }
    static int _fxW;
    static int _fxH;

    public static void Shutdown()
    {
        if (_fxReady)
        {
            UnloadFxTargets();
            _fxReady = false;
        }

        if (_grainReady)
        {
            Raylib.UnloadTexture(_grainTexture);
            _grainReady = false;
        }

        if (_gradientReady)
        {
            Raylib.UnloadTexture(_gradientTex);
            _gradientReady = false;
        }

        if (_moonReady)
        {
            Raylib.UnloadTexture(_moonTex);
            _moonReady = false;
        }

        if (_uiFont.BaseSize > 0) { Raylib.UnloadFont(_uiFont); _uiFont = default; }
        if (_uiFontBold.BaseSize > 0) { Raylib.UnloadFont(_uiFontBold); _uiFontBold = default; }
        _uiFontReady = false;
        _uiFontTried = false;

        if (_postShaderActive)
        {
            Raylib.UnloadShader(_postShader);
            _postShaderActive = false;
        }
        _postShaderTried = false;

        if (_bloomActive)
        {
            Raylib.UnloadShader(_bloomPrefilter);
            Raylib.UnloadShader(_bloomDown);
            Raylib.UnloadShader(_bloomUp);
            _bloomActive = false;
        }
        _bloomShaderTried = false;
    }

    /// <summary>§4.9 hand-assembled FP16 render target, mirroring raylib's
    /// LoadRenderTexture step-for-step with the color format swapped to
    /// R16G16B16A16 (GL_RGBA16F under GL 3.3). Returns false — with every GL
    /// object already released — when the driver rejects half-float textures
    /// or reports the FBO incomplete; the caller then falls back to RGBA8.</summary>
    static unsafe bool TryLoadRenderTextureFp16(int w, int h, out RenderTexture2D rt)
    {
        rt = default;
        uint fbo = Rlgl.LoadFramebuffer();
        if (fbo == 0) return false;
        Rlgl.EnableFramebuffer(fbo);
        uint tex = Rlgl.LoadTexture(null, w, h, PixelFormat.UncompressedR16G16B16A16, 1);
        if (tex == 0) // GPU without half-float texture support
        {
            Rlgl.DisableFramebuffer();
            Rlgl.UnloadFramebuffer(fbo);
            return false;
        }
        uint depth = Rlgl.LoadTextureDepth(w, h, true); // depth renderbuffer, like stock
        Rlgl.FramebufferAttach(fbo, tex, FramebufferAttachType.ColorChannel0,
            FramebufferAttachTextureType.Texture2D, 0);
        Rlgl.FramebufferAttach(fbo, depth, FramebufferAttachType.Depth,
            FramebufferAttachTextureType.Renderbuffer, 0);
        bool ok = Rlgl.FramebufferComplete(fbo);
        Rlgl.DisableFramebuffer();
        if (!ok)
        {
            Rlgl.UnloadTexture(tex);
            Rlgl.UnloadFramebuffer(fbo); // also deletes the attached depth renderbuffer
            return false;
        }
        rt.Id = fbo;
        rt.Texture.Id = tex;
        rt.Texture.Width = w;
        rt.Texture.Height = h;
        rt.Texture.Mipmaps = 1;
        rt.Texture.Format = PixelFormat.UncompressedR16G16B16A16;
        rt.Depth.Id = depth;
        rt.Depth.Width = w;
        rt.Depth.Height = h;
        rt.Depth.Mipmaps = 1;
        rt.Depth.Format = (PixelFormat)19; // raylib's DEPTH_COMPONENT_24 sentinel
        return true;
    }

    /// <summary>All-or-nothing FP16 creation of the scene target + bloom mips.
    /// On any failure every already-created FP16 object is released and the
    /// caller reverts the whole chain to RGBA8 — targets never mix formats.</summary>
    static bool TryCreateHdrTargets(int w, int h)
    {
        if (!TryLoadRenderTextureFp16(w, h, out _frameTarget)) return false;
        for (int i = 0; i < BloomMipCount; i++)
        {
            if (!TryLoadRenderTextureFp16(Math.Max(1, w >> (i + 1)), Math.Max(1, h >> (i + 1)),
                out _bloomMips[i]))
            {
                UnloadManualTarget(ref _frameTarget);
                for (int j = 0; j < i; j++) UnloadManualTarget(ref _bloomMips[j]);
                return false;
            }
        }
        return true;
    }

    /// <summary>Single teardown owner for the scene + bloom targets (resize
    /// branch and Shutdown both come through here).</summary>
    static void UnloadFxTargets()
    {
        if (_hdrActive)
        {
            UnloadManualTarget(ref _frameTarget);
            for (int i = 0; i < BloomMipCount; i++) UnloadManualTarget(ref _bloomMips[i]);
            _hdrActive = false;
        }
        else
        {
            Raylib.UnloadRenderTexture(_frameTarget);
            for (int i = 0; i < BloomMipCount; i++) Raylib.UnloadRenderTexture(_bloomMips[i]);
        }
        // The light buffer (§5 5.2) is always a stock RGBA8 target — stock
        // teardown on both the HDR and fallback paths.
        Raylib.UnloadRenderTexture(_lightTarget);
        _lightTarget = default;
    }

    // Manual FBO teardown (§4.9): color texture first, then the framebuffer —
    // Rlgl.UnloadFramebuffer queries and deletes the attached depth
    // renderbuffer itself. Stock UnloadRenderTexture would issue the exact
    // same two GL deletes on the wrapped struct, so routing a hand-assembled
    // target through BOTH paths would double-free GL names; manual targets are
    // torn down only here, never via UnloadRenderTexture.
    static void UnloadManualTarget(ref RenderTexture2D rt)
    {
        if (rt.Id == 0) return;
        Rlgl.UnloadTexture(rt.Texture.Id);
        Rlgl.UnloadFramebuffer(rt.Id);
        rt = default;
    }

    /// <summary>One bloom-chain pass: full-quad shader blit srcTex → dst. Explicit
    /// negative-height source rect on every RT→RT blit (each pass un-flips raylib's
    /// render-texture y-inversion — never rely on even-pass-count cancellation).</summary>
    static void BloomBlit(Shader shader, Texture2D srcTex, RenderTexture2D dst)
    {
        Raylib.BeginTextureMode(dst);
        Raylib.BeginShaderMode(shader);
        Raylib.DrawTexturePro(srcTex,
            new Rectangle(0, 0, srcTex.Width, -srcTex.Height),
            new Rectangle(0, 0, dst.Texture.Width, dst.Texture.Height),
            Vector2.Zero, 0, Color.White);
        Raylib.EndShaderMode();
        Raylib.EndTextureMode();
    }

    /// <summary>GPU threshold bloom (§5 2.1). Bright pixels come from the rendered
    /// scene itself — soft-knee bright pass into mip0 (W/2), dual-Kawase down to
    /// W/16 and back up, then additive composite into _frameTarget so the §4.1
    /// uber-shader blit grades on top of the bloomed scene.</summary>
    static void RenderBloom()
    {
        // Bright pass: _frameTarget → mip0. threshold/knee/intensity were
        // uploaded once at shader load (uniform values persist on the program).
        BloomBlit(_bloomPrefilter, _frameTarget.Texture, _bloomMips[0]);

        for (int i = 0; i < BloomMipCount - 1; i++)
        {
            var srcTex = _bloomMips[i].Texture;
            Raylib.SetShaderValue(_bloomDown, _locBloomDownHalfPixel,
                new Vector2(0.5f / srcTex.Width, 0.5f / srcTex.Height), ShaderUniformDataType.Vec2);
            BloomBlit(_bloomDown, srcTex, _bloomMips[i + 1]);
        }

        // Up chain overwrites the lower mips in place — pure dual-Kawase, each
        // step only needs the previous step's output.
        for (int i = BloomMipCount - 1; i > 0; i--)
        {
            var srcTex = _bloomMips[i].Texture;
            Raylib.SetShaderValue(_bloomUp, _locBloomUpHalfPixel,
                new Vector2(0.5f / srcTex.Width, 0.5f / srcTex.Height), ShaderUniformDataType.Vec2);
            BloomBlit(_bloomUp, srcTex, _bloomMips[i - 1]);
        }

        // Additive composite, RGB only: dstRGB += srcRGB·srcA while alpha blends
        // src-over (srcA + dstA·(1−srcA)) — bloom can't pollute _frameTarget's
        // alpha ahead of the composite blit.
        Rlgl.SetBlendFactorsSeparate(GlSrcAlpha, GlOne, GlOne, GlOneMinusSrcAlpha, GlFuncAdd, GlFuncAdd);
        var tex = _bloomMips[0].Texture;
        Raylib.BeginTextureMode(_frameTarget);
        Raylib.BeginBlendMode(BlendMode.CustomSeparate);
        Raylib.DrawTexturePro(tex,
            new Rectangle(0, 0, tex.Width, -tex.Height),
            new Rectangle(0, 0, _fxW, _fxH),
            Vector2.Zero, 0, Color.White);
        Raylib.EndBlendMode();
        Raylib.EndTextureMode();
    }

    /// <summary>§5 5.2: queue one light blob for this frame's light pass. Called
    /// from inside the world-pass draw functions, where the positions already
    /// exist. Past the cap the newest entries drop — by collection order that
    /// sacrifices late explosion blobs before scenery lights, acceptable in
    /// max-chaos frames.</summary>
    static void AddLight(float x, float y, float radius, byte r, byte g, byte b, byte a)
    {
        if (!_lightsOn || _lightCount >= MaxLights || a < 2 || radius <= 2f) return;
        _lights[_lightCount++] = (x, y, radius, r, g, b, a);
    }

    /// <summary>§5 5.2 light pass: all collected blobs into the quarter-res
    /// target — one clear, one blend mode, one texture (the shared radial
    /// gradient), so the whole pass is a single batched draw.</summary>
    static void RenderLightBuffer()
    {
        float sx = _lightTarget.Texture.Width / (float)_fxW;
        float sy = _lightTarget.Texture.Height / (float)_fxH;
        Raylib.BeginTextureMode(_lightTarget);
        Raylib.ClearBackground(Color.Blank);
        Raylib.BeginBlendMode(BlendMode.Additive);
        for (int i = 0; i < _lightCount; i++)
        {
            ref var l = ref _lights[i];
            DrawGradientCircle(l.X * sx, l.Y * sy, l.R * sx, new Color(l.Cr, l.Cg, l.Cb, l.Ca));
        }
        Raylib.EndBlendMode();
        Raylib.EndTextureMode();
    }

    /// <summary>Feature 2.2 per-frame uniforms: shockwave ring fronts and heat-shimmer
    /// sources for the uber-shader's refraction pass. stackalloc + the Span overload of
    /// SetShaderValueV — zero heap allocation. World y converts to texcoord y-up
    /// (cy = 1 - y/H); radii normalize by screen height to match the shader's
    /// aspect-corrected distance space.</summary>
    static void UploadRefractionUniforms(GameState s)
    {
        float invW = 1f / s.W, invH = 1f / s.H;

        // Newest-first copy = oldest-first eviction beyond 16 (the newest fronts
        // are the strongest on screen; the oldest are nearly faded anyway).
        Span<Vector4> waves = stackalloc Vector4[MaxShaderShockwaves];
        int waveCount = 0;
        for (int i = s.Shockwaves.Count - 1; i >= 0 && waveCount < MaxShaderShockwaves; i--)
        {
            var sw = s.Shockwaves[i];
            float strength = sw.Life / sw.MaxLife; // = 1 - age
            if (strength <= 0.01f) continue;
            // EMP flag: the EMP explosion that spawned this wave is still live at
            // the exact same center (SpawnExpl adds both from one x/y, never moved).
            bool emp = false;
            for (int j = s.Explosions.Count - 1; j >= 0; j--)
            {
                var e = s.Explosions[j];
                if (e.Emp && e.X == sw.X && e.Y == sw.Y) { emp = true; break; }
            }
            waves[waveCount++] = new Vector4(sw.X * invW, 1f - sw.Y * invH,
                sw.Radius * invH, emp ? 1f + strength : MathF.Min(strength, 0.999f));
        }

        // 4 hottest live non-EMP explosions (EMP is light, not heat), ranked by
        // remaining life × size — insertion sort into a fixed descending array.
        Span<Vector4> heat = stackalloc Vector4[MaxShaderHeatSources];
        Span<float> heatRank = stackalloc float[MaxShaderHeatSources];
        int heatCount = 0;
        for (int i = 0; i < s.Explosions.Count; i++)
        {
            var e = s.Explosions[i];
            if (e.Emp || e.MaxRadius < 70f) continue;
            float h = (e.Life / e.MaxLife) * MathH.Clamp(e.MaxRadius / 160f, 0.55f, 1f);
            if (h <= 0.02f) continue;
            if (heatCount == MaxShaderHeatSources && h <= heatRank[MaxShaderHeatSources - 1]) continue;
            int pos = heatCount < MaxShaderHeatSources ? heatCount : MaxShaderHeatSources - 1;
            while (pos > 0 && h > heatRank[pos - 1])
            {
                heatRank[pos] = heatRank[pos - 1];
                heat[pos] = heat[pos - 1];
                pos--;
            }
            heatRank[pos] = h;
            heat[pos] = new Vector4(e.X * invW, 1f - e.Y * invH, e.MaxRadius * invH, h);
            if (heatCount < MaxShaderHeatSources) heatCount++;
        }

        if (waveCount > 0)
            Raylib.SetShaderValueV<Vector4>(_postShader, _locShockwaves, waves, ShaderUniformDataType.Vec4, waveCount);
        Raylib.SetShaderValue(_postShader, _locShockwaveCount, waveCount, ShaderUniformDataType.Int);
        if (heatCount > 0)
            Raylib.SetShaderValueV<Vector4>(_postShader, _locHeatSources, heat, ShaderUniformDataType.Vec4, heatCount);
        Raylib.SetShaderValue(_postShader, _locHeatCount, heatCount, ShaderUniformDataType.Int);
    }

    public static void DrawAll(GameState s)
    {
        EnsureFxTargets(s);

        // §4.4 mirror the colorblind toggle so the static Palette.VariantColor
        // (no GameState handle at its call sites) reads the right hue table.
        Palette.Colorblind = s.Settings.ColorblindMode;

        // §5 5.2: arm light collection for this frame's world pass. Without the
        // uber-shader there is no composite to consume the buffer — skip it all.
        _lightsOn = _postShaderActive && !_lightDisabled;
        _lightCount = 0;

        Raylib.BeginTextureMode(_frameTarget);
        Raylib.ClearBackground(new Color(2, 5, 10, 255));

        DrawSky(s);
        DrawNebula(s);
        DrawAurora(s);
        DrawStars(s);
        DrawBokeh(s);
        DrawClouds(s);
        DrawShootingStars(s);
        DrawWeatherBack(s);
        DrawMountains(s);
        DrawGround(s);
        DrawScorches(s);
        Raylib.BeginBlendMode(BlendMode.Additive);
        DrawScorchGlow(s); // §5 5.3 cooling ground glow — same additive group
        DrawLightBursts(s);
        Raylib.EndBlendMode();
        DrawCities(s);
        DrawBases(s);
        DrawHellRaiser(s);
        DrawPhalanxes(s);
        DrawUFOs(s);
        DrawRaiders(s);
        DrawDemon(s);
        DrawMothership(s);
        DrawFighters(s);
        DrawWeatherFront(s);
        DrawLightning(s);
        DrawTrails(s);
        DrawSmoke(s);
        DrawEnemyMissiles(s);
        DrawPlayerMissiles(s);
        Raylib.BeginBlendMode(BlendMode.Additive);
        DrawMuzzleFlashes(s);
        DrawExplosions(s);
        DrawBlastFlashes(s); // §5 5.3 1-frame detonation pop — same additive group
        DrawSparks(s);
        DrawShockwaves(s);
        Raylib.EndBlendMode();
        DrawDebris(s);
        DrawFloatingTexts(s);

        Raylib.EndTextureMode();

        if (_lightsOn) RenderLightBuffer(); // §5 5.2 — before bloom
        if (_bloomActive) RenderBloom();

        // §4.1 composite — ONE DrawTexturePro: trauma translation + roll about
        // screen center. Settings.ShakeIntensity (§5 3.1) scales the amplitude.
        float t2 = s.Trauma * s.Trauma * s.Settings.ShakeIntensity;
        float offX = 0f, offY = 0f, roll = 0f;
        if (t2 > 0.0001f)
        {
            float nt = FeelDirector.Clock * NoiseRate;
            offX = SampleNoise(_shakeNoiseX, nt) * MaxShakePx * t2;
            offY = SampleNoise(_shakeNoiseY, nt) * MaxShakePx * 0.65f * t2;
            roll = SampleNoise(_shakeNoiseRoll, nt) * MaxRollDeg * t2;
        }
        // Zoom derived from the sampled roll/offset so the rotated quad provably
        // contains the screen at any aspect (a static 1.03 exposed corners at
        // ±1.5° roll). Cover needs quad half-extents of at least
        //   (W/2+|offX|)·cosθ + (H/2+|offY|)·sinθ   (width)
        //   (W/2+|offX|)·sinθ + (H/2+|offY|)·cosθ   (height);
        // the +1 px pad absorbs float rounding when zoom ≈ 1.
        float rollRad = MathF.Abs(roll) * (MathF.PI / 180f);
        float cr = MathF.Cos(rollRad), sr = MathF.Sin(rollRad);
        float exW = s.W + 2f * (MathF.Abs(offX) + 1f);
        float exH = s.H + 2f * (MathF.Abs(offY) + 1f);
        float zoom = MathF.Max((exW * cr + exH * sr) / s.W, (exW * sr + exH * cr) / s.H);
        var src = new Rectangle(0, 0, _frameTarget.Texture.Width, -_frameTarget.Texture.Height);
        var dst = new Rectangle(s.W * 0.5f + offX, s.H * 0.5f + offY, s.W * zoom, s.H * zoom);
        if (_postShaderActive)
        {
            // Time wraps to keep the grain hash's sin() argument in float32 range.
            Raylib.SetShaderValue(_postShader, _locTime, FeelDirector.Clock % 64f, ShaderUniformDataType.Float);
            Raylib.SetShaderValue(_postShader, _locDanger, s.Danger, ShaderUniformDataType.Float);
            Raylib.SetShaderValue(_postShader, _locChromatic, s.Chromatic, ShaderUniformDataType.Float);
            // Flash reduction (§5 3.1): suppress the full-screen shader flash;
            // DrawPostFx renders the edge vignette pulse instead.
            float flashAmt = s.Settings.FlashReduction ? 0f : MathH.Clamp(s.Flash * 1.1f, 0f, 1f);
            Raylib.SetShaderValue(_postShader, _locFlashAmount, flashAmt, ShaderUniformDataType.Float);
            Raylib.SetShaderValue(_postShader, _locFlashDir, FlashDirDefault, ShaderUniformDataType.Vec2);
            Raylib.SetShaderValue(_postShader, _locFlashColor, FlashColorDefault, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(_postShader, _locResolution, new Vector2(s.W, s.H), ShaderUniformDataType.Vec2);
            Raylib.SetShaderValue(_postShader, _locHdrActive, _hdrActive ? 1f : 0f, ShaderUniformDataType.Float);
            // §5 5.2: day/night ambient key (SkyCycle day peaks at 0.86 —
            // normalize so full daylight reads exactly 1.0) + the light buffer
            // as a second sampler. rlgl clears its extra texture slots after
            // every batch draw, so the sampler rebinds every frame.
            var (_, dayF, _, _) = SkyCycle(s.Time);
            Raylib.SetShaderValue(_postShader, _locDayFactor,
                MathH.Clamp(dayF / 0.86f, 0f, 1f), ShaderUniformDataType.Float);
            Raylib.SetShaderValue(_postShader, _locLightActive, _lightsOn ? 1f : 0f, ShaderUniformDataType.Float);
            if (_lightsOn) Raylib.SetShaderValueTexture(_postShader, _locLightTex, _lightTarget.Texture);
            // §5 5.4 theme grade + CRT (Modern = identity/0, diff-identical) + the
            // last-city red-shift state modifier (1 when exactly one city stands).
            var gp = Palette.Active(s);
            Raylib.SetShaderValue(_postShader, _locGradeLift, gp.Lift.V, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(_postShader, _locGradeGamma, gp.Gamma.V, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(_postShader, _locGradeGain, gp.Gain.V, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(_postShader, _locCrtAmount, gp.CrtAmount, ShaderUniformDataType.Float);
            Raylib.SetShaderValue(_postShader, _locLastCity, s.AliveCities == 1 ? 1f : 0f, ShaderUniformDataType.Float);
            UploadRefractionUniforms(s);
            Raylib.BeginShaderMode(_postShader);
        }
        Raylib.DrawTexturePro(_frameTarget.Texture, src, dst,
            new Vector2(s.W * zoom * 0.5f, s.H * zoom * 0.5f), roll, Color.White);
        if (_postShaderActive) Raylib.EndShaderMode();

        DrawPostFx(s);

        // HUD / crosshair / overlays live on the backbuffer — they never
        // shake, rotate, or refract with the world (overlays on top)
        DrawCrosshair(s);
        DrawHUD(s);
        DrawOverlays(s);
    }

    static void EnsureFxTargets(GameState s)
    {
        int w = Math.Max(1, (int)s.W);
        int h = Math.Max(1, (int)s.H);
        if (!_fxReady || _fxW != w || _fxH != h)
        {
            if (_fxReady) UnloadFxTargets();

            // §5 5.1: FP16 first, so additive overlap accumulates past 1.0 for
            // the bloom threshold and the ACES tonemap. A driver that rejects
            // FP16 once will reject it on every resize — latch the fallback.
            _hdrActive = !_hdrDisabled && TryCreateHdrTargets(w, h);
            if (!_hdrActive && !_hdrDisabled)
            {
                _hdrDisabled = true;
                Raylib.TraceLog(TraceLogLevel.Warning,
                    "HDR: FP16 framebuffer unavailable, falling back to RGBA8");
            }
            if (!_hdrActive)
            {
                _frameTarget = Raylib.LoadRenderTexture(w, h);
                for (int i = 0; i < BloomMipCount; i++)
                    _bloomMips[i] = Raylib.LoadRenderTexture(Math.Max(1, w >> (i + 1)), Math.Max(1, h >> (i + 1)));
            }

            Raylib.SetTextureFilter(_frameTarget.Texture, TextureFilter.Bilinear);
            // Dual-Kawase mip chain W/2…W/16 — bilinear for the half-pixel taps,
            // clamp so edge taps never wrap to the opposite screen edge.
            for (int i = 0; i < BloomMipCount; i++)
            {
                Raylib.SetTextureFilter(_bloomMips[i].Texture, TextureFilter.Bilinear);
                Raylib.SetTextureWrap(_bloomMips[i].Texture, TextureWrap.Clamp);
            }

            // §5 5.2: quarter-res light buffer. Bilinear hides the upscale;
            // clamp keeps the shader's displaced-uv taps off the opposite edge.
            _lightTarget = Raylib.LoadRenderTexture(Math.Max(1, w / 4), Math.Max(1, h / 4));
            Raylib.SetTextureFilter(_lightTarget.Texture, TextureFilter.Bilinear);
            Raylib.SetTextureWrap(_lightTarget.Texture, TextureWrap.Clamp);

            _fxW = w;
            _fxH = h;
            _fxReady = true;
        }

        if (!_postShaderTried)
        {
            _postShaderTried = true;
            _postShader = Raylib.LoadShaderFromMemory(null, Shaders.PostFxFrag);
            _locResolution = Raylib.GetShaderLocation(_postShader, "resolution");
            // A failed compile hands back raylib's DEFAULT shader id (which
            // IsShaderValid still accepts) — it has none of our uniforms, so
            // also require a live custom-uniform location.
            if (Raylib.IsShaderValid(_postShader) && _locResolution != -1)
            {
                _locTime = Raylib.GetShaderLocation(_postShader, "time");
                _locDanger = Raylib.GetShaderLocation(_postShader, "danger");
                _locChromatic = Raylib.GetShaderLocation(_postShader, "chromatic");
                _locFlashAmount = Raylib.GetShaderLocation(_postShader, "flashAmount");
                _locFlashDir = Raylib.GetShaderLocation(_postShader, "flashDir");
                _locFlashColor = Raylib.GetShaderLocation(_postShader, "flashColor");
                // GL returns array locations under "name[0]" on some drivers,
                // bare "name" on others — try both. -1 stays harmless (no-op).
                _locShockwaves = Raylib.GetShaderLocation(_postShader, "shockwaves");
                if (_locShockwaves == -1) _locShockwaves = Raylib.GetShaderLocation(_postShader, "shockwaves[0]");
                _locShockwaveCount = Raylib.GetShaderLocation(_postShader, "shockwaveCount");
                _locHeatSources = Raylib.GetShaderLocation(_postShader, "heatSources");
                if (_locHeatSources == -1) _locHeatSources = Raylib.GetShaderLocation(_postShader, "heatSources[0]");
                _locHeatCount = Raylib.GetShaderLocation(_postShader, "heatCount");
                // §5 5.1: hdrActive re-uploads per frame (the HDR chain can drop
                // to RGBA8 on a resize failure); the pre-ACES exposure lift is a
                // tuning constant — upload once, uniform values persist.
                _locHdrActive = Raylib.GetShaderLocation(_postShader, "hdrActive");
                Raylib.SetShaderValue(_postShader, Raylib.GetShaderLocation(_postShader, "exposure"),
                    HdrExposure, ShaderUniformDataType.Float);
                // §5 5.2 light-buffer uniforms (sampler rebinds per frame)
                _locLightTex = Raylib.GetShaderLocation(_postShader, "lightTex");
                _locDayFactor = Raylib.GetShaderLocation(_postShader, "dayFactor");
                _locLightActive = Raylib.GetShaderLocation(_postShader, "lightActive");
                // §5 5.4 theme grade + CRT + last-city
                _locGradeLift = Raylib.GetShaderLocation(_postShader, "gradeLift");
                _locGradeGamma = Raylib.GetShaderLocation(_postShader, "gradeGamma");
                _locGradeGain = Raylib.GetShaderLocation(_postShader, "gradeGain");
                _locCrtAmount = Raylib.GetShaderLocation(_postShader, "crtAmount");
                _locLastCity = Raylib.GetShaderLocation(_postShader, "lastCity");
                _postShaderActive = true;
            }
        }

        if (!_bloomShaderTried)
        {
            _bloomShaderTried = true;
            _bloomPrefilter = Raylib.LoadShaderFromMemory(null, Shaders.BloomPrefilterFrag);
            _bloomDown = Raylib.LoadShaderFromMemory(null, Shaders.BloomDownsampleFrag);
            _bloomUp = Raylib.LoadShaderFromMemory(null, Shaders.BloomUpsampleFrag);
            int locThreshold = Raylib.GetShaderLocation(_bloomPrefilter, "threshold");
            _locBloomDownHalfPixel = Raylib.GetShaderLocation(_bloomDown, "halfPixel");
            _locBloomUpHalfPixel = Raylib.GetShaderLocation(_bloomUp, "halfPixel");
            // Same failed-compile detection as the post shader: a live custom
            // uniform proves we didn't get raylib's default shader back.
            if (Raylib.IsShaderValid(_bloomPrefilter) && locThreshold != -1
                && Raylib.IsShaderValid(_bloomDown) && _locBloomDownHalfPixel != -1
                && Raylib.IsShaderValid(_bloomUp) && _locBloomUpHalfPixel != -1)
            {
                // Tuning constants never change at runtime — upload once.
                Raylib.SetShaderValue(_bloomPrefilter, locThreshold, BloomThreshold, ShaderUniformDataType.Float);
                Raylib.SetShaderValue(_bloomPrefilter, Raylib.GetShaderLocation(_bloomPrefilter, "knee"),
                    BloomKnee, ShaderUniformDataType.Float);
                Raylib.SetShaderValue(_bloomPrefilter, Raylib.GetShaderLocation(_bloomPrefilter, "intensity"),
                    BloomIntensity, ShaderUniformDataType.Float);
                _bloomActive = true;
            }
            else
            {
                // Legacy CPU bloom was deleted (2.1) — the scene renders unbloomed.
                Raylib.TraceLog(TraceLogLevel.Warning, "BLOOM: shader compile failed, rendering without bloom");
            }
        }

        if (!_grainReady)
        {
            var noise = Raylib.GenImageWhiteNoise(GrainSize, GrainSize, 0.5f);
            _grainTexture = Raylib.LoadTextureFromImage(noise);
            Raylib.UnloadImage(noise);
            Raylib.SetTextureFilter(_grainTexture, TextureFilter.Point);
            _grainReady = true;
        }

        if (!_gradientReady)
        {
            _gradientTex = GenRadialGradient(GradientSize);
            Raylib.SetTextureFilter(_gradientTex, TextureFilter.Bilinear);
            _gradientReady = true;
        }

        if (!_moonReady)
        {
            _moonTex = GenMoonTexture(MoonSize);
            Raylib.SetTextureFilter(_moonTex, TextureFilter.Bilinear);
            _moonReady = true;
        }

        if (!_uiFontTried)
        {
            _uiFontTried = true;
            // Embedded JetBrains Mono (OFL, §5 1.5) — cross-platform primary.
            // Resource bytes are read exactly once here, never per frame.
            BuildUiCodepoints();
            TryLoadEmbeddedFont("JetBrainsMono-Regular.ttf", ref _uiFont);
            TryLoadEmbeddedFont("JetBrainsMono-Bold.ttf", ref _uiFontBold);

            // Windows system fonts — fallback when the embedded resource is missing.
            if (_uiFont.BaseSize <= 0)
            {
                string[] regularPaths = { @"C:\Windows\Fonts\segoeui.ttf", @"C:\Windows\Fonts\consola.ttf", @"C:\Windows\Fonts\arial.ttf" };
                foreach (var p in regularPaths)
                {
                    if (File.Exists(p))
                    {
                        _uiFont = Raylib.LoadFontEx(p, 64, _uiCodepoints, _uiCodepoints!.Length);
                        Raylib.SetTextureFilter(_uiFont.Texture, TextureFilter.Bilinear);
                        break;
                    }
                }
            }
            if (_uiFontBold.BaseSize <= 0)
            {
                string[] boldPaths = { @"C:\Windows\Fonts\segoeuib.ttf", @"C:\Windows\Fonts\consolab.ttf", @"C:\Windows\Fonts\arialbd.ttf" };
                foreach (var p in boldPaths)
                {
                    if (File.Exists(p))
                    {
                        _uiFontBold = Raylib.LoadFontEx(p, 64, _uiCodepoints, _uiCodepoints!.Length);
                        Raylib.SetTextureFilter(_uiFontBold.Texture, TextureFilter.Bilinear);
                        break;
                    }
                }
            }
            _uiFontReady = _uiFont.BaseSize > 0 && _uiFontBold.BaseSize > 0;
        }
    }

    // Raylib's default bake is ASCII 32-126 only; shop/HUD strings use a few
    // typographic glyphs (· — →) the TTFs carry but the default atlas drops —
    // they'd render as '?'. Built once at font init.
    static int[]? _uiCodepoints;
    static void BuildUiCodepoints()
    {
        var cp = new int[95 + 3];
        for (int i = 0; i < 95; i++) cp[i] = 32 + i;
        cp[95] = 0x00B7; cp[96] = 0x2014; cp[97] = 0x2192; // · — →
        _uiCodepoints = cp;
    }

    static void TryLoadEmbeddedFont(string suffix, ref Font font)
    {
        var asm = typeof(Renderer).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var f = Raylib.LoadFontFromMemory(".ttf", bytes, 64, _uiCodepoints, _uiCodepoints!.Length);
            if (f.BaseSize > 0)
            {
                Raylib.SetTextureFilter(f.Texture, TextureFilter.Bilinear);
                font = f;
            }
            return;
        }
    }

    /// <summary>Procedural moon: value-noise maria + micro crater detail + limb shading + rim highlight. Port of HTML moonTexture().</summary>
    static Texture2D GenMoonTexture(int size)
    {
        var img = Raylib.GenImageColor(size, size, new Color((byte)0, (byte)0, (byte)0, (byte)0));
        float cx = size * 0.5f;
        float cy = size * 0.5f;
        float rr = size * 0.5f;
        const float lightDirX = -0.58f;
        const float lightDirY = -0.35f;

        static float Hash2(float x, float y)
        {
            float v = MathF.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
            return v - MathF.Floor(v);
        }
        static float Noise2(float x, float y)
        {
            float xi = MathF.Floor(x), yi = MathF.Floor(y);
            float xf = x - xi, yf = y - yi;
            float u = xf * xf * (3 - 2 * xf);
            float v = yf * yf * (3 - 2 * yf);
            float a = Hash2(xi, yi);
            float b = Hash2(xi + 1, yi);
            float c = Hash2(xi, yi + 1);
            float d = Hash2(xi + 1, yi + 1);
            float ab = a + (b - a) * u;
            float cd = c + (d - c) * u;
            return ab + (cd - ab) * v;
        }

        unsafe
        {
            Color* pixels = (Color*)img.Data;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - cx) / rr;
                float dy = (y + 0.5f - cy) / rr;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d > 1f) continue;

                float nz1 = Noise2(dx * 4.6f + 7.2f, dy * 4.6f + 2.1f);
                float nz2 = Noise2(dx * 10.3f + 19.4f, dy * 10.3f + 3.7f);
                float nz3 = Noise2(dx * 18.7f + 1.8f, dy * 18.7f + 8.3f);
                float nz4 = Noise2(dx * 27.7f + 5.9f, dy * 27.7f + 12.6f);

                float broad = nz1 * 0.72f + nz2 * 0.28f;
                float fine = nz3 - 0.5f;
                float micro = nz4 - 0.5f;
                float mariaMask = MathF.Max(0f, (broad - 0.5f) / 0.38f);
                float maria = mariaMask * mariaMask * (0.24f + nz2 * 0.22f);

                float dot = MathF.Max(0f, -(dx * lightDirX + dy * lightDirY));
                float limb = d * d;
                float albedo = 0.79f + fine * 0.2f + micro * 0.11f - maria * 1.08f;
                float shade = 0.62f + dot * 0.31f - limb * 0.24f;
                float tone = MathH.Clamp(albedo * shade, 0.24f, 1f);
                float contrast = MathH.Clamp((tone - 0.5f) * 1.46f + 0.5f, 0.14f, 1f);

                byte rch = (byte)MathH.Clamp(174 * contrast + 14, 0, 255);
                byte gch = (byte)MathH.Clamp(190 * contrast + 18, 0, 255);
                byte bch = (byte)MathH.Clamp(216 * contrast + 22, 0, 255);
                float edge = MathH.Clamp((1f - d) / 0.04f, 0f, 1f);
                byte alpha = (byte)(255 * edge);
                pixels[y * size + x] = new Color(rch, gch, bch, alpha);
            }

            // Rim highlight pass (soft bright crescent on lit side, dark limb on shadow side) — emulates HTML's "screen" radial gradient
            float rimCx = cx - rr * 0.34f;
            float rimCy = cy - rr * 0.36f;
            float rimInner = rr * 0.14f;
            float rimOuter = rr * 1.02f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - cx) / rr;
                float dy = (y + 0.5f - cy) / rr;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d > 0.99f) continue;
                float rdx = x + 0.5f - rimCx;
                float rdy = y + 0.5f - rimCy;
                float rd = MathF.Sqrt(rdx * rdx + rdy * rdy);
                float tt = MathH.Clamp((rd - rimInner) / (rimOuter - rimInner), 0f, 1f);
                // stop 0 @ 0.18 white, stop .72 transparent, stop 1 dark limb blue
                Color src = pixels[y * size + x];
                float addR, addG, addB;
                if (tt < 0.72f)
                {
                    float k = 1f - tt / 0.72f;
                    float a = 0.18f * k;
                    addR = 255f * a; addG = 255f * a; addB = 255f * a;
                    // screen blend: out = 1 - (1-a)*(1-b)
                    float sr = 1f - (1f - src.R / 255f) * (1f - addR / 255f);
                    float sg = 1f - (1f - src.G / 255f) * (1f - addG / 255f);
                    float sb = 1f - (1f - src.B / 255f) * (1f - addB / 255f);
                    src.R = (byte)MathH.Clamp(sr * 255, 0, 255);
                    src.G = (byte)MathH.Clamp(sg * 255, 0, 255);
                    src.B = (byte)MathH.Clamp(sb * 255, 0, 255);
                }
                else
                {
                    float k = (tt - 0.72f) / 0.28f;
                    float a = 0.28f * k;
                    // multiply-toward dark limb (58,78,118)
                    float lr = 58f / 255f, lg = 78f / 255f, lb = 118f / 255f;
                    float sr = MathH.Lerp(src.R / 255f, lr, a);
                    float sg = MathH.Lerp(src.G / 255f, lg, a);
                    float sb = MathH.Lerp(src.B / 255f, lb, a);
                    src.R = (byte)MathH.Clamp(sr * 255, 0, 255);
                    src.G = (byte)MathH.Clamp(sg * 255, 0, 255);
                    src.B = (byte)MathH.Clamp(sb * 255, 0, 255);
                }
                pixels[y * size + x] = src;
            }
        }

        var tex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        return tex;
    }

    /// <summary>Generate a 128x128 radial gradient texture: white center ? transparent edge, smooth quadratic falloff.</summary>
    static Texture2D GenRadialGradient(int size)
    {
        var img = Raylib.GenImageColor(size, size, new Color((byte)0, (byte)0, (byte)0, (byte)0));
        float half = size * 0.5f;
        unsafe
        {
            Color* pixels = (Color*)img.Data;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float a = MathH.Clamp(1 - d, 0, 1);
                a = a * a; // quadratic falloff for smooth HTML-like gradient
                byte alpha = (byte)(a * 255);
                pixels[y * size + x] = new Color((byte)255, (byte)255, (byte)255, alpha);
            }
        }
        var tex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        return tex;
    }

    /// <summary>Draw a soft radial glow using the pre-generated gradient texture.</summary>
    static void DrawGradientCircle(float cx, float cy, float radius, Color tint)
    {
        if (radius <= 0.5f || tint.A < 2) return;
        var dst = new Rectangle(cx - radius, cy - radius, radius * 2, radius * 2);
        Raylib.DrawTexturePro(_gradientTex,
            new Rectangle(0, 0, GradientSize, GradientSize),
            dst, Vector2.Zero, 0, tint);
    }

    // ?????????????? SKY ??????????????
    // ?? Sky Cycle (day/night) ?? matches HTML skyCycle() exactly
    static (float phase, float day, float night, float twilight) SkyCycle(float time)
    {
        const float cycleSeconds = 840f;
        float phase = (time % cycleSeconds) / cycleSeconds;
        float wave = (1 - MathF.Cos(phase * TAU)) * 0.5f;
        float day = wave * wave * (3 - 2 * wave) * 0.86f;
        float twilight = MathF.Pow(MathF.Max(0, 1 - MathF.Abs(wave * 2 - 1)), 1.35f);
        return (phase, day, 1 - day, twilight);
    }

    static (byte R, byte G, byte B) MixRgb((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, float t)
    {
        t = MathH.Clamp(t, 0, 1);
        return ((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    static void DrawSky(GameState s)
    {
        var (phase, day, night, twilight) = SkyCycle(s.Time);
        var p = Palette.Active(s); // §5 5.4 color authority

        // Night ? Day palette (top, mid, bottom) — Modern holds the pre-sweep literals
        var top = MixRgb(p.SkyTopN, p.SkyTopD, day);
        var mid = MixRgb(p.SkyMidN, p.SkyMidD, day);
        var botBase = MixRgb(p.SkyBotN, p.SkyBotD, day);
        // Twilight warmth
        var bot = MixRgb(botBase, p.SkyTwilightWarm, twilight * 0.24f);

        // Sky gradient via quadratic Bezier through (top, mid, bot) — smooth C¹, no kinks.
        // B(t) = (1-t)² * top + 2(1-t)t * mid + t² * bot, with t biased so `mid` still dominates around 0.36..0.45.
        int bands = 64;
        (byte R, byte G, byte B) SkyAt(float t)
        {
            // Quadratic Bezier through 3 control colors
            float u = 1f - t;
            float a = u * u;
            float b = 2f * u * t;
            float c = t * t;
            byte rr = (byte)MathH.Clamp(top.R * a + mid.R * b + bot.R * c, 0, 255);
            byte gg = (byte)MathH.Clamp(top.G * a + mid.G * b + bot.G * c, 0, 255);
            byte bb = (byte)MathH.Clamp(top.B * a + mid.B * b + bot.B * c, 0, 255);
            return (rr, gg, bb);
        }
        for (int i = 0; i < bands; i++)
        {
            float t0 = i / (float)bands;
            float t1 = (i + 1) / (float)bands;
            int y0 = (int)(t0 * s.H);
            int y1 = (int)(t1 * s.H);
            var c0 = SkyAt(t0);
            var c1 = SkyAt(t1);
            Raylib.DrawRectangleGradientV(0, y0, (int)s.W, y1 - y0 + 1,
                new Color(c0.R, c0.G, c0.B, (byte)255),
                new Color(c1.R, c1.G, c1.B, (byte)255));
        }

        // Horizon haze — wider + softer, with matched RGB at the transparent edges (prevents
        // the "dark grey midpoint" artifact from the old (0,0,0,0) edge color).
        float dangerTint = MathH.Clamp(s.Danger * 0.45f, 0, 0.4f);
        int warm = (int)(148 + twilight * 92 + day * 24);
        int cool = (int)(128 + day * 54);
        int blue = (int)(190 + day * 18);
        float hazeA = 0.055f + twilight * 0.07f + day * 0.035f; // reduced max alpha
        byte hazeFull = (byte)(hazeA * 255);
        int hazeW = 240; // widened from 130 for smoother falloff
        byte warmByte = (byte)MathH.Clamp(warm + dangerTint * 80, 0, 255);
        byte coolByte = (byte)MathH.Clamp(cool + dangerTint * 40, 0, 255);
        byte blueByte = (byte)MathH.Clamp(blue, 0, 255);
        // Keep RGB constant across the band and only taper the alpha — this removes the
        // brightness hump / visible seam at HorizonY caused by interpolating toward black.
        Raylib.DrawRectangleGradientV(0, (int)(s.HorizonY - hazeW), (int)s.W, hazeW,
            new Color(warmByte, coolByte, blueByte, (byte)0),
            new Color(warmByte, coolByte, blueByte, hazeFull));
        Raylib.DrawRectangleGradientV(0, (int)s.HorizonY, (int)s.W, hazeW,
            new Color(warmByte, coolByte, blueByte, hazeFull),
            new Color(warmByte, coolByte, blueByte, (byte)0));

        // ?? Moon ??
        float moonTrack = phase;
        float moonArc = MathF.Sin(moonTrack * MathF.PI);
        float mx = MathH.Lerp(-s.W * 0.12f, s.W * 1.12f, moonTrack);
        float my = MathH.Lerp(s.HorizonY * 0.95f, s.HorizonY * 0.2f, moonArc) + MathF.Cos(moonTrack * TAU) * 6;
        float mr = MathF.Max(35, s.W * 0.0336f);
        float moonVisRaw = MathH.Clamp((0.5f - day) / 0.22f, 0, 1);
        float moonVis = moonVisRaw * moonVisRaw * (3 - 2 * moonVisRaw);
        float moonA = moonVis * 0.92f * MathH.Clamp((moonArc + 0.06f) / 1.06f, 0, 1);
        if (moonA > 0.01f && _moonReady)
        {
            // §5 5.2: cool dim moonlight pool in the light buffer
            AddLight(mx, my, mr * 3.0f, 140, 180, 255, (byte)(moonA * 55));

            // Layered soft glow halo (additive) — big dreamy bloom ring matching HTML
            Raylib.BeginBlendMode(BlendMode.Additive);
            // Far halo: soft wide blue-white
            DrawGradientCircle(mx, my, mr * 3.2f, new Color((byte)150, (byte)190, (byte)255, (byte)(moonA * 55)));
            // Mid halo: brighter cyan
            DrawGradientCircle(mx, my, mr * 2.2f, new Color((byte)180, (byte)215, (byte)255, (byte)(moonA * 95)));
            // Inner halo: near-white close to disc
            DrawGradientCircle(mx, my, mr * 1.45f, new Color((byte)225, (byte)240, (byte)255, (byte)(moonA * 130)));
            Raylib.EndBlendMode();

            // Solid moon backplate (fully opaque — kills any sky/star bleed-through)
            Raylib.DrawCircle((int)mx, (int)my, mr * 0.985f,
                new Color((byte)34, (byte)42, (byte)58, (byte)255));

            // Moon disc: procedural texture with maria, craters, terminator shading
            float discSize = mr * 2f;
            Raylib.DrawTexturePro(_moonTex,
                new Rectangle(0, 0, MoonSize, MoonSize),
                new Rectangle(mx - mr, my - mr, discSize, discSize),
                Vector2.Zero, 0, new Color((byte)255, (byte)255, (byte)255, (byte)255));

            // Stash moon position for DrawStars to mask (HTML: skips stars inside moon disc)
            _moonMaskX = mx;
            _moonMaskY = my;
            _moonMaskR = mr;
            _moonMaskActive = true;
        }
        else
        {
            _moonMaskActive = false;
        }

        // ?? Sun ??
        float sunTrack = (moonTrack + 0.02f) % 1f;
        float sunArc = MathF.Sin(sunTrack * MathF.PI);
        float sxp = MathH.Lerp(-s.W * 0.12f, s.W * 1.12f, sunTrack);
        float syp = MathH.Lerp(s.HorizonY * 0.95f, s.HorizonY * 0.24f, sunArc) + MathF.Cos(sunTrack * TAU) * 5;
        float sr = MathF.Max(26, s.W * 0.024f);
        float sunVisRaw = MathH.Clamp((day - 0.58f) / 0.22f, 0, 1);
        float sunVisS = sunVisRaw * sunVisRaw * (3 - 2 * sunVisRaw);
        float sunA = sunVisS * MathH.Clamp((sunArc + 0.08f) / 1.08f, 0, 1);
        if (sunA > 0.01f)
        {
            // Outer corona
            Raylib.BeginBlendMode(BlendMode.Additive);
            float coronaR = sr * 2.4f;
            for (int gi = 8; gi >= 0; gi--)
            {
                float t = gi / 8f;
                float rr = coronaR * (0.16f + t * 0.84f);
                byte ga = (byte)((0.3f + sunA * 0.42f) * (1 - t) * 180);
                Raylib.DrawCircle((int)sxp, (int)syp, rr, new Color((byte)255, (byte)208, (byte)146, ga));
            }
            Raylib.EndBlendMode();
            // Sun disc
            for (int gi = 5; gi >= 0; gi--)
            {
                float t = gi / 5f;
                float rr = sr * 0.84f * (0.08f + t * 0.92f);
                byte r2 = (byte)MathH.Lerp(255, 255, t);
                byte g2 = (byte)MathH.Lerp(233, 208, t);
                byte b3 = (byte)MathH.Lerp(188, 146, t);
                byte aa = (byte)((0.96f - t * 0.24f) * 255);
                Raylib.DrawCircle((int)sxp, (int)syp, rr, new Color(r2, g2, b3, aa));
            }
        }
    }

    // ? Clouds ? soft ambient wisps, screen-blend like HTML
    static void DrawClouds(GameState s)
    {
        if (s.Clouds.Count == 0) return;
        var (_, day, _, _) = SkyCycle(s.Time);

        Raylib.BeginBlendMode(BlendMode.Additive);
        foreach (var c in s.Clouds)
        {
            float cx = c[0], cy = c[1], cw = c[2], ch = c[3], ca = c[4], sp = c[5], cp = c[6];
            float x = ((cx + s.Time * sp) % (s.W + cw * 1.2f)) - cw * 0.6f;
            float y = cy + MathF.Sin(s.Time * 0.12f + cp) * 16;
            float w = cw * (0.92f + MathF.Sin(s.Time * 0.08f + cp) * 0.08f);
            float h = ch * (0.88f + MathF.Cos(s.Time * 0.13f + cp) * 0.12f);

            var colA = MixRgb(((byte)140, (byte)174, (byte)230), ((byte)220, (byte)234, (byte)248), day);
            var colB = MixRgb(((byte)86, (byte)122, (byte)188), ((byte)174, (byte)196, (byte)222), day);
            float innerA = ca * (0.9f + day * 0.18f);
            float midA = ca * (0.45f + day * 0.2f);

            // Use gradient texture stretched into ellipse shape
            float gradR = w * 0.62f;
            float ew = gradR * 2;
            float eh = gradR * 2 * (h / MathF.Max(1, w));
            // Outer layer
            var dst = new Rectangle(x - ew * 0.5f, y - eh * 0.5f, ew, eh);
            Raylib.DrawTexturePro(_gradientTex,
                new Rectangle(0, 0, GradientSize, GradientSize),
                dst, Vector2.Zero, 0,
                new Color(colB.R, colB.G, colB.B, (byte)(midA * 255)));
            // Inner brighter layer
            float iw = ew * 0.5f, ih = eh * 0.5f;
            var dstI = new Rectangle(x - iw * 0.5f, y - ih * 0.5f, iw, ih);
            Raylib.DrawTexturePro(_gradientTex,
                new Rectangle(0, 0, GradientSize, GradientSize),
                dstI, Vector2.Zero, 0,
                new Color(colA.R, colA.G, colA.B, (byte)(innerA * 255)));
        }
        Raylib.EndBlendMode();
    }

    /// <summary>Large atmospheric bokeh circles � the defining visual of the HTML version.</summary>
    static void DrawBokeh(GameState s)
    {
        var (_, day, _, _) = SkyCycle(s.Time);
        float nightAlpha = MathH.Clamp(1 - day * 1.1f, 0.06f, 1);

        float mx = (s.MouseX - s.W * 0.5f) / s.W;
        float my = (s.MouseY - s.H * 0.5f) / s.H;

        Raylib.BeginBlendMode(BlendMode.Additive);
        for (int i = 0; i < 12; i++)
        {
            int ri = i * 6;
            float bx = _bokehRand[ri] * s.W * 1.1f - s.W * 0.05f;
            float by = _bokehRand[ri + 1] * s.HorizonY * 1.1f;
            float br = (_bokehRand[ri + 2] * 0.12f + 0.08f) * s.W;
            float ba = (_bokehRand[ri + 3] * 0.06f + 0.04f) * nightAlpha;
            float drift = _bokehRand[ri + 4] * 0.08f + 0.02f;
            float phase = _bokehRand[ri + 5] * TAU;

            float x = bx + MathF.Sin(s.Time * drift + phase) * 20 - mx * 30;
            float y = by + MathF.Cos(s.Time * drift * 0.7f + phase * 0.6f) * 14 - my * 20;
            float r = br * (0.9f + MathF.Sin(s.Time * 0.15f + phase) * 0.1f);

            DrawGradientCircle(x, y, r,
                new Color((byte)120, (byte)160, (byte)240, (byte)(ba * 255)));
        }
        Raylib.EndBlendMode();
    }

    /// <summary>9 nebula blobs � large soft radial gradient circles in the sky, parallax mouse offset.</summary>
    static void DrawNebula(GameState s)
    {
        if (s.Nebula.Count == 0) return;
        var (_, day, _, _) = SkyCycle(s.Time);
        float vis = MathH.Clamp(1 - day * 1.18f, 0.04f, 1);
        if (vis < 0.02f) return;

        float mx = (s.MouseX - s.W * 0.5f) / s.W;
        float my = (s.MouseY - s.H * 0.5f) / s.H;

        Raylib.BeginBlendMode(BlendMode.Additive);
        foreach (var n in s.Nebula)
        {
            float nx = n[0], ny = n[1], nr = n[2];
            float h1 = n[3], h2 = n[4], na = n[5], nd = n[6], np = n[7];

            float x = nx + MathF.Sin(s.Time * nd + np) * 22 - mx * 45;
            float y = ny + MathF.Cos(s.Time * nd * 0.8f + np * 0.6f) * 16 - my * 25;
            float r = nr * (0.85f + MathF.Sin(s.Time * 0.22f + np) * 0.08f);

            // Convert HSL hue to approximate RGB for the two gradient stops
            var (r1, g1, b1) = HslToRgb(h1, 0.85f, 0.74f);
            var (r2, g2, b2) = HslToRgb(h2, 0.75f, 0.62f);

            float a = na * vis;
            // Outer layer (hue2, fainter)
            DrawGradientCircle(x, y, r, new Color(r2, g2, b2, (byte)(a * 0.5f * 255)));
            // Inner layer (hue1, brighter)
            DrawGradientCircle(x, y, r * 0.55f, new Color(r1, g1, b1, (byte)(a * 255)));
        }
        Raylib.EndBlendMode();
    }

    /// <summary>3 aurora bands � wavy horizontal gradient ribbons, screen-blend.</summary>
    static void DrawAurora(GameState s)
    {
        if (s.Aurora.Count == 0) return;
        var (_, day, night, _) = SkyCycle(s.Time);
        float vis = MathH.Clamp(0.1f + night * 1.05f, 0.1f, 1);
        if (vis < 0.05f) return;

        Raylib.BeginBlendMode(BlendMode.Additive);
        foreach (var b in s.Aurora)
        {
            float ay = b[0], amp = b[1], th = b[2], sp = b[3], phase = b[4];
            float hue = b[5], aa = b[6];

            var (cr, cg, cb) = HslToRgb(hue, 0.92f, 0.66f);
            float alpha = aa * vis;

            // Draw the band as a series of vertical gradient strips
            int segs = 24;
            for (int i = 0; i < segs; i++)
            {
                float x0 = (i / (float)segs) * s.W;
                float x1 = ((i + 1) / (float)segs) * s.W;
                float y0 = ay + MathF.Sin(i * 0.42f + s.Time * sp + phase) * amp;
                float y1 = ay + MathF.Sin((i + 1) * 0.42f + s.Time * sp + phase) * amp;

                // Gradient band: peak color at center, transparent at top and bottom
                float midY0 = y0, midY1 = y1;
                float topY0 = y0 - th, topY1 = y1 - th;
                float botY0 = y0 + th, botY1 = y1 + th;

                byte a = (byte)(alpha * 255);
                byte aHalf = (byte)(alpha * 0.45f * 255);

                // Draw as two triangles forming a gradient quad (peak brightness at center)
                // Top half (transparent ? colored)
                DrawQuad(
                    new Vector2(x0, topY0), new Vector2(x1, topY1),
                    new Vector2(x1, midY1), new Vector2(x0, midY0),
                    new Color(cr, cg, cb, aHalf));
                // Bottom half (colored ? transparent)
                DrawQuad(
                    new Vector2(x0, midY0), new Vector2(x1, midY1),
                    new Vector2(x1, botY1), new Vector2(x0, botY0),
                    new Color(cr, cg, cb, aHalf));
                // Bright core line
                float lineW = 2f;
                DrawQuad(
                    new Vector2(x0, midY0 - lineW), new Vector2(x1, midY1 - lineW),
                    new Vector2(x1, midY1 + lineW), new Vector2(x0, midY0 + lineW),
                    new Color(cr, cg, cb, a));
            }
        }
        Raylib.EndBlendMode();
    }

    /// <summary>Approximate HSL to RGB conversion (S and L in 0..1, H in degrees).</summary>
    static (byte R, byte G, byte B) HslToRgb(float h, float s, float l)
    {
        h = ((h % 360) + 360) % 360;
        float c = (1 - MathF.Abs(2 * l - 1)) * s;
        float x = c * (1 - MathF.Abs((h / 60f) % 2 - 1));
        float m = l - c * 0.5f;
        float r1, g1, b1;
        if (h < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }
        return ((byte)((r1 + m) * 255), (byte)((g1 + m) * 255), (byte)((b1 + m) * 255));
    }

    static void DrawStars(GameState s)
    {
        var (_, day, _, _) = SkyCycle(s.Time);
        float visA = MathH.Clamp(1 - day * 1.25f, 0.02f, 1);
        if (visA < 0.03f) return;
        var p = Palette.Active(s); // §5 5.4: star core/halo hue per theme

        float mx = (s.MouseX - s.W * 0.5f) / s.W;
        float my = (s.MouseY - s.H * 0.5f) / s.H;

        int count = 950;
        // Slightly expand the mask so glow halo pixels aren't crosshatched either
        float maskR2 = _moonMaskActive ? (_moonMaskR * 1.15f) * (_moonMaskR * 1.15f) : 0f;
        Raylib.BeginBlendMode(BlendMode.Additive);
        for (int i = 0; i < count; i++)
        {
            int ri = i * 3;
            float x = _starRand[ri] * s.W - mx * 10;
            float y = _starRand[ri + 1] * (s.HorizonY + 60) - my * 8;
            if (_moonMaskActive)
            {
                float ddx = x - _moonMaskX, ddy = y - _moonMaskY;
                if (ddx * ddx + ddy * ddy <= maskR2) continue;
            }
            float tw = 0.3f + 0.7f * (0.5f + 0.5f * MathF.Sin(s.Time * (0.8f + i * 0.012f) + i * 0.73f));
            byte a = (byte)(tw * 220 * visA);
            float sz = 0.2f + _starRand[ri + 2] * 1.2f;
            if (sz > 0.9f)
                Raylib.DrawCircle((int)x, (int)y, sz * 2.5f, new Color(p.StarHalo.R, p.StarHalo.G, p.StarHalo.B, (byte)(a / 12)));
            Raylib.DrawCircle((int)x, (int)y, sz, new Color(p.StarCore.R, p.StarCore.G, p.StarCore.B, a));
        }
        Raylib.EndBlendMode();
    }

    // ? MOUNTAINS ? � gradient-filled silhouettes with parallax, matching HTML drawMount()
    static void DrawMountains(GameState s)
    {
        var (_, day, _, twilight) = SkyCycle(s.Time);
        float mx = (s.MouseX - s.W * 0.5f) / s.W;
        var p = Palette.Active(s); // §5 5.4 mountain silhouette stops

        // Far layer (Modern: top night=[30,40,78]→day=[82,106,150], bot night=[10,12,25]→day=[42,54,86])
        var farTop = MixRgb(p.MtnFarTopN, p.MtnFarTopD, day);
        var farBot = MixRgb(p.MtnFarBotN, p.MtnFarBotD, day);
        // Near layer (Modern: top night=[40,42,65]→day=[98,110,142], bot night=[11,10,20]→day=[52,58,86])
        var nearTop = MixRgb(p.MtnNearTopN, p.MtnNearTopD, day);
        var nearBot = MixRgb(p.MtnNearBotN, p.MtnNearBotD, day);

        DrawMountLayerGradient(s, _ridgeFarRand, s.HorizonY + 70, s.H * 0.11f, 16, 0.6f,
            new Color(farTop.R, farTop.G, farTop.B, (byte)(210 + day * 10)),
            new Color(farBot.R, farBot.G, farBot.B, (byte)(242 - day * 16)),
            mx * 20);

        DrawMountLayerGradient(s, _ridgeNearRand, s.HorizonY + 130, s.H * 0.14f, 20, 1f,
            new Color(nearTop.R, nearTop.G, nearTop.B, (byte)(220 + day * 8)),
            new Color(nearBot.R, nearBot.G, nearBot.B, (byte)(250 - day * 20)),
            mx * 45);

        // Ambient light overlay during day/twilight (screen blend approximation).
        // Rendered as a 3-segment vertical gradient so the top edge fades in gradually
        // (instead of a sharp line at HorizonY-60) and the bottom tapers into the ground.
        if (day > 0.03f || twilight > 0.06f)
        {
            var amb = MixRgb(((byte)84, (byte)108, (byte)152), ((byte)234, (byte)204, (byte)162),
                twilight * 0.45f + day * 0.2f);
            float ambA = 0.05f + day * 0.08f + twilight * 0.06f;
            byte peakA = (byte)(ambA * 255);
            var ambRgb = new Color(amb.R, amb.G, amb.B, (byte)0);
            var ambRgbPeak = new Color(amb.R, amb.G, amb.B, peakA);

            // Fade-in band: top at HorizonY-220 (transparent) → peak at HorizonY-20
            int fadeInTop = (int)(s.HorizonY - 220);
            int fadeInH = 200;
            // Solid peak band: HorizonY-20 to GroundY
            int solidTop = (int)(s.HorizonY - 20);
            int solidH = (int)(s.GroundY - solidTop);
            // Fade-out band: GroundY → GroundY+60
            int fadeOutTop = (int)s.GroundY;
            int fadeOutH = 60;

            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawRectangleGradientV(0, fadeInTop, (int)s.W, fadeInH, ambRgb, ambRgbPeak);
            Raylib.DrawRectangle(0, solidTop, (int)s.W, Math.Max(1, solidH), ambRgbPeak);
            Raylib.DrawRectangleGradientV(0, fadeOutTop, (int)s.W, fadeOutH, ambRgbPeak, ambRgb);
            Raylib.EndBlendMode();
        }
    }

    static void DrawMountLayerGradient(GameState s, float[] ridgeRand, float baseY, float amp, int segs, float roughness,
        Color topCol, Color botCol, float parallaxOffset)
    {
        float os = s.W * 0.1f;
        // Generate ridge points into the shared scratch buffer (no per-frame List)
        var pts = _ridgePts;
        int ptCount = segs + 1;
        for (int i = 0; i < ptCount; i++)
        {
            float x = -os + (i / (float)segs * (s.W + os * 2));
            float w = MathF.Sin(i / (float)segs * MathF.PI * (1.5f + roughness * 0.7f));
            float y = baseY - (w * amp + MathH.Lerp(-amp * 0.45f, amp * 0.55f, ridgeRand[i]));
            pts[i] = (x - parallaxOffset, y);
        }

        float gndY = s.GroundY + 4;

        // Draw using Rlgl quads � per-vertex colors give a proper gradient,
        // and quads avoid all triangle winding / backface-cull issues.
        // Rlgl quad vertex order must be: BL ? BR ? TR ? TL
        Rlgl.CheckRenderBatchLimit(ptCount * 4);
        Rlgl.SetTexture(Rlgl.GetTextureIdDefault());
        Rlgl.Begin(DrawMode.Quads);
        for (int i = 1; i < ptCount; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];

            // BL (left ground)
            Rlgl.Color4ub(botCol.R, botCol.G, botCol.B, botCol.A);
            Rlgl.TexCoord2f(0, 1);
            Rlgl.Vertex2f(a.X, gndY);

            // BR (right ground)
            Rlgl.Color4ub(botCol.R, botCol.G, botCol.B, botCol.A);
            Rlgl.TexCoord2f(1, 1);
            Rlgl.Vertex2f(b.X, gndY);

            // TR (right peak)
            Rlgl.Color4ub(topCol.R, topCol.G, topCol.B, topCol.A);
            Rlgl.TexCoord2f(1, 0);
            Rlgl.Vertex2f(b.X, b.Y);

            // TL (left peak)
            Rlgl.Color4ub(topCol.R, topCol.G, topCol.B, topCol.A);
            Rlgl.TexCoord2f(0, 0);
            Rlgl.Vertex2f(a.X, a.Y);
        }
        Rlgl.End();
        Rlgl.SetTexture(0);
    }

    static Color LerpColor(Color a, Color b, float t)
    {
        return new Color(
            (byte)MathH.Lerp(a.R, b.R, t),
            (byte)MathH.Lerp(a.G, b.G, t),
            (byte)MathH.Lerp(a.B, b.B, t),
            (byte)MathH.Lerp(a.A, b.A, t));
    }

    // ?? GROUND ?? day/night palette matching HTML
    static void DrawGround(GameState s)
    {
        var (_, day, _, _) = SkyCycle(s.Time);
        var p = Palette.Active(s); // §5 5.4 ground + grid authority

        // Ground palette (Modern: night=[42,37,68]→[9,10,22], day=[86,84,108]→[28,30,44])
        var gTop = MixRgb(p.GroundTopN, p.GroundTopD, day);
        var gBot = MixRgb(p.GroundBotN, p.GroundBotD, day);

        Raylib.DrawRectangleGradientV(0, (int)s.GroundY - 8, (int)s.W, (int)(s.H - s.GroundY + 8),
            new Color(gTop.R, gTop.G, gTop.B, (byte)255),
            new Color(gBot.R, gBot.G, gBot.B, (byte)255));

        // Retro perspective grid � purple/blue lines receding toward horizon
        var gridCol = MixRgb(p.GridN, p.GridD, day);
        byte gridA = (byte)((0.24f + day * 0.08f) * 255);
        var gc = new Color(gridCol.R, gridCol.G, gridCol.B, gridA);
        // Horizontal lines (closer together near horizon for perspective)
        for (int i = 1; i <= 20; i++)
        {
            float t = i / 20f;
            float y = MathH.Lerp(s.GroundY + 4, s.H, t * t); // perspective spacing
            Raylib.DrawLine(0, (int)y, (int)s.W, (int)y, gc);
        }
        // Vertical lines converging to vanishing point
        for (int i = 0; i <= 32; i++)
        {
            float x = i / 32f * s.W;
            float tx = MathH.Lerp(x, s.W * 0.5f, 0.68f);
            Raylib.DrawLine((int)x, (int)s.H, (int)tx, (int)s.GroundY, gc);
        }
        // Horizon edge line
        Raylib.DrawLine(0, (int)s.GroundY, (int)s.W, (int)s.GroundY,
            new Color(gridCol.R, gridCol.G, gridCol.B, (byte)(gridA + 20)));

        // Haze bands above ground (subtle atmospheric haze)
        var hazeCol = MixRgb(((byte)106, (byte)148, (byte)210), ((byte)154, (byte)172, (byte)194), day);
        for (int i = 0; i < 5; i++)
        {
            int ri = i * 5;
            float hy = MathH.Lerp(s.HorizonY * 0.78f, s.GroundY - 30, _hazeRand[ri]);
            float hth = 44 + _hazeRand[ri + 1] * 48;
            float ha = _hazeRand[ri + 2] * 0.06f + 0.04f;
            float hsp = _hazeRand[ri + 3] * 0.16f + 0.08f;
            float hp = _hazeRand[ri + 4] * TAU;
            float wob = MathF.Sin(s.Time * hsp + hp);
            float cy = hy + wob * 16;
            float bandA = ha * (0.82f + 0.18f * wob) * (1 - day * 0.24f);
            // Gradient: transparent ? hazeCol at bandA ? transparent
            Raylib.DrawRectangleGradientV(0, (int)(cy - hth), (int)s.W, (int)hth,
                new Color((byte)0, (byte)0, (byte)0, (byte)0),
                new Color(hazeCol.R, hazeCol.G, hazeCol.B, (byte)(bandA * 255)));
            Raylib.DrawRectangleGradientV(0, (int)cy, (int)s.W, (int)hth,
                new Color(hazeCol.R, hazeCol.G, hazeCol.B, (byte)(bandA * 255)),
                new Color((byte)0, (byte)0, (byte)0, (byte)0));
        }

        // Pulsing grid dots � very faint
        Raylib.BeginBlendMode(BlendMode.Additive);
        var dotCol = MixRgb(((byte)120, (byte)185, (byte)255), ((byte)176, (byte)200, (byte)220), day);
        byte dotA = (byte)((0.018f + s.Danger * 0.02f + day * 0.012f) * 255);
        for (int i = 0; i < 24; i++)
        {
            float x = i / 23f * s.W;
            float j = MathF.Sin(i * 1.7f + s.Time * 0.8f) * 4;
            Raylib.DrawRectangle((int)(x - 1), (int)(s.GroundY - 5 + j), 2, 3,
                new Color(dotCol.R, dotCol.G, dotCol.B, dotA));
        }
        Raylib.EndBlendMode();
    }

    // ?????????????? SCORCHES ??????????????
    static void DrawScorches(GameState s)
    {
        foreach (var sc in s.Scorches)
        {
            float t = MathH.Clamp(sc.Life * 0.12f, 0, 1);
            byte a = (byte)(t * 90);
            Raylib.DrawCircle((int)sc.X, (int)sc.Y, sc.Radius * 1.2f, new Color((byte)12, (byte)7, (byte)4, a));
        }
    }

    /// <summary>§5 5.3 lingering ground glow: fresh scorches radiate warm light
    /// that cools over ~2.5 s. Caller wraps in the additive light-burst group.</summary>
    static void DrawScorchGlow(GameState s)
    {
        foreach (var sc in s.Scorches)
        {
            if (sc.Heat <= 0.02f) continue;
            float h = sc.Heat * sc.Heat; // ease the cooldown — hot pop, long tail
            DrawGradientCircle(sc.X, sc.Y - 2, sc.Radius * (1.5f + (1 - sc.Heat) * 0.4f),
                new Color((byte)255, (byte)110, (byte)40, (byte)(h * 120)));
            DrawGradientCircle(sc.X, sc.Y - 2, sc.Radius * 0.6f,
                new Color((byte)255, (byte)200, (byte)130, (byte)(h * 150)));
        }
    }

    // ?????????????? LIGHT BURSTS (additive) ??????????????
    static void DrawLightBursts(GameState s)
    {
        foreach (var lb in s.LightBursts)
        {
            float p = 1 - lb.Life / lb.MaxLife;
            float a = (1 - p) * 0.42f;
            if (a <= 0.01f) continue;
            float r = lb.Radius * (1 + p * 0.55f);
            DrawGradientCircle(lb.X, lb.Y, r * 1.3f, new Color((byte)70, (byte)140, (byte)210, (byte)(a * 0.2f * 255)));
            DrawGradientCircle(lb.X, lb.Y, r * 0.7f, new Color((byte)130, (byte)210, (byte)255, (byte)(a * 0.35f * 255)));
            DrawGradientCircle(lb.X, lb.Y, r * 0.3f, new Color((byte)200, (byte)240, (byte)255, (byte)(a * 0.5f * 255)));
        }
    }

    // ?????????????? CITIES ??????????????
    static void DrawCities(GameState s)
    {
        foreach (var c in s.Cities) { if (c.Destroyed) DrawCityRuin(s, c); else DrawCityAlive(s, c); }
    }

    static void DrawCityAlive(GameState s, City city)
    {
        float cx = city.X - city.W * 0.5f;
        float cy = city.Y;
        var pal = Palette.Active(s); // §5 5.4: per-theme window/trim hue set
        // §5 5.2: very dim warm neon pool over each alive skyline
        AddLight(city.X, city.Y - 26f, city.W * 1.05f, 255, 190, 130, 22);
        var rng = new DrawRand(city.Id.GetHashCode());
        int n = 10 + rng.Next(6);
        float slice = city.W / n;

        // Foundation glow band (HTML: gradient strip + cyan highlight line)
        Raylib.DrawRectangleGradientV((int)(cx - 12), (int)(cy - 22), (int)(city.W + 24), 32,
            new Color((byte)10, (byte)18, (byte)34, (byte)240),
            new Color((byte)6, (byte)10, (byte)20, (byte)250));
        Raylib.DrawRectangle((int)(cx - 8), (int)(cy - 9), (int)(city.W + 16), 2,
            new Color((byte)124, (byte)198, (byte)255, (byte)80));

        // First pass: building bodies + roofs (opaque, back-to-front not needed since non-overlapping)
        var buildings = _cityBuildings; // shared scratch, first n slots valid this call
        for (int i = 0; i < n; i++)
        {
            float bw = slice * (0.55f + rng.NextSingle() * 0.9f);
            float bh = 32 + rng.NextSingle() * 74;
            float bx = cx + i * slice + (slice - bw) * 0.5f;
            byte r = (byte)(16 + rng.Next(18));
            byte g = (byte)(22 + rng.Next(22));
            byte b = (byte)(40 + rng.Next(36));
            int rt = rng.Next(4);
            float rh = 5 + rng.NextSingle() * 10;
            float stepW = rng.NextSingle() < 0.35f ? bw * 0.12f : 0f;
            float stepH = stepW > 0 ? 6 + rng.NextSingle() * 10 : 0f;
            float spireH = rng.NextSingle() < 0.28f ? bh * (0.18f + rng.NextSingle() * 0.25f) : 0f;
            float antH = rng.NextSingle() < 0.42f ? 6 + rng.NextSingle() * 14 : 0f;
            int antCol = rng.Next(3);
            int seed = rng.Next();
            buildings[i] = (bx, bw, bh, r, g, b, rt, rh, seed, stepW, stepH, spireH, antH, antCol);

            // Body gradient (metallic cyber-glass: brighter top, darker base)
            Raylib.DrawRectangleGradientV((int)bx, (int)(cy - bh), (int)bw, (int)bh,
                new Color((byte)(r * 2), (byte)(g * 2), (byte)Math.Min(255, b * 3), (byte)235),
                new Color(r, g, b, (byte)248));

            // Right edge highlight (bright 3D)
            Raylib.DrawRectangle((int)(bx + bw - 2), (int)(cy - bh), 2, (int)bh,
                new Color((byte)180, (byte)220, (byte)255, (byte)85));
            // Left edge (dimmer blue)
            Raylib.DrawRectangle((int)bx, (int)(cy - bh), 2, (int)bh,
                new Color((byte)100, (byte)160, (byte)255, (byte)60));

            // Step geometry (setback roof section)
            if (stepW > 0)
            {
                float sw = bw - stepW * 2;
                float sx = bx + stepW;
                Raylib.DrawRectangle((int)sx, (int)(cy - bh - stepH), (int)sw, (int)stepH,
                    new Color((byte)(r * 0.7f), (byte)(g * 0.7f), (byte)(b * 0.8f), (byte)245));
                Raylib.DrawRectangle((int)(sx + sw - 1.5f), (int)(cy - bh - stepH), 2, (int)stepH,
                    new Color((byte)180, (byte)220, (byte)255, (byte)95));
            }

            // Roof shape
            if (rt == 1)
            {
                Raylib.DrawTriangle(
                    new Vector2(bx, cy - bh), new Vector2(bx + bw * 0.5f, cy - bh - rh),
                    new Vector2(bx + bw, cy - bh),
                    new Color((byte)(r + 10), (byte)(g + 10), (byte)(b + 18), (byte)220));
            }
            else if (rt == 2)
            {
                float sh = 6 + rh;
                float sw = bw * 0.5f;
                float sx = bx + (bw - sw) * 0.5f;
                Raylib.DrawRectangle((int)sx, (int)(cy - bh - sh), (int)sw, (int)sh,
                    new Color(r, g, b, (byte)228));
                Raylib.DrawRectangle((int)sx, (int)(cy - bh - sh), (int)sw, 2,
                    new Color((byte)90, (byte)135, (byte)200, (byte)195));
            }
            else
            {
                Raylib.DrawRectangle((int)bx, (int)(cy - bh), (int)bw, 2,
                    new Color((byte)90, (byte)135, (byte)200, (byte)205));
            }

            // Ledge band (cyan separator ~40% down the building)
            float ledgeY = cy - bh * 0.58f;
            Raylib.DrawRectangle((int)bx, (int)ledgeY, (int)bw, 1,
                new Color((byte)100, (byte)160, (byte)255, (byte)85));
        }

        // Second pass: additive-blended windows, trims, warning lights (HTML: globalCompositeOperation='lighter')
        Raylib.BeginBlendMode(BlendMode.Additive);
        for (int bi = 0; bi < n; bi++)
        {
            var bd = buildings[bi];
            var brng = new DrawRand(bd.seed);
            int cols = Math.Max(2, (int)(bd.bw / 7));
            int rows = Math.Max(3, (int)(bd.bh / 10));
            float colStep = (bd.bw - 4) / cols;
            float rowStep = (bd.bh - 6) / rows;
            float ww = MathF.Max(2.6f, colStep * 0.55f);
            float wh = MathF.Max(2.4f, rowStep * 0.48f);

            for (int ri = 0; ri < rows; ri++)
            {
                if (brng.NextSingle() < 0.05f) continue;
                for (int ci = 0; ci < cols; ci++)
                {
                    if (brng.NextSingle() < 0.12f) continue;
                    float wx = bd.bx + 3 + ci * colStep;
                    float wy = cy - bd.bh + 4 + ri * rowStep;
                    float speed = 1.2f + brng.NextSingle() * 4.5f;
                    float phase = brng.NextSingle() * 14f;
                    float baseG = 0.35f + brng.NextSingle() * 0.35f;
                    float fl = 0.3f + baseG * (0.4f + 0.3f * MathF.Sin(s.Time * speed + phase));
                    int ct = brng.Next(8);
                    byte rr, gg, bb;
                    // Modern: ct0 magenta (255,80,200), ct1 yellow (255,220,100), common cyan/white (180,230,255)
                    if (ct == 0) { rr = pal.CityWindow0.R; gg = pal.CityWindow0.G; bb = pal.CityWindow0.B; }      // rare accent A
                    else if (ct == 1) { rr = pal.CityWindow1.R; gg = pal.CityWindow1.G; bb = pal.CityWindow1.B; } // rare accent B
                    else { rr = pal.CityWindowCommon.R; gg = pal.CityWindowCommon.G; bb = pal.CityWindowCommon.B; } // common
                    Color wc = new Color(
                        (byte)MathH.Clamp(rr * fl, 0, 255),
                        (byte)MathH.Clamp(gg * fl, 0, 255),
                        (byte)MathH.Clamp(bb * fl, 0, 255),
                        (byte)240);
                    Raylib.DrawRectangle((int)wx, (int)wy, (int)ww, (int)wh, wc);
                }
            }

            // Neon trim bands (horizontal skybridges at a couple of heights)
            int trimCount = brng.NextSingle() < 0.6f ? 1 : (brng.NextSingle() < 0.4f ? 2 : 0);
            for (int t = 0; t < trimCount; t++)
            {
                float ty = cy - bd.bh + rowStep * (2 + brng.Next(Math.Max(1, rows - 2)));
                int tc = brng.Next(3);
                byte tr = 100, tg = 255, tb = 255;
                if (tc == 1) { tr = 255; tg = 50; tb = 150; }
                else if (tc == 2) { tr = 250; tg = 255; tb = 50; }
                // Band + glow halo
                Raylib.DrawRectangle((int)(bd.bx - 2), (int)ty, (int)(bd.bw + 4), 2,
                    new Color(tr, tg, tb, (byte)220));
                Raylib.DrawRectangle((int)(bd.bx - 4), (int)(ty - 2), (int)(bd.bw + 8), 6,
                    new Color(tr, tg, tb, (byte)60));
            }

            // Spire
            if (bd.spireH > 0)
            {
                float sx = bd.bx + bd.bw * 0.5f;
                Raylib.DrawLineEx(new Vector2(sx, cy - bd.bh), new Vector2(sx, cy - bd.bh - bd.spireH), 1.5f,
                    new Color((byte)160, (byte)210, (byte)255, (byte)180));
            }

            // Antenna + warning light
            if (bd.antH > 0)
            {
                float sx = bd.bx + bd.bw * 0.5f;
                float ty = cy - bd.bh - bd.antH;
                float baseY = cy - bd.bh - (bd.rt == 1 ? bd.rh : 2);
                Raylib.DrawLineEx(new Vector2(sx, baseY), new Vector2(sx, ty), 1.2f,
                    new Color((byte)140, (byte)180, (byte)240, (byte)180));
                float bl = 0.4f + 0.6f * MathF.Max(0f, MathF.Sin(s.Time * 5f + bd.seed * 0.001f));
                byte lr = 255, lg = 50, lb = 50;
                if (bd.antCol == 1) { lr = 255; lg = 255; lb = 100; }
                else if (bd.antCol == 2) { lr = 120; lg = 200; lb = 255; }
                Raylib.DrawCircle((int)sx, (int)ty, 2.2f, new Color(lr, lg, lb, (byte)(bl * 230)));
                Raylib.DrawCircle((int)sx, (int)ty, 5.5f, new Color(lr, lg, lb, (byte)(bl * 80)));
                DrawGradientCircle(sx, ty, 10f, new Color(lr, lg, lb, (byte)(bl * 60)));
            }
        }
        Raylib.EndBlendMode();
    }

    static void DrawCityRuin(GameState s, City c)
    {
        float cx = c.X - c.W * 0.5f;
        float cy = c.Y;
        var rng = new DrawRand(c.Id.GetHashCode() + 999);
        int n = 6 + rng.Next(5);
        float sl = c.W / n;

        // Deep crater base (dark)
        Raylib.DrawRectangle((int)(cx - 10), (int)(cy - 5), (int)(c.W + 20), 7,
            new Color((byte)16, (byte)12, (byte)20, (byte)245));

        // Jagged fragment silhouettes (stable from seed)
        var frags = _ruinFrags; // shared scratch, first n slots valid this call
        for (int i = 0; i < n; i++)
        {
            float fw = sl * (0.45f + rng.NextSingle() * 0.8f);
            float fh = 6 + rng.NextSingle() * 26;
            float fx = cx + i * sl + rng.NextSingle() * 4;
            float tip = 3 + rng.NextSingle() * 11;
            float t = rng.NextSingle();
            frags[i] = (fx, fw, fh, tip, t);
            // Gradient body (dark bottom, hot base)
            int k = (int)(80 + t * 60);
            Raylib.DrawRectangleGradientV((int)fx, (int)(cy - fh), (int)fw, (int)fh,
                new Color((byte)(30 + t * 25), (byte)15, (byte)12, (byte)220),
                new Color((byte)k, (byte)30, (byte)20, (byte)240));
            // Jagged tip
            Raylib.DrawTriangle(
                new Vector2(fx, cy - fh),
                new Vector2(fx + fw * 0.38f, cy - fh - tip),
                new Vector2(fx + fw * 0.72f, cy - fh),
                new Color((byte)(36 + t * 20), (byte)20, (byte)14, (byte)210));
        }

        // Radial heat scar (soft gradient glow around the impact crater)
        float impactX = c.X + rng.NextSingle() * c.W * 0.2f - c.W * 0.1f;
        float scarR = c.W * 0.55f;
        Raylib.BeginBlendMode(BlendMode.Additive);
        DrawGradientCircle(impactX, cy - 2, scarR * 1.5f,
            new Color((byte)120, (byte)30, (byte)14, (byte)85));
        DrawGradientCircle(impactX, cy - 4, scarR * 0.7f,
            new Color((byte)255, (byte)90, (byte)35, (byte)160));

        // Central heat core — pulsing bright
        float gp = rng.NextSingle() * TAU;
        float coreGl = 0.4f + 0.6f * MathF.Max(0f, MathF.Sin(s.Time * 5f + gp));
        DrawGradientCircle(impactX, cy - 6, 24f,
            new Color((byte)255, (byte)120, (byte)40, (byte)(coreGl * 220)));
        DrawGradientCircle(impactX, cy - 6, 12f,
            new Color((byte)255, (byte)220, (byte)180, (byte)(coreGl * 255)));

        // Ember pockets on each fragment (soft glow halos, not solid dots)
        for (int i = 0; i < n; i++)
        {
            var f = frags[i];
            if ((i % 2) == 0) // half fragments have embers
            {
                float ex = f.fx + f.fw * 0.5f;
                float ey = cy - f.fh * 0.7f;
                float bl = 0.4f + 0.6f * MathF.Max(0f, MathF.Sin(s.Time * 8f + i * 1.3f));
                DrawGradientCircle(ex, ey, 10f,
                    new Color((byte)255, (byte)80, (byte)30, (byte)(bl * 180)));
                DrawGradientCircle(ex, ey, 4f,
                    new Color((byte)255, (byte)200, (byte)100, (byte)(bl * 220)));
            }
        }

        // Scattered ember sparks (small stars rising from ruin)
        var erng = new DrawRand(c.Id.GetHashCode() + 1777);
        int embCount = 14;
        for (int i = 0; i < embCount; i++)
        {
            float ex = c.X + (erng.NextSingle() - 0.5f) * c.W * 0.85f;
            float ey = cy - 2 - erng.NextSingle() * 30;
            float sp = 0.9f + erng.NextSingle() * 2.5f;
            float ph = erng.NextSingle() * TAU;
            float rad = 1.5f + erng.NextSingle() * 2.5f;
            float bl = 0.3f + 0.7f * MathF.Sin(s.Time * sp + ph);
            if (bl < 0.05f) continue;
            DrawGradientCircle(ex, ey, rad * 2.2f,
                new Color((byte)255, (byte)140, (byte)50, (byte)(bl * 140)));
            Raylib.DrawCircle((int)ex, (int)ey, rad * 0.55f,
                new Color((byte)255, (byte)220, (byte)160, (byte)(bl * 220)));
        }

        // Thin smoke drift above ruin (vertical gradient columns)
        for (int i = 0; i < 3; i++)
        {
            float sxp = c.X + (i - 1) * c.W * 0.22f + MathF.Sin(s.Time * 0.6f + i * 1.4f) * 4f;
            float syp = cy - 28 - MathF.Sin(s.Time * 0.4f + i * 2.1f) * 6f;
            DrawGradientCircle(sxp, syp, 22f, new Color((byte)40, (byte)22, (byte)18, (byte)70));
            DrawGradientCircle(sxp, syp - 10, 16f, new Color((byte)30, (byte)18, (byte)14, (byte)55));
        }
        Raylib.EndBlendMode();
    }

    // ?????????????? BASES � Faithful port of drawBases() ??????????????
    static void DrawBases(GameState s)
    {
        foreach (var b in s.Bases)
        {
            if (b.Destroyed)
            {
                // Crater
                Raylib.DrawEllipse((int)b.X, (int)b.Y, 48, 16, new Color(16, 12, 20, 245));
                Raylib.DrawEllipse((int)b.X, (int)b.Y, 36, 12, new Color(40, 18, 12, 80));
                Raylib.DrawRectangle((int)(b.X - 22), (int)(b.Y - 18), 14, 18, new Color(60, 30, 25, 100));
                Raylib.DrawRectangle((int)(b.X + 8), (int)(b.Y - 12), 12, 12, new Color(60, 30, 25, 100));
                DrawTextM("OFFLINE", b.X - MeasureTextM("OFFLINE", 16) * 0.5f, b.Y - 56, 16, new Color(255, 100, 50, 200));
                continue;
            }

            // Recoil spring: whole structure dips ~3 px on fire
            bool recoiled = b.Recoil > 0.05f || b.Recoil < -0.05f;
            if (recoiled)
            {
                Rlgl.PushMatrix();
                Rlgl.Translatef(0, b.Recoil, 0);
            }

            float ar = b.Ammo / MathF.Max(1, 16 + MathF.Floor(s.Level * 2.2f));
            float pulse = 0.3f + 0.7f * MathF.Max(0, MathF.Sin(s.Time * 5.2f + b.X * 0.017f));

            // Foundation ellipse with glowing edge
            Raylib.DrawEllipse((int)b.X, (int)(b.Y + 4), 52, 15, new Color(15, 20, 35, 250));
            Raylib.DrawEllipse((int)b.X, (int)b.Y, 48, 13, new Color(28, 36, 55, 242));
            // Platform glow ring
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawEllipseLines((int)b.X, (int)b.Y, 49, 14, new Color((byte)60, (byte)160, (byte)220, (byte)(50 + pulse * 40)));
            Raylib.DrawEllipse((int)b.X, (int)(b.Y + 2), 54, 8, new Color((byte)40, (byte)120, (byte)200, (byte)(18 + pulse * 16)));
            Raylib.EndBlendMode();

            // Armored plating
            Raylib.DrawEllipse((int)b.X, (int)(b.Y - 3), 42, 11, new Color(65, 78, 100, 242));

            // Warning chevrons (simplified)
            for (int i = -4; i < 5; i++)
            {
                float cx = b.X + i * 8;
                Raylib.DrawTriangle(
                    new Vector2(cx - 3, b.Y - 10), new Vector2(cx + 3, b.Y - 10), new Vector2(cx, b.Y + 4),
                    new Color(160, 130, 35, 90));
            }

            // Central ammo silo shaft
            Raylib.DrawRectangleGradientV((int)(b.X - 18), (int)(b.Y - 48), 36, 48,
                new Color(40, 50, 80, 242), new Color(10, 15, 25, 250));
            // Inner wall lighting
            Raylib.DrawRectangle((int)(b.X - 18), (int)(b.Y - 48), 4, 48, new Color(80, 120, 200, 50));
            Raylib.DrawRectangle((int)(b.X + 14), (int)(b.Y - 48), 4, 48, new Color(80, 120, 200, 50));

            // Blast doors
            Raylib.DrawRectangle((int)(b.X - 36), (int)(b.Y - 5), 72, 6, new Color(60, 70, 90, 250));
            Raylib.DrawRectangle((int)(b.X - 34), (int)(b.Y - 4), 68, 2, new Color(120, 140, 170, 150));

            // GLOWING AMMO CELLS � the signature visual
            int maxCells = 8;
            int activeCells = (int)MathF.Ceiling(ar * maxCells);
            Raylib.BeginBlendMode(BlendMode.Additive);
            for (int i = 0; i < maxCells; i++)
            {
                float cy = b.Y - 8 - i * (38f / maxCells);
                if (i < activeCells)
                {
                    byte ca = (byte)(140 + pulse * 110);
                    Raylib.DrawRectangle((int)(b.X - 12), (int)cy, 24, 3, new Color((byte)180, (byte)240, (byte)255, ca));
                    Raylib.DrawRectangle((int)(b.X - 10), (int)(cy + 0.5f), 20, 2, new Color((byte)220, (byte)255, (byte)255, (byte)(ca * 0.7f)));
                    if (b.ResupplyFlash > 0)
                        Raylib.DrawRectangle((int)(b.X - 12), (int)cy, 24, 3,
                            new Color((byte)255, (byte)255, (byte)255, (byte)(b.ResupplyFlash * 150)));
                }
                else
                {
                    Raylib.EndBlendMode();
                    Raylib.DrawRectangle((int)(b.X - 12), (int)cy, 24, 3, new Color(40, 50, 70, 200));
                    Raylib.BeginBlendMode(BlendMode.Additive);
                }
            }
            Raylib.EndBlendMode();

            // Frame / top cap
            Raylib.DrawTriangle(
                new Vector2(b.X - 24, b.Y - 48), new Vector2(b.X + 24, b.Y - 48),
                new Vector2(b.X + 16, b.Y - 65), new Color(50, 60, 80, 245));
            Raylib.DrawTriangle(
                new Vector2(b.X - 24, b.Y - 48), new Vector2(b.X + 16, b.Y - 65),
                new Vector2(b.X - 16, b.Y - 65), new Color(50, 60, 80, 245));

            // Heat vents (glow red as ammo drops)
            Raylib.DrawRectangle((int)(b.X - 12), (int)(b.Y - 60), 24, 8, new Color(20, 25, 35, 250));
            for (int i = -10; i < 10; i += 4)
            {
                byte ventR = (byte)(50 + (1 - ar) * 200);
                Raylib.DrawRectangle((int)(b.X + i), (int)(b.Y - 59), 2, 6, new Color(ventR, (byte)40, (byte)30, (byte)(50 + (1 - ar) * 150)));
            }

            // Radar dish (3D rotating)
            DrawRadarDish(s, b.X + 30, b.Y - 16, 14, 22, s.Time * 2.5f + b.X);

            // 2-frame additive muzzle flash at the silo mouth
            if (b.MuzzleT > 0)
            {
                float mt = b.MuzzleT / Entities.Base.MuzzleFlashDur;
                Raylib.BeginBlendMode(BlendMode.Additive);
                Raylib.DrawCircle((int)b.X, (int)(b.Y - 58), 17 + (1 - mt) * 7,
                    new Color((byte)190, (byte)238, (byte)255, (byte)(mt * 200)));
                Raylib.DrawCircle((int)b.X, (int)(b.Y - 58), 8,
                    new Color((byte)255, (byte)255, (byte)255, (byte)(mt * 235)));
                Raylib.EndBlendMode();
            }

            // Floating ammo counter � large retro display
            string ammoStr = b.Ammo.ToString();
            int ammoFontSz = 26;
            int ammoW = MeasureTextM(ammoStr, ammoFontSz);
            int ammoX = (int)(b.X - ammoW / 2);
            int ammoY = (int)(b.Y - 100);
            // Backing panel
            Raylib.DrawRectangle(ammoX - 6, ammoY - 4, ammoW + 12, ammoFontSz + 6,
                new Color((byte)4, (byte)10, (byte)22, (byte)180));
            Raylib.DrawRectangleLines(ammoX - 6, ammoY - 4, ammoW + 12, ammoFontSz + 6,
                new Color((byte)80, (byte)200, (byte)255, (byte)60));
            // Glow line below number
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawRectangle(ammoX - 1, ammoY + ammoFontSz, ammoW + 2, 2,
                new Color((byte)100, (byte)220, (byte)255, (byte)(70 + pulse * 130)));
            Raylib.EndBlendMode();
            // Number
            Color ammoCol = ar > 0.25f ? new Color((byte)210, (byte)250, (byte)255, (byte)252)
                : new Color((byte)255, (byte)100, (byte)60, (byte)252);
            DrawTextM(ammoStr, ammoX, ammoY, ammoFontSz, ammoCol);

            if (recoiled) Rlgl.PopMatrix();
        }
    }

    /// <summary>Draws a 3D-perspective rotating radar dish.</summary>
    static void DrawRadarDish(GameState s, float x, float y, float dishR, float mastH, float rot)
    {
        float rRot = rot % TAU;
        float rc = MathF.Cos(rRot), rs = MathF.Sin(rRot);
        float effRim = MathF.Max(1.8f, MathF.Abs(rc) * dishR);
        bool front = rc > 0;

        // Mast
        Raylib.DrawRectangle((int)(x - 2), (int)(y - mastH), 4, (int)mastH, new Color(60, 72, 95, 250));
        Raylib.DrawRectangle((int)(x - 2), (int)(y - mastH + 4), 4, 1, new Color(100, 120, 155, 110));
        Raylib.DrawRectangle((int)(x - 2), (int)(y - mastH + 10), 4, 1, new Color(100, 120, 155, 110));

        // Pivot hub
        Raylib.DrawEllipse((int)x, (int)(y - mastH), 4, 3, new Color(90, 100, 125, 242));

        float dY = y - mastH - 1;

        // Dish body: simplified 3D projection
        byte bodyA = front ? (byte)225 : (byte)240;
        Color bodyCol = front ? new Color((byte)45, (byte)55, (byte)75, bodyA) : new Color((byte)30, (byte)35, (byte)50, bodyA);
        Raylib.DrawEllipse((int)x, (int)dY, (int)effRim, (int)dishR, bodyCol);

        // Rim face
        Color rimCol = front ? new Color(95, 115, 150, 210) : new Color(50, 55, 68, 215);
        Raylib.DrawEllipseLines((int)x, (int)dY, (int)effRim, (int)dishR, rimCol);

        // Front: concave gradient
        if (front && rc > 0.15f)
        {
            byte fa = (byte)(rc * 50);
            Raylib.DrawEllipse((int)x, (int)dY, (int)(effRim * 0.7f), (int)(dishR * 0.7f),
                new Color((byte)140, (byte)180, (byte)225, fa));
            // Rings
            Raylib.DrawEllipseLines((int)x, (int)dY, (int)(effRim * 0.5f), (int)(dishR * 0.5f),
                new Color((byte)140, (byte)170, (byte)210, (byte)(rc * 30)));
        }

        // Back: X-brace
        if (!front && MathF.Abs(rc) > 0.15f)
        {
            byte ba = (byte)(MathF.Abs(rc) * 100);
            Raylib.DrawLineEx(new Vector2(x - effRim * 0.45f, dY - dishR * 0.45f),
                new Vector2(x + effRim * 0.45f, dY + dishR * 0.45f), 1.5f, new Color((byte)80, (byte)95, (byte)120, ba));
            Raylib.DrawLineEx(new Vector2(x - effRim * 0.45f, dY + dishR * 0.45f),
                new Vector2(x + effRim * 0.45f, dY - dishR * 0.45f), 1.5f, new Color((byte)80, (byte)95, (byte)120, ba));
        }

        // Feed horn
        float hornVis = MathH.Clamp((rc + 0.15f) / 0.5f, 0, 1);
        if (hornVis > 0.01f)
        {
            float hX = x + rs * (dishR * 0.8f);
            float armExt = MathF.Abs(rs);
            byte ha = (byte)(hornVis * (130 + armExt * 60));
            Raylib.DrawLineEx(new Vector2(x, dY - dishR * 0.5f), new Vector2(hX, dY), 1, new Color((byte)120, (byte)138, (byte)165, ha));
            Raylib.DrawLineEx(new Vector2(x, dY + dishR * 0.5f), new Vector2(hX, dY), 1, new Color((byte)120, (byte)138, (byte)165, ha));
            Raylib.DrawCircle((int)hX, (int)dY, 1.5f + armExt * 1.5f, new Color((byte)100, (byte)130, (byte)160, ha));
            // Signal pulse
            float rp = 0.4f + MathF.Sin(s.Time * 8 + x * 0.1f) * 0.4f;
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawCircle((int)hX, (int)dY, 2 + armExt * 2, new Color((byte)100, (byte)200, (byte)255, (byte)(rp * hornVis * 80)));
            Raylib.EndBlendMode();
        }

        // Tracking indicator light
        float indY = dY + dishR * MathF.Sin(MathF.PI * 0.3f);
        float indX = x + MathF.Cos(MathF.PI * 0.3f) * effRim;
        if (MathF.Sin(MathF.PI * 0.3f) * rs < 0)
        {
            float ip = 0.6f + 0.4f * MathF.Sin(s.Time * 6);
            Raylib.DrawCircle((int)indX, (int)indY, 1.5f, new Color((byte)255, (byte)60, (byte)60, (byte)(ip * 210)));
        }
    }

    // ?????????????? HELLRAISER ??????????????
    static void DrawHellRaiser(GameState s)
    {
        var hr = s.HellRaiser; if (hr == null) return;

        // Foundation
        Raylib.DrawEllipse((int)hr.X, (int)(hr.Y + 2), 52, 16, new Color(22, 24, 38, 244));
        // Tracks
        Raylib.DrawRectangle((int)(hr.X - 45), (int)(hr.Y - 7), 90, 7, new Color(80, 86, 112, 106));
        Raylib.DrawRectangle((int)(hr.X - 36), (int)(hr.Y - 7), 72, 2, new Color(124, 136, 178, 55));
        // Inner housing
        Raylib.DrawRectangle((int)(hr.X - 34), (int)(hr.Y - 6), 68, 6, new Color(42, 44, 62, 244));

        // Doors
        float slide = hr.DoorOpen * 20;
        Color doorC = hr.Destroyed ? new Color(92, 58, 56, 200) : new Color(106, 118, 148, 245);
        Raylib.DrawRectangle((int)(hr.X - 34 - slide), (int)(hr.Y - 5), 30, 5, doorC);
        Raylib.DrawRectangle((int)(hr.X + 4 + slide), (int)(hr.Y - 5), 30, 5, doorC);

        if (hr.Destroyed)
        {
            Raylib.DrawRectangle((int)(hr.X - 16), (int)(hr.Y - 3), 32, 3, new Color(32, 22, 16, 175));
            return;
        }

        // Shaft glow
        if (hr.DoorOpen > 0.04f || hr.Lift > 0.04f)
        {
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawRectangle((int)(hr.X - 9), (int)(hr.Y - 5), 18, 5,
                new Color((byte)255, (byte)132, (byte)96, (byte)(20 + hr.Lift * 50)));
            Raylib.EndBlendMode();
            Raylib.DrawRectangle((int)(hr.X - 9), (int)(hr.Y - 78), 17, 73, new Color(34, 28, 46, 250));
        }

        if (hr.Lift > 0.01f)
        {
            float topY = hr.Y - 7 - hr.Lift * 72;
            // Body
            Raylib.DrawRectangle((int)(hr.X - 10), (int)(topY - 24), 20, 24, new Color(118, 124, 146, 247));
            // Cap
            Raylib.DrawRectangle((int)(hr.X - 12), (int)(topY - 24), 24, 5, new Color(164, 170, 198, 242));

            // Missile rack dots
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    float mx = hr.X - 6 + col * 6;
                    float my = topY - 20 + row * 5;
                    float p = 0.38f + 0.62f * MathF.Max(0, MathF.Sin(s.Time * 9 + row * 0.9f + col * 1.1f + hr.X * 0.01f));
                    Raylib.DrawCircle((int)mx, (int)my, 1.5f, new Color((byte)255, (byte)196, (byte)136, (byte)(75 + p * 100)));
                }
            }

            // Turret head
            Raylib.DrawRectangle((int)(hr.X - 6), (int)(topY - 40), 12, 16, new Color(88, 96, 126, 250));

            // HP bar
            float hpR = hr.MaxAmmo > 0 ? (float)hr.Ammo / hr.MaxAmmo : 0;
            Raylib.DrawRectangle((int)(hr.X - 6), (int)(topY - 42), 11, 2, new Color(40, 30, 25, 200));
            Raylib.DrawRectangle((int)(hr.X - 6), (int)(topY - 42), (int)(11 * hpR), 2, new Color(255, 96, 72, 242));

            if (hr.State == "active")
            {
                Raylib.BeginBlendMode(BlendMode.Additive);
                float p = 0.45f + MathF.Sin(s.Time * 14) * 0.16f;
                Raylib.DrawCircle((int)hr.X, (int)(topY - 44), 3, new Color((byte)255, (byte)146, (byte)108, (byte)(p * 200)));
                Raylib.DrawEllipse((int)hr.X, (int)(hr.Y - 38), 40, 14,
                    new Color((byte)255, (byte)116, (byte)86, (byte)(30 + (1 - hpR) * 50)));
                Raylib.EndBlendMode();
            }

            // Ammo count
            DrawTextM(MathF.Max(0, hr.Ammo).ToString(), hr.X - 6, topY - 98, 12,
                new Color(255, 236, 176, 242));
        }

        if (hr.State == "cooldown")
        {
            float p = 0.3f + 0.7f * MathF.Sin(s.Time * 2);
            Raylib.DrawCircle((int)hr.X, (int)(hr.Y - 2), 2.5f, new Color((byte)255, (byte)150, (byte)50, (byte)(p * 140)));
        }
    }

    // ?????????????? PHALANX � Faithful port with rotating gatling ??????????????
    static void DrawPhalanxes(GameState s)
    {
        foreach (var p in s.Phalanxes)
        {
            if (p.Destroyed)
            {
                Raylib.DrawEllipse((int)p.X, (int)p.Y, 34, 12, new Color(20, 15, 18, 242));
                Raylib.DrawRectangle((int)(p.X - 18), (int)(p.Y - 20), 36, 20, new Color(40, 25, 20, 200));
                Raylib.DrawLineEx(new Vector2(p.X - 16, p.Y - 6), new Vector2(p.X + 16, p.Y - 16), 2.5f,
                    new Color(255, 80, 40, 150));
                DrawTextM("OFFLINE", p.X - MeasureTextM("OFFLINE", 14) * 0.5f, p.Y - 40, 14, new Color(255, 80, 40, 180));
                continue;
            }

            float ang = float.IsFinite(p.AimAng) ? p.AimAng : -MathF.PI * 0.5f;
            float heat = MathF.Max(p.Heat, p.FireMix);
            float spin = p.SpinAngle;
            bool locked = p.Target != null;

            // Ground shadow / firing glow
            if (heat > 0.1f)
            {
                Raylib.BeginBlendMode(BlendMode.Additive);
                Raylib.DrawEllipse((int)p.X, (int)(p.Y + 5), 50, 16,
                    new Color((byte)130, (byte)200, (byte)255, (byte)(heat * 100)));
                Raylib.EndBlendMode();
            }
            else
            {
                Raylib.DrawEllipse((int)p.X, (int)(p.Y + 5), 45, 14, new Color(0, 0, 0, 75));
            }

            // Armored base deck
            Raylib.DrawEllipse((int)p.X, (int)(p.Y - 2), 40, 14, new Color(60, 72, 90, 250));
            Raylib.DrawEllipseLines((int)p.X, (int)(p.Y - 2), 34, 11, new Color(160, 190, 225, 120));

            // Gear teeth
            for (int i = 0; i < 28; i++)
            {
                float t = i / 28f * TAU;
                float x1 = p.X + MathF.Cos(t) * 28, y1 = p.Y - 2 + MathF.Sin(t) * 9;
                float x2 = p.X + MathF.Cos(t) * 33, y2 = p.Y - 2 + MathF.Sin(t) * 11;
                Raylib.DrawLineEx(new Vector2(x1, y1), new Vector2(x2, y2), 1, new Color(40, 50, 65, 200));
            }

            // Pedestal/strut
            Raylib.DrawTriangle(
                new Vector2(p.X - 18, p.Y - 8), new Vector2(p.X + 18, p.Y - 8),
                new Vector2(p.X + 12, p.Y - 65), new Color(55, 68, 88, 250));
            Raylib.DrawTriangle(
                new Vector2(p.X - 18, p.Y - 8), new Vector2(p.X + 12, p.Y - 65),
                new Vector2(p.X - 12, p.Y - 65), new Color(55, 68, 88, 250));

            // Hydraulic lines (glow when firing)
            byte hAlpha = (byte)(70 + heat * 120);
            Raylib.DrawRectangle((int)(p.X - 8), (int)(p.Y - 60), 4, 48, new Color((byte)160, (byte)210, (byte)240, hAlpha));
            Raylib.DrawRectangle((int)(p.X + 4), (int)(p.Y - 60), 4, 48, new Color((byte)160, (byte)210, (byte)240, hAlpha));

            // Turret body
            Raylib.DrawTriangle(
                new Vector2(p.X - 22, p.Y - 65), new Vector2(p.X + 22, p.Y - 65),
                new Vector2(p.X + 16, p.Y - 92), new Color(42, 52, 68, 245));
            Raylib.DrawTriangle(
                new Vector2(p.X - 22, p.Y - 65), new Vector2(p.X + 16, p.Y - 92),
                new Vector2(p.X - 16, p.Y - 92), new Color(42, 52, 68, 245));
            // Metallic sheen
            Raylib.DrawTriangle(
                new Vector2(p.X - 18, p.Y - 65), new Vector2(p.X - 8, p.Y - 65),
                new Vector2(p.X - 6, p.Y - 90), new Color(130, 155, 180, 72));
            Raylib.DrawTriangle(
                new Vector2(p.X - 18, p.Y - 65), new Vector2(p.X - 6, p.Y - 90),
                new Vector2(p.X - 14, p.Y - 90), new Color(130, 155, 180, 72));

            // Heat sync fins
            for (int i = 0; i < 5; i++)
            {
                float hy = p.Y - 88 + i * 5;
                Raylib.DrawRectangle((int)(p.X - 14), (int)hy, 28, 3, new Color(65, 80, 98, 230));
                if (heat > 0.05f)
                {
                    Raylib.BeginBlendMode(BlendMode.Additive);
                    Raylib.DrawRectangle((int)(p.X - 12), (int)(hy + 1), 24, 2,
                        new Color((byte)160, (byte)220, (byte)255, (byte)(heat * 180)));
                    Raylib.EndBlendMode();
                }
            }

            // Rotating gatling gun assembly (aims at target)
            float pivotX = p.X, pivotY = p.Y - 78;
            float cosA = MathF.Cos(ang), sinA = MathF.Sin(ang);

            // Cradle
            DrawRotatedRect(pivotX, pivotY, ang, -18, -14, 38, 28, new Color(55, 68, 88, 250));
            // Inner recess
            DrawRotatedRect(pivotX, pivotY, ang, -10, -12, 26, 24, new Color(28, 38, 48, 230));
            // Barrel port
            float portX = pivotX + cosA * 4, portY = pivotY + sinA * 4;
            Raylib.DrawCircle((int)portX, (int)portY, 11, new Color(20, 25, 35, 242));

            // Individual barrels � depth-sorted, alternating for visibility
            int numBarrels = 6;
            float barrelLen = 34, assemblyR = 10;
            float recoil = heat > 0.05f ? MathH.Rand(0, 3) * heat : 0;

            var barrels = new (int idx, float depth, float bx, float by)[numBarrels];
            for (int i = 0; i < numBarrels; i++)
            {
                float ba = spin + i * TAU / numBarrels;
                float depth = MathF.Cos(ba);
                float perpOff = MathF.Sin(ba) * assemblyR;
                // Barrel in local space, then rotate by aim angle
                float lx = 14 - recoil; // along barrel axis
                float ly = perpOff; // perpendicular to barrel axis
                float wx = pivotX + cosA * lx - sinA * ly;
                float wy = pivotY + sinA * lx + cosA * ly;
                barrels[i] = (i, depth, wx, wy);
            }
            Array.Sort(barrels, (a, b) => a.depth.CompareTo(b.depth));

            foreach (var (idx, depth, bsx, bsy) in barrels)
            {
                float bw = 1.2f + (depth + 1) * 1.0f;
                bool dark = idx % 2 == 0;
                int baseBright = dark ? 45 : 75;
                int brightness = baseBright + (int)((depth + 1) * 35);
                byte br = (byte)brightness, bg = (byte)(brightness + 8), bb = (byte)(brightness + 18);

                float ex = bsx + cosA * (barrelLen - 6);
                float ey = bsy + sinA * (barrelLen - 6);
                Raylib.DrawLineEx(new Vector2(bsx, bsy), new Vector2(ex, ey), bw,
                    new Color(br, bg, bb, (byte)242));

                // Barrel band at midpoint
                if (depth > -0.3f)
                {
                    float mx = bsx + cosA * (barrelLen * 0.48f);
                    float my = bsy + sinA * (barrelLen * 0.48f);
                    Raylib.DrawCircle((int)mx, (int)my, bw * 0.8f,
                        dark ? new Color(100, 115, 140, 200) : new Color(60, 70, 90, 175));
                }
            }

            // Muzzle flange � thin metal ring perpendicular to barrel axis
            float mzX = pivotX + cosA * (14 + barrelLen + 2 - recoil);
            float mzY = pivotY + sinA * (14 + barrelLen + 2 - recoil);
            float flangeW = 3f; // thin along barrel axis
            float flangeH = (assemblyR + 3) * 2; // spans across barrel bundle
            Raylib.DrawRectanglePro(
                new Rectangle(mzX, mzY, flangeW, flangeH),
                new Vector2(flangeW * 0.5f, flangeH * 0.5f), ang * 57.2958f,
                new Color(65, 78, 95, 248));
            // Rim highlight
            Raylib.DrawRectanglePro(
                new Rectangle(mzX - sinA * 0.5f, mzY + cosA * 0.5f, flangeW, flangeH + 2),
                new Vector2(flangeW * 0.5f, (flangeH + 2) * 0.5f), ang * 57.2958f,
                new Color(130, 145, 170, 100));

            // Hub disc at breech
            float hubX = pivotX + cosA * (14 - recoil);
            float hubY = pivotY + sinA * (14 - recoil);
            Raylib.DrawCircle((int)hubX, (int)hubY, 7, new Color(55, 65, 80, 247));
            // Hub spokes
            for (int i = 0; i < numBarrels; i++)
            {
                float ba = spin + i * TAU / numBarrels;
                float spokeY = MathF.Sin(ba) * 6;
                float sx = hubX - sinA * spokeY;
                float sy = hubY + cosA * spokeY;
                byte bright = (byte)(i % 2 == 0 ? 200 : 80);
                Raylib.DrawLineEx(new Vector2(hubX, hubY), new Vector2(sx, sy), i % 2 == 0 ? 1.5f : 0.7f,
                    new Color(bright, (byte)(bright + 10), (byte)(bright + 20), (byte)210));
            }

            // Muzzle flash
            if (heat > 0.05f)
            {
                Raylib.BeginBlendMode(BlendMode.Additive);
                float flashSz = 5 + heat * 10;
                Raylib.DrawCircle((int)mzX, (int)mzY, flashSz, new Color((byte)175, (byte)225, (byte)255, (byte)(heat * 150)));
                Raylib.DrawCircle((int)mzX, (int)mzY, flashSz * 0.45f, new Color((byte)215, (byte)248, (byte)255, (byte)(heat * 100)));
                // Heat glow on barrels
                if (heat > 0.15f)
                    Raylib.DrawRectanglePro(
                        new Rectangle(pivotX + cosA * 20, pivotY + sinA * 20, barrelLen, assemblyR * 2 + 2),
                        new Vector2(0, assemblyR + 1), ang * 57.2958f,
                        new Color((byte)130, (byte)190, (byte)255, (byte)(heat * 35)));
                Raylib.EndBlendMode();
            }

            // Targeting optics
            float optX = pivotX + cosA * 20 + sinA * 18;
            float optY = pivotY + sinA * 20 - cosA * 18;
            Raylib.DrawRectanglePro(new Rectangle(optX, optY, 12, 6), new Vector2(6, 3), ang * 57.2958f,
                new Color(20, 30, 40, 230));
            float lensP = 0.38f + 0.62f * MathF.Max(0, MathF.Sin(s.Time * 8.5f + p.X * 0.01f));
            Color lensC = locked ? new Color((byte)255, (byte)80, (byte)60, (byte)(lensP * 200))
                                 : new Color((byte)80, (byte)220, (byte)255, (byte)(lensP * 200));
            Raylib.DrawCircle((int)optX, (int)optY, 3, lensC);
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawCircle((int)optX, (int)optY, 6, new Color(lensC.R, lensC.G, lensC.B, (byte)(lensP * 40)));
            Raylib.EndBlendMode();

            // Ammo counter
            DrawTextM((p.Ammo).ToString(), p.X - 10, p.Y - 64, 12,
                new Color(255, 236, 176, 242));
        }
    }

    static void DrawRotatedRect(float cx, float cy, float ang, float ox, float oy, float w, float h, Color c)
    {
        Raylib.DrawRectanglePro(
            new Rectangle(cx + MathF.Cos(ang) * ox - MathF.Sin(ang) * oy,
                           cy + MathF.Sin(ang) * ox + MathF.Cos(ang) * oy, w, h),
            new Vector2(0, 0), ang * 57.2958f, c);
    }

    // --- UFOS --- Faithful port of drawUfo()
    static void DrawUFOs(GameState s)
    {
        foreach (var u in s.UFOs)
        {
            float sc = u.Boss ? 1.4f : 1f;
            float maxHp = u.Boss ? 6f : 2f;
            float hpPct = MathH.Clamp(u.Hp / maxHp, 0, 1);
            float dmg = 1 - hpPct;
            float wob = MathF.Sin(s.Time * 3.2f + u.BobPhase);
            float glow = 0.42f + 0.58f * MathF.Max(0, wob);

            // Drop shadow
            Raylib.DrawEllipse((int)u.X, (int)(u.Y + 10 * sc), (int)(42 * sc), (int)(12 * sc),
                new Color((byte)0, (byte)0, (byte)0, (byte)(65 + dmg * 50)));

            // Shield for boss
            if (u.Hp > 2 && u.Boss)
            {
                float sp = 0.86f + MathF.Sin(s.Time * 10 + u.Id) * 0.12f;
                Raylib.BeginBlendMode(BlendMode.Additive);
                Raylib.DrawEllipse((int)u.X, (int)(u.Y - sc), (int)(47 * sp * sc), (int)(29 * sp * sc),
                    new Color((byte)72, (byte)124, (byte)205, (byte)((14 + hpPct * 12) * sp)));
                Raylib.DrawEllipseLines((int)u.X, (int)(u.Y - sc), (int)(47 * sp * sc), (int)(29 * sp * sc),
                    new Color((byte)110, (byte)188, (byte)255, (byte)((65 + hpPct * 60) * sp)));
                Raylib.EndBlendMode();
            }

            // Engine halo glow underneath
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawEllipse((int)u.X, (int)(u.Y + 5 * sc), (int)(40 * sc), (int)(14 * sc),
                new Color((byte)108, (byte)236, (byte)222, (byte)((45 + glow * 45) * 0.5f)));
            Raylib.EndBlendMode();

            // Hull (top lighter, bottom darker)
            Raylib.DrawEllipse((int)u.X, (int)u.Y, (int)(28 * sc), (int)(10.6f * sc), new Color(120, 140, 155, 250));
            Raylib.DrawEllipse((int)u.X, (int)(u.Y + 2 * sc), (int)(26 * sc), (int)(8 * sc), new Color(70, 88, 100, 248));

            // Panel lines
            for (int i = -3; i <= 3; i++)
            {
                float ly = u.Y + i * 2.2f * sc;
                Raylib.DrawLineEx(new Vector2(u.X - 24 * sc, ly), new Vector2(u.X + 24 * sc, ly), 0.8f,
                    new Color(74, 90, 104, 90));
            }

            // Hull rim
            Raylib.DrawEllipseLines((int)u.X, (int)(u.Y + 0.5f * sc), (int)(28 * sc), (int)(10.6f * sc),
                new Color(196, 214, 222, 170));

            // Dome
            Raylib.DrawEllipse((int)u.X, (int)(u.Y - 8.4f * sc), (int)(13.8f * sc), (int)(8.3f * sc),
                new Color(160, 185, 200, 240));
            Raylib.DrawEllipse((int)u.X, (int)(u.Y - 10 * sc), (int)(10 * sc), (int)(5 * sc),
                new Color(200, 218, 230, 230));
            // Viewport
            Raylib.DrawEllipse((int)u.X, (int)(u.Y - 7 * sc), (int)(7.8f * sc), (int)(3.3f * sc),
                new Color(18, 30, 40, 200));
            // Dome highlight
            Raylib.DrawRectangle((int)(u.X - 6 * sc), (int)(u.Y - 13 * sc), (int)(8.6f * sc), (int)(1.3f * sc),
                new Color(244, 252, 255, 128));

            // Navigation lights
            for (int i = 0; i < 8; i++)
            {
                float t = i / 8f * TAU;
                float lx = u.X + MathF.Cos(t) * 21 * sc;
                float ly = u.Y + MathF.Sin(t) * 5.4f * sc + 2.5f * sc;
                float blink = 0.34f + 0.66f * MathF.Max(0, MathF.Sin(s.Time * 6.4f + i * 0.85f + u.Id * 0.4f));
                Color lc = i % 2 != 0 ? new Color((byte)136, (byte)236, (byte)255, (byte)(blink * 215))
                                       : new Color((byte)255, (byte)180, (byte)128, (byte)(blink * 180));
                Raylib.DrawCircle((int)lx, (int)ly, 1.4f * sc, lc);
            }

            // Engine glow pods
            Raylib.BeginBlendMode(BlendMode.Additive);
            float eng = (0.35f + glow * 0.45f) * (1 - dmg * 0.45f);
            Raylib.DrawEllipse((int)(u.X - 11.8f * sc), (int)(u.Y + 8 * sc), (int)(5.6f * sc), (int)(2.3f * sc),
                new Color((byte)120, (byte)242, (byte)255, (byte)(eng * 160)));
            Raylib.DrawEllipse((int)u.X, (int)(u.Y + 8.8f * sc), (int)(6.2f * sc), (int)(2.6f * sc),
                new Color((byte)120, (byte)242, (byte)255, (byte)(eng * 160)));
            Raylib.DrawEllipse((int)(u.X + 11.8f * sc), (int)(u.Y + 8 * sc), (int)(5.6f * sc), (int)(2.3f * sc),
                new Color((byte)120, (byte)242, (byte)255, (byte)(eng * 160)));
            Raylib.EndBlendMode();

            // Damage cracks
            if (dmg > 0.08f)
            {
                byte da = (byte)(dmg * 150);
                Raylib.DrawLineEx(new Vector2(u.X - 8 * sc, u.Y + sc), new Vector2(u.X - 2 * sc, u.Y - 1.8f * sc), 1.1f, new Color((byte)60, (byte)40, (byte)34, da));
                Raylib.DrawLineEx(new Vector2(u.X + 6 * sc, u.Y + 2.4f * sc), new Vector2(u.X + 13 * sc, u.Y + 0.2f * sc), 1.1f, new Color((byte)60, (byte)40, (byte)34, da));
            }

            DrawDamageFlash(u.X, u.Y, u.FlashT, 28 * sc, 11 * sc);
        }
    }

    // --- RAIDERS --- Faithful port of drawRaiders()
    static void DrawRaiders(GameState s)
    {
        foreach (var r in s.Raiders)
        {
            float maxHp = 5f;
            float hpR = MathH.Clamp(r.Hp / maxHp, 0, 1);
            float dmg = 1 - hpR;
            float ra = r.Angle;
            float cosR = MathF.Cos(ra), sinR = MathF.Sin(ra);
            Vector2 RP(float lx, float ly) => new(r.X + cosR * lx - sinR * ly, r.Y + sinR * lx + cosR * ly);

            // Drop shadow
            Raylib.DrawEllipse((int)r.X, (int)(r.Y + 8), 52, 12, new Color((byte)0, (byte)0, (byte)0, (byte)(55 + dmg * 60)));

            // Screen glow
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawEllipse((int)(r.X - cosR * 10), (int)(r.Y + 2), 48, 14,
                new Color((byte)255, (byte)154, (byte)102, (byte)(35 + dmg * 60)));
            Raylib.EndBlendMode();

            // Fuselage body
            DrawQuad(RP(28, 0), RP(11, -9.8f), RP(-20, -9.2f), RP(-34, -2.2f), new Color(130, 145, 160, 245));
            DrawQuad(RP(28, 0), RP(-34, -2.2f), RP(-34, 2.2f), RP(-20, 9.2f), new Color(95, 108, 122, 248));
            DrawQuad(RP(28, 0), RP(-20, 9.2f), RP(11, 9.8f), RP(28, 0), new Color(110, 125, 140, 246));

            // Upper wing
            DrawQuad(RP(-7, -2), RP(-40, -14), RP(-28, -4), RP(-8, 0), new Color(72, 82, 94, 242));
            // Lower wing
            DrawQuad(RP(-7, 2), RP(-40, 14), RP(-28, 4), RP(-8, 0), new Color(72, 82, 94, 242));

            // Cockpit housing
            DrawQuad(RP(12, -2.3f), RP(24, -2.3f), RP(24, 2.3f), RP(12, 2.3f), new Color(170, 188, 208, 240));
            DrawQuad(RP(14, -1.5f), RP(22.8f, -1.5f), RP(22.8f, 1.5f), RP(14, 1.5f), new Color(32, 50, 68, 218));
            // Cockpit glint
            DrawQuad(RP(14.5f, -1.9f), RP(19.7f, -1.9f), RP(19.7f, -1f), RP(14.5f, -1f), new Color(228, 244, 255, 148));

            // Panel lines
            for (int i = -2; i <= 2; i++)
                Raylib.DrawLineEx(RP(-20, i * 2.9f), RP(18, i * 2.3f), 0.9f, new Color(54, 64, 74, 155));
            // Center line
            Raylib.DrawLineEx(RP(-28, 0), RP(20, 0), 1, new Color(190, 208, 222, 105));

            // Engine exhaust block
            DrawQuad(RP(-38, -2.6f), RP(-30.4f, -2.6f), RP(-30.4f, 2.6f), RP(-38, 2.6f), new Color(255, 206, 156, 230));
            float flicker = 0.46f + MathF.Sin(s.Time * 9 + r.Id) * 0.18f;
            DrawQuad(RP(-39.4f, -1.2f), RP(-36.7f, -1.2f), RP(-36.7f, 1.2f), RP(-39.4f, 1.2f),
                new Color((byte)126, (byte)236, (byte)255, (byte)(flicker * 200)));

            // HP bar
            DrawQuad(RP(-18, -12.2f), RP(19, -12.2f), RP(19, -9.8f), RP(-18, -9.8f), new Color(44, 52, 60, 200));
            float barW = 35.6f * hpR;
            DrawQuad(RP(-17.3f, -11.6f), RP(-17.3f + barW, -11.6f), RP(-17.3f + barW, -10.4f), RP(-17.3f, -10.4f),
                new Color(255, 102, 84, 242));

            // Damage cracks
            if (dmg > 0.08f)
            {
                byte da = (byte)(dmg * 175);
                Raylib.DrawLineEx(RP(-8, -5), RP(2, -2), 1.1f, new Color((byte)44, (byte)24, (byte)18, da));
                Raylib.DrawLineEx(RP(-6, 5), RP(4, 1), 1.1f, new Color((byte)44, (byte)24, (byte)18, da));
            }

            // Engine flame
            Raylib.BeginBlendMode(BlendMode.Additive);
            float flame = 0.32f + 0.68f * MathF.Max(0, MathF.Sin(s.Time * 17 + r.Id));
            var fp = RP(-42, 0);
            Raylib.DrawEllipse((int)fp.X, (int)fp.Y, (int)(6.2f + flame * 3), (int)(2.8f + flame),
                new Color((byte)255, (byte)146, (byte)102, (byte)(flame * 175)));
            Raylib.EndBlendMode();

            DrawDamageFlash(r.X, r.Y, r.FlashT, 40, 12);
        }
    }

    static void DrawQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color col)
    {
        Raylib.DrawTriangle(a, b, c, col);
        Raylib.DrawTriangle(a, c, d, col);
    }

    /// <summary>Additive white tint for damaged-but-not-killed enemies (FlashT 0.05 → 0).</summary>
    static void DrawDamageFlash(float x, float y, float flashT, float rx, float ry)
    {
        if (flashT <= 0) return;
        float fa = MathH.Clamp(flashT / 0.05f, 0, 1);
        Raylib.BeginBlendMode(BlendMode.Additive);
        Raylib.DrawEllipse((int)x, (int)y, (int)rx, (int)ry,
            new Color((byte)255, (byte)255, (byte)255, (byte)(fa * 170)));
        Raylib.EndBlendMode();
    }

    /// <summary>Shorthand to avoid Color(int,int,int,byte) ambiguity.</summary>
    static Color C4(int r, int g, int b, float a) =>
        new((byte)r, (byte)g, (byte)b, (byte)MathH.Clamp(a, 0, 255));

    // ?????????????? DEMON (easter egg) — faithful port of HTML drawDemon ??????????????
    static void DrawDemon(GameState s)
    {
        var d = s.Demon;
        if (d == null) return;

        float wing = 1f + MathF.Sin(s.Time * 11f + d.Phase) * 0.1f;
        float beat = 0.45f + 0.55f * MathF.Max(0f, MathF.Sin(s.Time * 8.6f + d.Phase));

        Vector2 P(float lx, float ly) => new(d.X + lx * wing, d.Y + ly);

        // --------- Aura glow (screen-blend via additive) ----------
        Raylib.BeginBlendMode(BlendMode.Additive);
        DrawGradientCircle(d.X, d.Y + 4, 74f, new Color((byte)255, (byte)90, (byte)76, (byte)((0.28f + beat * 0.22f) * 255)));
        DrawGradientCircle(d.X, d.Y + 4, 46f, new Color((byte)188, (byte)34, (byte)32, (byte)120));
        Raylib.EndBlendMode();

        // --------- Wings (membrane) — dark gradient silhouettes ----------
        // Left wing triangle with soft inner gradient (approx): three triangles back-to-front
        var lwOuter = P(-60, -14);
        var lwUp    = P(-36, -31);
        var lwBase1 = P(-9, -4);
        var lwBase2 = P(-26, 8);
        Raylib.DrawTriangle(lwBase1, lwUp, lwOuter, new Color((byte)96, (byte)20, (byte)22, (byte)240));
        Raylib.DrawTriangle(lwBase1, lwOuter, lwBase2, new Color((byte)56, (byte)12, (byte)14, (byte)235));
        // Edge highlight along outer membrane
        Raylib.DrawLineEx(lwUp, lwOuter, 1.6f, new Color((byte)200, (byte)60, (byte)54, (byte)195));
        // Inner bone struts
        Raylib.DrawLineEx(P(-11, -3), P(-42, -16), 1.2f, new Color((byte)188, (byte)52, (byte)48, (byte)150));
        Raylib.DrawLineEx(P(-15, 1), P(-36, 2), 1.2f, new Color((byte)188, (byte)52, (byte)48, (byte)130));

        var rwOuter = P(60, -14);
        var rwUp    = P(36, -31);
        var rwBase1 = P(9, -4);
        var rwBase2 = P(26, 8);
        Raylib.DrawTriangle(rwBase1, rwOuter, rwUp, new Color((byte)96, (byte)20, (byte)22, (byte)240));
        Raylib.DrawTriangle(rwBase1, rwBase2, rwOuter, new Color((byte)56, (byte)12, (byte)14, (byte)235));
        Raylib.DrawLineEx(rwUp, rwOuter, 1.6f, new Color((byte)200, (byte)60, (byte)54, (byte)195));
        Raylib.DrawLineEx(P(11, -3), P(42, -16), 1.2f, new Color((byte)188, (byte)52, (byte)48, (byte)150));
        Raylib.DrawLineEx(P(15, 1), P(36, 2), 1.2f, new Color((byte)188, (byte)52, (byte)48, (byte)130));

        // --------- Core body (hex-ish silhouette) ----------
        var bodyPts = new Vector2[]
        {
            new(d.X,       d.Y - 25),
            new(d.X + 16,  d.Y - 6),
            new(d.X + 13,  d.Y + 16),
            new(d.X,       d.Y + 26),
            new(d.X - 13,  d.Y + 16),
            new(d.X - 16,  d.Y - 6),
        };
        // Fan-triangulate from top vertex
        for (int i = 1; i < bodyPts.Length - 1; i++)
        {
            // Gradient approximation: top lighter, bottom darker
            float yMid = (bodyPts[0].Y + bodyPts[i].Y + bodyPts[i + 1].Y) / 3f;
            float t = MathH.Clamp((yMid - (d.Y - 24)) / 50f, 0, 1);
            byte r = (byte)MathH.Lerp(238, 86, t);
            byte g = (byte)MathH.Lerp(70, 10, t);
            byte b = (byte)MathH.Lerp(58, 12, t);
            Raylib.DrawTriangle(bodyPts[0], bodyPts[i], bodyPts[i + 1], new Color(r, g, b, (byte)250));
        }
        // Body outline
        for (int i = 0; i < bodyPts.Length; i++)
        {
            var a = bodyPts[i];
            var b = bodyPts[(i + 1) % bodyPts.Length];
            Raylib.DrawLineEx(a, b, 1.2f, new Color((byte)246, (byte)94, (byte)76, (byte)180));
        }

        // Horns (dark triangles at the top)
        Raylib.DrawTriangle(
            new Vector2(d.X - 3,  d.Y - 19),
            new Vector2(d.X - 10, d.Y - 31),
            new Vector2(d.X - 2,  d.Y - 25),
            new Color((byte)84, (byte)8, (byte)8, (byte)220));
        Raylib.DrawTriangle(
            new Vector2(d.X + 3,  d.Y - 19),
            new Vector2(d.X + 2,  d.Y - 25),
            new Vector2(d.X + 10, d.Y - 31),
            new Color((byte)84, (byte)8, (byte)8, (byte)220));

        // Eye socket shadow (dark ellipse)
        Raylib.DrawEllipse((int)d.X, (int)(d.Y - 5.5f), 8, 3, new Color((byte)18, (byte)4, (byte)4, (byte)230));

        // Glowing eyes + mouth (additive)
        Raylib.BeginBlendMode(BlendMode.Additive);
        byte eyeA = (byte)((0.6f + beat * 0.4f) * 255);
        Raylib.DrawCircle((int)(d.X - 4.8f), (int)(d.Y - 5.5f), 2.2f, new Color((byte)255, (byte)198, (byte)146, eyeA));
        Raylib.DrawCircle((int)(d.X + 4.8f), (int)(d.Y - 5.5f), 2.2f, new Color((byte)255, (byte)198, (byte)146, eyeA));
        // Glow halo around eyes
        DrawGradientCircle(d.X - 4.8f, d.Y - 5.5f, 7f, new Color((byte)255, (byte)140, (byte)90, (byte)(beat * 180)));
        DrawGradientCircle(d.X + 4.8f, d.Y - 5.5f, 7f, new Color((byte)255, (byte)140, (byte)90, (byte)(beat * 180)));
        // Mouth / mandible glow
        Raylib.DrawCircle((int)d.X, (int)(d.Y + 5.8f), 4.8f, new Color((byte)255, (byte)106, (byte)84, (byte)((0.4f + beat * 0.4f) * 255)));
        DrawGradientCircle(d.X, d.Y + 5.8f, 14f, new Color((byte)255, (byte)70, (byte)40, (byte)(beat * 160)));

        // Tail flame below body
        Raylib.DrawTriangle(
            new Vector2(d.X, d.Y + 24),
            new Vector2(d.X + 3.5f, d.Y + 36),
            new Vector2(d.X - 3.5f, d.Y + 36),
            new Color((byte)255, (byte)122, (byte)90, (byte)((0.4f + beat * 0.4f) * 255)));
        DrawGradientCircle(d.X, d.Y + 38f, 10f,
            new Color((byte)255, (byte)100, (byte)60, (byte)(beat * 180)));
        Raylib.EndBlendMode();

        // HP bar if damaged
        int maxHp = 6;
        float hpR = MathH.Clamp((float)d.Hp / maxHp, 0, 1);
        if (hpR < 0.999f)
        {
            Raylib.DrawRectangle((int)(d.X - 22), (int)(d.Y + 42), 44, 4,
                new Color((byte)40, (byte)12, (byte)12, (byte)200));
            Raylib.DrawRectangle((int)(d.X - 21), (int)(d.Y + 43), (int)(42 * hpR), 2,
                new Color((byte)255, (byte)90, (byte)70, (byte)240));
        }

        DrawDamageFlash(d.X, d.Y, d.FlashT, 30, 26);
    }

    // ?????????????? MOTHERSHIP (Star Destroyer easter egg) ??????????????
    static void DrawMothership(GameState s)
    {
        var m = s.Mothership;
        if (m == null) return;

        float forward = m.Vx >= 0 ? 1f : -1f;
        float cx = m.X, cy = m.Y;
        float w = m.W;

        Vector2 P(float lx, float ly) => new(cx + forward * lx, cy + ly);

        // Fade-in over the first 1.2s
        float alpha = MathH.Clamp(m.AppearTime / 1.2f, 0f, 1f);
        byte aByte = (byte)(alpha * 255);

        // --------- Rear trapezoid hull ----------
        // Split into 4 horizontal bands, each a solid-color trapezoid made of 2 triangles.
        // This gives us a clean gradient top-light → bottom-shadow without relying on
        // Rlgl.Quads (which emulates GL_QUADS and fails silently in one direction).
        var rt = P(-w * 0.5f, -24);   // rear-top
        var mt = P(w * 0.25f, -12);   // mid-top
        var rb = P(-w * 0.5f, 24);    // rear-bottom
        var mb = P(w * 0.25f, 12);    // mid-bottom
        // Precompute mid strips for gradient effect
        Vector2 Lerp(Vector2 a, Vector2 b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        var rMid1 = Lerp(rt, rb, 0.33f);
        var rMid2 = Lerp(rt, rb, 0.66f);
        var mMid1 = Lerp(mt, mb, 0.33f);
        var mMid2 = Lerp(mt, mb, 0.66f);

        Color bandA = new((byte)168, (byte)176, (byte)192, aByte); // topmost (lightest)
        Color bandB = new((byte)130, (byte)138, (byte)152, aByte);
        Color bandC = new((byte)94, (byte)100, (byte)114, aByte);
        Color bandD = new((byte)62, (byte)68, (byte)80, aByte);    // bottom (darkest)

        // Band 1 (top): rt, mt, mMid1, rMid1
        Raylib.DrawTriangle(rt, mt, mMid1, bandA);
        Raylib.DrawTriangle(rt, mMid1, rMid1, bandA);
        Raylib.DrawTriangle(rt, mMid1, mt, bandA);   // both windings to survive any culling state
        Raylib.DrawTriangle(rt, rMid1, mMid1, bandA);

        // Band 2
        Raylib.DrawTriangle(rMid1, mMid1, mMid2, bandB);
        Raylib.DrawTriangle(rMid1, mMid2, rMid2, bandB);
        Raylib.DrawTriangle(rMid1, mMid2, mMid1, bandB);
        Raylib.DrawTriangle(rMid1, rMid2, mMid2, bandB);

        // Band 3
        Raylib.DrawTriangle(rMid2, mMid2, mb, bandC);
        Raylib.DrawTriangle(rMid2, mb, rb, bandC);
        Raylib.DrawTriangle(rMid2, mb, mMid2, bandC);
        Raylib.DrawTriangle(rMid2, rb, mb, bandC);

        // Band 4 (bottom darkest accent strip — only cover a bit of the bottom for accent)
        var rMid3 = Lerp(rb, rt, 0.08f);   // tiny strip just above bottom
        var mMid3 = Lerp(mb, mt, 0.08f);
        Raylib.DrawTriangle(rMid3, mMid3, mb, bandD);
        Raylib.DrawTriangle(rMid3, mb, rb, bandD);
        Raylib.DrawTriangle(rMid3, mb, mMid3, bandD);
        Raylib.DrawTriangle(rMid3, rb, mb, bandD);

        // --------- Forward wedge tip (triangle from mid → nose) ----------
        // Top highlight triangle (upper half, lighter)
        Raylib.DrawTriangle(
            P(w * 0.25f, -12),
            P(w * 0.5f, 0),
            P(w * 0.25f, 0),
            new Color((byte)148, (byte)156, (byte)170, aByte));
        // Bottom shadow triangle (lower half, darker)
        Raylib.DrawTriangle(
            P(w * 0.25f, 0),
            P(w * 0.5f, 0),
            P(w * 0.25f, 12),
            new Color((byte)74, (byte)80, (byte)92, aByte));

        // Upper-edge highlight line (running from rear to nose tip)
        Raylib.DrawLineEx(P(-w * 0.5f, -24), P(w * 0.25f, -12), 1.4f,
            new Color((byte)200, (byte)208, (byte)222, aByte));
        Raylib.DrawLineEx(P(w * 0.25f, -12), P(w * 0.5f, 0), 1.4f,
            new Color((byte)200, (byte)208, (byte)222, aByte));
        // Bottom dark edge
        Raylib.DrawLineEx(P(-w * 0.5f, 24), P(w * 0.25f, 12), 1.2f,
            new Color((byte)28, (byte)34, (byte)44, aByte));
        Raylib.DrawLineEx(P(w * 0.25f, 12), P(w * 0.5f, 0), 1.0f,
            new Color((byte)28, (byte)34, (byte)44, aByte));

        // --------- Trench / panel lines (horizontal hull detail) ----------
        int trenches = 3;
        for (int i = 0; i < trenches; i++)
        {
            float ty = -10 + i * 8f;
            byte shade = (byte)(44 + i * 4);
            Raylib.DrawLineEx(P(-w * 0.48f, ty), P(w * 0.22f, ty), 1f,
                new Color(shade, (byte)(shade + 4), (byte)(shade + 8), aByte));
        }

        // --------- Command tower (stepped structure near rear) ----------
        float towerX = -w * 0.28f;
        // Base box — slightly angled front face
        var tb1 = P(towerX - 28, -24);
        var tb2 = P(towerX + 30, -24);
        var tb3 = P(towerX + 26, -44);
        var tb4 = P(towerX - 24, -44);
        Raylib.DrawTriangle(tb1, tb2, tb3, new Color((byte)130, (byte)138, (byte)152, aByte));
        Raylib.DrawTriangle(tb1, tb3, tb4, new Color((byte)130, (byte)138, (byte)152, aByte));
        // Dark bridge window strip
        Raylib.DrawLineEx(P(towerX - 26, -32), P(towerX + 27, -32), 3f,
            new Color((byte)30, (byte)38, (byte)52, aByte));
        // Bridge light glow (row of bright windows on the strip)
        for (int i = 0; i < 8; i++)
        {
            float ft = i / 7f;
            float wx = towerX - 22 + ft * 46;
            float bright = 0.6f + 0.4f * MathF.Sin(m.Phase * 2 + i * 0.9f);
            Raylib.DrawCircle((int)P(wx, -32).X, (int)P(wx, -32).Y, 1.0f,
                new Color((byte)200, (byte)230, (byte)255, (byte)(bright * aByte)));
        }
        // Top step (smaller)
        var ts1 = P(towerX - 18, -44);
        var ts2 = P(towerX + 20, -44);
        var ts3 = P(towerX + 16, -58);
        var ts4 = P(towerX - 14, -58);
        Raylib.DrawTriangle(ts1, ts2, ts3, new Color((byte)146, (byte)154, (byte)168, aByte));
        Raylib.DrawTriangle(ts1, ts3, ts4, new Color((byte)146, (byte)154, (byte)168, aByte));
        // Two sensor globes atop
        var g1 = P(towerX - 14, -62);
        var g2 = P(towerX + 14, -62);
        Raylib.DrawCircle((int)g1.X, (int)g1.Y, 5.2f, new Color((byte)160, (byte)168, (byte)184, aByte));
        Raylib.DrawCircle((int)g2.X, (int)g2.Y, 5.2f, new Color((byte)160, (byte)168, (byte)184, aByte));
        // Globe highlights
        Raylib.DrawCircle((int)(g1.X - 1.5f), (int)(g1.Y - 1.5f), 1.4f, new Color((byte)220, (byte)228, (byte)240, aByte));
        Raylib.DrawCircle((int)(g2.X - 1.5f), (int)(g2.Y - 1.5f), 1.4f, new Color((byte)220, (byte)228, (byte)240, aByte));

        // --------- Engines (rear exhaust glow) ----------
        float engX = -w * 0.5f;
        float glow = 0.7f + 0.3f * MathF.Sin(m.Phase * 3);
        Raylib.BeginBlendMode(BlendMode.Additive);
        for (int i = -1; i <= 1; i++)
        {
            float ey = i * 9;
            var ep = P(engX + 2, ey);
            DrawGradientCircle(ep.X, ep.Y, 22f,
                new Color((byte)140, (byte)200, (byte)255, (byte)(glow * alpha * 170)));
            DrawGradientCircle(ep.X - forward * 16, ep.Y, 14f,
                new Color((byte)180, (byte)220, (byte)255, (byte)(glow * alpha * 120)));
            Raylib.DrawCircle((int)ep.X, (int)ep.Y, 4.2f,
                new Color((byte)220, (byte)240, (byte)255, aByte));
        }
        // Engine trail (soft plume behind the hull)
        var plume = P(engX - forward * 46, 0);
        DrawGradientCircle(plume.X, plume.Y, 38f,
            new Color((byte)120, (byte)180, (byte)240, (byte)(glow * alpha * 65)));
        Raylib.EndBlendMode();

        // --------- Window dots along hull side ----------
        Raylib.BeginBlendMode(BlendMode.Additive);
        int wins = 24;
        for (int i = 0; i < wins; i++)
        {
            float ft = i / (float)(wins - 1);
            float winX = -w * 0.46f + ft * (w * 0.70f);
            float winY = -6 + ft * 2f;
            float ph = i * 0.7f + m.Phase;
            float fl = 0.5f + 0.5f * MathF.Sin(ph);
            Raylib.DrawCircle((int)P(winX, winY).X, (int)P(winX, winY).Y, 0.85f,
                new Color((byte)225, (byte)255, (byte)210, (byte)(fl * alpha * 180)));
        }
        Raylib.EndBlendMode();

        // --------- Deflector force-field (shield active state) ----------
        if (m.ShieldActive)
        {
            float shieldRx = w * 0.58f;
            float shieldRy = 60f;
            float pulse = 0.7f + 0.3f * MathF.Sin(s.Time * 4.2f + m.Phase);

            // Soft inner bubble (additive)
            Raylib.BeginBlendMode(BlendMode.Additive);
            DrawGradientCircle(cx, cy + 4, shieldRx,
                new Color((byte)80, (byte)160, (byte)255, (byte)(alpha * pulse * 75)));
            Raylib.EndBlendMode();

            // Hex-pattern outline (static dome ring + a few scattered hex facets)
            byte ringA = (byte)(alpha * (0.55f + pulse * 0.25f) * 255);
            Raylib.DrawEllipseLines((int)cx, (int)(cy + 4), shieldRx, shieldRy,
                new Color((byte)140, (byte)210, (byte)255, ringA));
            Raylib.DrawEllipseLines((int)cx, (int)(cy + 4), shieldRx - 3, shieldRy - 2,
                new Color((byte)100, (byte)170, (byte)230, (byte)(ringA * 0.55f)));

            // Hex "panels" — draw 8 rotating hexagon outlines around the dome surface for a force-field grid feel
            int hexCount = 10;
            for (int i = 0; i < hexCount; i++)
            {
                float theta = (i / (float)hexCount) * MathH.TAU + s.Time * 0.4f;
                float hx = cx + MathF.Cos(theta) * shieldRx * 0.85f;
                float hy = cy + 4 + MathF.Sin(theta) * shieldRy * 0.85f;
                float hr = 7f;
                // Hexagon outline
                for (int k = 0; k < 6; k++)
                {
                    float a1 = (k / 6f) * MathH.TAU;
                    float a2 = ((k + 1) / 6f) * MathH.TAU;
                    Raylib.DrawLineEx(
                        new Vector2(hx + MathF.Cos(a1) * hr, hy + MathF.Sin(a1) * hr),
                        new Vector2(hx + MathF.Cos(a2) * hr, hy + MathF.Sin(a2) * hr),
                        1f,
                        new Color((byte)160, (byte)220, (byte)255, (byte)(alpha * 160)));
                }
            }

            // Ripple expanding outward after a deflection hit
            if (m.ShieldRippleT > 0.02f)
            {
                Raylib.BeginBlendMode(BlendMode.Additive);
                for (int i = 0; i < 3; i++)
                {
                    float t = MathH.Clamp(m.ShieldRippleT - i * 0.08f, 0, 1);
                    if (t <= 0) continue;
                    float rr = shieldRx * (0.6f + (1f - t) * 0.55f);
                    byte ra = (byte)(t * 180);
                    Raylib.DrawEllipseLines((int)cx, (int)(cy + 4), rr, rr * (shieldRy / shieldRx),
                        new Color((byte)200, (byte)240, (byte)255, ra));
                }
                Raylib.EndBlendMode();
            }

            // "SHIELDS UP" HUD tag
            string tag = "SHIELDS UP";
            int tw = MeasureTextM(tag, 14, true);
            DrawTextM(tag, cx - tw * 0.5f, cy - 100, 14,
                new Color((byte)140, (byte)220, (byte)255, (byte)(alpha * 230)), true);
        }

        // --------- Hit flash (visible whether shield is up or down) ----------
        if (m.ShieldFlash > 0.02f)
        {
            Raylib.BeginBlendMode(BlendMode.Additive);
            byte sa = (byte)(m.ShieldFlash * alpha * 140);
            DrawGradientCircle(cx, cy + 4, w * 0.58f,
                new Color((byte)100, (byte)180, (byte)255, sa));
            for (int i = 1; i <= 3; i++)
            {
                float rr = w * 0.5f * (0.42f + i * 0.18f);
                Raylib.DrawCircleLines((int)cx, (int)cy, rr,
                    new Color((byte)150, (byte)210, (byte)255, (byte)(m.ShieldFlash * alpha * 90 / i)));
            }
            Raylib.EndBlendMode();
        }

        // --------- HP bar ----------
        float hpR = MathH.Clamp((float)m.Hp / m.MaxHp, 0, 1);
        if (hpR < 0.999f)
        {
            float barW = w * 0.42f;
            float barX = cx - barW * 0.5f;
            float barY = cy - 82;
            Raylib.DrawRectangle((int)barX, (int)barY, (int)barW, 6,
                new Color((byte)20, (byte)26, (byte)40, (byte)220));
            Raylib.DrawRectangle((int)(barX + 1), (int)(barY + 1), (int)((barW - 2) * hpR), 4,
                new Color((byte)220, (byte)90, (byte)60, (byte)240));
            // Tick marks every 10%
            for (int t = 1; t < 10; t++)
            {
                float tx = barX + (barW * t / 10f);
                Raylib.DrawLine((int)tx, (int)barY, (int)tx, (int)(barY + 6),
                    new Color((byte)8, (byte)14, (byte)24, (byte)200));
            }
        }
    }

    // ?????????????? TIE FIGHTERS (mothership deployments) ??????????????
    // Cull-safe triangle helper — draws both windings so the panel shows regardless of rotation.
    static void DrawTriBoth(Vector2 a, Vector2 b, Vector2 c, Color col)
    {
        Raylib.DrawTriangle(a, b, c, col);
        Raylib.DrawTriangle(a, c, b, col);
    }

    static void DrawFighters(GameState s)
    {
        if (s.Fighters.Count == 0) return;

        foreach (var f in s.Fighters)
        {
            // Orientation from velocity
            float ang = MathF.Atan2(f.Vy, f.Vx);
            float cos = MathF.Cos(ang), sin = MathF.Sin(ang);
            const float SC = 1.95f;
            Vector2 R(float lx, float ly) => new(
                f.X + (lx * cos - ly * sin) * SC,
                f.Y + (lx * sin + ly * cos) * SC);

            float bank = f.Roll;
            float lBank = 1f + bank * 0.15f;
            float rBank = 1f - bank * 0.15f;

            // Iconic TIE hex-wing coords (left panel, mirrored for right)
            // Panel is a flat hexagon standing vertically
            Vector2 L_top = R(-10, -11 * lBank);
            Vector2 L_tu  = R(-14, -6 * lBank);
            Vector2 L_td  = R(-14,  6 * lBank);
            Vector2 L_bot = R(-10, 11 * lBank);
            Vector2 L_bd  = R(-6,   6 * lBank);
            Vector2 L_bu  = R(-6,  -6 * lBank);

            Vector2 R_top = R( 10, -11 * rBank);
            Vector2 R_tu  = R( 14, -6 * rBank);
            Vector2 R_td  = R( 14,  6 * rBank);
            Vector2 R_bot = R( 10, 11 * rBank);
            Vector2 R_bd  = R(  6,  6 * rBank);
            Vector2 R_bu  = R(  6, -6 * rBank);

            Color darkPanel = new((byte)26, (byte)32, (byte)42, (byte)250);
            Color midPanel  = new((byte)52, (byte)60, (byte)74, (byte)250);
            Color panelEdge = new((byte)140, (byte)152, (byte)170, (byte)245);

            // Left panel: fan from top vertex with both windings
            DrawTriBoth(L_top, L_tu, L_bu, midPanel);
            DrawTriBoth(L_tu,  L_td, L_bu, darkPanel);
            DrawTriBoth(L_td,  L_bd, L_bu, darkPanel);
            DrawTriBoth(L_td,  L_bot, L_bd, midPanel);

            // Right panel
            DrawTriBoth(R_top, R_tu, R_bu, midPanel);
            DrawTriBoth(R_tu,  R_td, R_bu, darkPanel);
            DrawTriBoth(R_td,  R_bd, R_bu, darkPanel);
            DrawTriBoth(R_td,  R_bot, R_bd, midPanel);

            // Panel outline (hex perimeter)
            Raylib.DrawLineEx(L_top, L_tu,  2.0f, panelEdge);
            Raylib.DrawLineEx(L_tu,  L_td,  2.0f, panelEdge);
            Raylib.DrawLineEx(L_td,  L_bot, 2.0f, panelEdge);
            Raylib.DrawLineEx(L_bot, L_bd,  2.0f, panelEdge);
            Raylib.DrawLineEx(L_bd,  L_bu,  2.0f, panelEdge);
            Raylib.DrawLineEx(L_bu,  L_top, 2.0f, panelEdge);

            Raylib.DrawLineEx(R_top, R_tu,  2.0f, panelEdge);
            Raylib.DrawLineEx(R_tu,  R_td,  2.0f, panelEdge);
            Raylib.DrawLineEx(R_td,  R_bot, 2.0f, panelEdge);
            Raylib.DrawLineEx(R_bot, R_bd,  2.0f, panelEdge);
            Raylib.DrawLineEx(R_bd,  R_bu,  2.0f, panelEdge);
            Raylib.DrawLineEx(R_bu,  R_top, 2.0f, panelEdge);

            // Internal panel strut (the distinctive TIE "+" cross)
            Color strutCol = new((byte)170, (byte)184, (byte)208, (byte)235);
            Raylib.DrawLineEx(R(-12, 0), R(-8, 0), 1.6f, strutCol);
            Raylib.DrawLineEx(R(-10, -10), R(-10, 10), 1.6f, strutCol);
            Raylib.DrawLineEx(R( 12, 0), R( 8, 0), 1.6f, strutCol);
            Raylib.DrawLineEx(R( 10, -10), R( 10, 10), 1.6f, strutCol);

            // ---- Pylons (horizontal bars between cockpit and each wing) ----
            Color pylonCol = new((byte)155, (byte)168, (byte)188, (byte)255);
            // Left pylon as two thin triangles to survive cull
            DrawTriBoth(R(-6, -1.6f), R(-6, 1.6f), R(-2, 1.4f), pylonCol);
            DrawTriBoth(R(-6, -1.6f), R(-2, 1.4f), R(-2, -1.4f), pylonCol);
            // Right pylon
            DrawTriBoth(R(6, -1.6f),  R(6, 1.6f),  R(2, 1.4f), pylonCol);
            DrawTriBoth(R(6, -1.6f),  R(2, 1.4f),  R(2, -1.4f), pylonCol);
            // Bright rim on pylons
            Raylib.DrawLineEx(R(-6, -1.6f), R(-2, -1.4f), 1.0f, new Color((byte)210, (byte)222, (byte)240, (byte)220));
            Raylib.DrawLineEx(R( 6, -1.6f), R( 2, -1.4f), 1.0f, new Color((byte)210, (byte)222, (byte)240, (byte)220));

            // ---- Central cockpit ball ----
            Color cockBody = new((byte)76, (byte)86, (byte)104, (byte)255);
            Color cockHi   = new((byte)172, (byte)186, (byte)208, (byte)235);
            Raylib.DrawCircleV(R(0, 0), 7.8f, cockBody);
            Raylib.DrawCircleV(R(-0.9f, -0.9f), 3.2f, cockHi);
            // Rim ring
            Raylib.DrawCircleLines((int)R(0, 0).X, (int)R(0, 0).Y, 7.8f,
                new Color((byte)200, (byte)214, (byte)235, (byte)230));

            // Cockpit viewport (menacing red dot)
            float winPulse = 0.6f + 0.4f * MathF.Sin(f.Phase);
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawCircleV(R(1.2f, 0), 2.9f,
                new Color((byte)255, (byte)100, (byte)70, (byte)(winPulse * 240)));
            DrawGradientCircle(R(1.2f, 0).X, R(1.2f, 0).Y, 11f,
                new Color((byte)255, (byte)110, (byte)55, (byte)(winPulse * 175)));
            // Engine plume
            var ep = R(-3.4f, 0);
            DrawGradientCircle(ep.X, ep.Y, 14f,
                new Color((byte)180, (byte)220, (byte)255, (byte)(winPulse * 130)));
            Raylib.EndBlendMode();

            DrawDamageFlash(f.X, f.Y, f.FlashT, 16, 14);
        }
    }

    // ?????????????? WEATHER ??????????????
    static void DrawWeatherBack(GameState s)
    {
        foreach (var fb in s.Weather.FogBands)
        {
            float w = MathF.Sin(s.Time * fb.Speed + fb.Phase);
            float y = fb.Y + w * 18;
            byte a = (byte)(fb.Alpha * (0.8f + 0.2f * w) * 255);
            Raylib.DrawRectangle(0, (int)(y - fb.Thickness), (int)s.W, (int)(fb.Thickness * 2),
                new Color((byte)85, (byte)125, (byte)185, (byte)(a / 3)));
        }
    }

    static void DrawWeatherFront(GameState s)
    {
        bool rain = s.Weather.Mode == "storm"; bool ash = s.Weather.Mode == "ash";
        if (!rain && !ash) return;
        foreach (var p in s.Weather.Particles)
        {
            byte a = (byte)(p.Alpha * 180);
            if (rain)
            {
                float ex = p.X + p.Vx * 0.025f, ey = p.Y + p.Vy * 0.025f;
                Raylib.DrawLineEx(new Vector2(p.X, p.Y), new Vector2(ex, ey), 1.2f, new Color((byte)148, (byte)182, (byte)232, a));
            }
            else Raylib.DrawCircle((int)p.X, (int)p.Y, p.Len * 0.5f, new Color((byte)172, (byte)132, (byte)92, a));
        }
    }

    static void DrawLightning(GameState s)
    {
        if (s.Weather.Bolts.Count == 0) return;
        Raylib.BeginBlendMode(BlendMode.Additive);
        foreach (var bolt in s.Weather.Bolts)
        {
            if (bolt.Segments == null || bolt.Segments.Count == 0) continue;
            float p = bolt.Life / bolt.MaxLife;
            float a = p * bolt.Bright;
            if (a <= 0.02f) continue;

            // Layer 1: Wide glow (all segments)
            foreach (var seg in bolt.Segments)
            {
                Raylib.DrawLineEx(new Vector2(seg.X1, seg.Y1), new Vector2(seg.X2, seg.Y2),
                    6f, new Color((byte)180, (byte)200, (byte)255, (byte)(a * 0.3f * 255)));
            }

            // Layer 2: Main bolt � per-segment (trunk thicker, branches thinner)
            foreach (var seg in bolt.Segments)
            {
                float lw = seg.Branch ? 1.2f : 2.5f;
                byte sa = seg.Branch ? (byte)(a * 0.5f * 255) : (byte)(a * 0.85f * 255);
                Color col = seg.Branch
                    ? new Color((byte)180, (byte)200, (byte)255, sa)
                    : new Color((byte)240, (byte)245, (byte)255, sa);
                Raylib.DrawLineEx(new Vector2(seg.X1, seg.Y1), new Vector2(seg.X2, seg.Y2), lw, col);
            }

            // Layer 3: Bright core (trunk segments only). §5 5.2: a blob every
            // 3rd trunk segment lights the cloudscape along the bolt's path.
            int trunkIdx = 0;
            foreach (var seg in bolt.Segments)
            {
                if (seg.Branch) continue;
                Raylib.DrawLineEx(new Vector2(seg.X1, seg.Y1), new Vector2(seg.X2, seg.Y2),
                    1f, new Color((byte)255, (byte)255, (byte)255, (byte)(a * 0.9f * 255)));
                if (trunkIdx++ % 3 == 1)
                    AddLight((seg.X1 + seg.X2) * 0.5f, (seg.Y1 + seg.Y2) * 0.5f, 70f,
                        185, 205, 255, (byte)(a * 110));
            }
        }
        Raylib.EndBlendMode();
    }

    // ?????????????? TRAILS ??????????????
    static void DrawTrails(GameState s)
    {
        foreach (var tr in s.Trails)
        {
            float a = tr.Life / tr.MaxLife;
            if (a <= 0) continue;
            Raylib.DrawCircle((int)tr.X, (int)tr.Y, tr.Size * 1.3f, new Color(tr.R, tr.G, tr.B, (byte)(a * 185)));
        }
    }

    // ?????????????? SMOKE ??????????????
    // §5 5.3: destruction smoke is DARK and alpha-blended — the old bright
    // blue-grey stops washed out over additive fire. Drawn grouped per
    // BlendClass so the blend mode switches at most once for the whole list.
    static void DrawSmoke(GameState s)
    {
        bool anyAdditive = false;
        foreach (var sm in s.SmokeParts)
        {
            if (sm.Blend == BlendClass.Additive) { anyAdditive = true; continue; }
            float p = 1 - sm.Life / sm.MaxLife;
            // Quick fade-in (first 12% of life) so puffs don't pop, then fade out
            float a = sm.Alpha * MathF.Min(1f, p * 8.5f) * (1 - p);
            if (a <= 0.01f) continue;
            float r = sm.Size * (0.85f + p * 1.8f);
            // Brown-grey translucent body, max-alpha clamped at 160
            byte body = (byte)MathF.Min(160f, a * 255f);
            DrawGradientCircle(sm.X, sm.Y, r, new Color((byte)33, (byte)29, (byte)26, body));
            DrawGradientCircle(sm.X, sm.Y, r * 0.55f,
                new Color((byte)45, (byte)39, (byte)34, (byte)(body * 0.8f)));
        }
        if (!anyAdditive) return;
        // Additive-class smoke (bright vapor) — none spawned today; this is the
        // §5 5.3 grouping seam so a future class never costs per-puff flushes.
        Raylib.BeginBlendMode(BlendMode.Additive);
        foreach (var sm in s.SmokeParts)
        {
            if (sm.Blend != BlendClass.Additive) continue;
            float p = 1 - sm.Life / sm.MaxLife;
            float a = sm.Alpha * (1 - p);
            if (a <= 0.01f) continue;
            DrawGradientCircle(sm.X, sm.Y, sm.Size * (0.85f + p * 1.8f),
                new Color((byte)150, (byte)180, (byte)220, (byte)(a * 255)));
        }
        Raylib.EndBlendMode();
    }

    // --- ENEMY MISSILES --- Rotated shaped warheads
    static void DrawEnemyMissiles(GameState s)
    {
        foreach (var m in s.Enemies)
        {
            if (m.Dead) continue;
            var vc = Palette.VariantColor(m.Variant);
            float vx = m._Vx, vy = m._Vy;
            float ang = MathF.Atan2(vy, vx);
            float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
            Vector2 MP(float lx, float ly) => new(m.X + ca * lx - sa * ly, m.Y + sa * lx + ca * ly);

            bool isStealth = m.Variant == "stealth";
            bool isCarrier = m.Variant == "carrier";
            bool isCruise = m.Variant == "cruise";
            bool isDrone = m.Variant == "drone";
            bool isSpit = m.Variant == "spit";
            bool isHell = m.Variant == "hell";
            bool isShield = m.Variant == "shield";

            float sAlpha = isStealth ? MathF.Pow(MathH.Clamp((m.Y + 100) / (s.GroundY + 100), 0, 1), 3) * 0.55f + 0.05f : 1f;

            // Curved trail from position buffer
            float tw = isCarrier ? 4.6f : isCruise ? 3.8f : isDrone ? 2.2f : 3.2f;
            if (m.Trail.Count > 1)
            {
                Color tc = isCruise ? new Color((byte)116, (byte)255, (byte)210, (byte)255)
                    : isCarrier ? new Color((byte)255, (byte)182, (byte)132, (byte)255)
                    : isDrone ? new Color((byte)166, (byte)246, (byte)255, (byte)255)
                    : new Color(vc.R, vc.G, vc.B, (byte)255);
                for (int ti = 0; ti < m.Trail.Count - 1; ti++)
                {
                    float al = (1 - (float)ti / m.Trail.Count) * 0.72f * sAlpha;
                    var a = m.Trail[ti]; var b = m.Trail[ti + 1];
                    Raylib.DrawLineEx(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), tw,
                        new Color(tc.R, tc.G, tc.B, (byte)(al * 255)));
                }
            }

            // Shadow glow
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawCircle((int)m.X, (int)m.Y, 16, new Color(vc.R, vc.G, vc.B, (byte)(16 * sAlpha * (isStealth ? 0.2f : 1f))));
            Raylib.EndBlendMode();

            // --- MISSILE BODY SHAPES � proper warhead/body/fins/exhaust per variant ---
            if (isCarrier)
            {
                // Heavy armored carrier � wide hexagonal fuselage, cockpit, panel lines, HP bar, dual engines
                float hpRatio = MathH.Clamp(m.Hp / 3f, 0, 1);
                // Main hull (upper + lower halves for shading)
                DrawQuad(MP(18, 0), MP(7, -7.5f), MP(-13, -7.5f), MP(-18, 0), C4(100, 112, 128, 248 * sAlpha));
                DrawQuad(MP(-18, 0), MP(-13, 7.5f), MP(7, 7.5f), MP(18, 0), C4(75, 85, 100, 245 * sAlpha));
                // Mid body highlight stripe
                DrawQuad(MP(-10, -2), MP(12, -2), MP(12, 2), MP(-10, 2), C4(140, 152, 168, 180 * sAlpha));
                // Nosecone housing
                DrawQuad(MP(10, -2f), MP(19, -1.5f), MP(19, 1.5f), MP(10, 2f), C4(196, 214, 234, 238 * sAlpha));
                // Cockpit window
                DrawQuad(MP(11, -1f), MP(17, -0.8f), MP(17, 0.8f), MP(11, 1f), C4(40, 56, 72, 200 * sAlpha));
                // Panel lines
                for (int i = -2; i <= 2; i++)
                    Raylib.DrawLineEx(MP(-12, i * 2.5f), MP(10, i * 2.2f), 0.7f, C4(42, 54, 66, 85 * sAlpha));
                // HP bar backing
                DrawQuad(MP(-11, -9.5f), MP(11, -9.5f), MP(11, -7.5f), MP(-11, -7.5f), C4(52, 58, 68, 200 * sAlpha));
                // HP bar fill
                DrawQuad(MP(-10, -9.2f), MP(-10 + 20 * hpRatio, -9.2f), MP(-10 + 20 * hpRatio, -7.8f), MP(-10, -7.8f),
                    C4(255, 96, 86, 245 * sAlpha));
                // Tail fins (upper + lower)
                DrawQuad(MP(-13, -7.5f), MP(-17, -11f), MP(-19, -10f), MP(-16, -7.5f), C4(70, 82, 98, 220 * sAlpha));
                DrawQuad(MP(-13, 7.5f), MP(-17, 11f), MP(-19, 10f), MP(-16, 7.5f), C4(60, 72, 88, 220 * sAlpha));
                // Dual engine blocks
                float ep = 0.4f + MathF.Sin(s.Time * 13 + m.Id) * 0.16f;
                DrawQuad(MP(-19, -3.5f), MP(-14, -3.5f), MP(-14, -1.5f), MP(-19, -1.5f), C4(50, 58, 70, 235 * sAlpha));
                DrawQuad(MP(-19, 1.5f), MP(-14, 1.5f), MP(-14, 3.5f), MP(-19, 3.5f), C4(50, 58, 70, 235 * sAlpha));
                // Engine glow
                Raylib.BeginBlendMode(BlendMode.Additive);
                DrawQuad(MP(-24, -3f), MP(-19, -3f), MP(-19, -2f), MP(-24, -2f),
                    new Color((byte)126, (byte)238, (byte)255, (byte)(ep * 200 * sAlpha)));
                DrawQuad(MP(-24, 2f), MP(-19, 2f), MP(-19, 3f), MP(-24, 3f),
                    new Color((byte)126, (byte)238, (byte)255, (byte)(ep * 200 * sAlpha)));
                // §5 4.2 deploy telegraph: pulsing bay glow under the hull,
                // ≥0.5 s before the drones release
                if (m.TelegraphT > 0 && !m._Deployed)
                {
                    float bp = 0.55f + 0.45f * MathF.Sin(m.TelegraphT * 13f);
                    var bay = MP(-3, 6.5f);
                    Raylib.DrawEllipse((int)bay.X, (int)bay.Y, 12f, 5f,
                        new Color((byte)255, (byte)188, (byte)110, (byte)(bp * 170 * sAlpha)));
                    Raylib.DrawEllipse((int)bay.X, (int)bay.Y, 6f, 2.6f,
                        new Color((byte)255, (byte)236, (byte)190, (byte)(bp * 140 * sAlpha)));
                }
                Raylib.EndBlendMode();
            }
            else if (isCruise)
            {
                // Cruise missile � elongated fuselage, swept wings, pointed nose, single engine
                // Body tube
                DrawQuad(MP(8, -3.5f), MP(-10, -3.5f), MP(-12, -2.8f), MP(8, -2.8f), C4(100, 220, 190, 235 * sAlpha));
                DrawQuad(MP(8, 2.8f), MP(-10, 2.8f), MP(-12, 3.5f), MP(8, 3.5f), C4(78, 195, 168, 230 * sAlpha));
                DrawQuad(MP(8, -2.8f), MP(-12, -2.8f), MP(-12, 2.8f), MP(8, 2.8f), C4(126, 255, 220, 242 * sAlpha));
                // Pointed nosecone
                DrawQuad(MP(8, -2.8f), MP(16, -0.8f), MP(16, 0.8f), MP(8, 2.8f), C4(150, 255, 235, 245 * sAlpha));
                DrawQuad(MP(16, -0.8f), MP(20, 0), MP(16, 0.8f), MP(16, -0.8f), C4(212, 255, 244, 250 * sAlpha));
                // Swept wings (upper + lower)
                DrawQuad(MP(-3, -3.5f), MP(2, -8f), MP(-2, -8f), MP(-5, -3.5f), C4(92, 210, 185, 210 * sAlpha));
                DrawQuad(MP(-3, 3.5f), MP(2, 8f), MP(-2, 8f), MP(-5, 3.5f), C4(80, 195, 170, 210 * sAlpha));
                // Tail fins
                DrawQuad(MP(-10, -3.5f), MP(-9, -6.5f), MP(-12, -6f), MP(-12, -3.5f), C4(85, 200, 175, 220 * sAlpha));
                DrawQuad(MP(-10, 3.5f), MP(-9, 6.5f), MP(-12, 6f), MP(-12, 3.5f), C4(75, 185, 160, 220 * sAlpha));
                // Engine nozzle
                DrawQuad(MP(-14, -2f), MP(-12, -2.5f), MP(-12, 2.5f), MP(-14, 2f), C4(72, 165, 145, 230 * sAlpha));
                // Exhaust plume
                Raylib.BeginBlendMode(BlendMode.Additive);
                var epp = MP(-20, 0);
                Raylib.DrawEllipse((int)epp.X, (int)epp.Y, 5.5f, 2.4f, new Color((byte)102, (byte)255, (byte)218, (byte)(120 * sAlpha)));
                Raylib.DrawEllipse((int)epp.X, (int)epp.Y, 3f, 1.2f, new Color((byte)200, (byte)255, (byte)240, (byte)(90 * sAlpha)));
                Raylib.EndBlendMode();
            }
            else if (isDrone)
            {
                // Small reconnaissance drone � delta wing, sensor pod, compact engine
                // Delta-wing body
                DrawQuad(MP(10, 0), MP(0, -5f), MP(-9, -3.2f), MP(-9, 3.2f), C4(130, 165, 185, 245 * sAlpha));
                DrawQuad(MP(10, 0), MP(0, 5f), MP(-9, 3.2f), MP(-9, -3.2f), C4(100, 135, 155, 240 * sAlpha));
                // Bay/panel
                DrawQuad(MP(-5, -2.5f), MP(2, -2.5f), MP(2, 2.5f), MP(-5, 2.5f), C4(44, 62, 78, 225 * sAlpha));
                // Nosecone
                DrawQuad(MP(10, 0), MP(14, -0.6f), MP(14, 0.6f), MP(10, 0), C4(204, 238, 255, 248 * sAlpha));
                // Wing tips (upper + lower)
                DrawQuad(MP(-2, -5f), MP(1, -7.2f), MP(-3, -6.8f), MP(-4, -4.5f), C4(95, 125, 145, 210 * sAlpha));
                DrawQuad(MP(-2, 5f), MP(1, 7.2f), MP(-3, 6.8f), MP(-4, 4.5f), C4(85, 115, 135, 210 * sAlpha));
                // Sensor eye
                var eye = MP(2, 0);
                Raylib.DrawCircle((int)eye.X, (int)eye.Y, 1.8f, C4(236, 250, 255, 245 * sAlpha));
                // Engine block
                DrawQuad(MP(-11, -1.5f), MP(-9, -2f), MP(-9, 2f), MP(-11, 1.5f), C4(55, 75, 95, 230 * sAlpha));
                // Exhaust
                float dex = 0.38f + MathF.Sin(s.Time * 12 + m.Id) * 0.2f;
                Raylib.BeginBlendMode(BlendMode.Additive);
                DrawQuad(MP(-14, -1f), MP(-11, -1.2f), MP(-11, 1.2f), MP(-14, 1f),
                    new Color((byte)136, (byte)242, (byte)255, (byte)(dex * 200 * sAlpha)));
                Raylib.EndBlendMode();
            }
            else if (isSpit || isHell)
            {
                // Organic/incendiary warhead � bulbous nose, short body, fiery exhaust
                Color c1 = isHell ? C4(255, 108, 72, 250 * sAlpha) : C4(255, 170, 120, 250 * sAlpha);
                Color c2 = isHell ? C4(255, 220, 160, 248 * sAlpha) : C4(255, 234, 206, 242 * sAlpha);
                Color c3 = isHell ? C4(180, 30, 20, 200 * sAlpha) : C4(200, 90, 40, 200 * sAlpha);
                // Bulbous body
                Raylib.DrawEllipse((int)m.X, (int)m.Y, 8, 4.5f, c1);
                // Bright warhead tip
                DrawQuad(MP(6, -1.4f), MP(12, -0.6f), MP(12, 0.6f), MP(6, 1.4f), c2);
                DrawQuad(MP(12, -0.6f), MP(15, 0), MP(12, 0.6f), MP(12, -0.6f), c2);
                // Stub fins
                DrawQuad(MP(-6, -4.5f), MP(-5, -6.5f), MP(-7, -6f), MP(-8, -4.5f), c3);
                DrawQuad(MP(-6, 4.5f), MP(-5, 6.5f), MP(-7, 6f), MP(-8, 4.5f), c3);
                // Exhaust area
                DrawQuad(MP(-10, -1.5f), MP(-8, -2.5f), MP(-8, 2.5f), MP(-10, 1.5f), c3);
                // Fiery exhaust plume
                Raylib.BeginBlendMode(BlendMode.Additive);
                Color exC = isHell ? new Color((byte)255, (byte)180, (byte)80, (byte)(150 * sAlpha))
                    : new Color((byte)255, (byte)200, (byte)120, (byte)(130 * sAlpha));
                var exP = MP(-13, 0);
                Raylib.DrawEllipse((int)exP.X, (int)exP.Y, 4.5f, 2.2f, exC);
                Raylib.EndBlendMode();
            }
            else if (isShield)
            {
                // §5 4.2 shield drone — hexagonal bubble shimmer + hovering core.
                // The bubble is drawn at its true gameplay radius so cover reads.
                float bubbleR = VariantStats.Def("shield").ShieldRadius;
                float shim = 0.5f + 0.5f * MathF.Sin(s.Time * 2.4f + m.Id);
                float ripple = MathH.Clamp(m.ShieldFlashT * 4f, 0, 1);
                var center = new Vector2(m.X, m.Y);

                Raylib.BeginBlendMode(BlendMode.Additive);
                DrawGradientCircle(m.X, m.Y, bubbleR,
                    new Color((byte)96, (byte)178, (byte)255, (byte)(14 + shim * 8 + ripple * 46)));
                // Counter-rotating hex outlines = the shimmer
                Raylib.DrawPolyLinesEx(center, 6, bubbleR, s.Time * 9f + m.Id, 2.2f,
                    new Color((byte)136, (byte)208, (byte)255, (byte)(58 + shim * 42 + ripple * 130)));
                Raylib.DrawPolyLinesEx(center, 6, bubbleR * 0.94f, -s.Time * 6f + m.Id * 0.7f, 1.3f,
                    new Color((byte)110, (byte)190, (byte)255, (byte)(30 + shim * 26 + ripple * 80)));
                Raylib.EndBlendMode();

                // Core: hex emitter body + glowing heart
                Raylib.DrawPoly(center, 6, 8f, s.Time * 30f + m.Id, C4(58, 84, 120, 245));
                Raylib.DrawPolyLinesEx(center, 6, 8f, s.Time * 30f + m.Id, 1.6f, C4(150, 214, 255, 235));
                Raylib.BeginBlendMode(BlendMode.Additive);
                Raylib.DrawCircle((int)m.X, (int)m.Y, 4.5f + shim * 1.5f,
                    new Color((byte)140, (byte)220, (byte)255, (byte)(150 + ripple * 90)));
                Raylib.DrawCircle((int)m.X, (int)m.Y, 2.2f,
                    new Color((byte)230, (byte)250, (byte)255, (byte)230));
                Raylib.EndBlendMode();
            }
            else
            {
                // Standard / fast / zig / stealth / decoy / split / shard / heavy
                // Full missile shape: nosecone ? body tube ? fins ? engine nozzle ? exhaust
                bool isHeavy = m.Variant == "heavy";
                float bodyL = isHeavy ? 10f : 7f;  // body half-length
                float bodyH = isHeavy ? 3.5f : 2.5f; // body half-height
                float noseL = isHeavy ? 8f : 6f;  // nosecone length
                float finSpan = isHeavy ? 7f : 5.5f;

                // Body tube
                DrawQuad(MP(-bodyL, -bodyH), MP(bodyL, -bodyH), MP(bodyL, bodyH), MP(-bodyL, bodyH),
                    new Color(vc.R, vc.G, vc.B, (byte)(235 * sAlpha)));
                // Body highlight stripe
                DrawQuad(MP(-bodyL + 1, -bodyH * 0.3f), MP(bodyL - 1, -bodyH * 0.3f),
                    MP(bodyL - 1, bodyH * 0.3f), MP(-bodyL + 1, bodyH * 0.3f),
                    new Color((byte)MathH.Clamp(vc.R + 40, 0, 255), (byte)MathH.Clamp(vc.G + 40, 0, 255),
                        (byte)MathH.Clamp(vc.B + 30, 0, 255), (byte)(120 * sAlpha)));
                // Nosecone (tapered)
                DrawQuad(MP(bodyL, -bodyH), MP(bodyL + noseL * 0.6f, -bodyH * 0.4f),
                    MP(bodyL + noseL * 0.6f, bodyH * 0.4f), MP(bodyL, bodyH),
                    new Color((byte)MathH.Clamp(vc.R + 20, 0, 255), (byte)MathH.Clamp(vc.G + 20, 0, 255),
                        (byte)MathH.Clamp(vc.B + 15, 0, 255), (byte)(242 * sAlpha)));
                // Nosecone tip
                DrawQuad(MP(bodyL + noseL * 0.6f, -bodyH * 0.4f), MP(bodyL + noseL, 0),
                    MP(bodyL + noseL, 0), MP(bodyL + noseL * 0.6f, bodyH * 0.4f),
                    new Color((byte)255, (byte)240, (byte)205, (byte)(248 * sAlpha)));
                // Tail fins (upper + lower, swept back)
                DrawQuad(MP(-bodyL, -bodyH), MP(-bodyL + 2, -finSpan), MP(-bodyL - 2, -finSpan + 1), MP(-bodyL - 1, -bodyH),
                    new Color((byte)(vc.R * 0.7f), (byte)(vc.G * 0.7f), (byte)(vc.B * 0.7f), (byte)(220 * sAlpha)));
                DrawQuad(MP(-bodyL, bodyH), MP(-bodyL + 2, finSpan), MP(-bodyL - 2, finSpan - 1), MP(-bodyL - 1, bodyH),
                    new Color((byte)(vc.R * 0.7f), (byte)(vc.G * 0.7f), (byte)(vc.B * 0.7f), (byte)(220 * sAlpha)));
                // Engine nozzle
                DrawQuad(MP(-bodyL - 2, -bodyH * 0.7f), MP(-bodyL, -bodyH), MP(-bodyL, bodyH), MP(-bodyL - 2, bodyH * 0.7f),
                    new Color((byte)(vc.R * 0.5f), (byte)(vc.G * 0.5f), (byte)(vc.B * 0.5f), (byte)(230 * sAlpha)));
                // Exhaust glow
                Raylib.BeginBlendMode(BlendMode.Additive);
                var exhP = MP(-bodyL - 5, 0);
                float exhFlicker = 0.6f + MathF.Sin(s.Time * 16 + m.Id * 3.7f) * 0.3f;
                Raylib.DrawEllipse((int)exhP.X, (int)exhP.Y, 4f + (isHeavy ? 2 : 0), 2f,
                    new Color(vc.R, vc.G, vc.B, (byte)(exhFlicker * 120 * sAlpha)));
                Raylib.DrawEllipse((int)exhP.X, (int)exhP.Y, 2f, 1f,
                    new Color((byte)255, (byte)240, (byte)210, (byte)(exhFlicker * 80 * sAlpha)));
                Raylib.EndBlendMode();

                // Heavy gets additional warning ring glow
                if (isHeavy)
                {
                    Raylib.BeginBlendMode(BlendMode.Additive);
                    Raylib.DrawCircle((int)m.X, (int)m.Y, 9, new Color((byte)255, (byte)104, (byte)88, (byte)(100 * sAlpha)));
                    // §5 4.2 MIRV telegraph: pulsing red warning glow ≥0.5 s before the split
                    if (m.Mirv && m.TelegraphT > 0 && !m.HasSplit)
                    {
                        float wp = 0.5f + 0.5f * MathF.Sin(m.TelegraphT * 15f);
                        Raylib.DrawCircle((int)m.X, (int)m.Y, 13 + wp * 6,
                            new Color((byte)255, (byte)84, (byte)56, (byte)((40 + wp * 130) * sAlpha)));
                    }
                    Raylib.EndBlendMode();
                }
                // Bright core dot
                Raylib.DrawCircle((int)m.X, (int)m.Y, isHeavy ? 2f : 1.5f,
                    new Color((byte)255, (byte)248, (byte)230, (byte)(240 * sAlpha)));
            }

            // Exhaust trail (shield drone hovers — no exhaust)
            if (!isShield)
                DrawQuad(MP(-18, -1.1f), MP(-4, -1.1f), MP(-4, 1.1f), MP(-18, 1.1f),
                    new Color(vc.R, vc.G, vc.B, (byte)(100 * sAlpha)));

            DrawDamageFlash(m.X, m.Y, m.FlashT, isCarrier ? 18 : 10, isCarrier ? 12 : 10);
        }
    }

    // --- PLAYER MISSILES --- Arrowhead shapes
    static void DrawPlayerMissiles(GameState s)
    {
        foreach (var p in s.PlayerMissiles)
        {
            if (p.Detonated) continue;
            float vx = p._Vx, vy = p._Vy;
            float ang = MathF.Atan2(vy, vx);
            float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
            Vector2 PP(float lx, float ly) => new(p.X + ca * lx - sa * ly, p.Y + sa * lx + ca * ly);

            // Curved trail from position buffer
            if (p.Trail.Count > 1)
            {
                for (int ti = 0; ti < p.Trail.Count - 1; ti++)
                {
                    float al = (1 - (float)ti / p.Trail.Count) * 0.82f;
                    var a = p.Trail[ti]; var b = p.Trail[ti + 1];
                    Raylib.DrawLineEx(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), 2.6f,
                        new Color((byte)124, (byte)245, (byte)255, (byte)(al * 255)));
                }
            }

            // Glow
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawCircle((int)p.X, (int)p.Y, 14, new Color((byte)80, (byte)200, (byte)255, (byte)25));
            Raylib.EndBlendMode();

            // Arrowhead body
            DrawQuad(PP(12, 0), PP(-8, -3.2f), PP(-12, 0), PP(-8, 3.2f), new Color(162, 244, 255, 245));
            // Bright nosecone
            DrawQuad(PP(8, -1.2f), PP(14.5f, -1.2f), PP(14.5f, 1.2f), PP(8, 1.2f), new Color(214, 255, 255, 250));
            // Exhaust block
            DrawQuad(PP(-15, -1.2f), PP(-7.5f, -1.2f), PP(-7.5f, 1.2f), PP(-15, 1.2f), new Color(124, 245, 255, 205));
            // Exhaust glow
            var ep = PP(-17.5f, 0);
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawEllipse((int)ep.X, (int)ep.Y, 5, 2, new Color((byte)154, (byte)255, (byte)255, (byte)100));
            Raylib.EndBlendMode();
        }
    }

    // ?????????????? MUZZLE FLASHES (additive) ??????????????
    static void DrawMuzzleFlashes(GameState s)
    {
        foreach (var mf in s.MuzzleFlashes)
        {
            float t = mf.Life / mf.MaxLife;
            byte a = (byte)(t * 225);
            float sz = 15 + (1 - t) * 12;
            AddLight(mf.X, mf.Y, sz * 3.2f, 185, 228, 255, (byte)(t * 120)); // §5 5.2
            Raylib.DrawCircle((int)mf.X, (int)mf.Y, sz, new Color((byte)175, (byte)225, (byte)255, (byte)(a / 3)));
            Raylib.DrawCircle((int)mf.X, (int)mf.Y, sz * 0.45f, new Color((byte)215, (byte)248, (byte)255, (byte)(a / 2)));
        }
    }

    // ?????????????? EXPLOSIONS (additive) ??????????????
    static void DrawExplosions(GameState s)
    {
        foreach (var ex in s.Explosions)
        {
            if (ex.Radius <= 1) continue;
            float p = 1 - ex.Life / ex.MaxLife;
            float a = 1 - p;
            if (a < 0.01f) continue;

            float r = ex.Radius;

            // §5 5.2: light contribution. Fire light is warm regardless of the
            // blast's palette (the cyan player core still throws orange onto
            // the skyline — that's the acceptance shot); EMP is the exception:
            // a cyan center pool plus blobs riding the expanding ring front.
            // The light radius floors at 0.55·MaxRadius: ex.Radius grows from 0
            // while brightness (a) starts at 1, so without the floor a newborn
            // blast would throw no light exactly when it should strobe hardest.
            float lr = MathF.Max(r, ex.MaxRadius * 0.55f);
            if (ex.Emp)
            {
                AddLight(ex.X, ex.Y, lr * 1.5f, 110, 225, 255, (byte)(a * 70));
                for (int k = 0; k < 10; k++)
                {
                    float ang = k * (TAU / 10f);
                    AddLight(ex.X + MathF.Cos(ang) * r, ex.Y + MathF.Sin(ang) * r,
                        r * 0.55f, 120, 235, 255, (byte)(a * 80));
                }
            }
            else if (ex.Player)
                AddLight(ex.X, ex.Y, lr * 2.2f, 255, 186, 120, (byte)(a * 125));
            else
                AddLight(ex.X, ex.Y, lr * 2.2f, 255, 140, 64, (byte)(a * 140));

            if (ex.Player)
            {
                if (ex.Emp)
                {
                    // EMP: bright white-cyan center ? blue ? transparent (layered gradient textures)
                    DrawGradientCircle(ex.X, ex.Y, r, new Color((byte)32, (byte)78, (byte)152, (byte)(a * 0.4f * 255)));
                    DrawGradientCircle(ex.X, ex.Y, r * 0.75f, new Color((byte)70, (byte)175, (byte)255, (byte)(a * 0.55f * 255)));
                    DrawGradientCircle(ex.X, ex.Y, r * 0.45f, new Color((byte)126, (byte)244, (byte)255, (byte)(a * 0.7f * 255)));
                    DrawGradientCircle(ex.X, ex.Y, r * 0.25f, new Color((byte)210, (byte)248, (byte)255, (byte)(a * 0.95f * 255)));
                }
                else
                {
                    // Player: cyan center ? blue ? transparent
                    DrawGradientCircle(ex.X, ex.Y, r, new Color((byte)42, (byte)88, (byte)150, (byte)(a * 0.35f * 255)));
                    DrawGradientCircle(ex.X, ex.Y, r * 0.7f, new Color((byte)92, (byte)240, (byte)255, (byte)(a * 0.65f * 255)));
                    DrawGradientCircle(ex.X, ex.Y, r * 0.35f, new Color((byte)196, (byte)255, (byte)255, (byte)(a * 0.98f * 255)));
                }
            }
            else
            {
                // Enemy: warm center ? orange ? dark ? transparent
                DrawGradientCircle(ex.X, ex.Y, r, new Color((byte)90, (byte)25, (byte)15, (byte)(a * 0.35f * 255)));
                DrawGradientCircle(ex.X, ex.Y, r * 0.7f, new Color((byte)255, (byte)128, (byte)64, (byte)(a * 0.65f * 255)));
                DrawGradientCircle(ex.X, ex.Y, r * 0.35f, new Color((byte)255, (byte)224, (byte)172, (byte)(a * 0.98f * 255)));
            }

            // Outer ring with glow halo
            float ringA = (1 - p) * 0.65f;
            if (ringA > 0.02f)
            {
                Color ringC = ex.Emp ? new Color((byte)132, (byte)240, (byte)255, (byte)(ringA * 255))
                    : ex.Player ? new Color((byte)172, (byte)248, (byte)255, (byte)(ringA * 255))
                    : new Color((byte)255, (byte)182, (byte)120, (byte)(ringA * 255));
                float outerR = r * (1.05f + p * 0.35f);
                // Soft glow halo around ring (simulates shadowBlur)
                DrawGradientCircle(ex.X, ex.Y, outerR + 10, new Color(ringC.R, ringC.G, ringC.B, (byte)(ringA * 0.3f * 255)));
                Raylib.DrawCircleLinesV(new Vector2(ex.X, ex.Y), outerR, ringC);
                if (ex.Emp)
                    Raylib.DrawCircleLinesV(new Vector2(ex.X, ex.Y), r * 0.92f,
                        new Color((byte)140, (byte)242, (byte)255, (byte)(0.6f * (1 - p) * 255)));
            }
        }
    }

    // ?????????????? SPARKS (additive) ??????????????
    static void DrawSparks(GameState s)
    {
        foreach (var sp in s.Sparks)
        {
            float a = sp.Life / sp.MaxLife;
            if (a <= 0) continue;
            if (sp.Target)
            {
                // §5 3.5 scrap spark: motion streak + hot white-gold core
                Raylib.DrawLineEx(
                    new Vector2(sp.X - sp.Vx * 0.03f * a, sp.Y - sp.Vy * 0.03f * a),
                    new Vector2(sp.X, sp.Y), 1.6f,
                    new Color(sp.R, sp.G, sp.B, (byte)(a * 150)));
                Raylib.DrawCircle((int)sp.X, (int)sp.Y, sp.Size * 1.5f, new Color(sp.R, sp.G, sp.B, (byte)(a * 235)));
                Raylib.DrawCircle((int)sp.X, (int)sp.Y, sp.Size * 0.7f,
                    new Color((byte)255, (byte)246, (byte)205, (byte)(a * 245)));
                continue;
            }
            if (sp.Kind == SparkKind.Hot)
            {
                // §5 5.3 white-hot core spark: shrinks as it dies, slight streak
                Raylib.DrawLineEx(
                    new Vector2(sp.X - sp.Vx * 0.016f, sp.Y - sp.Vy * 0.016f),
                    new Vector2(sp.X, sp.Y), 1.4f,
                    new Color(sp.R, sp.G, sp.B, (byte)(a * 170)));
                Raylib.DrawCircle((int)sp.X, (int)sp.Y, sp.Size * (0.6f + 0.7f * a),
                    new Color(sp.R, sp.G, sp.B, (byte)(a * 245)));
                continue;
            }
            if (sp.Kind == SparkKind.Ember)
            {
                // §5 5.3 ember: warm flickering mote with a soft glow halo
                float fl = 0.72f + 0.28f * MathF.Sin(sp.Life * 26f + sp.Size * 7f);
                DrawGradientCircle(sp.X, sp.Y, sp.Size * 3f,
                    new Color(sp.R, sp.G, sp.B, (byte)(a * fl * 110)));
                Raylib.DrawCircle((int)sp.X, (int)sp.Y, sp.Size * 0.8f,
                    new Color((byte)255, (byte)226, (byte)170, (byte)(a * fl * 220)));
                continue;
            }
            Raylib.DrawCircle((int)sp.X, (int)sp.Y, sp.Size * 1.3f, new Color(sp.R, sp.G, sp.B, (byte)(a * 225)));
        }
    }

    // ?????????????? SHOCKWAVES (additive) ??????????????
    static void DrawShockwaves(GameState s)
    {
        foreach (var sw in s.Shockwaves)
        {
            float p = 1 - sw.Life / sw.MaxLife;
            float a = (1 - p) * 0.62f;
            if (a <= 0.01f) continue;
            byte al = (byte)(a * 255);
            Raylib.DrawCircleLinesV(new Vector2(sw.X, sw.Y), sw.Radius, new Color((byte)128, (byte)232, (byte)255, al));
            Raylib.DrawCircleLinesV(new Vector2(sw.X, sw.Y), sw.Radius * 0.8f, new Color((byte)82, (byte)195, (byte)255, (byte)(al / 2)));
        }
    }

    // ?????????????? DEBRIS ??????????????
    // §5 5.3 permanence: chunks never alpha out — Life is only the cooling
    // clock, lerping blast-hot orange back down to the city-palette tone.
    static void DrawDebris(GameState s)
    {
        foreach (var d in s.DebrisParts)
        {
            float heat = d.MaxLife > 0 ? MathH.Clamp(d.Life / d.MaxLife, 0, 1) : 0;
            byte r = (byte)(d.R + (255 - d.R) * heat);
            byte g = (byte)(d.G + (196 - d.G) * heat);
            byte b = (byte)(d.B + (120 - d.B) * heat);
            byte a = d.Resting ? (byte)200 : (byte)230;
            Raylib.DrawRectanglePro(new Rectangle(d.X, d.Y, d.Size * 2.2f, d.Size * 1.1f),
                new Vector2(d.Size * 1.1f, d.Size * 0.55f), d.Rot * 57.2958f,
                new Color(r, g, b, a));
        }
    }

    // ?????????????? BLAST FLASHES (additive) ??????????????
    /// <summary>§5 5.3: the 1-frame white detonation quad. Caller wraps in the
    /// shared explosion additive group.</summary>
    static void DrawBlastFlashes(GameState s)
    {
        // Flash reduction (§5 3.1 accessibility): the per-detonation white pop is
        // the same rapid bright-white flashing the full-screen flash is gated off
        // for — suppress it too. Gated at the draw site (not spawn) so the quads
        // still spawn identically and the cosmetic RNG stream stays in sync.
        if (s.Settings.FlashReduction) return;
        foreach (var bf in s.BlastFlashes)
        {
            float a = MathH.Clamp(bf.Life / Combat.BlastFlashLife, 0f, 1f);
            if (a <= 0.02f) continue;
            Raylib.DrawRectanglePro(new Rectangle(bf.X, bf.Y, bf.Size * 2f, bf.Size * 2f),
                new Vector2(bf.Size, bf.Size), bf.Rot,
                new Color((byte)255, (byte)255, (byte)255, (byte)(a * 235)));
        }
    }

    // ?????????????? SHOOTING STARS ??????????????
    static void DrawShootingStars(GameState s)
    {
        foreach (var ss in s.ShootingStars)
        {
            float a = ss.Life / ss.MaxLife;
            if (a <= 0) continue;
            byte al = (byte)(a * 215);
            float tx = ss.X - ss.Vx * 0.035f, ty = ss.Y - ss.Vy * 0.035f;
            Raylib.DrawLineEx(new Vector2(tx, ty), new Vector2(ss.X, ss.Y), 2f, new Color((byte)198, (byte)218, (byte)255, al));
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawCircle((int)ss.X, (int)ss.Y, 2.5f, new Color((byte)238, (byte)248, (byte)255, al));
            Raylib.EndBlendMode();
        }
    }

    // ?????????????? FLOATING TEXTS ??????????????
    static void DrawFloatingTexts(GameState s)
    {
        foreach (var ft in s.FloatingTexts)
        {
            float a = MathH.Clamp(ft.Life / 0.5f, 0, 1);
            byte al = (byte)(a * 255);
            bool combo = ft.Text.Contains("COMBO");
            int sz = combo ? 26 : 22;

            // Pop-in scale effect
            float scale = 1 + (1 - MathF.Min(1, ft.Life / 0.3f)) * 0.2f;
            int drawSz = (int)(sz * scale);
            int drawFw = MeasureTextM(ft.Text, drawSz);
            int tx = (int)(ft.X - drawFw * 0.5f);
            int ty = (int)ft.Y;

            // Black outline (4 offsets like HTML strokeText)
            var outCol = new Color((byte)0, (byte)0, (byte)0, (byte)(a * 0.7f * 255));
            DrawTextM(ft.Text, tx - 1, ty, drawSz, outCol);
            DrawTextM(ft.Text, tx + 1, ty, drawSz, outCol);
            DrawTextM(ft.Text, tx, ty - 1, drawSz, outCol);
            DrawTextM(ft.Text, tx, ty + 1, drawSz, outCol);

            // Themed fill
            var col = combo
                ? new Color((byte)255, (byte)200, (byte)100, al)
                : new Color((byte)255, (byte)255, (byte)255, al);
            DrawTextM(ft.Text, tx, ty, drawSz, col);
        }
    }

    // ?????????????? CROSSHAIR ??????????????
    static void DrawCrosshair(GameState s)
    {
        if (s.Phase != GamePhase.Playing && s.Phase != GamePhase.Shop) return;
        int mx = (int)s.MouseX, my = (int)s.MouseY;
        bool hot = false;
        foreach (var m in s.Enemies)
        {
            float dx = m.X - mx, dy = m.Y - my;
            if (dx * dx + dy * dy < 170 * 170) { hot = true; break; }
        }
        if (!hot)
            foreach (var u in s.UFOs)
            {
                float dx = u.X - mx, dy = u.Y - my;
                if (dx * dx + dy * dy < 190 * 190) { hot = true; break; }
            }
        if (!hot)
            foreach (var r in s.Raiders)
            {
                float dx = r.X - mx, dy = r.Y - my;
                if (dx * dx + dy * dy < 210 * 210) { hot = true; break; }
            }
        if (!hot && s.Demon != null)
        {
            float dx = s.Demon.X - mx, dy = s.Demon.Y - my;
            if (dx * dx + dy * dy < 240 * 240) hot = true;
        }

        // Scale-pop 1.3 → 1.0 on fire (pop² of the linear timer = ease-out)
        float pop = 1f + 0.3f * s.CrosshairPop * s.CrosshairPop;
        bool popped = pop > 1.001f;
        if (popped)
        {
            Rlgl.PushMatrix();
            Rlgl.Translatef(mx, my, 0);
            Rlgl.Scalef(pop, pop, 1);
            Rlgl.Translatef(-mx, -my, 0);
        }

        if (s.Theme == "recharged")
        {
            float pulse = 0.7f + MathF.Sin(s.Time * 6) * 0.3f;
            Color col = hot ? new Color((byte)255, (byte)0, (byte)100, (byte)(0.82f * pulse * 255))
                : new Color((byte)0, (byte)255, (byte)180, (byte)(0.8f * pulse * 255));
            float lw = hot ? 2.0f : 1.6f;
            Raylib.DrawLineEx(new Vector2(mx, my - 16), new Vector2(mx + 16, my), lw, col);
            Raylib.DrawLineEx(new Vector2(mx + 16, my), new Vector2(mx, my + 16), lw, col);
            Raylib.DrawLineEx(new Vector2(mx, my + 16), new Vector2(mx - 16, my), lw, col);
            Raylib.DrawLineEx(new Vector2(mx - 16, my), new Vector2(mx, my - 16), lw, col);
            Raylib.DrawLineEx(new Vector2(mx - 22, my), new Vector2(mx - 10, my), lw, col);
            Raylib.DrawLineEx(new Vector2(mx + 10, my), new Vector2(mx + 22, my), lw, col);
            Raylib.DrawLineEx(new Vector2(mx, my - 22), new Vector2(mx, my - 10), lw, col);
            Raylib.DrawLineEx(new Vector2(mx, my + 10), new Vector2(mx, my + 22), lw, col);
            Raylib.DrawCircle(mx, my, 1.5f, hot
                ? new Color((byte)255, (byte)0, (byte)100, (byte)255)
                : new Color((byte)0, (byte)255, (byte)180, (byte)255));
        }
        else if (s.Theme == "xbox")
        {
            Color col = hot ? new Color((byte)255, (byte)180, (byte)80, (byte)220)
                : new Color((byte)180, (byte)220, (byte)140, (byte)210);
            float lw = hot ? 2.0f : 1.6f;
            int sz = 16;
            Raylib.DrawLineEx(new Vector2(mx - sz, my - sz + 5), new Vector2(mx - sz, my - sz), lw, col);
            Raylib.DrawLineEx(new Vector2(mx - sz, my - sz), new Vector2(mx - sz + 5, my - sz), lw, col);
            Raylib.DrawLineEx(new Vector2(mx + sz - 5, my - sz), new Vector2(mx + sz, my - sz), lw, col);
            Raylib.DrawLineEx(new Vector2(mx + sz, my - sz), new Vector2(mx + sz, my - sz + 5), lw, col);
            Raylib.DrawLineEx(new Vector2(mx + sz, my + sz - 5), new Vector2(mx + sz, my + sz), lw, col);
            Raylib.DrawLineEx(new Vector2(mx + sz, my + sz), new Vector2(mx + sz - 5, my + sz), lw, col);
            Raylib.DrawLineEx(new Vector2(mx - sz + 5, my + sz), new Vector2(mx - sz, my + sz), lw, col);
            Raylib.DrawLineEx(new Vector2(mx - sz, my + sz), new Vector2(mx - sz, my + sz - 5), lw, col);
            Raylib.DrawLineEx(new Vector2(mx - 22, my), new Vector2(mx - 6, my), 1.0f, col);
            Raylib.DrawLineEx(new Vector2(mx + 6, my), new Vector2(mx + 22, my), 1.0f, col);
            Raylib.DrawLineEx(new Vector2(mx, my - 22), new Vector2(mx, my - 6), 1.0f, col);
            Raylib.DrawLineEx(new Vector2(mx, my + 6), new Vector2(mx, my + 22), 1.0f, col);
            Raylib.DrawCircle(mx, my, 1.8f, hot
                ? new Color((byte)255, (byte)180, (byte)80, (byte)255)
                : new Color((byte)180, (byte)220, (byte)140, (byte)255));
        }
        else
        {
            Color col = hot ? new Color((byte)255, (byte)156, (byte)96, (byte)210)
                : new Color((byte)130, (byte)236, (byte)255, (byte)205);
            float lw = hot ? 1.8f : 1.4f;
            Raylib.DrawCircleLinesV(new Vector2(mx, my), 14, col);
            Raylib.DrawLineEx(new Vector2(mx - 21, my), new Vector2(mx - 8, my), lw, col);
            Raylib.DrawLineEx(new Vector2(mx + 8, my), new Vector2(mx + 21, my), lw, col);
            Raylib.DrawLineEx(new Vector2(mx, my - 21), new Vector2(mx, my - 8), lw, col);
            Raylib.DrawLineEx(new Vector2(mx, my + 8), new Vector2(mx, my + 21), lw, col);
            Raylib.DrawCircleLinesV(new Vector2(mx, my), 8, new Color(col.R, col.G, col.B, (byte)MathH.Clamp(col.A + 20, 0, 255)));
            Raylib.DrawCircle(mx, my, 1.5f, hot
                ? new Color((byte)255, (byte)156, (byte)96, (byte)255)
                : new Color((byte)218, (byte)255, (byte)255, (byte)215));
        }

        if (popped) Rlgl.PopMatrix();

        // §5 4.4 combo ring — live decay arc around the crosshair; hidden at
        // combo 0 and outside active play
        if (s.Phase == GamePhase.Playing && s.Combo > 0) DrawComboRing(s, mx, my);
    }

    // §5 4.4 combo-ring geometry/feel constants
    const float ComboRingR = 27f;

    static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    /// <summary>§5 4.4 combo widget: DrawRing arc = remaining combo window,
    /// squash-stretch pop on each kill (ease-out-back settle via s.ComboPop —
    /// brief overshoot below 1 reads as the rebound), white→gold→red ramp with
    /// the chain, tremble in the final second. Chain count is a cached string.</summary>
    static void DrawComboRing(GameState s, int mx, int my)
    {
        float window = 4f + s.Perks.ComboTimeBonus; // matches Combat.RegKill
        float frac = MathH.Clamp(s.ComboTimer / window, 0f, 1f);

        // White (fresh) → gold (≈×6) → red (≈×14+)
        float t1 = MathH.Clamp((s.Combo - 1) / 5f, 0f, 1f);
        float t2 = MathH.Clamp((s.Combo - 6) / 8f, 0f, 1f);
        byte cg = (byte)MathH.Lerp(MathH.Lerp(255, 214, t1), 80, t2);
        byte cb = (byte)MathH.Lerp(MathH.Lerp(255, 90, t1), 60, t2);
        var col = new Color((byte)255, cg, cb, (byte)215);
        var faint = new Color((byte)255, cg, cb, (byte)36);

        float cx = mx, cy = my;
        if (s.ComboTimer < 1f) // tremble as the window runs out
        {
            float amp = (1f - s.ComboTimer) * 2.6f;
            float ph = FeelDirector.Clock * 43f;
            cx += MathF.Sin(ph) * amp;
            cy += MathF.Cos(ph * 1.31f) * amp;
        }

        // Squash-stretch pop: x stretches while y squashes, settling through a
        // slight ease-out-back undershoot
        float e = 1f - EaseOutBack(1f - s.ComboPop);
        bool scaled = MathF.Abs(e) > 0.001f;
        if (scaled)
        {
            Rlgl.PushMatrix();
            Rlgl.Translatef(cx, cy, 0);
            Rlgl.Scalef(1f + 0.45f * e, 1f - 0.25f * e, 1);
            Rlgl.Translatef(-cx, -cy, 0);
        }
        var center = new Vector2(cx, cy);
        Raylib.DrawRing(center, ComboRingR - 2.2f, ComboRingR, 0f, 360f, 40, faint);
        Raylib.DrawRing(center, ComboRingR - 2.6f, ComboRingR + 0.4f,
            -90f, -90f + 360f * frac, 40, col);
        if (scaled) Rlgl.PopMatrix();

        // Chain multiplier under the ring (CacheSb dialect — no per-frame alloc)
        string txt = HudX(ref _ringCombo, s.Combo);
        DrawTextM(txt, cx - MeasureTextM(txt, 13, true) * 0.5f, cy + ComboRingR + 5f, 13, col, true);
    }

    // ?????????????? POST-FX ??????????????
    // Legacy CPU post chain — kept ONLY as the fallback when the 1.4 uber-shader
    // failed to compile (vignette/scanlines/flash/danger/grain live in the shader).
    static void DrawPostFx(GameState s)
    {
        if (!_postShaderActive)
        {
            float vignetteAlpha = 0.42f;
            float vignetteRadius = MathF.Max(s.W, s.H) * 0.85f;
            Raylib.DrawCircleGradient((int)(s.W * 0.5f), (int)(s.H * 0.5f), vignetteRadius,
                new Color((byte)0, (byte)0, (byte)0, (byte)0), new Color((byte)0, (byte)0, (byte)0, (byte)(vignetteAlpha * 255)));

            float scanAlpha = 0.06f + s.Danger * 0.05f;
            byte sa = (byte)(scanAlpha * 255);
            for (int y = 0; y < (int)s.H; y += 3)
                Raylib.DrawLine(0, y, (int)s.W, y, new Color((byte)106, (byte)181, (byte)255, sa));

            if (s.Flash > 0.01f && !s.Settings.FlashReduction)
            {
                Raylib.BeginBlendMode(BlendMode.Additive);
                byte fa = (byte)(MathH.Clamp(s.Flash * 1.1f, 0, 1) * 255);
                Raylib.DrawRectangle(0, 0, (int)s.W, (int)s.H, new Color((byte)160, (byte)218, (byte)255, fa));
                Raylib.DrawRectangleGradientV(0, (int)s.HorizonY, (int)s.W, (int)(s.H - s.HorizonY),
                    new Color((byte)255, (byte)142, (byte)106, (byte)(s.Flash * 216)),
                    new Color((byte)255, (byte)142, (byte)106, (byte)0));
                Raylib.EndBlendMode();
            }

            if (s.Danger > 0.55f)
            {
                Raylib.BeginBlendMode(BlendMode.Additive);
                byte da = (byte)((s.Danger - 0.55f) * 56);
                Raylib.DrawRectangle(0, 0, (int)s.W, (int)s.H, new Color((byte)255, (byte)80, (byte)60, da));
                Raylib.EndBlendMode();
            }
        }

        // Flash reduction (§5 3.1 accessibility): the full-screen flash (shader
        // uniform / CPU rect, both gated off above) renders as an additive edge
        // vignette pulse instead — same color and decay, none of the white-out.
        if (s.Settings.FlashReduction && s.Flash > 0.01f)
        {
            float fa = MathH.Clamp(s.Flash * 1.1f, 0f, 1f);
            int ew = (int)(MathF.Min(s.W, s.H) * 0.12f);
            var inner = new Color((byte)160, (byte)218, (byte)255, (byte)(fa * 150));
            var outer = new Color((byte)160, (byte)218, (byte)255, (byte)0);
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawRectangleGradientH(0, 0, ew, (int)s.H, inner, outer);
            Raylib.DrawRectangleGradientH((int)s.W - ew, 0, ew, (int)s.H, outer, inner);
            Raylib.DrawRectangleGradientV(0, 0, (int)s.W, ew, inner, outer);
            Raylib.DrawRectangleGradientV(0, (int)s.H - ew, (int)s.W, ew, outer, inner);
            Raylib.EndBlendMode();
        }

        // Weather ash tint is not part of the shader's uniform contract — always CPU.
        if (s.Weather.Mode == "ash" && s.Weather.Intensity > 0)
        {
            byte wa = (byte)(MathH.Clamp(s.Weather.Intensity * 0.08f, 0, 0.12f) * 255);
            Raylib.DrawRectangle(0, 0, (int)s.W, (int)s.H, new Color((byte)164, (byte)110, (byte)70, wa));
        }

        if (!_postShaderActive) DrawGrain(s);
        // Legacy additive CA double-draw removed — feature 1.4 consumes
        // s.Chromatic as the intensity uniform for true shader RGB-split.
    }

    static void DrawGrain(GameState s)
    {
        if (!_grainReady) return;

        float alpha = 0.01f + s.Danger * 0.008f;
        if (s.Weather.Mode == "storm") alpha += s.Weather.Intensity * 0.008f;
        if (alpha <= 0.003f) return;

        int ox = (int)(MathF.Sin(s.Time * 12) * 6);
        int oy = (int)(MathF.Cos(s.Time * 10) * 6);
        byte ga = (byte)(MathH.Clamp(alpha, 0, 0.04f) * 255);
        for (int y = -GrainSize; y < s.H + GrainSize; y += GrainSize)
        for (int x = -GrainSize; x < s.W + GrainSize; x += GrainSize)
            Raylib.DrawTexture(_grainTexture, x + ox, y + oy, new Color((byte)255, (byte)255, (byte)255, ga));
    }

    // HUD string caches (§5 2.6): DrawHUD runs every gameplay frame — each value
    // is composed into the shared StringBuilder (alloc-free) and re-materialized
    // as a string only when the content actually changed.
    static readonly System.Text.StringBuilder _hudSb = new(96);
    static string _hudWave = "", _hudScrap = "", _hudCombo = "", _hudMax = "",
        _hudVol = "", _hudCities = "", _hudEmp = "", _hudAmmo = "", _hudHost = "",
        _hudUfo = "", _hudRaider = "", _hudPend = "", _hudBases = "", _hudPhx = "",
        _hudHr = "", _hudThreat = "", _hudWx = "", _hudUp = "", _ringCombo = "";

    // §5 4.4 odometer digit strings — cached once; the draw path never composes
    static readonly string[] _odoDigits = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"];

    // Re-materialize the cached string only if the freshly built _hudSb differs.
    static string CacheSb(ref string cache)
    {
        var sb = _hudSb;
        bool same = sb.Length == cache.Length;
        if (same)
            for (int i = 0; i < cache.Length; i++)
                if (sb[i] != cache[i]) { same = false; break; }
        if (!same) cache = sb.ToString();
        return cache;
    }

    static string HudInt(ref string cache, int v)
    {
        _hudSb.Clear();
        _hudSb.Append(v);
        return CacheSb(ref cache);
    }

    static string HudX(ref string cache, int v)
    {
        _hudSb.Clear();
        _hudSb.Append('x').Append(v);
        return CacheSb(ref cache);
    }

    // Alloc-free stand-in for text.ToUpperInvariant() in HUD composition.
    static void AppendUpper(System.Text.StringBuilder sb, string text)
    {
        for (int i = 0; i < text.Length; i++) sb.Append(char.ToUpperInvariant(text[i]));
    }

    // Modern HUD — rounded panel, bottom-left, clears the HellRaiser silo at center-bottom.
    static void DrawHUD(GameState s)
    {
        if (s.Phase == GamePhase.Title) return;
        // UI scale (§5 3.1) multiplies HUD font sizes; panel tracks the text
        float ui = s.Settings.UiScale;
        float fs = 14f * ui;
        float lineH = fs + 8;
        int lines = 5;
        int panelW = (int)MathF.Min(640f * ui, s.W * 0.5f * MathF.Max(1f, ui));
        int panelH = (int)(lines * lineH + 20);
        int panelX = 12;
        int panelY = (int)(s.H - panelH - 8); // flush to bottom

        Color bg;
        Color border;
        Color dim;
        Color accent;
        Color warn = new Color((byte)255, (byte)110, (byte)90, (byte)255);
        switch (s.Theme)
        {
            case "xbox":
                bg = new Color((byte)16, (byte)26, (byte)16, (byte)210);
                border = new Color((byte)200, (byte)230, (byte)200, (byte)150);
                dim = new Color((byte)168, (byte)188, (byte)168, (byte)220);
                accent = new Color((byte)240, (byte)252, (byte)240, (byte)255);
                break;
            case "recharged":
                bg = new Color((byte)6, (byte)2, (byte)2, (byte)220);
                border = new Color((byte)255, (byte)102, (byte)40, (byte)220);
                dim = new Color((byte)255, (byte)190, (byte)150, (byte)220);
                accent = new Color((byte)255, (byte)255, (byte)255, (byte)255);
                break;
            default:
                bg = new Color((byte)6, (byte)10, (byte)20, (byte)215);
                border = new Color((byte)120, (byte)220, (byte)255, (byte)170);
                dim = new Color((byte)180, (byte)210, (byte)250, (byte)195);
                accent = new Color((byte)120, (byte)240, (byte)255, (byte)255);
                break;
        }

        // Rounded panel with subtle inner glow
        var panelRect = new Rectangle(panelX, panelY, panelW, panelH);
        Raylib.DrawRectangleRounded(panelRect, 0.14f, 10, bg);
        // Soft inner glow halo (additive)
        Raylib.BeginBlendMode(BlendMode.Additive);
        Raylib.DrawRectangleRounded(
            new Rectangle(panelX - 3, panelY - 3, panelW + 6, panelH + 6),
            0.16f, 10, new Color(border.R, border.G, border.B, (byte)28));
        Raylib.EndBlendMode();
        Raylib.DrawRectangleRoundedLinesEx(panelRect, 0.14f, 10, 1.5f, border);
        // Top accent strip
        Raylib.DrawRectangle(panelX + 14, panelY + 3, 42, 2, new Color(accent.R, accent.G, accent.B, (byte)200));
        Raylib.DrawRectangle(panelX + 62, panelY + 3, 14, 2, new Color(accent.R, accent.G, accent.B, (byte)120));

        int citiesAlive = s.AliveCities; // cached count (§5 2.6) — no per-frame LINQ
        int pending = Math.Max(0, s.WavePlan.Count - s.SpawnI);
        int ufoCount = s.UFOs.Count;
        int raiderCount = s.Raiders.Count;
        int hostiles = s.Enemies.Count + ufoCount + raiderCount + (s.Demon != null ? 1 : 0);
        int ammoLeft = 0;
        for (int i = 0; i < s.Bases.Count; i++)
            if (!s.Bases[i].Destroyed) ammoLeft += s.Bases[i].Ammo;
        for (int i = 0; i < s.Phalanxes.Count; i++)
            if (!s.Phalanxes[i].Destroyed) ammoLeft += s.Phalanxes[i].Ammo;

        var sb = _hudSb;
        sb.Clear();
        for (int i = 0; i < s.Bases.Count; i++)
        {
            var b = s.Bases[i];
            if (i > 0) sb.Append("  ");
            sb.Append(b.Id).Append(':');
            if (b.Destroyed) sb.Append('X'); else sb.Append(b.Ammo);
        }
        string ammo = CacheSb(ref _hudBases);

        string ph = "--";
        if (s.Phalanxes.Count > 0)
        {
            sb.Clear();
            for (int i = 0; i < s.Phalanxes.Count; i++)
            {
                var p = s.Phalanxes[i];
                if (i > 0) sb.Append("  ");
                sb.Append(p.Id switch
                {
                    "PHALANX_L" => "L",
                    "PHALANX_R" => "R",
                    _ => p.Id
                }).Append(':');
                if (p.Destroyed) sb.Append('X'); else sb.Append(p.Ammo);
            }
            ph = CacheSb(ref _hudPhx);
        }

        sb.Clear();
        sb.Append("HR ");
        if (s.HellRaiser == null) sb.Append("--");
        else if (s.HellRaiser.Destroyed) sb.Append('X');
        else { AppendUpper(sb, s.HellRaiser.State); sb.Append(' ').Append(s.HellRaiser.Ammo); }
        string hrStr = CacheSb(ref _hudHr);

        sb.Clear();
        if (SynthAudio.IsMuted) sb.Append("MUTED");
        else sb.Append(MathF.Round(SynthAudio.Volume * 100)).Append('%');
        string volText = CacheSb(ref _hudVol);

        sb.Clear();
        AppendUpper(sb, s.Weather.Mode);
        sb.Append(' ').Append(MathF.Round(s.Weather.Intensity * 100)).Append('%');
        string weather = CacheSb(ref _hudWx);

        sb.Clear();
        sb.Append($"YLD x{s.Upgrades.BlastScale:F1}  RLD x{s.Upgrades.ReloadMult:F2}  EMP x{s.Upgrades.EmpScale:F1}");
        string up = CacheSb(ref _hudUp);

        int bars = 14;
        int fill = (int)MathF.Round(s.Danger * bars);
        sb.Clear();
        sb.Append('[').Append('#', fill).Append('-', bars - fill).Append("] ")
          .Append(MathF.Round(s.Danger * 100)).Append('%');
        string threat = CacheSb(ref _hudThreat);

        float y = panelY + 10;
        float x = panelX + 14;

        void DrawSep(ref float xPos)
        {
            DrawTextM("·", xPos, y, fs, new Color((byte)140, (byte)150, (byte)170, (byte)150));
            xPos += MeasureTextM("·", fs) + 10;
        }
        void DrawSegment(ref float xPos, string label, string value, Color valueCol)
        {
            DrawTextM(label, xPos, y, fs, dim);
            xPos += MeasureTextM(label, fs) + 5;
            DrawTextM(value, xPos, y, fs, valueCol, true);
            xPos += MeasureTextM(value, fs, true) + 12;
        }

        // §5 3.5: scrap currency identity color (shared with the shop panel)
        Color gold = new Color((byte)255, (byte)214, (byte)90, (byte)255);

        DrawSegment(ref x, "WAVE", HudInt(ref _hudWave, s.Level), accent);
        DrawSep(ref x);
        // §5 4.4 score odometer — label like DrawSegment, value as rolling digits
        DrawTextM("SCORE", x, y, fs, dim);
        x += MeasureTextM("SCORE", fs) + 5;
        x = DrawScoreOdometer(s, x, y, fs, accent) + 12;
        DrawSep(ref x);
        float scrapX0 = x;
        DrawSegment(ref x, "SCRAP", HudInt(ref _hudScrap, s.Scrap), gold);
        // Live counter anchor — homing scrap sparks magnet-stream to this point
        s.ScrapHudX = (scrapX0 + x) * 0.5f;
        s.ScrapHudY = y + fs * 0.5f;
        DrawSep(ref x);
        DrawSegment(ref x, "COMBO", HudX(ref _hudCombo, Math.Max(1, s.Combo)), s.Combo > 2 ? accent : dim);
        DrawSep(ref x);
        DrawSegment(ref x, "MAX", HudX(ref _hudMax, Math.Max(1, s.MaxCombo)), accent);
        DrawSep(ref x);
        DrawSegment(ref x, "VOL", volText, accent);

        y += lineH;
        x = panelX + 14;
        DrawSegment(ref x, "CITIES", HudInt(ref _hudCities, citiesAlive), citiesAlive <= 2 ? warn : accent);
        DrawSep(ref x);
        DrawSegment(ref x, "EMP", HudInt(ref _hudEmp, s.Emp), s.Emp > 0 ? accent : dim);
        DrawSep(ref x);
        DrawSegment(ref x, "AMMO", HudInt(ref _hudAmmo, ammoLeft), ammoLeft > 0 ? accent : dim);
        DrawSep(ref x);
        DrawSegment(ref x, "MODE", s.Auto ? "AUTO" : "MANUAL", s.Auto ? accent : dim);

        y += lineH;
        x = panelX + 14;
        DrawSegment(ref x, "HOST", HudInt(ref _hudHost, hostiles), accent);
        DrawSep(ref x);
        DrawSegment(ref x, "UFO", HudInt(ref _hudUfo, ufoCount), accent);
        DrawSep(ref x);
        DrawSegment(ref x, "RAIDER", HudInt(ref _hudRaider, raiderCount), accent);
        DrawSep(ref x);
        DrawSegment(ref x, "PEND", HudInt(ref _hudPend, pending), accent);

        y += lineH;
        x = panelX + 14;
        DrawSegment(ref x, "BASES", ammo.Length == 0 ? "--" : ammo, accent);
        DrawSep(ref x);
        DrawSegment(ref x, "PHX", ph, accent);

        y += lineH;
        x = panelX + 14;
        DrawSegment(ref x, "THREAT", threat, s.Danger > 0.66f ? warn : accent);
        DrawSep(ref x);
        DrawSegment(ref x, "WX", weather, accent);

        // Upgrades + HR on far right column (small row above panel)
        float uy = panelY - lineH - 2;
        DrawTextM(up, panelX + 14, uy, fs, dim);
        int hrW = MeasureTextM(hrStr, fs);
        DrawTextM(hrStr, panelX + panelW - hrW - 14, uy, fs, accent, true);

        if (s.Debug.Enabled)
        {
            string dbg = $"FPS {Raylib.GetFPS()}  E{s.Enemies.Count} P{s.PlayerMissiles.Count} X{s.Explosions.Count}";
            int dw = MeasureTextM(dbg, fs);
            DrawTextM(dbg, panelX + panelW - dw - 14, panelY - lineH * 2 - 2, fs, dim);

            var ec = s.Debug.EventCounts;
            string ev = $"EV K{ec[(int)EventKind.Kill]} CD{ec[(int)EventKind.CityDestroyed]}"
                + $" BD{ec[(int)EventKind.BaseDestroyed]} WS{ec[(int)EventKind.WaveStart]}"
                + $" WC{ec[(int)EventKind.WaveCleared]} EMP{ec[(int)EventKind.Emp]}"
                + $" GI{ec[(int)EventKind.GroundImpact]}";
            int ew = MeasureTextM(ev, fs);
            DrawTextM(ev, panelX + panelW - ew - 14, panelY - lineH * 3 - 2, fs, dim);
        }
    }

    /// <summary>§5 4.4 per-digit rolling score. Wheel i shows digit d_i scrolled
    /// up toward d_i+1 only while every lower wheel wraps (classic odometer
    /// cascade: roll = max(0, lower − (10^i − 1)) ∈ [0,1)). Rolling cells clip
    /// through BeginScissorMode; static digits skip the scissor entirely, so an
    /// idle score costs zero extra batch flushes and a full roll stays inside
    /// the plan's ~14-flush budget. Cached digit strings — zero alloc. Returns
    /// the cursor x past the last cell.</summary>
    static float DrawScoreOdometer(GameState s, float x, float y, float fs, Color col)
    {
        double v = s.DisplayScore < 0 ? 0 : s.DisplayScore;
        int top = Math.Max(s.Score, (int)v);
        int nDigits = 1;
        for (int t = top; t >= 10; t /= 10) nDigits++;
        float cellW = MeasureTextM("8", fs, true) + 1f;
        int scissorW = (int)MathF.Ceiling(cellW);
        int scissorH = (int)(fs + 2f);
        double scale = 1;
        for (int i = 1; i < nDigits; i++) scale *= 10;
        for (int i = nDigits - 1; i >= 0; i--)
        {
            int d = (int)(v / scale) % 10;
            double lower = v % scale; // value carried by all lower wheels
            float roll = (float)Math.Max(0.0, lower - (scale - 1));
            if (roll > 0.002f && roll < 0.998f)
            {
                Raylib.BeginScissorMode((int)x, (int)(y - 1f), scissorW, scissorH);
                DrawTextM(_odoDigits[d], x, y - roll * fs, fs, col, true);
                DrawTextM(_odoDigits[(d + 1) % 10], x, y + (1f - roll) * fs, fs, col, true);
                Raylib.EndScissorMode();
            }
            else
            {
                DrawTextM(_odoDigits[roll >= 0.998f ? (d + 1) % 10 : d], x, y, fs, col, true);
            }
            x += cellW;
            scale /= 10;
        }
        return x;
    }

    // ?????????????? OVERLAYS ??????????????
    static void DrawOverlays(GameState s)
    {
        if (s.Phase == GamePhase.Title)
        {
            Raylib.DrawRectangle(0, 0, (int)s.W, (int)s.H, new Color((byte)0, (byte)0, (byte)0, (byte)192));
            var t = "MISSILE COMMAND OVERDRIVE";
            int tw = MeasureTextM(t, 44, true);
            Raylib.DrawRectangleRounded(
                new Rectangle(s.W / 2 - tw / 2 - 28, s.H / 2 - 58, tw + 56, 72),
                0.2f, 10, new Color((byte)0, (byte)28, (byte)48, (byte)140));
            DrawTextM(t, s.W / 2 - tw / 2, s.H / 2 - 46, 44, new Color((byte)120, (byte)240, (byte)255, (byte)255), true);
            var sub = "Click to Start";
            int sw = MeasureTextM(sub, 24);
            float p = 0.38f + 0.62f * MathF.Sin(s.Time * 3.2f);
            DrawTextM(sub, s.W / 2 - sw / 2, s.H / 2 + 28, 24, new Color((byte)198, (byte)228, (byte)255, (byte)(108 + p * 148)));
            var hint = "LMB: Fire   RMB/E: EMP   C: Auto   H: HellRaiser   T: Theme   R: Restart";
            int hw = MeasureTextM(hint, 15);
            DrawTextM(hint, s.W / 2 - hw / 2, s.H / 2 + 82, 15, new Color((byte)138, (byte)158, (byte)192, (byte)170));
            var hint2 = "D: Daily Seed Run";
            int hw2 = MeasureTextM(hint2, 15);
            DrawTextM(hint2, s.W / 2 - hw2 / 2, s.H / 2 + 104, 15, new Color((byte)138, (byte)158, (byte)192, (byte)170));
        }
        if (s.Phase == GamePhase.GameOver)
        {
            DrawGameOver(s);
        }
        if (s.Phase == GamePhase.Shop)
        {
            DrawShopPanel(s);
        }
        float msgFs = 28f * s.Settings.UiScale;
        float noteFs = 17f * s.Settings.UiScale;
        if (s.MsgT > 0 && s.Msg.Length > 0)
        {
            byte a = (byte)(MathH.Clamp(s.MsgT, 0, 1) * 255);
            int mw = MeasureTextM(s.Msg, msgFs, true);
            DrawTextM(s.Msg, s.W / 2 - mw / 2, s.H * 0.18f, msgFs, new Color((byte)120, (byte)240, (byte)255, a), true);
        }
        if (s.NoteT > 0 && s.Note.Length > 0)
        {
            byte a = (byte)(MathH.Clamp(s.NoteT, 0, 1) * 230);
            int nw = MeasureTextM(s.Note, noteFs);
            DrawTextM(s.Note, s.W / 2 - nw / 2, s.H - 44, noteFs, new Color((byte)188, (byte)218, (byte)242, a));
        }
        // Pause menu last — it dims the frozen world AND everything above
        if (s.Phase == GamePhase.Paused) DrawPauseMenu(s);
    }

    // Game-over screen (§5 3.2): initials ceremony + top-10 + seed. Table/seed
    // strings are cached in Profile (rebuilt on profile events only); the
    // score/combo line uses the CacheSb dialect — zero steady-state allocation.
    const string GoTitle = "GAME OVER";
    const string GoNewHighScore = "NEW HIGH SCORE — ENTER INITIALS";
    const string GoInitialsHint = "UP/DOWN LETTER   LEFT/RIGHT SLOT   ENTER CONFIRM";
    const string GoTableHeader = " #  INI     SCORE  WAV  CMB"; // column-aligned with Profile.BuildRow
    const string GoRestartHint = "Press R to Restart";
    static string _goStats = "";

    static void DrawGameOver(GameState s)
    {
        byte oa = (byte)MathH.Clamp(s.GameOverTime * 108, 0, 190);
        Raylib.DrawRectangle(0, 0, (int)s.W, (int)s.H, new Color((byte)0, (byte)0, (byte)0, oa));
        float cx = s.W * 0.5f;
        float topY = s.H * 0.10f;

        int gow = MeasureTextM(GoTitle, 52, true);
        float p = 0.62f + 0.38f * MathF.Sin(s.Time * 2.2f);
        DrawTextM(GoTitle, cx - gow / 2, topY, 52, new Color((byte)255, (byte)90, (byte)70, (byte)(p * 255)), true);

        _hudSb.Clear();
        _hudSb.Append("SCORE ").Append(s.Score).Append("    MAX COMBO x").Append(s.MaxCombo);
        string stats = CacheSb(ref _goStats);
        int stw = MeasureTextM(stats, 24);
        DrawTextM(stats, cx - stw / 2, topY + 62, 24, new Color((byte)198, (byte)228, (byte)255, (byte)235));

        if (s.GameOverTime <= 0.6f) return; // table/ceremony fade in after the slam

        float y = topY + 108;
        if (Profile.PendingInitials)
        {
            int hsw = MeasureTextM(GoNewHighScore, 22, true);
            float hp = 0.55f + 0.45f * MathF.Sin(s.Time * 4.4f);
            DrawTextM(GoNewHighScore, cx - hsw / 2, y, 22, new Color((byte)255, (byte)214, (byte)90, (byte)(hp * 255)), true);
            y += 38;

            // Three rotating A-Z slots, arcade style
            const float slotW = 44, slotH = 54, gap = 12;
            float sx0 = cx - (slotW * 3 + gap * 2) * 0.5f;
            for (int i = 0; i < 3; i++)
            {
                bool sel = i == Profile.SlotSel;
                var rect = new Rectangle(sx0 + i * (slotW + gap), y, slotW, slotH);
                Raylib.DrawRectangleRounded(rect, 0.18f, 6, new Color((byte)8, (byte)18, (byte)34, (byte)225));
                Raylib.DrawRectangleRoundedLinesEx(rect, 0.18f, 6, sel ? 2.4f : 1.2f,
                    sel ? new Color((byte)120, (byte)240, (byte)255, (byte)255)
                        : new Color((byte)90, (byte)130, (byte)180, (byte)190));
                string letter = Profile.Letter(Profile.Slots[i]); // pre-baked, no alloc
                int lw = MeasureTextM(letter, 34, true);
                float roll = sel ? Profile.RollDir * Profile.RollT * 9f : 0f;
                DrawTextM(letter, rect.X + slotW * 0.5f - lw * 0.5f, rect.Y + 11f + roll, 34,
                    new Color((byte)234, (byte)245, (byte)255, (byte)(sel ? 255 : 215)), true);
                if (sel)
                {
                    int aw = MeasureTextM("^", 15, true);
                    DrawTextM("^", rect.X + slotW * 0.5f - aw * 0.5f, rect.Y - 18, 15,
                        new Color((byte)120, (byte)240, (byte)255, (byte)220), true);
                    int vw = MeasureTextM("v", 15, true);
                    DrawTextM("v", rect.X + slotW * 0.5f - vw * 0.5f, rect.Y + slotH + 4, 15,
                        new Color((byte)120, (byte)240, (byte)255, (byte)220), true);
                }
            }
            y += slotH + 26;

            int ihw = MeasureTextM(GoInitialsHint, 14);
            DrawTextM(GoInitialsHint, cx - ihw / 2, y, 14, new Color((byte)138, (byte)168, (byte)202, (byte)200));
            y += 32;
        }

        // Top-10 table (lines cached in Profile)
        if (Profile.TableCount > 0)
        {
            const float rowH = 21f;
            int thw = MeasureTextM(GoTableHeader, 17, true);
            float tx = cx - thw / 2f;
            DrawTextM(GoTableHeader, tx, y, 17, new Color((byte)120, (byte)240, (byte)255, (byte)230), true);
            y += rowH + 3;
            for (int i = 0; i < Profile.TableCount; i++)
            {
                bool hl = i == Profile.PendingIndex;
                DrawTextM(Profile.TableText[i], tx, y, 17,
                    hl ? new Color((byte)255, (byte)214, (byte)90, (byte)255)
                       : new Color((byte)172, (byte)202, (byte)232, (byte)215), hl);
                y += rowH;
            }
            y += 10;
        }

        // Seed readout — same seed ⇒ same wave plans (daily runs: D on the title)
        if (Profile.SeedText.Length > 0)
        {
            int sw = MeasureTextM(Profile.SeedText, 16);
            DrawTextM(Profile.SeedText, cx - sw / 2, y, 16, new Color((byte)140, (byte)232, (byte)255, (byte)225));
            y += 30;
        }

        if (!Profile.PendingInitials && s.GameOverTime > 1.5f)
        {
            int rw = MeasureTextM(GoRestartHint, 20);
            float rp = 0.38f + 0.62f * MathF.Sin(s.Time * 3.2f);
            DrawTextM(GoRestartHint, cx - rw / 2, y, 20, new Color((byte)120, (byte)240, (byte)255, (byte)(rp * 230)));
        }
    }

    // Pause/settings menu (§5 3.1) — HUD aesthetic, zero per-frame allocation:
    // labels are constants, value strings are cached in Menu (rebuilt on key
    // presses only).
    const string PauseTitle = "PAUSED";
    const string PauseHint = "UP/DOWN SELECT   LEFT/RIGHT ADJUST   ENTER ACTIVATE   ESC RESUME";

    static void DrawPauseMenu(GameState s)
    {
        // Full-screen dim — the frozen world (and HUD) reads through it
        Raylib.DrawRectangle(0, 0, (int)s.W, (int)s.H, new Color((byte)0, (byte)0, (byte)0, (byte)150));

        Color bg, border, dim, accent;
        switch (s.Theme)
        {
            case "xbox":
                bg = new Color((byte)16, (byte)26, (byte)16, (byte)232);
                border = new Color((byte)200, (byte)230, (byte)200, (byte)170);
                dim = new Color((byte)168, (byte)188, (byte)168, (byte)220);
                accent = new Color((byte)240, (byte)252, (byte)240, (byte)255);
                break;
            case "recharged":
                bg = new Color((byte)6, (byte)2, (byte)2, (byte)238);
                border = new Color((byte)255, (byte)102, (byte)40, (byte)220);
                dim = new Color((byte)255, (byte)190, (byte)150, (byte)220);
                accent = new Color((byte)255, (byte)255, (byte)255, (byte)255);
                break;
            default:
                bg = new Color((byte)6, (byte)10, (byte)20, (byte)235);
                border = new Color((byte)120, (byte)220, (byte)255, (byte)190);
                dim = new Color((byte)180, (byte)210, (byte)250, (byte)195);
                accent = new Color((byte)120, (byte)240, (byte)255, (byte)255);
                break;
        }

        float ui = s.Settings.UiScale;
        float fs = 17f * ui;
        float rowH = fs + 13f;
        float panelW = MathF.Min(s.W - 40, 560f * ui);
        float headerH = 66f * ui;
        float footerH = 44f * ui;
        float panelH = headerH + Menu.ItemCount * rowH + footerH;
        float px = s.W * 0.5f - panelW * 0.5f;
        float py = MathF.Max(16, s.H * 0.5f - panelH * 0.5f);

        // Rounded glass panel + glow + border (same dialect as HUD/shop)
        var rect = new Rectangle(px, py, panelW, panelH);
        Raylib.DrawRectangleRounded(rect, 0.05f, 10, bg);
        Raylib.BeginBlendMode(BlendMode.Additive);
        Raylib.DrawRectangleRounded(new Rectangle(px - 4, py - 4, panelW + 8, panelH + 8),
            0.05f, 10, new Color(border.R, border.G, border.B, (byte)24));
        Raylib.EndBlendMode();
        Raylib.DrawRectangleRoundedLinesEx(rect, 0.05f, 10, 2f, border);

        // Title
        float titleFs = 30f * ui;
        int tw = MeasureTextM(PauseTitle, titleFs, true);
        DrawTextM(PauseTitle, px + panelW * 0.5f - tw * 0.5f, py + 16f * ui, titleFs, accent, true);

        // Items: label left, cached value right-aligned, selection row highlighted
        float rowY = py + headerH;
        for (int i = 0; i < Menu.ItemCount; i++)
        {
            bool sel = i == Menu.Sel;
            if (sel)
            {
                Raylib.DrawRectangleRounded(
                    new Rectangle(px + 12, rowY - 4, panelW - 24, rowH),
                    0.4f, 6, new Color(border.R, border.G, border.B, (byte)42));
                DrawTextM(">", px + 18, rowY, fs, accent, true);
            }
            DrawTextM(Menu.Labels[i], px + 36, rowY, fs, sel ? accent : dim, sel);
            string v = Menu.Values[i];
            if (v.Length > 0)
            {
                int vw = MeasureTextM(v, fs, sel);
                DrawTextM(v, px + panelW - 28 - vw, rowY, fs, sel ? accent : dim, sel);
            }
            rowY += rowH;
        }

        // Hint
        float hintFs = 13f * ui;
        int hw = MeasureTextM(PauseHint, hintFs);
        DrawTextM(PauseHint, px + panelW * 0.5f - hw * 0.5f, py + panelH - footerH + 12f * ui, hintFs, dim);
    }

    static void DrawShopPanel(GameState s)
    {
        float panelW = MathF.Min(s.W * 0.72f, 820);
        float panelH = MathF.Min(s.H * 0.86f, 600); // taller: armory draft row (§5 4.3)
        float px = s.W * 0.5f - panelW * 0.5f;
        float py = MathF.Max(24, s.H * 0.09f);

        // Full-screen dim
        Raylib.DrawRectangle(0, 0, (int)s.W, (int)s.H, new Color((byte)0, (byte)0, (byte)0, (byte)140));

        // Rounded glass panel + glow
        var rect = new Rectangle(px, py, panelW, panelH);
        Raylib.DrawRectangleRounded(rect, 0.06f, 10, new Color((byte)10, (byte)16, (byte)35, (byte)230));
        Raylib.BeginBlendMode(BlendMode.Additive);
        Raylib.DrawRectangleRounded(new Rectangle(px - 4, py - 4, panelW + 8, panelH + 8),
            0.06f, 10, new Color((byte)120, (byte)220, (byte)255, (byte)24));
        Raylib.EndBlendMode();
        Raylib.DrawRectangleRoundedLinesEx(rect, 0.06f, 10, 2f,
            new Color((byte)130, (byte)210, (byte)255, (byte)210));

        // Title
        string title = $"WAVE {s.Level} COMPLETE — STRATEGY LINK";
        int tw = MeasureTextM(title, 30, true);
        DrawTextM(title, px + panelW * 0.5f - tw * 0.5f, py + 22, 30,
            new Color((byte)234, (byte)245, (byte)255, (byte)255), true);

        // Funds (§5 3.5: the shop spends SCRAP — Score is leaderboard-pure)
        string funds = $"SCRAP RESERVE:  {s.Scrap}";
        int fw = MeasureTextM(funds, 22, true);
        DrawTextM(funds, px + panelW * 0.5f - fw * 0.5f, py + 60, 22,
            new Color((byte)255, (byte)214, (byte)90, (byte)255), true);

        // Free-repair crew status (§5 3.5: earned every 3 cleared waves, banked)
        int repairIn = Math.Max(0, 3 - s.Upgrades.WavesSinceFreeRepair);
        string crew = repairIn == 0
            ? "FIELD REPAIR CREW STANDING BY"
            : $"FIELD REPAIR CREW READY IN {repairIn} WAVE{(repairIn == 1 ? "" : "S")}";
        int cw = MeasureTextM(crew, 14);
        DrawTextM(crew, px + panelW * 0.5f - cw * 0.5f, py + 86, 14,
            new Color((byte)190, (byte)185, (byte)150, (byte)200));

        // §5 4.1/4.3: draft cards left, intel forecast column right; the scrap
        // buys (now keys 4-0) sit under the cards
        float intelW = MathF.Min(212f, panelW * 0.3f);
        float draftY = py + 108;
        DrawShopDraft(s, px + 28, draftY, panelW - intelW - 72);
        DrawShopIntel(s, px + panelW - intelW - 28, draftY, intelW);

        // Upgrade buttons (§5 4.3: remapped to 4-0 — the draft owns 1-3)
        var items = new (int cost, string label, bool enabled)[]
        {
            (500, "4. REBUILD CITY",    s.Scrap >= 500 && s.Cities.Any(c => c.Destroyed)),
            (250, "5. BUY EMP",         s.Scrap >= 250 && s.Emp < s.EmpMax),
            (400, $"6. WARHEAD YIELD  [x{s.Upgrades.BlastScale:F1} → x{MathF.Min(2.8f, s.Upgrades.BlastScale + 0.2f):F1}]",
                s.Scrap >= 400 && s.Upgrades.BlastScale < 2.8f - 0.001f),
            (350, $"7. RELOAD BOOST   [x{s.Upgrades.ReloadMult:F2} → x{MathF.Min(2.2f, s.Upgrades.ReloadMult + 0.12f):F2}]",
                s.Scrap >= 350 && s.Upgrades.ReloadMult < 2.2f - 0.001f),
            (360, $"8. EMP AMPLIFIER  [x{s.Upgrades.EmpScale:F2} → x{MathF.Min(2.4f, s.Upgrades.EmpScale + 0.14f):F2}]",
                s.Scrap >= 360 && s.Upgrades.EmpScale < 2.4f - 0.001f),
            (300, "9. REPAIR BASE",     s.Scrap >= 300 && s.Bases.Any(b => b.Destroyed)),
            (250, "0. REPAIR PHALANX",  s.Scrap >= 250 && s.Phalanxes.Any(p => p.Destroyed)),
        };

        float btnW = MathF.Min(panelW - intelW - 92, 440);
        float btnH = 30; // 7 rows + draft row must clear the 600 px panel
        float btnX = px + (panelW - intelW - 56 - btnW) * 0.5f + 28;
        float btnY = py + 248;
        for (int i = 0; i < items.Length; i++)
        {
            var (cost, label, enabled) = items[i];
            var bgCol = enabled
                ? new Color((byte)40, (byte)92, (byte)150, (byte)220)
                : new Color((byte)30, (byte)36, (byte)50, (byte)200);
            var txCol = enabled
                ? new Color((byte)255, (byte)255, (byte)255, (byte)255)
                : new Color((byte)150, (byte)150, (byte)160, (byte)200);
            var brCol = enabled
                ? new Color((byte)130, (byte)210, (byte)255, (byte)230)
                : new Color((byte)90, (byte)100, (byte)120, (byte)180);
            var br = new Rectangle(btnX, btnY + i * (btnH + 6), btnW, btnH);
            Raylib.DrawRectangleRounded(br, 0.22f, 8, bgCol);
            Raylib.DrawRectangleRoundedLinesEx(br, 0.22f, 8, 1.3f, brCol);

            string priced = $"{label}    [{cost}]";
            int lw = MeasureTextM(priced, 17, true);
            DrawTextM(priced, br.X + br.Width * 0.5f - lw * 0.5f, br.Y + br.Height * 0.5f - 9, 17, txCol, true);
        }

        // Timer
        float timerY = btnY + items.Length * (btnH + 6) + 10;
        string t = $"COMMENCING NEXT WAVE IN {MathF.Max(0, MathF.Ceiling(s.ShopTimer))}s";
        int tww = MeasureTextM(t, 22, true);
        DrawTextM(t, px + panelW * 0.5f - tww * 0.5f, timerY, 22,
            new Color((byte)189, (byte)244, (byte)255, (byte)255), true);

        // Hint
        int hw2 = MeasureTextM(ShopHint, 15);
        DrawTextM(ShopHint, px + panelW * 0.5f - hw2 * 0.5f, timerY + 30, 15,
            new Color((byte)140, (byte)232, (byte)255, (byte)210));
    }

    // §5 4.3 draft card chrome — constant strings only (cached-string discipline:
    // names/descs live on the Perk records, everything else is a literal)
    const string ShopHint = "1-3 INSTALL PERK / R REROLL / 4-0 PURCHASE / SPACE LAUNCH";
    const string DraftHeader = "ARMORY DRAFT - INSTALL ONE PERK";
    const string DraftRerollReady = "R: REROLL [150]";
    const string DraftRerollUsed = "REROLL EXPENDED";
    const string DraftInstalled = "INSTALLED";
    const string DraftEmpty = "OUT OF\nSTOCK";
    static readonly string[] DraftKeys = ["1", "2", "3"];
    static readonly string[] RarityLabels = ["COMMON", "RARE", "EPIC"]; // indexed by Rarity

    static Color RarityColor(Rarity r) => r switch
    {
        Rarity.Epic => new Color((byte)236, (byte)128, (byte)255, (byte)255),
        Rarity.Rare => new Color((byte)255, (byte)214, (byte)90, (byte)255),
        _ => new Color((byte)140, (byte)220, (byte)255, (byte)255)
    };

    /// <summary>§5 4.3 perk draft: three rarity-colored cards (keys 1-3) + the
    /// reroll status. Renders allocation-free — all strings are constants or
    /// pre-built Perk record fields.</summary>
    static void DrawShopDraft(GameState s, float dx, float dy, float dw)
    {
        Color head = new Color((byte)130, (byte)210, (byte)255, (byte)235);
        Color dim = new Color((byte)170, (byte)195, (byte)230, (byte)215);
        Color bright = new Color((byte)234, (byte)245, (byte)255, (byte)255);
        DrawTextM(DraftHeader, dx, dy, 15, head, true);

        // Reroll status, right-aligned on the header row
        if (s.DraftPicked < 0)
        {
            string rr = s.DraftRerolled ? DraftRerollUsed : DraftRerollReady;
            bool can = !s.DraftRerolled && s.Scrap >= PerkSystem.RerollCost
                && (s.Draft[0] != null || s.Draft[1] != null || s.Draft[2] != null);
            int rw = MeasureTextM(rr, 13, can);
            DrawTextM(rr, dx + dw - rw, dy + 1, 13,
                can ? new Color((byte)255, (byte)214, (byte)90, (byte)255)
                    : new Color((byte)150, (byte)150, (byte)160, (byte)200), can);
        }

        float cardW = (dw - 20) / 3f;
        const float cardH = 92f;
        float cy = dy + 22;
        for (int i = 0; i < 3; i++)
        {
            float cx = dx + i * (cardW + 10);
            var rect = new Rectangle(cx, cy, cardW, cardH);
            var card = s.Draft[i];
            bool installed = s.DraftPicked == i;
            bool dead = s.DraftPicked >= 0 && !installed; // a sibling was picked

            var rc = card != null ? RarityColor(card.Rarity)
                : new Color((byte)90, (byte)100, (byte)120, (byte)180);
            var border = dead ? new Color(rc.R, rc.G, rc.B, (byte)70) : rc;
            var bg = installed
                ? new Color((byte)20, (byte)44, (byte)40, (byte)235)
                : dead
                    ? new Color((byte)12, (byte)16, (byte)26, (byte)200)
                    : new Color((byte)14, (byte)24, (byte)44, (byte)235);
            Raylib.DrawRectangleRounded(rect, 0.12f, 8, bg);
            Raylib.DrawRectangleRoundedLinesEx(rect, 0.12f, 8, installed ? 2.2f : 1.3f, border);

            if (card == null)
            {
                int ew = MeasureTextM(DraftEmpty, 12);
                DrawTextM(DraftEmpty, cx + cardW * 0.5f - ew * 0.5f, cy + 34, 12, dim);
                continue;
            }

            var txDim = dead ? new Color(dim.R, dim.G, dim.B, (byte)110) : dim;
            var txBright = dead ? new Color(bright.R, bright.G, bright.B, (byte)120) : bright;
            var txRare = dead ? new Color(rc.R, rc.G, rc.B, (byte)110) : rc;

            DrawTextM(DraftKeys[i], cx + 8, cy + 6, 13, txBright, true);
            string rl = RarityLabels[(int)card.Rarity];
            int rlw = MeasureTextM(rl, 11, true);
            DrawTextM(rl, cx + cardW - rlw - 8, cy + 7, 11, txRare, true);

            int nw = MeasureTextM(card.Name, 13, true);
            DrawTextM(card.Name, cx + cardW * 0.5f - nw * 0.5f, cy + 26, 13, txBright, true);
            DrawTextM(card.Desc, cx + 10, cy + 45, 11, txDim);

            if (installed)
            {
                int iw2 = MeasureTextM(DraftInstalled, 12, true);
                DrawTextM(DraftInstalled, cx + cardW * 0.5f - iw2 * 0.5f, cy + cardH - 17, 12,
                    new Color((byte)120, (byte)255, (byte)170, (byte)255), true);
            }
        }
    }

    // §5 4.1 intel forecast caches — rebuilt only when the pinned plan OBJECT
    // changes (once per shop-open), so the panel draws allocation-free while
    // open (cached-string discipline; the rebuild itself is a shop-path alloc).
    static readonly string[] IntelVariants =
        ["standard", "fast", "zig", "stealth", "decoy", "split", "cruise", "drone", "spit", "heavy", "carrier", "shield"];
    static readonly string[] IntelLabels =
        ["STANDARD", "FAST", "ZIG", "STEALTH", "DECOY", "SPLIT", "CRUISE", "DRONE", "SPIT", "HEAVY", "CARRIER", "SHIELD"];
    static List<WavePlanEntry>? _intelPlanRef;
    static readonly int[] _intelCounts = new int[IntelVariants.Length];
    static readonly string[] _intelCountStr = new string[IntelVariants.Length];
    static int _intelMirvCount;
    static string _intelMirvStr = "";
    static string _intelThreatStr = "";
    static float _intelThreatFrac;

    static void BuildIntelCache(GameState s)
    {
        var plan = s.PinnedPlan;
        if (ReferenceEquals(plan, _intelPlanRef)) return;
        _intelPlanRef = plan;
        Array.Clear(_intelCounts);
        _intelMirvCount = 0;
        int threat = 0;
        if (plan == null) return;
        for (int i = 0; i < plan.Count; i++)
        {
            var e = plan[i];
            for (int v = 0; v < IntelVariants.Length; v++)
                if (e.Variant == IntelVariants[v]) { _intelCounts[v]++; break; }
            // Threat meter = the director's own per-variant budget prices;
            // MIRV-tagged heavies carry a premium (3 extra shards inbound)
            threat += WaveSystem.ThreatOf(e.Variant);
            if (e.Mirv) { _intelMirvCount++; threat += 8; }
        }
        var sb = _hudSb;
        for (int v = 0; v < IntelVariants.Length; v++)
        {
            sb.Clear();
            sb.Append('x').Append(_intelCounts[v]);
            _intelCountStr[v] = sb.ToString();
        }
        sb.Clear();
        sb.Append("MIRV TAGGED x").Append(_intelMirvCount);
        _intelMirvStr = sb.ToString();
        sb.Clear();
        sb.Append("THREAT ").Append(threat);
        _intelThreatStr = sb.ToString();
        // Fixed scale (director budget ≈ 140 + 50·level, capped near level 21)
        _intelThreatFrac = MathH.Clamp(threat / 1200f, 0f, 1f);
    }

    /// <summary>§5 4.1 shop intel: per-variant icon counts + threat meter for the
    /// PINNED next-wave plan (§4.3 — the same object StartWave will execute).</summary>
    static void DrawShopIntel(GameState s, float ix, float iy, float iw)
    {
        BuildIntelCache(s);
        Color head = new Color((byte)130, (byte)210, (byte)255, (byte)235);
        Color dim = new Color((byte)170, (byte)195, (byte)230, (byte)215);
        Color warn = new Color((byte)255, (byte)110, (byte)90, (byte)255);
        DrawTextM("NEXT WAVE INTEL", ix, iy, 15, head, true);
        float y = iy + 24;
        if (_intelPlanRef == null)
        {
            DrawTextM("NO TELEMETRY", ix, y, 13, dim);
            return;
        }
        for (int v = 0; v < IntelVariants.Length; v++)
        {
            if (_intelCounts[v] == 0) continue;
            var vc = Palette.VariantColor(IntelVariants[v]);
            // warhead icon: small down-pointing triangle in the track color
            Raylib.DrawPoly(new Vector2(ix + 6, y + 7), 3, 6f, 90f, vc);
            DrawTextM(IntelLabels[v], ix + 18, y, 13, dim);
            int cwid = MeasureTextM(_intelCountStr[v], 13, true);
            DrawTextM(_intelCountStr[v], ix + iw - cwid, y, 13,
                new Color((byte)234, (byte)245, (byte)255, (byte)255), true);
            y += 19;
        }
        if (_intelMirvCount > 0)
        {
            y += 3;
            DrawTextM(_intelMirvStr, ix, y, 13, warn, true);
            y += 19;
        }
        // Threat meter — sum of the plan's per-variant threat prices
        y += 8;
        Color meterCol = _intelThreatFrac < 0.34f
            ? new Color((byte)120, (byte)240, (byte)255, (byte)255)
            : _intelThreatFrac < 0.67f
                ? new Color((byte)255, (byte)214, (byte)90, (byte)255)
                : warn;
        DrawTextM(_intelThreatStr, ix, y, 13, meterCol, true);
        y += 19;
        float barW = iw - 2;
        Raylib.DrawRectangle((int)ix, (int)y, (int)barW, 9,
            new Color((byte)20, (byte)30, (byte)50, (byte)230));
        Raylib.DrawRectangle((int)ix, (int)y, (int)(barW * _intelThreatFrac), 9, meterCol);
        Raylib.DrawRectangleLines((int)ix, (int)y, (int)barW, 9,
            new Color((byte)130, (byte)210, (byte)255, (byte)160));
    }
}
