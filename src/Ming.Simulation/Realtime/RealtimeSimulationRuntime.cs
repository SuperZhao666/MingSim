using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Common;
using MingSim.Domain.Errors;
using MingSim.Domain.Events;
using MingSim.Domain.Military;
using MingSim.Domain.Economy;
using MingSim.Domain.Realtime;

namespace MingSim.Simulation.Realtime;

/// <summary>玩家或代理想让实时世界做的一件事。</summary>
public abstract record RealtimeCommand(
    string CommandId,
    CharacterId ActorId,
    DateTime RequestedAt);

/// <summary>命令一支军队从当前省份向相邻省份行军。</summary>
public sealed record MoveArmyCommand(
    string CommandId,
    CharacterId ActorId,
    ArmyId ArmyId,
    ProvinceId DestinationId,
    DateTime RequestedAt,
    int TravelHours = 24)
    : RealtimeCommand(CommandId, ActorId, RequestedAt);

/// <summary>玩家或 Agent 提交的唯一粮运命令；Simulation 才能真正扣库存和推进运输。</summary>
public sealed record CreateShipmentCommand(
    string CommandId,
    CharacterId ActorId,
    ShipmentId ShipmentId,
    RouteId RouteId,
    long GrainQuantity,
    DateTime RequestedAt)
    : RealtimeCommand(CommandId, ActorId, RequestedAt);

/// <summary>调度器内部保存的一个未来事件。</summary>
public sealed record ScheduledSimulationEvent(
    string EventId,
    GameTime DueGameTime,
    int Phase,
    int Priority,
    long CreationSequence,
    string EventType,
    IReadOnlyDictionary<string, string> Data)
{
    /// <summary>旧原型读取时间的兼容别名；排序使用 DueGameTime。</summary>
    public DateTime DueAt => DueGameTime.Value;
}

/// <summary>实时命令入队后的结果。</summary>
public sealed record RealtimeCommandResult(
    bool Accepted,
    string CommandId,
    string Message,
    IReadOnlyList<SimulationError> Errors);

/// <summary>一次明确目标时间推进后的只读报告。</summary>
public sealed record RealtimeAdvanceResult(
    WorldState State,
    IReadOnlyList<DomainEvent> Events,
    TimeSpan GameTimeAdvanced,
    int ProcessedScheduledEvents,
    int PendingScheduledEvents,
    bool IsPaused,
    double Speed,
    string StateHash);

/// <summary>
/// 可暂停实时模拟运行时，也是实时世界的单写者。
/// </summary>
/// <remarks>
/// <para>
/// 权威 API 是 <see cref="AdvanceTo(GameTime)" />：调用方给出目标游戏时刻，
/// 运行时负责按稳定调度顺序处理其间的事件。这样 30 FPS、60 FPS 或一次性快进
/// 不会把渲染帧切分泄漏进规则结果。
/// </para>
/// <para>
/// 旧的 <see cref="Advance(TimeSpan)" /> 只保留为 UI 原型兼容适配器；它先把现实时间
/// 换算成目标 <see cref="GameTime" />，然后仍然走同一条权威推进路径。
/// </para>
/// </remarks>
public sealed class RealtimeSimulationRuntime
{
    private const double GameHoursPerRealSecondAtSpeedOne = 6.0;
    private const int DailyHeartbeatPhase = 2;
    private const string RandomState = "schema=1;streams=none";

    private readonly CapabilityAuthorizer _authorizer = new();
    private readonly PriorityQueue<
        ScheduledSimulationEvent,
        (GameTime DueGameTime, int Phase, int Priority, long CreationSequence)> _events = new();
    private readonly Dictionary<string, (string Fingerprint, RealtimeCommandResult Result)> _commandOutcomes =
        new(StringComparer.Ordinal);
    private readonly List<DomainEvent> _eventBuffer = [];
    private long _nextCreationSequence;

    public RealtimeSimulationRuntime(WorldState initialState)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        State = initialState;
        Speed = 1.0;
        ScheduleDailyHeartbeat();
    }

    /// <summary>模拟线程唯一拥有的权威状态。</summary>
    public WorldState State { get; }

    /// <summary>是否暂停游戏时间。暂停时仍可查看状态和接纳命令。</summary>
    public bool IsPaused { get; private set; }

    /// <summary>1 到 5 倍的游戏速度；它只影响兼容适配器的时间换算。</summary>
    public double Speed { get; private set; }

    /// <summary>当前状态、调度队列和随机流元数据的规范化哈希。</summary>
    public string StateHash => RealtimeStateHasher.Compute(
        State,
        GetScheduledEvents(),
        RandomState,
        _commandOutcomes
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}:{item.Value.Fingerprint}:{item.Value.Result.Accepted}:{string.Join(',', item.Value.Result.Errors.Select(error => error.Code))}"));

    public void SetPaused(bool paused) => IsPaused = paused;

    public void SetSpeed(double speed)
    {
        if (speed is < 0.25 or > 5.0)
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "实时速度必须在 0.25 到 5 倍之间。");
        }

        Speed = speed;
    }

    /// <summary>
    /// 接收一条行军命令，但不马上瞬移军队。
    /// 同一 CommandId 的重试返回第一次结果，不会再次入队。
    /// </summary>
    public RealtimeCommandResult EnqueueMoveArmy(MoveArmyCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var fingerprint = Fingerprint(command);

        if (_commandOutcomes.TryGetValue(command.CommandId, out var previous))
        {
            if (previous.Fingerprint != fingerprint)
            {
                return Reject(command.CommandId, "同一命令编号不能携带不同的命令内容。", "IDEMPOTENCY_CONFLICT");
            }

            return previous.Result;
        }

        var result = ValidateAndScheduleMove(command);
        if (result.Accepted)
        {
            // 接纳命令会改变权威 Scheduler，因此它本身就是一次最小实时提交。
            State.CommitRealtime($"command-{command.CommandId}");
        }

        _commandOutcomes.Add(command.CommandId, (fingerprint, result));
        return result;
    }

    /// <summary>
    /// 记录一批粮运计划。计划阶段立即从起点库存移入 Shipment 账本，随后由同一调度器处理出发和抵达。
    /// </summary>
    public RealtimeCommandResult EnqueueCreateShipment(CreateShipmentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var fingerprint = Fingerprint(command);

        if (_commandOutcomes.TryGetValue(command.CommandId, out var previous))
        {
            if (previous.Fingerprint != fingerprint)
            {
                return Reject(command.CommandId, "同一命令编号不能携带不同的命令内容。", "IDEMPOTENCY_CONFLICT");
            }

            return previous.Result;
        }

        var result = ValidateAndPlanShipment(command);
        if (result.Accepted)
        {
            State.CommitRealtime($"command-{command.CommandId}");
        }

        _commandOutcomes.Add(command.CommandId, (fingerprint, result));
        return result;
    }

    /// <summary>
    /// 确定性地推进到目标游戏时刻。
    /// </summary>
    public RealtimeAdvanceResult AdvanceTo(GameTime targetGameTime)
    {
        _eventBuffer.Clear();
        var startTime = State.GameTime;
        var processed = 0;

        if (!IsPaused && targetGameTime > State.GameTime)
        {
            while (State.GameTime < targetGameTime)
            {
                if (!_events.TryPeek(out _, out var nextPriority) || nextPriority.DueGameTime > targetGameTime)
                {
                    State.AdvanceTo(targetGameTime);
                    break;
                }

                // 直接跳到下一个权威边界；不把现实帧或无意义的小时切分写进规则。
                State.AdvanceTo(nextPriority.DueGameTime);
                processed += ProcessDueEvents();
            }
        }

        return Report(State.GameTime.Value - startTime.Value, processed);
    }

    /// <summary>
    /// 旧 UI 原型的兼容入口，最终仍调用 AdvanceTo。
    /// </summary>
    public RealtimeAdvanceResult Advance(TimeSpan realElapsed)
    {
        if (realElapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(realElapsed), "现实时间不能倒退。");
        }

        var gameHours = realElapsed.TotalSeconds * GameHoursPerRealSecondAtSpeedOne * Speed;
        var targetTime = new GameTime(State.GameTime.Value.AddHours(gameHours));
        return AdvanceTo(targetTime);
    }

    /// <summary>按 (时间、阶段、优先级、创建序号) 返回调度器的稳定快照。</summary>
    public IReadOnlyList<ScheduledSimulationEvent> GetScheduledEvents() =>
        _events.UnorderedItems
            .OrderBy(item => item.Priority.DueGameTime)
            .ThenBy(item => item.Priority.Phase)
            .ThenBy(item => item.Priority.Priority)
            .ThenBy(item => item.Priority.CreationSequence)
            .Select(item => item.Element)
            .ToArray();

    private RealtimeCommandResult ValidateAndScheduleMove(MoveArmyCommand command)
    {
        if (!State.Military.Armies.TryGetValue(command.ArmyId, out var army))
        {
            return Reject(command.CommandId, $"军队 {command.ArmyId} 不存在。", "ARMY_NOT_FOUND");
        }

        var authorization = _authorizer.Check(
            State,
            command.ActorId,
            GameCapability.MoveArmy,
            command.ArmyId.Value);
        if (!authorization.Allowed)
        {
            return Reject(command.CommandId, authorization.Reason, "TOOL_SCOPE_DENIED");
        }

        if (!State.Map.Contains(command.DestinationId))
        {
            return Reject(command.CommandId, $"目标地区 {command.DestinationId} 不在当前地图中。", "PROVINCE_NOT_FOUND");
        }

        if (!State.Map.IsAdjacent(army.LocationId, command.DestinationId))
        {
            return Reject(
                command.CommandId,
                $"军队当前位于 {army.LocationId}，不能直接行军到非相邻地区 {command.DestinationId}。",
                "PROVINCE_NOT_ADJACENT");
        }

        if (command.TravelHours <= 0 || command.TravelHours > 24 * 365)
        {
            return Reject(command.CommandId, "行军时间必须在 1 小时到 365 天之间。", "INVALID_TRAVEL_TIME");
        }

        var dueGameTime = new GameTime(State.GameTime.Value.AddHours(command.TravelHours));
        Schedule(
            $"army-arrival-{command.CommandId}",
            dueGameTime,
            phase: 1,
            priority: 0,
            "ArmyArrival",
            new Dictionary<string, string>
            {
                ["army_id"] = command.ArmyId.Value,
                ["destination_id"] = command.DestinationId.Value,
                ["actor_id"] = command.ActorId.Value,
            });

        _eventBuffer.Add(CreateEvent(
            command.CommandId,
            "ArmyMarchStarted",
            $"军队 {army.Name} 已从 {army.LocationId} 出发，预计 {dueGameTime.Value:yyyy-MM-dd HH:mm} 抵达 {command.DestinationId}。",
            ("army_id", command.ArmyId.Value),
            ("from", army.LocationId.Value),
            ("to", command.DestinationId.Value),
            ("due_at", dueGameTime.ToString())));

        return new RealtimeCommandResult(true, command.CommandId, "行军命令已进入实时调度器。", []);
    }

    private RealtimeCommandResult ValidateAndPlanShipment(CreateShipmentCommand command)
    {
        if (command.GrainQuantity <= 0)
        {
            return Reject(command.CommandId, "运输粮食数量必须为正数。", "INVALID_GRAIN_QUANTITY");
        }

        if (State.Logistics.Shipments.ContainsKey(command.ShipmentId))
        {
            return Reject(command.CommandId, $"运输单 {command.ShipmentId} 已经存在。", "SHIPMENT_EXISTS");
        }

        if (!State.Logistics.Routes.TryGetValue(command.RouteId, out var route))
        {
            return Reject(command.CommandId, $"路线 {command.RouteId} 不存在。", "ROUTE_NOT_FOUND");
        }

        var authorization = _authorizer.Check(
            State,
            command.ActorId,
            GameCapability.PlanLogistics,
            command.RouteId.Value);
        if (!authorization.Allowed)
        {
            return Reject(command.CommandId, authorization.Reason, "TOOL_SCOPE_DENIED");
        }

        var source = State.Logistics.Stockpiles[route.FromStockpileId];
        var destination = State.Logistics.Stockpiles[route.ToStockpileId];
        if (!GrainLogisticsRules.HasEnoughSourceGrain(source, command.GrainQuantity))
        {
            return Reject(command.CommandId, "起点库存粮食不足。", "INSUFFICIENT_GRAIN");
        }

        if (!GrainLogisticsRules.FitsRouteCapacity(State.Logistics, route, command.GrainQuantity))
        {
            return Reject(command.CommandId, "路线在途容量不足。", "ROUTE_CAPACITY_EXCEEDED");
        }

        if (!GrainLogisticsRules.FitsDestinationCapacity(State.Logistics, destination, command.GrainQuantity))
        {
            return Reject(command.CommandId, "终点库存容量不足。", "DESTINATION_CAPACITY_EXCEEDED");
        }

        if (!source.TryTakeGrain(command.GrainQuantity))
        {
            return Reject(command.CommandId, "起点库存扣减失败。", "INSUFFICIENT_GRAIN");
        }

        var shipment = new ShipmentState(
            command.ShipmentId,
            route.Id,
            command.GrainQuantity,
            State.GameTime);
        State.Logistics.AddShipment(shipment);
        Schedule(
            $"shipment-departure-{shipment.Id.Value}",
            State.GameTime,
            phase: 0,
            priority: 1,
            "ShipmentDeparture",
            new Dictionary<string, string> { ["shipment_id"] = shipment.Id.Value });
        _eventBuffer.Add(CreateEvent(
            command.CommandId,
            "ShipmentPlanned",
            $"粮运 {shipment.Id} 已计划：{source.Id} -> {destination.Id}，数量 {command.GrainQuantity}。",
            ("shipment_id", shipment.Id.Value),
            ("route_id", route.Id.Value),
            ("grain", command.GrainQuantity.ToString())));

        return new RealtimeCommandResult(true, command.CommandId, "粮运计划已记录，等待调度出发。", []);
    }

    private int ProcessDueEvents()
    {
        var processed = 0;
        while (_events.TryPeek(out _, out var priority) && priority.DueGameTime <= State.GameTime)
        {
            var scheduled = _events.Dequeue();
            ApplyScheduledEvent(scheduled);
            State.CommitRealtime($"realtime-{scheduled.CreationSequence}");
            processed++;
        }

        return processed;
    }

    private void ApplyScheduledEvent(ScheduledSimulationEvent scheduled)
    {
        if (scheduled.EventType == "DailyHeartbeat")
        {
            _eventBuffer.Add(CreateEvent(
                scheduled.EventId,
                "DailySimulationTick",
                $"世界完成 {State.CurrentTime:yyyy-MM-dd} 的日常推进。",
                ("tick", "daily")));
            ScheduleDailyHeartbeat();
            return;
        }

        if (scheduled.EventType == "ShipmentDeparture")
        {
            ApplyShipmentDeparture(scheduled);
            return;
        }

        if (scheduled.EventType == "ShipmentArrival")
        {
            ApplyShipmentArrival(scheduled);
            return;
        }

        if (scheduled.EventType != "ArmyArrival")
        {
            _eventBuffer.Add(CreateEvent(
                scheduled.EventId,
                "UnknownScheduledEvent",
                $"忽略未知调度事件 {scheduled.EventType}，世界状态没有被未知数据直接修改。",
                ("event_type", scheduled.EventType)));
            return;
        }

        var armyId = new ArmyId(scheduled.Data["army_id"]);
        var destination = new ProvinceId(scheduled.Data["destination_id"]);
        if (!State.Military.Armies.TryGetValue(armyId, out var army))
        {
            _eventBuffer.Add(CreateEvent(
                scheduled.EventId,
                "ArmyArrivalCancelled",
                $"军队 {armyId} 已不存在，抵达事件取消。",
                ("army_id", armyId.Value)));
            return;
        }

        var previousLocation = army.LocationId;
        army.ArriveAt(destination);
        _eventBuffer.Add(CreateEvent(
            scheduled.EventId,
            "ArmyArrived",
            $"军队 {army.Name} 已从 {previousLocation} 抵达 {destination}。",
            ("army_id", armyId.Value),
            ("from", previousLocation.Value),
            ("to", destination.Value)));
    }

    private void ApplyShipmentDeparture(ScheduledSimulationEvent scheduled)
    {
        var shipmentId = new ShipmentId(scheduled.Data["shipment_id"]);
        if (!State.Logistics.Shipments.TryGetValue(shipmentId, out var shipment) ||
            !State.Logistics.Routes.TryGetValue(shipment.RouteId, out var route))
        {
            _eventBuffer.Add(CreateEvent(
                scheduled.EventId,
                "ShipmentDepartureCancelled",
                $"粮运 {shipmentId} 缺少运输单或路线，出发已取消。",
                ("shipment_id", shipmentId.Value)));
            return;
        }

        shipment.MarkInTransit(State.GameTime);
        var arrivalAt = State.GameTime.Add(TimeSpan.FromHours(route.TravelHours));
        Schedule(
            $"shipment-arrival-{shipment.Id.Value}",
            arrivalAt,
            phase: 1,
            priority: 1,
            "ShipmentArrival",
            new Dictionary<string, string> { ["shipment_id"] = shipment.Id.Value });
        _eventBuffer.Add(CreateEvent(
            scheduled.EventId,
            "ShipmentDeparted",
            $"粮运 {shipment.Id} 已出发，预计 {arrivalAt.Value:yyyy-MM-dd HH:mm} 抵达。",
            ("shipment_id", shipment.Id.Value),
            ("due_at", arrivalAt.ToString())));
    }

    private void ApplyShipmentArrival(ScheduledSimulationEvent scheduled)
    {
        var shipmentId = new ShipmentId(scheduled.Data["shipment_id"]);
        if (!State.Logistics.Shipments.TryGetValue(shipmentId, out var shipment) ||
            !State.Logistics.Routes.TryGetValue(shipment.RouteId, out var route))
        {
            _eventBuffer.Add(CreateEvent(
                scheduled.EventId,
                "ShipmentArrivalCancelled",
                $"粮运 {shipmentId} 缺少运输单或路线，抵达已取消。",
                ("shipment_id", shipmentId.Value)));
            return;
        }

        var destination = State.Logistics.Stockpiles[route.ToStockpileId];
        var (delivered, loss) = GrainLogisticsRules.CalculateArrival(route, shipment.GrainQuantity);
        if (!destination.TryStoreGrain(delivered))
        {
            _eventBuffer.Add(CreateEvent(
                scheduled.EventId,
                "ShipmentArrivalBlocked",
                $"粮运 {shipment.Id} 抵达时终点容量不足，货物仍保持在途。",
                ("shipment_id", shipment.Id.Value),
                ("destination_id", destination.Id.Value)));
            Schedule(
                $"shipment-arrival-retry-{shipment.Id.Value}-{State.GameTime.Value.Ticks}",
                State.GameTime.Add(TimeSpan.FromHours(1)),
                phase: 1,
                priority: 1,
                "ShipmentArrival",
                new Dictionary<string, string> { ["shipment_id"] = shipment.Id.Value });
            return;
        }

        shipment.MarkArrived(State.GameTime, delivered, loss);
        _eventBuffer.Add(CreateEvent(
            scheduled.EventId,
            "ShipmentArrived",
            $"粮运 {shipment.Id} 已抵达 {destination.Id}：交付 {delivered}，损耗 {loss}。",
            ("shipment_id", shipment.Id.Value),
            ("destination_id", destination.Id.Value),
            ("delivered_grain", delivered.ToString()),
            ("loss_grain", loss.ToString())));
    }

    private void ScheduleDailyHeartbeat()
    {
        var nextMidnight = State.CurrentTime.Date.AddDays(1);
        Schedule(
            $"daily-heartbeat-{nextMidnight:yyyyMMdd}",
            new GameTime(nextMidnight),
            DailyHeartbeatPhase,
            priority: 0,
            "DailyHeartbeat",
            new Dictionary<string, string>());
    }

    private void Schedule(
        string eventId,
        GameTime dueGameTime,
        int phase,
        int priority,
        string eventType,
        IReadOnlyDictionary<string, string> data)
    {
        var creationSequence = _nextCreationSequence++;
        var scheduled = new ScheduledSimulationEvent(
            eventId,
            dueGameTime,
            phase,
            priority,
            creationSequence,
            eventType,
            new Dictionary<string, string>(data, StringComparer.Ordinal));
        _events.Enqueue(
            scheduled,
            (dueGameTime, phase, priority, creationSequence));
    }

    private RealtimeAdvanceResult Report(TimeSpan advanced, int processed)
    {
        return new RealtimeAdvanceResult(
            State.Clone(),
            _eventBuffer.ToArray(),
            advanced,
            processed,
            _events.Count,
            IsPaused,
            Speed,
            StateHash);
    }

    private DomainEvent CreateEvent(
        string eventId,
        string eventType,
        string description,
        params (string Key, string Value)[] data)
    {
        return new DomainEvent(
            eventId,
            State.Id,
            State.TurnNumber,
            eventType,
            description,
            data.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            State.CurrentTime);
    }

    private static string Fingerprint(MoveArmyCommand command) =>
        string.Join(
            "|",
            command.ActorId.Value,
            command.ArmyId.Value,
            command.DestinationId.Value,
            command.TravelHours,
            command.RequestedAt.ToUniversalTime().Ticks);

    private static string Fingerprint(CreateShipmentCommand command) =>
        string.Join(
            "|",
            command.ActorId.Value,
            command.ShipmentId.Value,
            command.RouteId.Value,
            command.GrainQuantity,
            command.RequestedAt.ToUniversalTime().Ticks);

    private static RealtimeCommandResult Reject(
        string commandId,
        string message,
        string errorCode) =>
        new(false, commandId, message, [new SimulationError(errorCode, message)]);
}
