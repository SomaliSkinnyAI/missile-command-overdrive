using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Bosses;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Combat helpers: creating projectiles, explosions, particles, damage, kills.</summary>
public static class Combat
{
    // --- Enemy Projectile Factory ---
    public static Enemy CreateEnemyProjectile(GameState s, string v, float sx, float sy, TargetInfo t,
        float? blastOverride = null, float? homingOverride = null, float? ampOverride = null, float? fqOverride = null,
        bool mirv = false)
    {
        var def = VariantStats.Def(v);
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

        // §5 4.2: a heavy tagged by the wave plan MIRV-splits below half altitude
        // (existing SplitAt/SplitMissile machinery; the split point itself is a
        // runtime jitter on the cosmetic stream, matching the "split" variant)
        bool isMirv = mirv && v == "heavy";

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
            Resistance = def.Resistance,
            ZigPhase = (v is "zig" or "drone" or "cruise" or "spit" or "hell") ? RandHelper.Next01() * MathH.TAU : 0,
            ZigAmp = amp,
            HomingFactor = homing,
            Split = v == "split",
            SplitAt = v == "split" ? MathH.Rand(0.4f, 0.63f) : isMirv ? MathH.Rand(0.46f, 0.56f) : 0,
            HasSplit = false,
            Hp = def.Hp,
            Mirv = isMirv,
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
        m.DeployAt = v == "carrier" ? MathH.Rand(def.DeployMin, def.DeployMax) : 0;
        m._Deployed = false;
        m._Val = def.Value;
        // §5 4.2 stealth decloak pings: desynchronized start offsets (cosmetic)
        if (def.CloakPing > 0) m.PingT = MathH.Rand(0.5f, def.CloakPing);

        s.Enemies.Add(m);
        return m;
    }

    // --- Explosion Factory ---
    public static void SpawnExpl(GameState s, float x, float y, float maxRadius = 92f,
        float life = 1.3f, float shakeTime = 0.36f, bool player = false, bool emp = false,
        bool noShake = false, float flash = 0f, bool heavy = false, bool chainChild = false)
    {
        // §5 4.3 CHAIN PULSE perk: the player's EMP schedules one echo pulse —
        // GameUpdate fires it with chainChild:true so an echo never re-chains
        if (emp && player && !chainChild && s.Perks.EmpChain)
        {
            s.Perks.ChainT = 0.55f;
            s.Perks.ChainX = x;
            s.Perks.ChainY = y;
        }
        s.Explosions.Add(new Explosion
        {
            Id = s.NewId(),
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

        if (!noShake) s.AddTrauma(emp ? 0.45f : player ? 0.15f : heavy ? 0.4f : 0.25f);
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

        // §5 5.3 layered blast anatomy: 1-frame flash quad on everything; EMP
        // keeps its cyan omni ring burst (its identity), every other blast gets
        // the flash → hot sparks → embers → dark smoke → debris composition.
        float m = MathH.Clamp(maxRadius / 100f, 0.9f, 1.9f);
        SpawnBlastFlash(s, x, y, maxRadius);
        if (emp) SpawnSparks(s, x, y, player, emp, m);
        else SpawnBlastRecipe(s, x, y, maxRadius, heavy, m);
    }

    // §5 5.3 pool caps — spawn sites clamp against these; debris evicts oldest
    public const int MaxSparks = 480;
    public const int MaxSmoke = 168;
    public const int MaxDebris = 80;
    public const int MaxBlastFlashes = 24;
    public const float BlastFlashLife = 0.038f; // ~1-2 frames at 58 fps

    static void SpawnBlastFlash(GameState s, float x, float y, float maxRadius)
    {
        // §5 3.1 photosensitivity: the per-detonation white pop is a flash like
        // the full-screen one (Renderer.cs:610) — suppress it under FlashReduction.
        // Early-return keeps default-off behavior byte-identical and skips the
        // wasted pool insert entirely.
        if (s.Settings.FlashReduction) return;
        if (s.BlastFlashes.Count >= MaxBlastFlashes) return;
        s.BlastFlashes.Add(new BlastFlash
        {
            X = x, Y = y,
            Size = maxRadius * 0.6f,
            Rot = MathH.Rand(0, 90),
            Life = BlastFlashLife
        });
    }

    /// <summary>§5 5.3: spark/ember/smoke/debris composition for one non-EMP blast,
    /// scaled by blast size. Caller spawns the flash quad. Structs only.</summary>
    static void SpawnBlastRecipe(GameState s, float x, float y, float maxRadius, bool heavy, float m)
    {
        // 8-14 fast white-hot sparks — the detonation core; high drag kills
        // them within ~0.3 s so the white reads as a pop, not a spray
        int hot = Math.Min((int)(MathH.Rand(8, 14.99f) * MathF.Min(m, 1.5f)),
            MaxSparks - s.Sparks.Count);
        for (int i = 0; i < hot; i++)
        {
            float a = RandHelper.Next01() * MathH.TAU;
            float sp = MathH.Rand(260, 760) * m;
            float life = MathH.Rand(0.14f, 0.34f);
            s.Sparks.Add(new Spark
            {
                X = x, Y = y,
                Vx = MathF.Cos(a) * sp,
                Vy = MathF.Sin(a) * sp,
                Life = life, MaxLife = life,
                Size = MathH.Rand(1.5f, 3f),
                R = 255, G = 248, B = 232,
                Kind = SparkKind.Hot
            });
        }

        // 4-8 gravity embers — warm, longer-lived, settle on the ground
        if (maxRadius >= 52)
        {
            int emb = Math.Min((int)(MathH.Rand(4, 8.99f) * MathF.Min(m, 1.5f)),
                MaxSparks - s.Sparks.Count);
            for (int i = 0; i < emb; i++)
            {
                float a = RandHelper.Next01() * MathH.TAU;
                float sp = MathH.Rand(50, 190) * m;
                float life = MathH.Rand(1.1f, 2.3f);
                s.Sparks.Add(new Spark
                {
                    X = x, Y = y,
                    Vx = MathF.Cos(a) * sp,
                    Vy = MathF.Sin(a) * sp - MathH.Rand(30, 110),
                    Life = life, MaxLife = life,
                    Size = MathH.Rand(1.1f, 2.2f),
                    R = 255, G = (byte)MathH.Rand(120, 185), B = (byte)MathH.Rand(40, 90),
                    Kind = SparkKind.Ember
                });
            }
        }

        // 2-4 dark alpha-blended smoke puffs — rising, expanding (the renderer
        // draws Alpha-class smoke as dark translucent, never additive)
        if (maxRadius >= 60)
        {
            int n = Math.Min(heavy ? 4 : 2 + (RandHelper.Next01() < 0.5f ? 1 : 0),
                MaxSmoke - s.SmokeParts.Count);
            for (int i = 0; i < n; i++)
            {
                float life = MathH.Rand(1.9f, 3.4f);
                s.SmokeParts.Add(new Smoke
                {
                    X = x + MathH.Rand(-0.3f, 0.3f) * maxRadius,
                    Y = y + MathH.Rand(-12, 10),
                    Vx = MathH.Rand(-18, 18),
                    Vy = -MathH.Rand(24, 52),
                    Life = life, MaxLife = life,
                    Size = maxRadius * MathH.Rand(0.16f, 0.3f),
                    Alpha = MathH.Rand(0.47f, 0.63f), // peaks in the 120-160/255 band
                    Blend = BlendClass.Alpha
                });
            }
        }

        // city-palette debris chunks → persistent ground litter (§5 5.3 permanence)
        if (maxRadius >= 64)
            SpawnDebrisChunks(s, x, y, (int)(MathH.Rand(2, 5.99f) * MathF.Min(m, 1.6f)), m);
    }

    // City-palette chunk tones (DrawCityAlive body gradient / concrete / charred
    // frame / neon glass) — keep in step with the §5 5.4 ThemePalette sweep.
    static readonly (byte R, byte G, byte B)[] DebrisPalette =
    [
        (52, 62, 96),    // glass-gradient tower mid
        (30, 38, 64),    // tower base shadow
        (96, 110, 134),  // concrete grey
        (24, 30, 46),    // charred frame
        (124, 188, 232), // neon window shard
    ];

    public static void SpawnDebrisChunks(GameState s, float x, float y, int n, float m = 1f)
    {
        for (int i = 0; i < n; i++)
        {
            if (s.DebrisParts.Count >= MaxDebris) s.DebrisParts.RemoveAt(0); // oldest litter evicted
            var c = DebrisPalette[(int)(RandHelper.Next01() * 0.999f * DebrisPalette.Length)];
            float a = RandHelper.Next01() * MathH.TAU;
            float sp = MathH.Rand(60, 230) * m;
            float life = MathH.Rand(1.2f, 2f); // cooling clock only — litter persists
            s.DebrisParts.Add(new Debris
            {
                X = x, Y = y,
                Vx = MathF.Cos(a) * sp,
                Vy = MathF.Sin(a) * sp - MathH.Rand(60, 170),
                Life = life, MaxLife = life,
                Size = MathH.Rand(1.6f, 3.4f),
                Rot = RandHelper.Next01() * MathH.TAU,
                RotSpeed = MathH.Rand(-7, 7),
                R = c.R, G = c.G, B = c.B
            });
        }
    }

    public static void SpawnSparks(GameState s, float x, float y, bool player, bool emp, float m = 1f)
    {
        int n = Math.Min((int)((emp ? 46 : player ? 18 : 24) * m), MaxSparks - s.Sparks.Count);
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

    // §5 3.5 juice: gold scrap sparks that magnet-stream to the HUD scrap counter
    // (homing handled in GameUpdate.UpdParticles via the Spark.Target flag).
    const int MaxScrapSparks = 64; // global alive cap

    static void SpawnScrapSparks(GameState s, float x, float y, int scrap)
    {
        int alive = 0;
        for (int i = 0; i < s.Sparks.Count; i++)
            if (s.Sparks[i].Target) alive++;
        int n = 2 + scrap / 6;
        if (n > 7) n = 7;
        if (n > MaxScrapSparks - alive) n = MaxScrapSparks - alive;
        for (int i = 0; i < n; i++)
        {
            float a = RandHelper.Next01() * MathH.TAU;
            float sp = MathH.Rand(70, 190);
            float life = MathH.Rand(0.55f, 0.75f);
            s.Sparks.Add(new Spark
            {
                X = x, Y = y,
                Vx = MathF.Cos(a) * sp,
                Vy = MathF.Sin(a) * sp - MathH.Rand(20, 70),
                Life = life, MaxLife = life,
                Size = MathH.Rand(1.5f, 2.5f),
                R = 255, G = (byte)MathH.Rand(192, 226), B = (byte)MathH.Rand(60, 110),
                Target = true
            });
        }
    }

    public static void SpawnSmoke(GameState s, float x, float y, int n, float k = 1f)
    {
        n = Math.Min(n, MaxSmoke - s.SmokeParts.Count);
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
                Alpha = MathH.Rand(0.16f, 0.35f),
                Blend = BlendClass.Alpha
            });
        }
    }

    public static void SpawnScorch(GameState s, float x, float y)
    {
        s.Scorches.Add(new Scorch
        {
            X = x, Y = y,
            Radius = MathH.Rand(12, 28),
            // §5 5.3: Life is the fade reservoir consumed at wave start; the
            // mark holds at full strength for the whole wave (extended life)
            Life = MathH.Rand(6, 12),
            Heat = 1f // lingering ground glow, cools over ~2.5 s
        });
        if (s.Scorches.Count > 40) s.Scorches.RemoveAt(0);
    }

    // --- Damage / Kill ---
    /// <summary>The single kill-bookkeeping site: score, combo, max combo, Kill event.
    /// comboPerks (combo banner + EMP grant) historically fire only on enemy-missile kills.</summary>
    public static void RegKill(GameState s, int value, float x, float y, bool comboPerks = false)
    {
        // §5 3.2: Profile.OnGameOver snapshots Score/MaxCombo on the phase edge,
        // but lingering explosions/in-flight interceptors keep killing afterwards.
        // Freeze run-scoped totals (Score/Scrap/Combo) so the death screen matches
        // the saved table row; the Kill event still fires for FX + lifetime stats.
        // §5 6.3: the bridge covers BOTH the ceremony and the GameOver tail so the
        // grade/score the player watches count up never drifts from the saved row.
        if (s.GameOver)
        {
            s.Events.Emit(EventKind.Kill, x, y, value);
            return;
        }
        // §5 4.3 OVERDRIVE SCORING perk scales the combo portion of the bonus
        float bonus = 1 + MathF.Min(2.2f, s.Combo * 0.09f) * s.Perks.ComboBonusMult;
        int gain = (int)MathF.Round(value * bonus);
        s.Score += gain;
        // §5 3.5 scrap economy: flat Value/10 (no combo multiplier — the economy
        // must not snowball with score), shed as gold sparks toward the HUD
        // counter. §5 4.3 SCRAP MAGNET adds a flat per-kill bonus.
        int scrap = value / 10 + s.Perks.ScrapPerKill;
        if (scrap > 0)
        {
            s.Scrap += scrap;
            SpawnScrapSparks(s, x, y, scrap);
        }
        s.Combo++;
        s.ComboTimer = 4 + s.Perks.ComboTimeBonus; // §5 4.3 COMBO CAPACITOR
        s.ComboPop = 1f; // §5 4.4 combo-ring squash-stretch pop
        s.MaxCombo = Math.Max(s.MaxCombo, s.Combo);
        s.Events.Emit(EventKind.Kill, x, y, value);

        if (!comboPerks) return;
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
            SynthAudio.EmpReady(); // §5 4.5 charge-grant ping
        }
    }

    public static void RegKill(GameState s, Enemy m, float x, float y)
    {
        // All enemy kill paths (incl. PhalanxSystem) funnel here — flag for HR alive-checks
        m.Dead = true;
        RegKill(s, m._Val, x, y, comboPerks: true);

        float r = m.Variant is "heavy" or "carrier" ? 94 : m.Variant == "cruise" ? 78 : 70;
        SpawnExpl(s, x, y, r, 0.9f, 0.41f, player: true,
            flash: m.Variant is "heavy" or "carrier" ? 0.16f : 0.08f,
            noShake: m.Variant is not "heavy" and not "carrier");
        // §5 4.4 pitch ladder: kill chains climb a semitone per combo step
        SynthAudio.Hit(MathH.Clamp(x / s.W, 0, 1), m.Variant is "heavy" or "carrier" ? 1f : 0.6f, s.Combo);
    }

    public static bool DamageEnemyUnit(GameState s, Enemy m, float x, float y, float dmg = 1)
    {
        if (m.Hp > dmg)
        {
            m.Hp -= dmg;
            m.FlashT = 0.05f;
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
        s.Events.Emit(EventKind.GroundImpact, x, y, m._Blast);
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
            foreach (var city in s.Cities)
                if (city.Id == t.Id)
                {
                    if (!city.Destroyed) KillCity(s, city, x, y);
                    break;
                }
        }
        else if (t.Type == "base")
        {
            foreach (var b in s.Bases)
                if (b.Id == t.Id)
                {
                    if (!b.Destroyed)
                    {
                        b.Destroyed = true;
                        b.Ammo = 0;
                        s.AliveBases--;
                        s.Events.Emit(EventKind.BaseDestroyed, b.X, b.Y, 1f);
                        SpawnExpl(s, b.X, b.Y - 4, 84, 1.05f, 0.3f, flash: 0.18f);
                    }
                    break;
                }
        }
        else if (t.Type == "phalanx")
        {
            foreach (var p in s.Phalanxes)
                if (p.Id == t.Id)
                {
                    if (!p.Destroyed)
                    {
                        p.Destroyed = true;
                        p.Ammo = 0;
                        SpawnExpl(s, p.X, p.Y - 4, 72, 0.9f, 0.3f, flash: 0.14f);
                    }
                    break;
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
                    s.AliveBases--;
                    s.Events.Emit(EventKind.BaseDestroyed, b.X, b.Y, 1f);
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
        // §5 4.3 AEGIS DOME perk: absorbs the first city hit of every wave
        // (latch re-armed in WaveSystem.StartWave)
        if (s.Perks.CityShield && !s.Perks.CityShieldUsed)
        {
            s.Perks.CityShieldUsed = true;
            s.LightBursts.Add(new LightBurst
            {
                X = city.X, Y = s.GroundY - 26,
                Radius = 120, Life = 0.55f, MaxLife = 0.55f
            });
            s.Flash = MathF.Max(s.Flash, 0.12f);
            s.Note = "AEGIS DOME absorbed the hit";
            s.NoteT = 1.5f;
            SynthAudio.ShieldHum(MathH.Clamp(city.X / s.W, 0, 1));
            return;
        }
        city.Destroyed = true;
        s.AliveCities--;
        s.Events.Emit(EventKind.CityDestroyed, x, y, 1f);
        SpawnExpl(s, x, y, MathH.Rand(74, 120), 1.08f, 0.3f, flash: 0.22f, heavy: true);
        SpawnSmoke(s, x, y - 6, 16, 1.2f);
        // §5 5.3: a collapsing city throws extra building chunks on top of the
        // blast recipe's — this litter is the wave's most legible scar
        SpawnDebrisChunks(s, city.X, s.GroundY - 18, 8, 1.25f);
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
                // Nearest alive base with ammo (cooldown intentionally unchecked here,
                // matching the old LINQ path — the guard below handles it)
                float bestD = float.MaxValue;
                foreach (var b in s.Bases)
                {
                    if (b.Destroyed || b.Ammo <= 0) continue;
                    float d = MathF.Abs(b.X - tx);
                    if (d < bestD) { bestD = d; c = b; }
                }
                if (c == null) return false;
            }
        }

        if (c.Destroyed || c.Ammo <= 0 || c.Cooldown > 0)
        {
            s.AddTrauma(0.05f);
            return false;
        }

        float y2 = MathF.Min(ty, s.GroundY - 56);
        float dx = tx - c.X, dy = y2 - c.Y;
        float dist = MathF.Max(90, MathF.Sqrt(dx * dx + dy * dy));
        float speed = VariantStats.InterceptorSpeed(s);
        float dur = dist / speed;

        c.Ammo--;
        float reloadMult = s.Upgrades.ReloadMult * s.Perks.ReloadMult; // §5 4.3 RAPID CYCLER
        float lvlReload = 1 + MathF.Min(1.9f, s.Level * 0.03f);
        c.Cooldown = 0.24f / (MathF.Max(0.4f, reloadMult) * lvlReload);

        float blastRadius = 102f * s.Upgrades.BlastScale * s.Perks.BlastMult; // §5 4.3 BIG-BORE WARHEADS

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

        // Micro-feedback: 2-frame silo flash + recoil spring kick; crosshair pop
        // only for mouse fire (auto-defense passes baseIndex)
        c.MuzzleT = Base.MuzzleFlashDur;
        c.RecoilV = 70f;
        if (baseIndex == null)
        {
            s.CrosshairPop = 1f;
            SynthAudio.UiClick(); // §5 4.5 crosshair fire click (manual fire only)
        }

        SynthAudio.Launch(MathH.Clamp(c.X / s.W, 0, 1));
        return true;
    }

    public static bool UseEMP(GameState s)
    {
        if (s.Intro || s.GameOver || s.Emp <= 0 || s.EmpCd > 0) return false;
        s.Emp--;
        s.EmpCd = 13;
        s.Events.Emit(EventKind.Emp, s.MouseX, s.MouseY, 228 * s.Upgrades.EmpScale);
        SpawnExpl(s, s.MouseX, s.MouseY,
            228 * s.Upgrades.EmpScale, 1.45f, 0.42f,
            player: true, emp: true, flash: 0.32f);
        s.AddTrauma(0.4f);
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
    /// <summary>Shared split machinery: "split" variant sheds 2-3 shards; a plan-
    /// tagged MIRV heavy (§5 4.2) always sheds 3 heavier warheads.</summary>
    public static void SplitMissile(GameState s, Enemy m)
    {
        m.HasSplit = true;
        bool mirv = m.Mirv;
        SpawnExpl(s, m.X, m.Y, mirv ? 50 : 42, 0.62f, 0.28f, flash: mirv ? 0.11f : 0.08f, noShake: true);

        int count = mirv ? 3 : 2 + (RandHelper.Next01() < 0.35f ? 1 : 0);
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
                blastOverride: mirv ? MathH.Rand(56, 84) : MathH.Rand(40, 64));
        }
    }

    /// <summary>§5 4.2 carrier deploy: 2-3 drone children released mid-flight while
    /// the carrier flies on. Only reached via the living carrier's update — an
    /// early kill removes it first and denies the spawn.</summary>
    public static void DeployDrones(GameState s, Enemy carrier)
    {
        int count = 2 + (RandHelper.Next01() < 0.5f ? 1 : 0); // cosmetic stream
        for (int i = 0; i < count; i++)
        {
            var t = WaveSystem.ChooseTarget(s, "drone");
            if (t == null) break;
            CreateEnemyProjectile(s, "drone", carrier.X + MathH.Rand(-14, 14), carrier.Y + 8, t.Value);
        }
        // Bay-release pop (visual only — non-player explosion, no shake)
        SpawnExpl(s, carrier.X, carrier.Y + 6, 30, 0.5f, 0.3f, flash: 0.04f, noShake: true);
        s.LightBursts.Add(new LightBurst
        {
            X = carrier.X, Y = carrier.Y + 6,
            Radius = 52, Life = 0.4f, MaxLife = 0.4f
        });
    }

    static TargetInfo? ChooseTargetForShard(GameState s) => WaveSystem.ChooseTarget(s, "shard");

    // §5 4.3 MIRV INTERCEPTOR: shorter trail cap for the children
    public const int MirvChildTrail = 18;

    /// <summary>§5 4.3 MIRV INTERCEPTOR perk: a base-launched interceptor sheds
    /// 3 homing children at mid-flight, reusing the HellRaiser Hr* machinery on
    /// PlayerMissile (steering/retargeting/detonation all ride the existing Hr
    /// path in GameUpdate.UpdPlayer). The caller removes the parent.</summary>
    public static void SplitPlayerMissile(GameState s, PlayerMissile m)
    {
        float ang0 = MathF.Atan2(m._Vy, m._Vx);
        float speed = MathF.Sqrt(m._Vx * m._Vx + m._Vy * m._Vy);
        float remain = MathF.Max(0.3f, m._Dur - m._Elapsed);
        // Split pop — a small player blast, so the break-up itself can clip a track
        SpawnExpl(s, m.X, m.Y, 26, 0.4f, 0.3f, player: true, flash: 0.04f, noShake: true);
        for (int k = 0; k < 3; k++)
        {
            float ang = ang0 + (k - 1) * 0.42f; // fan: left / straight / right
            s.PlayerMissiles.Add(new PlayerMissile(MirvChildTrail)
            {
                Id = s.NewId(),
                X = m.X, Y = m.Y,
                Sx = m.X, Sy = m.Y,
                Tx = m.Tx, Ty = m.Ty,
                Speed = speed,
                BaseIndex = m.BaseIndex,
                Auto = m.Auto,
                _Vx = MathF.Cos(ang) * speed,
                _Vy = MathF.Sin(ang) * speed,
                _Dur = remain + MathH.Rand(0.2f, 0.45f),
                _Elapsed = 0,
                _Blast = m._Blast * 0.55f,
                Hr = true,                      // reuse the homing machinery
                HrSpeed = speed * 0.92f,
                HrTurn = MathH.Rand(5.0f, 7.5f),
                HrRetarget = 0,                 // adopt a target on the first live frame
                SquiggleAmp = MathH.Rand(4, 9),
                SquiggleFreq = MathH.Rand(3.1f, 6.8f),
                SquigglePhase = RandHelper.Next01() * MathH.TAU
            });
        }
        SynthAudio.Launch(MathH.Clamp(m.X / s.W, 0, 1));
    }

    // §5 4.2 shield drones: per-frame scratch of living bubbles (zero alloc)
    static readonly Enemy?[] _shieldScratch = new Enemy?[8];
    static readonly float _shieldR2 =
        VariantStats.Def("shield").ShieldRadius * VariantStats.Def("shield").ShieldRadius;

    /// <summary>True when a living shield drone covers the target while the blast
    /// center is outside its bubble. The drone itself is always damageable.</summary>
    static bool ShieldBlocks(Enemy target, Explosion e, int shields)
    {
        if (target.Variant == "shield") return false;
        if (e.Emp) return false; // EMP pierces bubbles — it's the anti-overwhelm tool
        for (int i = 0; i < shields; i++)
        {
            var sd = _shieldScratch[i]!;
            if (sd == target || sd.Dead) continue; // Dead recheck: a drone killed earlier this frame stops blocking
            float tx = target.X - sd.X, ty = target.Y - sd.Y;
            if (tx * tx + ty * ty > _shieldR2) continue; // target not covered
            float ex = e.X - sd.X, ey = e.Y - sd.Y;
            if (ex * ex + ey * ey <= _shieldR2) continue; // blast inside — penetrates
            sd.ShieldFlashT = 0.25f;                      // bubble ripple feedback
            return true;
        }
        return false;
    }

    // --- Collisions ---
    public static void RunCollisions(GameState s)
    {
        // §5 4.2: collect living shield drones once for the overlap checks below
        int shields = 0;
        for (int i = 0; i < s.Enemies.Count && shields < _shieldScratch.Length; i++)
        {
            var sd = s.Enemies[i];
            if (!sd.Dead && sd.Variant == "shield") _shieldScratch[shields++] = sd;
        }

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
                    // A blast whose center is outside a covering bubble is blocked
                    // for this target — another explosion may still connect
                    if (shields > 0 && ShieldBlocks(m, e, shields)) continue;
                    removed = DamageEnemyUnit(s, m, m.X, m.Y, 1);
                    break;
                }
            }
            if (removed) s.Enemies.RemoveAt(i);
        }
        // Drop scratch refs so dead drones aren't pinned across frames/waves
        for (int i = 0; i < shields; i++) _shieldScratch[i] = null;

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
                    u.FlashT = 0.05f;
                    hit = true;
                    SpawnExpl(s, u.X, u.Y, u.Boss ? 64 : 44, 0.58f, 0.34f, player: true, flash: 0.06f, noShake: true);
                    break;
                }
            }
            if (!hit) continue;
            if (u.Hp <= 0)
            {
                u.Dead = true;
                RegKill(s, u.Boss ? 1500 : 260, u.X, u.Y);
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
                    r.FlashT = 0.05f;
                    hit = true;
                    SpawnExpl(s, r.X + MathH.Rand(-8, 8), r.Y + MathH.Rand(-5, 5), 42, 0.52f, 0.35f,
                        player: true, flash: 0.05f, noShake: true);
                    break;
                }
            }
            if (!hit) continue;
            if (r.Hp <= 0)
            {
                r.Dead = true;
                RegKill(s, 460, r.X, r.Y);
                SpawnExpl(s, r.X, r.Y, 116, 1.1f, 0.33f, player: true, flash: 0.24f);
                SpawnSmoke(s, r.X, r.Y, 12, 1.1f);
                s.Note = "Stratospheric raider destroyed";
                s.NoteT = 0.9f;
                s.Raiders.RemoveAt(i);
            }
        }

        // §5 6.1: ONE generic boss damage + phase loop (pods, hull, phase
        // crossings, death + Epic reward) replaces the two copy-pasted
        // Mothership/Daemon blocks that used to live here.
        BossSystem.RunDamage(s);

        // Fighters (mothership-deployed) vs player explosions
        for (int i = s.Fighters.Count - 1; i >= 0; i--)
        {
            var f = s.Fighters[i];
            bool dead = false;
            foreach (var e in s.Explosions)
            {
                if (!e.Player) continue;
                float dx = f.X - e.X, dy = f.Y - e.Y;
                float r = MathF.Max(18, e.Radius * 0.55f);
                if (dx * dx + dy * dy <= r * r)
                {
                    f.Hp -= 1;
                    f.FlashT = 0.05f;
                    SpawnExpl(s, f.X + MathH.Rand(-4, 4), f.Y, 28, 0.42f, 0.22f,
                        player: true, noShake: true);
                    if (f.Hp <= 0)
                    {
                        RegKill(s, 180, f.X, f.Y);
                        SpawnExpl(s, f.X, f.Y, 62, 0.72f, 0.28f, player: true, flash: 0.08f);
                        SpawnSmoke(s, f.X, f.Y, 6, 0.8f);
                        dead = true;
                    }
                    break;
                }
            }
            if (dead) s.Fighters.RemoveAt(i);
        }
        // §5 6.1: Daemon damage now runs inside BossSystem.RunDamage above.
    }
}
