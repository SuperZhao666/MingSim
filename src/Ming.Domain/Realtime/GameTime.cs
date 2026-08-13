namespace MingSim.Domain.Realtime;

/// <summary>
/// 仿真内核使用的 UTC 游戏时间。
/// </summary>
/// <remarks>
/// UI 帧时间和现实钟表都不能成为规则输入。把游戏时间包成值对象，
/// 可以让运行时明确要求“推进到哪个权威时刻”，而不是接收任意帧增量。
/// </remarks>
public readonly record struct GameTime : IComparable<GameTime>
{
    public GameTime(DateTime value)
    {
        Value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    public DateTime Value { get; }

    public DateTime Date => Value.Date;

    public GameTime Add(TimeSpan elapsed) => new(Value.Add(elapsed));

    public int CompareTo(GameTime other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("O");

    public static bool operator <(GameTime left, GameTime right) => left.Value < right.Value;

    public static bool operator <=(GameTime left, GameTime right) => left.Value <= right.Value;

    public static bool operator >(GameTime left, GameTime right) => left.Value > right.Value;

    public static bool operator >=(GameTime left, GameTime right) => left.Value >= right.Value;
}
