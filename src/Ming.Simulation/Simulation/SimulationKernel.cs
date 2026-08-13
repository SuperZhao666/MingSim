using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Errors;
using MingSim.Domain.Events;
using MingSim.Domain.Intents;
using MingSim.Simulation.Systems;

namespace MingSim.Simulation;

/// <summary>模拟内核的最小接口。</summary>
public interface ISimulationKernel
{
    TurnResolution ResolveTurn(WorldState frozenState, IReadOnlyList<WorldIntent> intents);
}

/// <summary>
/// 确定性回合模拟内核。
/// </summary>
/// <remarks>
/// 这里是整个项目最重要的边界：输入是冻结状态和结构化意图，输出是新状态或结构化失败。
/// 这个类不引用 Godot、不调用数据库、不调用大模型，所以可以在命令行和测试中独立运行。
/// </remarks>
public sealed class SimulationKernel : ISimulationKernel
{
    private readonly CapabilityAuthorizer _authorizer;
    private readonly InvariantChecker _invariantChecker;
    private readonly IndustrySettlement _industrySettlement;

    public SimulationKernel(
        CapabilityAuthorizer? authorizer = null,
        InvariantChecker? invariantChecker = null,
        IndustrySettlement? industrySettlement = null)
    {
        _authorizer = authorizer ?? new CapabilityAuthorizer();
        _invariantChecker = invariantChecker ?? new InvariantChecker();
        _industrySettlement = industrySettlement ?? new IndustrySettlement();
    }

    public TurnResolution ResolveTurn(WorldState frozenState, IReadOnlyList<WorldIntent> intents)
    {
        ArgumentNullException.ThrowIfNull(frozenState);
        ArgumentNullException.ThrowIfNull(intents);

        var workingState = frozenState.Clone();
        var events = new List<DomainEvent>();
        var errors = new List<SimulationError>();
        var idempotencyKeys = new HashSet<string>(StringComparer.Ordinal);

        // 先逐项验证并写入“临时工作区”。只要最后有任何错误，整个工作区都会被丢弃。
        foreach (var intent in intents)
        {
            if (!idempotencyKeys.Add(intent.IdempotencyKey))
            {
                errors.Add(new SimulationError(
                    "IDEMPOTENCY_KEY_REUSED",
                    $"幂等键 {intent.IdempotencyKey} 在同一回合中重复使用。"));
                continue;
            }

            if (intent.ExpectedTurn != frozenState.TurnNumber)
            {
                errors.Add(new SimulationError(
                    "STATE_VERSION_CONFLICT",
                    $"意图 {intent.IntentId} 基于回合 {intent.ExpectedTurn}，当前冻结回合是 {frozenState.TurnNumber}。",
                    Retryable: true));
                continue;
            }

            var intentErrors = ApplyIntent(workingState, intent, events);
            errors.AddRange(intentErrors);
        }

        if (errors.Count > 0)
        {
            return TurnResolution.Reject(frozenState, errors);
        }

        // 自动系统在所有代理意图之后运行，且只运行一次。
        events.AddRange(_industrySettlement.Settle(workingState));

        var invariantErrors = _invariantChecker.Check(workingState);
        if (invariantErrors.Count > 0)
        {
            return TurnResolution.Reject(frozenState, invariantErrors);
        }

        events.Add(new DomainEvent(
            $"turn-committed-{workingState.Id}-{workingState.TurnNumber}",
            workingState.Id,
            workingState.TurnNumber,
            "TurnCommitted",
            $"回合 {workingState.TurnNumber} 已通过规则和不变量检查。",
            new Dictionary<string, string>
            {
                ["intent_count"] = intents.Count.ToString(),
            }));

        workingState.AdvanceTurn();
        return TurnResolution.Commit(workingState, events);
    }

    private IReadOnlyList<SimulationError> ApplyIntent(
        WorldState world,
        WorldIntent intent,
        ICollection<DomainEvent> events)
    {
        return intent switch
        {
            BuildFacilityIntent build => ApplyBuildFacility(world, build, events),
            ConvertArmyIntent convert => ApplyConvertArmy(world, convert, events),
            TrainArmyIntent train => ApplyTrainArmy(world, train, events),
            _ => [new SimulationError("UNKNOWN_INTENT", $"不认识的意图类型 {intent.GetType().Name}。")],
        };
    }

    private IReadOnlyList<SimulationError> ApplyBuildFacility(
        WorldState world,
        BuildFacilityIntent intent,
        ICollection<DomainEvent> events)
    {
        var authorization = _authorizer.Check(
            world,
            intent.ActorId,
            GameCapability.BuildIndustry,
            intent.LocationId.Value);

        if (!authorization.Allowed)
        {
            return [new SimulationError("TOOL_SCOPE_DENIED", authorization.Reason)];
        }

        if (intent.Budget <= 0 || intent.BaseCapacity <= 0 || intent.Workforce <= 0)
        {
            return [new SimulationError("PRECONDITION_FAILED", "工坊预算、产能和工人数量必须大于 0。")];
        }

        if (world.Industry.Contains(intent.FacilityId))
        {
            return [new SimulationError("FACILITY_ALREADY_EXISTS", $"工坊 {intent.FacilityId} 已经存在。")];
        }

        if (!world.Economy.Treasury.TrySpend(intent.Budget))
        {
            return [new SimulationError(
                "INSUFFICIENT_TREASURY",
                $"需要 {intent.Budget} 两，当前国库只有 {world.Economy.Treasury.Silver} 两。")];
        }

        world.Industry.Add(new FacilityState(
            intent.FacilityId,
            intent.LocationId,
            intent.FacilityType,
            intent.BaseCapacity,
            intent.Workforce,
            2,
            world.TurnNumber));

        events.Add(CreateEvent(
            world,
            intent.IntentId,
            "FacilityProjectStarted",
            $"工部登记了工坊 {intent.FacilityId} 的建设项目，预算 {intent.Budget} 两。",
            ("facility_id", intent.FacilityId.Value),
            ("budget", intent.Budget.ToString())));

        return [];
    }

    private IReadOnlyList<SimulationError> ApplyConvertArmy(
        WorldState world,
        ConvertArmyIntent intent,
        ICollection<DomainEvent> events)
    {
        var authorization = _authorizer.Check(
            world,
            intent.ActorId,
            GameCapability.ConvertArmy,
            intent.ArmyId.Value);

        if (!authorization.Allowed)
        {
            return [new SimulationError("TOOL_SCOPE_DENIED", authorization.Reason)];
        }

        if (intent.EquipmentType != "flintlock")
        {
            return [new SimulationError("PRECONDITION_FAILED", $"第一版暂不支持装备 {intent.EquipmentType}。")];
        }

        if (!world.Military.Armies.TryGetValue(intent.ArmyId, out var army))
        {
            return [new SimulationError("ARMY_NOT_FOUND", $"军队 {intent.ArmyId} 不存在。")];
        }

        if (intent.Count <= 0 || army.Auxiliaries < intent.Count)
        {
            return [new SimulationError("INSUFFICIENT_MANPOWER", $"辅兵不足，无法改编 {intent.Count} 人。")];
        }

        var stock = world.Economy.Inventory.GetOrCreate(intent.EquipmentType);
        if (!stock.TryConsume(intent.Count))
        {
            return [new SimulationError(
                "INSUFFICIENT_EQUIPMENT",
                $"需要 {intent.Count} 件 {intent.EquipmentType}，当前可用数量只有 {stock.Quantity - stock.Reserved}。")];
        }

        army.TryConvertAuxiliariesToLineInfantry(intent.Count);
        events.Add(CreateEvent(
            world,
            intent.IntentId,
            "ArmyConverted",
            $"军队 {army.Name} 完成 {intent.Count} 人辅兵改编，消耗同等数量燧发枪。",
            ("army_id", intent.ArmyId.Value),
            ("count", intent.Count.ToString()),
            ("equipment_type", intent.EquipmentType)));

        return [];
    }

    private IReadOnlyList<SimulationError> ApplyTrainArmy(
        WorldState world,
        TrainArmyIntent intent,
        ICollection<DomainEvent> events)
    {
        var authorization = _authorizer.Check(
            world,
            intent.ActorId,
            GameCapability.TrainArmy,
            intent.ArmyId.Value);

        if (!authorization.Allowed)
        {
            return [new SimulationError("TOOL_SCOPE_DENIED", authorization.Reason)];
        }

        if (!world.Military.Armies.TryGetValue(intent.ArmyId, out var army))
        {
            return [new SimulationError("ARMY_NOT_FOUND", $"军队 {intent.ArmyId} 不存在。")];
        }

        if (intent.Days <= 0 || intent.Days > 365 || intent.Budget <= 0)
        {
            return [new SimulationError("PRECONDITION_FAILED", "训练天数必须在 1 到 365 之间，训练预算必须大于 0。")];
        }

        if (!world.Economy.Treasury.TrySpend(intent.Budget))
        {
            return [new SimulationError("INSUFFICIENT_TREASURY", "训练预算超过当前国库可用银两。")];
        }

        army.AddTrainingDays(intent.Days);
        events.Add(CreateEvent(
            world,
            intent.IntentId,
            "ArmyTrainingStarted",
            $"军队 {army.Name} 获得 {intent.Days} 天训练安排，预算 {intent.Budget} 两。",
            ("army_id", intent.ArmyId.Value),
            ("days", intent.Days.ToString())));

        return [];
    }

    private static DomainEvent CreateEvent(
        WorldState world,
        string intentId,
        string eventType,
        string description,
        params (string Key, string Value)[] data)
    {
        return new DomainEvent(
            $"{eventType}-{intentId}",
            world.Id,
            world.TurnNumber,
            eventType,
            description,
            data.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
    }
}
