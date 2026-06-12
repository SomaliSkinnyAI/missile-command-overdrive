namespace MissileCommandOverdrive.Entities;

/// <summary>Fixed-capacity position ring for missile trails (§5 2.6).
/// Index 0 = newest sample, ages forward — replaces List.Insert(0) memmoves.</summary>
public sealed class TrailRing
{
    readonly (float X, float Y)[] _buf;
    int _head; // slot of the newest sample
    public int Count { get; private set; }

    public TrailRing(int capacity) => _buf = new (float X, float Y)[capacity];

    public (float X, float Y) this[int age]
    {
        get
        {
            int i = _head + age;
            if (i >= _buf.Length) i -= _buf.Length;
            return _buf[i];
        }
    }

    public void Push(float x, float y)
    {
        _head = _head == 0 ? _buf.Length - 1 : _head - 1;
        _buf[_head] = (x, y);
        if (Count < _buf.Length) Count++;
    }
}

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
    public float Hp = 1; // float: phalanx chips carriers fractionally (0.9/hit)
    public float FlashT; // white damage-flash timer (damaged but not killed)
    public float ReserveUntil; // auto-defense reservation timer
    public TargetInfo? Target;
    public bool Dead;

    // §5 4.2 behavioral roster
    public bool Mirv;            // plan-tagged heavy: splits into 3 warheads at SplitAt
    public float TelegraphT;     // pulse clock — runs while a telegraph glow is active
    public bool TelegraphPinged; // one-shot latch for the telegraph audio cue
    public float PingT;          // stealth: countdown to the next decloak ping
    public float ShieldFlashT;   // shield: bubble ripple timer on a blocked blast

    // Per-missile trail ring for curved trail rendering
    public TrailRing Trail = new(MaxTrail);
    public const int MaxTrail = 52;

    // Runtime update fields (internal)
    public float _Vx, _Vy;
    public float _Dur;
    public float _Elapsed;
    public float _Fq;
    public float _Blast;
    public float DeployAt;  // carrier: Progress fraction where the drone bay opens
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

    // Per-missile trail ring for curved trail rendering (§5 4.3: MIRV
    // INTERCEPTOR children pass a shorter cap)
    public TrailRing Trail;
    public const int MaxTrail = 46;
    public PlayerMissile(int trailCap = MaxTrail) => Trail = new TrailRing(trailCap);

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
    public int HrTargetId;    // entity Id passed by HellRaiserSystem at launch
    public string HrTargetKind = ""; // launch handoff only; cleared once adopted as a reference
    // Direct target references (§5 2.6) — no per-frame id lookups/closures
    public Enemy? HrTargetEnemy;
    public UFO? HrTargetUfo;
    public Raider? HrTargetRaider;
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
