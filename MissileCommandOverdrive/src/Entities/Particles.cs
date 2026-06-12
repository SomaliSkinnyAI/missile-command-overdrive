namespace MissileCommandOverdrive.Entities;

/// <summary>§5 5.3 render-group tag. Alpha is 0 so default(struct) lands in the
/// default (alpha-blend) pass; the renderer groups per class to keep
/// BeginBlendMode flushes at one per group, never per particle.</summary>
public static class BlendClass
{
    public const byte Alpha = 0;
    public const byte Additive = 1;
}

/// <summary>§5 5.3 blast-anatomy spark roles. Burst is 0 so every legacy spawn
/// site keeps its existing physics/draw path untouched.</summary>
public static class SparkKind
{
    public const byte Burst = 0; // legacy omni burst (EMP ring, scrap, misc)
    public const byte Hot = 1;   // fast high-drag white-hot detonation core
    public const byte Ember = 2; // warm gravity ember, settles + cools on the ground
}

/// <summary>High-volume particle types as structs to reduce GC pressure.</summary>
public struct Spark
{
    public float X, Y, Vx, Vy;
    public float Life, MaxLife;
    public float Size;
    public byte R, G, B;
    public byte Kind; // SparkKind
    // §5 3.5 scrap spark: skips gravity and magnet-streams toward the HUD
    // scrap counter (s.ScrapHudX/Y) over its ~0.7 s life
    public bool Target;
}

public struct Smoke
{
    public float X, Y, Vx, Vy;
    public float Life, MaxLife;
    public float Size;
    public float Alpha;
    public byte Blend; // BlendClass — destruction smoke is Alpha (dark, translucent)
}

public struct Trail
{
    public float X, Y;
    public float Vx, Vy;
    public float Life, MaxLife;
    public float Size;
    public byte R, G, B;
}

public struct Debris
{
    public float X, Y, Vx, Vy;
    // §5 5.3: Life is a cooling clock only (hot → palette tone); it floors at 0
    // and never removes the chunk — litter persists until wave start or the
    // oldest-evicted pool cap.
    public float Life, MaxLife;
    public float Size;
    public float Rot, RotSpeed;
    public byte R, G, B;        // city-palette chunk color
    public bool Bounced;        // one bounce (restitution ~0.3), then it rests
    public bool Resting;        // ground litter: physics skipped entirely
}

public struct Shockwave
{
    public float X, Y;
    public float Radius, MaxRadius;
    public float Life, MaxLife;
}

public struct LightBurst
{
    public float X, Y;
    public float Life, MaxLife;
    public float Radius;
}

public struct MuzzleFlash
{
    public float X, Y;
    public float Angle;
    public float Life, MaxLife;
}

public struct Scorch
{
    public float X, Y;
    public float Radius;
    // §5 5.3: Life only drains during the wave-start fade (GameState.ScorchFadeT);
    // marks accumulate for the whole wave otherwise.
    public float Life;
    public float Heat; // 1 → 0 lingering ground glow right after the blast
}

/// <summary>§5 5.3: 1-frame white detonation flash quad (additive).</summary>
public struct BlastFlash
{
    public float X, Y;
    public float Size; // half-extent of the quad
    public float Rot;  // degrees
    public float Life; // ~Combat.BlastFlashLife — one to two frames
}

public struct ShootingStar
{
    public float X, Y, Vx, Vy;
    public float Life, MaxLife;
    public float Length;
}

public class FloatingText
{
    public string Text = "";
    public float X, Y;
    public float Life, MaxLife;
    public float Scale;
    public byte R, G, B;
}
