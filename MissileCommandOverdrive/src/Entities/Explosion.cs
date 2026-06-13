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
    // §5 6.1 Hp/MaxHp are backed by the shared BossBase handle so the renderer +
    // the unified damage loop see one number. MaxHp replaces the renderer's
    // duplicated magic-6.
    public int Hp { get => (int)MathF.Ceiling(Boss.Hp); set => Boss.Hp = value; }
    public int MaxHp { get => (int)Boss.MaxHp; set => Boss.MaxHp = value; }
    public float FlashT; // white damage-flash timer
    public bool Active;
    public bool Scheduled; // §5 6.1: spawned by the wave scheduler (vs the 666 cheat)

    // §5 6.1 multi-phase boss handle (phases + unified damage loop live here)
    public Bosses.BossBase Boss = new() { Kind = Bosses.BossKind.Daemon };

    // §5 6.1 phase-1 rune-telegraphed meteors: a fixed pool of pending strikes,
    // each warning ≥1.2 s before impact so the player can intercept. The
    // interceptable explosion is spawned only when WarnT hits 0.
    public readonly MeteorWarn[] Meteors = new MeteorWarn[6];
    public float MeteorCd;

    // §5 6.1 phase-3 firewall sweep: a horizontal wall of fire crossing the sky,
    // telegraphed by SweepWarnT before SweepT begins.
    public float SweepWarnT;   // >0: charging (telegraph)
    public float SweepT;       // >0: active sweep
    public float SweepX;       // current leading-edge X
    public float SweepDir;     // +1 / -1
    public float SweepCd;
}

/// <summary>§5 6.1 one pending Daemon meteor: a rune marks the ground impact
/// point for ≥1.2 s (WarnT) before the interceptable blast fires.</summary>
public struct MeteorWarn
{
    public bool Active;
    public float X, Y;   // ground impact point
    public float WarnT;  // counts down; impact at 0
    public float MaxWarn;
}

public class Mothership
{
    public float X, Y;
    public float Vx;
    public float W;           // hull length (pixels)
    // §5 6.1 Hp/MaxHp backed by the shared BossBase handle (one number for the
    // renderer + the unified damage loop).
    public int Hp { get => (int)MathF.Ceiling(Boss.Hp); set => Boss.Hp = value; }
    public int MaxHp { get => (int)Boss.MaxHp; set => Boss.MaxHp = value; }
    public float Phase;
    public float SpawnCd;
    public float ShieldFlash; // 0..1, decays over time after each hit
    public float AppearTime;  // time since summoned (used for slow fade-in)
    public bool Active;
    public bool ReachedMid;   // has center crossed screen-mid yet?
    public bool Dead;
    public bool Scheduled;    // §5 6.1: spawned by the wave scheduler (vs the 777 cheat)
    // §5 6.1 readable shield: ShieldActive is now DERIVED from the boss pods (true
    // while either generator pod lives) — no longer an RNG on/off timer. Kept as a
    // field so the renderer/Phase-5 audio (s.Mothership.ShieldActive) read it
    // unchanged; MothershipSystem.Update refreshes it from Boss.ShieldUp each tick.
    public bool ShieldActive;
    public float ShieldRippleT;    // 0..1 visual ripple timer after a deflected hit

    // §5 6.1 multi-phase boss handle (HP, phases, pods, unified damage loop)
    public Bosses.BossBase Boss = new() { Kind = Bosses.BossKind.Mothership };

    // §5 6.1 turbolaser volleys: telegraphed by ~0.5 s tracer lines before firing.
    public readonly Turbolaser[] Lasers = new Turbolaser[3];
    public float LaserCd;
}

/// <summary>§5 6.1 one telegraphed Mothership turbolaser: a thin tracer line aims
/// at the target for WarnT (~0.5 s) before the bolt actually launches.</summary>
public struct Turbolaser
{
    public bool Active;
    public float Ox, Oy;   // muzzle origin (hull underside)
    public float Tx, Ty;   // aim point
    public float WarnT;    // telegraph countdown; fires at 0
    public float MaxWarn;
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
