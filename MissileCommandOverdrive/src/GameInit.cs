using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

public static class GameInit
{
    public static void BuildWorld(GameState s)
    {
        int bases = 3, cities = 6;
        float usable = s.W * 0.86f;
        float left = (s.W - usable) / 2f;
        float cityStep = cities > 1 ? usable / (cities - 1) : 0f;
        float baseStep = usable / (bases * 2);

        s.Bases.Clear();
        s.Cities.Clear();

        for (int i = 0; i < bases; i++)
        {
            float x = left + (i * 2 + 1) * baseStep;
            s.Bases.Add(new Base
            {
                Id = $"B{i + 1}",
                X = x,
                Y = s.GroundY,
                Ammo = 18,
                MaxAmmo = 18,
                Cooldown = 0,
                Destroyed = false
            });
        }

        for (int i = 0; i < cities; i++)
        {
            float x = left + i * cityStep;
            float w = MathH.Rand(MathF.Max(46, cityStep * 0.34f), MathF.Max(72, cityStep * 0.5f));
            s.Cities.Add(new City
            {
                Id = $"C{i + 1}",
                X = x,
                Y = s.GroundY,
                W = w,
                Destroyed = false
            });
        }

        // Reposition outer launchers between outer city pairs
        if (s.Cities.Count >= 6 && s.Bases.Count >= 3)
        {
            s.Bases[0].X = (s.Cities[0].X + s.Cities[1].X) * 0.5f;
            s.Bases[2].X = (s.Cities[4].X + s.Cities[5].X) * 0.5f;
        }

        float midX = s.Bases.Count > 1 ? s.Bases[s.Bases.Count / 2].X : s.W * 0.5f;
        float leftAnchor = s.Cities.Count > 2
            ? (s.Cities[1].X + s.Cities[2].X) * 0.5f
            : (s.Bases[0].X + midX) * 0.5f + 2f;
        float rightAnchor = s.Cities.Count > 4
            ? (s.Cities[3].X + s.Cities[4].X) * 0.5f
            : (midX + (s.Bases.Count > 2 ? s.Bases[2].X : s.W * 0.82f)) * 0.5f - 2f;

        s.Phalanxes.Clear();
        s.Phalanxes.Add(MakePhalanx("PHALANX_L", MathH.Clamp(leftAnchor, 26, s.W - 26), s));
        s.Phalanxes.Add(MakePhalanx("PHALANX_R", MathH.Clamp(rightAnchor, 26, s.W - 26), s));

        s.HellRaiser = new HellRaiser
        {
            X = s.W * 0.5f,
            Y = s.GroundY + MathF.Max(18, (s.H - s.GroundY) * 0.22f),
            State = "hidden",
            Ammo = 0,
            MaxAmmo = 0,
            Destroyed = false
        };

        // Clouds (7 wispy ambient layers)
        s.Clouds.Clear();
        for (int i = 0; i < 7; i++)
        {
            s.Clouds.Add(new float[]
            {
                MathH.Rand(-s.W * 0.1f, s.W * 1.1f), // x
                MathH.Rand(s.H * 0.1f, s.HorizonY * 0.86f), // y
                MathH.Rand(s.W * 0.16f, s.W * 0.34f), // w
                MathH.Rand(s.H * 0.06f, s.H * 0.12f), // h
                MathH.Rand(0.05f, 0.14f), // alpha
                MathH.Rand(8f, 24f), // speed
                MathH.Rand(0, MathF.PI * 2) // phase
            });
        }

        // Nebula blobs (9 radial gradient circles in the sky)
        s.Nebula.Clear();
        for (int i = 0; i < 9; i++)
        {
            s.Nebula.Add(new float[]
            {
                MathH.Rand(s.W * 0.05f, s.W * 0.95f), // x
                MathH.Rand(s.H * 0.05f, s.H * 0.54f), // y
                MathH.Rand(s.W * 0.11f, s.W * 0.23f), // radius
                Random.Shared.NextSingle() < 0.5f ? 205 : 242, // hue1
                Random.Shared.NextSingle() < 0.5f ? 272 : 188, // hue2
                MathH.Rand(0.06f, 0.16f), // alpha
                MathH.Rand(0.05f, 0.18f), // drift speed
                MathH.Rand(0, MathF.PI * 2) // phase
            });
        }

        // Aurora bands (3 wavy horizontal bands)
        s.Aurora.Clear();
        for (int i = 0; i < 3; i++)
        {
            s.Aurora.Add(new float[]
            {
                s.HorizonY * (0.35f + i * 0.11f), // y
                MathH.Rand(22, 58), // amplitude
                MathH.Rand(14, 34), // thickness
                MathH.Rand(0.14f, 0.4f), // speed
                MathH.Rand(0, MathF.PI * 2), // phase
                Random.Shared.NextSingle() < 0.5f ? 166 : 198, // hue
                MathH.Rand(0.09f, 0.2f) // alpha
            });
        }
    }

    static Phalanx MakePhalanx(string id, float x, GameState s) => new()
    {
        Id = id,
        X = x,
        Y = s.GroundY,
        AimAng = -MathF.PI * 0.5f,
        AimX = x,
        AimY = s.GroundY - 180,
        AimErr = MathF.PI
    };

    /// <summary>Reposition all defense objects to match the current screen dimensions,
    /// preserving game state (ammo, destroyed, etc). Also rebuilds sky scenery.
    /// Called on window resize, matching HTML's resize() → buildWorld() + buildSky().</summary>
    public static void Reposition(GameState s)
    {
        int bases = 3, cities = 6;
        float usable = s.W * 0.86f;
        float left = (s.W - usable) / 2f;
        float cityStep = cities > 1 ? usable / (cities - 1) : 0f;
        float baseStep = usable / (bases * 2);

        // Reposition bases (preserve state)
        for (int i = 0; i < s.Bases.Count && i < bases; i++)
        {
            s.Bases[i].X = left + (i * 2 + 1) * baseStep;
            s.Bases[i].Y = s.GroundY;
        }

        // Reposition cities (preserve state, keep W)
        for (int i = 0; i < s.Cities.Count && i < cities; i++)
        {
            s.Cities[i].X = left + i * cityStep;
            s.Cities[i].Y = s.GroundY;
        }

        // Outer launcher repositioning
        if (s.Cities.Count >= 6 && s.Bases.Count >= 3)
        {
            s.Bases[0].X = (s.Cities[0].X + s.Cities[1].X) * 0.5f;
            s.Bases[2].X = (s.Cities[4].X + s.Cities[5].X) * 0.5f;
        }

        // Phalanxes
        float midX = s.Bases.Count > 1 ? s.Bases[s.Bases.Count / 2].X : s.W * 0.5f;
        float leftAnchor = s.Cities.Count > 2
            ? (s.Cities[1].X + s.Cities[2].X) * 0.5f
            : (s.Bases[0].X + midX) * 0.5f + 2f;
        float rightAnchor = s.Cities.Count > 4
            ? (s.Cities[3].X + s.Cities[4].X) * 0.5f
            : (midX + (s.Bases.Count > 2 ? s.Bases[2].X : s.W * 0.82f)) * 0.5f - 2f;

        if (s.Phalanxes.Count >= 1)
        {
            s.Phalanxes[0].X = MathH.Clamp(leftAnchor, 26, s.W - 26);
            s.Phalanxes[0].Y = s.GroundY;
        }
        if (s.Phalanxes.Count >= 2)
        {
            s.Phalanxes[1].X = MathH.Clamp(rightAnchor, 26, s.W - 26);
            s.Phalanxes[1].Y = s.GroundY;
        }

        // HellRaiser
        if (s.HellRaiser != null)
        {
            s.HellRaiser.X = s.W * 0.5f;
            s.HellRaiser.Y = s.GroundY + MathF.Max(18, (s.H - s.GroundY) * 0.22f);
        }

        // Rebuild sky scenery (clouds, nebula, aurora depend on W/H/HorizonY)
        RebuildScenery(s);
    }

    /// <summary>Rebuild clouds, nebula, aurora for the current screen dimensions.</summary>
    static void RebuildScenery(GameState s)
    {
        s.Clouds.Clear();
        for (int i = 0; i < 7; i++)
        {
            s.Clouds.Add(new float[]
            {
                MathH.Rand(-s.W * 0.1f, s.W * 1.1f),
                MathH.Rand(s.H * 0.1f, s.HorizonY * 0.86f),
                MathH.Rand(s.W * 0.16f, s.W * 0.34f),
                MathH.Rand(s.H * 0.06f, s.H * 0.12f),
                MathH.Rand(0.05f, 0.14f),
                MathH.Rand(8f, 24f),
                MathH.Rand(0, MathF.PI * 2)
            });
        }

        s.Nebula.Clear();
        for (int i = 0; i < 9; i++)
        {
            s.Nebula.Add(new float[]
            {
                MathH.Rand(s.W * 0.05f, s.W * 0.95f),
                MathH.Rand(s.H * 0.05f, s.H * 0.54f),
                MathH.Rand(s.W * 0.11f, s.W * 0.23f),
                Random.Shared.NextSingle() < 0.5f ? 205 : 242,
                Random.Shared.NextSingle() < 0.5f ? 272 : 188,
                MathH.Rand(0.06f, 0.16f),
                MathH.Rand(0.05f, 0.18f),
                MathH.Rand(0, MathF.PI * 2)
            });
        }

        s.Aurora.Clear();
        for (int i = 0; i < 3; i++)
        {
            s.Aurora.Add(new float[]
            {
                s.HorizonY * (0.35f + i * 0.11f),
                MathH.Rand(22, 58),
                MathH.Rand(14, 34),
                MathH.Rand(0.14f, 0.4f),
                MathH.Rand(0, MathF.PI * 2),
                Random.Shared.NextSingle() < 0.5f ? 166 : 198,
                MathH.Rand(0.09f, 0.2f)
            });
        }

        // Rebuild weather particles for new dimensions
        WeatherSystem.BuildWeather(s);
    }

    public static void ResetGame(GameState s)
    {
        bool dbgEnabled = s.Debug.Enabled;
        s.Level = 1;
        s.Score = 0;
        s.Combo = 0;
        s.MaxCombo = 0;
        s.ComboTimer = 0;
        s.GameOver = false;
        s.GameOverSfx = false;
        s.Intro = false;
        s.Time = 0;
        s.Danger = 0;

        s.UFOs.Clear();
        s.Raiders.Clear();
        s.Demon = null;
        s.Enemies.Clear();
        s.PlayerMissiles.Clear();
        s.Explosions.Clear();
        s.Sparks.Clear();
        s.SmokeParts.Clear();
        s.Trails.Clear();
        s.DebrisParts.Clear();
        s.Shockwaves.Clear();
        s.LightBursts.Clear();
        s.MuzzleFlashes.Clear();
        s.Scorches.Clear();
        s.ShootingStars.Clear();
        s.FloatingTexts.Clear();
        s.Chromatic = 0;
        s.Emp = 1;
        s.EmpCd = 0;
        s.Shake = 0;
        s.Flash = 0;
        s.Shop = false;
        s.ShopTimer = 0;
        s.Upgrades = new Upgrades();
        s.SelectedBase = null;

        s.Weather.Mode = "clear";
        s.Weather.Intensity = 0.15f;
        s.Weather.Wind = MathH.Rand(-40, 40);
        s.Weather.Bolts.Clear();

        s.GameOverTime = 0;
        s.Debug = new DebugState { Enabled = dbgEnabled };

        BuildWorld(s);
        WaveSystem.StartWave(s, 2.5f);
    }
}
