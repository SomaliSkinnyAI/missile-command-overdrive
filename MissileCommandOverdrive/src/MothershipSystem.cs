using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Star Destroyer mothership easter egg — summoned by typing "777". Slowly crosses the sky, tanks damage, deploys fighter waves at mid-screen.</summary>
public static class MothershipSystem
{
    public const int BaseHp = 22;

    public static void Summon(GameState s)
    {
        if (s.Mothership != null) return;
        if (s.Intro || s.GameOver) return;

        bool fromLeft = RandHelper.Chance(0.5f); // §4.3: cosmetic stream
        float hullW = MathF.Max(340, s.W * 0.32f);
        float startX = fromLeft ? -hullW * 0.6f : s.W + hullW * 0.6f;
        float vx = fromLeft ? 33f : -33f; // slow cinematic traverse (+50% from original)
        float y = s.HorizonY * 0.38f;

        s.Mothership = new Mothership
        {
            X = startX,
            Y = y,
            Vx = vx,
            W = hullW,
            Hp = BaseHp,
            MaxHp = BaseHp,
            Phase = MathH.Rand(0, MathH.TAU),
            SpawnCd = 4.5f,
            AppearTime = 0,
            Active = true,
            ReachedMid = false,
            ShieldActive = false,
            ShieldStateTimer = MathH.Rand(3.0f, 5.5f), // first shield raise delay
            ShieldRippleT = 0f
        };

        s.Note = "EASTER EGG: IMPERIAL MOTHERSHIP INBOUND";
        s.NoteT = 3.0f;
        s.Flash = MathF.Max(s.Flash, 0.22f);
        s.AddTrauma(0.3f);
        SynthAudio.Thunder(0.5f, 0.9f);
    }

    public static void Update(GameState s, float dt)
    {
        var m = s.Mothership;
        if (m == null || !m.Active) return;

        m.AppearTime += dt;
        m.X += m.Vx * dt;
        m.Phase += dt * 0.6f;
        m.ShieldFlash = MathF.Max(0, m.ShieldFlash - dt * 1.8f);
        m.ShieldRippleT = MathF.Max(0, m.ShieldRippleT - dt * 2.2f);

        // Deflector-shield toggle: ~4s off, ~2.5s on, randomized
        m.ShieldStateTimer -= dt;
        if (m.ShieldStateTimer <= 0f)
        {
            m.ShieldActive = !m.ShieldActive;
            m.ShieldStateTimer = m.ShieldActive ? MathH.Rand(2.0f, 3.4f) : MathH.Rand(3.2f, 5.6f);
            if (m.ShieldActive)
            {
                SynthAudio.Thunder(MathH.Clamp(m.X / s.W, 0, 1), 0.35f);
            }
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

        // Off-screen exit (after traversal complete)
        if ((m.Vx > 0 && m.X > s.W + m.W) || (m.Vx < 0 && m.X < -m.W))
        {
            s.Mothership = null;
            s.Note = "Mothership retreated";
            s.NoteT = 1.4f;
            return;
        }

        // Dead
        if (m.Hp <= 0)
        {
            // Handled in Combat.RunCollisions for score + explosion
            s.Mothership = null;
        }
    }

    /// <summary>True while a live mothership is on-screen — other enemy spawning is paused.</summary>
    public static bool HoldSpawning(GameState s) => s.Mothership != null && s.Mothership.Active;

    static void DeployFighter(GameState s, Mothership m)
    {
        // Launch a unique small TIE-style fighter from the hangar bay underside.
        // Fighters are faster, smaller, and have a dogfight-style diving trajectory (not just cruising).
        float bayX = m.X + MathH.Rand(-m.W * 0.28f, m.W * 0.28f);
        float bayY = m.Y + 24;

        // Pick a random city (or ground spot) as strafe target
        var aliveCities = s.Cities.Where(c => !c.Destroyed).ToList();
        float tx, ty;
        if (aliveCities.Count > 0)
        {
            var pick = RandHelper.Pick(aliveCities);
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
                var aliveCities = s.Cities.Where(c => !c.Destroyed).ToList();
                if (aliveCities.Count > 0)
                {
                    var pick = RandHelper.Pick(aliveCities);
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
                var aliveCities = s.Cities.Where(c => !c.Destroyed).ToList();
                if (aliveCities.Count > 0)
                {
                    var pick = RandHelper.Pick(aliveCities);
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
