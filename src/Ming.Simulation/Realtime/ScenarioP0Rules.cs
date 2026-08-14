using MingSim.Domain;
using MingSim.Domain.Common;
using MingSim.Domain.Decrees;
using MingSim.Domain.Economy;
using MingSim.Domain.Military;
using MingSim.Domain.Scenario;
using MingSim.Simulation.Random;

namespace MingSim.Simulation.Realtime;

/// <summary>前线日耗结算结果，供 Runtime 生成审计事件。</summary>
public enum RationKind
{
    None,
    Full,
    Short,
    Zero,
}

/// <summary>一次前线日耗的摘要。</summary>
public readonly record struct DailyRationSummary(
    RationKind Kind,
    long ConsumedGrain,
    long AvailableBefore,
    long ShortfallGrain);

/// <summary>
/// 宁远急饷 P0 玩法规则的纯函数部分：前线日耗/战备、政令到期、风险样本抽取。
/// </summary>
/// <remarks>
/// 为什么单独成类：这些规则只做"世界状态 + 稳定种子 → 确定变化"，
/// 与调度/提交无关，便于逐条测试，也避免 <see cref="RealtimeSimulationRuntime" /> 继续膨胀。
/// 所有数值都是 DESIGN（doc 03 §7.1 场景控制表），不是史实测量。
/// </remarks>
internal static class ScenarioP0Rules
{
    // 固定风险样本只安排一次（doc 03 §4 P0 固定风险样本行），日期是 DESIGN 调参起点。
    public const int DesignWeatherDelayDay = 12;
    public const int DesignGrainRaidDay = 24;
    public const int DesignReportsDay = 30;

    public const string WeatherDelayEvent = "WeatherDelay";
    public const string GrainRaidEvent = "GrainRaid";
    public const string ScenarioReportsEvent = "ScenarioReports";

    // 风险样本的抽取区间全部是 DESIGN 调参起点（doc 03 §7.1 天气/袭粮"概率与损失先用固定种子场景校准"）。
    public const int DesignWeatherDelayMinDays = 1;      // 天气延误下限（日）
    public const int DesignWeatherDelayMaxExclusive = 4; // 天气延误上限（排他，即最多 3 日）
    public const int DesignRaidLossMaxEscortedExclusive = 6;   // 护卫批次袭粮上限 5%
    public const int DesignRaidLossMaxPlainExclusive = 21;     // 无护卫批次袭粮上限 20%
    public const int DesignReportCredibilityMin = 50;    // 报告可信度下限
    public const int DesignReportCredibilityMaxExclusive = 96; // 报告可信度上限（排他，即最多 95）
    public const int DesignReportStaleTrustThreshold = 40;     // 大臣信任低于该值时报告更陈旧
    public const int DesignReportStaleAgeMin = 5;              // 低信任：报告时效 5..10 日
    public const int DesignReportStaleAgeMaxExclusive = 11;
    public const int DesignReportFreshAgeMin = 1;              // 高信任：报告时效 1..4 日
    public const int DesignReportFreshAgeMaxExclusive = 5;

    /// <summary>前线日耗结算：先扣粮再判战备；返回摘要供 Runtime 写事件。</summary>
    public static DailyRationSummary ApplyDailyRation(WorldState state)
    {
        var scenario = state.Scenario;
        if (scenario.FrontStockpileId is not StockpileId frontId ||
            !state.Logistics.Stockpiles.TryGetValue(frontId, out var front))
        {
            // 场景规则关闭或前线粮仓缺失：本日没有日耗，也不动战备。
            return new DailyRationSummary(RationKind.None, 0, 0, 0);
        }

        var available = front.GrainQuantity;
        var demand = scenario.DailyGrainDemand;
        var consumed = Math.Min(available, demand);
        if (consumed > 0)
        {
            front.TryTakeGrain(consumed);
        }

        if (available >= demand)
        {
            state.Readiness.ApplyFullDay();
            return new DailyRationSummary(RationKind.Full, consumed, available, 0);
        }

        if (available == 0)
        {
            state.Readiness.ApplyZeroDay(demand);
            return new DailyRationSummary(RationKind.Zero, 0, 0, demand);
        }

        var shortfall = demand - available;
        state.Readiness.ApplyShortDay(shortfall);
        return new DailyRationSummary(RationKind.Short, consumed, available, shortfall);
    }

    /// <summary>政令期限检查：期限已到且未完成即甩责（政令作废、大臣信任 -5）。返回到期的政令供写事件。</summary>
    public static IReadOnlyList<DecreeState> ExpireOverdueDecrees(WorldState state)
    {
        var overdue = new List<DecreeState>();
        foreach (var decree in state.Decrees.Values)
        {
            if (decree.Status == DecreeStatus.Executing && decree.Deadline <= state.GameTime)
            {
                decree.Expire();
                state.Scenario.ChangeMinisterTrust(-ScenarioState.DesignShirkTrustPenalty);
                overdue.Add(decree);
            }
        }

        return overdue;
    }

    /// <summary>硬失败检测（doc 03 §7.2）：连续 7 日可用粮为 0，或战备低于 25。返回失败原因或 null。</summary>
    public static string? DetectHardFailure(WorldState state)
    {
        if (state.Readiness.ConsecutiveZeroGrainDays >= EndgameEvaluator.DesignHardFailureZeroDays)
        {
            return "连续7日可用粮为0";
        }

        return state.Readiness.ValueBasisPoints < ReadinessState.DesignHardFailureBasisPoints
            ? "战备低于25"
            : null;
    }

    /// <summary>天气延误天数：确定性随机抽取 1..3 日（DESIGN：一次天气延误，运输 +N 日）。</summary>
    public static int ResolveWeatherDelayDays(WorldState state) =>
        NewRandom(state, "ningyuan-risk-weather").Next(DesignWeatherDelayMinDays, DesignWeatherDelayMaxExclusive);

    /// <summary>袭粮损失比例：护卫与否决定上限（DESIGN：无护卫 0..20%，有护卫 0..5%）。</summary>
    public static int ResolveRaidLossPercent(bool escorted, WorldState state)
    {
        var random = NewRandom(state, "ningyuan-risk-raid");
        return escorted ? random.Next(0, DesignRaidLossMaxEscortedExclusive) : random.Next(0, DesignRaidLossMaxPlainExclusive);
    }

    /// <summary>三份报告之一：可信度与时效都确定性抽取；大臣信任越低报告越陈旧（DESIGN）。</summary>
    public static (int AgeDays, int Credibility) ResolveReportProfile(WorldState state, int reportIndex)
    {
        var random = NewRandom(state, $"ningyuan-risk-report-{reportIndex}");
        var credibility = random.Next(DesignReportCredibilityMin, DesignReportCredibilityMaxExclusive);
        var ageDays = state.Scenario.MinisterTrust < DesignReportStaleTrustThreshold
            ? random.Next(DesignReportStaleAgeMin, DesignReportStaleAgeMaxExclusive)
            : random.Next(DesignReportFreshAgeMin, DesignReportFreshAgeMaxExclusive);
        return (ageDays, credibility);
    }

    /// <summary>后半段配合度延迟（小时）：负担每超阈值 1 点延迟 1 小时（DESIGN）。</summary>
    public static int ResolveCooperationDelayHours(WorldState state)
    {
        var elapsedDays = (int)((state.GameTime.Value - state.Scenario.ScenarioStartGameTime.Value).TotalDays);
        if (elapsedDays < state.Scenario.SecondHalfFromDay ||
            state.Scenario.LocalBurden <= state.Scenario.BurdenCooperationThreshold)
        {
            return 0;
        }

        return state.Scenario.LocalBurden - state.Scenario.BurdenCooperationThreshold;
    }

    /// <summary>带键确定性随机：种子只来自世界编号/回合/事件键，同一世界同一输入必然同一结果。</summary>
    private static DeterministicRandom NewRandom(WorldState state, string eventId) =>
        new(state.Id.Value, state.TurnNumber, eventId);
}