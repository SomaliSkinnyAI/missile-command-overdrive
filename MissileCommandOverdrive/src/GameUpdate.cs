using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>All per-frame update logic.</summary>
public static class GameUpdate
{
    public static void UpdateAll(GameState s, float dt)
    {
        s.Time += dt;

        // Timers
        if (s.MsgT > 0) s.MsgT = MathF.Max(0, s.MsgT - dt);
        if (s.NoteT > 0) s.NoteT = MathF.Max(0, s.NoteT - dt);
        if (s.ComboTimer > 0)
        {
            s.ComboTimer = MathF.Max(0, s.ComboTimer - dt);
            if (s.ComboTimer == 0) s.Combo = 0;
        }
        if (s.Shake > 0) s.Shake = MathF.Max(0, s.Shake - dt * 20);
        if (s.Flash > 0) s.Flash = MathF.Max(0, s.Flash - dt * 1.7f);
        if (s.EmpCd > 0) s.EmpCd = MathF.Max(0, s.EmpCd - dt);
        if (s.Chromatic > 0) s.Chromatic = MathF.Max(0, s.Chromatic - dt * 1.8f);

        // Floating texts
        for (int i = s.FloatingTexts.Count - 1; i >= 0; i--)
        {
            s.FloatingTexts[i].Life -= dt;
            s.FloatingTexts[i].Y -= dt * 38;
            if (s.FloatingTexts[i].Life <= 0) s.FloatingTexts.RemoveAt(i);
        }

        if (s.GameOver)
        {
            s.GameOverTime += dt;
            UpdEnemies(s, dt);
            UpdUfo(s, dt);
            UpdRaiders(s, dt);
            UpdPlayer(s, dt);
            UpdExplosions(s, dt);
            UpdParticles(s, dt);
            Combat.RunCollisions(s);
            return;
        }

        // Ammo resupply for alive bases
        int aliveCities = s.Cities.Count(c => !c.Destroyed);
        if (aliveCities > 0)
        {
            var liveBases = s.Bases.Where(b => !b.Destroyed).ToList();
            if (liveBases.Count > 0)
            {
                int totalAmmo = liveBases.Sum(b => b.Ammo);
                bool emergency = totalAmmo <= Math.Max(24, 8 + s.Level * 0.9f);
                float supportRate = 0.18f + MathH.Clamp((s.Level - 12) * 0.012f, 0, 0.28f)
                    + (s.Auto ? 0.09f : 0) + s.Danger * 0.16f + (emergency ? 0.32f : 0);
                int targetLow = s.Auto ? 44 : 36;
                var lowBases = liveBases.Where(b => b.Ammo < targetLow).ToList();
                if (lowBases.Count > 0 && RandHelper.Next01() < dt * supportRate)
                {
                    var target = lowBases.OrderBy(b => b.Ammo).First();
                    int grant = emergency && RandHelper.Next01() < 0.4f ? 2 : 1;
                    target.Ammo = Math.Min(180, target.Ammo + grant);
                }
            }
        }

        // Base cooldowns
        foreach (var b in s.Bases)
            if (b.Cooldown > 0) b.Cooldown = MathF.Max(0, b.Cooldown - dt);

        // Wave spawning
        if (!s.Intro && !s.Shop)
        {
            if (s.WavePause > 0)
            {
                s.WavePause -= dt;
            }
            else
            {
                s.WaveTime += dt;
                while (s.SpawnI < s.WavePlan.Count && s.WavePlan[s.SpawnI].Time <= s.WaveTime)
                {
                    WaveSystem.SpawnEnemy(s, s.WavePlan[s.SpawnI]);
                    s.SpawnI++;
                }
                if (s.UfoQuota > 0 && s.WaveTime >= s.NextUfo)
                {
                    WaveSystem.SpawnUfo(s);
                    s.UfoQuota--;
                    s.NextUfo = s.WaveTime + MathH.Rand(8.1f, 13.8f) - MathF.Min(2.4f, s.Level * 0.14f);
                }
                if (s.RaiderQuota > 0 && s.WaveTime >= s.NextRaider)
                {
                    WaveSystem.SpawnRaider(s);
                    s.RaiderQuota--;
                    s.NextRaider = s.WaveTime + MathH.Rand(9.5f, 15.5f) - MathF.Min(2.1f, s.Level * 0.1f);
                }
            }
        }

        UpdEnemies(s, dt);
        UpdUfo(s, dt);
        UpdRaiders(s, dt);
        UpdPlayer(s, dt);
        UpdExplosions(s, dt);
        UpdParticles(s, dt);
        Combat.RunCollisions(s);

        // Auto-defense AI
        if (s.Auto) AutoDefense.RunAuto(s);

        // Phalanx CIWS turrets
        PhalanxSystem.UpdateAll(s, dt);

        // HellRaiser underground launcher
        HellRaiserSystem.Update(s, dt);

        // Weather
        WeatherSystem.Update(s, dt);

        // Wave cleared → shop
        if (!s.Intro && !s.Shop
            && s.SpawnI >= s.WavePlan.Count
            && s.Enemies.Count == 0 && s.UFOs.Count == 0 && s.Raiders.Count == 0
            && s.Explosions.Count == 0 && s.DebrisParts.Count == 0 && s.Shockwaves.Count == 0)
        {
            s.Shop = true;
            s.ShopTimer = 5.0f;
            Audio.SynthAudio.WaveCleared();
        }

        // Shop timer
        if (s.Shop)
        {
            s.ShopTimer -= dt;
            if (s.ShopTimer <= 0)
            {
                s.Shop = false;
                s.Level++;
                WaveSystem.StartWave(s, 2.9f);
            }
        }

        // Game over check
        if (aliveCities <= 0)
        {
            if (!s.GameOver)
            {
                s.GameOverTime = 0;
                Audio.SynthAudio.GameOver();
            }
            s.GameOver = true;
            s.Note = "Defense grid collapsed";
            s.NoteT = 2.2f;
        }
        if (s.GameOver) s.GameOverTime += dt;

        UpdateDanger(s);
    }

    // --- Enemy Update ---
    static void UpdEnemies(GameState s, float dt)
    {
        for (int i = s.Enemies.Count - 1; i >= 0; i--)
        {
            var m = s.Enemies[i];
            m._Elapsed += dt;
            float p = m._Dur > 0 ? m._Elapsed / m._Dur : 1;

            // Split check
            if (m.Variant == "split" && !m.HasSplit && p >= m.SplitAt)
            {
                Combat.SplitMissile(s, m);
                s.Enemies.RemoveAt(i);
                continue;
            }

            // Reached target
            if (p >= 1)
            {
                s.Enemies.RemoveAt(i);
                Combat.ImpactEnemy(s, m, m.Tx, m.Ty);
                continue;
            }

            // Homing
            if (m.HomingFactor > 0 && m.Target != null)
            {
                float tx = m.Target.Value.X, ty = m.Target.Value.Y;
                float desired = MathF.Atan2(ty - m.Y, tx - m.X);
                float cur = MathF.Atan2(m._Vy, m._Vx);
                float diff = MathF.Atan2(MathF.Sin(desired - cur), MathF.Cos(desired - cur));
                float turn = MathH.Clamp(diff, -1, 1) * m.HomingFactor * dt * 2.2f;
                float sp = MathF.Sqrt(m._Vx * m._Vx + m._Vy * m._Vy);
                float na = cur + turn;
                m._Vx = MathF.Cos(na) * sp;
                m._Vy = MathF.Sin(na) * sp;
            }

            // Position from mPos logic
            float local = MathH.Clamp(m._Elapsed, 0, m._Dur);
            float pp = m._Dur > 0 ? local / m._Dur : 1;
            float x = m.Sx + m._Vx * local;
            float y = m.Sy + m._Vy * local;

            if (m.ZigAmp > 0)
                x += MathF.Sin(pp * m._Fq * MathH.TAU + m.ZigPhase) * m.ZigAmp * (1 - pp * 0.5f);
            if (m.Variant == "heavy")
                x += MathF.Sin(pp * MathH.TAU * 0.6f + m.Id) * 7;
            if (m.Variant == "cruise")
                y += MathF.Sin(pp * MathH.TAU * 1.2f + m.ZigPhase) * 18 * (1 - pp * 0.32f);
            if (m.Variant == "drone")
            {
                x += MathF.Sin(pp * MathH.TAU * 3.4f + m.ZigPhase) * 12 * (1 - pp * 0.16f);
                y += MathF.Cos(pp * MathH.TAU * 2.7f + m.ZigPhase * 0.8f) * 9 * (1 - pp * 0.22f);
            }

            m.X = x;
            m.Y = y;

            // Record trail position for curved trail rendering
            m.Trail.Insert(0, (m.X, m.Y));
            if (m.Trail.Count > Enemy.MaxTrail) m.Trail.RemoveAt(m.Trail.Count - 1);

            // Hit ground
            if (m.Y >= s.GroundY - 4)
            {
                s.Enemies.RemoveAt(i);
                Combat.ImpactEnemy(s, m, m.X, s.GroundY - 2);
            }
        }
    }

    // --- UFO Update ---
    static void UpdUfo(GameState s, float dt)
    {
        for (int i = s.UFOs.Count - 1; i >= 0; i--)
        {
            var u = s.UFOs[i];
            u.X += u.Vx * dt;
            u.Y += MathF.Sin(s.Time * 2f + u.BobPhase) * dt * 12;
            u.FireCd -= dt;
            if (u.FireCd <= 0)
            {
                // Spawn UFO bomb
                var t = WaveSystem.ChooseTarget(s, "ufoBomb");
                if (t != null)
                    Combat.CreateEnemyProjectile(s, "ufoBomb", u.X + MathH.Rand(-20, 20), u.Y + 8, t.Value);
                u.FireCd = MathH.Rand(1.15f, 2.2f);
            }
            if ((u.Vx > 0 && u.X > s.W + 130) || (u.Vx < 0 && u.X < -130))
                s.UFOs.RemoveAt(i);
        }
    }

    // --- Raider Update ---
    static void UpdRaiders(GameState s, float dt)
    {
        for (int i = s.Raiders.Count - 1; i >= 0; i--)
        {
            var r = s.Raiders[i];
            r.FireCd -= dt;
            if (r.FireCd <= 0)
            {
                r.FireCd = MathH.Rand(0.55f, 1.25f);
                r.Vx = -r.Vx * MathH.Rand(0.9f, 1.22f);
                // Spit burst
                int burst = 3 + (RandHelper.Next01() < 0.45f ? 2 : 1);
                for (int j = 0; j < burst; j++)
                {
                    var t = WaveSystem.ChooseTarget(s, "spit");
                    if (t != null)
                        Combat.CreateEnemyProjectile(s, "spit", r.X + MathH.Rand(-20, 20), r.Y + 10, t.Value,
                            blastOverride: MathH.Rand(46, 78), ampOverride: MathH.Rand(10, 28), fqOverride: MathH.Rand(1.2f, 2.4f));
                }
            }
            r.X += r.Vx * dt;
            r.Y += MathF.Sin(s.Time * 2.7f + r.Angle) * dt * 24;
            r.Angle = MathF.Atan2(MathF.Cos(s.Time * 2.7f + r.Angle) * 24, r.Vx);

            if (r.X < -180 || r.X > s.W + 180)
                s.Raiders.RemoveAt(i);
        }
    }

    // --- Player Missiles Update ---
    static void UpdPlayer(GameState s, float dt)
    {
        for (int i = s.PlayerMissiles.Count - 1; i >= 0; i--)
        {
            var m = s.PlayerMissiles[i];
            m._Elapsed += dt;

            if (m.Hr)
            {
                // HellRaiser homing missile — velocity-based with turn rate
                m.HrRetarget -= dt;
                if (m.HrRetarget <= 0 || !HrTargetAlive(s, m))
                {
                    PickNewHrTarget(s, m);
                    m.HrRetarget = MathH.Rand(0.06f, 0.18f);
                }

                float ang = MathF.Atan2(m._Vy, m._Vx);

                // Steer toward target
                var aim = GetHrTargetPoint(s, m, MathH.Rand(0.03f, 0.12f));
                if (aim != null)
                {
                    float desired = MathF.Atan2(aim.Value.Y - m.Y, aim.Value.X - m.X);
                    float diff = MathF.Atan2(MathF.Sin(desired - ang), MathF.Cos(desired - ang));
                    ang += MathH.Clamp(diff, -m.HrTurn * dt, m.HrTurn * dt);
                }

                // Squiggle
                ang += MathF.Sin((s.Time + m.Id * 0.013f) * m.SquiggleFreq + m.SquigglePhase) * dt * 1.35f;

                float sp = m.HrSpeed;
                m._Vx = MathF.Cos(ang) * sp;
                m._Vy = MathF.Sin(ang) * sp;
                m.X += m._Vx * dt;
                m.Y += m._Vy * dt;

                // Out of bounds → expire
                if (m.X < -60 || m.X > s.W + 60 || m.Y < -40 || m.Y > s.GroundY + 28)
                    m._Elapsed = m._Dur;
            }
            else
            {
                // Normal player missile — parametric
                m.X = m.Sx + m._Vx * m._Elapsed;
                m.Y = m.Sy + m._Vy * m._Elapsed;
            }

            // Record trail position for curved trail rendering
            m.Trail.Insert(0, (m.X, m.Y));
            if (m.Trail.Count > PlayerMissile.MaxTrail) m.Trail.RemoveAt(m.Trail.Count - 1);

            float p = m._Dur > 0 ? m._Elapsed / m._Dur : 1;
            if (p >= 1)
            {
                s.PlayerMissiles.RemoveAt(i);
                if (m.Hr)
                {
                    float ex = MathH.Clamp(m.X, 0, s.W);
                    float ey = MathH.Clamp(m.Y, 18, s.GroundY - 4);
                    Combat.SpawnExpl(s, ex, ey, m._Blast, 0.8f, 0.36f, player: true, flash: 0.05f);
                }
                else
                {
                    Combat.SpawnExpl(s, m.Tx, m.Ty, m._Blast, 1.28f, 0.36f, player: true, flash: 0.08f);
                }
            }
        }
    }

    /// <summary>Check if a HellRaiser missile's target is still alive.</summary>
    static bool HrTargetAlive(GameState s, PlayerMissile m)
    {
        if (string.IsNullOrEmpty(m.HrTargetKind)) return false;
        return m.HrTargetKind switch
        {
            "enemy" => s.Enemies.Any(e => e.Id == m.HrTargetId),
            "ufo" => s.UFOs.Any(u => u.Id == m.HrTargetId),
            "raider" => s.Raiders.Any(r => r.Id == m.HrTargetId),
            _ => false
        };
    }

    /// <summary>Get the predicted position of a HellRaiser missile's target with lead.</summary>
    static (float X, float Y)? GetHrTargetPoint(GameState s, PlayerMissile m, float lead)
    {
        if (string.IsNullOrEmpty(m.HrTargetKind)) return null;
        if (m.HrTargetKind == "enemy")
        {
            var e = s.Enemies.FirstOrDefault(e => e.Id == m.HrTargetId);
            if (e == null) return null;
            return (e.X + e._Vx * lead, e.Y + e._Vy * lead);
        }
        if (m.HrTargetKind == "ufo")
        {
            var u = s.UFOs.FirstOrDefault(u => u.Id == m.HrTargetId);
            if (u == null) return null;
            return (u.X + u.Vx * lead, u.Y);
        }
        if (m.HrTargetKind == "raider")
        {
            var r = s.Raiders.FirstOrDefault(r => r.Id == m.HrTargetId);
            if (r == null) return null;
            return (r.X + r.Vx * lead, r.Y + MathF.Sin((s.Time + lead) * 2.7f + r.Angle) * 10);
        }
        return null;
    }

    /// <summary>Pick a new target for a HellRaiser homing missile using weighted selection.</summary>
    static void PickNewHrTarget(GameState s, PlayerMissile m)
    {
        var pool = new List<(string Kind, int Id, float Weight)>();
        foreach (var e in s.Enemies)
        {
            if (e.Y > s.GroundY + 18) continue;
            float dx = e.X - m.X, dy = e.Y - m.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > 900) continue;
            float distW = 1f / (0.38f + dist * 0.0034f);
            float baseW = 80 + (e.Target?.Type == "city" ? 46 : 0);
            pool.Add(("enemy", e.Id, MathF.Max(1, baseW * distW)));
        }
        foreach (var u in s.UFOs)
        {
            float dx = u.X - m.X, dy = u.Y - m.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > 900) continue;
            float distW = 1f / (0.38f + dist * 0.0034f);
            pool.Add(("ufo", u.Id, MathF.Max(1, (u.Boss ? 200 : 120) * distW)));
        }
        foreach (var r in s.Raiders)
        {
            float dx = r.X - m.X, dy = r.Y - m.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > 900) continue;
            float distW = 1f / (0.38f + dist * 0.0034f);
            pool.Add(("raider", r.Id, MathF.Max(1, 228 * distW)));
        }

        if (pool.Count == 0)
        {
            m.HrTargetKind = "";
            m.HrTargetId = -1;
            return;
        }

        // Weighted random pick
        float total = pool.Sum(p => p.Weight);
        float roll = RandHelper.Next01() * total;
        float acc = 0;
        foreach (var p in pool)
        {
            acc += p.Weight;
            if (roll <= acc)
            {
                m.HrTargetKind = p.Kind;
                m.HrTargetId = p.Id;
                return;
            }
        }
        var last = pool[^1];
        m.HrTargetKind = last.Kind;
        m.HrTargetId = last.Id;
    }

    // --- Explosions Update ---
    static void UpdExplosions(GameState s, float dt)
    {
        for (int i = s.Explosions.Count - 1; i >= 0; i--)
        {
            var e = s.Explosions[i];
            e.Life -= dt;
            if (e.Life <= 0)
            {
                s.Explosions.RemoveAt(i);
                continue;
            }
            float elapsed = e.MaxLife - e.Life;
            e.Radius = Combat.ExplRadius(elapsed, e.MaxRadius, e.Shake, e.MaxLife);
        }
    }

    // --- Particles Update ---
    static void UpdParticles(GameState s, float dt)
    {
        // Sparks
        for (int i = s.Sparks.Count - 1; i >= 0; i--)
        {
            var sp = s.Sparks[i];
            sp.Life -= dt;
            if (sp.Life <= 0) { s.Sparks.RemoveAt(i); continue; }
            sp.Vy += 140 * dt;
            sp.X += sp.Vx * dt;
            sp.Y += sp.Vy * dt;
            sp.Vx *= 0.994f;
            s.Sparks[i] = sp;
        }

        // Smoke
        for (int i = s.SmokeParts.Count - 1; i >= 0; i--)
        {
            var sm = s.SmokeParts[i];
            sm.Life -= dt;
            if (sm.Life <= 0) { s.SmokeParts.RemoveAt(i); continue; }
            float p = 1 - sm.Life / sm.MaxLife;
            sm.X += sm.Vx * dt * (0.5f + p * 0.7f);
            sm.Y += sm.Vy * dt * (0.7f + p * 0.45f);
            sm.Vx *= 0.99f;
            s.SmokeParts[i] = sm;
        }

        // Trails
        for (int i = s.Trails.Count - 1; i >= 0; i--)
        {
            var tr = s.Trails[i];
            tr.Life -= dt;
            if (tr.Life <= 0) { s.Trails.RemoveAt(i); continue; }
            tr.X += tr.Vx * dt;
            tr.Y += tr.Vy * dt;
            tr.Vy += 35 * dt;
            tr.Vx *= 0.99f;
            s.Trails[i] = tr;
        }

        // Debris
        for (int i = s.DebrisParts.Count - 1; i >= 0; i--)
        {
            var d = s.DebrisParts[i];
            d.Life -= dt;
            if (d.Life <= 0) { s.DebrisParts.RemoveAt(i); continue; }
            d.Vy += 360 * dt;
            d.X += d.Vx * dt;
            d.Y += d.Vy * dt;
            d.Rot += d.RotSpeed * dt;
            d.Vx *= 0.992f;
            if (d.Y > s.GroundY - 2)
            {
                d.Y = s.GroundY - 2;
                d.Vy *= -0.26f;
                d.Vx *= 0.84f;
                d.RotSpeed *= 0.7f;
            }
            s.DebrisParts[i] = d;
        }

        // Shockwaves
        for (int i = s.Shockwaves.Count - 1; i >= 0; i--)
        {
            var sw = s.Shockwaves[i];
            sw.Life -= dt;
            if (sw.Life <= 0) { s.Shockwaves.RemoveAt(i); continue; }
            float p = 1 - sw.Life / sw.MaxLife;
            sw.Radius = MathH.Lerp(8, sw.MaxRadius, MathH.EaseOut(p));
            s.Shockwaves[i] = sw;
        }

        // Light bursts
        for (int i = s.LightBursts.Count - 1; i >= 0; i--)
        {
            var lb = s.LightBursts[i];
            lb.Life -= dt;
            if (lb.Life <= 0) { s.LightBursts.RemoveAt(i); continue; }
            s.LightBursts[i] = lb;
        }

        // Muzzle flashes
        for (int i = s.MuzzleFlashes.Count - 1; i >= 0; i--)
        {
            var mf = s.MuzzleFlashes[i];
            mf.Life -= dt;
            if (mf.Life <= 0) { s.MuzzleFlashes.RemoveAt(i); continue; }
            s.MuzzleFlashes[i] = mf;
        }

        // Scorches
        for (int i = s.Scorches.Count - 1; i >= 0; i--)
        {
            var sc = s.Scorches[i];
            sc.Life -= dt;
            if (sc.Life <= 0) { s.Scorches.RemoveAt(i); continue; }
            s.Scorches[i] = sc;
        }

        // Shooting stars
        for (int i = s.ShootingStars.Count - 1; i >= 0; i--)
        {
            var ss = s.ShootingStars[i];
            ss.Life -= dt;
            ss.X += ss.Vx * dt;
            ss.Y += ss.Vy * dt;
            if (ss.Life <= 0 || ss.Y > s.HorizonY) { s.ShootingStars.RemoveAt(i); continue; }
            s.ShootingStars[i] = ss;
        }
        // Spawn new shooting stars occasionally
        if (s.ShootingStars.Count < 2 && RandHelper.Next01() < dt * 0.04f)
        {
            float sx = MathH.Rand(s.W * 0.1f, s.W * 0.9f);
            float sy = MathH.Rand(20, s.HorizonY * 0.4f);
            float ang = MathH.Rand(0.2f, 0.8f);
            float speed = MathH.Rand(400, 800);
            s.ShootingStars.Add(new ShootingStar
            {
                X = sx, Y = sy,
                Vx = MathF.Cos(ang) * speed,
                Vy = MathF.Sin(ang) * speed,
                Life = MathH.Rand(0.4f, 0.9f),
                MaxLife = MathH.Rand(0.4f, 0.9f),
                Length = speed * 0.02f
            });
        }
    }

    static void UpdateDanger(GameState s)
    {
        if (s.Intro || s.GameOver) { s.Danger = 0; return; }
        int alive = Math.Max(1, s.Cities.Count(c => !c.Destroyed));
        float d = 0;
        d += s.Enemies.Count * 12;
        d += (s.WavePlan.Count - s.SpawnI) * 1.8f;
        d += s.UFOs.Count * 52;
        d += s.Raiders.Count * 66;
        if (s.Demon != null) d += 82;
        s.Danger = MathH.Clamp(d / (alive * 170), 0, 1);
    }
}
