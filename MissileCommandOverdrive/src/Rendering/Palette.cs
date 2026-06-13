using Raylib_cs;

namespace MissileCommandOverdrive.Rendering;

/// <summary>Game color palette matching the JS C object.</summary>
public static class Palette
{
    public static readonly Color SkyA = new(4, 10, 35, 255);
    public static readonly Color SkyB = new(18, 34, 79, 255);
    public static readonly Color SkyC = new(19, 15, 47, 255);
    public static readonly Color GroundA = new(42, 37, 68, 255);
    public static readonly Color GroundB = new(9, 10, 22, 255);
    public static readonly Color Enemy = new(255, 201, 146, 255);
    public static readonly Color Fast = new(255, 224, 106, 255);
    public static readonly Color Zig = new(255, 149, 240, 255);
    public static readonly Color Split = new(255, 159, 111, 255);
    public static readonly Color Heavy = new(255, 107, 85, 255);
    public static readonly Color Ufo = new(152, 255, 211, 255);
    public static readonly Color UfoBomb = new(159, 233, 255, 255);
    public static readonly Color PhalanxColor = new(255, 217, 160, 255);
    public static readonly Color PhalanxGlow = new(255, 241, 195, 255);
    public static readonly Color Player = new(184, 255, 255, 255);
    public static readonly Color Ember = new(255, 184, 116, 255);
    public static readonly Color Ion = new(149, 236, 255, 255);

    /// <summary>§4.4 / §5 5.4 — the active theme's color authority. Selected by the
    /// s.Theme string (the single source of truth). All world color literals route
    /// through this; the returned struct is a value copy (cheap, read-only).</summary>
    public static ThemePalette Active(GameState s) => s.Theme switch
    {
        "xbox" => ThemePalette.Xbox,
        "recharged" => ThemePalette.Recharged,
        _ => ThemePalette.Modern,
    };

    /// <summary>§4.4 colorblind toggle — mirrored from Settings.ColorblindMode at the
    /// top of each frame so the static <see cref="VariantColor"/> (called from many
    /// draw sites without a GameState handle) can consult the alternate hue table
    /// without threading state through every signature.</summary>
    public static bool Colorblind;

    public static Color VariantColor(string variant)
    {
        // §4.4: colorblind-safe set differentiates by blue / orange / white / amber
        // (no red-vs-green pairs). Rides this one helper — NOT a parallel refactor.
        if (Colorblind)
            return variant switch
            {
                "fast" => new Color(255, 196, 60, 255),     // amber
                "zig" => new Color(120, 200, 255, 255),     // sky blue
                "split" or "shard" => new Color(255, 150, 40, 255), // orange
                "heavy" => new Color(255, 110, 30, 255),    // deep orange
                "ufoBomb" => new Color(180, 230, 255, 255), // pale blue
                "stealth" => new Color(70, 110, 210, 255),  // blue
                "decoy" => new Color(245, 245, 245, 255),   // white
                "cruise" => new Color(150, 210, 255, 255),  // light blue
                "carrier" => new Color(255, 170, 50, 255),  // orange-amber
                "drone" => new Color(200, 235, 255, 255),   // ice blue
                "spit" => new Color(255, 130, 30, 255),     // orange
                "hell" => new Color(255, 80, 0, 255),       // hot orange
                "shield" => new Color(120, 190, 255, 255),  // blue
                _ => new Color(255, 215, 130, 255),         // amber (default enemy)
            };

        return variant switch
        {
            "fast" => Fast,
            "zig" => Zig,
            "split" or "shard" => Split,
            "heavy" => Heavy,
            "ufoBomb" => UfoBomb,
            "stealth" => new Color(51, 76, 156, 255),
            "decoy" => new Color(68, 255, 102, 255),
            "cruise" => new Color(125, 255, 216, 255),
            "carrier" => new Color(255, 176, 127, 255),
            "drone" => new Color(156, 247, 255, 255),
            "spit" => new Color(255, 140, 106, 255),
            "hell" => new Color(255, 58, 45, 255),
            "shield" => new Color(122, 196, 255, 255),
            _ => Enemy
        };
    }
}
