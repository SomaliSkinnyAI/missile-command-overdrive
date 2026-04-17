using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Auto-defense AI: automatically fires interceptors at the highest-threat enemies.</summary>
public static class AutoDefense
{
    public static void RunAuto(GameState s)
    {
        if (s.Intro || s.GameOver || s.Shop) return;
        var bases = s.Bases.Where(b => !b.Destroyed && b.Ammo > 0 && b.Cooldown <= 0).ToList();
        if (bases.Count == 0) return;

        float autoSpeed = VariantStats.InterceptorSpeed(s, 1.08f);
        int maxShots = Math.Min(10, 3 + Math.Max(0, s.Level - 1) / 10);

        // Sort enemies by threat (highest first)
        var enemies = s.Enemies.OrderByDescending(m => Threat(s, m)).ToList();
        int shots = 0;

        // HIGHEST PRIORITY: hammer the mothership if present (spawning paused while it's alive anyway)
        TargetMothership(s, bases, ref shots, maxShots, autoSpeed);

        foreach (var m in enemies)
        {
            if (shots >= maxShots || bases.Count == 0) break;
            if (m.ReserveUntil > s.Time) continue;

            // Find best base + intercept point
            (Base bestBase, float ix, float iy, float it)? best = null;
            float bestScore = float.MinValue;

            foreach (var b in bases)
            {
                var intr = FindIntercept(s, b, m, autoSpeed);
                if (intr == null) continue;

                float score = Threat(s, m) - intr.Value.t * 42 - MathF.Abs(b.X - intr.Value.x) * 0.045f;
                if (m.Target?.Type == "city") score += 58;
                if (m.Variant is "fast" or "stealth") score += 36;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = (b, intr.Value.x, intr.Value.y, intr.Value.t);
                }
            }

            if (best == null) continue;

            // Don't over-commit: skip if a player missile is already inbound to this intercept point.
            // Multi-HP enemies still allow one extra (to chip).
            int needed = Math.Max(1, m.Hp);
            int inbound = CountInboundAt(s, best.Value.ix, best.Value.iy, 62f);
            if (inbound >= needed) continue;

            bool fired = Combat.LaunchPlayer(s, best.Value.ix, best.Value.iy,
                s.Bases.IndexOf(best.Value.bestBase));
            if (fired)
            {
                // Reservation must outlast the interceptor's flight + explosion check,
                // otherwise RunAuto on subsequent frames re-fires at the same target before the first shot arrives.
                // Single-HP enemies: reserve through arrival + buffer so one interceptor is enough.
                // Multi-HP enemies: shorter reserve so we can chip away with follow-ups.
                float reserve = m.Hp <= 1
                    ? best.Value.it + 0.55f
                    : best.Value.it * 0.9f + 0.24f;
                m.ReserveUntil = s.Time + MathH.Clamp(reserve, 0.5f, 3.6f);
                shots++;
                bases.Remove(best.Value.bestBase);
            }
        }

        // Try to intercept UFOs with remaining bases
        foreach (var u in s.UFOs.OrderByDescending(u => ThreatUfo(s, u)))
        {
            if (shots >= maxShots + 1 || bases.Count == 0) break;
            if (u.ReserveUntil > s.Time) continue;

            (Base bestBase, float ix, float iy, float t)? best = null;
            float bestScore = float.MinValue;

            foreach (var b in bases)
            {
                var intr = FindInterceptUfo(s, b, u, autoSpeed);
                if (intr == null) continue;
                float score = ThreatUfo(s, u) - intr.Value.t * 45 - MathF.Abs(b.X - intr.Value.x) * 0.035f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (b, intr.Value.x, intr.Value.y, intr.Value.t);
                }
            }

            if (best == null) continue;

            int ufoNeeded = Math.Max(1, u.Hp);
            int ufoInbound = CountInboundAt(s, best.Value.ix, best.Value.iy, 70f);
            if (ufoInbound >= ufoNeeded) continue;

            bool fired = Combat.LaunchPlayer(s, best.Value.ix, best.Value.iy,
                s.Bases.IndexOf(best.Value.bestBase));
            if (fired)
            {
                float reserve = u.Hp <= 1
                    ? best.Value.t + 0.6f
                    : best.Value.t * 0.85f + 0.3f;
                u.ReserveUntil = s.Time + MathH.Clamp(reserve, 0.55f, 3.6f);
                shots++;
                bases.Remove(best.Value.bestBase);
            }
        }
    }

    /// <summary>Count in-flight player interceptors whose target point is within radius of (x,y).
    /// Used to prevent the auto-defense from double-committing when an interceptor is already inbound.</summary>
    /// <summary>Keep pounding the mothership every time there's a free silo and no interceptor already inbound to its hull center.</summary>
    static void TargetMothership(GameState s, List<Base> bases, ref int shots, int maxShots, float autoSpeed)
    {
        var ms = s.Mothership;
        if (ms == null) return;

        // Fire-budget for mothership: allow more concentrated volleys so it actually gets hammered down
        int msBudget = Math.Min(3, bases.Count);
        for (int k = 0; k < msBudget; k++)
        {
            if (shots >= maxShots + 2 || bases.Count == 0) break;

            // Pick aim point: hull center with a little spread so shots fan across the hull
            float aimX = ms.X + MathH.Rand(-ms.W * 0.35f, ms.W * 0.35f);
            float aimY = ms.Y + MathH.Rand(-22, 10);

            // Bail if several interceptors are already en route near the aim point
            if (CountInboundAt(s, aimX, aimY, 60f) >= 2) continue;

            // Pick nearest base that can reach
            Base? best = null;
            float bestDist = float.MaxValue;
            foreach (var b in bases)
            {
                float dx = aimX - b.X, dy = aimY - b.Y;
                float d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; best = b; }
            }
            if (best == null) continue;

            bool fired = Combat.LaunchPlayer(s, aimX, aimY, s.Bases.IndexOf(best));
            if (fired)
            {
                shots++;
                bases.Remove(best);
            }
        }
    }

    static int CountInboundAt(GameState s, float x, float y, float radius)
    {
        int count = 0;
        float r2 = radius * radius;
        foreach (var pm in s.PlayerMissiles)
        {
            if (pm.Detonated) continue;
            float dx = pm.Tx - x, dy = pm.Ty - y;
            if (dx * dx + dy * dy <= r2) count++;
        }
        return count;
    }

    static float Threat(GameState s, Enemy m)
    {
        float ti = m._Dur > 0 ? m._Dur - m._Elapsed : 1;
        float tv = 70;
        if (m.Target?.Type == "city") tv = 120;
        if (m.Target?.Type == "base") tv = 100 + 20;
        float mul = m.Variant switch
        {
            "heavy" => 1.65f, "split" => 1.45f, "zig" => 1.35f, "fast" => 1.2f,
            "ufoBomb" => 1.42f, "cruise" => 1.58f, "carrier" => 1.9f, "drone" => 1.28f,
            "spit" => 1.24f, "hell" => 1.74f, _ => 1f
        };
        float hpBonus = MathF.Max(0, m.Hp - 1) * 42;
        return tv * mul + 128 / (ti + 0.75f) + m.Speed * 0.18f + hpBonus;
    }

    static float ThreatUfo(GameState s, UFO u)
    {
        float near = 0;
        foreach (var city in s.Cities)
        {
            if (city.Destroyed) continue;
            float d = MathF.Abs(city.X - u.X);
            if (d < s.W * 0.16f) near = MathF.Max(near, 1 - d / (s.W * 0.16f));
        }
        return 168 + near * 110 + MathF.Abs(u.Vx) * 0.42f + (3 - u.Hp) * 34;
    }

    static (float x, float y, float t)? FindIntercept(GameState s, Base b, Enemy m, float speed)
    {
        float rem = m._Dur - m._Elapsed;
        if (rem <= 0.05f) return null;
        (float x, float y, float t)? best = null;
        float bestQ = float.MaxValue;

        for (float t = 0.1f; t < rem; t += 0.045f)
        {
            // Predict enemy position at time t
            float local = MathH.Clamp(m._Elapsed + t, 0, m._Dur);
            float pp = m._Dur > 0 ? local / m._Dur : 1;
            float px = m.Sx + m._Vx * local;
            float py = m.Sy + m._Vy * local;
            if (m.ZigAmp > 0)
                px += MathF.Sin(pp * m._Fq * MathH.TAU + m.ZigPhase) * m.ZigAmp * (1 - pp * 0.5f);

            if (py >= s.GroundY - 10 || px < -40 || px > s.W + 40) continue;

            float dx = px - b.X, dy = py - b.Y;
            float travel = MathF.Sqrt(dx * dx + dy * dy) / speed;
            float err = MathF.Abs(travel - t);
            if (err > 0.1f) continue;

            float q = err * 8 + t * 0.07f;
            if (q < bestQ) { bestQ = q; best = (px, py, t); }
        }
        return best;
    }

    static (float x, float y, float t)? FindInterceptUfo(GameState s, Base b, UFO u, float speed)
    {
        (float x, float y, float t)? best = null;
        float bestQ = float.MaxValue;

        for (float t = 0.14f; t < 3.4f; t += 0.055f)
        {
            float x = u.X + u.Vx * t;
            float y = u.Y + MathF.Sin((s.Time + t) * 2f + u.BobPhase) * 12;
            if (x < 16 || x > s.W - 16 || y < 24 || y > s.GroundY - 84) continue;

            float dx = x - b.X, dy = y - b.Y;
            float travel = MathF.Sqrt(dx * dx + dy * dy) / speed;
            float err = MathF.Abs(travel - t);
            if (err > 0.13f) continue;

            float q = err * 8 + t * 0.12f + MathF.Abs(b.X - x) * 0.0007f;
            if (q < bestQ) { bestQ = q; best = (x, y, t); }
        }
        return best;
    }
}
