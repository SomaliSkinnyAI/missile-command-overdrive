using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Combat helpers: creating projectiles, explosions, particles, damage, kills.</summary>
public static class Combat
{
    // --- Enemy Projectile Factory ---
    public static Enemy CreateEnemyProjectile(GameState s, string v, float sx, float sy, TargetInfo t,
        float? blastOverride = null, float? homingOverride = null, float? ampOverride = null, float? fqOverride = null)
    {
        float dx = t.X - sx, dy = t.Y - sy;
        float dist = MathF.Max(100, MathF.Sqrt(dx * dx + dy * dy));
        float sp = VariantStats.Speed(v, s.Level);
        float dur = dist / sp;

        float amp = ampOverride ?? v switch
        {
            "zig" => MathH.Rand(40, 95),
            "drone" => MathH.Rand(16, 36),
            "cruise" => MathH.Rand(6, 18),
            "spit" => MathH.Rand(8, 20),
            "hell" => MathH.Rand(18, 34),
            _ => 0
        };
        float fq = fqOverride ?? v switch
        {
            "zig" => MathH.Rand(1.1f, 2f),
            "drone" => MathH.Rand(2.1f, 3.8f),
            "cruise" => MathH.Rand(0.7f, 1.3f),
            "spit" => MathH.Rand(1.1f, 2.1f),
            "hell" => MathH.Rand(1.2f, 2.4f),
            _ => 0
        };
        float blast = blastOverride ?? v switch
        {
            "heavy" => MathH.Rand(120, 170),
            "carrier" => MathH.Rand(118, 150),
            "drone" => MathH.Rand(34, 52),
            "cruise" => MathH.Rand(70, 102),
            "spit" => MathH.Rand(44, 72),
            "hell" => MathH.Rand(108, 152),
            _ => MathH.Rand(56, 90)
        };
        float homing = homingOverride ?? v switch
        {
            "cruise" => MathH.Rand(0.62f, 0.95f),
            "drone" => MathH.Rand(0.25f, 0.5f),
            "hell" => MathH.Rand(0.35f, 0.6f),
            _ => 0
        };

        var m = new Enemy
        {
            Id = s.NewId(),
            Variant = v,
            X = sx, Y = sy,
            Sx = sx, Sy = sy,
            Tx = t.X, Ty = t.Y,
            Speed = sp,
            Progress = 0,
            Life = dur,
            Resistance = VariantStats.Resistance(v),
            ZigPhase = (v is "zig" or "drone" or "cruise" or "spit" or "hell") ? RandHelper.Next01() * MathH.TAU : 0,
            ZigAmp = amp,
            HomingFactor = homing,
            Split = v == "split",
            SplitAt = v == "split" ? MathH.Rand(0.4f, 0.63f) : 0,
            HasSplit = false,
            Hp = v == "carrier" ? 3 : 1,
            Target = t,
            Dead = false
        };
        // Store extra fields in the Enemy for update logic
        m._Vx = dx / dur;
        m._Vy = dy / dur;
        m._Dur = dur;
        m._Elapsed = 0;
        m._Fq = fq;
        m._Blast = blast;
        m._DeployAt = v == "carrier" ? MathH.Rand(0.35f, 0.62f) : 0;
        m._Deployed = false;
        m._Val = VariantStats.Value(v);

        s.Enemies.Add(m);
        return m;
    }

    // --- Explosion Factory ---
    public static void SpawnExpl(GameState s, float x, float y, float maxRadius = 92f,
        float life = 1.3f, float shakeTime = 0.36f, bool player = false, bool emp = false,
        bool noShake = false, float flash = 0f, bool heavy = false)
    {
        s.Explosions.Add(new Explosion
        {
            X = x, Y = y,
            Radius = 0,
            MaxRadius = maxRadius,
            Life = life,
            MaxLife = life,
            Player = player,
            Emp = emp,
            Shake = shakeTime,
            Flash = flash,
            NoShake = noShake
        });

        if (!noShake) s.Shake = MathF.Max(s.Shake, emp ? 19 : player ? 7 : heavy ? 14 : 11);
        if (flash > 0) s.Flash = MathF.Max(s.Flash, flash);
        if (emp) s.Chromatic = MathF.Max(s.Chromatic, 1f);
        else if (heavy) s.Chromatic = MathF.Max(s.Chromatic, 0.45f);

        // Light burst
        s.LightBursts.Add(new LightBurst
        {
            X = x, Y = y,
            Radius = maxRadius * (emp ? 0.9f : 0.7f),
            Life = emp ? 0.72f : 0.5f,
            MaxLife = emp ? 0.72f : 0.5f
        });

        // Shockwave for big explosions
        if (emp || heavy || maxRadius > 92)
        {
            s.Shockwaves.Add(new Shockwave
            {
                X = x, Y = y,
                Radius = 8,
                MaxRadius = maxRadius * (emp ? 1.28f : 0.98f),
                Life = emp ? 0.86f : 0.6f,
                MaxLife = emp ? 0.86f : 0.6f
            });
        }

        SpawnSparks(s, x, y, player, emp, MathH.Clamp(maxRadius / 100f, 0.9f, 1.9f));
    }

    public static void SpawnSparks(GameState s, float x, float y, bool player, bool emp, float m = 1f)
    {
        int n = (int)((emp ? 46 : player ? 18 : 24) * m);
        for (int i = 0; i < n; i++)
        {
            float a = RandHelper.Next01() * MathH.TAU;
            float sp = (emp ? MathH.Rand(120, 440) : MathH.Rand(100, 280)) * MathF.Max(0.8f, m * 0.9f);
            byte r, g, b;
            if (emp) { r = 149; g = 236; b = 255; }
            else if (player) { r = 184; g = 255; b = 255; }
            else { r = 255; g = 200; b = 100; }
            s.Sparks.Add(new Spark
            {
                X = x, Y = y,
                Vx = MathF.Cos(a) * sp,
                Vy = MathF.Sin(a) * sp - MathH.Rand(0, 70),
                Life = emp ? MathH.Rand(0.65f, 1.2f) : MathH.Rand(0.45f, 0.95f),
                MaxLife = emp ? MathH.Rand(0.65f, 1.2f) : MathH.Rand(0.45f, 0.95f),
                Size = emp ? MathH.Rand(1.6f, 3.2f) : MathH.Rand(1.3f, 2.6f),
                R = r, G = g, B = b
            });
        }
    }

    public static void SpawnSmoke(GameState s, float x, float y, int n, float k = 1f)
    {
        for (int i = 0; i < n; i++)
        {
            s.SmokeParts.Add(new Smoke
            {
                X = x + MathH.Rand(-34, 34),
                Y = y + MathH.Rand(-18, 6),
                Vx = MathH.Rand(-26, 26),
                Vy = -MathH.Rand(18, 42),
                Life = MathH.Rand(2.2f, 4.6f),
                MaxLife = MathH.Rand(2.2f, 4.6f),
                Size = MathH.Rand(11, 26) * k,
                Alpha = MathH.Rand(0.16f, 0.35f)
            });
        }
    }

    public static void SpawnScorch(GameState s, float x, float y)
    {
        s.Scorches.Add(new Scorch
        {
            X = x, Y = y,
            Radius = MathH.Rand(12, 28),
            Life = MathH.Rand(6, 12)
        });
        if (s.Scorches.Count > 40) s.Scorches.RemoveAt(0);
    }

    // --- Damage / Kill ---
    public static void RegKill(GameState s, Enemy m, float x, float y)
    {
        float bonus = 1 + MathF.Min(2.2f, s.Combo * 0.09f);
        int gain = (int)MathF.Round(m._Val * bonus);
        s.Score += gain;
        s.Combo++;
        s.ComboTimer = 4;
        s.MaxCombo = Math.Max(s.MaxCombo, s.Combo);

        if (s.Combo > 1 && s.Combo % 5 == 0)
        {
            s.FloatingTexts.Add(new FloatingText
            {
                Text = $"{s.Combo}x COMBO!",
                X = x, Y = y - 20,
                Life = 1.2f, MaxLife = 1.2f
            });
        }
        if (s.Combo > 0 && s.Combo % 12 == 0 && s.Emp < s.EmpMax)
        {
            s.Emp++;
            s.Note = "EMP charge granted";
            s.NoteT = 1.25f;
        }

        float r = m.Variant is "heavy" or "carrier" ? 94 : m.Variant == "cruise" ? 78 : 70;
        SpawnExpl(s, x, y, r, 0.9f, 0.41f, player: true,
            flash: m.Variant is "heavy" or "carrier" ? 0.16f : 0.08f,
            noShake: m.Variant is not "heavy" and not "carrier");
        SynthAudio.Hit(MathH.Clamp(x / s.W, 0, 1), m.Variant is "heavy" or "carrier" ? 1f : 0.6f);
    }

    public static bool DamageEnemyUnit(GameState s, Enemy m, float x, float y, float dmg = 1)
    {
        if (m.Hp > dmg)
        {
            m.Hp -= (int)dmg;
            SpawnExpl(s, x, y, m.Variant == "carrier" ? 56 : 34, 0.46f, 0.34f, player: true, flash: 0.04f, noShake: true);
            return false;
        }
        RegKill(s, m, x, y);
        return true;
    }

    public static void ImpactEnemy(GameState s, Enemy m, float x, float y)
    {
        if (m.Variant == "decoy")
        {
            SpawnExpl(s, x, y, 30, 0.8f, 0.3f, flash: 0.05f, noShake: true);
            return;
        }

        bool inferHeavy = m.Variant is "heavy" or "carrier" or "hell";
        SpawnExpl(s, x, y, m._Blast,
            inferHeavy ? 1.22f : m.Variant == "drone" ? 0.82f : 1f,
            inferHeavy ? 0.24f : 0.3f,
            flash: inferHeavy ? 0.28f : m.Variant == "drone" ? 0.1f : 0.16f,
            heavy: inferHeavy);
        SpawnSmoke(s, x, y - 6, inferHeavy ? 18 : m.Variant == "drone" ? 7 : 10,
            inferHeavy ? 1.35f : m.Variant == "drone" ? 0.75f : 1f);
        if (y >= s.GroundY - 20) SpawnScorch(s, x, s.GroundY);
        DestroyTarget(s, m.Target, x, y, m._Blast);
        SynthAudio.Impact(MathH.Clamp(x / s.W, 0, 1), inferHeavy);
    }

    public static void DestroyTarget(GameState s, TargetInfo? target, float x, float y, float blast)
    {
        if (target == null) return;
        var t = target.Value;

        if (t.Type == "city")
        {
            var city = s.Cities.FirstOrDefault(c => c.Id == t.Id);
            if (city != null && !city.Destroyed) KillCity(s, city, x, y);
        }
        else if (t.Type == "base")
        {
            var b = s.Bases.FirstOrDefault(b2 => b2.Id == t.Id);
            if (b != null && !b.Destroyed)
            {
                b.Destroyed = true;
                b.Ammo = 0;
                SpawnExpl(s, b.X, b.Y - 4, 84, 1.05f, 0.3f, flash: 0.18f);
            }
        }
        else if (t.Type == "phalanx")
        {
            var p = s.Phalanxes.FirstOrDefault(p2 => p2.Id == t.Id);
            if (p != null && !p.Destroyed)
            {
                p.Destroyed = true;
                p.Ammo = 0;
                SpawnExpl(s, p.X, p.Y - 4, 72, 0.9f, 0.3f, flash: 0.14f);
            }
        }

        // Splash damage for big blasts
        if (blast > 100)
        {
            foreach (var c in s.Cities)
                if (!c.Destroyed && MathF.Abs(c.X - x) <= blast * 0.75f)
                    KillCity(s, c, c.X, s.GroundY - 20);
            foreach (var b in s.Bases)
                if (!b.Destroyed && MathF.Abs(b.X - x) <= blast * 0.68f)
                {
                    b.Destroyed = true;
                    b.Ammo = 0;
                    SpawnExpl(s, b.X, b.Y - 8, 78, 0.95f, 0.3f, flash: 0.12f, noShake: true);
                }
            foreach (var p in s.Phalanxes)
                if (!p.Destroyed && MathF.Abs(p.X - x) <= blast * 0.64f)
                {
                    p.Destroyed = true;
                    p.Ammo = 0;
                }
        }
    }

    public static void KillCity(GameState s, City city, float x, float y)
    {
        city.Destroyed = true;
        SpawnExpl(s, x, y, MathH.Rand(74, 120), 1.08f, 0.3f, flash: 0.22f, heavy: true);
        SpawnSmoke(s, x, y - 6, 16, 1.2f);
        SpawnScorch(s, x, s.GroundY);
        SynthAudio.CityDestroyed(MathH.Clamp(x / s.W, 0, 1));
    }

    // --- Player Firing ---
    public static bool LaunchPlayer(GameState s, float tx, float ty, int? baseIndex = null)
    {
        if (s.GameOver || s.Intro || s.Shop) return false;

        Base? c = null;
        if (baseIndex != null && baseIndex >= 0 && baseIndex < s.Bases.Count)
        {
            var b = s.Bases[baseIndex.Value];
            if (!b.Destroyed && b.Ammo > 0 && b.Cooldown <= 0)
                c = b;
        }

        if (c == null)
        {
            if (s.SelectedBase != null && s.SelectedBase >= 0 && s.SelectedBase < s.Bases.Count)
            {
                var sb = s.Bases[s.SelectedBase.Value];
                if (!sb.Destroyed && sb.Ammo > 0 && sb.Cooldown <= 0) c = sb;
            }
            if (c == null)
            {
                var live = s.Bases.Where(b => !b.Destroyed && b.Ammo > 0).ToList();
                if (live.Count == 0) return false;
                c = live.OrderBy(b => MathF.Abs(b.X - tx)).First();
            }
        }

        if (c.Destroyed || c.Ammo <= 0 || c.Cooldown > 0)
        {
            s.Shake = MathF.Max(s.Shake, 2);
            return false;
        }

        float y2 = MathF.Min(ty, s.GroundY - 56);
        float dx = tx - c.X, dy = y2 - c.Y;
        float dist = MathF.Max(90, MathF.Sqrt(dx * dx + dy * dy));
        float speed = VariantStats.InterceptorSpeed(s);
        float dur = dist / speed;

        c.Ammo--;
        float reloadMult = s.Upgrades.ReloadMult;
        float lvlReload = 1 + MathF.Min(1.9f, s.Level * 0.03f);
        c.Cooldown = 0.24f / (MathF.Max(0.4f, reloadMult) * lvlReload);

        float blastRadius = 102f * s.Upgrades.BlastScale;

        s.PlayerMissiles.Add(new PlayerMissile
        {
            Id = s.NewId(),
            X = c.X, Y = c.Y,
            Sx = c.X, Sy = c.Y,
            Tx = tx, Ty = y2,
            Speed = speed,
            Progress = 0,
            Detonated = false,
            BaseIndex = s.Bases.IndexOf(c),
            _Vx = dx / dur,
            _Vy = dy / dur,
            _Dur = dur,
            _Elapsed = 0,
            _Blast = blastRadius
        });

        // Muzzle flash
        float launchAngle = MathF.Atan2(y2 - c.Y, tx - c.X);
        s.MuzzleFlashes.Add(new MuzzleFlash
        {
            X = c.X, Y = c.Y,
            Angle = launchAngle,
            Life = 0.18f, MaxLife = 0.18f
        });

        SynthAudio.Launch(MathH.Clamp(c.X / s.W, 0, 1));
        return true;
    }

    public static bool UseEMP(GameState s)
    {
        if (s.Intro || s.GameOver || s.Emp <= 0 || s.EmpCd > 0) return false;
        s.Emp--;
        s.EmpCd = 13;
        SpawnExpl(s, s.MouseX, s.MouseY,
            228 * s.Upgrades.EmpScale, 1.45f, 0.42f,
            player: true, emp: true, flash: 0.32f);
        s.Shake = MathF.Max(s.Shake, 20);
        s.Flash = MathF.Max(s.Flash, 0.32f);
        s.Chromatic = MathF.Max(s.Chromatic, 0.8f);
        s.Note = "EMP pulse deployed";
        s.NoteT = 1.1f;
        SynthAudio.EMP();
        return true;
    }

    // --- Explosion Radius ---
    public static float ExplRadius(float elapsed, float maxRadius, float shakeTime, float life)
    {
        if (!float.IsFinite(elapsed) || elapsed < 0 || elapsed > life) return 0;
        float p = life > 0 ? elapsed / life : 1;
        if (p < shakeTime)
            return maxRadius * MathH.EaseOut(p / MathF.Max(0.0001f, shakeTime));
        float q = (p - shakeTime) / MathF.Max(0.0001f, 1 - shakeTime);
        return maxRadius * MathF.Max(0, 1 - MathH.EaseIn(q));
    }

    // --- Split Missile ---
    public static void SplitMissile(GameState s, Enemy m)
    {
        m.HasSplit = true;
        SpawnExpl(s, m.X, m.Y, 42, 0.62f, 0.28f, flash: 0.08f, noShake: true);

        int count = 2 + (RandHelper.Next01() < 0.35f ? 1 : 0);
        for (int i = 0; i < count; i++)
        {
            var t = ChooseTargetForShard(s);
            if (t == null) continue;
            float tx = MathH.Clamp(MathH.Lerp(t.Value.X, m.Tx + MathH.Rand(-150, 150), 0.42f), 18, s.W - 18);
            CreateEnemyProjectile(s, "shard", m.X, m.Y, new TargetInfo
            {
                Type = t.Value.Type,
                X = tx,
                Y = t.Value.Y,
                Id = t.Value.Id
            }, ampOverride: MathH.Rand(14, 34), fqOverride: MathH.Rand(1.2f, 2.4f),
                blastOverride: MathH.Rand(40, 64));
        }
    }

    static TargetInfo? ChooseTargetForShard(GameState s) => WaveSystem.ChooseTarget(s, "shard");

    // --- Collisions ---
    public static void RunCollisions(GameState s)
    {
        // Enemies vs player explosions
        for (int i = s.Enemies.Count - 1; i >= 0; i--)
        {
            var m = s.Enemies[i];
            bool removed = false;
            foreach (var e in s.Explosions)
            {
                if (!e.Player) continue;
                float dx = m.X - e.X, dy = m.Y - e.Y;
                float rf = 1 - m.Resistance * 0.45f;
                float r = MathF.Max(18, e.Radius * rf);
                if (dx * dx + dy * dy <= r * r)
                {
                    removed = DamageEnemyUnit(s, m, m.X, m.Y, 1);
                    break;
                }
            }
            if (removed) s.Enemies.RemoveAt(i);
        }

        // UFOs vs player explosions
        for (int i = s.UFOs.Count - 1; i >= 0; i--)
        {
            var u = s.UFOs[i];
            bool hit = false;
            foreach (var e in s.Explosions)
            {
                if (!e.Player) continue;
                float dx = u.X - e.X, dy = u.Y - e.Y;
                float r = MathF.Max(22, e.Radius * 0.6f);
                if (dx * dx + dy * dy <= r * r)
                {
                    u.Hp -= 1;
                    hit = true;
                    SpawnExpl(s, u.X, u.Y, u.Boss ? 64 : 44, 0.58f, 0.34f, player: true, flash: 0.06f, noShake: true);
                    break;
                }
            }
            if (!hit) continue;
            if (u.Hp <= 0)
            {
                float bonus = 1 + MathF.Min(2.2f, s.Combo * 0.09f);
                int gain = (int)MathF.Round((u.Boss ? 1500 : 260) * bonus);
                s.Score += gain;
                s.Combo++;
                s.ComboTimer = 4;
                s.MaxCombo = Math.Max(s.MaxCombo, s.Combo);
                SpawnExpl(s, u.X, u.Y, u.Boss ? 140 : 96, u.Boss ? 1.4f : 1.02f, 0.34f, player: true, flash: u.Boss ? 0.32f : 0.18f);
                SpawnSmoke(s, u.X, u.Y, u.Boss ? 20 : 10, u.Boss ? 1.3f : 1.05f);
                s.Note = u.Boss ? "Boss UFO destroyed" : "UFO destroyed";
                s.NoteT = 0.9f;
                s.UFOs.RemoveAt(i);
            }
        }

        // Raiders vs player explosions
        for (int i = s.Raiders.Count - 1; i >= 0; i--)
        {
            var r = s.Raiders[i];
            bool hit = false;
            foreach (var e in s.Explosions)
            {
                if (!e.Player) continue;
                float dx = r.X - e.X, dy = r.Y - e.Y;
                float rr = MathF.Max(26, e.Radius * 0.55f);
                if (dx * dx + dy * dy <= rr * rr)
                {
                    r.Hp -= 1;
                    hit = true;
                    SpawnExpl(s, r.X + MathH.Rand(-8, 8), r.Y + MathH.Rand(-5, 5), 42, 0.52f, 0.35f,
                        player: true, flash: 0.05f, noShake: true);
                    break;
                }
            }
            if (!hit) continue;
            if (r.Hp <= 0)
            {
                float bonus = 1 + MathF.Min(2.2f, s.Combo * 0.09f);
                int gain = (int)MathF.Round(460 * bonus);
                s.Score += gain;
                s.Combo++;
                s.ComboTimer = 4;
                s.MaxCombo = Math.Max(s.MaxCombo, s.Combo);
                SpawnExpl(s, r.X, r.Y, 116, 1.1f, 0.33f, player: true, flash: 0.24f);
                SpawnSmoke(s, r.X, r.Y, 12, 1.1f);
                s.Note = "Stratospheric raider destroyed";
                s.NoteT = 0.9f;
                s.Raiders.RemoveAt(i);
            }
        }
    }
}
