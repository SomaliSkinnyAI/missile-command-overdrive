namespace MissileCommandOverdrive.Util;

public static class MathH
{
    public const float TAU = MathF.PI * 2f;

    public static float Clamp(float v, float min, float max)
        => MathF.Max(min, MathF.Min(max, v));

    public static float Rand(float a, float b)
        => a + Random.Shared.NextSingle() * (b - a);

    public static float Rand(float a, float b, Random rng)
        => a + rng.NextSingle() * (b - a);

    public static float Lerp(float a, float b, float t)
        => a + (b - a) * t;

    public static float AngleDelta(float from, float to)
        => MathF.Atan2(MathF.Sin(to - from), MathF.Cos(to - from));

    public static float EaseOut(float t)
    {
        t = Clamp(t, 0f, 1f);
        return 1f - MathF.Pow(1f - t, 3f);
    }

    public static float EaseIn(float t)
    {
        t = Clamp(t, 0f, 1f);
        return t * t * t;
    }

    public static (byte R, byte G, byte B) MixRgb((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, float t)
    {
        return (
            (byte)MathF.Round(Lerp(a.R, b.R, t)),
            (byte)MathF.Round(Lerp(a.G, b.G, t)),
            (byte)MathF.Round(Lerp(a.B, b.B, t))
        );
    }

    public static Raylib_cs.Color ToColor((byte R, byte G, byte B) rgb, byte a = 255)
        => new(rgb.R, rgb.G, rgb.B, a);

    public static Raylib_cs.Color WithAlpha(Raylib_cs.Color c, float alpha)
        => new(c.R, c.G, c.B, (byte)(Clamp(alpha, 0f, 1f) * 255f));

    public static float Round3(float v)
        => MathF.Round(v * 1000f) / 1000f;
}
