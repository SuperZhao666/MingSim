using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
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

/// <summary>
/// 从权威世界状态编译有限的代理上下文（P1-AGENT-01/02）。
/// </summary>
/// <remarks>
/// 候选集规则：路线只保留"可行动"候选（起点有粮、目的地有余量、路线在途未满），
/// 军队携带当前位置与地图邻接合法目的地。模型与规则都只能从这些候选里选，
/// 因此不会把完整 WorldState 塞给模型，也不会让代理发明不存在的路线/军队/目的地。
/// </remarks>
public sealed class AgentContextCompiler
{
    public AgentContext Compile(WorldState world, CharacterId actorId)
    {
        var authorizer = new CapabilityAuthorizer();
        var armies = world.Military.Armies.Values
            .Select(army =>
            {
                var allowed = new HashSet<GameCapability>();
                if (authorizer.Check(world, actorId, GameCapability.MoveArmy, army.Id.Value).Allowed)
                    allowed.Add(GameCapability.MoveArmy);
                if (authorizer.Check(world, actorId, GameCapability.ConvertArmy, army.Id.Value).Allowed)
                    allowed.Add(GameCapability.ConvertArmy);
                return new ArmyObservation(
                    army.Id,
                    army.Name,
                    army.LocationId,
                    army.Auxiliaries,
                    army.LineInfantry,
                    army.TrainingDays,
                    allowed.Contains(GameCapability.MoveArmy) ? AdjacentDestinations(world, army.LocationId) : [],
                    allowed);
            })
            .Where(army => army.AllowedCapabilities.Count > 0)
            .OrderBy(army => army.ArmyId.Value, StringComparer.Ordinal)
            .ToArray();

        var routes = world.Logistics.Routes.Values
            .Where(route => authorizer.Check(world, actorId, GameCapability.PlanLogistics, route.Id.Value).Allowed)
            .Select(route => BuildRouteObservation(world, route))
            .Where(route => route.IsActionable)
            .OrderBy(route => route.RouteId.Value, StringComparer.Ordinal)
            .ToArray();

        // 与 CapabilityAuthorizer 的两条来源保持一致：直接 Grant + 当前有效 Appointment 对应机构能力。
        // Scope 只在具体候选动作上由 authorizer.Check(resourceId) 收窄；Capabilities 表示“具备该类能力”。
        var capabilities = world.CapabilityGrants
            .Where(grant => grant.ActorId == actorId)
            .Where(grant => grant.ExpiresAtTurn is null || world.TurnNumber <= grant.ExpiresAtTurn)
            .Select(grant => grant.Capability)
            .ToHashSet();
        foreach (var appointment in world.Appointments)
        {
            if (appointment.PersonId != actorId || !appointment.IsActiveAt(world.GameTime)) continue;
            if (!world.Institutions.TryGetValue(appointment.OfficeId, out var office)) continue;
            foreach (var capability in office.Capabilities)
                capabilities.Add(capability);
        }

        return new AgentContext(
            actorId,
            world.TurnNumber,
            world.Economy.Treasury.Silver,
            world.Industry.Facilities.Count,
            armies,
            routes,
            capabilities,
            world.WorldVersion,
            world.GameTime);
    }

    /// <summary>军队当前位置出发、地图上邻接的合法目的地（排序保证确定性）。</summary>
    private static IReadOnlyList<ProvinceId> AdjacentDestinations(WorldState world, ProvinceId locationId) =>
        world.Map.Provinces.Keys
            .Where(province => world.Map.IsAdjacent(locationId, province))
            .OrderBy(province => province.Value, StringComparer.Ordinal)
            .ToArray();

    /// <summary>把权威 RouteState 裁剪成最小路线候选观察。</summary>
    private static RouteObservation BuildRouteObservation(WorldState world, RouteState route)
    {
        var source = world.Logistics.Stockpiles[route.FromStockpileId];
        var destination = world.Logistics.Stockpiles[route.ToStockpileId];
        var reserved = world.Logistics.ReservedIncomingGrain(destination.Id);
        var headroom = destination.Capacity - destination.GrainQuantity - reserved;
        if (headroom < 0)
        {
            headroom = 0;
        }

        return new RouteObservation(
            route.Id,
            source.LocationId,
            destination.LocationId,
            source.GrainQuantity,
            headroom,
            route.Capacity,
            world.Logistics.InTransitGrain(route.Id),
            route.TravelHours,
            route.LossPerThousand);
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
