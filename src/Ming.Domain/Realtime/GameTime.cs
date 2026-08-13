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
    public GameTime(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("GameTime 必须使用 UTC+00:00。", nameof(value));
        }

        Value = value.ToUniversalTime();
    }

    public GameTime(DateTime value)
        : this(value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(value)
            : throw new ArgumentException("GameTime 的 DateTime 必须明确标记为 UTC。", nameof(value)))
    {
    }

    public DateTimeOffset Value { get; }

    public DateTimeOffset Date => new(Value.Date, TimeSpan.Zero);

    public GameTime Add(TimeSpan elapsed) => new(Value.Add(elapsed));

    public int CompareTo(GameTime other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("O");

    public static bool operator <(GameTime left, GameTime right) => left.Value < right.Value;

    public static bool operator <=(GameTime left, GameTime right) => left.Value <= right.Value;

    public static bool operator >(GameTime left, GameTime right) => left.Value > right.Value;

    public static bool operator >=(GameTime left, GameTime right) => left.Value >= right.Value;
}
