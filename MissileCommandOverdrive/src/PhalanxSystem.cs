using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Phalanx CIWS turret: auto-acquires targets and fires rapid tracers.</summary>
public static class PhalanxSystem
{
    public static void UpdateAll(GameState s, float dt)
    {
        if (s.Intro || s.GameOver) return;
        foreach (var p in s.Phalanxes) Update(s, p, dt);
    }

    static void Update(GameState s, Phalanx p, float dt)
    {
        float phEff = s.Upgrades.PhalanxEff;
        float cdMult = (s.Cities.Count > 2 && !s.Cities[2].Destroyed ? 2.2f : 1f)
            * (1 + (phEff - 1) * 0.55f);

        if (p.Cool > 0) p.Cool = MathF.Max(0, p.Cool - dt * cdMult);
        if (p.Heat > 0) p.Heat = MathF.Max(0, p.Heat - dt * 1.25f * cdMult);
        if (p.AudioT > 0) p.AudioT = MathF.Max(0, p.AudioT - dt);

        // Barrel spin: slow idle rotation, fast spin when firing (matches HTML exactly)
        bool firing = p.FireMix > 0.01f || p.Heat > 0.01f;
        float spinTarget = firing ? (10f + p.FireMix * 18f) : 1.5f; // rad/sec — max 28
        float ramp = firing ? 10f : 1.8f;
        p.SpinSpeed += (spinTarget - p.SpinSpeed) * MathF.Min(1, dt * ramp);
        p.SpinAngle = (p.SpinAngle + p.SpinSpeed * dt) % (MathF.PI * 2);

        if (p.Destroyed)
        {
            p.Target = null;
            p.FireAcc = 0;
            return;
        }

        float fireRange = 620 + MathF.Max(0, phEff - 1) * 110;
        float lockRange = MathF.Max(fireRange * 2.15f, s.W * 1.08f);

        // Acquire target
        var best = AcquireTarget(s, p, lockRange, fireRange);
        p.Target = best;

        if (!float.IsFinite(p.AimAng)) p.AimAng = -MathF.PI * 0.5f;

        if (best != null)
        {
            float aimX = best.Value.X + best.Value.Vx * 0.06f;
            float aimY = best.Value.Y + best.Value.Vy * 0.06f;
            p.AimX = aimX;
            p.AimY = aimY;

            float desired = MathF.Atan2(aimY - (p.Y - 52), aimX - p.X);
            float turnRate = (2.8f + (1 - p.Heat) * 2.4f + MathF.Min(1.3f, s.Level * 0.02f))
                * (1 + (phEff - 1) * 0.18f);
            float diff = AngleDelta(p.AimAng, desired);
            float step = turnRate * dt;
            p.AimAng += MathH.Clamp(diff, -step, step);
            p.AimErr = MathF.Abs(diff);
        }
        else
        {
            p.AimX = p.X + MathF.Cos(p.AimAng) * 160;
            p.AimY = (p.Y - 52) + MathF.Sin(p.AimAng) * 160;
            p.AimErr = MathF.PI;
            p.FireMix = MathF.Max(0, p.FireMix - dt * 4.2f);
            return;
        }

        bool inRange = best.Value.Dist <= fireRange;
        bool aligned = p.AimErr <= 0.16f;
        if (!inRange || !aligned || p.Ammo <= 0 || p.Cool > 0)
        {
            p.FireMix = MathF.Max(0, p.FireMix - dt * 4.2f);
            return;
        }

        // Fire
        float fireRate = ((p.Heat > 0.72f ? 56 : 94) + MathF.Min(28, s.Level * 2.2f))
            * (1 + (phEff - 1) * 0.4f);
        p.FireAcc += dt * fireRate;

        int shots = 0;
        while (p.FireAcc >= 1 && p.Ammo > 0 && shots < 120)
        {
            p.FireAcc -= 1;
            p.Ammo--;
            shots++;

            float pivotY = p.Y - 52;
            float muzzleDist = 48;
            float srcX = p.X + MathF.Cos(p.AimAng) * muzzleDist;
            float srcY = pivotY + MathF.Sin(p.AimAng) * muzzleDist;
            float spread = 5 + (1 - MathH.Clamp(1 - p.AimErr / 0.18f, 0, 1)) * 8;
            float tx = p.AimX + MathH.Rand(-spread, spread);
            float ty = p.AimY + MathH.Rand(-spread, spread);

            // Tracer trail
            s.Trails.Add(new Trail
            {
                X = (srcX + tx) * 0.5f, Y = (srcY + ty) * 0.5f,
                Vx = 0, Vy = 0,
                Life = 0.12f, MaxLife = 0.12f,
                Size = 1.5f,
                R = 255, G = 222, B = 156
            });

            // Hit check for enemies
            if (best.Value.Kind == "enemy")
            {
                var target = best.Value.EnemyRef;
                if (target == null || !s.Enemies.Contains(target)) break;

                float miss = MathF.Sqrt((tx - target.X) * (tx - target.X) + (ty - target.Y) * (ty - target.Y));
                float missRadius = 24 + (target.Variant == "fast" ? 6 : target.Variant == "zig" ? 8 : 0);
                float aimQ = MathH.Clamp(1 - p.AimErr / 0.18f, 0, 1);
                float hitChance = MathH.Clamp((1 - best.Value.Dist / fireRange) * 0.37f + 0.09f, 0.08f, 0.56f)
                    * (1 - target.Resistance * 0.9f)
                    * (target.Variant == "heavy" ? 0.6f : 1f)
                    * (1 + (phEff - 1) * 0.45f)
                    * (0.52f + aimQ * 0.78f);

                if (miss < missRadius && RandHelper.Next01() < hitChance)
                {
                    bool killed = Combat.DamageEnemyUnit(s, target, target.X, target.Y, target.Variant == "carrier" ? 0.9f : 1);
                    if (killed)
                    {
                        s.Enemies.Remove(target);
                        break;
                    }
                }
            }
            else if (best.Value.Kind == "ufo")
            {
                var target = best.Value.UfoRef;
                if (target == null || !s.UFOs.Contains(target)) break;
                float aimQ = MathH.Clamp(1 - p.AimErr / 0.18f, 0, 1);
                float hitChance = MathH.Clamp((1 - best.Value.Dist / (fireRange * 1.08f)) * 0.42f + 0.08f, 0.08f, 0.44f)
                    * (1 + (phEff - 1) * 0.4f) * (0.48f + aimQ * 0.82f);
                if (RandHelper.Next01() < hitChance)
                {
                    target.Hp--;
                    Combat.SpawnExpl(s, target.X + MathH.Rand(-9, 9), target.Y + MathH.Rand(-5, 5),
                        30, 0.43f, 0.34f, player: true, flash: 0.03f, noShake: true);
                    if (target.Hp <= 0)
                    {
                        float bonus = 1 + MathF.Min(2.2f, s.Combo * 0.09f);
                        s.Score += (int)MathF.Round((target.Boss ? 1500 : 260) * bonus);
                        s.Combo++; s.ComboTimer = 4;
                        s.MaxCombo = Math.Max(s.MaxCombo, s.Combo);
                        Combat.SpawnExpl(s, target.X, target.Y, target.Boss ? 140 : 96, target.Boss ? 1.4f : 1.02f, 0.34f, player: true, flash: target.Boss ? 0.32f : 0.18f);
                        s.UFOs.Remove(target);
                        break;
                    }
                }
            }
            else if (best.Value.Kind == "raider")
            {
                var target = best.Value.RaiderRef;
                if (target == null || !s.Raiders.Contains(target)) break;
                float aimQ = MathH.Clamp(1 - p.AimErr / 0.18f, 0, 1);
                float hitChance = MathH.Clamp((1 - best.Value.Dist / (fireRange * 1.12f)) * 0.46f + 0.1f, 0.1f, 0.58f)
                    * (1 + (phEff - 1) * 0.35f) * (0.5f + aimQ * 0.78f);
                if (RandHelper.Next01() < hitChance)
                {
                    target.Hp--;
                    Combat.SpawnExpl(s, target.X + MathH.Rand(-8, 8), target.Y + MathH.Rand(-5, 5),
                        38, 0.46f, 0.35f, player: true, flash: 0.05f, noShake: true);
                    if (target.Hp <= 0)
                    {
                        float bonus = 1 + MathF.Min(2.2f, s.Combo * 0.09f);
                        s.Score += (int)MathF.Round(460 * bonus);
                        s.Combo++; s.ComboTimer = 4;
                        s.MaxCombo = Math.Max(s.MaxCombo, s.Combo);
                        Combat.SpawnExpl(s, target.X, target.Y, 116, 1.1f, 0.33f, player: true, flash: 0.24f);
                        s.Raiders.Remove(target);
                        break;
                    }
                }
            }
        }

        if (shots > 0)
        {
            p.Heat = MathF.Min(1, p.Heat + shots * (0.0036f / MathF.Max(1, phEff)));
            p.FireMix = MathF.Min(1, p.FireMix + shots * 0.03f);
        }
        else
        {
            p.FireMix = MathF.Max(0, p.FireMix - dt * 4.2f);
        }

        if (p.Ammo <= 0 && p.Cool <= 0)
        {
            p.Cool = 2.1f / MathF.Max(1, phEff);
            s.Note = "Phalanx out of ammo";
            s.NoteT = 0.75f;
        }
    }

    struct TargetEntry
    {
        public string Kind;
        public float X, Y, Vx, Vy, Dist, Score;
        public Enemy? EnemyRef;
        public UFO? UfoRef;
        public Raider? RaiderRef;
    }

    static TargetEntry? AcquireTarget(GameState s, Phalanx p, float lockRange, float fireRange)
    {
        float originY = p.Y - 52;
        TargetEntry? best = null;

        void Consider(string kind, float x, float y, float vx, float vy, float baseScore,
            Enemy? eRef = null, UFO? uRef = null, Raider? rRef = null)
        {
            float dx = x - p.X, dy = y - originY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > lockRange || y > s.GroundY + 8) return;
            float rangeBias = dist <= fireRange ? 54 : 0;
            float score = baseScore + rangeBias - dist * 0.14f;
            if (best == null || score > best.Value.Score)
                best = new TargetEntry { Kind = kind, X = x, Y = y, Vx = vx, Vy = vy, Dist = dist, Score = score,
                    EnemyRef = eRef, UfoRef = uRef, RaiderRef = rRef };
        }

        foreach (var m in s.Enemies)
        {
            if (m.Y > s.GroundY - 4) continue;
            float eta = m._Dur - m._Elapsed;
            float baseScore = 70 + (m.Target?.Type == "city" ? 62 : 0)
                + (m.Target?.Type == "base" ? 24 : 0)
                + (m.Variant == "ufoBomb" ? 42 : 0)
                + 128 / (eta + 0.75f);
            Consider("enemy", m.X, m.Y, m._Vx, m._Vy, baseScore, eRef: m);
        }
        foreach (var u in s.UFOs)
        {
            float baseScore = 168 + (u.Boss ? 80 : 54);
            Consider("ufo", u.X, u.Y, u.Vx, 0, baseScore, uRef: u);
        }
        foreach (var r in s.Raiders)
        {
            float baseScore = 250;
            Consider("raider", r.X, r.Y, r.Vx, 0, baseScore, rRef: r);
        }

        return best;
    }

    static float AngleDelta(float from, float to)
    {
        float diff = to - from;
        while (diff > MathF.PI) diff -= MathH.TAU;
        while (diff < -MathF.PI) diff += MathH.TAU;
        return diff;
    }
}
