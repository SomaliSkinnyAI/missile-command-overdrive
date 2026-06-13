using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive.Bosses;

/// <summary>§5 6.1 THE single boss damage + phase loop. Replaces the two
/// copy-pasted Mothership/Daemon damage blocks that used to live in
/// Combat.RunCollisions. Both bosses keep their concrete entity instances on
/// GameState (s.Mothership / s.Demon); this drives their shared
/// <see cref="BossBase"/> handle: shield-generator pods gate the hull, every HP
/// change advances phases (emitting BossPhase), and the killing blow emits
/// BossDeath + grants the guaranteed Epic perk.</summary>
public static class BossSystem
{
    /// <summary>Called once per frame from Combat.RunCollisions. Iterates a
    /// SNAPSHOT of the explosion count (collision spawns only APPEND, never
    /// remove, during this pass — same invariant the old blocks relied on).</summary>
    public static void RunDamage(GameState s)
    {
        if (s.Mothership != null) DamageMothership(s, s.Mothership);
        if (s.Demon != null) DamageDaemon(s, s.Demon);
    }

    // ---- Mothership: pods gate the hull shield --------------------------------
    static void DamageMothership(GameState s, Mothership m)
    {
        var boss = m.Boss;
        float forward = m.Vx >= 0 ? 1f : -1f;
        int expCount = s.Explosions.Count;

        for (int ei = 0; ei < expCount; ei++)
        {
            var e = s.Explosions[ei];
            if (!e.Player) continue;
            if (boss.HitBy.Contains(e.Id)) continue;
            float blastR = MathF.Max(24, e.Radius * 0.75f);

            // Pods first: while any pod lives, the hull shield is up and only pods
            // (and shield ripple) can be hit. A blast can only damage ONE pod.
            if (boss.ShieldUp)
            {
                bool hitPod = false;
                for (int p = 0; p < boss.PodCount; p++)
                {
                    if (boss.Pods[p].Dead) continue;
                    float px = m.X + boss.Pods[p].OffX * forward;
                    float py = m.Y + boss.Pods[p].OffY;
                    float dx = px - e.X, dy = py - e.Y;
                    float rr = boss.Pods[p].Rx + blastR;
                    if (dx * dx + dy * dy > rr * rr) continue;

                    boss.HitBy.Add(e.Id);
                    hitPod = true;
                    int dmg = e.Emp ? 2 : 1;
                    boss.Pods[p].Hp -= dmg;
                    boss.Pods[p].FlashT = 0.18f;
                    Combat.SpawnExpl(s, px, py, 26, 0.34f, 0.24f, player: false, flash: 0.06f, noShake: true);
                    SynthAudio.Hit(MathH.Clamp(px / s.W, 0, 1), 0.7f);
                    if (boss.Pods[p].Hp <= 0)
                    {
                        boss.Pods[p].Dead = true;
                        boss.Pods[p].DeathT = 1f;
                        Combat.SpawnExpl(s, px, py, 78, 0.9f, 0.36f, player: false, flash: 0.22f, heavy: true);
                        Combat.SpawnSmoke(s, px, py, 12, 1.1f);
                        s.AddTrauma(0.28f);
                        SynthAudio.Impact(MathH.Clamp(px / s.W, 0, 1), heavy: true);
                        if (boss.PodsAlive == 0)
                        {
                            // BOTH pods down → main shield collapses, hull exposed.
                            // Phase 1 is pod-gated, not HP-gated: set it explicitly.
                            m.ShieldRippleT = 1f;
                            s.Flash = MathF.Max(s.Flash, 0.3f);
                            SynthAudio.Thunder(MathH.Clamp(m.X / s.W, 0, 1), 0.8f);
                            if (boss.PhaseIndex < 1)
                            {
                                boss.PhaseIndex = 1;
                                EmitPhase(s, boss, m.X, m.Y);
                            }
                        }
                    }
                    break;
                }
                if (hitPod) continue;

                // Blast that missed all pods but reached the hull just ripples the shield.
                float hdx = (m.X - e.X) / (boss.HullRx + blastR);
                float hdy = (m.Y - e.Y) / (boss.HullRy + blastR);
                if (hdx * hdx + hdy * hdy <= 1f)
                {
                    boss.HitBy.Add(e.Id);
                    m.ShieldRippleT = 1f;
                    Combat.SpawnExpl(s, e.X, e.Y, 20, 0.28f, 0f, player: false, flash: 0f, noShake: true);
                }
                continue;
            }

            // Shield down: damage the hull directly.
            float gx = (m.X - e.X) / boss.HullRx;
            float gy = (m.Y - e.Y) / boss.HullRy;
            float bx = e.X - m.X, by = e.Y - m.Y;
            bool inHull = gx * gx + gy * gy <= 1.25f;
            bool inBlast = bx * bx + by * by <= (boss.HullRx + blastR) * (boss.HullRx + blastR);
            if (!(inHull || inBlast)) continue;

            boss.HitBy.Add(e.Id);
            int hdmg = e.Emp ? 2 : 1;
            ApplyHullDamage(s, boss, hdmg, m.X, m.Y);
            m.ShieldFlash = 1f; // hull impact flash (renderer reads ShieldFlash)
            Combat.SpawnExpl(s, e.X, e.Y, 24, 0.32f, 0.22f, player: false, flash: 0f, noShake: true);
            SynthAudio.Hit(MathH.Clamp(e.X / s.W, 0, 1), 0.85f);

            if (boss.Hp <= 0)
            {
                KillMothership(s, m);
                break;
            }
        }
    }

    static void KillMothership(GameState s, Mothership m)
    {
        float hullHalf = m.W * 0.5f;
        Combat.RegKill(s, 18000, m.X, m.Y);
        for (int k = 0; k < 14; k++)
        {
            float kt = k / 13f;
            float ex = m.X - hullHalf + kt * m.W;
            float ey = m.Y + MathH.Rand(-16, 12);
            Combat.SpawnExpl(s, ex, ey, MathH.Rand(82, 136),
                MathH.Rand(1.1f, 1.7f), 0.4f, player: true,
                flash: 0.12f + kt * 0.18f, heavy: true);
        }
        Combat.SpawnSmoke(s, m.X, m.Y, 46, 1.8f);
        s.Flash = MathF.Max(s.Flash, 0.6f);
        s.AddTrauma(0.5f);
        s.Note = "MOTHERSHIP DESTROYED";
        s.NoteT = 2.4f;
        EmitDeath(s, m.X, m.Y, 18000);
        s.Mothership = null;
    }

    // ---- Daemon: no pods, three escalating phases -----------------------------
    static void DamageDaemon(GameState s, Daemon d)
    {
        var boss = d.Boss;
        int expCount = s.Explosions.Count;
        for (int ei = 0; ei < expCount; ei++)
        {
            var e = s.Explosions[ei];
            if (!e.Player) continue;
            if (boss.HitBy.Contains(e.Id)) continue;
            float blastR = MathF.Max(30, e.Radius * 0.55f);
            float dx = d.X - e.X, dy = d.Y - e.Y;
            if (dx * dx + dy * dy > blastR * blastR) continue;

            boss.HitBy.Add(e.Id);
            int dmg = e.Emp ? 2 : 1;
            ApplyHullDamage(s, boss, dmg, d.X, d.Y);
            d.FlashT = 0.05f;
            Combat.SpawnExpl(s, d.X + MathH.Rand(-10, 10), d.Y + MathH.Rand(-6, 6),
                40, 0.55f, 0.32f, player: true, flash: 0.06f, noShake: true);
            SynthAudio.Hit(MathH.Clamp(d.X / s.W, 0, 1), 0.8f);

            if (boss.Hp <= 0)
            {
                Combat.RegKill(s, 3200, d.X, d.Y);
                Combat.SpawnExpl(s, d.X, d.Y, 170, 1.6f, 0.5f, player: true, flash: 0.44f, heavy: true);
                Combat.SpawnSmoke(s, d.X, d.Y, 30, 1.5f);
                s.Note = "DAEMON BANISHED";
                s.NoteT = 1.8f;
                EmitDeath(s, d.X, d.Y, 3200);
                s.Demon = null;
                break;
            }
        }
    }

    // ---- shared --------------------------------------------------------------

    /// <summary>Apply hull damage, sync the live Hp into the entity field via the
    /// caller, and advance + announce phases.</summary>
    static void ApplyHullDamage(GameState s, BossBase boss, int dmg, float x, float y)
    {
        boss.Hp -= dmg;
        if (boss.AdvancePhase() >= 0) EmitPhase(s, boss, x, y);
    }

    static void EmitPhase(GameState s, BossBase boss, float x, float y)
    {
        var def = boss.Phases[boss.PhaseIndex];
        if (def.Banner.Length > 0) { s.Note = def.Banner; s.NoteT = 2.2f; }
        s.AddTrauma(0.3f);
        s.Flash = MathF.Max(s.Flash, 0.18f);
        s.Events.Emit(EventKind.BossPhase, x, y, boss.PhaseIndex);
    }

    static void EmitDeath(GameState s, float x, float y, float value)
    {
        s.Events.Emit(EventKind.BossDeath, x, y, value);
        // Boss kill → guaranteed Epic perk (§5 4.3 reward hook). RegKill already
        // emitted the Kill; this is the milestone bonus on top.
        PerkSystem.GrantBossEpic(s);
    }
}
