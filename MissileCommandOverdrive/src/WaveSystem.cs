using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

public static class WaveSystem
{
    public static List<WavePlanEntry> BuildPlan(int level)
    {
        int total = 14 + level * 5;
        var plan = new List<WavePlanEntry>();
        float t = 0;

        var weights = new (string v, float w)[]
        {
            ("standard", 58),
            ("fast", level > 1 ? 20 + level * 1.8f : 6),
            ("zig", level > 2 ? 12 + level * 1.8f : 0),
            ("stealth", level > 2 ? 10 + level * 1.6f : 0),
            ("decoy", level > 2 ? 10 + level * 1.4f : 0),
            ("split", level > 3 ? 9 + level * 1.45f : 0),
            ("heavy", level > 4 ? 8 + level * 1.25f : 0),
            ("cruise", level > 3 ? 8 + level * 1.3f : 0),
            ("carrier", level > 5 ? 4 + level * 0.9f : 0),
            ("drone", level > 4 ? 5 + level * 1.1f : 0),
        };

        var laneWeights = new (float v, float w)[]
        {
            (-0.68f, 1f), (-0.35f, 1.2f), (0f, 1.6f), (0.35f, 1.2f), (0.68f, 1f)
        };

        for (int i = 0; i < total && plan.Count < total; i++)
        {
            float salvoChance = MathH.Clamp(0.13f + level * 0.02f, 0.13f, 0.48f);
            int salvo = RandHelper.Next01() < salvoChance ? (RandHelper.Next01() < 0.25f ? 3 : 2) : 1;
            float lane = RandHelper.PickWeighted(laneWeights.Select(x => (x.v, x.w)).ToList());

            for (int s = 0; s < salvo && plan.Count < total; s++)
            {
                plan.Add(new WavePlanEntry
                {
                    Variant = RandHelper.PickWeighted(weights.Select(x => (x.v, x.w)).ToList()),
                    Time = t + s * MathH.Rand(0.06f, 0.16f),
                    Lane = lane + MathH.Rand(-0.12f, 0.12f)
                });
            }

            t += MathF.Max(0.28f, 1.26f - level * 0.05f) + MathH.Rand(0.06f, 0.72f);
        }

        plan.Sort((a, b) => a.Time.CompareTo(b.Time));
        return plan;
    }

    public static void StartWave(GameState s, float delay)
    {
        s.UFOs.Clear();
        s.Raiders.Clear();
        s.Enemies.Clear();
        s.PlayerMissiles.Clear();
        s.Explosions.Clear();
        s.Sparks.Clear();
        s.SmokeParts.Clear();
        s.Trails.Clear();
        s.DebrisParts.Clear();
        s.Shockwaves.Clear();
        s.LightBursts.Clear();

        int waveBaseAmmo = Math.Min(155, (int)MathF.Round(20 + s.Level * 2.7f + MathF.Max(0, s.Level - 14) * 1.35f));
        foreach (var b in s.Bases)
        {
            if (b.Destroyed && RandHelper.Next01() < 0.33f) b.Destroyed = false;
            b.Ammo = b.Destroyed ? 0 : waveBaseAmmo;
            b.MaxAmmo = waveBaseAmmo;
            b.Cooldown = 0;
        }

        float oldPhalanxMax = MathF.Min(1300, MathF.Round((620 + s.Level * 90) * (1 + (s.Upgrades.PhalanxEff - 1) * 0.55f)));
        int perUnitMax = (int)MathF.Max(340, MathF.Round(oldPhalanxMax * 0.62f));
        foreach (var p in s.Phalanxes)
        {
            if (p.Destroyed && RandHelper.Next01() < 0.4f) p.Destroyed = false;
            p.Ammo = p.Destroyed ? 0 : perUnitMax;
            p.MaxAmmo = perUnitMax;
            p.Cool = 0;
            p.Heat = 0;
            p.FireAcc = 0;
            p.Target = null;
        }

        if (s.HellRaiser != null)
        {
            var hr = s.HellRaiser;
            if (hr.Destroyed && RandHelper.Next01() < 0.58f) hr.Destroyed = false;
            hr.MaxAmmo = hr.Destroyed ? 0 : Math.Min(1100, (int)MathF.Round(460 + s.Level * 70));
            hr.Ammo = hr.MaxAmmo;
            hr.State = hr.Destroyed ? "destroyed" : "hidden";
            hr.Lift = hr.Destroyed ? 0.45f : 0;
            hr.DoorOpen = hr.Destroyed ? 0.5f : 0;
            hr.FireCd = 0;
            hr.Cool = hr.Destroyed ? 0 : 0.95f;
        }

        if (s.Level > 1) s.Emp = (int)MathH.Clamp(s.Emp + 1, 0, s.EmpMax);

        s.WavePlan = BuildPlan(s.Level);
        s.WavePause = delay;
        s.WaveTime = 0;
        s.SpawnI = 0;
        s.UfoQuota = s.Level >= 2 ? Math.Min(3, 1 + (s.Level - 2) / 2) : 0;
        s.NextUfo = delay + MathH.Rand(5.8f, 10.6f);
        s.RaiderQuota = s.Level >= 4 ? Math.Min(2 + s.Level / 6, 4) : 0;
        s.NextRaider = delay + MathH.Rand(4.4f, 8.2f);

        s.Note = $"Wave {s.Level} incoming | {s.Weather.Mode.ToUpperInvariant()} FRONT";
        s.NoteT = 2.1f;

        WeatherSystem.SetWaveWeather(s);
    }

    public static TargetInfo? ChooseTarget(GameState s, string variant)
    {
        var candidates = new List<(TargetInfo value, float weight)>();

        foreach (var city in s.Cities)
        {
            if (city.Destroyed) continue;
            int neigh = s.Cities.Count(x => !x.Destroyed && MathF.Abs(x.X - city.X) < s.W * 0.14f);
            float w = 95 + neigh * 22 + MathH.Rand(0, 18);
            if (variant == "heavy") w += 44;
            if (variant == "split") w += 26;
            if (variant == "ufoBomb") w += 60;
            if (variant == "carrier") w += 28;
            if (variant == "drone") w += 14;
            if (variant == "hell") w += 64;
            if (variant == "spit") w += 22;
            candidates.Add((new TargetInfo
            {
                Type = "city",
                X = city.X + MathH.Rand(-city.W * 0.24f, city.W * 0.24f),
                Y = s.GroundY - 30,
                Id = city.Id
            }, w));
        }

        foreach (var b in s.Bases)
        {
            if (b.Destroyed) continue;
            float w = 72 + b.Ammo * 2.6f + MathH.Rand(0, 16);
            if (variant == "fast") w += 28;
            if (variant == "zig") w += 14;
            if (variant == "ufoBomb") w -= 14;
            if (variant == "cruise") w += 56;
            if (variant == "carrier") w += 18;
            if (variant == "drone") w += 26;
            if (variant == "hell") w += 42;
            if (variant == "spit") w += 14;
            candidates.Add((new TargetInfo
            {
                Type = "base",
                X = b.X,
                Y = s.GroundY - 14,
                Id = b.Id
            }, w));
        }

        foreach (var p in s.Phalanxes)
        {
            if (p.Destroyed) continue;
            float w = 28 + p.Ammo * 0.012f + MathH.Rand(0, 8);
            if (variant is "fast" or "heavy") w += 10;
            if (variant == "drone") w += 14;
            if (variant == "cruise") w += 13;
            if (variant == "hell") w += 10;
            candidates.Add((new TargetInfo
            {
                Type = "phalanx",
                X = p.X,
                Y = s.GroundY - 18,
                Id = p.Id
            }, w));
        }

        if (candidates.Count == 0) return null;
        return RandHelper.PickWeighted(candidates);
    }

    public static void SpawnEnemy(GameState s, WavePlanEntry e)
    {
        var t = ChooseTarget(s, e.Variant);
        if (t == null) { s.GameOver = true; return; }

        bool cruise = e.Variant == "cruise";
        bool carrier = e.Variant == "carrier";
        float sx = cruise
            ? (RandHelper.Next01() < 0.5f ? -70 : s.W + 70)
            : MathH.Clamp(s.W * 0.5f + e.Lane * s.W * 0.44f + MathH.Rand(-140, 140), 14, s.W - 14);
        float sy = cruise
            ? MathH.Rand(s.HorizonY * 0.66f, s.GroundY * 0.52f)
            : carrier ? MathH.Rand(-220, -120) : MathH.Rand(-160, -40);

        Combat.CreateEnemyProjectile(s, e.Variant, sx, sy, t.Value);
        SynthAudio.EnemyLaunch(MathH.Clamp(sx / s.W, 0, 1));
    }

    public static void SpawnUfo(GameState s)
    {
        int alive = s.Cities.Count(c => !c.Destroyed);
        if (alive == 0) return;

        bool left = RandHelper.Next01() < 0.5f;
        float baseY = MathH.Rand(s.HorizonY * 0.62f, s.HorizonY * 0.88f);
        float x = left ? -90 : s.W + 90;
        bool isBoss = s.Level >= 5 && RandHelper.Next01() < 0.2f + s.Level * 0.05f;
        float vxMult = isBoss ? 0.6f : 1f;
        float vx = left
            ? (MathH.Rand(58, 96) + s.Level * 3) * vxMult
            : -(MathH.Rand(58, 96) + s.Level * 3) * vxMult;

        s.UFOs.Add(new UFO
        {
            Id = s.NewId(),
            X = x,
            Y = baseY,
            Vx = vx,
            Speed = MathF.Abs(vx),
            BobPhase = RandHelper.Next01() * MathH.TAU,
            Boss = isBoss,
            Hp = isBoss ? 6 : 2,
            FireCd = MathH.Rand(1.25f, 2.35f)
        });

        s.Note = isBoss ? "WARNING: Boss UFO detected" : "UFO intruder detected";
        s.NoteT = 1.1f;
    }

    public static void SpawnRaider(GameState s)
    {
        bool left = RandHelper.Next01() < 0.5f;
        float x = left ? -95 : s.W + 95;
        float y = MathH.Rand(s.HorizonY * 0.14f, s.HorizonY * 0.34f);
        float dir = left ? 1 : -1;

        s.Raiders.Add(new Raider
        {
            Id = s.NewId(),
            X = x,
            Y = y,
            Vx = dir * MathH.Rand(150, 210),
            Speed = MathH.Rand(150, 210),
            FireCd = MathH.Rand(0.65f, 1.25f),
            Angle = 0,
            Hp = 5
        });

        s.Note = "Stratospheric Raider detected";
        s.NoteT = 0.95f;
    }
}
