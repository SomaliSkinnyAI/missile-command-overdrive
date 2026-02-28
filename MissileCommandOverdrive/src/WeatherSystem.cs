using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Weather system: storm/ash/clear with particles, fog bands, lightning.</summary>
public static class WeatherSystem
{
    public static void SetWaveWeather(GameState s)
    {
        float roll = RandHelper.Next01() + s.Level * 0.055f;
        string mode;
        if (roll > 1.78f) mode = "storm";
        else if (roll > 1.28f) mode = "ash";
        else mode = "clear";

        float baseInt = mode == "clear" ? 0.06f : mode == "ash" ? 0.3f : 0.44f;
        s.Weather.Mode = mode;
        s.Weather.Intensity = MathH.Clamp(baseInt + s.Level * 0.026f + MathH.Rand(-0.08f, 0.08f), 0.04f, 0.92f);
        s.Weather.Wind = MathH.Rand(-85, 85) * (0.55f + s.Weather.Intensity * 0.75f);
        s.Weather.LightningTimer = MathH.Rand(3.2f, 6.4f);
        s.Weather.ThunderCd = 0;

        BuildWeather(s);
    }

    public static void BuildWeather(GameState s)
    {
        var w = s.Weather;
        w.Particles.Clear();
        w.FogBands.Clear();

        string mode = w.Mode ?? "clear";
        float inten = MathH.Clamp(w.Intensity, 0, 1);
        bool isRain = mode == "storm";
        int count = mode == "storm" ? (int)(150 + inten * 320)
            : mode == "ash" ? (int)(90 + inten * 180) : 0;

        for (int i = 0; i < count; i++)
        {
            w.Particles.Add(new WeatherParticle
            {
                X = RandHelper.Next01() * s.W,
                Y = RandHelper.Next01() * s.H,
                Z = MathH.Rand(0.5f, 1.4f),
                Alpha = MathH.Rand(0.18f, 0.7f),
                Len = isRain ? MathH.Rand(12, 34) : MathH.Rand(1.2f, 4.2f),
                Vx = w.Wind * MathH.Rand(0.25f, 0.9f),
                Vy = isRain ? MathH.Rand(360, 760) : MathH.Rand(26, 84),
                Hue = mode == "ash" ? MathH.Rand(22, 42) : MathH.Rand(185, 205)
            });
        }

        int fogCount = mode == "clear" ? 1 : mode == "ash" ? 4 : 5;
        for (int i = 0; i < fogCount; i++)
        {
            w.FogBands.Add(new FogBand
            {
                Y = MathH.Rand(s.HorizonY - 40, s.GroundY + 90),
                Thickness = MathH.Rand(50, 140),
                Alpha = (mode == "clear" ? MathH.Rand(0.018f, 0.045f) : MathH.Rand(0.03f, 0.15f))
                    * (0.5f + inten),
                Speed = MathH.Rand(0.02f, 0.16f),
                Phase = RandHelper.Next01() * MathH.TAU
            });
        }
    }

    public static void Update(GameState s, float dt)
    {
        var w = s.Weather;
        if (w.Mode == "clear" && w.Particles.Count == 0) return;

        // Update particles
        for (int i = 0; i < w.Particles.Count; i++)
        {
            var p = w.Particles[i];
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt * p.Z;

            // Wrap around screen
            if (p.Y > s.H + 10) { p.Y = -10; p.X = RandHelper.Next01() * s.W; }
            if (p.X < -30) p.X += s.W + 60;
            if (p.X > s.W + 30) p.X -= s.W + 60;

            w.Particles[i] = p;
        }

        // Lightning
        if (w.Mode == "storm")
        {
            w.LightningTimer -= dt;
            if (w.LightningTimer <= 0)
            {
                w.LightningTimer = MathH.Rand(2.5f, 8f) / MathF.Max(0.3f, w.Intensity);
                float boltX = MathH.Rand(s.W * 0.1f, s.W * 0.9f);
                float boltY0 = MathH.Rand(20, s.HorizonY * 0.3f);
                float boltY1 = MathH.Rand(s.HorizonY * 0.5f, s.GroundY - 40);
                float boltLife = MathH.Rand(0.12f, 0.28f);
                int branches = 2 + RandHelper.NextInt(0, 3);
                var segments = GenBoltSegments(boltX, boltY0, boltY1, branches);
                w.Bolts.Add(new LightningBolt
                {
                    X = boltX,
                    Y0 = boltY0,
                    Y1 = boltY1,
                    Life = boltLife,
                    MaxLife = boltLife,
                    Bright = MathH.Rand(0.7f, 1f),
                    Branches = branches,
                    Segments = segments
                });
                s.Flash = MathF.Max(s.Flash, 0.08f + w.Intensity * 0.12f);
                s.Shake = MathF.Max(s.Shake, 3 + w.Intensity * 5);
                SynthAudio.Thunder(MathH.Clamp(w.Bolts[^1].X / s.W, 0, 1), w.Intensity);
            }

            // Update bolts
            for (int i = w.Bolts.Count - 1; i >= 0; i--)
            {
                var b = w.Bolts[i];
                b.Life -= dt;
                if (b.Life <= 0) w.Bolts.RemoveAt(i);
                else w.Bolts[i] = b;
            }
        }
    }

    /// <summary>Pre-generate bolt segments with branches, matching HTML bolt geometry.</summary>
    static List<LightningSegment> GenBoltSegments(float x, float y0, float y1, int branchCount)
    {
        var segs = new List<LightningSegment>();
        int trunkSegs = 8;
        float segH = (y1 - y0) / trunkSegs;
        float px = x, py = y0;

        for (int i = 1; i <= trunkSegs; i++)
        {
            float nx = x + MathH.Rand(-36, 36);
            float ny = y0 + i * segH;
            segs.Add(new LightningSegment { X1 = px, Y1 = py, X2 = nx, Y2 = ny, Branch = false });

            // Branch from some trunk points
            if (i < trunkSegs - 1 && branchCount > 0 && RandHelper.Next01() < 0.45f)
            {
                branchCount--;
                float bx = nx + MathH.Rand(-50, 50);
                float by = ny + segH * MathH.Rand(0.4f, 1f);
                segs.Add(new LightningSegment { X1 = nx, Y1 = ny, X2 = bx, Y2 = by, Branch = true });
                // Sub-branch
                if (RandHelper.Next01() < 0.35f)
                {
                    float bx2 = bx + MathH.Rand(-35, 35);
                    float by2 = by + segH * MathH.Rand(0.3f, 0.7f);
                    segs.Add(new LightningSegment { X1 = bx, Y1 = by, X2 = bx2, Y2 = by2, Branch = true });
                }
            }

            px = nx; py = ny;
        }
        return segs;
    }
}
