namespace MissileCommandOverdrive;

public enum EventKind
{
    Kill,
    CityDestroyed,
    BaseDestroyed,
    WaveStart,
    WaveCleared,
    Emp,
    GroundImpact
}

public struct GameEvent
{
    public EventKind Kind;
    public float X, Y;
    public float Magnitude;
}

/// <summary>Fixed-capacity event ring. Systems emit during update; Program.cs drains
/// after GameUpdate.UpdateAll and clears every frame. Zero per-frame heap allocation.</summary>
public sealed class EventRing
{
    public const int Capacity = 256; // power of two — indices are masked
    public const int KindCount = (int)EventKind.GroundImpact + 1;

    readonly GameEvent[] _buf = new GameEvent[Capacity];
    public int Head;
    public int Count;

    public void Emit(EventKind kind, float x, float y, float magnitude = 0f)
    {
        int i = (Head + Count) & (Capacity - 1);
        _buf[i].Kind = kind;
        _buf[i].X = x;
        _buf[i].Y = y;
        _buf[i].Magnitude = magnitude;
        if (Count < Capacity) Count++;
        else Head = (Head + 1) & (Capacity - 1); // full: overwrite oldest
    }

    public ref readonly GameEvent At(int index) => ref _buf[(Head + index) & (Capacity - 1)];

    public void Clear()
    {
        Head = 0;
        Count = 0;
    }
}
