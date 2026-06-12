namespace MissileCommandOverdrive;

/// <summary>Per-variant definition (§4.4: INCREMENTAL VariantDef lookup — string
/// keys and the legacy Speed/Value/Resistance helpers stay so the ~140 call-site
/// literals are untouched; the §5 4.2 behavior fields live here so the wave
/// director can compose behaviors from the table instead of scattered switches).</summary>
public readonly struct VariantDef
{
    public readonly float SpeedBase, SpeedPerLvl;
    public readonly int Value;
    public readonly float Resistance;
    public readonly int Hp;
    public readonly float DeployMin, DeployMax; // carrier: drone-bay window (Progress fraction)
    public readonly float MirvChance;           // heavy: plan-time MIRV tag ratio (§4.3 plan stream)
    public readonly float CloakPing;            // stealth: decloak-ping period (s); 0 = none
    public readonly float ShieldRadius;         // shield: bubble radius (px); 0 = none

    public VariantDef(float speedBase, float speedPerLvl, int value, float resistance,
        int hp = 1, float deployMin = 0, float deployMax = 0, float mirvChance = 0,
        float cloakPing = 0, float shieldRadius = 0)
    {
        SpeedBase = speedBase; SpeedPerLvl = speedPerLvl;
        Value = value; Resistance = resistance; Hp = hp;
        DeployMin = deployMin; DeployMax = deployMax;
        MirvChance = mirvChance; CloakPing = cloakPing; ShieldRadius = shieldRadius;
    }
}

/// <summary>Variant stat lookups matching the JS vSpeed/vValue/vRes lambdas.</summary>
public static class VariantStats
{
    public const float BasePlayerSpeed = 640f;

    // Values mirror the pre-VariantDef switches exactly (calibration unchanged)
    static readonly VariantDef DefStandard = new(105, 11, 75, 0.08f);
    static readonly VariantDef DefFast = new(165, 16, 90, 0.14f);
    static readonly VariantDef DefZig = new(116, 12, 120, 0.2f);
    static readonly VariantDef DefSplit = new(112, 10, 170, 0.22f);
    static readonly VariantDef DefShard = new(190, 18, 80, 0.08f);
    static readonly VariantDef DefHeavy = new(92, 9, 210, 0.38f, mirvChance: 0.5f);
    static readonly VariantDef DefUfoBomb = new(150, 12, 115, 0.1f);
    static readonly VariantDef DefStealth = new(100, 10, 140, 0.05f, cloakPing: 1.4f);
    static readonly VariantDef DefDecoy = new(120, 11, 25, 0.08f);
    static readonly VariantDef DefCruise = new(145, 12, 190, 0.2f);
    static readonly VariantDef DefCarrier = new(82, 8, 320, 0.46f, hp: 3, deployMin: 0.35f, deployMax: 0.62f);
    static readonly VariantDef DefDrone = new(180, 16, 95, 0.12f);
    static readonly VariantDef DefSpit = new(168, 14, 70, 0.1f);
    static readonly VariantDef DefHell = new(196, 14, 180, 0.18f);
    // §5 4.2 shield drone: slow, high value, near-immune to blast falloff (res 1);
    // projects the bubble that blocks player explosions whose center is outside
    static readonly VariantDef DefShield = new(56, 5, 360, 1f, hp: 2, shieldRadius: 90f);

    public static VariantDef Def(string v) => v switch
    {
        "fast" => DefFast,
        "zig" => DefZig,
        "split" => DefSplit,
        "shard" => DefShard,
        "heavy" => DefHeavy,
        "ufoBomb" => DefUfoBomb,
        "stealth" => DefStealth,
        "decoy" => DefDecoy,
        "cruise" => DefCruise,
        "carrier" => DefCarrier,
        "drone" => DefDrone,
        "spit" => DefSpit,
        "hell" => DefHell,
        "shield" => DefShield,
        _ => DefStandard
    };

    public static float Speed(string v, int level)
    {
        var d = Def(v);
        // §5 4.1 (salvaged from the cut endless mode): speed growth stops at its
        // level-18 value — past that the director escalates via density/composition
        return d.SpeedBase + Math.Min(level, 18) * d.SpeedPerLvl;
    }

    public static int Value(string v) => Def(v).Value;

    public static float Resistance(string v) => Def(v).Resistance;

    public static float InterceptorSpeed(GameState s, float mult = 1f)
    {
        float lvlBoost = 1f + MathF.Min(1.7f, MathF.Max(0f, s.Level - 1) * 0.022f);
        float weatherDrag = s.Weather.Mode == "storm"
            ? 1f - Util.MathH.Clamp(s.Weather.Intensity * 0.08f, 0f, 0.08f)
            : 1f;
        // §5 4.3 AFTERBURNERS perk — single chokepoint, so AutoDefense's
        // intercept prediction and Combat.LaunchPlayer stay in agreement
        return BasePlayerSpeed * lvlBoost * weatherDrag * mult * s.Perks.InterceptorSpeedMult;
    }
}
