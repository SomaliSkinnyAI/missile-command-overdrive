namespace MissileCommandOverdrive.Entities;

public class Explosion
{
    public int Id; // unique (s.NewId()) — used by Mothership.HitBy
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
    public float FlashT; // white damage-flash timer
    public bool Dead;
    public float ReserveUntil; // auto-defense reservation timer
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
    public float FlashT; // white damage-flash timer
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
    public float FlashT; // white damage-flash timer
    public bool Active;
}

public class Mothership
{
    public float X, Y;
    public float Vx;
    public float W;           // hull length (pixels)
    public int Hp;
    public int MaxHp;
    public float Phase;
    public float SpawnCd;
    public float ShieldFlash; // 0..1, decays over time after each hit
    public float AppearTime;  // time since summoned (used for slow fade-in)
    public bool Active;
    public bool ReachedMid;   // has center crossed screen-mid yet?
    public bool Dead;
    // Each explosion damages the mothership at most once (prevents per-frame re-damage bug).
    // Int ids (preallocated) instead of object refs: no growth reallocs in normal fights,
    // and dead Explosion instances aren't kept alive by the set.
    public HashSet<int> HitBy = new(256);
    // Deflector-shield state: toggles on/off with random intervals; blocks all damage while active.
    public bool ShieldActive;
    public float ShieldStateTimer; // time until next on/off toggle
    public float ShieldRippleT;    // 0..1 visual ripple timer after a deflected hit
}

public class Fighter
{
    public int Id;
    public float X, Y;
    public float Vx, Vy;
    public float Phase;
    public float Roll;       // wing-panel oscillation
    public int Hp = 1;
    public float FlashT;     // white damage-flash timer
    public float FireCd;
    public float Life = 1f;
    public bool Dead;
    public float TargetX, TargetY;
    public float StrafeT;    // timer for target re-pick
}
