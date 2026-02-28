using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>HellRaiser underground launcher state machine.</summary>
public static class HellRaiserSystem
{
    public static void Update(GameState s, float dt)
    {
        var hr = s.HellRaiser;
        if (hr == null || hr.Destroyed) return;
        bool canOperate = !s.Intro && !s.GameOver && !s.Shop;

        if (hr.State is "active" or "opening" or "rising" && !canOperate)
            hr.Command = "retract";

        switch (hr.State)
        {
            case "hidden":
                if (hr.Cool > 0) hr.Cool = MathF.Max(0, hr.Cool - dt * MathF.Max(1, s.Upgrades.ReloadMult));
                if (hr.Command == "deploy" && hr.Cool <= 0 && hr.Ammo > 0)
                {
                    hr.State = "opening";
                    hr.Command = "idle";
                    s.Note = "Hell Raiser online";
                    s.NoteT = 0.85f;
                }
                break;

            case "opening":
                hr.DoorOpen = MathF.Min(1, hr.DoorOpen + dt * 2.7f);
                if (hr.DoorOpen >= 1) hr.State = "rising";
                break;

            case "rising":
                hr.Lift = MathF.Min(1, hr.Lift + dt * 1.95f);
                if (hr.Lift >= 1)
                {
                    hr.State = "active";
                    hr.FireCd = 0;
                    hr.ActiveTime = 6.4f;
                    s.Note = "Hell Raiser barrage";
                    s.NoteT = 0.85f;
                }
                break;

            case "active":
                hr.ActiveTime -= dt;
                if (hr.Command == "retract" || hr.ActiveTime <= 0 || hr.Ammo <= 0)
                {
                    hr.State = "lowering";
                    hr.Command = "idle";
                    break;
                }
                FireBarrage(s, hr, dt);
                break;

            case "lowering":
                hr.Lift = MathF.Max(0, hr.Lift - dt * 2.15f);
                if (hr.Lift <= 0) hr.State = "closing";
                break;

            case "closing":
                hr.DoorOpen = MathF.Max(0, hr.DoorOpen - dt * 2.7f);
                if (hr.DoorOpen <= 0)
                {
                    hr.State = "cooldown";
                    hr.Cool = MathF.Max(1.4f, 3.2f / MathF.Max(0.6f, s.Upgrades.ReloadMult));
                }
                break;

            case "cooldown":
                hr.Cool = MathF.Max(0, hr.Cool - dt * MathF.Max(1, s.Upgrades.ReloadMult));
                if (hr.Cool <= 0)
                {
                    hr.State = "hidden";
                    hr.Ammo = hr.MaxAmmo;
                    hr.FireCd = 0;
                }
                break;
        }
    }

    static void FireBarrage(GameState s, HellRaiser hr, float dt)
    {
        float topX = hr.X;
        float topY = hr.Y - hr.Lift * 40;

        // Collect weighted targets (enemies, UFOs, raiders)
        var targets = CollectTargets(s, topX, topY);
        if (targets.Count == 0) return;

        float fireRate = 95 + MathF.Min(52, s.Level * 3.4f);
        hr.FireCd += dt * fireRate;

        int shots = 0;
        while (hr.FireCd >= 1 && hr.Ammo > 0 && shots < 22)
        {
            hr.FireCd -= 1;
            hr.Ammo--;
            shots++;

            // Weighted random pick
            var target = PickWeighted(targets);
            float tx = MathH.Clamp(target.X + MathH.Rand(-20, 20), 20, s.W - 20);
            float ty = MathH.Clamp(target.Y + MathH.Rand(-18, 18), 24, s.GroundY - 52);

            LaunchHellRaiserMissile(s, hr, tx, ty, MathH.Rand(820, 1080), target.Kind, target.Id);
        }
    }

    static List<(string Kind, int Id, float X, float Y, float Weight)> CollectTargets(GameState s, float ox, float oy)
    {
        var pool = new List<(string Kind, int Id, float X, float Y, float Weight)>();
        foreach (var m in s.Enemies)
        {
            if (m.Y > s.GroundY + 18) continue;
            float dx = m.X - ox, dy = m.Y - oy;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > 900) continue;
            float distW = 1f / (0.38f + dist * 0.0034f);
            float baseW = 80 + (m.Target?.Type == "city" ? 46 : 0);
            pool.Add(("enemy", m.Id, m.X, m.Y, MathF.Max(1, baseW * distW)));
        }
        foreach (var u in s.UFOs)
        {
            float dx = u.X - ox, dy = u.Y - oy;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > 900) continue;
            float distW = 1f / (0.38f + dist * 0.0034f);
            pool.Add(("ufo", u.Id, u.X, u.Y, MathF.Max(1, (u.Boss ? 200 : 120 + 58) * distW)));
        }
        foreach (var r in s.Raiders)
        {
            float dx = r.X - ox, dy = r.Y - oy;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > 900) continue;
            float distW = 1f / (0.38f + dist * 0.0034f);
            pool.Add(("raider", r.Id, r.X, r.Y, MathF.Max(1, 228 * distW)));
        }
        return pool;
    }

    static (string Kind, int Id, float X, float Y, float Weight) PickWeighted(
        List<(string Kind, int Id, float X, float Y, float Weight)> pool)
    {
        float total = pool.Sum(p => p.Weight);
        float roll = RandHelper.Next01() * total;
        float acc = 0;
        foreach (var p in pool)
        {
            acc += p.Weight;
            if (roll <= acc) return p;
        }
        return pool[^1];
    }

    static void LaunchHellRaiserMissile(GameState s, HellRaiser hr, float tx, float ty, float speed,
        string targetKind, int targetId)
    {
        float sx = hr.X + MathH.Rand(-6, 6);
        float sy = hr.Y - hr.Lift * 40 - 6 + MathH.Rand(-4, 4);
        float dx = tx - sx, dy = ty - sy;
        float dist = MathF.Max(80, MathF.Sqrt(dx * dx + dy * dy));
        float travel = dist / speed;
        float dur = travel + MathH.Rand(0.28f, 0.74f);

        s.PlayerMissiles.Add(new PlayerMissile
        {
            Id = s.NewId(),
            X = sx, Y = sy,
            Sx = sx, Sy = sy,
            Tx = tx, Ty = ty,
            Speed = speed,
            Progress = 0,
            Detonated = false,
            BaseIndex = -1,
            Auto = true,
            _Vx = dx / dur,
            _Vy = dy / dur,
            _Dur = dur,
            _Elapsed = 0,
            _Blast = (34 + MathH.Rand(0, 12)) * (0.55f + s.Upgrades.BlastScale * 0.45f),
            // Homing fields
            Hr = true,
            HrSpeed = speed,
            HrTurn = MathH.Rand(5.6f, 8.8f),
            HrRetarget = MathH.Rand(0.07f, 0.2f),
            HrTargetKind = targetKind,
            HrTargetId = targetId,
            SquiggleAmp = MathH.Rand(5, 13),
            SquiggleFreq = MathH.Rand(3.1f, 6.8f),
            SquigglePhase = RandHelper.Next01() * MathH.TAU
        });

        // Muzzle flash
        float angle = MathF.Atan2(dy, dx);
        s.MuzzleFlashes.Add(new MuzzleFlash
        {
            X = sx, Y = sy,
            Angle = angle,
            Life = 0.14f, MaxLife = 0.14f
        });

        SynthAudio.HellRaiserFire(MathH.Clamp(sx / s.W, 0, 1));
    }

    public static void Toggle(GameState s)
    {
        var hr = s.HellRaiser;
        if (hr == null || hr.Destroyed) return;

        if (hr.State == "hidden" && hr.Cool <= 0)
        {
            hr.Command = "deploy";
        }
        else if (hr.State is "active" or "rising" or "opening")
        {
            hr.Command = "retract";
        }
    }
}
