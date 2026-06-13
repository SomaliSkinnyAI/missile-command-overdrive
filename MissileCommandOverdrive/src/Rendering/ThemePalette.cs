namespace MissileCommandOverdrive.Rendering;

/// <summary>§4.4 / §5 5.4 — the single color authority. One <see cref="ThemePalette"/>
/// owns the sky/ground/grid/city/bloom color identity of a theme plus the analytic
/// grade (lift/gamma/gain) and CRT amount applied in the uber-shader after ACES.
///
/// The world color literals in DrawSky/DrawGround/DrawCityAlive/DrawMountains/DrawStars
/// route through the active instance (<see cref="Palette.Active"/>); Modern holds the
/// EXACT pre-sweep values with an identity grade and crtAmount 0, so "Modern with grading
/// disabled is diff-identical to pre-sweep" (the §5 5.4 acceptance). Xbox and Recharged
/// shift those same stops and turn the grade/CRT on.
///
/// A grade triple is a float3 (r,g,b): graded = gain * pow(max(color + lift, 0), 1/gamma).
/// The colorblind variant set rides this same sweep through <see cref="Palette.VariantColor"/>
/// — it is NOT a parallel refactor.</summary>
public readonly struct Rgb
{
    public readonly byte R, G, B;
    public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
    // Implicit conversion to the (byte,byte,byte) tuple the renderer's MixRgb consumes,
    // so call sites read `MixRgb(p.SkyTopN, p.SkyTopD, day)` with zero ceremony.
    public static implicit operator (byte R, byte G, byte B)(Rgb c) => (c.R, c.G, c.B);
}

/// <summary>A (r,g,b) float triple used for the lift/gamma/gain grade channels.</summary>
public readonly struct Grade3
{
    public readonly float R, G, B;
    public Grade3(float r, float g, float b) { R = r; G = g; B = b; }
    public readonly System.Numerics.Vector3 V => new(R, G, B);
}

public readonly struct ThemePalette
{
    // ——— Sky (DrawSky): night→day stops for top/mid/bot + the twilight warm tint ———
    public readonly Rgb SkyTopN, SkyTopD, SkyMidN, SkyMidD, SkyBotN, SkyBotD;
    public readonly Rgb SkyTwilightWarm; // mixed into the bottom band by twilight*0.24

    // ——— Stars (DrawStars): the bright star core + its faint halo ———
    public readonly Rgb StarCore, StarHalo;

    // ——— Mountains (DrawMountains): far/near layer top+bottom night→day stops ———
    public readonly Rgb MtnFarTopN, MtnFarTopD, MtnFarBotN, MtnFarBotD;
    public readonly Rgb MtnNearTopN, MtnNearTopD, MtnNearBotN, MtnNearBotD;

    // ——— Ground (DrawGround): top/bottom night→day stops + the retro grid ———
    public readonly Rgb GroundTopN, GroundTopD, GroundBotN, GroundBotD;
    public readonly Rgb GridN, GridD;

    // ——— City (DrawCityAlive): the three window/trim hue choices ———
    // ct 0 / 1 / common — the analytic grade does the heavy lifting for theme feel,
    // but the rare accent hues let each theme keep its own neon character.
    public readonly Rgb CityWindow0, CityWindow1, CityWindowCommon;

    // ——— Bloom tint (reserved for the bloom composite; Modern = white passthrough) ———
    public readonly Rgb BloomTint;

    // ——— Analytic grade (uber-shader, after ACES, before vignette) + CRT ———
    public readonly Grade3 Lift, Gamma, Gain;
    public readonly float CrtAmount; // >0 enables the Lottes-style CRT branch
    public readonly int ThemeId;     // 0 modern, 1 xbox, 2 recharged (for shader branches)

    public ThemePalette(
        Rgb skyTopN, Rgb skyTopD, Rgb skyMidN, Rgb skyMidD, Rgb skyBotN, Rgb skyBotD, Rgb skyTwilightWarm,
        Rgb starCore, Rgb starHalo,
        Rgb mtnFarTopN, Rgb mtnFarTopD, Rgb mtnFarBotN, Rgb mtnFarBotD,
        Rgb mtnNearTopN, Rgb mtnNearTopD, Rgb mtnNearBotN, Rgb mtnNearBotD,
        Rgb groundTopN, Rgb groundTopD, Rgb groundBotN, Rgb groundBotD, Rgb gridN, Rgb gridD,
        Rgb cityWindow0, Rgb cityWindow1, Rgb cityWindowCommon, Rgb bloomTint,
        Grade3 lift, Grade3 gamma, Grade3 gain, float crtAmount, int themeId)
    {
        SkyTopN = skyTopN; SkyTopD = skyTopD; SkyMidN = skyMidN; SkyMidD = skyMidD;
        SkyBotN = skyBotN; SkyBotD = skyBotD; SkyTwilightWarm = skyTwilightWarm;
        StarCore = starCore; StarHalo = starHalo;
        MtnFarTopN = mtnFarTopN; MtnFarTopD = mtnFarTopD; MtnFarBotN = mtnFarBotN; MtnFarBotD = mtnFarBotD;
        MtnNearTopN = mtnNearTopN; MtnNearTopD = mtnNearTopD; MtnNearBotN = mtnNearBotN; MtnNearBotD = mtnNearBotD;
        GroundTopN = groundTopN; GroundTopD = groundTopD; GroundBotN = groundBotN; GroundBotD = groundBotD;
        GridN = gridN; GridD = gridD;
        CityWindow0 = cityWindow0; CityWindow1 = cityWindow1; CityWindowCommon = cityWindowCommon;
        BloomTint = bloomTint;
        Lift = lift; Gamma = gamma; Gain = gain; CrtAmount = crtAmount; ThemeId = themeId;
    }

    // ——————————————————————————————————————————————————————————————
    // MODERN — defaults EXACTLY the pre-sweep literals; identity grade; CRT off.
    // Every stop below is copied verbatim from the original DrawSky/DrawStars/
    // DrawMountains/DrawGround/DrawCityAlive so the Modern diff is line-by-line.
    // ——————————————————————————————————————————————————————————————
    public static readonly ThemePalette Modern = new(
        // Sky top N/D, mid N/D, bot N/D, twilight warm
        new(4, 10, 35),    new(102, 156, 222),
        new(18, 34, 79),   new(146, 196, 238),
        new(19, 15, 47),   new(232, 191, 146),
        new(255, 164, 112),
        // Stars: core (220,230,255), halo (140,170,255)
        new(220, 230, 255), new(140, 170, 255),
        // Mountains far top N/D, far bot N/D
        new(30, 40, 78),   new(82, 106, 150),
        new(10, 12, 25),   new(42, 54, 86),
        // Mountains near top N/D, near bot N/D
        new(40, 42, 65),   new(98, 110, 142),
        new(11, 10, 20),   new(52, 58, 86),
        // Ground top N/D, bot N/D
        new(42, 37, 68),   new(86, 84, 108),
        new(9, 10, 22),    new(28, 30, 44),
        // Grid N/D
        new(90, 80, 160),  new(120, 110, 170),
        // City windows: ct0 magenta, ct1 yellow, common cyan/white
        new(255, 80, 200), new(255, 220, 100), new(180, 230, 255),
        // Bloom tint: white (passthrough)
        new(255, 255, 255),
        // Identity grade, CRT off, themeId 0
        new(0f, 0f, 0f), new(1f, 1f, 1f), new(1f, 1f, 1f), 0f, 0);

    // ——————————————————————————————————————————————————————————————
    // XBOX — warm sand + green-phosphor. Sky shifts olive-warm, ground sandy, the
    // grade pulls green and crtAmount turns the CRT branch on (~0.5).
    // ——————————————————————————————————————————————————————————————
    public static readonly ThemePalette Xbox = new(
        // Sky: warm olive night → sandy-green day
        new(14, 20, 14),   new(150, 158, 92),
        new(34, 40, 22),   new(176, 184, 116),
        new(40, 30, 18),   new(236, 206, 132),
        new(255, 176, 96),
        // Stars: warm amber core, dim olive halo
        new(236, 238, 196), new(150, 170, 90),
        // Mountains far: olive-green silhouettes
        new(34, 44, 30),   new(96, 108, 66),
        new(12, 16, 10),   new(48, 58, 34),
        new(42, 46, 30),   new(108, 116, 72),
        new(12, 12, 8),    new(56, 62, 36),
        // Ground: sandy/khaki
        new(54, 48, 30),   new(104, 98, 64),
        new(14, 13, 8),    new(40, 38, 22),
        // Grid: green phosphor
        new(120, 150, 70), new(150, 176, 96),
        // City windows: amber / lime / phosphor-green
        new(255, 180, 70), new(200, 255, 120), new(170, 230, 130),
        // Bloom tint: warm
        new(255, 236, 196),
        // Grade: green push (gain G > R,B), slightly lifted warm blacks, CRT on
        new(0.015f, 0.020f, 0.005f), new(0.96f, 1.04f, 0.92f), new(1.05f, 1.12f, 0.84f),
        0.5f, 1);

    // ——————————————————————————————————————————————————————————————
    // RECHARGED — crushed-black neon. Near-black sky, saturated magenta/cyan accents,
    // high-contrast grade (lifted blacks crushed back down, punchy gain). CRT off.
    // ——————————————————————————————————————————————————————————————
    public static readonly ThemePalette Recharged = new(
        // Sky: near-black night → deep desaturated indigo "day"
        new(2, 2, 8),      new(20, 16, 48),
        new(6, 4, 22),     new(34, 22, 64),
        new(4, 2, 14),     new(48, 26, 60),
        new(255, 60, 180),
        // Stars: electric white core, magenta halo
        new(255, 255, 255), new(255, 90, 220),
        // Mountains far: black with a cool magenta-blue edge
        new(14, 8, 30),    new(40, 24, 72),
        new(2, 1, 8),      new(14, 8, 30),
        new(18, 10, 36),   new(52, 28, 84),
        new(2, 1, 6),      new(16, 8, 28),
        // Ground: crushed black
        new(10, 6, 22),    new(26, 16, 44),
        new(2, 1, 6),      new(8, 4, 16),
        // Grid: saturated cyan/magenta
        new(0, 220, 255),  new(120, 60, 255),
        // City windows: hot magenta / electric cyan / saturated cyan-white
        new(255, 40, 200), new(60, 240, 255), new(120, 240, 255),
        // Bloom tint: magenta-cyan lean
        new(255, 210, 255),
        // Grade: crush blacks (negative lift), low gamma = more contrast, punchy gain
        new(-0.035f, -0.045f, -0.020f), new(0.86f, 0.84f, 0.90f), new(1.16f, 1.10f, 1.22f),
        0f, 2);
}
