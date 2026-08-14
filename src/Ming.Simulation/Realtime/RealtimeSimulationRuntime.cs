using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
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

public sealed record SetPausedCommand(
    string CommandId,
    CharacterId ActorId,
    bool Paused,
    DateTimeOffset SubmittedAt,
    long ExpectedWorldVersion)
    : RealtimeCommand(CommandId, ActorId, SubmittedAt, ExpectedWorldVersion);

public sealed record SetSimulationSpeedCommand(
    string CommandId,
    CharacterId ActorId,
    double Speed,
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
        string payloadChecksum,
        GameTime initialGameTime,
        long initialWorldVersion,
        long processedScheduledEventCount,
        bool isPaused,
        double speed,
        long nextEventSequence)
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
        PayloadChecksum = payloadChecksum;
        NextEventSequence = nextEventSequence;
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

    public string PayloadChecksum { get; }

    internal long NextEventSequence { get; }
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
    // 当前实时垂直切片的权威提交粒度；GameTime 仍由 Scheduler 精确推进到 DueAt。
    private static readonly TimeSpan RealtimeCommitQuantum = TimeSpan.FromHours(1);
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
    private long _nextEventSequence;
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
        var initialWork = new WorkingCopy(_state, _scheduledEvents, _commandOutcomes, _outboxEvents, _nextCreationSequence, _nextIngressSequence, 0, _isPaused, _speed, _nextEventSequence);
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
        _nextEventSequence = snapshot.NextEventSequence;
        _isPaused = snapshot.IsPaused;
        _speed = snapshot.Speed;
        ValidateSpeed(_speed);
        _randomState = snapshot.RandomState;

        var actualHash = ComputeStateHash();
        if (!StringComparer.Ordinal.Equals(actualHash, snapshot.StateHash))
        {
            throw new InvalidDataException("实时快照的 canonical state hash 校验失败。");
        }

        var pendingCommands = _inbox.ToArray();
        var outboxEvents = _outboxEvents.ToArray();
        var restoredStateHash = ComputeStateHash(pendingCommands.Select(Fingerprint));
        if (!StringComparer.Ordinal.Equals(ComputePayloadChecksum(pendingCommands, outboxEvents, restoredStateHash), snapshot.PayloadChecksum))
        {
            throw new InvalidDataException("实时快照 payload checksum 校验失败。");
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

    public bool IsPaused
    {
        get
        {
            lock (_writerGate)
            {
                return _isPaused;
            }
        }
    }

    public double Speed
    {
        get
        {
            lock (_writerGate)
            {
                return _speed;
            }
        }
    }

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

    public RealtimeCommandReceipt EnqueueSetPaused(SetPausedCommand command) => Enqueue(command);

    public RealtimeCommandReceipt EnqueueSetSimulationSpeed(SetSimulationSpeedCommand command) => Enqueue(command);

    public void SetPaused(bool paused)
    {
        lock (_writerGate)
        {
            var commandId = $"control-pause-{_state.WorldVersion}-{paused.ToString().ToLowerInvariant()}";
            Enqueue(new SetPausedCommand(commandId, new CharacterId("system"), paused,
                _state.GameTime.Value, _state.WorldVersion));
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
            var speedBits = BitConverter.DoubleToInt64Bits(speed);
            var commandId = $"control-speed-{_state.WorldVersion}-{speedBits}";
            Enqueue(new SetSimulationSpeedCommand(commandId, new CharacterId("system"), speed,
                _state.GameTime.Value, _state.WorldVersion));
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
            var pendingCommands = _inbox.ToArray();
            var outboxEvents = _outboxEvents.ToArray();
            var stateHash = ComputeStateHash(pendingCommands.Select(Fingerprint));
            return new RealtimeSnapshot(
                RealtimeSnapshotSchema.Version,
                _state.Clone(),
                _scheduledEvents.ToArray(),
                pendingCommands,
                _nextCreationSequence,
                _nextIngressSequence,
                CommandOutcomes,
                _randomState,
                outboxEvents,
                _realGameTickRemainder,
                stateHash,
                ComputePayloadChecksum(pendingCommands, outboxEvents, stateHash),
                _initialGameTime,
                _initialWorldVersion,
                _processedScheduledEventCount,
                _isPaused,
                _speed,
                _nextEventSequence);
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

    private RealtimeAdvanceResult AdvanceToCore(GameTime targetGameTime, bool fixedHourlyCommits = false)
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

                var nextBoundary = fixedHourlyCommits
                    ? NextRealtimeCommitBoundary(_state.GameTime)
                    : targetGameTime;
                var commitTarget = nextBoundary;
                if (commitTarget > targetGameTime)
                {
                    commitTarget = targetGameTime;
                }

                if (next is not null && next.DueGameTime <= commitTarget)
                {
                    if (!TryCommitScheduledEvent(next, events, out var eventError))
                    {
                        errors.Add(eventError!);
                        break;
                    }

                    processed++;
                    continue;
                }

                if (_state.GameTime < commitTarget &&
                    (!fixedHourlyCommits || commitTarget == nextBoundary))
                {
                    CommitTimeOnly(commitTarget, events);
                }

                if (!fixedHourlyCommits || _state.GameTime >= targetGameTime || commitTarget != nextBoundary)
                {
                    break;
                }
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
            // 先在当前安全点接纳收件箱。这样同一帧提交暂停命令时，
            // 这帧的现实耗时不会先被错误地记入余数；暂停状态下也永远不累计余数。
            var ingress = AdvanceToCore(_state.GameTime);
            if (ingress.Errors.Count > 0 || _isPaused)
            {
                return ingress;
            }

            var gameTicks = ((decimal)realElapsed.Ticks / TimeSpan.TicksPerSecond) *
                (decimal)GameHoursPerRealSecondAtSpeedOne * (decimal)_speed * TimeSpan.TicksPerHour;

            _realGameTickRemainder += gameTicks;
            var startTime = _state.GameTime;
            var requestedTicks = decimal.Truncate(_realGameTickRemainder);
            var target = new GameTime(startTime.Value.AddTicks(checked((long)requestedTicks)));
            var result = AdvanceToCore(target, fixedHourlyCommits: true);
            var committedTicks = (decimal)(result.ReadModel.GameTime.Value - startTime.Value).Ticks;
            _realGameTickRemainder -= committedTicks;
            return MergeReports(ingress, result, startTime);
        }
    }

    private static GameTime NextRealtimeCommitBoundary(GameTime current)
    {
        var value = current.Value;
        var hour = new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero);
        return new GameTime(hour.Add(RealtimeCommitQuantum));
    }

    private RealtimeCommandReceipt Enqueue(RealtimeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_writerGate)
        {
            if (!IsValidId(command.CommandId))
            {
                return new RealtimeCommandReceipt(
                    command.CommandId,
                    false,
                    "CommandId 必须为 1 到 128 个字母、数字或 -_.: 字符。",
                    new ReadOnlyCollection<SimulationError>([new SimulationError("INVALID_COMMAND_ID", "CommandId 必须为 1 到 128 个字母、数字或 -_.: 字符。")]));
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
    }

    private List<RealtimeCommandResult> DrainInbox(List<DomainEvent> events)
    {
        var results = new List<RealtimeCommandResult>();
        // 先窥视队首再处理，只有该命令提交成功后才出队。
        // 为什么：出队本身也是权威状态的一部分，若处理时抛出未预期异常，
        // 命令必须继续留在收件箱等待诊断/重试，而不是被静默消费造成丢命令；
        // 这样“出队、状态、Outcome、事件”仍与设计文档一致地同批原子提交。
        while (_inbox.TryPeek(out var command))
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
                duplicateEvents.Add(CreateEvent(candidate, command.CommandId, duplicate ? "CommandDeduplicated" : "CommandRejected",
                    candidate.State.GameTime, ("ingress_sequence", ingressSequence.ToString()), ("command_id", command.CommandId)));
                candidate.Outbox.Add(duplicateEvents[^1]);
                CommitWorkingCopy(candidate);
                _inbox.TryDequeue(out _);
                events.AddRange(duplicateEvents);
                results.Add(duplicateResult);
                continue;
            }

            var result = ValidateAndApplyCommand(candidate, command, ingressSequence, fingerprint, events);
            CommitWorkingCopy(candidate);
            _inbox.TryDequeue(out _);
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
        if (!IsValidId(command.ActorId.Value))
        {
            result = Reject(command.CommandId, "命令中的角色编号不合法。", "INVALID_OBJECT_ID", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }
        else if (command.ExpectedWorldVersion != candidate.State.WorldVersion)
        {
            result = Reject(command.CommandId, "命令基于过期世界版本。", "STATE_VERSION_CONFLICT", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }
        else
        {
            candidate.PendingWorldVersion = candidate.State.WorldVersion + 1;
            candidate.PendingCommitId = $"command-{ingressSequence}";
            result = command switch
            {
                MoveArmyCommand move => ApplyMove(candidate, move, ingressSequence, acceptedAt, events),
                CreateShipmentCommand shipment => ApplyShipment(candidate, shipment, ingressSequence, acceptedAt, events),
                SetPausedCommand pause => ApplyPause(candidate, pause, ingressSequence, acceptedAt),
                SetSimulationSpeedCommand speed => ApplySpeed(candidate, speed, ingressSequence, acceptedAt),
                _ => Reject(command.CommandId, "未知实时命令类型。", "UNKNOWN_COMMAND", ingressSequence, acceptedAt, candidate.State.WorldVersion),
            };
        }

        var commitId = result.Accepted ? $"command-{ingressSequence}" : null;
        var resultingVersion = candidate.State.WorldVersion;
        if (result.Accepted)
        {
            resultingVersion = candidate.State.WorldVersion + 1;
            candidate.State.CommitRealtime(resultingVersion, commitId!);
            result = result with { ResultingWorldVersion = resultingVersion };
        }
        else
        {
            candidate.PendingWorldVersion = candidate.State.WorldVersion;
            candidate.PendingCommitId = candidate.State.CommitId;
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
        events.Add(CreateCommandEvent(candidate, command, result, ingressSequence, acceptedAt));
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
        events.Add(CreateEvent(candidate, command.CommandId, "ArmyMarchStarted", acceptedAt,
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
        if (!GrainLogisticsRules.HasEnoughSourceGrain(source, command.GrainQuantity))
        {
            return Reject(command.CommandId, "起点库存不足。", "INSUFFICIENT_GRAIN", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (!GrainLogisticsRules.FitsRouteCapacity(candidate.State.Logistics, route, command.GrainQuantity))
        {
            return Reject(command.CommandId, "路线在途容量不足。", "ROUTE_CAPACITY_EXCEEDED", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (!GrainLogisticsRules.TryCalculateArrival(route, command.GrainQuantity, out var plannedDelivery, out _))
        {
            return Reject(command.CommandId, "运输损耗计算超出安全范围。", "LOSS_CALCULATION_OVERFLOW", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (!GrainLogisticsRules.FitsDestinationCapacity(candidate.State.Logistics, destination, plannedDelivery))
        {
            return Reject(command.CommandId, "目的地库存容量不足。", "DESTINATION_CAPACITY_EXCEEDED", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (!source.TryTakeGrain(command.GrainQuantity))
        {
            return Reject(command.CommandId, "起点库存扣减失败。", "INSUFFICIENT_GRAIN", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        candidate.State.Logistics.AddShipment(new ShipmentState(command.ShipmentId, route.Id, command.GrainQuantity, acceptedAt));
        Schedule(candidate, $"shipment-departure-{command.ShipmentId.Value}", acceptedAt, 0, 1, "ShipmentDeparture",
            new Dictionary<string, string>
            {
                ["shipment_id"] = command.ShipmentId.Value,
                ["route_id"] = route.Id.Value,
            }, command.CommandId);
        events.Add(CreateEvent(candidate, command.CommandId, "ShipmentPlanned", acceptedAt,
            ("shipment_id", command.ShipmentId.Value), ("route_id", route.Id.Value), ("grain", command.GrainQuantity.ToString())));
        candidate.Outbox.Add(events[^1]);
        return Accepted(command.CommandId, "粮运计划已接纳。", ingressSequence, acceptedAt, candidate.State.WorldVersion);
    }

    private RealtimeCommandResult ApplyPause(WorkingCopy candidate, SetPausedCommand command, long ingressSequence, GameTime acceptedAt)
    {
        candidate.IsPaused = command.Paused;
        return Accepted(command.CommandId, "pause control accepted", ingressSequence, acceptedAt, candidate.State.WorldVersion);
    }

    private RealtimeCommandResult ApplySpeed(WorkingCopy candidate, SetSimulationSpeedCommand command, long ingressSequence, GameTime acceptedAt)
    {
        try
        {
            ValidateSpeed(command.Speed);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Reject(command.CommandId, "speed must be finite and between 0.25 and 5", "INVALID_SPEED", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        candidate.Speed = command.Speed;
        return Accepted(command.CommandId, "speed control accepted", ingressSequence, acceptedAt, candidate.State.WorldVersion);
    }

    private bool TryCommitScheduledEvent(ScheduledSimulationEvent scheduled, List<DomainEvent> events, out SimulationError? error)
    {
        var candidate = CreateWorkingCopy();
        candidate.Scheduled.Remove(scheduled);
        var candidateEvents = new List<DomainEvent>();
        try
        {
            candidate.State.AdvanceTo(scheduled.DueGameTime);
            candidate.PendingWorldVersion = candidate.State.WorldVersion + 1;
            candidate.PendingCommitId = $"event-{scheduled.CreationSequence}";
            ApplyScheduledEvent(candidate, scheduled, candidateEvents);
            var errors = new InvariantChecker().Check(candidate.State);
            if (errors.Count > 0)
            {
                error = errors[0];
                return false;
            }

            candidate.ProcessedScheduledEventCount++;
            candidate.State.CommitRealtime(candidate.State.WorldVersion + 1, $"event-{scheduled.CreationSequence}");
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
            events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "DailySimulationTick", candidate.State.GameTime, ("tick", "daily")));
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
            events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "ArmyArrived", candidate.State.GameTime,
                ("army_id", armyId.Value), ("from", movement.Origin.Value), ("to", movement.Destination.Value)));
            candidate.Outbox.Add(events[^1]);
            return;
        }

        if (scheduled.EventType == "ShipmentDeparture")
        {
            var shipmentId = new ShipmentId(scheduled.Data["shipment_id"]);
            if (!candidate.State.Logistics.Shipments.TryGetValue(shipmentId, out var shipment) ||
                !candidate.State.Logistics.Routes.TryGetValue(shipment.RouteId, out var route) ||
                route.Id != new RouteId(scheduled.Data.GetValueOrDefault("route_id", route.Id.Value)))
            {
                throw new InvalidOperationException("Shipment departure does not match the authoritative shipment state.");
            }

            if (shipment.Status != ShipmentStatus.Planned)
            {
                events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "ShipmentDepartureIgnored", candidate.State.GameTime,
                    ("shipment_id", shipment.Id.Value), ("status", shipment.Status.ToString())));
                candidate.Outbox.Add(events[^1]);
                return;
            }

            shipment.MarkInTransit(candidate.State.GameTime);
            var arrivalAt = candidate.State.GameTime.Add(TimeSpan.FromHours(route.TravelHours));
            Schedule(candidate, $"shipment-arrival-{shipment.Id.Value}", arrivalAt, 1, 1, "ShipmentArrival",
                new Dictionary<string, string>
                {
                    ["shipment_id"] = shipment.Id.Value,
                    ["route_id"] = route.Id.Value,
                }, scheduled.CausalCommandId);
            events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "ShipmentDeparted", candidate.State.GameTime,
                ("shipment_id", shipment.Id.Value), ("due_at", arrivalAt.ToString())));
            candidate.Outbox.Add(events[^1]);
            return;
        }

        if (scheduled.EventType == "ShipmentArrival")
        {
            var shipmentId = new ShipmentId(scheduled.Data["shipment_id"]);
            if (!candidate.State.Logistics.Shipments.TryGetValue(shipmentId, out var shipment) ||
                !candidate.State.Logistics.Routes.TryGetValue(shipment.RouteId, out var route) ||
                route.Id != new RouteId(scheduled.Data.GetValueOrDefault("route_id", route.Id.Value)))
            {
                throw new InvalidOperationException("Shipment arrival does not match the authoritative shipment state.");
            }

            if (shipment.Status == ShipmentStatus.Arrived)
            {
                events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "ShipmentArrivalIgnored", candidate.State.GameTime,
                    ("shipment_id", shipment.Id.Value), ("status", shipment.Status.ToString())));
                candidate.Outbox.Add(events[^1]);
                return;
            }

            if (shipment.Status != ShipmentStatus.InTransit)
            {
                throw new InvalidOperationException("Shipment arrival does not match the authoritative shipment state.");
            }

            var destination = candidate.State.Logistics.Stockpiles[route.ToStockpileId];
            if (!GrainLogisticsRules.TryCalculateArrival(route, shipment.GrainQuantity, out var delivered, out var loss))
            {
                throw new InvalidOperationException("运输损耗计算超出安全范围。");
            }

            if (!destination.TryStoreGrain(delivered))
            {
                Schedule(candidate, $"shipment-arrival-retry-{shipment.Id.Value}-{candidate.State.GameTime.Value.UtcTicks}",
                    candidate.State.GameTime.Add(TimeSpan.FromHours(1)), 1, 1, "ShipmentArrival",
                    new Dictionary<string, string>
                    {
                        ["shipment_id"] = shipment.Id.Value,
                        ["route_id"] = route.Id.Value,
                    }, scheduled.CausalCommandId);
                events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "ShipmentArrivalBlocked", candidate.State.GameTime,
                    ("shipment_id", shipment.Id.Value), ("destination_id", destination.Id.Value)));
                candidate.Outbox.Add(events[^1]);
                return;
            }

            shipment.MarkArrived(candidate.State.GameTime, delivered, loss);
            events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "ShipmentArrived", candidate.State.GameTime,
                ("shipment_id", shipment.Id.Value), ("destination_id", destination.Id.Value),
                ("delivered_grain", delivered.ToString()), ("loss_grain", loss.ToString())));
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
        candidate.PendingWorldVersion = candidate.State.WorldVersion + 1;
        candidate.PendingCommitId = $"time-{target.Value.UtcTicks}";
        var timeEvent = CreateEvent(candidate, null, "TimeAdvanced", target,
            ("from", previous.ToString()), ("to", target.ToString()));
        candidate.Outbox.Add(timeEvent);
        events.Add(timeEvent);
        // 纯时间提交同样原子更新 GameTime、WorldVersion、CommitId 和 Scheduler；
        // 帧切分只影响余数累计，不影响这次明确目标提交的结果。
        candidate.State.CommitRealtime(candidate.State.WorldVersion + 1, $"time-{target.Value.UtcTicks}");
        CommitWorkingCopy(candidate);
    }

    private WorkingCopy CreateWorkingCopy() => new(
        _state.Clone(),
        _scheduledEvents.ToList(),
        _commandOutcomes.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
        _outboxEvents.ToList(),
        _nextCreationSequence,
        _nextIngressSequence,
        _processedScheduledEventCount,
        _isPaused,
        _speed,
        _nextEventSequence);

    private void CommitWorkingCopy(WorkingCopy candidate)
    {
        _state = candidate.State;
        _scheduledEvents = candidate.Scheduled;
        _commandOutcomes = candidate.Outcomes;
        _outboxEvents = candidate.Outbox;
        _nextCreationSequence = candidate.NextCreationSequence;
        _nextIngressSequence = candidate.NextIngressSequence;
        _processedScheduledEventCount = candidate.ProcessedScheduledEventCount;
        _isPaused = candidate.IsPaused;
        _speed = candidate.Speed;
        _nextEventSequence = candidate.NextEventSequence;
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

    private RealtimeAdvanceResult MergeReports(
        RealtimeAdvanceResult ingress,
        RealtimeAdvanceResult advancement,
        GameTime startTime) =>
        advancement with
        {
            Succeeded = ingress.Succeeded && advancement.Succeeded,
            Events = new ReadOnlyCollection<DomainEvent>(ingress.Events.Concat(advancement.Events).ToArray()),
            CommandResults = new ReadOnlyCollection<RealtimeCommandResult>(ingress.CommandResults.Concat(advancement.CommandResults).ToArray()),
            Errors = new ReadOnlyCollection<SimulationError>(ingress.Errors.Concat(advancement.Errors).ToArray()),
            GameTimeAdvanced = advancement.ReadModel.GameTime.Value - startTime.Value,
            ProcessedScheduledEvents = ingress.ProcessedScheduledEvents + advancement.ProcessedScheduledEvents,
        };

    private RealtimeReadModel BuildReadModel() => RealtimeReadModel.From(_state, _scheduledEvents, _commandOutcomes.Values, _outboxEvents.Count, ComputeStateHash());

    private string ComputeStateHash(IEnumerable<string>? pendingCommandFingerprints = null) => CanonicalStateHasher.Compute(_state, _scheduledEvents, _nextCreationSequence, _nextIngressSequence,
        _commandOutcomes.Values, _randomState, _outboxEvents, _realGameTickRemainder, _initialGameTime, _initialWorldVersion,
        _processedScheduledEventCount, _isPaused, _speed, pendingCommandFingerprints ?? _inbox.Select(Fingerprint), _nextEventSequence);

    private string ComputePayloadChecksum(IReadOnlyList<RealtimeCommand> pendingCommands, IReadOnlyList<DomainEvent> outboxEvents, string stateHash)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(RealtimeSnapshotSchema.Version);
        writer.Write(stateHash);
        writer.Write(_nextCreationSequence);
        writer.Write(_nextIngressSequence);
        writer.Write(_nextEventSequence);
        writer.Write(_processedScheduledEventCount);
        writer.Write(_realGameTickRemainder.ToString("G29", CultureInfo.InvariantCulture));
        writer.Write(_isPaused);
        writer.Write(BitConverter.DoubleToInt64Bits(_speed));
        writer.Write(_randomState);
        foreach (var command in pendingCommands) writer.Write(Fingerprint(command));
        foreach (var domainEvent in outboxEvents)
        {
            writer.Write(domainEvent.EventId);
            writer.Write(domainEvent.WorldId.Value);
            writer.Write(domainEvent.TurnNumber);
            writer.Write(domainEvent.EventType);
            writer.Write(domainEvent.Description);
            writer.Write(domainEvent.OccurredAt.HasValue);
            if (domainEvent.OccurredAt.HasValue) writer.Write(domainEvent.OccurredAt.Value.UtcTicks);
            writer.Write(domainEvent.EventSequence);
            writer.Write(domainEvent.WorldVersion);
            writer.Write(domainEvent.CommitId);
            writer.Write(domainEvent.CausalCommandId ?? string.Empty);
            var data = domainEvent.Data.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            writer.Write(data.Length);
            foreach (var item in data)
            {
                writer.Write(item.Key);
                writer.Write(item.Value);
            }
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

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

    private static DomainEvent CreateEvent(WorkingCopy candidate, string? causalCommandId, string eventType, GameTime time,
        params (string Key, string Value)[] data) => new($"event-{candidate.NextEventSequence++}", candidate.State.Id, candidate.State.TurnNumber, eventType, eventType,
        data.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal), time.Value,
        candidate.NextEventSequence - 1, candidate.PendingWorldVersion, candidate.PendingCommitId, causalCommandId);

    private static DomainEvent CreateCommandEvent(WorkingCopy candidate, RealtimeCommand command, RealtimeCommandResult result,
        long ingressSequence, GameTime acceptedAt) => CreateEvent(candidate, command.CommandId,
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
            case SetPausedCommand pause:
                WriteFingerprintString(writer, "pause");
                WriteFingerprintString(writer, pause.CommandId);
                WriteFingerprintString(writer, pause.ActorId.Value);
                writer.Write(pause.Paused);
                writer.Write(pause.SubmittedAt.UtcTicks);
                writer.Write(pause.ExpectedWorldVersion);
                break;
            case SetSimulationSpeedCommand speed:
                WriteFingerprintString(writer, "speed");
                WriteFingerprintString(writer, speed.CommandId);
                WriteFingerprintString(writer, speed.ActorId.Value);
                writer.Write(BitConverter.DoubleToInt64Bits(speed.Speed));
                writer.Write(speed.SubmittedAt.UtcTicks);
                writer.Write(speed.ExpectedWorldVersion);
                break;
            default:
                // 未知命令类型没有稳定的负载字段；用类型名兜底生成指纹。
                // 为什么：ValidateAndApplyCommand 的 switch 已经用 UNKNOWN_COMMAND
                // 结构化拒绝未知类型，Fingerprint 若在这里抛异常反而会在拒绝之前
                // 让整个推进崩溃；稳定的类型指纹让同一命令编号的重放得到同一拒绝结果。
                WriteFingerprintString(writer, "unknown");
                WriteFingerprintString(writer, command.GetType().FullName ?? command.GetType().Name);
                WriteFingerprintString(writer, command.CommandId);
                WriteFingerprintString(writer, command.ActorId.Value);
                writer.Write(command.SubmittedAt.UtcTicks);
                writer.Write(command.ExpectedWorldVersion);
                break;
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

    private static bool IsValidId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private sealed class WorkingCopy(
        WorldState state,
        List<ScheduledSimulationEvent> scheduled,
        Dictionary<string, CommandOutcome> outcomes,
        List<DomainEvent> outbox,
        long nextCreationSequence,
        long nextIngressSequence,
        long processedScheduledEventCount,
        bool isPaused,
        double speed,
        long nextEventSequence)
    {
        public WorldState State { get; } = state;
        public List<ScheduledSimulationEvent> Scheduled { get; } = scheduled;
        public Dictionary<string, CommandOutcome> Outcomes { get; } = outcomes;
        public List<DomainEvent> Outbox { get; } = outbox;
        public long NextCreationSequence { get; set; } = nextCreationSequence;
        public long NextIngressSequence { get; set; } = nextIngressSequence;
        public long ProcessedScheduledEventCount { get; set; } = processedScheduledEventCount;
        public bool IsPaused { get; set; } = isPaused;
        public double Speed { get; set; } = speed;
        public long NextEventSequence { get; set; } = nextEventSequence;
        public long PendingWorldVersion { get; set; } = state.WorldVersion;
        public string PendingCommitId { get; set; } = state.CommitId;
    }
}

public static class RealtimeSnapshotSchema
{
    public const int Version = 5;
}
