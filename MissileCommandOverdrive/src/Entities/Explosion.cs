namespace MissileCommandOverdrive.Entities;

public class Explosion
{
    public float X, Y;
    public float Radius;
    public float MaxRadius;
    public float Life;
    public float MaxLife;
    public bool Player; // player-caused or enemy
    public bool Emp;
    public float Shake;
    public float Flash;
    public bool NoShake;
}

public class UFO
{
    public int Id;
    public float X, Y;
    public float Vx, Vy;
    public float Speed;
    public float Life = 1f;
    public float FireCd;
    public float BobPhase;
    public bool Boss;
    public int Hp;
    public bool Dead;
}

public class Raider
{
    public int Id;
    public float X, Y;
    public float Vx, Vy;
    public float Speed;
    public float Life = 1f;
    public float FireCd;
    public float Angle;
    public int Hp;
    public bool Dead;
}

public class Daemon
{
    public float X, Y;
    public float Vx, Vy;
    public float Life = 1f;
    public float FireCd;
    public float Phase;
    public int Hp;
    public bool Active;
}
