namespace MingSim.Domain.Military;

/// <summary>
/// 前线战备（P0 最小版）：只做"粮饷压力对士气"的最小抽象，绝不直接改兵力。
/// </summary>
/// <remarks>
/// 所有数值都是 DESIGN（doc 03 §7.1 场景控制表），不是 1629 年史实测量：
/// 初始 60/100；缺粮、欠饷逐日下降；到粮后缓慢恢复。
/// 用基点保存（10000 = 100%），避免权威模拟里出现 float/double；
/// <see cref="Value"/> 只是给 UI 和评估函数看的整数投影。
/// </remarks>
public sealed class ReadinessState
{
    public const int DesignInitialValueBasisPoints = 6_000; // DESIGN：初始 60/100
    public const int DesignFullDayGainBasisPoints = 10;     // DESIGN：足额供粮日 +0.1/日（缓慢恢复）
    public const int DesignShortDayLossBasisPoints = 100;   // DESIGN：有粮但不足的缺粮日 -1/日
    public const int DesignZeroDayLossBasisPoints = 200;    // DESIGN：完全断粮日 -2/日
    public const int DesignHardFailureBasisPoints = 2_500;  // DESIGN：战备低于 25/100 即硬失败（doc 03 §7.2）

    /// <summary>内容/测试初始化入口；运行中的数值只由 Simulation 的日耗规则改变。</summary>
    public ReadinessState(int valueBasisPoints = DesignInitialValueBasisPoints)
    {
        ValueBasisPoints = Math.Clamp(valueBasisPoints, 0, 10_000);
    }

    public int ValueBasisPoints { get; private set; }

    /// <summary>欠饷累计（石）：只在缺粮/断粮日增加，MVP 不做自动清偿，留给终局评估展示。</summary>
    public long ArrearsGrain { get; private set; }

    /// <summary>连续可用粮为 0 的天数：用于 doc 03 §7.2 "连续 7 日可用粮为 0" 硬失败判定。</summary>
    public int ConsecutiveZeroGrainDays { get; private set; }

    /// <summary>0..100 的整数战备视图。</summary>
    public int Value => ValueBasisPoints / 100;

    /// <summary>足额供粮日：战备缓慢恢复，断粮连续计数清零。</summary>
    internal void ApplyFullDay()
    {
        ValueBasisPoints = Math.Min(10_000, ValueBasisPoints + DesignFullDayGainBasisPoints);
        ConsecutiveZeroGrainDays = 0;
    }

    /// <summary>
    /// 减耗令生效日的足额供粮：战备恢复减半（+10→+5 基点/日，纸面推演 §3.2 减耗政策代价）。
    /// 增益由标准恢复常量派生（减半），不引入新的平衡常量。
    /// </summary>
    internal void ApplyReducedFullDay()
    {
        ValueBasisPoints = Math.Min(10_000, ValueBasisPoints + DesignFullDayGainBasisPoints / 2);
        ConsecutiveZeroGrainDays = 0;
    }

    /// <summary>缺粮日（0 &lt; 可用粮 &lt; 日需）：战备 -1，缺口计入欠饷。</summary>
    internal void ApplyShortDay(long shortfallGrain)
    {
        ValueBasisPoints = Math.Max(0, ValueBasisPoints - DesignShortDayLossBasisPoints);
        ArrearsGrain = checked(ArrearsGrain + shortfallGrain);
        ConsecutiveZeroGrainDays = 0;
    }

    /// <summary>完全断粮日（可用粮 = 0）：战备 -2，整日需求计入欠饷，连续断粮计数 +1。</summary>
    internal void ApplyZeroDay(long dailyDemand)
    {
        ValueBasisPoints = Math.Max(0, ValueBasisPoints - DesignZeroDayLossBasisPoints);
        ArrearsGrain = checked(ArrearsGrain + dailyDemand);
        ConsecutiveZeroGrainDays++;
    }

    internal ReadinessState Clone() => new(ValueBasisPoints)
    {
        ArrearsGrain = ArrearsGrain,
        ConsecutiveZeroGrainDays = ConsecutiveZeroGrainDays,
    };
}
