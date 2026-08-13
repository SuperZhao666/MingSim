using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Common;
using MingSim.Domain.Errors;
using MingSim.Domain.Events;
using MingSim.Domain.Military;
using MingSim.Domain.Realtime;

namespace MingSim.Simulation.Realtime;

/// <summary>
/// 玩家或代理想让实时世界做的一件事。
/// </summary>
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

/// <summary>调度器内部保存的一个未来事件。</summary>
public sealed record ScheduledSimulationEvent(
    string EventId,
    DateTime DueAt,
    string EventType,
    IReadOnlyDictionary<string, string> Data);

/// <summary>实时命令入队后的结果。</summary>
public sealed record RealtimeCommandResult(
    bool Accepted,
    string CommandId,
    string Message,
    IReadOnlyList<SimulationError> Errors);

/// <summary>一次现实时间推进后的只读报告。</summary>
public sealed record RealtimeAdvanceResult(
    WorldState State,
    IReadOnlyList<DomainEvent> Events,
    TimeSpan GameTimeAdvanced,
    int ProcessedScheduledEvents,
    int PendingScheduledEvents,
    bool IsPaused,
    double Speed);

/// <summary>
/// 混合式实时模拟运行时：固定小时基础时钟 + 离散未来事件。
/// </summary>
/// <remarks>
/// 这是从旧“ResolveTurn”骨架迈向实时推演的第一条真正执行链：
///
/// 1. Godot 或 Agent 只能把命令放进这里；
/// 2. 只有这个运行时所属的模拟线程修改 WorldState；
/// 3. 现实时间被转换成游戏时间；
/// 4. 逐小时推进，遇到到期事件就立刻处理；
/// 5. UI 读取 Clone，不拿到可写的权威对象。
///
/// 第一版只实现“军队行军”这个可见玩法切片，但调度器已经能承载公文、
/// 粮队、财政、人物决策等后续事件。LLM 不在主循环里同步等待。
/// </remarks>
public sealed class RealtimeSimulationRuntime
{
    private const double GameHoursPerRealSecondAtSpeedOne = 6.0;
    private readonly CapabilityAuthorizer _authorizer = new();
    private readonly PriorityQueue<ScheduledSimulationEvent, (DateTime DueAt, long Sequence)> _events = new();
    private readonly List<DomainEvent> _eventBuffer = [];
    private long _sequence;

    public RealtimeSimulationRuntime(WorldState initialState)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        State = initialState;
        Speed = 1.0;
        ScheduleDailyHeartbeat();
    }

    /// <summary>模拟线程唯一拥有的权威状态。</summary>
    public WorldState State { get; }

    /// <summary>是否暂停游戏时间。暂停时 UI 仍然可以响应命令和查看地图。</summary>
    public bool IsPaused { get; private set; }

    /// <summary>1 到 5 倍的游戏速度；它只影响时间换算，不改变规则结果。</summary>
    public double Speed { get; private set; }

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
    /// </summary>
    public RealtimeCommandResult EnqueueMoveArmy(MoveArmyCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

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

        var dueAt = State.CurrentTime.AddHours(command.TravelHours);
        Schedule(
            $"army-arrival-{command.CommandId}",
            dueAt,
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
            $"军队 {army.Name} 已从 {army.LocationId} 出发，预计 {dueAt:yyyy-MM-dd HH:mm} 抵达 {command.DestinationId}。",
            ("army_id", command.ArmyId.Value),
            ("from", army.LocationId.Value),
            ("to", command.DestinationId.Value),
            ("due_at", dueAt.ToString("O"))));

        return new RealtimeCommandResult(true, command.CommandId, "行军命令已进入实时调度器。", []);
    }

    /// <summary>
    /// 把一段现实时间换算成游戏时间，并执行所有已经到期的事件。
    /// </summary>
    public RealtimeAdvanceResult Advance(TimeSpan realElapsed)
    {
        if (realElapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(realElapsed), "现实时间不能倒退。");
        }

        _eventBuffer.Clear();
        if (IsPaused || realElapsed == TimeSpan.Zero)
        {
            return Report(TimeSpan.Zero, 0);
        }

        var gameHours = realElapsed.TotalSeconds * GameHoursPerRealSecondAtSpeedOne * Speed;
        var targetTime = State.CurrentTime.AddHours(gameHours);
        var startTime = State.CurrentTime;
        var processed = 0;

        // 固定 1 小时推进，确保事件顺序稳定，也方便以后在小时边界挂载系统。
        while (State.CurrentTime.AddHours(1) <= targetTime)
        {
            State.AdvanceTime(TimeSpan.FromHours(1));
            processed += ProcessDueEvents();
        }

        var remainder = targetTime - State.CurrentTime;
        if (remainder > TimeSpan.Zero)
        {
            State.AdvanceTime(remainder);
            processed += ProcessDueEvents();
        }

        return Report(State.CurrentTime - startTime, processed);
    }

    private int ProcessDueEvents()
    {
        var processed = 0;
        while (_events.TryPeek(out _, out var priority) && priority.DueAt <= State.CurrentTime)
        {
            var scheduled = _events.Dequeue();
            ApplyScheduledEvent(scheduled);
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

    private void ScheduleDailyHeartbeat()
    {
        var nextMidnight = State.CurrentTime.Date.AddDays(1);
        Schedule(
            $"daily-heartbeat-{nextMidnight:yyyyMMdd}",
            nextMidnight,
            "DailyHeartbeat",
            new Dictionary<string, string>());
    }

    private void Schedule(
        string eventId,
        DateTime dueAt,
        string eventType,
        IReadOnlyDictionary<string, string> data)
    {
        _events.Enqueue(
            new ScheduledSimulationEvent(eventId, dueAt, eventType, data),
            (dueAt, _sequence++));
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
            Speed);
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

    private static RealtimeCommandResult Reject(
        string commandId,
        string message,
        string errorCode) =>
        new(false, commandId, message, [new SimulationError(errorCode, message)]);
}
