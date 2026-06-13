using MissileCommandOverdrive.Entities;

namespace MissileCommandOverdrive.Bosses;

/// <summary>§5 6.1 boss kind — identifies the concrete entity behind a
/// <see cref="BossBase"/> handle so the unified damage loop can dispatch the
/// (small) entity-specific kill/spark work without a virtual call from Combat.</summary>
public enum BossKind { Mothership, Daemon }

/// <summary>§5 6.1 one phase of a multi-phase boss: entered when the boss drops
/// to or below <see cref="HpFraction"/> of MaxHp. Attacks within the phase are
/// driven by the concrete system (MothershipSystem / DemonSystem) reading the
/// boss's current <see cref="BossBase.PhaseIndex"/>; this record carries only the
/// authored threshold + an intro banner. Every attack a phase fires MUST
/// telegraph ≥0.5 s (meteors ≥1.2 s) — enforced in the concrete systems.</summary>
public readonly record struct BossPhaseDef(float HpFraction, string Banner);

/// <summary>§5 6.1 base boss model. The concrete bosses (Mothership, Daemon)
/// embed one of these as <c>Boss</c> and remain the live entity instances on
/// GameState; this carries the SHARED contract the unified Combat damage loop
/// drives: an ellipse hull hitbox, HP, the ordered phase table + current index,
/// and (Mothership only) two destructible shield-generator pods that gate the
/// main shield. Pods live here — not on a subclass — so the one damage loop in
/// Combat.RunCollisions reads/writes them generically.</summary>
public sealed class BossBase
{
    public BossKind Kind;
    public float Hp;
    public float MaxHp;
    // Ellipse hull hitbox half-extents, centred on the entity's X/Y (the
    // entity owns its own X/Y; the loop reads them through the dispatch).
    public float HullRx, HullRy;

    // Ordered high→low HP-fraction thresholds. PhaseIndex advances when Hp/MaxHp
    // drops to/below the NEXT phase's fraction. Index 0 is the entry phase.
    public BossPhaseDef[] Phases = [];
    public int PhaseIndex;

    // §5 6.1 (Mothership) shield-generator pods. Two glowing ellipse sub-hitboxes
    // at hull offsets; the main hull shield only drops once BOTH are destroyed.
    // Daemon leaves PodCount = 0 (no pods).
    public int PodCount;
    public readonly Pod[] Pods = new Pod[2];

    // Each player explosion damages a boss (or a single pod) at most once.
    public readonly HashSet<int> HitBy = new(256);

    /// <summary>True when the hull is damage-immune: a Mothership whose pods are
    /// still alive. Daemon (PodCount==0) is never shielded.</summary>
    public bool ShieldUp => PodCount > 0 && PodsAlive > 0;

    public int PodsAlive
    {
        get
        {
            int n = 0;
            for (int i = 0; i < PodCount; i++) if (!Pods[i].Dead) n++;
            return n;
        }
    }

    /// <summary>Current HP as a 0..1 fraction of MaxHp.</summary>
    public float HpFraction => MaxHp > 0 ? Math.Clamp(Hp / MaxHp, 0f, 1f) : 0f;

    /// <summary>Advance PhaseIndex if HP has fallen into the next band. Returns the
    /// new index when a threshold was crossed this call, else -1. Called from the
    /// unified damage loop after every HP change.</summary>
    public int AdvancePhase()
    {
        int crossed = -1;
        while (PhaseIndex + 1 < Phases.Length
               && HpFraction <= Phases[PhaseIndex + 1].HpFraction)
        {
            PhaseIndex++;
            crossed = PhaseIndex;
        }
        return crossed;
    }
}

/// <summary>§5 6.1 destructible shield-generator pod (Mothership). A small glowing
/// ellipse sub-hitbox offset from the hull centre; killing both drops the hull
/// shield. <see cref="OffX"/>/<see cref="OffY"/> are hull-local; world position
/// is entity X/Y + offsets (mirrored by hull facing where the system computes it).</summary>
public struct Pod
{
    public float OffX, OffY; // hull-local offset (world = boss X/Y + off*facing)
    public float Rx, Ry;     // ellipse half-extents
    public int Hp;
    public int MaxHp;
    public float FlashT;     // white hit-flash timer
    public float DeathT;     // 0..1 death-burst timer (counts down)
    public bool Dead;
}
