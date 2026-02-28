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

    public static Color VariantColor(string variant) => variant switch
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
        _ => Enemy
    };
}
