using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Bosses;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Star Destroyer mothership boss (§5 6.1). Scheduled every 5th wave
/// (5/15/25…) and also summonable by typing "777". Slowly crosses the sky,
/// deploys fighters at mid-screen, fires telegraphed turbolaser volleys, and is
/// guarded by two destructible shield-generator pods — the hull shield only
/// drops once BOTH pods die (replacing the old unreadable RNG shield timer).</summary>
public static class MothershipSystem
{
    public const int BaseHp = 22;
    const int PodHp = 4;

    public static void Summon(GameState s) => Spawn(s, scheduled: false);

    /// <summary>§5 6.1 scheduled spawn (wave director). `scheduled` flags it so the
    /// banner reads as an encounter rather than the easter-egg note.</summary>
    public static void Spawn(GameState s, bool scheduled)
    {
        if (s.Mothership != null) return;
        if (s.Intro || s.GameOver) return;

        // §4.3: spawn FACING is deterministic for scheduled bosses (Level-keyed)
        // so a daily seed produces the same encounter; the 777 cheat uses cosmetic.
        bool fromLeft = scheduled ? (s.Level & 1) == 0 : RandHelper.Chance(0.5f);
        float hullW = MathF.Max(340, s.W * 0.32f);
        // Scheduled bosses enter already partly on-screen (so they read as a
        // prominent threat immediately, with pods/shield visible) and traverse
        // a little faster toward mid; the 777 cheat keeps the slow edge entrance.
        float startX = fromLeft
            ? (scheduled ? s.W * 0.18f : -hullW * 0.6f)
            : (scheduled ? s.W * 0.82f : s.W + hullW * 0.6f);
        float vx = (fromLeft ? 1f : -1f) * (scheduled ? 42f : 33f);
        float y = s.HorizonY * (scheduled ? 0.52f : 0.38f);

        var m = new Mothership
        {
            X = startX,
            Y = y,
            Vx = vx,
            W = hullW,
            Phase = MathH.Rand(0, MathH.TAU),
            SpawnCd = 4.5f,
            AppearTime = 0,
            Active = true,
            ReachedMid = false,
            Scheduled = scheduled,
            ShieldActive = true, // pods alive → hull shielded from the start
            ShieldRippleT = 0f,
            LaserCd = 3.0f
        };
        // §5 6.1 boss handle: HP + phase table + two shield-generator pods.
        var boss = m.Boss;
        boss.HullRx = hullW * 0.5f * 0.92f;
        boss.HullRy = 42f;
        boss.PodCount = 2;
        boss.Pods[0] = NewPod(-hullW * 0.30f, 30f);
        boss.Pods[1] = NewPod(hullW * 0.30f, 30f);
        // Phase 1 threshold is -1 (unreachable by HP): the pod-collapse path sets
        // PhaseIndex=1 explicitly. Phase 2 is the HP-driven reactor-critical band.
        boss.Phases =
        [
            new(1.00f, ""),                                  // 0: shielded approach
            new(-1f, "HULL EXPOSED — ALL BATTERIES FIRE"),   // 1: pods down (manual)
            new(0.40f, "REACTOR CRITICAL"),                  // 2: <=40% hull — frantic volleys
        ];
        m.Hp = BaseHp; m.MaxHp = BaseHp; // backed by boss handle

        s.Mothership = m;
        s.Note = scheduled ? "WARNING: IMPERIAL MOTHERSHIP INBOUND"
                           : "EASTER EGG: IMPERIAL MOTHERSHIP INBOUND";
        s.NoteT = 3.0f;
        s.Flash = MathF.Max(s.Flash, 0.22f);
        s.AddTrauma(0.3f);
        SynthAudio.Thunder(0.5f, 0.9f);
    }

    static Pod NewPod(float offX, float offY) => new()
    {
        OffX = offX, OffY = offY, Rx = 22f, Ry = 16f, Hp = PodHp, MaxHp = PodHp
    };

    public static void Update(GameState s, float dt)
    {
        var m = s.Mothership;
        if (m == null || !m.Active) return;
        var boss = m.Boss;

        m.AppearTime += dt;
        m.X += m.Vx * dt;
        m.Phase += dt * 0.6f;
        m.ShieldFlash = MathF.Max(0, m.ShieldFlash - dt * 1.8f);
        m.ShieldRippleT = MathF.Max(0, m.ShieldRippleT - dt * 2.2f);

        // §5 6.1 readable shield: derived from the pods, not an RNG timer.
        m.ShieldActive = boss.ShieldUp;
        // Pod hit-flash + death-burst timers
        for (int p = 0; p < boss.PodCount; p++)
        {
            if (boss.Pods[p].FlashT > 0) boss.Pods[p].FlashT = MathF.Max(0, boss.Pods[p].FlashT - dt);
            if (boss.Pods[p].DeathT > 0) boss.Pods[p].DeathT = MathF.Max(0, boss.Pods[p].DeathT - dt * 1.6f);
        }

        // Reached-mid milestone: once the hull center crosses screen center, start deploying fighters
        float mid = s.W * 0.5f;
        if (!m.ReachedMid && ((m.Vx > 0 && m.X >= mid) || (m.Vx < 0 && m.X <= mid)))
        {
            m.ReachedMid = true;
            m.SpawnCd = 0.6f; // first wave fires quickly after arriving
            s.Note = "DEPLOYING FIGHTERS";
            s.NoteT = 2.0f;
        }

        // Fighter deploy: small fast UFOs launched from the hangar bay (underside of the hull)
        if (m.ReachedMid)
        {
            m.SpawnCd -= dt;
            if (m.SpawnCd <= 0)
            {
                m.SpawnCd = MathH.Rand(3.2f, 5.8f);
                DeployFighter(s, m);
                SynthAudio.EnemyLaunch(MathH.Clamp(m.X / s.W, 0, 1));
            }
        }

        // §5 6.1 telegraphed turbolaser volleys — only once on-screen and pods
        // are down (the exposed-hull phase opens up its batteries). Each bolt is
        // warned by a ~0.5 s tracer line (UpdateLasers) before it actually fires.
        if (m.ReachedMid)
        {
            UpdateLasers(s, m, dt);
            m.LaserCd -= dt;
            if (m.LaserCd <= 0f && boss.PhaseIndex >= 1)
            {
                // reactor-critical (phase 2) fires faster
                m.LaserCd = boss.PhaseIndex >= 2 ? MathH.Rand(1.3f, 2.2f) : MathH.Rand(2.4f, 3.6f);
                TelegraphLaser(s, m);
            }
        }

        // Off-screen exit (after traversal complete)
        if ((m.Vx > 0 && m.X > s.W + m.W) || (m.Vx < 0 && m.X < -m.W))
        {
            s.Mothership = null;
            s.Note = "Mothership retreated";
            s.NoteT = 1.4f;
            return;
        }
    }

    /// <summary>§5 6.1 telegraph a turbolaser: pick a target, arm the tracer warn.
    /// The bolt is launched (as an enemy projectile) when WarnT reaches 0.</summary>
    static void TelegraphLaser(GameState s, Mothership m)
    {
        // find a free slot
        int slot = -1;
        for (int i = 0; i < m.Lasers.Length; i++)
            if (!m.Lasers[i].Active) { slot = i; break; }
        if (slot < 0) return;

        float ox = m.X + MathH.Rand(-m.W * 0.30f, m.W * 0.30f);
        float oy = m.Y + 26;
        float tx, ty;
        // §4.3 in-fight targeting uses the cosmetic stream
        var alive = PickAliveCity(s);
        if (alive != null) { tx = alive.X; ty = alive.Y - 8; }
        else { tx = MathH.Rand(s.W * 0.15f, s.W * 0.85f); ty = s.GroundY - 14; }

        m.Lasers[slot] = new Turbolaser
        {
            Active = true, Ox = ox, Oy = oy, Tx = tx, Ty = ty,
            WarnT = 0.55f, MaxWarn = 0.55f // ≥0.5 s telegraph (contract)
        };
        SynthAudio.EnemyLaunch(MathH.Clamp(ox / s.W, 0, 1));
    }

    static void UpdateLasers(GameState s, Mothership m, float dt)
    {
        for (int i = 0; i < m.Lasers.Length; i++)
        {
            if (!m.Lasers[i].Active) continue;
            m.Lasers[i].WarnT -= dt;
            if (m.Lasers[i].WarnT <= 0f)
            {
                // Fire: a fast intercepting bolt aimed at the telegraphed point.
                var t = new TargetInfo
                {
                    Type = s.AliveCities > 0 ? "city" : "ground",
                    X = m.Lasers[i].Tx, Y = m.Lasers[i].Ty
                };
                Combat.CreateEnemyProjectile(s, "fast", m.Lasers[i].Ox, m.Lasers[i].Oy, t);
                SynthAudio.Thunder(MathH.Clamp(m.Lasers[i].Ox / s.W, 0, 1), 0.4f);
                m.Lasers[i].Active = false;
            }
        }
    }

    static City? PickAliveCity(GameState s)
    {
        // cosmetic-stream index over alive cities (no LINQ alloc); null if none
        int n = 0;
        for (int i = 0; i < s.Cities.Count; i++) if (!s.Cities[i].Destroyed) n++;
        if (n == 0) return null;
        int pick = RandHelper.NextInt(0, n);
        for (int i = 0; i < s.Cities.Count; i++)
        {
            if (s.Cities[i].Destroyed) continue;
            if (pick-- == 0) return s.Cities[i];
        }
        return null;
    }

    /// <summary>True while a live mothership is on-screen — other enemy spawning is paused.</summary>
    public static bool HoldSpawning(GameState s) => s.Mothership != null && s.Mothership.Active;

    static void DeployFighter(GameState s, Mothership m)
    {
        // Launch a unique small TIE-style fighter from the hangar bay underside.
        // Fighters are faster, smaller, and have a dogfight-style diving trajectory (not just cruising).
        float bayX = m.X + MathH.Rand(-m.W * 0.28f, m.W * 0.28f);
        float bayY = m.Y + 24;

        // Pick a random city (or ground spot) as strafe target (alloc-free, §2.6)
        float tx, ty;
        var pick = PickAliveCity(s);
        if (pick != null)
        {
            tx = pick.X;
            ty = pick.Y - 10;
        }
        else
        {
            tx = MathH.Rand(s.W * 0.15f, s.W * 0.85f);
            ty = s.GroundY - 16;
        }

        float ang = MathF.Atan2(ty - bayY, tx - bayX);
        float speed = MathH.Rand(120, 170);

        s.Fighters.Add(new Fighter
        {
            Id = s.NewId(),
            X = bayX,
            Y = bayY,
            Vx = MathF.Cos(ang) * speed,
            Vy = MathF.Sin(ang) * speed,
            Phase = RandHelper.Next01() * MathH.TAU,
            Hp = 2,
            FireCd = MathH.Rand(1.5f, 3.0f),
            TargetX = tx,
            TargetY = ty,
            StrafeT = MathH.Rand(2.4f, 3.8f)
        });
    }

    public static void UpdateFighters(GameState s, float dt)
    {
        for (int i = s.Fighters.Count - 1; i >= 0; i--)
        {
            var f = s.Fighters[i];
            f.Phase += dt * 6f;
            f.Roll = MathF.Sin(s.Time * 8f + f.Phase) * 0.35f;
            f.StrafeT -= dt;
            if (f.StrafeT <= 0f)
            {
                var pick = PickAliveCity(s); // alloc-free (§2.6)
                if (pick != null)
                {
                    f.TargetX = pick.X;
                    f.TargetY = pick.Y - 10;
                }
                else
                {
                    f.TargetX = MathH.Rand(s.W * 0.1f, s.W * 0.9f);
                    f.TargetY = s.GroundY - 12;
                }
                f.StrafeT = MathH.Rand(2.2f, 3.6f);
            }

            // Steer toward target with bounded turn rate (tight, twitchy fighter feel)
            float desiredAng = MathF.Atan2(f.TargetY - f.Y, f.TargetX - f.X);
            float currentAng = MathF.Atan2(f.Vy, f.Vx);
            float angDelta = MathH.AngleDelta(currentAng, desiredAng);
            float turn = MathH.Clamp(angDelta, -4.2f * dt, 4.2f * dt);
            float speed = MathF.Sqrt(f.Vx * f.Vx + f.Vy * f.Vy);
            float newAng = currentAng + turn;
            float targetSpeed = MathH.Clamp(speed, 135f, 175f);
            f.Vx = MathF.Cos(newAng) * targetSpeed;
            f.Vy = MathF.Sin(newAng) * targetSpeed;
            f.X += f.Vx * dt;
            f.Y += f.Vy * dt;

            // Fire periodic small projectiles
            f.FireCd -= dt;
            if (f.FireCd <= 0f && f.Y > 20 && f.Y < s.GroundY - 30)
            {
                f.FireCd = MathH.Rand(1.8f, 3.4f);
                var pick = PickAliveCity(s); // alloc-free (§2.6)
                if (pick != null)
                {
                    var tinfo = new TargetInfo { Type = "city", X = pick.X, Y = pick.Y, Id = pick.Id };
                    Combat.CreateEnemyProjectile(s, "fast", f.X, f.Y + 3, tinfo);
                }
            }

            // Out-of-bounds despawn
            if (f.X < -120 || f.X > s.W + 120 || f.Y > s.GroundY + 10 || f.Y < -160)
            {
                s.Fighters.RemoveAt(i);
                continue;
            }
        }
    }
}
