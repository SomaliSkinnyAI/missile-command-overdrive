namespace MissileCommandOverdrive.Util;

public static class RandHelper
{
    private static readonly Random _rng = new();

    public static float Next(float min, float max)
        => min + _rng.NextSingle() * (max - min);

    public static float Next01() => _rng.NextSingle();

    public static int NextInt(int min, int maxExclusive)
        => _rng.Next(min, maxExclusive);

    public static bool Chance(float probability)
        => _rng.NextSingle() < probability;

    public static T Pick<T>(IList<T> list)
        => list[_rng.Next(list.Count)];

    /// <summary>Weighted random pick. Each item has (value, weight).</summary>
    public static T PickWeighted<T>(IList<(T Value, float Weight)> items)
    {
        float sum = 0f;
        foreach (var item in items)
            if (item.Weight > 0) sum += item.Weight;
        if (sum <= 0f) return items[0].Value;
        float r = _rng.NextSingle() * sum;
        foreach (var item in items)
        {
            if (item.Weight <= 0) continue;
            r -= item.Weight;
            if (r <= 0) return item.Value;
        }
        return items[^1].Value;
    }
}
