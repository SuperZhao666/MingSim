namespace MingSim.Domain.Realtime;

/// <summary>
/// 实时推演使用的时间起点。
/// </summary>
/// <remarks>
/// 这里不直接把“第 1 回合”当成时间，因为实时世界需要日期和时刻。
/// 第一版把第 1 回合映射到 1627-01-01 00:00；以后剧本可以提供自己的起点。
/// </remarks>
public static class SimulationEpoch
{
    public static DateTime DefaultForTurn(int turnNumber)
    {
        if (turnNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(turnNumber), "回合号必须从 1 开始。");
        }

        return new DateTime(1627, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddDays(turnNumber - 1);
    }
}
