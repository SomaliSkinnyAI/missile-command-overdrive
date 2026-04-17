using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Entities;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

/// <summary>Rolling 8-char secret-code buffer for easter eggs (e.g. "666" → summon demon).</summary>
public static class SecretCode
{
    public static string Buffer = "";
}

/// <summary>Demon easter egg — summoned by typing "666". Drifts along the horizon, emits periodic hell-fire explosions and flashes.</summary>
public static class DemonSystem
{
    public static void Summon(GameState s)
    {
        if (s.Demon != null) return;
        if (s.Intro || s.GameOver) return;

        s.Demon = new Daemon
        {
            X = s.W * 0.5f,
            Y = s.HorizonY * 0.22f,
            Vx = 160f,
            Vy = 0f,
            Life = 18f,
            FireCd = 0.7f,
            Phase = MathH.Rand(0f, MathH.TAU),
            Hp = 6,
            Active = true
        };

        s.Note = "EASTER EGG: DAEMON UNLEASHED";
        s.NoteT = 2.0f;
        s.Flash = MathF.Max(s.Flash, 0.28f);
        s.Shake = MathF.Max(s.Shake, 11);
        SynthAudio.Thunder(0.5f, 1.0f);
    }

    public static void Update(GameState s, float dt)
    {
        var d = s.Demon;
        if (d == null || !d.Active) return;

        d.Life -= dt;
        if (d.Life <= 0 || d.Hp <= 0)
        {
            s.Demon = null;
            s.Note = "Daemon banished";
            s.NoteT = 1.2f;
            return;
        }

        d.X += d.Vx * dt;
        d.Y = s.HorizonY * 0.2f + MathF.Sin(s.Time * 1.9f + d.Phase) * 30f;
        if (d.X < 70 || d.X > s.W - 70) d.Vx *= -1f;

        d.FireCd -= dt;
        if (d.FireCd <= 0)
        {
            d.FireCd = MathH.Rand(0.9f, 1.6f);
            // Periodic hell blasts near cities/bases (picks random ground-level spot near demon)
            float tx = d.X + MathH.Rand(-s.W * 0.35f, s.W * 0.35f);
            tx = MathH.Clamp(tx, 60, s.W - 60);
            float ty = s.GroundY - MathH.Rand(0, 30);
            Combat.SpawnExpl(s, tx, ty,
                maxRadius: MathH.Rand(72, 110),
                life: 1.1f,
                shakeTime: 0.42f,
                player: false,
                flash: 0.08f,
                heavy: true);
            s.Flash = MathF.Max(s.Flash, 0.08f);
            SynthAudio.Thunder(MathH.Clamp(d.X / s.W, 0, 1), 0.55f);
        }

        // Atmospheric havoc flashes around the demon itself (self-flicker, no shake)
        d.Phase += dt * 0.5f;
    }
}
