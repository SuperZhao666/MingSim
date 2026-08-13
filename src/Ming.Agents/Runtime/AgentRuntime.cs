using MingSim.Domain;
using MingSim.Domain.Common;
using MingSim.Domain.Intents;

namespace MingSim.Agents.Runtime;

/// <summary>为一个角色产生结构化意图的代理接口。</summary>
public interface IAgentDecisionSource
{
    IReadOnlyList<WorldIntent> Decide(AgentContext context);
}

/// <summary>把一个角色和它的决策来源绑定起来。</summary>
public sealed record AgentRegistration(
    CharacterId ActorId,
    IAgentDecisionSource DecisionSource);

/// <summary>从权威世界状态编译有限的代理上下文。</summary>
public sealed class AgentContextCompiler
{
    public AgentContext Compile(WorldState world, CharacterId actorId)
    {
        var armies = world.Military.Armies.Values
            .Select(army => new ArmyObservation(
                army.Id,
                army.Name,
                army.Auxiliaries,
                army.LineInfantry,
                army.TrainingDays))
            .OrderBy(army => army.ArmyId.Value, StringComparer.Ordinal)
            .ToArray();

        var capabilities = world.CapabilityGrants
            .Where(grant => grant.ActorId == actorId)
            .Where(grant => grant.ExpiresAtTurn is null || world.TurnNumber <= grant.ExpiresAtTurn)
            .Select(grant => grant.Capability)
            .ToHashSet();

        return new AgentContext(
            actorId,
            world.TurnNumber,
            world.Economy.Treasury.Silver,
            world.Industry.Facilities.Count,
            armies,
            capabilities);
    }
}

/// <summary>
/// 代理运行时。
/// </summary>
/// <remarks>
/// 它只负责“收集提案”，不会调用 WorldState 的修改方法。
/// 真正的权限验证和执行仍在 SimulationKernel 中，这个边界即使换成 LLM 也不能删掉。
/// </remarks>
public sealed class AgentRuntime
{
    private readonly AgentContextCompiler _contextCompiler;

    public AgentRuntime(AgentContextCompiler? contextCompiler = null)
    {
        _contextCompiler = contextCompiler ?? new AgentContextCompiler();
    }

    public IReadOnlyList<WorldIntent> CollectDecisions(
        WorldState world,
        IReadOnlyList<AgentRegistration> registrations)
    {
        var intents = new List<WorldIntent>();

        foreach (var registration in registrations.OrderBy(item => item.ActorId.Value, StringComparer.Ordinal))
        {
            var context = _contextCompiler.Compile(world, registration.ActorId);
            intents.AddRange(registration.DecisionSource.Decide(context));
        }

        // 固定排序让同一份输入在不同运行中都以相同顺序进入归并阶段。
        return intents.OrderBy(intent => intent.IntentId, StringComparer.Ordinal).ToArray();
    }
}
