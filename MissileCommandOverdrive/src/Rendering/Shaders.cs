namespace MissileCommandOverdrive.Rendering;

/// <summary>Embedded GLSL 330 sources. Feature 1.4: the composite uber-shader runs the
/// full post chain in one pass over the §4.1 frame blit; the legacy CPU draws remain in
/// Renderer.DrawPostFx as the compile-failure fallback. Feature 2.1: soft-knee bright
/// pass + dual-Kawase down/up for the GPU threshold bloom mip chain (the bloom has no
/// CPU fallback — on compile failure the scene simply renders unbloomed). Feature 2.2:
/// shockwave refraction / heat shimmer / EMP ripple ride the same uber-shader pass via
/// two small uniform arrays uploaded per frame. Feature 5.1: ACES filmic tonemap over
/// the FP16 scene, gated by hdrActive so the RGBA8 fallback stays a pure passthrough.
/// Feature 5.2: quarter-res dynamic light buffer composited at the refracted uv,
/// pre-ACES, with a day/night ambient floor and a luminance mask.</summary>
public static class Shaders
{
    // Raylib default attribute/uniform names (fragTexCoord/fragColor/texture0/colDiffuse)
    // are mandatory — the default vertex shader feeds them. texture(), not texture2D().
    public const string PostFxFrag = """
#version 330

in vec2 fragTexCoord;
in vec4 fragColor;

uniform sampler2D texture0;
uniform vec4 colDiffuse;

uniform float time;
uniform float danger;
uniform float chromatic;
uniform float flashAmount;
uniform vec2 flashDir;
uniform vec3 flashColor;
uniform vec2 resolution;

// Feature 2.2 — shockwave refraction. xy = ring center (texcoord space, y-up),
// z = current front radius in units of screen height, w = strength = Life/MaxLife
// (already encodes the (1-age) fade). w > 1.0 flags an EMP wave: strength = w - 1.
uniform vec4 shockwaves[16];
uniform int shockwaveCount;
// Heat shimmer sources (the 4 hottest live non-EMP explosions). Separate array —
// not a w-flag on shockwaves — because w already carries the EMP flag and most
// hot blasts never spawn a Shockwave entry. xy = center (texcoord), z = blast
// radius (height units), w = heat 0..1.
uniform vec4 heatSources[4];
uniform int heatCount;

// Feature 5.1 — ACES tonemap gate (1 when the scene target is FP16, 0 on the
// RGBA8 fallback) and the pre-ACES exposure lift (~1.15, uploaded once).
uniform float hdrActive;
uniform float exposure;

// Feature 5.2 — dynamic 2D light buffer: quarter-res additive blob accumulation
// (explosions, muzzle flashes, lightning trunks, EMP fronts, moon, city neon).
// dayFactor = SkyCycle day normalized 0 (deep night) .. 1 (full day);
// lightActive = 0 keeps this whole block a passthrough (env kill-switch).
uniform sampler2D lightTex;
uniform float dayFactor;
uniform float lightActive;

// Feature 5.4 — theme identity. The analytic grade runs AFTER ACES and BEFORE the
// vignette: graded = gain * pow(max(color + lift, 0), 1/gamma). Modern ships an
// identity grade (lift 0 / gamma 1 / gain 1) so its output is unchanged. crtAmount
// > 0 (Xbox) enables a cheap Lottes-style CRT branch (hardScan + slot mask + a bit
// of extra barrel warp); it reuses the existing barrel-distorted uv so the warp is
// not double-applied. lastCity (0..1) folds a red-shift desaturation in at one city
// left — a state modifier riding the same pass as danger.
uniform vec3 gradeLift;
uniform vec3 gradeGamma;
uniform vec3 gradeGain;
uniform float crtAmount;
uniform float lastCity;

out vec4 finalColor;

float hash12(vec2 p)
{
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

// Bilinear value noise over the integer lattice of hash12
float vnoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash12(i),                  hash12(i + vec2(1.0, 0.0)), f.x),
               mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), f.x), f.y);
}

void main()
{
    // Subtle barrel distortion (~0.02). 5.4: the CRT branch reuses/extends THIS
    // warp (it is not a second distortion) — crtAmount adds curvature so the tube
    // bulge and the base barrel stay coordinated.
    vec2 d = fragTexCoord - 0.5;
    float barrel = 0.02 + crtAmount * 0.10;
    vec2 uv = 0.5 + d * (1.0 + barrel * dot(d, d));

    // Render-texture wrap defaults to GL_REPEAT — clamp to half a texel
    vec2 px = 0.5 / resolution;
    vec2 lo = px;
    vec2 hi = 1.0 - px;

    // Shockwave refraction + EMP ripple (2.2). Distances run in aspect-corrected
    // UV (units of screen height) so rings stay circular; displacement vectors
    // convert back to texcoord space via /ar.
    vec2 ar = vec2(resolution.x / resolution.y, 1.0);
    vec2 refr = vec2(0.0);
    vec2 empCa = vec2(0.0);
    for (int i = 0; i < shockwaveCount; i++)
    {
        vec4 sw = shockwaves[i];
        float isEmp = step(1.0, sw.w);
        float str = sw.w - isEmp;
        vec2 q = (uv - sw.xy) * ar;
        float dist = max(length(q), 1e-5);
        vec2 dir = q / dist;
        float band = 1.0 - smoothstep(0.0, mix(0.040, 0.105, isEmp), abs(dist - sw.z));
        band *= band;
        // EMP keeps its punch late in life (sqrt fade); plain blasts fade linearly
        float fade = mix(str, sqrt(str), isEmp);
        // Negative radial sampling = image lensed outward at the ring front
        refr -= dir * (band * fade * mix(0.0042, 0.0060, isEmp)) / ar;
        // EMP only: radial chroma split riding the front
        empCa += dir * (band * fade * 0.0040 * isEmp);
    }

    // Heat shimmer — hot-air column rising above strong live explosions
    float shimmer = 0.0;
    for (int i = 0; i < heatCount; i++)
    {
        vec4 hs = heatSources[i];
        vec2 q = (uv - hs.xy) * ar;
        float horiz = 1.0 - smoothstep(0.0, hs.z * 1.5, abs(q.x));
        float vert = smoothstep(-hs.z * 0.5, hs.z * 0.2, q.y)
                   * (1.0 - smoothstep(hs.z * 0.8, hs.z * 2.8, q.y));
        shimmer = max(shimmer, horiz * vert * hs.w);
    }
    if (shimmer > 0.001)
    {
        // 2-octave fbm value noise scrolled upward — horizontal-only wobble
        vec2 np = vec2(uv.x * ar.x, uv.y) * 42.0 + vec2(0.0, -time * 2.7);
        float n = vnoise(np) * 0.667 + vnoise(np * 2.13 + 7.7) * 0.333;
        refr.x += (n - 0.5) * shimmer * 0.0040;
    }

    // Cap total displacement (~0.006 UV) and keep samples inside the quad
    refr = clamp(refr, vec2(-0.0065), vec2(0.0065));
    uv = clamp(uv + refr, lo, hi);

    // True RGB-split chromatic aberration: three taps, radial offset
    // (the EMP front contributes its own radial split)
    vec2 ca = d * (chromatic * 0.0035) + empCa;
    vec3 col;
    col.r = texture(texture0, clamp(uv + ca, lo, hi)).r;
    col.g = texture(texture0, clamp(uv, lo, hi)).g;
    col.b = texture(texture0, clamp(uv - ca, lo, hi)).b;

    // 5.2 — light buffer composite. Sampled at the SAME refraction-displaced uv
    // as the scene so refracted pixels light correctly, and placed pre-ACES so
    // the warm wash rides through the tonemap on the HDR path. Night drops the
    // ambient floor to ~0.82 so lights visibly pop; day stays ~1.0 with only a
    // tiny additive spill so the scene isn't washed out. The multiplicative
    // boost is luminance-masked above 0.8 — bloom cores and explosion centers
    // are already bright and must not double-brighten.
    if (lightActive > 0.5)
    {
        vec3 lightC = texture(lightTex, uv).rgb;
        float sceneLuma = dot(col, vec3(0.299, 0.587, 0.114));
        float lumaMask = 1.0 - smoothstep(0.8, 1.0, sceneLuma);
        float ambient = mix(0.82, 1.0, dayFactor);
        float boost = mix(1.25, 0.1, dayFactor) * lumaMask;
        float spill = mix(0.38, 0.05, dayFactor);
        col = col * (ambient + boost * lightC) + lightC * spill;
    }

    // 5.1 — ACES filmic (Narkowicz 2015 fit) over the FP16 scene. Sits after
    // every texture tap (refraction/CA sample linear HDR energy) and before
    // every LDR grading step below, so desat/flash/vignette/scanlines/grain
    // behave identically on both paths. exposure (~1.15) is the pre-ACES lift
    // for the key additive emitters (explosion cores, lightning trunks, EMP
    // fronts): rlgl vertex colors clamp at 255 so per-draw tints can't exceed
    // 1.0 — HDR energy only comes from additive overlap, and the lift restores
    // the punch the tonemap shoulder would otherwise dull. hdrActive = 0 →
    // passthrough, preserving the exact RGBA8 fallback look.
    if (hdrActive > 0.5)
    {
        vec3 x = col * exposure;
        col = clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
    }

    // 5.4 — analytic theme grade. Identity (Modern) is a no-op: pow(c,1)=c, *1, +0.
    // Runs in LDR (post-ACES) so both HDR and RGBA8 paths grade identically.
    col = gradeGain * pow(max(col + gradeLift, 0.0), 1.0 / max(gradeGamma, vec3(1e-3)));
    col = clamp(col, 0.0, 1.0);

    // 5.4 — last-city red-shift: desaturate toward a danger red as the final city
    // teeters. Distinct from `danger` (a continuous tension scalar) — this is the
    // discrete "one left" state modifier the plan calls for.
    if (lastCity > 0.001)
    {
        float lcl = dot(col, vec3(0.299, 0.587, 0.114));
        vec3 redShift = mix(vec3(lcl), vec3(lcl * 1.25, lcl * 0.45, lcl * 0.4), 0.7);
        col = mix(col, redShift, lastCity * 0.6);
    }

    // Danger desaturation
    float luma = dot(col, vec3(0.299, 0.587, 0.114));
    col = mix(col, vec3(luma), danger * 0.25);

    // Directional screen flash — gradient along flashDir in y-down screen space
    vec2 suv = vec2(uv.x, 1.0 - uv.y);
    float fg = clamp(0.5 + dot(suv - 0.5, flashDir), 0.0, 1.0);
    col = mix(col, flashColor, clamp(flashAmount * (0.6 + 0.5 * fg), 0.0, 0.85));

    // Vignette — pixel-space radial like the CPU DrawCircleGradient (alpha 0.42 at
    // radius 0.85*max(W,H); screen edges land around the legacy ~52/255 darkening)
    vec2 vp = (suv - 0.5) * resolution;
    float vig = smoothstep(0.0, 1.0, length(vp) / (0.85 * max(resolution.x, resolution.y)));
    col *= 1.0 - 0.42 * vig;

    // Scanlines — every 3rd row, light blue, alpha 0.06 + danger*0.05.
    // 5.4: the heavier CRT theme owns its own scanline look below, so suppress this
    // subtle one when the CRT branch is active (avoids stacking two scan patterns).
    float scan = step(fract(suv.y * resolution.y / 3.0), 1.0 / 3.0);
    col = mix(col, vec3(0.416, 0.710, 1.0), scan * (0.06 + danger * 0.05) * (1.0 - crtAmount));

    // 5.4 — Lottes-style CRT (Xbox theme). Cheap hardScan brightness modulation
    // by scanline position + an aperture-grille slot mask, scaled by crtAmount so
    // crtAmount==0 is a clean passthrough. Public-domain technique (Timothy Lottes).
    if (crtAmount > 0.001)
    {
        // hardScan: a soft beam falloff between scanlines (period 3 device px)
        float pos = fract(suv.y * resolution.y / 3.0) * 2.0 - 1.0;
        float scanBeam = exp2(-1.7 * pos * pos);
        // slot mask: 3-phase RGB aperture grille on a 3 device-px horizontal period
        float ph = fract(suv.x * resolution.x / 3.0);
        vec3 mask = vec3(0.62);
        if (ph < 0.333)      mask.r = 1.0;
        else if (ph < 0.666) mask.g = 1.0;
        else                 mask.b = 1.0;
        vec3 crtCol = col * scanBeam * mask;
        // a hair of overdrive so the masked image keeps its brightness
        crtCol *= 1.0 + 0.5 * crtAmount;
        col = mix(col, crtCol, crtAmount);
    }

    // Animated hash film grain, alpha 0.01 + danger*0.008 (legacy cap 0.04)
    vec2 gp = floor(suv * resolution) + floor(vec2(time * 91.0, time * 113.0));
    float grain = hash12(gp);
    col = mix(col, vec3(grain), clamp(0.01 + danger * 0.008, 0.0, 0.04));

    finalColor = vec4(col, 1.0) * colDiffuse * fragColor;
}
""";

    // ——— GPU threshold bloom (feature 2.1) ———
    // Bright pass with a quadratic soft knee (Unity/Karis style): contribution is 0
    // below threshold-knee, eases in quadratically across the knee, linear above.
    // Intensity is folded in here so the rest of the chain stays a pure blur.
    public const string BloomPrefilterFrag = """
#version 330

in vec2 fragTexCoord;
in vec4 fragColor;

uniform sampler2D texture0;
uniform vec4 colDiffuse;

uniform float threshold;
uniform float knee;
uniform float intensity;

out vec4 finalColor;

void main()
{
    vec3 c = texture(texture0, fragTexCoord).rgb;
    float br = max(c.r, max(c.g, c.b));
    float soft = clamp(br - threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 1e-4);
    float w = max(soft, br - threshold) / max(br, 1e-4);
    finalColor = vec4(c * w * intensity, 1.0);
}
""";

    // Dual-Kawase downsample: weighted center + 4 diagonal half-pixel taps
    // (Bjorge, SIGGRAPH 2015). halfPixel = 0.5 / source resolution.
    public const string BloomDownsampleFrag = """
#version 330

in vec2 fragTexCoord;
in vec4 fragColor;

uniform sampler2D texture0;
uniform vec4 colDiffuse;

uniform vec2 halfPixel;

out vec4 finalColor;

void main()
{
    vec2 uv = fragTexCoord;
    vec3 sum = texture(texture0, uv).rgb * 4.0;
    sum += texture(texture0, uv - halfPixel).rgb;
    sum += texture(texture0, uv + halfPixel).rgb;
    sum += texture(texture0, uv + vec2(halfPixel.x, -halfPixel.y)).rgb;
    sum += texture(texture0, uv - vec2(halfPixel.x, -halfPixel.y)).rgb;
    finalColor = vec4(sum / 8.0, 1.0);
}
""";

    // Dual-Kawase upsample: 8-tap tent around the texel.
    public const string BloomUpsampleFrag = """
#version 330

in vec2 fragTexCoord;
in vec4 fragColor;

uniform sampler2D texture0;
uniform vec4 colDiffuse;

uniform vec2 halfPixel;

out vec4 finalColor;

void main()
{
    vec2 uv = fragTexCoord;
    vec3 sum = texture(texture0, uv + vec2(-halfPixel.x * 2.0, 0.0)).rgb;
    sum += texture(texture0, uv + vec2(-halfPixel.x, halfPixel.y)).rgb * 2.0;
    sum += texture(texture0, uv + vec2(0.0, halfPixel.y * 2.0)).rgb;
    sum += texture(texture0, uv + vec2(halfPixel.x, halfPixel.y)).rgb * 2.0;
    sum += texture(texture0, uv + vec2(halfPixel.x * 2.0, 0.0)).rgb;
    sum += texture(texture0, uv + vec2(halfPixel.x, -halfPixel.y)).rgb * 2.0;
    sum += texture(texture0, uv + vec2(0.0, -halfPixel.y * 2.0)).rgb;
    sum += texture(texture0, uv + vec2(-halfPixel.x, -halfPixel.y)).rgb * 2.0;
    finalColor = vec4(sum / 12.0, 1.0);
}
""";
}
