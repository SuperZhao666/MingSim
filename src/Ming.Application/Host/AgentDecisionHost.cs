using MingSim.Agents.Decision;
using MingSim.Agents.Realtime;
using MingSim.Agents.Runtime;
using MingSim.Domain;
using MingSim.Domain.Common;
using MingSim.Domain.Intents;
using MingSim.Domain.Realtime;
using MingSim.Simulation.Realtime;

namespace MingSim.Application.Host;

/// <summary>组合根把一位托管角色与其决策规划器绑定，交给 <see cref="AgentDecisionHost"/> 统一执行。</summary>
public sealed record HostedAgent(CharacterId ActorId, DecisionPlanner Planner);

/// <summary>一位托管角色的一次决策结果：来源、回退原因、产出意图与入口提交结果。</summary>
public sealed record HostedAgentDecision(
    CharacterId ActorId,
    string DecisionId,
    DecisionSource Source,
    ModelFallbackReason? FallbackReason,
    IReadOnlyList<WorldIntent> Intents,
    IReadOnlyList<AgentIntentResult> Submissions);

/// <summary>一轮托管决策的聚合结果：决策被接受时的权威游戏时刻 + 每位角色的记录。</summary>
public sealed record HostedDecisionBatch(
    GameTime AcceptedGameTime,
    IReadOnlyList<HostedAgentDecision> Decisions);

/// <summary>
/// 组合根宿主（doc 04 §3 HOST 的最小接线）：把 #27 的 <see cref="DecisionPlanner"/>
/// （预算/审计/密钥/回退）与 #18 的 <see cref="AgentRealtimeEntry"/> 接入实时内核，
/// 使托管角色（如 1629 场景的 zhu-youjian、hubu-slot、duliaoxiang-slot）的决策
/// 走“模型增强→结构化 Intent→入口提交”的完整管线；模型离线/超支/解析失败时
/// 由 DecisionPlanner 回退 Utility AI，世界主循环不阻塞（doc 07 §13.3/13.4）。
/// </summary>
/// <remarks>
/// 边界约束（与 AGENTS.md / doc 04 §5 / doc 05 §7 红线一致）：
/// - 本类只读调用方提供的 WorldState（编译最小上下文 + 入口权限预检），绝不写世界；
///   意图只能经 AgentRealtimeEntry 转成 RealtimeCommand 投入唯一 Simulation 收件箱，
///   由单写者安全点复核权限、版本与前置条件；
/// - 不创建接口/工厂；不复制 DecisionPlanner/AgentRealtimeEntry 已有逻辑；
/// - 每个角色的 DecisionId 由 actor + 观察世界版本 + 接受时刻派生，同一决策重试保持
///   稳定；模型输出第 N 个意图的 CommandId 稳定派生为 DecisionId-N（doc 07 §12），
///   不会因重试绕过内核幂等；
/// - 调用方必须提供与内核一致的世界快照（当前由宿主进程在安全点持有）；模型结果
///   即使基于过期快照，内核仍以 STATE_VERSION_CONFLICT 拒绝，绝不“适配一下”执行。
/// </remarks>
public sealed class AgentDecisionHost
{
    /// <summary>决策窗口：模型结果必须在接受时刻之前一个游戏小时内返回（半开区间，doc 07 §12）。</summary>
    private static readonly TimeSpan DecisionWindow = TimeSpan.FromHours(1);

    private readonly AgentRealtimeEntry _entry;
    private readonly AgentContextCompiler _contextCompiler = new();
    private readonly IReadOnlyList<HostedAgent> _agents;

    public AgentDecisionHost(RealtimeSimulationRuntime runtime, IReadOnlyList<HostedAgent> agents)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(agents);
        if (agents.Count == 0)
        {
            throw new ArgumentException("至少需要注册一位托管角色。", nameof(agents));
        }

        _agents = agents.ToArray();
        var duplicate = _agents.GroupBy(agent => agent.ActorId.Value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"角色 {duplicate.Key} 被重复注册到宿主。", nameof(agents));
        }

        _entry = new AgentRealtimeEntry(runtime);
    }

    /// <summary>
    /// 让所有托管角色在当前世界快照上完成一次决策并提交意图。
    /// </summary>
    /// <param name="world">调用方提供的权威世界快照（读上下文/权限预检用；内核仍在安全点复核）。</param>
    /// <param name="acceptedGameTime">结果被世界接受时的权威游戏时刻；缺省为世界当前时刻。</param>
    public async Task<HostedDecisionBatch> DecideAndSubmitAsync(
        WorldState world,
        GameTime? acceptedGameTime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        var acceptedAt = acceptedGameTime ?? world.GameTime;
        var decisions = new List<HostedAgentDecision>(_agents.Count);
        foreach (var agent in _agents.OrderBy(item => item.ActorId.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = _contextCompiler.Compile(world, agent.ActorId);
            var decisionId = BuildDecisionId(agent.ActorId, context.WorldVersion, acceptedAt);
            var request = new DecisionRequest(
                decisionId,
                agent.ActorId,
                context.WorldVersion,
                context.GameTime,
                acceptedAt.Add(DecisionWindow));
            var result = await agent.Planner.PlanAsync(request, context, acceptedAt, cancellationToken).ConfigureAwait(false);
            var submissions = _entry.Submit(world, result.Intents);
            decisions.Add(new HostedAgentDecision(
                agent.ActorId, decisionId, result.Source, result.FallbackReason, result.Intents, submissions));
        }

        return new HostedDecisionBatch(acceptedAt, decisions);
    }

    /// <summary>
    /// 稳定决策编号：actor + 观察版本 + 接受时刻，保证同一决策重试得到同一 ID
    /// （幂等键随之为 DecisionId-N，不会因重试绕过内核幂等）。
    /// </summary>
    private static string BuildDecisionId(CharacterId actorId, long worldVersion, GameTime acceptedAt) =>
        $"agent-{actorId.Value}-{worldVersion}-{acceptedAt.Value.UtcTicks}";
}
