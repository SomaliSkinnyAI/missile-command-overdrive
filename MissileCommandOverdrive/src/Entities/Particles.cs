namespace MissileCommandOverdrive.Entities;

/// <summary>High-volume particle types as structs to reduce GC pressure.</summary>
public struct Spark
{
    public float X, Y, Vx, Vy;
    public float Life, MaxLife;
    public float Size;
    public byte R, G, B;
}

public struct Smoke
{
    public float X, Y, Vx, Vy;
    public float Life, MaxLife;
    public float Size;
    public float Alpha;
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
    public float Life, MaxLife;
    public float Size;
    public float Rot, RotSpeed;
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
    public float Life;
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
