namespace MissileCommandOverdrive.Entities;

public class Enemy
{
    public int Id;
    public string Variant = "standard"; // standard,fast,zig,stealth,decoy,split,shard,heavy,cruise,carrier,drone,ufoBomb,spit,hell
    public float X, Y;
    public float Sx, Sy; // start position
    public float Tx, Ty; // target position
    public float Speed;
    public float Progress; // 0..1 parametric
    public float Life = 1f;
    public float Resistance;
    public float ZigPhase;
    public float ZigAmp;
    public float HomingFactor;
    public bool Split;
    public float SplitAt = 0.5f;
    public bool HasSplit;
    public int Hp = 1;
    public float ReserveUntil; // auto-defense reservation timer
    public TargetInfo? Target;
    public bool Dead;

    // Per-missile trail buffer for curved trail rendering
    public List<(float X, float Y)> Trail = new(56);
    public const int MaxTrail = 52;

    // Runtime update fields (internal)
    public float _Vx, _Vy;
    public float _Dur;
    public float _Elapsed;
    public float _Fq;
    public float _Blast;
    public float _DeployAt;
    public bool _Deployed;
    public int _Val;
}

public class PlayerMissile
{
    public int Id;
    public float X, Y;
    public float Sx, Sy;
    public float Tx, Ty;
    public float Speed;
    public float Progress;
    public bool Detonated;
    public int BaseIndex;
    public bool Auto;

    // Per-missile trail buffer for curved trail rendering
    public List<(float X, float Y)> Trail = new(50);
    public const int MaxTrail = 46;

    // Runtime update fields
    public float _Vx, _Vy;
    public float _Dur;
    public float _Elapsed;
    public float _Blast;

    // HellRaiser homing fields
    public bool Hr;           // true = homing HellRaiser missile
    public float HrSpeed;     // constant speed
    public float HrTurn;      // max turn rate (rad/s)
    public float HrRetarget;  // countdown to next retarget check
    public int HrTargetId;    // entity Id of current target (enemy/ufo/raider)
    public string HrTargetKind = ""; // "enemy", "ufo", "raider"
    public float SquiggleAmp;
    public float SquiggleFreq;
    public float SquigglePhase;
}

public struct TargetInfo
{
    public string Type; // city, base, phalanx, hellRaiser, ground
    public float X, Y;
    public string? Id;
}
