using System.Numerics;

namespace MissileCommandOverdrive.Util;

/// <summary>xoshiro256** PRNG (§4.3). STRUCT — a copy silently forks the stream,
/// so instances live as fields and are passed with `ref` only.</summary>
public struct Xoshiro
{
    ulong _s0, _s1, _s2, _s3;

    public Xoshiro(ulong seed)
    {
        // SplitMix64 expansion per the xoshiro reference — never yields the
        // degenerate all-zero state, even for seed 0.
        ulong x = seed;
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    static ulong SplitMix64(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextULong()
    {
        ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform [0,1) — mirrors Random.NextSingle().</summary>
    public float NextSingle() => (NextULong() >> 40) * (1f / (1 << 24));

    /// <summary>Uniform [0, maxExclusive) — mirrors Random.Next(int).</summary>
    public int Next(int maxExclusive)
        => maxExclusive <= 0 ? 0 : (int)(NextULong() % (ulong)maxExclusive);

    public int Next(int min, int maxExclusive) => min + Next(maxExclusive - min);

    public float NextFloat(float a, float b) => a + NextSingle() * (b - a);
}

/// <summary>Seed derivation/display helpers (§5 3.2).</summary>
public static class SeedUtil
{
    /// <summary>FNV-1a 64 — stable across processes and machines
    /// (string.GetHashCode is per-process randomized; never use it for seeds).</summary>
    public static ulong Fnv1a64(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }

    /// <summary>Daily seed: FNV-1a64 of the UTC yyyy-MM-dd string — identical on
    /// every machine for the same date.</summary>
    public static ulong DailySeed()
        => Fnv1a64(DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>"XXXX-XXXX" display form: 32-bit xor-fold of the master seed.
    /// Event-driven only (allocates).</summary>
    public static string Format(ulong seed)
    {
        uint f = (uint)(seed ^ (seed >> 32));
        return $"{f >> 16:X4}-{f & 0xFFFF:X4}";
    }
}
