using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Errors;
using MingSim.Domain.Events;
using MingSim.Domain.Military;
using MingSim.Domain.Realtime;

namespace MingSim.Simulation.Realtime;

/// <summary>所有实时写请求都使用 UTC 诊断时间和预期世界版本。</summary>
public abstract record RealtimeCommand(
    string CommandId,
    CharacterId ActorId,
    DateTimeOffset SubmittedAt,
    long ExpectedWorldVersion);

/// <summary>命令一支军队从当前省份向相邻省份行军。</summary>
public sealed record MoveArmyCommand(
    string CommandId,
    CharacterId ActorId,
    ArmyId ArmyId,
    ProvinceId DestinationId,
    DateTimeOffset SubmittedAt,
    long ExpectedWorldVersion,
    int TravelHours = 24)
    : RealtimeCommand(CommandId, ActorId, SubmittedAt, ExpectedWorldVersion);

/// <summary>玩家或 Agent 提交的唯一粮运命令。</summary>
public sealed record CreateShipmentCommand(
    string CommandId,
    CharacterId ActorId,
    ShipmentId ShipmentId,
    RouteId RouteId,
    long GrainQuantity,
    DateTimeOffset SubmittedAt,
    long ExpectedWorldVersion)
    : RealtimeCommand(CommandId, ActorId, SubmittedAt, ExpectedWorldVersion);

/// <summary>只读命令提交回执；实际接纳在 Simulation 安全点完成。</summary>
public sealed record RealtimeCommandReceipt(
    string CommandId,
    bool Queued,
    string Message,
    IReadOnlyList<SimulationError> Errors);

/// <summary>一次已处理的命令结果。</summary>
public sealed record RealtimeCommandResult(
    bool Accepted,
    string CommandId,
    string Message,
    IReadOnlyList<SimulationError> Errors,
    long IngressSequence,
    GameTime AcceptedGameTime,
    long ResultingWorldVersion);

/// <summary>一次明确目标时间推进后的只读报告。</summary>
public sealed record RealtimeAdvanceResult(
    bool Succeeded,
    RealtimeReadModel ReadModel,
    IReadOnlyList<DomainEvent> Events,
    IReadOnlyList<RealtimeCommandResult> CommandResults,
    IReadOnlyList<SimulationError> Errors,
    TimeSpan GameTimeAdvanced,
    int ProcessedScheduledEvents,
    int PendingScheduledEvents,
    bool IsPaused,
    double Speed,
    string StateHash);

/// <summary>一个最小版本化的实时快照，包含恢复未来行为所需的全部状态。</summary>
public sealed class RealtimeSnapshot
{
    internal RealtimeSnapshot(
        int schemaVersion,
        WorldState state,
        IReadOnlyList<ScheduledSimulationEvent> scheduledEvents,
        IReadOnlyList<RealtimeCommand> pendingCommands,
        long nextCreationSequence,
        long nextIngressSequence,
        IReadOnlyList<CommandOutcome> commandOutcomes,
        string randomState,
        IReadOnlyList<DomainEvent> outboxEvents,
        decimal realGameTickRemainder,
        string stateHash,
        GameTime initialGameTime,
        long initialWorldVersion,
        long processedScheduledEventCount,
        bool isPaused,
        double speed)
    {
        SchemaVersion = schemaVersion;
        State = state;
        ScheduledEvents = scheduledEvents;
        PendingCommands = pendingCommands;
        NextCreationSequence = nextCreationSequence;
        NextIngressSequence = nextIngressSequence;
        CommandOutcomes = commandOutcomes;
        RandomState = randomState;
        OutboxEvents = outboxEvents;
        RealGameTickRemainder = realGameTickRemainder;
        InitialGameTime = initialGameTime;
        InitialWorldVersion = initialWorldVersion;
        ProcessedScheduledEventCount = processedScheduledEventCount;
        IsPaused = isPaused;
        Speed = speed;
        StateHash = stateHash;
    }

    public int SchemaVersion { get; }

    internal WorldState State { get; }

    internal IReadOnlyList<ScheduledSimulationEvent> ScheduledEvents { get; }

    internal IReadOnlyList<RealtimeCommand> PendingCommands { get; }

    internal long NextCreationSequence { get; }

    internal long NextIngressSequence { get; }

    internal IReadOnlyList<CommandOutcome> CommandOutcomes { get; }

    internal string RandomState { get; }

    internal IReadOnlyList<DomainEvent> OutboxEvents { get; }

    internal decimal RealGameTickRemainder { get; }

    internal GameTime InitialGameTime { get; }

    internal long InitialWorldVersion { get; }

    internal long ProcessedScheduledEventCount { get; }

    internal bool IsPaused { get; }

    internal double Speed { get; }

    public string StateHash { get; }
}

/// <summary>
/// 可暂停实时模拟运行时，也是实时世界的单写者。
/// </summary>
/// <remarks>
/// 外部线程只能把不可变 Command 放入收件箱。运行时在安全点复制出候选 State、
/// Scheduler、Outcome 和 outbox；全部规则成功后才一次性替换内部引用。
/// </remarks>
public sealed class RealtimeSimulationRuntime
{
    private const double GameHoursPerRealSecondAtSpeedOne = 6.0;
    private const int DailyHeartbeatPhase = 2;
    private const string RandomState = "schema=1;streams=none";

    private readonly CapabilityAuthorizer _authorizer = new();
    private readonly ConcurrentQueue<RealtimeCommand> _inbox = new();
    private readonly object _writerGate = new();
    private WorldState _state;
    private List<ScheduledSimulationEvent> _scheduledEvents;
    private Dictionary<string, CommandOutcome> _commandOutcomes;
    private List<DomainEvent> _outboxEvents;
    private long _nextCreationSequence;
    private long _nextIngressSequence;
    private decimal _realGameTickRemainder;
    private readonly GameTime _initialGameTime;
    private readonly long _initialWorldVersion;
    private long _processedScheduledEventCount;
    private bool _isPaused;
    private double _speed;
    private string _randomState = RandomState;
    private static readonly IReadOnlyList<SimulationError> NoErrors = ReadOnlyCollection<SimulationError>.Empty;

    public RealtimeSimulationRuntime(WorldState initialState)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        _state = initialState.Clone();
        _initialGameTime = _state.GameTime;
        _initialWorldVersion = _state.WorldVersion;
        _scheduledEvents = [];
        _commandOutcomes = new(StringComparer.Ordinal);
        _outboxEvents = [];
        _speed = 1.0;
        var initialWork = new WorkingCopy(_state, _scheduledEvents, _commandOutcomes, _outboxEvents, _nextCreationSequence, _nextIngressSequence, 0);
        ScheduleDailyHeartbeat(_state, _scheduledEvents, initialWork);
        _nextCreationSequence = initialWork.NextCreationSequence;
    }

    private RealtimeSimulationRuntime(RealtimeSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != RealtimeSnapshotSchema.Version)
        {
            throw new InvalidDataException($"不支持实时快照版本 {snapshot.SchemaVersion}。");
        }

        _state = snapshot.State.Clone();
        _initialGameTime = snapshot.InitialGameTime;
        _initialWorldVersion = snapshot.InitialWorldVersion;
        _scheduledEvents = snapshot.ScheduledEvents.ToList();
        foreach (var command in snapshot.PendingCommands)
        {
            _inbox.Enqueue(command);
        }
        _commandOutcomes = snapshot.CommandOutcomes.ToDictionary(item => item.CommandId, StringComparer.Ordinal);
        _outboxEvents = snapshot.OutboxEvents.ToList();
        _nextCreationSequence = snapshot.NextCreationSequence;
        _nextIngressSequence = snapshot.NextIngressSequence;
        _realGameTickRemainder = snapshot.RealGameTickRemainder;
        _processedScheduledEventCount = snapshot.ProcessedScheduledEventCount;
        _isPaused = snapshot.IsPaused;
        _speed = snapshot.Speed;
        ValidateSpeed(_speed);
        _randomState = snapshot.RandomState;

        var actualHash = ComputeStateHash();
        if (!StringComparer.Ordinal.Equals(actualHash, snapshot.StateHash))
        {
            throw new InvalidDataException("实时快照的 canonical state hash 校验失败。");
        }
    }

    /// <summary>只读的 UI/调试视图；不暴露 WorldState 或可写领域对象。</summary>
    public RealtimeReadModel ReadModel
    {
        get
        {
            lock (_writerGate)
            {
                return BuildReadModel();
            }
        }
    }

    public bool IsPaused => _isPaused;

    public double Speed => _speed;

    public string StateHash
    {
        get
        {
            lock (_writerGate)
            {
                return ComputeStateHash();
            }
        }
    }

    public IReadOnlyList<DomainEvent> OutboxEvents
    {
        get
        {
            lock (_writerGate)
            {
                return new ReadOnlyCollection<DomainEvent>(_outboxEvents.ToArray());
            }
        }
    }

    public IReadOnlyList<CommandOutcome> CommandOutcomes
    {
        get
        {
            lock (_writerGate)
            {
                return new ReadOnlyCollection<CommandOutcome>(_commandOutcomes.Values.OrderBy(item => item.IngressSequence).ToArray());
            }
        }
    }

    public RealtimeCommandReceipt EnqueueMoveArmy(MoveArmyCommand command) => Enqueue(command);

    public RealtimeCommandReceipt EnqueueCreateShipment(CreateShipmentCommand command) => Enqueue(command);

    public void SetPaused(bool paused)
    {
        lock (_writerGate)
        {
            _isPaused = paused;
        }
    }

    public void SetSpeed(double speed)
    {
        ValidateSpeed(speed);
        if (double.IsNaN(speed) || double.IsInfinity(speed) || speed is < 0.25 or > 5.0)
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "实时速度必须是 0.25 到 5 倍之间的有限数。");
        }

        lock (_writerGate)
        {
            _speed = speed;
        }
    }

    /// <summary>创建一份可校验、可恢复的完整实时快照。</summary>
    private static void ValidateSpeed(double speed)
    {
        if (double.IsNaN(speed) || double.IsInfinity(speed) || speed is < 0.25 or > 5.0)
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "实时速度必须是 0.25 到 5 倍之间的有限数。");
        }
    }

    public RealtimeSnapshot CaptureSnapshot()
    {
        lock (_writerGate)
        {
            return new RealtimeSnapshot(
                RealtimeSnapshotSchema.Version,
                _state.Clone(),
                _scheduledEvents.ToArray(),
                _inbox.ToArray(),
                _nextCreationSequence,
                _nextIngressSequence,
                CommandOutcomes,
                _randomState,
                _outboxEvents.ToArray(),
                _realGameTickRemainder,
                ComputeStateHash(),
                _initialGameTime,
                _initialWorldVersion,
                _processedScheduledEventCount,
                _isPaused,
                _speed);
        }
    }

    public static RealtimeSimulationRuntime Restore(RealtimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new RealtimeSimulationRuntime(snapshot);
    }

    /// <summary>确定性地推进到目标游戏时刻；过去时刻只返回结构化错误。</summary>
    public RealtimeAdvanceResult AdvanceTo(GameTime targetGameTime)
    {
        lock (_writerGate)
        {
            return AdvanceToCore(targetGameTime);
        }
    }

    private RealtimeAdvanceResult AdvanceToCore(GameTime targetGameTime)
    {
        var errors = new List<SimulationError>();
        var startTime = _state.GameTime;

        if (targetGameTime < _state.GameTime)
        {
            errors.Add(new SimulationError("TARGET_GAME_TIME_IN_PAST", "目标游戏时间不能早于当前权威时间。"));
            return Report(false, [], [], errors, startTime, 0);
        }

        var events = new List<DomainEvent>();
        var commandResults = DrainInbox(events);

        if (!_isPaused)
        {
            var processed = 0;
            while (true)
            {
                var next = _scheduledEvents
                    .OrderBy(item => item.DueGameTime)
                    .ThenBy(item => item.Phase)
                    .ThenBy(item => item.Priority)
                    .ThenBy(item => item.CreationSequence)
                    .FirstOrDefault();

                if (next is not null && next.DueGameTime <= targetGameTime)
                {
                    if (!TryCommitScheduledEvent(next, events, out var eventError))
                    {
                        errors.Add(eventError!);
                        break;
                    }

                    processed++;
                    continue;
                }

                if (_state.GameTime < targetGameTime)
                {
                    CommitTimeOnly(targetGameTime, events);
                }

                break;
            }

            return Report(errors.Count == 0, events, commandResults, errors, startTime, processed);
        }

        return Report(true, events, commandResults, errors, startTime, 0);
    }

    /// <summary>
    /// UI 原型兼容入口。用十进制定点余数累计游戏 tick，避免每帧独立舍入。
    /// </summary>
    public RealtimeAdvanceResult Advance(TimeSpan realElapsed)
    {
        if (realElapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(realElapsed), "现实时间不能倒退。");
        }

        lock (_writerGate)
        {
            var gameTicks = ((decimal)realElapsed.Ticks / TimeSpan.TicksPerSecond) *
                6m * (decimal)_speed * TimeSpan.TicksPerHour;
            if (_isPaused)
            {
                return AdvanceToCore(_state.GameTime);
            }

            _realGameTickRemainder += gameTicks;
            var wholeTicks = decimal.Truncate(_realGameTickRemainder);
            _realGameTickRemainder -= wholeTicks;
            var target = new GameTime(_state.GameTime.Value.AddTicks(checked((long)wholeTicks)));
            return AdvanceToCore(target);
        }
    }

    private RealtimeCommandReceipt Enqueue(RealtimeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.CommandId) || command.CommandId.Length > 128)
        {
                return new RealtimeCommandReceipt(
                    command.CommandId,
                    false,
                    "CommandId 必须为 1 到 128 个非空字符。",
                    new ReadOnlyCollection<SimulationError>([new SimulationError("INVALID_COMMAND_ID", "CommandId 必须为 1 到 128 个非空字符。")]));
        }

        if (command.SubmittedAt.Offset != TimeSpan.Zero)
        {
                return new RealtimeCommandReceipt(
                    command.CommandId,
                    false,
                    "命令 SubmittedAt 必须使用 UTC。",
                    new ReadOnlyCollection<SimulationError>([new SimulationError("NON_UTC_COMMAND_TIME", "命令 SubmittedAt 必须使用 UTC。")]));
        }

        _inbox.Enqueue(command);
        return new RealtimeCommandReceipt(command.CommandId, true, "命令已进入 Simulation 收件箱。", ReadOnlyCollection<SimulationError>.Empty);
    }

    private List<RealtimeCommandResult> DrainInbox(List<DomainEvent> events)
    {
        var results = new List<RealtimeCommandResult>();
        while (_inbox.TryDequeue(out var command))
        {
            var candidate = CreateWorkingCopy();
            var ingressSequence = candidate.NextIngressSequence++;
            var fingerprint = Fingerprint(command);
            if (_commandOutcomes.TryGetValue(command.CommandId, out var previous))
            {
                var duplicate = StringComparer.Ordinal.Equals(previous.Fingerprint, fingerprint);
                var duplicateResult = duplicate
                    ? new RealtimeCommandResult(previous.Accepted, command.CommandId, "命令已按幂等记录处理。",
                        previous.ErrorCodes.Select(code => new SimulationError(code, code)).ToArray(), previous.IngressSequence,
                        previous.AcceptedGameTime, previous.ResultingWorldVersion)
                    : Reject(command.CommandId, "同一命令编号不能携带不同的命令内容。", "IDEMPOTENCY_CONFLICT", ingressSequence,
                        candidate.State.GameTime, candidate.State.WorldVersion);
                var duplicateEvents = new List<DomainEvent>();
                duplicateEvents.Add(CreateEvent(candidate.State, command.CommandId, duplicate ? "CommandDeduplicated" : "CommandRejected",
                    candidate.State.GameTime, ("ingress_sequence", ingressSequence.ToString()), ("command_id", command.CommandId)));
                candidate.Outbox.Add(duplicateEvents[^1]);
                CommitWorkingCopy(candidate);
                events.AddRange(duplicateEvents);
                results.Add(duplicateResult);
                continue;
            }

            var result = ValidateAndApplyCommand(candidate, command, ingressSequence, fingerprint, events);
            CommitWorkingCopy(candidate);
            results.Add(result);
        }

        return results;
    }

    private RealtimeCommandResult ValidateAndApplyCommand(
        WorkingCopy candidate,
        RealtimeCommand command,
        long ingressSequence,
        string fingerprint,
        List<DomainEvent> events)
    {
        var acceptedAt = candidate.State.GameTime;
        RealtimeCommandResult result;
        if (command.ExpectedWorldVersion != candidate.State.WorldVersion)
        {
            result = Reject(command.CommandId, "命令基于过期世界版本。", "STATE_VERSION_CONFLICT", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }
        else
        {
            result = command switch
            {
                MoveArmyCommand move => ApplyMove(candidate, move, ingressSequence, acceptedAt, events),
                CreateShipmentCommand shipment => ApplyShipment(candidate, shipment, ingressSequence, acceptedAt, events),
                _ => Reject(command.CommandId, "未知实时命令类型。", "UNKNOWN_COMMAND", ingressSequence, acceptedAt, candidate.State.WorldVersion),
            };
        }

        var commitId = result.Accepted ? $"command-{ingressSequence}" : null;
        var resultingVersion = candidate.State.WorldVersion;
        if (result.Accepted)
        {
            resultingVersion = CalculateWorldVersion(candidate, acceptedCommand: true);
            candidate.State.CommitRealtime(resultingVersion, commitId!);
            result = result with { ResultingWorldVersion = resultingVersion };
        }

        candidate.Outcomes[command.CommandId] = new CommandOutcome(
            command.CommandId,
            fingerprint,
            result.Accepted,
            result.Errors.Select(error => error.Code),
            ingressSequence,
            acceptedAt,
            command.ExpectedWorldVersion,
            result.ResultingWorldVersion,
            commitId);
        events.Add(CreateCommandEvent(candidate.State, command, result, ingressSequence, acceptedAt));
        candidate.Outbox.Add(events[^1]);
        return result;
    }

    private RealtimeCommandResult ApplyMove(
        WorkingCopy candidate,
        MoveArmyCommand command,
        long ingressSequence,
        GameTime acceptedAt,
        List<DomainEvent> events)
    {
        if (!IsValidId(command.ActorId.Value) || !IsValidId(command.ArmyId.Value) || !IsValidId(command.DestinationId.Value))
        {
            return Reject(command.CommandId, "命令中的对象编号不合法。", "INVALID_OBJECT_ID", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (!candidate.State.Military.Armies.TryGetValue(command.ArmyId, out var army))
        {
            return Reject(command.CommandId, $"军队 {command.ArmyId} 不存在。", "ARMY_NOT_FOUND", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (candidate.State.Movements.ContainsKey(command.ArmyId))
        {
            return Reject(command.CommandId, "该军队已经在执行另一条行军。", "ARMY_ALREADY_IN_TRANSIT", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        var authorization = _authorizer.Check(candidate.State, command.ActorId, GameCapability.MoveArmy, command.ArmyId.Value);
        if (!authorization.Allowed)
        {
            return Reject(command.CommandId, authorization.Reason, "TOOL_SCOPE_DENIED", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (command.TravelHours <= 0 || command.TravelHours > 24 * 365)
        {
            return Reject(command.CommandId, "行军时间必须在 1 小时到 365 天之间。", "INVALID_TRAVEL_TIME", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (!candidate.State.Map.Contains(command.DestinationId) || !candidate.State.Map.IsAdjacent(army.LocationId, command.DestinationId))
        {
            return Reject(command.CommandId, "目标地区必须存在且与军队当前地区相邻。", "PROVINCE_NOT_ADJACENT", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        var due = acceptedAt.Add(TimeSpan.FromHours(command.TravelHours));
        var actionId = $"move-{command.CommandId}";
        var routeFingerprint = $"{army.LocationId.Value}>{command.DestinationId.Value}";
        candidate.State.SetMovement(new MovementState(actionId, army.Id, army.LocationId, command.DestinationId, due, routeFingerprint));
        Schedule(candidate, $"army-arrival-{command.CommandId}", due, 1, 0, "ArmyArrival",
            new Dictionary<string, string>
            {
                ["action_id"] = actionId,
                ["army_id"] = army.Id.Value,
                ["origin"] = army.LocationId.Value,
                ["destination_id"] = command.DestinationId.Value,
                ["route_fingerprint"] = routeFingerprint,
            }, command.CommandId);
        events.Add(CreateEvent(candidate.State, command.CommandId, "ArmyMarchStarted", acceptedAt,
            ("army_id", army.Id.Value), ("from", army.LocationId.Value), ("to", command.DestinationId.Value), ("due_at", due.ToString())));
        candidate.Outbox.Add(events[^1]);
        return Accepted(command.CommandId, "行军命令已接纳。", ingressSequence, acceptedAt, candidate.State.WorldVersion);
    }

    private RealtimeCommandResult ApplyShipment(
        WorkingCopy candidate,
        CreateShipmentCommand command,
        long ingressSequence,
        GameTime acceptedAt,
        List<DomainEvent> events)
    {
        if (!IsValidId(command.ActorId.Value) || !IsValidId(command.ShipmentId.Value) || !IsValidId(command.RouteId.Value))
        {
            return Reject(command.CommandId, "命令中的对象编号不合法。", "INVALID_OBJECT_ID", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (command.GrainQuantity <= 0)
        {
            return Reject(command.CommandId, "运输粮食数量必须为正数。", "INVALID_GRAIN_QUANTITY", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (candidate.State.Logistics.Shipments.ContainsKey(command.ShipmentId))
        {
            return Reject(command.CommandId, "运输单编号已经存在。", "SHIPMENT_ALREADY_EXISTS", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (!candidate.State.Logistics.Routes.TryGetValue(command.RouteId, out var route))
        {
            return Reject(command.CommandId, "路线不存在。", "ROUTE_NOT_FOUND", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        var authorization = _authorizer.Check(candidate.State, command.ActorId, GameCapability.PlanLogistics, command.RouteId.Value);
        if (!authorization.Allowed)
        {
            return Reject(command.CommandId, authorization.Reason, "TOOL_SCOPE_DENIED", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        var source = candidate.State.Logistics.Stockpiles[route.FromStockpileId];
        var destination = candidate.State.Logistics.Stockpiles[route.ToStockpileId];
        if (!GrainLogisticsRules.HasEnoughSourceGrain(source, command.GrainQuantity) ||
            !GrainLogisticsRules.FitsRouteCapacity(candidate.State.Logistics, route, command.GrainQuantity) ||
            !GrainLogisticsRules.FitsDestinationCapacity(candidate.State.Logistics, destination, command.GrainQuantity))
        {
            return Reject(command.CommandId, "粮运前置条件不满足。", "SHIPMENT_PRECONDITION_FAILED", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (!source.TryTakeGrain(command.GrainQuantity))
        {
            return Reject(command.CommandId, "起点库存扣减失败。", "INSUFFICIENT_GRAIN", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        candidate.State.Logistics.AddShipment(new ShipmentState(command.ShipmentId, route.Id, command.GrainQuantity, acceptedAt));
        Schedule(candidate, $"shipment-departure-{command.ShipmentId.Value}", acceptedAt, 0, 1, "ShipmentDeparture",
            new Dictionary<string, string> { ["shipment_id"] = command.ShipmentId.Value }, command.CommandId);
        events.Add(CreateEvent(candidate.State, command.CommandId, "ShipmentPlanned", acceptedAt,
            ("shipment_id", command.ShipmentId.Value), ("route_id", route.Id.Value), ("grain", command.GrainQuantity.ToString())));
        candidate.Outbox.Add(events[^1]);
        return Accepted(command.CommandId, "粮运计划已接纳。", ingressSequence, acceptedAt, candidate.State.WorldVersion);
    }

    private bool TryCommitScheduledEvent(ScheduledSimulationEvent scheduled, List<DomainEvent> events, out SimulationError? error)
    {
        var candidate = CreateWorkingCopy();
        candidate.Scheduled.Remove(scheduled);
        var candidateEvents = new List<DomainEvent>();
        try
        {
            candidate.State.AdvanceTo(scheduled.DueGameTime);
            ApplyScheduledEvent(candidate, scheduled, candidateEvents);
            var errors = new InvariantChecker().Check(candidate.State);
            if (errors.Count > 0)
            {
                error = errors[0];
                return false;
            }

            candidate.ProcessedScheduledEventCount++;
            candidate.State.CommitRealtime(CalculateWorldVersion(candidate), $"event-{scheduled.CreationSequence}");
            CommitWorkingCopy(candidate);
            events.AddRange(candidateEvents);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = new SimulationError("SCHEDULED_EVENT_FAILED", exception.Message);
            return false;
        }
    }

    private void ApplyScheduledEvent(WorkingCopy candidate, ScheduledSimulationEvent scheduled, List<DomainEvent> events)
    {
        if (scheduled.EventType == "DailyHeartbeat")
        {
            events.Add(CreateEvent(candidate.State, scheduled.EventId, "DailySimulationTick", candidate.State.GameTime, ("tick", "daily")));
            candidate.Outbox.Add(events[^1]);
            ScheduleDailyHeartbeat(candidate.State, candidate.Scheduled, candidate);
            return;
        }

        if (scheduled.EventType == "ArmyArrival")
        {
            var armyId = new ArmyId(scheduled.Data["army_id"]);
            var movement = candidate.State.Movements[armyId];
            if (movement.ActionId != scheduled.Data["action_id"] || movement.Origin.Value != scheduled.Data["origin"] ||
                movement.Destination.Value != scheduled.Data["destination_id"] || movement.RouteFingerprint != scheduled.Data["route_fingerprint"] ||
                !candidate.State.Map.IsAdjacent(movement.Origin, movement.Destination) ||
                !candidate.State.Military.Armies.TryGetValue(armyId, out var army) || army.LocationId != movement.Origin)
            {
                throw new InvalidOperationException("军队抵达事件的 action、origin 或 route 复核失败。");
            }

            army.ArriveAt(movement.Destination);
            candidate.State.RemoveMovement(armyId);
            events.Add(CreateEvent(candidate.State, scheduled.EventId, "ArmyArrived", candidate.State.GameTime,
                ("army_id", armyId.Value), ("from", movement.Origin.Value), ("to", movement.Destination.Value)));
            candidate.Outbox.Add(events[^1]);
            return;
        }

        throw new InvalidOperationException($"未知调度事件类型 {scheduled.EventType}。");
    }

    private void CommitTimeOnly(GameTime target, List<DomainEvent> events)
    {
        var candidate = CreateWorkingCopy();
        var previous = candidate.State.GameTime;
        candidate.State.AdvanceTo(target);
        var timeEvent = CreateEvent(candidate.State, $"time-{target.Value.UtcTicks}", "TimeAdvanced", target,
            ("from", previous.ToString()), ("to", target.ToString()));
        candidate.Outbox.Add(timeEvent);
        events.Add(timeEvent);
        // 纯时间提交同样原子更新 GameTime、WorldVersion、CommitId 和 Scheduler；
        // 帧切分只影响余数累计，不影响这次明确目标提交的结果。
        candidate.State.CommitRealtime(CalculateWorldVersion(candidate), $"time-{target.Value.UtcTicks}");
        CommitWorkingCopy(candidate);
    }

    private long CalculateWorldVersion(WorkingCopy candidate, bool acceptedCommand = false)
    {
        var acceptedCommands = candidate.Outcomes.Values.LongCount(item => item.Accepted) + (acceptedCommand ? 1 : 0);
        var timeBoundary = candidate.State.GameTime > _initialGameTime ? 1L : 0L;
        return checked(_initialWorldVersion + acceptedCommands + candidate.ProcessedScheduledEventCount + timeBoundary);
    }

    private WorkingCopy CreateWorkingCopy() => new(
        _state.Clone(),
        _scheduledEvents.ToList(),
        _commandOutcomes.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
        _outboxEvents.ToList(),
        _nextCreationSequence,
        _nextIngressSequence,
        _processedScheduledEventCount);

    private void CommitWorkingCopy(WorkingCopy candidate)
    {
        _state = candidate.State;
        _scheduledEvents = candidate.Scheduled;
        _commandOutcomes = candidate.Outcomes;
        _outboxEvents = candidate.Outbox;
        _nextCreationSequence = candidate.NextCreationSequence;
        _nextIngressSequence = candidate.NextIngressSequence;
        _processedScheduledEventCount = candidate.ProcessedScheduledEventCount;
    }

    private RealtimeAdvanceResult Report(bool succeeded, List<DomainEvent> events, List<RealtimeCommandResult> commandResults,
        List<SimulationError> errors, GameTime startTime, int processed)
    {
        return new RealtimeAdvanceResult(
            succeeded,
            BuildReadModel(),
            new ReadOnlyCollection<DomainEvent>(events.ToArray()),
            new ReadOnlyCollection<RealtimeCommandResult>(commandResults.ToArray()),
            new ReadOnlyCollection<SimulationError>(errors.ToArray()),
            _state.GameTime.Value - startTime.Value,
            processed,
            _scheduledEvents.Count,
            _isPaused,
            _speed,
            ComputeStateHash());
    }

    private RealtimeReadModel BuildReadModel() => RealtimeReadModel.From(_state, _scheduledEvents, _commandOutcomes.Values, _outboxEvents.Count, ComputeStateHash());

    private string ComputeStateHash() => CanonicalStateHasher.Compute(_state, _scheduledEvents, _nextCreationSequence, _nextIngressSequence,
        _commandOutcomes.Values, _randomState, _outboxEvents, _realGameTickRemainder, _initialGameTime, _initialWorldVersion,
        _processedScheduledEventCount, _isPaused, _speed, _inbox.Select(Fingerprint));

    private static void Schedule(WorkingCopy candidate, string eventId, GameTime due, int phase, int priority, string eventType,
        IReadOnlyDictionary<string, string> data, string? causalCommandId)
    {
        candidate.Scheduled.Add(new ScheduledSimulationEvent(eventId, due, phase, priority, candidate.NextCreationSequence++, eventType, data, causalCommandId));
    }

    private static void ScheduleDailyHeartbeat(WorldState state, List<ScheduledSimulationEvent> scheduled, WorkingCopy candidate)
    {
        var nextMidnight = new GameTime(new DateTimeOffset(state.GameTime.Value.Date.AddDays(1), TimeSpan.Zero));
        scheduled.Add(new ScheduledSimulationEvent($"daily-heartbeat-{nextMidnight.Value:yyyyMMdd}", nextMidnight, DailyHeartbeatPhase, 0, candidate.NextCreationSequence++, "DailyHeartbeat", new Dictionary<string, string>()));
    }

    private static DomainEvent CreateEvent(WorldState state, string eventId, string eventType, GameTime time,
        params (string Key, string Value)[] data) => new(eventId, state.Id, state.TurnNumber, eventType, eventType,
        data.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal), time.Value);

    private static DomainEvent CreateCommandEvent(WorldState state, RealtimeCommand command, RealtimeCommandResult result,
        long ingressSequence, GameTime acceptedAt) => CreateEvent(state, command.CommandId,
        result.Accepted ? "CommandAccepted" : "CommandRejected", acceptedAt,
        ("ingress_sequence", ingressSequence.ToString()), ("command_id", command.CommandId),
        ("accepted", result.Accepted.ToString()), ("expected_world_version", command.ExpectedWorldVersion.ToString()));

    private static RealtimeCommandResult Accepted(string commandId, string message, long ingress, GameTime acceptedAt, long version) =>
        new(true, commandId, message, NoErrors, ingress, acceptedAt, version);

    private static RealtimeCommandResult Reject(string commandId, string message, string code, long ingress, GameTime acceptedAt, long version) =>
        new(false, commandId, message, new ReadOnlyCollection<SimulationError>([new SimulationError(code, message)]), ingress, acceptedAt, version);

    private static string Fingerprint(RealtimeCommand command)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        switch (command)
        {
            case MoveArmyCommand move:
                WriteFingerprintString(writer, "move");
                WriteFingerprintString(writer, move.CommandId);
                WriteFingerprintString(writer, move.ActorId.Value);
                WriteFingerprintString(writer, move.ArmyId.Value);
                WriteFingerprintString(writer, move.DestinationId.Value);
                writer.Write(move.SubmittedAt.UtcTicks);
                writer.Write(move.ExpectedWorldVersion);
                writer.Write(move.TravelHours);
                break;
            case CreateShipmentCommand shipment:
                WriteFingerprintString(writer, "shipment");
                WriteFingerprintString(writer, shipment.CommandId);
                WriteFingerprintString(writer, shipment.ActorId.Value);
                WriteFingerprintString(writer, shipment.ShipmentId.Value);
                WriteFingerprintString(writer, shipment.RouteId.Value);
                writer.Write(shipment.GrainQuantity);
                writer.Write(shipment.SubmittedAt.UtcTicks);
                writer.Write(shipment.ExpectedWorldVersion);
                break;
            default:
                throw new InvalidOperationException("未知命令类型。");
        }

        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteFingerprintString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static bool IsValidId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private sealed class WorkingCopy(
        WorldState state,
        List<ScheduledSimulationEvent> scheduled,
        Dictionary<string, CommandOutcome> outcomes,
        List<DomainEvent> outbox,
        long nextCreationSequence,
        long nextIngressSequence,
        long processedScheduledEventCount)
    {
        public WorldState State { get; } = state;
        public List<ScheduledSimulationEvent> Scheduled { get; } = scheduled;
        public Dictionary<string, CommandOutcome> Outcomes { get; } = outcomes;
        public List<DomainEvent> Outbox { get; } = outbox;
        public long NextCreationSequence { get; set; } = nextCreationSequence;
        public long NextIngressSequence { get; set; } = nextIngressSequence;
        public long ProcessedScheduledEventCount { get; set; } = processedScheduledEventCount;
    }
}

public static class RealtimeSnapshotSchema
{
    public const int Version = 4;
}
