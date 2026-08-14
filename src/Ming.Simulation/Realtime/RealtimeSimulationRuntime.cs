using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Common;
using MingSim.Domain.Decrees;
using MingSim.Domain.Economy;
using MingSim.Domain.Errors;
using MingSim.Domain.Events;
using MingSim.Domain.Military;
using MingSim.Domain.Realtime;
using MingSim.Domain.Scenario;

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
    long ExpectedWorldVersion,
    bool Escort = false)
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
    // 风险与冲突阶段（doc 08 §4.3 Phase 3）：天气/袭粮/报告都在同刻日耗之后结算。
    private const int RiskSamplePhase = 3;
    private const string RandomState = "schema=1;streams=none";

    private readonly CapabilityAuthorizer _authorizer = new();
    private readonly ICommitStore? _commitStore;
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

    public RealtimeSimulationRuntime(WorldState initialState, ICommitStore? commitStore = null)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        _commitStore = commitStore;
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

    public RealtimeCommandReceipt EnqueueCreateDecree(CreateDecreeCommand command) => Enqueue(command);

    public RealtimeCommandReceipt EnqueueApproveDecree(ApproveDecreeCommand command) => Enqueue(command);

    public RealtimeCommandReceipt EnqueueSetPaused(SetPausedCommand command) => Enqueue(command);

    public RealtimeCommandReceipt EnqueueSetSimulationSpeed(SetSimulationSpeedCommand command) => Enqueue(command);

    /// <summary>
    /// 从权威路线中为一次调粮选择一个当前可执行的路线（P1-UI-01 修复）：
    /// 按路线编号稳定排序，返回第一个"调用者有授权、来源有粮、路线容量与目的仓余量都放得下"的路线。
    /// 只读权威状态；UI 用它替代硬编码路线，绝不猜测来源仓。
    /// </summary>
    public RouteId? ResolveRouteForGrainShipment(CharacterId actorId, long grainQuantity)
    {
        if (grainQuantity <= 0 || !IsValidId(actorId.Value))
        {
            return null;
        }

        lock (_writerGate)
        {
            foreach (var candidate in _state.Logistics.Routes.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
            {
                if (!_state.Logistics.Stockpiles.TryGetValue(candidate.FromStockpileId, out var source)
                    || !_state.Logistics.Stockpiles.TryGetValue(candidate.ToStockpileId, out var destination))
                {
                    continue;
                }

                var authorization = _authorizer.Check(_state, actorId, GameCapability.PlanLogistics, candidate.Id.Value);
                if (!authorization.Allowed)
                {
                    continue;
                }

                if (!GrainLogisticsRules.HasEnoughSourceGrain(source, grainQuantity) ||
                    !GrainLogisticsRules.FitsRouteCapacity(_state.Logistics, candidate, grainQuantity))
                {
                    continue;
                }

                if (!GrainLogisticsRules.TryCalculateArrival(candidate, grainQuantity, out var plannedDelivery, out _))
                {
                    continue;
                }

                if (!GrainLogisticsRules.FitsDestinationCapacity(_state.Logistics, destination, plannedDelivery))
                {
                    continue;
                }

                return candidate.Id;
            }

            return null;
        }
    }

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

    /// <summary>
    /// 安排 P0 固定风险样本（doc 03 §4）：一次天气延误、一次袭粮、三份差异报告。
    /// 样本日期与随机种子都是 DESIGN；同一世界同一输入必然得到同一结果（可种子重放）。
    /// 重复调用是安全的幂等操作：队列里已存在同类样本就直接返回。
    /// </summary>
    public void ScheduleScenarioRiskSamples()
    {
        lock (_writerGate)
        {
            if (_scheduledEvents.Any(item => item.EventType == ScenarioP0Rules.WeatherDelayEvent))
            {
                return;
            }

            ScheduleFixedSample(ScenarioP0Rules.DesignWeatherDelayDay, ScenarioP0Rules.WeatherDelayEvent);
            ScheduleFixedSample(ScenarioP0Rules.DesignGrainRaidDay, ScenarioP0Rules.GrainRaidEvent);
            ScheduleFixedSample(ScenarioP0Rules.DesignReportsDay, ScenarioP0Rules.ScenarioReportsEvent);
        }
    }

    /// <summary>自动可检查的终局评估（doc 03 §7.2）：先判硬失败，再按 90 日分档并输出六维解释。</summary>
    public EndgameEvaluation EvaluateEndgame()
    {
        lock (_writerGate)
        {
            return EndgameEvaluator.Evaluate(_state, _initialGameTime);
        }
    }

    private void ScheduleFixedSample(int day, string eventType)
    {
        var due = _initialGameTime.Add(TimeSpan.FromDays(day));
        _scheduledEvents.Add(new ScheduledSimulationEvent(
            $"{eventType.ToLowerInvariant()}-day-{day}", due, RiskSamplePhase, 0, _nextCreationSequence++,
            eventType, new Dictionary<string, string>()));
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

    /// <summary>从提交商店恢复最后一个完整提交（doc 04 §5）；没有提交时抛异常而不是静默开新世界。</summary>
    public static RealtimeSimulationRuntime RestoreFromStore(ICommitStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var loaded = store.LoadCommittedWorld()
            ?? throw new InvalidDataException("提交商店中没有可恢复的完整提交。");
        return Restore(loaded.Snapshot);
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
                // M5：硬失败自动暂停发生在每日心跳提交内（ApplyDailyScenarioRules）。提交一完成
                // 立即停止本次推进——同一 Advance 内不再处理更晚的到期事件，也不再推进时间。
                if (_isPaused)
                {
                    break;
                }

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
                CreateDecreeCommand decree => ApplyDecree(candidate, decree, ingressSequence, acceptedAt, events),
                ApproveDecreeCommand approve => ApplyApproveDecree(candidate, approve, ingressSequence, acceptedAt, events),
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

        if (command.Escort && candidate.State.Economy.Treasury.Silver < ScenarioState.DesignEscortCostSilver)
        {
            return Reject(command.CommandId, "国库银两不足以支付护卫费用。", "ESCORT_BUDGET_INSUFFICIENT", ingressSequence, acceptedAt, candidate.State.WorldVersion);
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

        // 每次调粮都动用地方车马征发：地方负担升高（DESIGN，doc 03 §7.1）；只在场景规则启用时生效。
        if (candidate.State.Scenario.IsScenarioActive)
        {
            candidate.State.Scenario.ChangeLocalBurden(ScenarioState.DesignShipmentBurdenIncrease);
        }

        candidate.State.Logistics.AddShipment(new ShipmentState(command.ShipmentId, route.Id, command.GrainQuantity, acceptedAt, command.Escort));
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

    private RealtimeCommandResult ApplyDecree(
        WorkingCopy candidate,
        CreateDecreeCommand command,
        long ingressSequence,
        GameTime acceptedAt,
        List<DomainEvent> events)
    {
        if (!IsValidId(command.CommandId) || !IsValidId(command.ActorId.Value) ||
            !IsValidId(command.DecreeId.Value) || !IsValidId(command.ResponsibleActorId.Value))
        {
            return Reject(command.CommandId, "命令中的对象编号不合法。", "INVALID_OBJECT_ID", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (string.IsNullOrWhiteSpace(command.Goal))
        {
            return RejectDecree(candidate, command, "INVALID_DECREE_GOAL", "政令目标不能为空。", ingressSequence, acceptedAt, events);
        }

        if (candidate.State.Decrees.ContainsKey(command.DecreeId))
        {
            return RejectDecree(candidate, command, "DECREE_ALREADY_EXISTS", "政令编号已经存在。", ingressSequence, acceptedAt, events);
        }

        if (command.Budget <= 0)
        {
            return RejectDecree(candidate, command, "INVALID_DECREE_BUDGET", "政令预算必须为正数。", ingressSequence, acceptedAt, events);
        }

        if (command.Deadline <= acceptedAt)
        {
            return RejectDecree(candidate, command, "DECREE_DEADLINE_IN_PAST", "政令期限必须晚于当前时间。", ingressSequence, acceptedAt, events);
        }

        if (!candidate.State.Characters.ContainsKey(command.ResponsibleActorId))
        {
            return RejectDecree(candidate, command, "RESPONSIBLE_ACTOR_NOT_FOUND", "政令承办人不存在。", ingressSequence, acceptedAt, events);
        }

        // P1-AUTH-02：签发人必须是世界内真实角色。旧实现只校验承办人存在，
        // 签发人（ActorId）可被任意伪造；现在先做签发人真实性校验，防伪冒 issuer。
        if (!candidate.State.Characters.ContainsKey(command.ActorId))
        {
            return RejectDecree(candidate, command, "DECREE_ISSUER_UNAUTHORIZED", "政令签发人不存在。", ingressSequence, acceptedAt, events);
        }

        var policy = ResolveDecreePolicy(command.Kind);

        // P1-DECREE-03：LinkedShipment 不变量。接纳时绑定运输单必须存在且为 Planned/InTransit；
        // 同一运输单最多被一个 active（Executing/Submitted）政令占用；已抵达运输单不允许新绑定。
        // 全部返回结构化错误码，拒绝路径不改变世界。
        if (command.LinkedShipmentId is not null)
        {
            if (!candidate.State.Logistics.Shipments.TryGetValue(new ShipmentId(command.LinkedShipmentId), out var boundShipment))
            {
                return RejectDecree(candidate, command, "DECREE_SHIPMENT_NOT_FOUND", $"绑定运输单 {command.LinkedShipmentId} 不存在。", ingressSequence, acceptedAt, events);
            }

            if (boundShipment.Status == ShipmentStatus.Arrived)
            {
                return RejectDecree(candidate, command, "DECREE_SHIPMENT_ALREADY_ARRIVED", $"绑定运输单 {command.LinkedShipmentId} 已抵达，不允许再绑定。", ingressSequence, acceptedAt, events);
            }

            var occupiedByActiveDecree = candidate.State.Decrees.Values.Any(decree =>
                decree.LinkedShipmentId == command.LinkedShipmentId &&
                (decree.Status == DecreeStatus.Executing || decree.Status == DecreeStatus.Submitted));
            if (occupiedByActiveDecree)
            {
                return RejectDecree(candidate, command, "DECREE_SHIPMENT_ALREADY_BOUND", $"运输单 {command.LinkedShipmentId} 已被其他 active 政令占用。", ingressSequence, acceptedAt, events);
            }
        }

        // 承办人能力由内核按 DecreeKind 的 trusted 映射决定（P1-AUTH-01 修复）：
        // 命令只表达业务意图，调用方不能再声明 RequiredCapability/RequiredResourceId 来降级审核策略。
        // 请愿类政令（请饷奏疏）无承办能力要求。为什么拒绝不改世界：业务拒绝按 doc 08 §5
        // 只原子记录 CommandId 终态 Outcome，绝不能在"世界和时间不变"的拒绝路径上偷偷扣预算或改信任。
        if (policy.ResponsibleCapability is not null)
        {
            var authorization = _authorizer.Check(candidate.State, command.ResponsibleActorId, policy.ResponsibleCapability.Value, resourceId: null);
            if (!authorization.Allowed)
            {
                return RejectDecree(candidate, command, "DECREE_RESPONSIBLE_UNAUTHORIZED", authorization.Reason, ingressSequence, acceptedAt, events);
            }
        }

        // 预算：普通/减耗/催饷/拨饷政令在接纳时扣除并计入场景支出；
        // 请饷奏疏是请愿文书，创建时不扣中央预算，批准（ApplyApproveDecree）时才扣。
        if (!policy.IsPetition)
        {
            if (!candidate.State.Economy.Treasury.TrySpend(command.Budget))
            {
                return RejectDecree(candidate, command, "DECREE_BUDGET_EXCEEDS_TREASURY", "国库银两不足以批准该政令预算。", ingressSequence, acceptedAt, events);
            }

            candidate.State.Scenario.AddSpentSilver(command.Budget);
        }

        var initialStatus = policy.IsPetition ? DecreeStatus.Submitted : DecreeStatus.Executing;
        candidate.State.AddDecree(new DecreeState(
            command.DecreeId, command.ActorId, command.Goal, command.RegionScope, command.Budget,
            command.ResponsibleActorId, command.Deadline, command.Restrictions, command.Remarks,
            policy.ResponsibleCapability ?? default, requiredResourceId: null, command.LinkedShipmentId,
            initialStatus));
        events.Add(CreateEvent(candidate, command.CommandId, "DecreeAccepted", acceptedAt,
            ("decree_id", command.DecreeId.Value), ("goal", command.Goal), ("budget", command.Budget.ToString()),
            ("responsible_actor", command.ResponsibleActorId.Value), ("deadline", command.Deadline.ToString()),
            ("linked_shipment", command.LinkedShipmentId ?? "")));
        candidate.Outbox.Add(events[^1]);

        // 减耗令（M5 通关杠杆，纸面推演 §3.2）：接纳即生效——前线日耗 300→240 石/日。
        // 信任规则：预先计划（硬失败前发布）不扣大臣信任；硬失败已发生后的临时改令才扣
        // （"未计划改令×2"，DESIGN）。改令与政令接纳同一提交原子生效，重放确定。
        // 非场景世界（FrontStockpileId 为 null、日耗/战备规则关闭）下减耗令仍被接纳但无玩法效果
        // （ScenarioState.DailyGrainDemand 变化无任何读取方）——预期行为，不做额外前置拒绝。
        if (command.Kind == DecreeKind.RationReduction)
        {
            var unplanned = candidate.State.Scenario.HardFailureReported;
            if (unplanned)
            {
                candidate.State.Scenario.ChangeMinisterTrust(-ScenarioState.DesignUnplannedDecreeTrustPenalty);
            }

            var beforeDemand = candidate.State.Scenario.DailyGrainDemand;
            candidate.State.Scenario.ApplyRationReduction();
            if (candidate.State.Scenario.DailyGrainDemand != beforeDemand)
            {
                events.Add(CreateEvent(candidate, command.CommandId, "RationReductionEnacted", acceptedAt,
                    ("decree_id", command.DecreeId.Value),
                    ("new_demand", candidate.State.Scenario.DailyGrainDemand.ToString()),
                    ("unplanned", unplanned.ToString())));
                candidate.Outbox.Add(events[^1]);
            }
        }

        return Accepted(command.CommandId,
            policy.IsPetition ? "请饷奏疏已接纳，等待批准。" : "政令已接纳并扣除预算。",
            ingressSequence, acceptedAt, candidate.State.WorldVersion);
    }

    private RealtimeCommandResult RejectDecree(
        WorkingCopy candidate,
        CreateDecreeCommand command,
        string code,
        string message,
        long ingressSequence,
        GameTime acceptedAt,
        List<DomainEvent> events)
    {
        // 政令特有的拒绝事件：与通用 CommandRejected 一起构成可审计的 DecreeAccepted/Rejected 事件对。
        events.Add(CreateEvent(candidate, command.CommandId, "DecreeRejected", acceptedAt,
            ("decree_id", command.DecreeId.Value), ("code", code), ("reason", message)));
        candidate.Outbox.Add(events[^1]);
        return Reject(command.CommandId, message, code, ingressSequence, acceptedAt, candidate.State.WorldVersion);
    }

    /// <summary>
    /// 批准一道已提交的请饷奏疏（P1-AUTH-01 请饷语义）：扣除批准预算、Submitted → Executing。
    /// 只有 Simulation 命令管线可调用；重复批准/批准非待批准政令都被结构化拒绝。
    /// </summary>
    private RealtimeCommandResult ApplyApproveDecree(
        WorkingCopy candidate,
        ApproveDecreeCommand command,
        long ingressSequence,
        GameTime acceptedAt,
        List<DomainEvent> events)
    {
        if (!IsValidId(command.CommandId) || !IsValidId(command.ActorId.Value) ||
            !IsValidId(command.DecreeId.Value))
        {
            return Reject(command.CommandId, "命令中的对象编号不合法。", "INVALID_OBJECT_ID", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (!candidate.State.Characters.ContainsKey(command.ActorId))
        {
            return Reject(command.CommandId, "批准人不存在。", "DECREE_ISSUER_UNAUTHORIZED", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (!candidate.State.Decrees.TryGetValue(command.DecreeId, out var decree))
        {
            return Reject(command.CommandId, "政令不存在。", "DECREE_NOT_FOUND", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (decree.Status != DecreeStatus.Submitted)
        {
            return Reject(command.CommandId, "只有已提交的请饷奏疏可以被批准。", "DECREE_NOT_PENDING_APPROVAL", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        if (!candidate.State.Economy.Treasury.TrySpend(decree.Budget))
        {
            return Reject(command.CommandId, "国库银两不足以批准该请饷预算。", "DECREE_BUDGET_EXCEEDS_TREASURY", ingressSequence, acceptedAt, candidate.State.WorldVersion);
        }

        candidate.State.Scenario.AddSpentSilver(decree.Budget);
        decree.Approve();
        events.Add(CreateEvent(candidate, command.CommandId, "DecreeApproved", acceptedAt,
            ("decree_id", decree.Id.Value), ("budget", decree.Budget.ToString()),
            ("responsible_actor", decree.ResponsibleActorId.Value)));
        candidate.Outbox.Add(events[^1]);
        return Accepted(command.CommandId, "请饷奏疏已批准并转可执行。", ingressSequence, acceptedAt, candidate.State.WorldVersion);
    }

    /// <summary>政令审核策略（trusted 映射，P1-AUTH-01/02 修复）：承办人能力（请愿类为 null）+ 是否请愿文书。</summary>
    private sealed record DecreePolicy(GameCapability? ResponsibleCapability, bool IsPetition);

    /// <summary>
    /// DecreeKind → 审核策略的内置 trusted 映射：命令只表达业务意图，承办人能力与资源域由内核决定，
    /// 调用方不可覆盖。资源域固定为 null（任意辖区）：政令不绑定具体路线，承办人持相应能力即可（DESIGN）。
    /// 请饷（RequestSupply）是请愿文书：创建时不校验承办能力、不扣预算，进入 Submitted 等待批准。
    /// </summary>
    private static DecreePolicy ResolveDecreePolicy(DecreeKind kind) => kind switch
    {
        DecreeKind.ExpediteSupply => new(GameCapability.PlanLogistics, IsPetition: false),
        DecreeKind.AllocateSupply => new(GameCapability.AllocateFinance, IsPetition: false),
        DecreeKind.RequestSupply => new(null, IsPetition: true),
        DecreeKind.RationReduction => new(GameCapability.PlanLogistics, IsPetition: false),
        _ => new(GameCapability.PlanLogistics, IsPetition: false),
    };

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
            ApplyDailyScenarioRules(candidate, events);
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
            // 后半段地方负担过高会降低配合度：到达延迟（DESIGN：负担每超阈值 1 点延迟 1 小时）。
            var cooperationDelayHours = ScenarioP0Rules.ResolveCooperationDelayHours(candidate.State);
            if (cooperationDelayHours > 0)
            {
                arrivalAt = arrivalAt.Add(TimeSpan.FromHours(cooperationDelayHours));
                events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "ShipmentCooperationDelay", candidate.State.GameTime,
                    ("shipment_id", shipment.Id.Value), ("delay_hours", cooperationDelayHours.ToString())));
                candidate.Outbox.Add(events[^1]);
            }

            // 护卫结算：每批 +400 两（DESIGN，doc 03 §7.1 护卫行）。国库不足时护卫无法成行但运输继续。
            if (shipment.Escort)
            {
                if (candidate.State.Economy.Treasury.TrySpend(ScenarioState.DesignEscortCostSilver))
                {
                    candidate.State.Scenario.AddSpentSilver(ScenarioState.DesignEscortCostSilver);
                    events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "EscortSettlement", candidate.State.GameTime,
                        ("shipment_id", shipment.Id.Value), ("cost_silver", ScenarioState.DesignEscortCostSilver.ToString())));
                    candidate.Outbox.Add(events[^1]);
                }
                else
                {
                    // 护卫费用结算失败（出发时国库已被其他支出耗尽）：护卫无法成行，
                    // 必须清除护卫标记，否则袭粮仍会按"有护卫"的低上限结算，自相矛盾。
                    shipment.ClearEscort();
                    events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "EscortSettlementFailed", candidate.State.GameTime,
                        ("shipment_id", shipment.Id.Value), ("reason", "treasury_insufficient"), ("escorted_after", "false")));
                    candidate.Outbox.Add(events[^1]);
                }
            }

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

            // 袭粮损失先从实到量扣除并计入损耗：实到 + 损耗 仍等于计划量，粮食守恒不被打破（doc 06 §7.4）。
            if (shipment.RaidLossGrain > 0)
            {
                delivered = Math.Max(0, delivered - shipment.RaidLossGrain);
                loss = shipment.GrainQuantity - delivered;
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
                ("delivered_grain", delivered.ToString()), ("loss_grain", loss.ToString()),
                ("raid_loss_grain", shipment.RaidLossGrain.ToString())));
            candidate.Outbox.Add(events[^1]);

            // 政令绑定（P1-DECREE-03）：绑定单抵达即视为政令完成（期限约束的另一半）。
            // 同一运输单最多被一个 active 政令占用（接纳时不变量），但这里仍按政令编号
            // 稳定排序后完成全部处于 Executing 的匹配政令，保证确定性（"全部匹配按稳定顺序完成"）。
            var linkedDecrees = candidate.State.Decrees.Values
                .Where(decree => decree.LinkedShipmentId == shipment.Id.Value && decree.Status == DecreeStatus.Executing)
                .OrderBy(decree => decree.Id.Value, StringComparer.Ordinal)
                .ToArray();
            foreach (var linkedDecree in linkedDecrees)
            {
                linkedDecree.Complete();
                events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "DecreeCompleted", candidate.State.GameTime,
                    ("decree_id", linkedDecree.Id.Value), ("shipment_id", shipment.Id.Value)));
                candidate.Outbox.Add(events[^1]);
            }

            return;
        }

        if (scheduled.EventType == ScenarioP0Rules.WeatherDelayEvent)
        {
            var shipment = SelectMostImminentShipment(candidate);
            if (shipment is null)
            {
                events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "WeatherDelayNoOp", candidate.State.GameTime,
                    ("reason", "no_in_transit_shipment")));
                candidate.Outbox.Add(events[^1]);
                return;
            }

            var arrival = candidate.Scheduled
                .Where(item => item.EventType == "ShipmentArrival" && item.Data.GetValueOrDefault("shipment_id") == shipment.Id.Value)
                .OrderBy(item => item.DueGameTime)
                .ThenBy(item => item.CreationSequence)
                .FirstOrDefault();
            if (arrival is null)
            {
                events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "WeatherDelayNoOp", candidate.State.GameTime,
                    ("reason", "no_scheduled_arrival")));
                candidate.Outbox.Add(events[^1]);
                return;
            }

            // 一次天气延误：运输 +N 日（N 由确定性随机抽取 1..3，DESIGN）。重排到达事件保证"不提前也不重复"。
            var delayDays = ScenarioP0Rules.ResolveWeatherDelayDays(candidate.State);
            candidate.Scheduled.Remove(arrival);
            var newDue = arrival.DueGameTime.Add(TimeSpan.FromDays(delayDays));
            Schedule(candidate, $"shipment-arrival-{shipment.Id.Value}-delayed", newDue, 1, 1, "ShipmentArrival",
                arrival.Data, scheduled.CausalCommandId);
            events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "ShipmentDelayed", candidate.State.GameTime,
                ("shipment_id", shipment.Id.Value), ("delay_days", delayDays.ToString()),
                ("original_due", arrival.DueGameTime.ToString()), ("new_due", newDue.ToString())));
            candidate.Outbox.Add(events[^1]);
            return;
        }

        if (scheduled.EventType == ScenarioP0Rules.GrainRaidEvent)
        {
            var shipment = SelectMostImminentShipment(candidate);
            if (shipment is null)
            {
                events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "GrainRaidNoOp", candidate.State.GameTime,
                    ("reason", "no_in_transit_shipment")));
                candidate.Outbox.Add(events[^1]);
                return;
            }

            // 袭粮损失上限由护卫与否决定（DESIGN：无护卫 0..20%，有护卫 0..5%）；损失先进 ShipmentState，
            // 抵达结算时从实到量扣除，保证粮食守恒。
            var lossPercent = ScenarioP0Rules.ResolveRaidLossPercent(shipment.Escort, candidate.State);
            var lossGrain = checked(shipment.GrainQuantity * lossPercent / 100);
            if (lossGrain > 0)
            {
                shipment.ApplyRaidLoss(lossGrain);
            }

            events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "ShipmentAttacked", candidate.State.GameTime,
                ("shipment_id", shipment.Id.Value), ("escorted", shipment.Escort.ToString()),
                ("loss_percent", lossPercent.ToString()), ("loss_grain", lossGrain.ToString())));
            candidate.Outbox.Add(events[^1]);
            return;
        }

        if (scheduled.EventType == ScenarioP0Rules.ScenarioReportsEvent)
        {
            // 三份时效/可信度不同的报告（DESIGN）：确定性随机 + 种子生成；信任越低报告越陈旧；
            // 报告只是已提交观察，进日志不改世界。
            string[] subjects = ["宁远存粮", "欠饷", "前线战备"];
            for (var index = 1; index <= subjects.Length; index++)
            {
                var (ageDays, credibility) = ScenarioP0Rules.ResolveReportProfile(candidate.State, index);
                events.Add(CreateEvent(candidate, scheduled.CausalCommandId, "ScenarioReportReceived", candidate.State.GameTime,
                    ("report_id", $"ningyuan-report-{index}"), ("subject", subjects[index - 1]),
                    ("age_days", ageDays.ToString()), ("credibility", credibility.ToString()),
                    ("trust_at_report", candidate.State.Scenario.MinisterTrust.ToString())));
                candidate.Outbox.Add(events[^1]);
            }

            return;
        }

        throw new InvalidOperationException($"未知调度事件类型 {scheduled.EventType}。");
    }

    /// <summary>每日场景规则：前线日耗/战备、政令期限甩责、硬失败报告（只在相应状态存在时生效）。</summary>
    private static void ApplyDailyScenarioRules(WorkingCopy candidate, List<DomainEvent> events)
    {
        var ration = ScenarioP0Rules.ApplyDailyRation(candidate.State);
        if (ration.Kind != RationKind.None)
        {
            events.Add(CreateEvent(candidate, null, "FrontierDailyRation", candidate.State.GameTime,
                ("kind", ration.Kind.ToString()),
                ("available_before", ration.AvailableBefore.ToString()),
                ("consumed_grain", ration.ConsumedGrain.ToString()),
                ("shortfall_grain", ration.ShortfallGrain.ToString()),
                ("readiness_bps", candidate.State.Readiness.ValueBasisPoints.ToString()),
                ("arrears_grain", candidate.State.Readiness.ArrearsGrain.ToString()),
                ("consecutive_zero_days", candidate.State.Readiness.ConsecutiveZeroGrainDays.ToString())));
            candidate.Outbox.Add(events[^1]);
        }

        // 政令期限：到期未完成即甩责（大臣信任 -5）。事件按政令编号排序，避免字典枚举顺序影响确定性。
        var overdue = ScenarioP0Rules.ExpireOverdueDecrees(candidate.State);
        foreach (var decree in overdue.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            events.Add(CreateEvent(candidate, null, "DecreeDeadlineExpired", candidate.State.GameTime,
                ("decree_id", decree.Id.Value), ("responsible_actor", decree.ResponsibleActorId.Value),
                ("deadline", decree.Deadline.ToString()), ("trust", candidate.State.Scenario.MinisterTrust.ToString())));
            candidate.Outbox.Add(events[^1]);
        }

        // 硬失败（doc 03 §7.2）只报告一次，避免每日刷屏；评估函数本身随时可再查。
        var failureReason = ScenarioP0Rules.DetectHardFailure(candidate.State);
        if (failureReason is not null && !candidate.State.Scenario.HardFailureReported)
        {
            candidate.State.Scenario.MarkHardFailureReported();
            events.Add(CreateEvent(candidate, null, "ScenarioHardFailure", candidate.State.GameTime, ("reason", failureReason)));
            candidate.Outbox.Add(events[^1]);
            // M5（doc 08 §19"重大游戏事件"行、14 矩阵 SYS-013）：重大游戏事件正常提交后自动暂停一次。
            // 与 MarkHardFailureReported 同块：只触发一次；玩家仍可手动恢复，且不会二次自动暂停；
            // 不抢玩家手动暂停——手动 SetPaused 命令语义不变，恢复后的世界继续按原规则推进。
            candidate.IsPaused = true;
        }
    }

    /// <summary>选出最临近到达的在途运输单（天气延误/袭粮的目标）；排序稳定保证确定性。</summary>
    private static ShipmentState? SelectMostImminentShipment(WorkingCopy candidate)
    {
        var arrival = candidate.Scheduled
            .Where(item => item.EventType == "ShipmentArrival")
            .OrderBy(item => item.DueGameTime)
            .ThenBy(item => item.CreationSequence)
            .FirstOrDefault();
        if (arrival is null)
        {
            return null;
        }

        return candidate.State.Logistics.Shipments.TryGetValue(new ShipmentId(arrival.Data["shipment_id"]), out var shipment)
            ? shipment
            : null;
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
        // 本次提交新产生的事件日志增量：候选 outbox 里超过既有 outbox 数量的部分。
        var newEvents = candidate.Outbox.Skip(_outboxEvents.Count).ToArray();
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

        // 权威提交落盘：内存发布之后立刻调用持久化端口；端口失败必须中止本次推进，
        // 让上层把整个会话视为致命错误（数据库始终保持上一个完整提交，绝不写半状态）。
        if (_commitStore is not null)
        {
            var receipt = _commitStore.CommitWorld(new CommitPackage(CaptureSnapshot(), newEvents));
            if (!receipt.Success)
            {
                throw new InvalidOperationException($"提交商店写入失败，中止推进：{receipt.Error}");
            }
        }
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

    private RealtimeReadModel BuildReadModel() => RealtimeReadModel.From(_state, _scheduledEvents, _commandOutcomes.Values, _outboxEvents.Count, _isPaused, ComputeStateHash());

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

    private RealtimeCommandResult Reject(string commandId, string message, string code, long ingress, GameTime acceptedAt, long version)
    {
        // 未改变世界的拒绝也要持久化（doc 08 §5）：重试同一命令必须得到同一结论。
        _commitStore?.RecordOutcome(new InputOutcome(commandId, code, message, version));
        return new(false, commandId, message, new ReadOnlyCollection<SimulationError>([new SimulationError(code, message)]), ingress, acceptedAt, version);
    }

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
                writer.Write(shipment.Escort);
                writer.Write(shipment.SubmittedAt.UtcTicks);
                writer.Write(shipment.ExpectedWorldVersion);
                break;
            case CreateDecreeCommand decree:
                WriteFingerprintString(writer, "decree");
                WriteFingerprintString(writer, decree.CommandId);
                WriteFingerprintString(writer, decree.ActorId.Value);
                WriteFingerprintString(writer, decree.DecreeId.Value);
                WriteFingerprintString(writer, decree.Goal);
                WriteFingerprintString(writer, decree.RegionScope.Value);
                writer.Write(decree.Budget);
                WriteFingerprintString(writer, decree.ResponsibleActorId.Value);
                writer.Write(decree.Deadline.Value.UtcTicks);
                WriteFingerprintString(writer, decree.Restrictions);
                WriteFingerprintString(writer, decree.Remarks);
                WriteFingerprintString(writer, decree.LinkedShipmentId ?? string.Empty);
                WriteFingerprintString(writer, decree.Kind.ToString());
                writer.Write(decree.SubmittedAt.UtcTicks);
                writer.Write(decree.ExpectedWorldVersion);
                break;
            case ApproveDecreeCommand approve:
                WriteFingerprintString(writer, "approve-decree");
                WriteFingerprintString(writer, approve.CommandId);
                WriteFingerprintString(writer, approve.ActorId.Value);
                WriteFingerprintString(writer, approve.DecreeId.Value);
                writer.Write(approve.SubmittedAt.UtcTicks);
                writer.Write(approve.ExpectedWorldVersion);
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
    // 快照 payload schema 版本。6→7：CanonicalStateHasher.SchemaVersion 5→6（RationReductionActive 入哈希）后，
    // 本 PR 之前的存档（旧哈希 schema）在新代码下必然哈希校验失败；版本门禁升到 7 使旧快照被显式拒绝
    // （Restore 的版本检查先于哈希校验，返回"不支持实时快照版本"），而不是以哈希失配的偶然失败收场。
    // 旧存档不再兼容，恢复即拒绝（fail-closed），与 doc 08 存档版本约定一致。
    public const int Version = 7;
}
