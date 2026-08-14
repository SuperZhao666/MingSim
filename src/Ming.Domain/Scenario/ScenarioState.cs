using MingSim.Domain.Common;
using MingSim.Domain.Realtime;

namespace MingSim.Domain.Scenario;

/// <summary>
/// 场景级状态（P0 玩法规则）：地方负担与大臣信任，以及场景规则参数。
/// </summary>
/// <remarks>
/// 数值全部是 DESIGN（doc 03 §7.1 场景控制表），不是史实断言：
/// 地方负担初始 20/100（超额征发升高，后半段降低配合度）；
/// 大臣信任初始 50/100（越权/甩责下降，影响后续报告时效）。
/// <see cref="FrontStockpileId"/> 为 null 时场景规则关闭：
/// 纯物流内核测试世界不携带场景配置，从而不被日耗/战备等场景规则干扰。
/// </remarks>
public sealed class ScenarioState
{
    public const int DesignInitialLocalBurden = 20;            // DESIGN：doc 03 §7.1
    public const int DesignInitialMinisterTrust = 50;          // DESIGN：doc 03 §7.1
    public const int DesignDailyGrainDemand = 300;             // DESIGN：300 石/日（5400 石约 18 日倒计时）
    public const int DesignSecondHalfFromDay = 45;             // DESIGN：90 日的中点，"后半段"
    public const int DesignBurdenCooperationThreshold = 60;    // DESIGN：后半段负担超过该值降低配合度
    public const long DesignScenarioSilverBudget = 20_000;     // DESIGN：doc 03 §7.1 场景银预算（两）
    public const int DesignShipmentBurdenIncrease = 4;         // DESIGN：每批调粮征发使地方负担 +4
    public const int DesignShirkTrustPenalty = 5;              // DESIGN：承办人逾期（甩责）使大臣信任 -5
    public const long DesignEscortCostSilver = 400;            // DESIGN：护卫每批 +400 两（doc 03 §7.1）

    public ScenarioState(
        int localBurden = DesignInitialLocalBurden,
        int ministerTrust = DesignInitialMinisterTrust,
        int dailyGrainDemand = DesignDailyGrainDemand,
        StockpileId? frontStockpileId = null,
        int secondHalfFromDay = DesignSecondHalfFromDay,
        int burdenCooperationThreshold = DesignBurdenCooperationThreshold,
        long scenarioSilverBudget = DesignScenarioSilverBudget)
    {
        if (dailyGrainDemand <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dailyGrainDemand), "前线日需粮食必须为正数。");
        }

        LocalBurden = Math.Clamp(localBurden, 0, 100);
        MinisterTrust = Math.Clamp(ministerTrust, 0, 100);
        DailyGrainDemand = dailyGrainDemand;
        FrontStockpileId = frontStockpileId;
        SecondHalfFromDay = secondHalfFromDay;
        BurdenCooperationThreshold = burdenCooperationThreshold;
        ScenarioSilverBudget = scenarioSilverBudget;
    }

    public int LocalBurden { get; private set; }

    public int MinisterTrust { get; private set; }

    public int DailyGrainDemand { get; }

    /// <summary>前线粮仓；null 表示本世界没有启用宁远场景规则。</summary>
    public StockpileId? FrontStockpileId { get; }

    /// <summary>从第几天起视为"后半段"，负担高时会降低地方配合度。</summary>
    public int SecondHalfFromDay { get; }

    /// <summary>后半段地方负担超过该值时，运输配合度下降（到达延迟）。</summary>
    public int BurdenCooperationThreshold { get; }

    /// <summary>场景银预算：终局评估用它判断"银预算未透支"（doc 03 §7.2）。</summary>
    public long ScenarioSilverBudget { get; }

    /// <summary>本场景累计已支出银两（政令预算 + 护卫费用）。</summary>
    public long SpentSilver { get; private set; }

    /// <summary>场景起点（世界初始时刻）：用于计算"后半段"与 90 日终局。</summary>
    public GameTime ScenarioStartGameTime { get; private set; }

    /// <summary>硬失败（连续 7 日断粮或战备低于 25）是否已向日志报告过：只报一次，避免每日刷屏。</summary>
    public bool HardFailureReported { get; private set; }

    /// <summary>场景规则是否启用：只有配置了前线粮仓的世界才运行日耗/战备/负担等场景规则。</summary>
    public bool IsScenarioActive => FrontStockpileId is not null;

    internal void ChangeLocalBurden(int delta) => LocalBurden = Math.Clamp(LocalBurden + delta, 0, 100);

    internal void ChangeMinisterTrust(int delta) => MinisterTrust = Math.Clamp(MinisterTrust + delta, 0, 100);

    internal void AddSpentSilver(long amount)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "场景支出不能为负。");
        }

        SpentSilver = checked(SpentSilver + amount);
    }

    internal void SetScenarioStart(GameTime scenarioStart) => ScenarioStartGameTime = scenarioStart;

    internal void MarkHardFailureReported() => HardFailureReported = true;

    internal ScenarioState Clone() => new(
        LocalBurden, MinisterTrust, DailyGrainDemand, FrontStockpileId,
        SecondHalfFromDay, BurdenCooperationThreshold, ScenarioSilverBudget)
    {
        SpentSilver = SpentSilver,
        ScenarioStartGameTime = ScenarioStartGameTime,
        HardFailureReported = HardFailureReported,
    };
}
