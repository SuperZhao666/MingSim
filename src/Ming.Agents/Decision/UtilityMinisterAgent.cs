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
        context.Capabilities.Contains(GameCapability.PlanLogistics) &&
        context.Routes.Count > 0;

    private static WorldIntent BuildFacilityIntent(AgentContext context) =>
        new BuildFacilityIntent(
            $"agent-build-first-flintlock-workshop-{context.WorldVersion}",  // 意图ID：随世界版本变化，不同决策周期可再次提交
            context.ActorId,
            context.TurnNumber,
            $"build-first-flintlock-workshop-{context.WorldVersion}", // 幂等键：同版本重复提交被内核去重，版本推进后产生新动作
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
            $"agent-convert-frontier-1000-{context.WorldVersion}",
            context.ActorId,
            context.TurnNumber,
            $"convert-frontier-1000-{context.WorldVersion}",
            army.ArmyId,
            Count: 1_000);
    }

    /// <summary>
    /// 从候选集选择真实存在的路线生成粮运意图（P1-AGENT-01 修复）。
    /// </summary>
    /// <remarks>
    /// 候选集由 AgentContextCompiler 从权威 WorldState 编译，只含可行动路线；
    /// 选择规则固定（RouteId 字典序取第一条、数量按约束封顶），同上下文必然同意图。
    /// 数量封顶在 300 石与 起点库存/目的地余量/路线剩余容量 之内，保证意图真实可执行；
    /// 意图 ID 与幂等键都携带所选路线 ID，不再出现审计中的虚构路线 capital-ningyuan-grain。
    /// </remarks>
    private static WorldIntent BuildPlanLogisticsIntent(AgentContext context)
    {
        // Decide 只在 CanPlanLogistics 成立时调用本方法，因此候选集非空。
        var route = context.Routes.OrderBy(item => item.RouteId.Value, StringComparer.Ordinal).First();
        var remainingCapacity = route.RouteCapacity - route.InTransitGrain;
        var quantity = Math.Min(300, Math.Min(route.SourceGrain, Math.Min(route.DestinationHeadroom, remainingCapacity)));
        return new PlanLogisticsIntent(
            $"agent-logistics-{route.RouteId.Value}-{quantity}-{context.WorldVersion}",
            context.ActorId,
            context.TurnNumber,
            $"logistics-{route.RouteId.Value}-{quantity}-{context.WorldVersion}",
            context.WorldVersion,
            route.RouteId,
            quantity,
            context.GameTime.Value);
    }

    private sealed record IntentCandidate(
        double Weight,
        bool Condition,
        Func<AgentContext, WorldIntent> Build);
}
