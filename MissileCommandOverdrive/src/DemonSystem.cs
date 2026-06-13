using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Bosses;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Rolling 8-char secret-code buffer for easter eggs (e.g. "666" → summon demon).</summary>
public static class SecretCode
{
    public static string Buffer = "";
}

/// <summary>Daemon boss (§5 6.1). Scheduled every 10th wave (10/20…) and also
/// summonable by typing "666". Three escalating phases: (1) rune-telegraphed
/// meteor impacts the player can intercept, (2) ≤half HP summons hell-variant
/// fighters, (3) a telegraphed firewall sweep across the sky. Every attack
/// telegraphs (meteors ≥1.2 s, firewall ≥0.8 s).</summary>
public static class DemonSystem
{
    public const int BaseHp = 14;

    public static void Summon(GameState s) => Spawn(s, scheduled: false);

    /// <summary>§5 6.1 scheduled spawn (wave director).</summary>
    public static void Spawn(GameState s, bool scheduled)
    {
        if (s.Demon != null) return;
        if (s.Intro || s.GameOver) return;

        var d = new Daemon
        {
            X = s.W * 0.5f,
            Y = s.HorizonY * 0.22f,
            Vx = scheduled ? 130f : 160f,
            Vy = 0f,
            Life = scheduled ? 9999f : 18f, // scheduled bosses stay until killed
            FireCd = 0.7f,
            Phase = MathH.Rand(0f, MathH.TAU),
            Active = true,
            Scheduled = scheduled,
            MeteorCd = 1.2f,
            SweepCd = 7f
        };
        var boss = d.Boss;
        boss.HullRx = 30f;
        boss.HullRy = 26f;
        boss.PodCount = 0; // no pods — never shielded
        boss.Phases =
        [
            new(1.00f, ""),                         // 0: meteor storm
            new(0.50f, "PHASE II — HELL LEGION"),   // 1: ≤half HP, summons hell fans
            new(0.22f, "PHASE III — FIREWALL"),     // 2: firewall sweeps
        ];
        d.Hp = BaseHp; d.MaxHp = BaseHp; // backed by boss handle

        s.Demon = d;
        s.Note = scheduled ? "WARNING: DAEMON UNLEASHED" : "EASTER EGG: DAEMON UNLEASHED";
        s.NoteT = 2.0f;
        s.Flash = MathF.Max(s.Flash, 0.28f);
        s.AddTrauma(0.35f);
        SynthAudio.Thunder(0.5f, 1.0f);
    }

    public static void Update(GameState s, float dt)
    {
        var d = s.Demon;
        if (d == null || !d.Active) return;
        var boss = d.Boss;

        d.Life -= dt;
        if (d.Life <= 0 || d.Hp <= 0)
        {
            // Death is handled by the unified Combat boss loop (score + reward).
            // This branch only catches the cheat-summon lifetime expiry.
            s.Demon = null;
            s.Note = "Daemon banished";
            s.NoteT = 1.2f;
            return;
        }

        d.X += d.Vx * dt;
        d.Y = s.HorizonY * 0.2f + MathF.Sin(s.Time * 1.9f + d.Phase) * 30f;
        if (d.X < 70 || d.X > s.W - 70) d.Vx *= -1f;
        d.Phase += dt * 0.5f;

        // ---- Phase 1+: rune-telegraphed meteors (interceptable) --------------
        UpdateMeteors(s, d, dt);
        d.MeteorCd -= dt;
        if (d.MeteorCd <= 0f)
        {
            // faster cadence in later phases
            d.MeteorCd = boss.PhaseIndex >= 1 ? MathH.Rand(0.9f, 1.6f) : MathH.Rand(1.5f, 2.4f);
            TelegraphMeteor(s, d);
        }

        // ---- Phase 2: summon hell-variant fighters ---------------------------
        if (boss.PhaseIndex >= 1)
        {
            d.FireCd -= dt;
            if (d.FireCd <= 0f)
            {
                d.FireCd = MathH.Rand(3.2f, 5.0f);
                SummonHellFan(s, d);
            }
        }

        // ---- Phase 3: firewall sweep -----------------------------------------
        if (boss.PhaseIndex >= 2)
            UpdateFirewall(s, d, dt);
    }

    // ---- meteors -------------------------------------------------------------
    static void TelegraphMeteor(GameState s, Daemon d)
    {
        int slot = -1;
        for (int i = 0; i < d.Meteors.Length; i++)
            if (!d.Meteors[i].Active) { slot = i; break; }
        if (slot < 0) return;

        // §4.3 in-fight targeting on the cosmetic stream
        float tx = MathH.Clamp(d.X + MathH.Rand(-s.W * 0.4f, s.W * 0.4f), 60, s.W - 60);
        float ty = s.GroundY - MathH.Rand(0, 24);
        d.Meteors[slot] = new MeteorWarn
        {
            Active = true, X = tx, Y = ty,
            WarnT = 1.35f, MaxWarn = 1.35f // ≥1.2 s warning (contract)
        };
        SynthAudio.EnemyLaunch(MathH.Clamp(tx / s.W, 0, 1));
    }

    static void UpdateMeteors(GameState s, Daemon d, float dt)
    {
        for (int i = 0; i < d.Meteors.Length; i++)
        {
            if (!d.Meteors[i].Active) continue;
            d.Meteors[i].WarnT -= dt;
            if (d.Meteors[i].WarnT <= 0f)
            {
                // Strike: a real (interceptable) blast at the telegraphed spot.
                float bx = d.Meteors[i].X, by = d.Meteors[i].Y;
                Combat.SpawnExpl(s, bx, by,
                    maxRadius: MathH.Rand(76, 112),
                    life: 1.1f, shakeTime: 0.42f,
                    player: false, flash: 0.1f, heavy: true);
                s.Flash = MathF.Max(s.Flash, 0.08f);
                SynthAudio.Impact(MathH.Clamp(bx / s.W, 0, 1), heavy: true);
                d.Meteors[i].Active = false;
            }
        }
    }

    // ---- hell-variant fighter summon ----------------------------------------
    static void SummonHellFan(GameState s, Daemon d)
    {
        // Reuse the enemy projectile factory with the "hell" variant aimed at a
        // city/ground point — a telegraphed (slow, glowing) inbound threat.
        float tx, ty;
        if (s.AliveCities > 0)
        {
            var c = PickAliveCity(s);
            tx = c.X; ty = c.Y - 10;
        }
        else { tx = MathH.Rand(s.W * 0.15f, s.W * 0.85f); ty = s.GroundY - 14; }
        var t = new TargetInfo { Type = s.AliveCities > 0 ? "city" : "ground", X = tx, Y = ty };
        Combat.CreateEnemyProjectile(s, "hell", d.X, d.Y + 10, t);
        SynthAudio.EnemyLaunch(MathH.Clamp(d.X / s.W, 0, 1));
    }

    // ---- firewall sweep ------------------------------------------------------
    static void UpdateFirewall(GameState s, Daemon d, float dt)
    {
        if (d.SweepT > 0f)
        {
            // Active sweep: advance the wall; ignite a rolling blast at the edge.
            d.SweepT -= dt;
            float prevX = d.SweepX;
            d.SweepX += d.SweepDir * (s.W / 2.4f) * dt; // crosses screen in ~2.4 s
            // emit interceptable blasts at intervals along the leading edge
            if (MathF.Abs(d.SweepX - prevX) > 0 && ((int)(d.SweepX / 70f) != (int)(prevX / 70f)))
            {
                float by = s.GroundY - MathH.Rand(0, 18);
                Combat.SpawnExpl(s, MathH.Clamp(d.SweepX, 20, s.W - 20), by,
                    maxRadius: 66, life: 0.85f, shakeTime: 0.3f,
                    player: false, flash: 0.06f, heavy: true);
                SynthAudio.Thunder(MathH.Clamp(d.SweepX / s.W, 0, 1), 0.35f);
            }
            return;
        }
        if (d.SweepWarnT > 0f)
        {
            d.SweepWarnT -= dt;
            if (d.SweepWarnT <= 0f)
            {
                d.SweepT = 2.4f;
                d.SweepDir = d.SweepX < s.W * 0.5f ? 1f : -1f;
            }
            return;
        }
        d.SweepCd -= dt;
        if (d.SweepCd <= 0f)
        {
            d.SweepCd = MathH.Rand(7f, 10f);
            d.SweepWarnT = 0.9f; // ≥0.8 s telegraph
            d.SweepDir = RandHelper.Chance(0.5f) ? 1f : -1f;
            d.SweepX = d.SweepDir > 0 ? 0f : s.W;
            s.Note = "FIREWALL CHARGING";
            s.NoteT = 1.0f;
            SynthAudio.Thunder(0.5f, 0.6f);
        }
    }

    static City PickAliveCity(GameState s)
    {
        int n = 0;
        for (int i = 0; i < s.Cities.Count; i++) if (!s.Cities[i].Destroyed) n++;
        int pick = RandHelper.NextInt(0, n);
        for (int i = 0; i < s.Cities.Count; i++)
        {
            if (s.Cities[i].Destroyed) continue;
            if (pick-- == 0) return s.Cities[i];
        }
        return s.Cities[0];
    }
}
