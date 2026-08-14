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
/// - P1-AGENT-05（Wave 5A 审计）：多角色不能共享同一快照版本——首位提交生效后，
///   后续角色的意图必须携带内核实际推进后的权威版本，否则被 STATE_VERSION_CONFLICT
///   拒绝。本类每角色规划前都从 <see cref="RealtimeSimulationRuntime"/> 的权威状态
///   重取当前版本（绝不是 base+index 猜未来版本），并在本角色提交成功后把收件箱
///   在当前安全点真实推进，让下一角色看到权威推进后的世界；
/// - 版本权威性由 Simulation 持有：本类只读 ReadModel 的权威版本，不猜测、不预测；
///   调用方提供的内容快照用于编译最小上下文与入口预检，内核仍在安全点按最新状态
///   复核权限、版本与前置条件（doc 08 §8：绝不按旧快照透支资源）。
/// </remarks>
public sealed class AgentDecisionHost
{
    /// <summary>决策窗口：模型结果必须在接受时刻之前一个游戏小时内返回（半开区间，doc 07 §12）。</summary>
    private static readonly TimeSpan DecisionWindow = TimeSpan.FromHours(1);

    private readonly RealtimeSimulationRuntime _runtime;
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

        _runtime = runtime;
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
    /// 让所有托管角色完成一次决策并提交意图；每角色提交成功后从权威状态重取版本
    /// 再规划下一角色（P1-AGENT-05）。
    /// </summary>
    /// <param name="world">调用方提供的世界内容快照（编译最小上下文/入口权限预检用；
    /// 版本以运行时权威状态为准，内核仍在安全点复核）。</param>
    /// <param name="acceptedGameTime">结果被世界接受时的权威游戏时刻；缺省为世界当前时刻。</param>
    public async Task<HostedDecisionBatch> DecideAndSubmitAsync(
        WorldState world,
        GameTime? acceptedGameTime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        var acceptedAt = acceptedGameTime ?? world.GameTime;
        var decisions = new List<HostedAgentDecision>(_agents.Count);
        var ordered = _agents.OrderBy(item => item.ActorId.Value, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var agent = ordered[index];
            cancellationToken.ThrowIfCancellationRequested();

            // P1-AGENT-05：规划前从权威状态重取当前版本。前序角色已提交的命令已经过
            // 安全点真实生效（见下方 AdvanceTo），因此这里读到的必然是内核实际推进后的
            // 版本——用 base+index 猜未来版本被明确禁止。
            var authoritativeVersion = _runtime.ReadModel.WorldVersion;
            var context = _contextCompiler.Compile(world, agent.ActorId);
            if (context.WorldVersion != authoritativeVersion)
            {
                context = context with { WorldVersion = authoritativeVersion };
            }

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

            // 本角色提交成功后（有意图进入收件箱）在当前安全点真实推进，权威版本随之
            // 前进；下一角色据此重取。末位角色无需内部推进：调用方在自己的安全点统一
            // 受理即可，保持“调用方一次 AdvanceTo 看到批次结果”的既有契约。
            if (index < ordered.Length - 1 && submissions.Any(submission => submission.Accepted))
            {
                _runtime.AdvanceTo(_runtime.ReadModel.GameTime);
            }
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
