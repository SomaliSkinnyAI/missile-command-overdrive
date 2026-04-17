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
    const float BloomScale = 0.25f;
    const int GrainSize = 160;
    const int GradientSize = 128;
    const int MoonSize = 192;
    const int BlurPasses = 3;

    static RenderTexture2D _frameTarget;
    static RenderTexture2D _bloomTarget;
    static RenderTexture2D _bloomPing; // ping-pong blur
    static Texture2D _grainTexture;
    static Texture2D _gradientTex; // smooth radial gradient: white center ? transparent edge
    static Texture2D _moonTex; // procedural moon with maria + craters + terminator shading
    static bool _fxReady;
    static bool _grainReady;
    static bool _gradientReady;
    static bool _moonReady;

    // Moon disc mask for star-occlusion (matches HTML: stars inside the moon circle are skipped)
    static float _moonMaskX, _moonMaskY, _moonMaskR;
    static bool _moonMaskActive;

    // Modern font (Segoe UI TTF) — replaces raylib's blocky bitmap default
    static Font _uiFont;
    static Font _uiFontBold;
    static bool _uiFontReady;

    public static Font UiFont => _uiFontReady ? _uiFont : Raylib.GetFontDefault();
    public static Font UiFontBold => _uiFontReady ? _uiFontBold : Raylib.GetFontDefault();

    /// <summary>Modern text draw (TrueType via LoadFontEx). Falls back to bitmap if font failed to load.</summary>
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
            Raylib.UnloadRenderTexture(_frameTarget);
            Raylib.UnloadRenderTexture(_bloomTarget);
            Raylib.UnloadRenderTexture(_bloomPing);
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

        if (_uiFontReady)
        {
            Raylib.UnloadFont(_uiFont);
            Raylib.UnloadFont(_uiFontBold);
            _uiFontReady = false;
        }
    }

    static void RenderBloomPass(GameState s)
    {
        if (!_fxReady || !_gradientReady || s.Theme == "recharged") return;

        // 1. Render bright elements to bloom target using gradient texture
        Raylib.BeginTextureMode(_bloomTarget);
        Raylib.ClearBackground(new Color((byte)0, (byte)0, (byte)0, (byte)0));
        Raylib.BeginBlendMode(BlendMode.Additive);

        float scale = BloomScale;
        foreach (var e in s.Explosions)
        {
            float elapsed = e.MaxLife - e.Life;
            float a = e.Life / MathF.Max(0.001f, e.MaxLife);
            if (a <= 0) continue;
            float r = e.Radius * (e.Emp ? 1.18f : 1f) * scale;
            float x = e.X * scale;
            float y = e.Y * scale;
            Color col = e.Player
                ? new Color((byte)180, (byte)250, (byte)255, (byte)(a * 180))
                : new Color((byte)255, (byte)208, (byte)136, (byte)(a * 170));
            DrawGradientCircle(x, y, r, col);
        }

        foreach (var t in s.Trails)
        {
            float a = t.Life / MathF.Max(0.001f, t.MaxLife);
            if (a <= 0) continue;
            float r = (2.1f + t.Size * 1.6f) * scale;
            float x = t.X * scale;
            float y = t.Y * scale;
            var col = new Color(t.R, t.G, t.B, (byte)(a * 140));
            DrawGradientCircle(x, y, r, col);
        }

        // Moon glow → bloom
        var (phase, day, _, _) = SkyCycle(s.Time);
        float moonTrack = phase;
        float moonArc = MathF.Sin(moonTrack * MathF.PI);
        float mmx = MathH.Lerp(-s.W * 0.12f, s.W * 1.12f, moonTrack);
        float mmy = MathH.Lerp(s.HorizonY * 0.95f, s.HorizonY * 0.2f, moonArc) + MathF.Cos(moonTrack * TAU) * 6;
        float mmr = MathF.Max(35, s.W * 0.0336f);
        float moonVisRaw = MathH.Clamp((0.5f - day) / 0.22f, 0, 1);
        float moonVisS = moonVisRaw * moonVisRaw * (3 - 2 * moonVisRaw);
        float mmA = moonVisS * 0.92f * MathH.Clamp((moonArc + 0.06f) / 1.06f, 0, 1);
        if (mmA > 0.02f)
        {
            DrawGradientCircle(mmx * scale, mmy * scale, mmr * 2.8f * scale,
                new Color((byte)170, (byte)210, (byte)255, (byte)(mmA * 95)));
            DrawGradientCircle(mmx * scale, mmy * scale, mmr * 1.5f * scale,
                new Color((byte)220, (byte)240, (byte)255, (byte)(mmA * 150)));
        }

        // Sun corona → bloom (day)
        float sunTrack = (moonTrack + 0.02f) % 1f;
        float sunArc = MathF.Sin(sunTrack * MathF.PI);
        float ssx = MathH.Lerp(-s.W * 0.12f, s.W * 1.12f, sunTrack);
        float ssy = MathH.Lerp(s.HorizonY * 0.95f, s.HorizonY * 0.24f, sunArc) + MathF.Cos(sunTrack * TAU) * 5;
        float ssr = MathF.Max(26, s.W * 0.024f);
        float sunVisRaw = MathH.Clamp((day - 0.58f) / 0.22f, 0, 1);
        float sunVisS = sunVisRaw * sunVisRaw * (3 - 2 * sunVisRaw);
        float sunA = sunVisS * MathH.Clamp((sunArc + 0.08f) / 1.08f, 0, 1);
        if (sunA > 0.02f)
        {
            DrawGradientCircle(ssx * scale, ssy * scale, ssr * 3.0f * scale,
                new Color((byte)255, (byte)210, (byte)150, (byte)(sunA * 130)));
        }

        // City antenna warning lights → bloom (cheap, big dreamy impact)
        foreach (var city in s.Cities)
        {
            if (city.Destroyed) continue;
            float cx = city.X - city.W * 0.5f;
            float cy = city.Y;
            var rng = new Random(city.Id.GetHashCode());
            int n = 10 + rng.Next(6);
            float slice = city.W / n;
            for (int i = 0; i < n; i++)
            {
                float bw = slice * (0.55f + rng.NextSingle() * 0.9f);
                float bh = 32 + rng.NextSingle() * 74;
                float bx = cx + i * slice + (slice - bw) * 0.5f;
                rng.Next(18); rng.Next(22); rng.Next(36); rng.Next(4);
                rng.NextSingle();
                bool hasStep = rng.NextSingle() < 0.35f;
                if (hasStep) rng.NextSingle();
                bool hasSpire = rng.NextSingle() < 0.28f;
                if (hasSpire) rng.NextSingle();
                bool hasAnt = rng.NextSingle() < 0.42f;
                float antH = hasAnt ? 6 + rng.NextSingle() * 14 : 0f;
                int antCol = rng.Next(3);
                rng.Next();
                if (antH > 0)
                {
                    float sxp = bx + bw * 0.5f;
                    float syp = cy - bh - antH;
                    float bl = 0.4f + 0.6f * MathF.Max(0f, MathF.Sin(s.Time * 5f + i * 1.7f));
                    byte lr = 255, lg = 50, lb = 50;
                    if (antCol == 1) { lr = 255; lg = 255; lb = 100; }
                    else if (antCol == 2) { lr = 120; lg = 200; lb = 255; }
                    DrawGradientCircle(sxp * scale, syp * scale, 14f * scale,
                        new Color(lr, lg, lb, (byte)(bl * 180)));
                }
            }
        }

        Raylib.EndBlendMode();
        Raylib.EndTextureMode();

        // 2. Multi-pass box blur via ping-pong between _bloomTarget and _bloomPing
        BlurBloomTarget(s);
    }

    /// <summary>Separable 5-tap Gaussian blur (ping-pong) — proper weighted convolution for soft dreamy glow.</summary>
    static void BlurBloomTarget(GameState s)
    {
        int bw = _bloomTarget.Texture.Width;
        int bh = _bloomTarget.Texture.Height;
        float blurAmount = 8 + s.Danger * 14 + MathH.Clamp(s.Flash * 24, 0, 18);
        float step = MathF.Max(1.2f, blurAmount * BloomScale * 0.48f);

        // Gaussian weights (sigma ~1.6, radius 2): [0.0702, 0.2448, 0.3900, 0.2448, 0.0702]
        byte w0 = (byte)(0.3900f * 255); // center
        byte w1 = (byte)(0.2448f * 255); // ±1
        byte w2 = (byte)(0.0702f * 255); // ±2

        var src = new Rectangle(0, 0, bw, -bh);
        var dst = new Rectangle(0, 0, bw, bh);

        for (int pass = 0; pass < BlurPasses; pass++)
        {
            float off1 = step * (1 + pass * 0.8f);
            float off2 = off1 * 2f;

            // Horizontal: _bloomTarget -> _bloomPing
            Raylib.BeginTextureMode(_bloomPing);
            Raylib.ClearBackground(new Color((byte)0, (byte)0, (byte)0, (byte)0));
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawTexturePro(_bloomTarget.Texture, src, dst, Vector2.Zero, 0,
                new Color((byte)255, (byte)255, (byte)255, w0));
            Raylib.DrawTexturePro(_bloomTarget.Texture, src,
                new Rectangle(-off1, 0, bw, bh), Vector2.Zero, 0,
                new Color((byte)255, (byte)255, (byte)255, w1));
            Raylib.DrawTexturePro(_bloomTarget.Texture, src,
                new Rectangle(off1, 0, bw, bh), Vector2.Zero, 0,
                new Color((byte)255, (byte)255, (byte)255, w1));
            Raylib.DrawTexturePro(_bloomTarget.Texture, src,
                new Rectangle(-off2, 0, bw, bh), Vector2.Zero, 0,
                new Color((byte)255, (byte)255, (byte)255, w2));
            Raylib.DrawTexturePro(_bloomTarget.Texture, src,
                new Rectangle(off2, 0, bw, bh), Vector2.Zero, 0,
                new Color((byte)255, (byte)255, (byte)255, w2));
            Raylib.EndBlendMode();
            Raylib.EndTextureMode();

            // Vertical: _bloomPing -> _bloomTarget
            Raylib.BeginTextureMode(_bloomTarget);
            Raylib.ClearBackground(new Color((byte)0, (byte)0, (byte)0, (byte)0));
            Raylib.BeginBlendMode(BlendMode.Additive);
            Raylib.DrawTexturePro(_bloomPing.Texture, src, dst, Vector2.Zero, 0,
                new Color((byte)255, (byte)255, (byte)255, w0));
            Raylib.DrawTexturePro(_bloomPing.Texture, src,
                new Rectangle(0, -off1, bw, bh), Vector2.Zero, 0,
                new Color((byte)255, (byte)255, (byte)255, w1));
            Raylib.DrawTexturePro(_bloomPing.Texture, src,
                new Rectangle(0, off1, bw, bh), Vector2.Zero, 0,
                new Color((byte)255, (byte)255, (byte)255, w1));
            Raylib.DrawTexturePro(_bloomPing.Texture, src,
                new Rectangle(0, -off2, bw, bh), Vector2.Zero, 0,
                new Color((byte)255, (byte)255, (byte)255, w2));
            Raylib.DrawTexturePro(_bloomPing.Texture, src,
                new Rectangle(0, off2, bw, bh), Vector2.Zero, 0,
                new Color((byte)255, (byte)255, (byte)255, w2));
            Raylib.EndBlendMode();
            Raylib.EndTextureMode();
        }
    }

    static void DrawBloomOverlay(GameState s)
    {
        if (!_fxReady || s.Theme == "recharged") return;

        float baseAlpha = 0.45f + s.Danger * 0.28f + MathH.Clamp(s.Flash * 0.22f, 0, 0.16f);
        if (baseAlpha <= 0.01f) return;

        var tex = _bloomTarget.Texture;
        var src = new Rectangle(0, 0, tex.Width, -tex.Height);
        var dst = new Rectangle(0, 0, s.W, s.H);
        Raylib.BeginBlendMode(BlendMode.Additive);
        // Sharper pass (full alpha)
        Raylib.DrawTexturePro(tex, src, dst, Vector2.Zero, 0,
            new Color((byte)255, (byte)255, (byte)255, (byte)(baseAlpha * 255)));
        // Wider spread pass — bigger dreamy halo
        float spread = 16 + s.Danger * 10;
        Raylib.DrawTexturePro(tex, src,
            new Rectangle(-spread, -spread, s.W + spread * 2, s.H + spread * 2),
            Vector2.Zero, 0,
            new Color((byte)255, (byte)255, (byte)255, (byte)(baseAlpha * 0.78f * 255)));
        // Extra-wide ultra-soft pass
        float spread2 = spread * 2.2f;
        Raylib.DrawTexturePro(tex, src,
            new Rectangle(-spread2, -spread2, s.W + spread2 * 2, s.H + spread2 * 2),
            Vector2.Zero, 0,
            new Color((byte)255, (byte)255, (byte)255, (byte)(baseAlpha * 0.42f * 255)));
        Raylib.EndBlendMode();
    }

    public static void DrawAll(GameState s)
    {
        EnsureFxTargets(s);
        RenderBloomPass(s);

        Raylib.BeginTextureMode(_frameTarget);
        Raylib.ClearBackground(new Color(2, 5, 10, 255));

        float sx = s.Shake > 0 ? MathH.Rand(-s.Shake, s.Shake) : 0;
        float sy = s.Shake > 0 ? MathH.Rand(-s.Shake * 0.65f, s.Shake * 0.65f) : 0;
        Rlgl.PushMatrix();
        Rlgl.Translatef(sx, sy, 0);

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
        DrawSparks(s);
        DrawShockwaves(s);
        Raylib.EndBlendMode();
        DrawDebris(s);
        DrawFloatingTexts(s);

        Rlgl.PopMatrix();

        DrawCrosshair(s);
        DrawBloomOverlay(s);
        DrawHUD(s);
        DrawOverlays(s);

        Raylib.EndTextureMode();

        var src = new Rectangle(0, 0, _frameTarget.Texture.Width, -_frameTarget.Texture.Height);
        var dst = new Rectangle(0, 0, s.W, s.H);
        Raylib.DrawTexturePro(_frameTarget.Texture, src, dst, Vector2.Zero, 0, Color.White);
        DrawPostFx(s);
    }

    static void EnsureFxTargets(GameState s)
    {
        int w = Math.Max(1, (int)s.W);
        int h = Math.Max(1, (int)s.H);
        if (!_fxReady || _fxW != w || _fxH != h)
        {
            if (_fxReady)
            {
                Raylib.UnloadRenderTexture(_frameTarget);
                Raylib.UnloadRenderTexture(_bloomTarget);
                Raylib.UnloadRenderTexture(_bloomPing);
            }

            int bw = Math.Max(1, (int)(w * BloomScale));
            int bh = Math.Max(1, (int)(h * BloomScale));
            _frameTarget = Raylib.LoadRenderTexture(w, h);
            _bloomTarget = Raylib.LoadRenderTexture(bw, bh);
            _bloomPing = Raylib.LoadRenderTexture(bw, bh);
            Raylib.SetTextureFilter(_frameTarget.Texture, TextureFilter.Bilinear);
            Raylib.SetTextureFilter(_bloomTarget.Texture, TextureFilter.Bilinear);
            Raylib.SetTextureFilter(_bloomPing.Texture, TextureFilter.Bilinear);
            _fxW = w;
            _fxH = h;
            _fxReady = true;
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

        if (!_uiFontReady)
        {
            // Load Segoe UI (Windows) — smooth TTF with mipmaps. Fall back silently if not found.
            string[] regularPaths = { @"C:\Windows\Fonts\segoeui.ttf", @"C:\Windows\Fonts\consola.ttf", @"C:\Windows\Fonts\arial.ttf" };
            string[] boldPaths = { @"C:\Windows\Fonts\segoeuib.ttf", @"C:\Windows\Fonts\consolab.ttf", @"C:\Windows\Fonts\arialbd.ttf" };
            foreach (var p in regularPaths)
            {
                if (File.Exists(p))
                {
                    _uiFont = Raylib.LoadFontEx(p, 64, null, 0);
                    Raylib.SetTextureFilter(_uiFont.Texture, TextureFilter.Bilinear);
                    break;
                }
            }
            foreach (var p in boldPaths)
            {
                if (File.Exists(p))
                {
                    _uiFontBold = Raylib.LoadFontEx(p, 64, null, 0);
                    Raylib.SetTextureFilter(_uiFontBold.Texture, TextureFilter.Bilinear);
                    break;
                }
            }
            _uiFontReady = _uiFont.BaseSize > 0 && _uiFontBold.BaseSize > 0;
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

        // Night ? Day palette (top, mid, bottom)
        var topN = ((byte)4, (byte)10, (byte)35);
        var topD = ((byte)102, (byte)156, (byte)222);
        var midN = ((byte)18, (byte)34, (byte)79);
        var midD = ((byte)146, (byte)196, (byte)238);
        var botN = ((byte)19, (byte)15, (byte)47);
        var botD = ((byte)232, (byte)191, (byte)146);

        var top = MixRgb(topN, topD, day);
        var mid = MixRgb(midN, midD, day);
        var botBase = MixRgb(botN, botD, day);
        // Twilight warmth
        var bot = MixRgb(botBase, ((byte)255, (byte)164, (byte)112), twilight * 0.24f);

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
        var rng = new Random(271);
        for (int i = 0; i < 12; i++)
        {
            float bx = rng.NextSingle() * s.W * 1.1f - s.W * 0.05f;
            float by = rng.NextSingle() * s.HorizonY * 1.1f;
            float br = (rng.NextSingle() * 0.12f + 0.08f) * s.W;
            float ba = (rng.NextSingle() * 0.06f + 0.04f) * nightAlpha;
            float drift = rng.NextSingle() * 0.08f + 0.02f;
            float phase = rng.NextSingle() * TAU;

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

        float mx = (s.MouseX - s.W * 0.5f) / s.W;
        float my = (s.MouseY - s.H * 0.5f) / s.H;

        var rng = new Random(42);
        int count = 950;
        // Slightly expand the mask so glow halo pixels aren't crosshatched either
        float maskR2 = _moonMaskActive ? (_moonMaskR * 1.15f) * (_moonMaskR * 1.15f) : 0f;
        Raylib.BeginBlendMode(BlendMode.Additive);
        for (int i = 0; i < count; i++)
        {
            float x = rng.NextSingle() * s.W - mx * 10;
            float y = rng.NextSingle() * (s.HorizonY + 60) - my * 8;
            if (_moonMaskActive)
            {
                float ddx = x - _moonMaskX, ddy = y - _moonMaskY;
                if (ddx * ddx + ddy * ddy <= maskR2) continue;
            }
            float tw = 0.3f + 0.7f * (0.5f + 0.5f * MathF.Sin(s.Time * (0.8f + i * 0.012f) + i * 0.73f));
            byte a = (byte)(tw * 220 * visA);
            float sz = 0.2f + rng.NextSingle() * 1.2f;
            if (sz > 0.9f)
                Raylib.DrawCircle((int)x, (int)y, sz * 2.5f, new Color((byte)140, (byte)170, (byte)255, (byte)(a / 12)));
            Raylib.DrawCircle((int)x, (int)y, sz, new Color((byte)220, (byte)230, (byte)255, a));
        }
        Raylib.EndBlendMode();
    }

    // ? MOUNTAINS ? � gradient-filled silhouettes with parallax, matching HTML drawMount()
    static void DrawMountains(GameState s)
    {
        var (_, day, _, twilight) = SkyCycle(s.Time);
        float mx = (s.MouseX - s.W * 0.5f) / s.W;

        // Far layer: top night=[30,40,78] ? day=[82,106,150], bot night=[10,12,25] ? day=[42,54,86]
        var farTop = MixRgb(((byte)30, (byte)40, (byte)78), ((byte)82, (byte)106, (byte)150), day);
        var farBot = MixRgb(((byte)10, (byte)12, (byte)25), ((byte)42, (byte)54, (byte)86), day);
        // Near layer: top night=[40,42,65] ? day=[98,110,142], bot night=[11,10,20] ? day=[52,58,86]
        var nearTop = MixRgb(((byte)40, (byte)42, (byte)65), ((byte)98, (byte)110, (byte)142), day);
        var nearBot = MixRgb(((byte)11, (byte)10, (byte)20), ((byte)52, (byte)58, (byte)86), day);

        DrawMountLayerGradient(s, new Random(777), s.HorizonY + 70, s.H * 0.11f, 16, 0.6f,
            new Color(farTop.R, farTop.G, farTop.B, (byte)(210 + day * 10)),
            new Color(farBot.R, farBot.G, farBot.B, (byte)(242 - day * 16)),
            mx * 20);

        DrawMountLayerGradient(s, new Random(888), s.HorizonY + 130, s.H * 0.14f, 20, 1f,
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

    static void DrawMountLayerGradient(GameState s, Random rng, float baseY, float amp, int segs, float roughness,
        Color topCol, Color botCol, float parallaxOffset)
    {
        float os = s.W * 0.1f;
        // Generate ridge points
        var pts = new List<(float X, float Y)>(segs + 1);
        for (int i = 0; i <= segs; i++)
        {
            float x = -os + (i / (float)segs * (s.W + os * 2));
            float w = MathF.Sin(i / (float)segs * MathF.PI * (1.5f + roughness * 0.7f));
            float y = baseY - (w * amp + MathH.Rand(-amp * 0.45f, amp * 0.55f, rng));
            pts.Add((x - parallaxOffset, y));
        }

        float gndY = s.GroundY + 4;

        // Draw using Rlgl quads � per-vertex colors give a proper gradient,
        // and quads avoid all triangle winding / backface-cull issues.
        // Rlgl quad vertex order must be: BL ? BR ? TR ? TL
        Rlgl.CheckRenderBatchLimit(pts.Count * 4);
        Rlgl.SetTexture(Rlgl.GetTextureIdDefault());
        Rlgl.Begin(DrawMode.Quads);
        for (int i = 1; i < pts.Count; i++)
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

        // Ground palette: night=[42,37,68]?[9,10,22], day=[86,84,108]?[28,30,44]
        var gTopN = ((byte)42, (byte)37, (byte)68);
        var gTopD = ((byte)86, (byte)84, (byte)108);
        var gBotN = ((byte)9, (byte)10, (byte)22);
        var gBotD = ((byte)28, (byte)30, (byte)44);
        var gTop = MixRgb(gTopN, gTopD, day);
        var gBot = MixRgb(gBotN, gBotD, day);

        Raylib.DrawRectangleGradientV(0, (int)s.GroundY - 8, (int)s.W, (int)(s.H - s.GroundY + 8),
            new Color(gTop.R, gTop.G, gTop.B, (byte)255),
            new Color(gBot.R, gBot.G, gBot.B, (byte)255));

        // Retro perspective grid � purple/blue lines receding toward horizon
        var gridCol = MixRgb(((byte)90, (byte)80, (byte)160), ((byte)120, (byte)110, (byte)170), day);
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
        var hrng = new Random(555);
        for (int i = 0; i < 5; i++)
        {
            float hy = MathH.Lerp(s.HorizonY * 0.78f, s.GroundY - 30, hrng.NextSingle());
            float hth = 44 + hrng.NextSingle() * 48;
            float ha = hrng.NextSingle() * 0.06f + 0.04f;
            float hsp = hrng.NextSingle() * 0.16f + 0.08f;
            float hp = hrng.NextSingle() * TAU;
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
        var rng = new Random(city.Id.GetHashCode());
        int n = 10 + rng.Next(6);
        float slice = city.W / n;

        // Foundation glow band (HTML: gradient strip + cyan highlight line)
        Raylib.DrawRectangleGradientV((int)(cx - 12), (int)(cy - 22), (int)(city.W + 24), 32,
            new Color((byte)10, (byte)18, (byte)34, (byte)240),
            new Color((byte)6, (byte)10, (byte)20, (byte)250));
        Raylib.DrawRectangle((int)(cx - 8), (int)(cy - 9), (int)(city.W + 16), 2,
            new Color((byte)124, (byte)198, (byte)255, (byte)80));

        // First pass: building bodies + roofs (opaque, back-to-front not needed since non-overlapping)
        var buildings = new (float bx, float bw, float bh, byte r, byte g, byte b, int rt, float rh, int seed, float stepW, float stepH, float spireH, float antH, int antCol)[n];
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
        foreach (var bd in buildings)
        {
            var brng = new Random(bd.seed);
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
                    if (ct == 0) { rr = 255; gg = 80; bb = 200; }       // magenta (rare)
                    else if (ct == 1) { rr = 255; gg = 220; bb = 100; } // yellow (rare)
                    else { rr = 180; gg = 230; bb = 255; }              // cyan/white (common)
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
        var rng = new Random(c.Id.GetHashCode() + 999);
        int n = 6 + rng.Next(5);
        float sl = c.W / n;

        // Deep crater base (dark)
        Raylib.DrawRectangle((int)(cx - 10), (int)(cy - 5), (int)(c.W + 20), 7,
            new Color((byte)16, (byte)12, (byte)20, (byte)245));

        // Jagged fragment silhouettes (stable from seed)
        var frags = new (float fx, float fw, float fh, float tip, float t)[n];
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
        for (int i = 0; i < frags.Length; i++)
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
        var erng = new Random(c.Id.GetHashCode() + 1777);
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
                Raylib.DrawText("OFFLINE", (int)(b.X - 30), (int)(b.Y - 56), 16, new Color(255, 100, 50, 200));
                continue;
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

            // Floating ammo counter � large retro display
            string ammoStr = b.Ammo.ToString();
            int ammoFontSz = 26;
            int ammoW = Raylib.MeasureText(ammoStr, ammoFontSz);
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
            Raylib.DrawText(ammoStr, ammoX, ammoY, ammoFontSz, ammoCol);
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
            Raylib.DrawText(MathF.Max(0, hr.Ammo).ToString(), (int)(hr.X - 6), (int)(topY - 98), 12,
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
                Raylib.DrawText("OFFLINE", (int)(p.X - 30), (int)(p.Y - 40), 14, new Color(255, 80, 40, 180));
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
            Raylib.DrawText((p.Ammo).ToString(), (int)(p.X - 10), (int)(p.Y - 64), 12,
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
        }
    }

    static void DrawQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color col)
    {
        Raylib.DrawTriangle(a, b, c, col);
        Raylib.DrawTriangle(a, c, d, col);
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

            // Layer 3: Bright core (trunk segments only)
            foreach (var seg in bolt.Segments)
            {
                if (seg.Branch) continue;
                Raylib.DrawLineEx(new Vector2(seg.X1, seg.Y1), new Vector2(seg.X2, seg.Y2),
                    1f, new Color((byte)255, (byte)255, (byte)255, (byte)(a * 0.9f * 255)));
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
    static void DrawSmoke(GameState s)
    {
        foreach (var sm in s.SmokeParts)
        {
            float p = 1 - sm.Life / sm.MaxLife;
            float a = sm.Alpha * (1 - p);
            if (a <= 0.01f) continue;
            float r = sm.Size * (0.85f + p * 1.8f);
            // Layered gradient circles matching HTML 3-stop radial gradient
            DrawGradientCircle(sm.X, sm.Y, r,
                new Color((byte)10, (byte)16, (byte)28, (byte)(a * 0.15f * 255)));
            DrawGradientCircle(sm.X, sm.Y, r * 0.65f,
                new Color((byte)88, (byte)102, (byte)140, (byte)(a * 0.24f * 255)));
            DrawGradientCircle(sm.X, sm.Y, r * 0.3f,
                new Color((byte)168, (byte)184, (byte)220, (byte)(a * 0.44f * 255)));
        }
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
                    Raylib.EndBlendMode();
                }
                // Bright core dot
                Raylib.DrawCircle((int)m.X, (int)m.Y, isHeavy ? 2f : 1.5f,
                    new Color((byte)255, (byte)248, (byte)230, (byte)(240 * sAlpha)));
            }

            // Exhaust trail
            DrawQuad(MP(-18, -1.1f), MP(-4, -1.1f), MP(-4, 1.1f), MP(-18, 1.1f),
                new Color(vc.R, vc.G, vc.B, (byte)(100 * sAlpha)));
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
    static void DrawDebris(GameState s)
    {
        foreach (var d in s.DebrisParts)
        {
            float p = 1 - d.Life / d.MaxLife;
            byte a = (byte)((1 - p) * 215);
            Raylib.DrawRectanglePro(new Rectangle(d.X, d.Y, d.Size * 2.2f, d.Size * 1.1f),
                new Vector2(d.Size * 1.1f, d.Size * 0.55f), d.Rot * 57.2958f,
                new Color((byte)255, (byte)168, (byte)98, a));
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
            int fw = Raylib.MeasureText(ft.Text, sz);

            // Pop-in scale effect
            float scale = 1 + (1 - MathF.Min(1, ft.Life / 0.3f)) * 0.2f;
            int drawSz = (int)(sz * scale);
            int drawFw = Raylib.MeasureText(ft.Text, drawSz);
            int tx = (int)(ft.X - drawFw * 0.5f);
            int ty = (int)ft.Y;

            // Black outline (4 offsets like HTML strokeText)
            var outCol = new Color((byte)0, (byte)0, (byte)0, (byte)(a * 0.7f * 255));
            Raylib.DrawText(ft.Text, tx - 1, ty, drawSz, outCol);
            Raylib.DrawText(ft.Text, tx + 1, ty, drawSz, outCol);
            Raylib.DrawText(ft.Text, tx, ty - 1, drawSz, outCol);
            Raylib.DrawText(ft.Text, tx, ty + 1, drawSz, outCol);

            // Themed fill
            var col = combo
                ? new Color((byte)255, (byte)200, (byte)100, al)
                : new Color((byte)255, (byte)255, (byte)255, al);
            Raylib.DrawText(ft.Text, tx, ty, drawSz, col);
        }
    }

    // ?????????????? CROSSHAIR ??????????????
    static void DrawCrosshair(GameState s)
    {
        if (s.Intro || s.GameOver) return;
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
    }

    // ?????????????? POST-FX ??????????????
    static void DrawPostFx(GameState s)
    {
        float vignetteAlpha = 0.42f;
        float vignetteRadius = MathF.Max(s.W, s.H) * 0.85f;
        Raylib.DrawCircleGradient((int)(s.W * 0.5f), (int)(s.H * 0.5f), vignetteRadius,
            new Color((byte)0, (byte)0, (byte)0, (byte)0), new Color((byte)0, (byte)0, (byte)0, (byte)(vignetteAlpha * 255)));

        float scanAlpha = 0.06f + s.Danger * 0.05f;
        byte sa = (byte)(scanAlpha * 255);
        for (int y = 0; y < (int)s.H; y += 3)
            Raylib.DrawLine(0, y, (int)s.W, y, new Color((byte)106, (byte)181, (byte)255, sa));

        if (s.Flash > 0.01f)
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

        if (s.Weather.Mode == "ash" && s.Weather.Intensity > 0)
        {
            byte wa = (byte)(MathH.Clamp(s.Weather.Intensity * 0.08f, 0, 0.12f) * 255);
            Raylib.DrawRectangle(0, 0, (int)s.W, (int)s.H, new Color((byte)164, (byte)110, (byte)70, wa));
        }

        DrawGrain(s);

        if (_fxReady && s.Chromatic > 0.02f)
        {
            int offset = (int)MathF.Round(s.Chromatic * 4);
            if (offset >= 1)
            {
                var src = new Rectangle(0, 0, _frameTarget.Texture.Width, -_frameTarget.Texture.Height);
                var dst = new Rectangle(0, 0, s.W, s.H);
                Raylib.BeginBlendMode(BlendMode.Additive);
                byte ca = (byte)(MathH.Clamp(s.Chromatic * 0.15f, 0, 0.3f) * 255);
                Raylib.DrawTexturePro(_frameTarget.Texture, src, new Rectangle(dst.X - offset, dst.Y, dst.Width, dst.Height),
                    Vector2.Zero, 0, new Color((byte)255, (byte)255, (byte)255, ca));
                Raylib.DrawTexturePro(_frameTarget.Texture, src, new Rectangle(dst.X + offset, dst.Y, dst.Width, dst.Height),
                    Vector2.Zero, 0, new Color((byte)255, (byte)255, (byte)255, ca));
                Raylib.EndBlendMode();
            }
        }
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

    // Modern HUD — rounded panel, bottom-left, clears the HellRaiser silo at center-bottom.
    static void DrawHUD(GameState s)
    {
        if (s.Intro) return;
        float fs = 14f;
        float lineH = fs + 8;
        int lines = 5;
        int panelW = (int)MathF.Min(640, s.W * 0.5f);
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

        int citiesAlive = s.Cities.Count(ci => !ci.Destroyed);
        int pending = Math.Max(0, s.WavePlan.Count - s.SpawnI);
        int ufoCount = s.UFOs.Count;
        int raiderCount = s.Raiders.Count;
        int hostiles = s.Enemies.Count + ufoCount + raiderCount + (s.Demon != null ? 1 : 0);
        int ammoLeft = s.Bases.Where(b => !b.Destroyed).Sum(b => b.Ammo) + s.Phalanxes.Where(p => !p.Destroyed).Sum(p => p.Ammo);
        string ammo = string.Join("  ", s.Bases.Select(b => $"{b.Id}:{(b.Destroyed ? "X" : b.Ammo.ToString())}"));
        string ph = s.Phalanxes.Count == 0 ? "--" : string.Join("  ", s.Phalanxes.Select(p =>
        {
            var label = p.Id switch
            {
                "PHALANX_L" => "L",
                "PHALANX_R" => "R",
                _ => p.Id
            };
            return $"{label}:{(p.Destroyed ? "X" : p.Ammo.ToString())}";
        }));
        string hr = s.HellRaiser == null ? "--" : s.HellRaiser.Destroyed ? "X" : $"{s.HellRaiser.State.ToUpperInvariant()} {s.HellRaiser.Ammo}";
        string volText = SynthAudio.IsMuted ? "MUTED" : $"{MathF.Round(SynthAudio.Volume * 100)}%";
        string weather = $"{s.Weather.Mode.ToUpperInvariant()} {MathF.Round(s.Weather.Intensity * 100)}%";
        string up = $"YLD x{s.Upgrades.BlastScale:F1}  RLD x{s.Upgrades.ReloadMult:F2}  EMP x{s.Upgrades.EmpScale:F1}";
        int bars = 14;
        int fill = (int)MathF.Round(s.Danger * bars);
        string bar = new string('#', fill) + new string('-', bars - fill);

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

        DrawSegment(ref x, "WAVE", s.Level.ToString(), accent);
        DrawSep(ref x);
        DrawSegment(ref x, "SCORE", s.Score.ToString(), accent);
        DrawSep(ref x);
        DrawSegment(ref x, "COMBO", $"x{Math.Max(1, s.Combo)}", s.Combo > 2 ? accent : dim);
        DrawSep(ref x);
        DrawSegment(ref x, "MAX", $"x{Math.Max(1, s.MaxCombo)}", accent);
        DrawSep(ref x);
        DrawSegment(ref x, "VOL", volText, accent);

        y += lineH;
        x = panelX + 14;
        DrawSegment(ref x, "CITIES", citiesAlive.ToString(), citiesAlive <= 2 ? warn : accent);
        DrawSep(ref x);
        DrawSegment(ref x, "EMP", s.Emp.ToString(), s.Emp > 0 ? accent : dim);
        DrawSep(ref x);
        DrawSegment(ref x, "AMMO", ammoLeft.ToString(), ammoLeft > 0 ? accent : dim);
        DrawSep(ref x);
        DrawSegment(ref x, "MODE", s.Auto ? "AUTO" : "MANUAL", s.Auto ? accent : dim);

        y += lineH;
        x = panelX + 14;
        DrawSegment(ref x, "HOST", hostiles.ToString(), accent);
        DrawSep(ref x);
        DrawSegment(ref x, "UFO", ufoCount.ToString(), accent);
        DrawSep(ref x);
        DrawSegment(ref x, "RAIDER", raiderCount.ToString(), accent);
        DrawSep(ref x);
        DrawSegment(ref x, "PEND", pending.ToString(), accent);

        y += lineH;
        x = panelX + 14;
        DrawSegment(ref x, "BASES", ammo.Length == 0 ? "--" : ammo, accent);
        DrawSep(ref x);
        DrawSegment(ref x, "PHX", ph, accent);

        y += lineH;
        x = panelX + 14;
        DrawSegment(ref x, "THREAT", $"[{bar}] {MathF.Round(s.Danger * 100)}%", s.Danger > 0.66f ? warn : accent);
        DrawSep(ref x);
        DrawSegment(ref x, "WX", weather, accent);

        // Upgrades + HR on far right column (small row above panel)
        float uy = panelY - lineH - 2;
        DrawTextM(up, panelX + 14, uy, fs, dim);
        string hrStr = $"HR {hr}";
        int hrW = MeasureTextM(hrStr, fs);
        DrawTextM(hrStr, panelX + panelW - hrW - 14, uy, fs, accent, true);

        if (s.Debug.Enabled)
        {
            string dbg = $"FPS {Raylib.GetFPS()}  E{s.Enemies.Count} P{s.PlayerMissiles.Count} X{s.Explosions.Count}";
            int dw = MeasureTextM(dbg, fs);
            DrawTextM(dbg, panelX + panelW - dw - 14, panelY - lineH * 2 - 2, fs, dim);
        }
    }

    // ?????????????? OVERLAYS ??????????????
    static void DrawOverlays(GameState s)
    {
        if (s.Intro)
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
        }
        if (s.GameOver)
        {
            byte oa = (byte)MathH.Clamp(s.GameOverTime * 108, 0, 190);
            Raylib.DrawRectangle(0, 0, (int)s.W, (int)s.H, new Color((byte)0, (byte)0, (byte)0, oa));
            var go = "GAME OVER"; int gow = MeasureTextM(go, 56, true);
            float p = 0.62f + 0.38f * MathF.Sin(s.Time * 2.2f);
            DrawTextM(go, s.W / 2 - gow / 2, s.H / 2 - 56, 56, new Color((byte)255, (byte)90, (byte)70, (byte)(p * 255)), true);
            var sc = $"Score: {s.Score}"; int scw = MeasureTextM(sc, 30);
            DrawTextM(sc, s.W / 2 - scw / 2, s.H / 2 + 14, 30, new Color((byte)198, (byte)228, (byte)255, (byte)235));
            var mc = $"Max Combo: {s.MaxCombo}x"; int mcw = MeasureTextM(mc, 22);
            DrawTextM(mc, s.W / 2 - mcw / 2, s.H / 2 + 54, 22, new Color((byte)172, (byte)202, (byte)232, (byte)205));
            if (s.GameOverTime > 1.5f)
            {
                var rs = "Press R to Restart"; int rw = MeasureTextM(rs, 20);
                float rp = 0.38f + 0.62f * MathF.Sin(s.Time * 3.2f);
                DrawTextM(rs, s.W / 2 - rw / 2, s.H / 2 + 96, 20, new Color((byte)120, (byte)240, (byte)255, (byte)(rp * 230)));
            }
        }
        if (s.Shop)
        {
            DrawShopPanel(s);
        }
        if (s.MsgT > 0 && s.Msg.Length > 0)
        {
            byte a = (byte)(MathH.Clamp(s.MsgT, 0, 1) * 255);
            int mw = MeasureTextM(s.Msg, 28, true);
            DrawTextM(s.Msg, s.W / 2 - mw / 2, s.H * 0.18f, 28, new Color((byte)120, (byte)240, (byte)255, a), true);
        }
        if (s.NoteT > 0 && s.Note.Length > 0)
        {
            byte a = (byte)(MathH.Clamp(s.NoteT, 0, 1) * 230);
            int nw = MeasureTextM(s.Note, 17);
            DrawTextM(s.Note, s.W / 2 - nw / 2, s.H - 44, 17, new Color((byte)188, (byte)218, (byte)242, a));
        }
    }

    static void DrawShopPanel(GameState s)
    {
        float panelW = MathF.Min(s.W * 0.72f, 820);
        float panelH = MathF.Min(s.H * 0.82f, 540);
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
        DrawTextM(title, px + panelW * 0.5f - tw * 0.5f, py + 26, 30,
            new Color((byte)234, (byte)245, (byte)255, (byte)255), true);

        // Funds
        string funds = $"AVAILABLE FUNDS:  {s.Score}";
        int fw = MeasureTextM(funds, 22, true);
        DrawTextM(funds, px + panelW * 0.5f - fw * 0.5f, py + 72, 22,
            new Color((byte)140, (byte)232, (byte)255, (byte)255), true);

        // Upgrade buttons
        var items = new (int cost, string label, bool enabled)[]
        {
            (5000, "1. REBUILD CITY",    s.Score >= 5000 && s.Cities.Any(c => c.Destroyed)),
            (2500, "2. BUY EMP",         s.Score >= 2500 && s.Emp < s.EmpMax),
            (4000, $"3. WARHEAD YIELD  [x{s.Upgrades.BlastScale:F1} → x{MathF.Min(2.8f, s.Upgrades.BlastScale + 0.2f):F1}]",
                s.Score >= 4000 && s.Upgrades.BlastScale < 2.8f - 0.001f),
            (3500, $"4. RELOAD BOOST   [x{s.Upgrades.ReloadMult:F2} → x{MathF.Min(2.2f, s.Upgrades.ReloadMult + 0.12f):F2}]",
                s.Score >= 3500 && s.Upgrades.ReloadMult < 2.2f - 0.001f),
            (3600, $"5. EMP AMPLIFIER  [x{s.Upgrades.EmpScale:F2} → x{MathF.Min(2.4f, s.Upgrades.EmpScale + 0.14f):F2}]",
                s.Score >= 3600 && s.Upgrades.EmpScale < 2.4f - 0.001f),
        };

        float btnW = MathF.Min(panelW - 80, 440);
        float btnH = 46;
        float btnX = px + panelW * 0.5f - btnW * 0.5f;
        float btnY = py + 120;
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
            var br = new Rectangle(btnX, btnY + i * (btnH + 10), btnW, btnH);
            Raylib.DrawRectangleRounded(br, 0.22f, 8, bgCol);
            Raylib.DrawRectangleRoundedLinesEx(br, 0.22f, 8, 1.3f, brCol);

            string priced = $"{label}    [{cost}]";
            int lw = MeasureTextM(priced, 19, true);
            DrawTextM(priced, br.X + br.Width * 0.5f - lw * 0.5f, br.Y + br.Height * 0.5f - 11, 19, txCol, true);
        }

        // Timer
        float timerY = btnY + items.Length * (btnH + 10) + 16;
        string t = $"COMMENCING NEXT WAVE IN {MathF.Max(0, MathF.Ceiling(s.ShopTimer))}s";
        int tww = MeasureTextM(t, 22, true);
        DrawTextM(t, px + panelW * 0.5f - tww * 0.5f, timerY, 22,
            new Color((byte)189, (byte)244, (byte)255, (byte)255), true);

        // Hint
        string hint = "Press 1/2/3/4/5 to purchase — SPACE to skip";
        int hw2 = MeasureTextM(hint, 16);
        DrawTextM(hint, px + panelW * 0.5f - hw2 * 0.5f, timerY + 34, 16,
            new Color((byte)140, (byte)232, (byte)255, (byte)210));
    }
}
