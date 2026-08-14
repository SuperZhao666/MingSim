using MingSim.Domain;
using MingSim.Domain.Common;
using MingSim.Domain.Decrees;
using MingSim.Domain.Economy;
using MingSim.Domain.Realtime;

namespace MingSim.Simulation.Realtime;

/// <summary>终局评估结果；六个解释维度对齐 doc 03 §7.3。</summary>
public enum EndgameOutcome
{
    InProgress,
    HardFailure,
    BarelyMaintained,
    Success,
    Excellent,
    Failed,
}

/// <summary>
/// 自动可检查的终局评估输出（doc 03 §7.2/§7.3）。
/// </summary>
/// <param name="AvailableGrainDays">维度1：宁远可用粮天数。</param>
/// <param name="ReadinessValue">维度2：前线有效战备（0..100）。</param>
/// <param name="TreasuryRemaining">维度3a：中央财政余量（两）。</param>
/// <param name="ArrearsGrain">维度3b：新增欠饷（石）。</param>
/// <param name="LocalBurden">维度4：场景级地方负担（0..100）。</param>
/// <param name="MinisterTrust">维度5a：大臣信任（0..100）。</param>
/// <param name="DeadlineMissedCount">维度5b：责任归属（逾期甩责政令数）。</param>
/// <param name="AuditChainComplete">维度6a：审计完整性（政令是否都已进入终态）。</param>
/// <param name="ScenarioBudgetOverdrawn">维度6b：银预算是否透支（累计支出超过场景银预算）。</param>
/// <param name="Explanation">六维解释文本。</param>
public sealed record EndgameEvaluation(
    EndgameOutcome Outcome,
    string? HardFailureReason,
    long AvailableGrainDays,
    int ReadinessValue,
    long TreasuryRemaining,
    long ArrearsGrain,
    int LocalBurden,
    int MinisterTrust,
    int DeadlineMissedCount,
    bool AuditChainComplete,
    bool ScenarioBudgetOverdrawn,
    string Explanation);

/// <summary>
/// 终局评估函数：先判硬失败，再按 90 日终局分档（doc 03 §7.2 首轮调参基线）。
/// 分档门槛与场景数值都是 DESIGN，不是史实断言。
/// </summary>
public static class EndgameEvaluator
{
    public const int ScenarioDurationDays = 90;

    public const int DesignHardFailureZeroDays = 7;   // doc 03 §7.2：连续 7 日可用粮为 0
    public const int DesignHardFailureReadiness = 25; // doc 03 §7.2：战备低于 25
    public const int DesignBarelyGrainDays = 7;       // doc 03 §7.2
    public const int DesignBarelyReadiness = 45;      // doc 03 §7.2
    public const int DesignSuccessGrainDays = 18;     // doc 03 §7.2
    public const int DesignSuccessReadiness = 60;     // doc 03 §7.2
    public const int DesignSuccessBurden = 70;        // doc 03 §7.2：地方负担低于 70
    public const int DesignExcellentGrainDays = 25;   // doc 03 §7.2
    public const int DesignExcellentReadiness = 70;   // doc 03 §7.2
    public const int DesignExcellentTrust = 55;       // doc 03 §7.2

    /// <summary>
    /// 评估终局：输出六维解释与分档结果。
    /// </summary>
    /// <param name="state">当前权威世界状态。</param>
    /// <param name="scenarioStart">场景起点（世界初始时刻），用于计算 90 日终局。</param>
    public static EndgameEvaluation Evaluate(WorldState state, GameTime scenarioStart)
    {
        var scenario = state.Scenario;
        long availableGrain = 0;
        if (scenario.FrontStockpileId is StockpileId frontId &&
            state.Logistics.Stockpiles.TryGetValue(frontId, out var front))
        {
            availableGrain = front.GrainQuantity;
        }

        var availableGrainDays = availableGrain / scenario.DailyGrainDemand;
        var readinessValue = state.Readiness.Value;
        var deadlineMissed = state.Decrees.Values.Count(decree => decree.Status == DecreeStatus.Expired);
        var auditComplete = state.Decrees.Values.All(decree =>
            decree.Status is DecreeStatus.Completed or DecreeStatus.Rejected or DecreeStatus.Expired);
        var overdrawn = scenario.SpentSilver > scenario.ScenarioSilverBudget;

        string? hardFailureReason = null;
        if (state.Readiness.ConsecutiveZeroGrainDays >= DesignHardFailureZeroDays)
        {
            hardFailureReason = "连续7日可用粮为0";
        }
        else if (readinessValue < DesignHardFailureReadiness)
        {
            hardFailureReason = "战备低于25";
        }

        var outcome = EndgameOutcome.InProgress;
        if (hardFailureReason is not null)
        {
            outcome = EndgameOutcome.HardFailure;
        }
        else
        {
            var elapsedDays = (int)((state.GameTime.Value - scenarioStart.Value).TotalDays);
            if (elapsedDays >= ScenarioDurationDays)
            {
                var barely = availableGrainDays >= DesignBarelyGrainDays &&
                             readinessValue >= DesignBarelyReadiness && auditComplete;
                var success = availableGrainDays >= DesignSuccessGrainDays &&
                              readinessValue >= DesignSuccessReadiness && !overdrawn &&
                              scenario.LocalBurden < DesignSuccessBurden;
                var excellent = success && availableGrainDays >= DesignExcellentGrainDays &&
                                readinessValue >= DesignExcellentReadiness &&
                                scenario.MinisterTrust >= DesignExcellentTrust;

                if (excellent)
                {
                    outcome = EndgameOutcome.Excellent;
                }
                else if (success)
                {
                    outcome = EndgameOutcome.Success;
                }
                else if (barely)
                {
                    outcome = EndgameOutcome.BarelyMaintained;
                }
                else
                {
                    // 未硬失败但未达"勉强维持"门槛：90 日终局判为失败（补齐 doc 03 §7.2 分档缺口，DESIGN）。
                    outcome = EndgameOutcome.Failed;
                }
            }
        }

        var explanation = string.Join(Environment.NewLine,
        [
            $"宁远可用粮：{availableGrainDays} 日（{availableGrain} 石 / 日需 {scenario.DailyGrainDemand} 石）",
            $"前线战备：{readinessValue}/100",
            $"中央财政：余 {state.Economy.Treasury.Silver} 两；场景已支 {scenario.SpentSilver} 两；新增欠饷 {state.Readiness.ArrearsGrain} 石",
            $"地方负担：{scenario.LocalBurden}/100",
            $"大臣信任：{scenario.MinisterTrust}/100；逾期甩责政令 {deadlineMissed} 次（责任归属）",
            $"执行与审计：政令审计链{(auditComplete ? "完整" : "不完整")}；银预算{(overdrawn ? "透支" : "未透支")}",
        ]);

        return new EndgameEvaluation(
            outcome,
            hardFailureReason,
            availableGrainDays,
            readinessValue,
            state.Economy.Treasury.Silver,
            state.Readiness.ArrearsGrain,
            scenario.LocalBurden,
            scenario.MinisterTrust,
            deadlineMissed,
            auditComplete,
            overdrawn,
            explanation);
    }
}
