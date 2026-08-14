using System.Diagnostics;
using MingSim.Domain.Events;

namespace MingSim.SoakTests;

/// <summary>
/// M7 纵切稳定第一批长跑测试（docs/设计蓝图/08 §20"MVP 长跑"、11 §10"性能预算"）。
/// 不依赖第三方测试框架，与 Ming.SmokeTests 相同的 Exe 验收形态，全部测试通过才返回 0：
/// - 90 日宁远急饷场景 × 20 固定种子确定性重放（同种子同 StateHash/事件流/终局）；
/// - 一年（365 日）合成世界长跑（无异常，输出每次推进 CPU 时间分布与内存量级摘要）。
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            NingyuanSeedReplay.RunAll();
            SyntheticYearLongRun.RunAll();
            Console.WriteLine("MingSim 长跑测试全部通过。");
            Console.WriteLine($"总耗时：{stopwatch.Elapsed.TotalSeconds:F1} 秒");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"长跑测试失败：{exception.Message}");
            Console.Error.WriteLine(exception.StackTrace);
            return 1;
        }
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>事件流指纹：与 SmokeTests 一致，覆盖身份/顺序/提交/因果/时刻/数据。</summary>
    internal static IReadOnlyList<string> EventFingerprints(IEnumerable<DomainEvent> events) =>
        events.Select(item => string.Join("", [
            item.EventId,
            item.EventSequence.ToString(),
            item.EventType,
            item.WorldVersion.ToString(),
            item.CommitId,
            item.CausalCommandId ?? "",
            item.OccurredAt?.UtcTicks.ToString() ?? "",
            string.Join("", item.Data.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"))])).ToArray();
}

/// <summary>每次推进墙钟（微秒）的分布摘要；只记录量级，不写死性能门槛（doc 11 §10）。</summary>
internal static class TimingSummary
{
    internal static string Format(IReadOnlyList<long> microseconds)
    {
        if (microseconds.Count == 0)
        {
            return "无样本";
        }

        var sorted = microseconds.OrderBy(value => value).ToArray();
        var p50 = sorted[(int)(0.50 * (sorted.Length - 1))];
        var p90 = sorted[(int)(0.90 * (sorted.Length - 1))];
        var p99 = sorted[(int)(0.99 * (sorted.Length - 1))];
        var mean = (long)(sorted.Average());

        (string Label, long Min)[] buckets =
        [
            ("<0.1ms", 0), ("0.1-0.5ms", 100), ("0.5-1ms", 500), ("1-5ms", 1_000),
            ("5-20ms", 5_000), ("20-100ms", 20_000), (">100ms", 100_000),
        ];
        var counts = new int[buckets.Length];
        foreach (var value in sorted)
        {
            var index = 0;
            while (index + 1 < buckets.Length && value >= buckets[index + 1].Min)
            {
                index++;
            }

            counts[index]++;
        }

        var histogram = string.Join(" ", buckets.Select((bucket, index) => $"{bucket.Label}={counts[index]}"));
        return $"n={sorted.Length} min={sorted[0] / 1000.0:F3}ms p50={p50 / 1000.0:F3}ms p90={p90 / 1000.0:F3}ms " +
               $"p99={p99 / 1000.0:F3}ms max={sorted[^1] / 1000.0:F3}ms mean={mean / 1000.0:F3}ms [{histogram}]";
    }
}
