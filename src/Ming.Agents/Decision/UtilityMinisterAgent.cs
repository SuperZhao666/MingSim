using MingSim.Agents.Realtime;
using MingSim.Agents.Runtime;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Intents;

namespace MingSim.Agents.Decision;

/// <summary>
/// 数据驱动的意图权重表：每个白名单意图一个权重，表示“这个大臣有多偏好做这件事”。
/// </summary>
/// <remarks>
/// 权重只是数据，不写在决策分支里；后续可以按人物人格/策略传入不同权重，而不用改决策逻辑。
/// 分数 = 权重 × 条件，因此权重表直接决定“同样可行的几个意图里优先选哪个”。
/// </remarks>
public sealed record MinisterUtilityWeights(
    double BuildIndustry = 10.0,
    double ConvertArmy = 10.0,
    double PlanLogistics = 10.0)
{
    /// <summary>
    /// 按专注方向给出一组默认权重：专注方向的意图权重最高，其余保留低分（不是完全禁用）。
    /// 这样“专注”是偏好而不是硬编码分支，与其他方向冲突时仍由评分结果决定。
    /// </summary>
    public static MinisterUtilityWeights ForFocus(MinisterFocus focus) => focus switch
    {
        MinisterFocus.Industry => new MinisterUtilityWeights(BuildIndustry: 10.0, ConvertArmy: 1.0, PlanLogistics: 1.0),
        MinisterFocus.Military => new MinisterUtilityWeights(BuildIndustry: 1.0, ConvertArmy: 10.0, PlanLogistics: 1.0),
        MinisterFocus.Logistics => new MinisterUtilityWeights(BuildIndustry: 1.0, ConvertArmy: 1.0, PlanLogistics: 10.0),
        _ => new MinisterUtilityWeights(),
    };
}

/// <summary>
/// 用最小效用评分决策的大臣代理（规则路径的默认决策源）。
/// </summary>
/// <remarks>
/// 对白名单内的每个意图计算 分数 = 权重 × 条件（条件成立为 1，否则为 0），
/// 取分数最高的意图提交；所有条件都不成立时提交空列表（不做动作）。
/// 与旧的 if/switch 分支相比，这里只新增了“评分取最高”这一层：
/// - 白名单意图仍是原来那三种（建厂/改编/粮运），没有引入通用框架或黑板；
/// - 权重来自 <see cref="MinisterUtilityWeights"/>，是数据，可解释、可调参；
/// - 评分顺序固定（数组顺序），OrderByDescending 对同分保持原顺序，因此同状态必然同选择。
/// 产出仍是结构化 Intent，提交仍必须经 AgentRealtimeEntry，本类不接触 WorldState。
/// </remarks>
public sealed class UtilityMinisterAgent : IAgentDecisionSource
{
    private readonly MinisterUtilityWeights _weights;

    /// <summary>用专注方向对应的默认权重创建代理。</summary>
    public UtilityMinisterAgent(MinisterFocus focus)
        : this(MinisterUtilityWeights.ForFocus(focus))
    {
    }

    /// <summary>用显式权重表创建代理（例如按人物人格调参）。</summary>
    public UtilityMinisterAgent(MinisterUtilityWeights weights)
    {
        _weights = weights ?? throw new ArgumentNullException(nameof(weights));
    }

    /// <summary>按 权重 × 条件 对白名单意图评分，提交分数最高的那一个意图。</summary>
    public IReadOnlyList<WorldIntent> Decide(AgentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var candidates = new[]
        {
            new IntentCandidate(_weights.BuildIndustry, CanBuildFacility(context), BuildFacilityIntent),
            new IntentCandidate(_weights.ConvertArmy, CanConvertArmy(context), BuildConvertArmyIntent),
            new IntentCandidate(_weights.PlanLogistics, CanPlanLogistics(context), BuildPlanLogisticsIntent),
        };

        // OrderByDescending 是稳定排序：同分时保持候选数组顺序，保证确定性。
        var best = candidates
            .Select(candidate => (Candidate: candidate, Score: candidate.Weight * (candidate.Condition ? 1.0 : 0.0)))
            .OrderByDescending(item => item.Score)
            .First();

        return best.Score > 0 ? [best.Candidate.Build(context)] : [];
    }

    private static bool CanBuildFacility(AgentContext context) =>
        context.Capabilities.Contains(GameCapability.BuildIndustry) &&
        context.FacilityCount == 0 &&
        context.TreasurySilver >= 50_000;

    private static bool CanConvertArmy(AgentContext context) =>
        context.Capabilities.Contains(GameCapability.ConvertArmy) &&
        context.Armies.Any(army => army.Auxiliaries >= 1_000);

    private static bool CanPlanLogistics(AgentContext context) =>
        context.Capabilities.Contains(GameCapability.PlanLogistics);

    private static WorldIntent BuildFacilityIntent(AgentContext context) =>
        new BuildFacilityIntent(
            "agent-build-first-flintlock-workshop",  // 意图ID（本回合动作唯一标识）
            context.ActorId,
            context.TurnNumber,
            "turn-1-build-first-flintlock-workshop", // 幂等键，避免重复提交同一动作
            new FacilityId("factory-capital-flintlock-01"),
            new ProvinceId("capital"),
            FacilityType.FlintlockWorkshop,
            Budget: 50_000,
            BaseCapacity: 800,
            Workforce: 80);

    private static WorldIntent BuildConvertArmyIntent(AgentContext context)
    {
        // Decide 只在 CanConvertArmy 成立时调用本方法，因此一定存在可转化军队。
        var army = context.Armies.First(candidate => candidate.Auxiliaries >= 1_000);
        return new ConvertArmyIntent(
            "agent-convert-frontier-1000",
            context.ActorId,
            context.TurnNumber,
            "turn-1-convert-frontier-1000",
            army.ArmyId,
            Count: 1_000);
    }

    private static WorldIntent BuildPlanLogisticsIntent(AgentContext context) =>
        new PlanLogisticsIntent(
            "agent-logistics-ningyuan-300",
            context.ActorId,
            context.TurnNumber,
            "turn-1-logistics-ningyuan-300",
            context.WorldVersion,
            new RouteId("capital-ningyuan-grain"),
            300,
            context.GameTime.Value);

    private sealed record IntentCandidate(
        double Weight,
        bool Condition,
        Func<AgentContext, WorldIntent> Build);
}
