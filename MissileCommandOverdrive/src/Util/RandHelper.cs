namespace MissileCommandOverdrive.Util;

/// <summary>Cosmetic-stream RNG façade (§4.3). Signatures unchanged from the old
/// Random-backed helper, now routed to the free-running xoshiro cosmetic stream
/// on GameState (bound once at boot). Wave-plan draws never come through here —
/// they use the explicit `ref Xoshiro` overload with the per-wave plan stream.</summary>
public static class RandHelper
{
    static GameState? _bound;
    // Pre-bind fallback so an early (static-init) caller can never null-ref.
    static Xoshiro _fallback = new(unchecked((ulong)DateTime.UtcNow.Ticks));

    public static void Bind(GameState s) => _bound = s;

    static ref Xoshiro C
    {
        get
        {
            var s = _bound;
            if (s != null) return ref s.Cosmetic;
            return ref _fallback;
        }
    }

    public static float Next(float min, float max)
        => min + C.NextSingle() * (max - min);

    public static float Next01() => C.NextSingle();

    public static int NextInt(int min, int maxExclusive)
        => C.Next(min, maxExclusive);

    public static bool Chance(float probability)
        => C.NextSingle() < probability;

    public static T Pick<T>(IList<T> list)
        => list[C.Next(list.Count)];

    /// <summary>Weighted random pick from the cosmetic stream.</summary>
    public static T PickWeighted<T>(IList<(T Value, float Weight)> items)
        => PickWeighted(items, ref C);

    /// <summary>Weighted random pick from an explicit stream (per-wave plan draws, §4.3).</summary>
    public static T PickWeighted<T>(IList<(T Value, float Weight)> items, ref Xoshiro rng)
    {
        float sum = 0f;
        foreach (var item in items)
            if (item.Weight > 0) sum += item.Weight;
        if (sum <= 0f) return items[0].Value;
        float r = rng.NextSingle() * sum;
        foreach (var item in items)
        {
            if (item.Weight <= 0) continue;
            r -= item.Weight;
            if (r <= 0) return item.Value;
        }
        return items[^1].Value;
    }
}
