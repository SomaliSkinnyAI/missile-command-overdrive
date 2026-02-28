namespace MissileCommandOverdrive;

/// <summary>Variant stat lookups matching the JS vSpeed/vValue/vRes lambdas.</summary>
public static class VariantStats
{
    public const float BasePlayerSpeed = 640f;

    public static float Speed(string v, int level) => v switch
    {
        "fast" => 165 + level * 16,
        "zig" => 116 + level * 12,
        "split" => 112 + level * 10,
        "shard" => 190 + level * 18,
        "heavy" => 92 + level * 9,
        "ufoBomb" => 150 + level * 12,
        "stealth" => 100 + level * 10,
        "decoy" => 120 + level * 11,
        "cruise" => 145 + level * 12,
        "carrier" => 82 + level * 8,
        "drone" => 180 + level * 16,
        "spit" => 168 + level * 14,
        "hell" => 196 + level * 14,
        _ => 105 + level * 11
    };

    public static int Value(string v) => v switch
    {
        "fast" => 90,
        "zig" => 120,
        "split" => 170,
        "shard" => 80,
        "heavy" => 210,
        "ufoBomb" => 115,
        "stealth" => 140,
        "decoy" => 25,
        "cruise" => 190,
        "carrier" => 320,
        "drone" => 95,
        "spit" => 70,
        "hell" => 180,
        _ => 75
    };

    public static float Resistance(string v) => v switch
    {
        "heavy" => 0.38f,
        "zig" => 0.2f,
        "split" => 0.22f,
        "fast" => 0.14f,
        "ufoBomb" => 0.1f,
        "stealth" => 0.05f,
        "cruise" => 0.2f,
        "carrier" => 0.46f,
        "drone" => 0.12f,
        "spit" => 0.1f,
        "hell" => 0.18f,
        _ => 0.08f
    };

    public static float InterceptorSpeed(GameState s, float mult = 1f)
    {
        float lvlBoost = 1f + MathF.Min(1.7f, MathF.Max(0f, s.Level - 1) * 0.022f);
        float weatherDrag = s.Weather.Mode == "storm"
            ? 1f - Util.MathH.Clamp(s.Weather.Intensity * 0.08f, 0f, 0.08f)
            : 1f;
        return BasePlayerSpeed * lvlBoost * weatherDrag * mult;
    }
}
