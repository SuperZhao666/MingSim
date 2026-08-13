using System.Reflection;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Map;
using MingSim.Domain.Military;
using MingSim.Domain.Realtime;
using MingSim.Simulation.Realtime;

namespace MingSim.SmokeTests;

/// <summary>
/// 不依赖第三方测试框架的实时内核边界测试。
/// 每条测试都从公开的 Command/ReadModel 入口验证一条审查红线。
/// </summary>
internal static class Program
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);

    private static int Main()
    {
        try
        {
            RuntimeMustNotExposeWritableWorldState();
            ShouldRejectNonUtcAndInvalidInputs();
            ShouldKeepReadModelCollectionsImmutable();
            ShouldRejectPastTargetStructurally();
            ShouldKeepPausedWorldUnchanged();
            ShouldCommitTimeAndVersionTogether();
            ShouldKeepRemainderIndependentOfFrameSplitting();
            ShouldKeepFrameSplittingHashStableAcrossScheduledEvent();
            ShouldAcceptOnlyOneMovementAndRecheckArrival();
            ShouldKeepStartedAndArrivedEventsObservable();
            ShouldKeepSameTimeSchedulerOrderStable();
            ShouldRollbackFailedScheduledEvent();
            ShouldKeepCanonicalHashCompleteAndCollisionSafe();
            ShouldRestoreQueueAndIdempotencyFromSnapshot();
            ShouldRejectExpiredCommandByExpectedWorldVersion();
            ShouldDisableLegacyTurnCommitPath();
            Console.WriteLine("MingSim 实时内核补审测试全部通过。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"实时内核补审测试失败：{exception.Message}");
            Console.Error.WriteLine(exception.StackTrace);
            return 1;
        }
    }

    private static void RuntimeMustNotExposeWritableWorldState()
    {
        Require(typeof(RealtimeSimulationRuntime).GetProperty("State") is null,
            "Runtime 不能公开活 WorldState");
        Require(typeof(WorldState).GetMethod("AdvanceTo", BindingFlags.Public | BindingFlags.Instance) is null,
            "WorldState 的时间写入口不能公开");
        Require(typeof(WorldState).GetMethod("AddCharacter", BindingFlags.Public | BindingFlags.Instance) is null,
            "WorldState 的初始化写入口不能公开给 UI/Agent");
        Require(typeof(RealtimeSimulationRuntime).GetProperty("ReadModel") is not null,
            "Runtime 必须公开只读 ReadModel");
    }

    private static void ShouldRejectNonUtcAndInvalidInputs()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        var nonUtc = CreateMove(runtime, "non-utc", new DateTimeOffset(FixedUtc.DateTime, TimeSpan.FromHours(8)));
        var receipt = runtime.EnqueueMoveArmy(nonUtc);
        Require(!receipt.Queued && receipt.Errors.Any(error => error.Code == "NON_UTC_COMMAND_TIME"),
            "非 UTC 命令必须被收件箱拒绝");

        RequireThrows<ArgumentOutOfRangeException>(() => runtime.SetSpeed(double.NaN));
        RequireThrows<ArgumentOutOfRangeException>(() => runtime.SetSpeed(double.PositiveInfinity));
        RequireThrows<ArgumentOutOfRangeException>(() => runtime.SetSpeed(0));

        var invalidId = CreateMove(runtime, " ", FixedUtc);
        Require(!runtime.EnqueueMoveArmy(invalidId).Queued, "空 CommandId 必须被拒绝");
        RequireThrows<ArgumentException>(() => new GameTime(new DateTime(2026, 8, 13)));
    }

    private static void ShouldKeepReadModelCollectionsImmutable()
    {
        var model = new RealtimeSimulationRuntime(CreateWorld()).ReadModel;
        var armies = (IList<ArmyReadModel>)model.Armies;
        RequireThrows<NotSupportedException>(() => armies.Add(model.Armies[0]));
        var actions = (IList<ScheduledActionReadModel>)model.ScheduledActions;
        RequireThrows<NotSupportedException>(() => actions.Clear());
    }

    private static void ShouldRejectPastTargetStructurally()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        var current = runtime.ReadModel.GameTime;
        var advanced = runtime.AdvanceTo(new GameTime(current.Value.AddHours(1)));
        var beforeVersion = advanced.ReadModel.WorldVersion;
        var rejected = runtime.AdvanceTo(new GameTime(current.Value.AddMinutes(30)));

        Require(!rejected.Succeeded, "过去目标必须返回失败结果");
        Require(rejected.Errors.Any(error => error.Code == "TARGET_GAME_TIME_IN_PAST"), "过去目标必须有结构化错误码");
        Require(rejected.ReadModel.WorldVersion == beforeVersion, "拒绝过去目标不能产生提交");
    }

    private static void ShouldKeepPausedWorldUnchanged()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        runtime.SetPaused(true);
        var before = runtime.ReadModel;
        var result = runtime.AdvanceTo(new GameTime(before.GameTime.Value.AddDays(2)));

        Require(result.Succeeded, "暂停请求本身不是错误");
        Require(result.ReadModel.GameTime == before.GameTime, "暂停时游戏时间不能推进");
        Require(result.ReadModel.WorldVersion == before.WorldVersion, "暂停时世界版本不能变化");
        Require(result.ReadModel.StateHash == before.StateHash, "暂停时 canonical hash 不能变化");
    }

    private static void ShouldCommitTimeAndVersionTogether()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        var before = runtime.ReadModel;
        var result = runtime.AdvanceTo(new GameTime(before.GameTime.Value.AddHours(1)));

        Require(result.ReadModel.GameTime.Value == before.GameTime.Value.AddHours(1), "目标时间必须成为权威时间");
        Require(result.ReadModel.WorldVersion == before.WorldVersion + 1, "权威时间推进必须同时增加 WorldVersion");
        Require(result.ReadModel.CommitId.StartsWith("time-", StringComparison.Ordinal), "时间提交必须有稳定 CommitId");
        Require(result.Events.Any(domainEvent => domainEvent.EventType == "TimeAdvanced"), "时间提交必须产生可观察事件");
    }

    private static void ShouldKeepRemainderIndependentOfFrameSplitting()
    {
        var oneFrame = new RealtimeSimulationRuntime(CreateWorld());
        var splitFrames = new RealtimeSimulationRuntime(CreateWorld());
        oneFrame.Advance(TimeSpan.FromSeconds(1));
        splitFrames.Advance(TimeSpan.FromMilliseconds(400));
        splitFrames.Advance(TimeSpan.FromMilliseconds(600));

        Require(oneFrame.ReadModel.GameTime == splitFrames.ReadModel.GameTime,
            "现实帧切分不能改变累计后的 GameTime");
        Require(oneFrame.ReadModel.ScheduledActions.SequenceEqual(splitFrames.ReadModel.ScheduledActions),
            "现实帧切分不能改变 Scheduler");
    }

    private static void ShouldKeepFrameSplittingHashStableAcrossScheduledEvent()
    {
        var oneFrame = new RealtimeSimulationRuntime(CreateWorld());
        var splitFrames = new RealtimeSimulationRuntime(CreateWorld());
        Require(oneFrame.EnqueueMoveArmy(CreateMove(oneFrame, "hash-event", FixedUtc, 0, 2)).Queued, "事件测试命令应该进入收件箱");
        Require(splitFrames.EnqueueMoveArmy(CreateMove(splitFrames, "hash-event", FixedUtc, 0, 2)).Queued, "事件测试命令应该进入收件箱");
        oneFrame.AdvanceTo(oneFrame.ReadModel.GameTime);
        splitFrames.AdvanceTo(splitFrames.ReadModel.GameTime);
        oneFrame.Advance(TimeSpan.FromSeconds(6));
        splitFrames.Advance(TimeSpan.FromSeconds(1));
        splitFrames.Advance(TimeSpan.FromSeconds(5));
        Require(oneFrame.ReadModel.StateHash == splitFrames.ReadModel.StateHash,
            "跨越到期事件时，帧切分不能改变最终 canonical hash");
        Require(oneFrame.ReadModel.WorldVersion == splitFrames.ReadModel.WorldVersion,
            "跨越到期事件时，帧切分不能改变最终 WorldVersion");
    }

    private static void ShouldAcceptOnlyOneMovementAndRecheckArrival()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        var first = CreateMove(runtime, "move-1", FixedUtc, expectedVersion: 0, travelHours: 2);
        Require(runtime.EnqueueMoveArmy(first).Queued, "第一条行军应该进入收件箱");
        var accepted = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(accepted.CommandResults.Single().Accepted, "安全点应该接纳第一条行军");
        Require(accepted.ReadModel.Movements.Count == 1, "接纳命令必须建立唯一 MovementState");

        var second = CreateMove(runtime, "move-2", FixedUtc, expectedVersion: accepted.ReadModel.WorldVersion, travelHours: 2);
        Require(runtime.EnqueueMoveArmy(second).Queued, "第二条行军应该进入收件箱");
        var rejected = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(rejected.CommandResults.Single().Errors.Any(error => error.Code == "ARMY_ALREADY_IN_TRANSIT"),
            "在途军队必须拒绝冲突行军");

        var arrived = runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddHours(2)));
        Require(arrived.ReadModel.Movements.Count == 0, "抵达成功后 MovementState 必须清除");
        Require(arrived.ReadModel.Armies.Single().LocationId == new ProvinceId("capital"), "抵达必须更新只读军队视图");
    }

    private static void ShouldKeepStartedAndArrivedEventsObservable()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        Require(runtime.EnqueueMoveArmy(CreateMove(runtime, "visible", FixedUtc, 0, 1)).Queued, "命令应该进入收件箱");
        var accepted = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(accepted.Events.Any(domainEvent => domainEvent.EventType == "ArmyMarchStarted"), "started 事件必须在接纳报告中可见");
        Require(runtime.OutboxEvents.Any(domainEvent => domainEvent.EventType == "ArmyMarchStarted"), "started 事件不能被下一次 Advance 清空");

        var arrived = runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddHours(1)));
        Require(arrived.Events.Any(domainEvent => domainEvent.EventType == "ArmyArrived"), "arrived 事件必须在到期报告中可见");
        Require(runtime.OutboxEvents.Any(domainEvent => domainEvent.EventType == "ArmyArrived"), "arrived 事件必须进入 outbox");
    }

    private static void ShouldKeepSameTimeSchedulerOrderStable()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        var command = CreateMove(runtime, "same-time-order", FixedUtc, 0, 24);
        Require(runtime.EnqueueMoveArmy(command).Queued, "同刻顺序测试命令应该进入收件箱");
        var accepted = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(accepted.Events.Any(domainEvent => domainEvent.EventType == "ArmyMarchStarted"), "同刻顺序测试必须先记录接纳事件");

        var arrival = runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddHours(24)));
        var eventTypes = arrival.Events.Select(domainEvent => domainEvent.EventType).ToArray();
        Require(eventTypes.SequenceEqual(["ArmyArrived", "DailySimulationTick"]),
            "同刻事件必须按 phase、priority、creation sequence 稳定排序");
    }

    private static void ShouldRollbackFailedScheduledEvent()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        var snapshot = runtime.CaptureSnapshot();
        var field = typeof(RealtimeSimulationRuntime).GetField("_scheduledEvents", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var actions = (List<ScheduledSimulationEvent>)field.GetValue(runtime)!;
        actions.Add(new ScheduledSimulationEvent("broken", new GameTime(runtime.ReadModel.GameTime.Value.AddHours(1)), 1, 0, 999, "Unknown", new Dictionary<string, string>()));
        var before = runtime.ReadModel;

        var result = runtime.AdvanceTo(new GameTime(before.GameTime.Value.AddHours(1)));

        Require(!result.Succeeded && result.Errors.Any(error => error.Code == "SCHEDULED_EVENT_FAILED"), "异常事件必须返回结构化失败");
        Require(result.ReadModel.GameTime == before.GameTime, "异常事件不能提交时间");
        Require(result.ReadModel.ScheduledActions.Any(action => action.EventId == "broken"), "异常事件不能从权威队列丢失");
        Require(runtime.CaptureSnapshot().StateHash != snapshot.StateHash, "测试注入的损坏队列应进入诊断哈希");
    }

    private static void ShouldKeepCanonicalHashCompleteAndCollisionSafe()
    {
        var first = new RealtimeSimulationRuntime(CreateWorld("world-a", "name|with:delimiters"));
        var second = new RealtimeSimulationRuntime(CreateWorld("world-a", "name"));
        Require(first.StateHash != second.StateHash, "影响未来行为的字符串变化必须改变 canonical hash");
        Require(CanonicalStateHasher.SchemaVersion >= 2, "canonical hash 必须带明确 schema 版本");
        Require(!File.Exists(Path.Combine("src", "Ming.Simulation", "Realtime", "RealtimeStateHasher.cs")),
            "不能保留第二套实时 StateHasher");
    }

    private static void ShouldRestoreQueueAndIdempotencyFromSnapshot()
    {
        var pendingRuntime = new RealtimeSimulationRuntime(CreateWorld());
        var pendingCommand = CreateMove(pendingRuntime, "pending-snapshot", FixedUtc, 0, 2);
        Require(pendingRuntime.EnqueueMoveArmy(pendingCommand).Queued, "快照前收件箱命令应该进入队列");
        var pendingSnapshot = pendingRuntime.CaptureSnapshot();
        var pendingRestored = RealtimeSimulationRuntime.Restore(pendingSnapshot);
        Require(pendingRestored.ReadModel.StateHash == pendingRuntime.ReadModel.StateHash,
            "恢复后待处理收件箱也必须保持 canonical hash");
        var pendingOriginalResult = pendingRuntime.AdvanceTo(pendingRuntime.ReadModel.GameTime);
        var pendingRestoredResult = pendingRestored.AdvanceTo(pendingRestored.ReadModel.GameTime);
        Require(pendingOriginalResult.ReadModel.StateHash == pendingRestoredResult.ReadModel.StateHash,
            "恢复后的收件箱命令必须按同一顺序接纳");

        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        var command = CreateMove(runtime, "snapshot-move", FixedUtc, 0, 2);
        Require(runtime.EnqueueMoveArmy(command).Queued, "快照前命令应该进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        var snapshot = runtime.CaptureSnapshot();
        var restored = RealtimeSimulationRuntime.Restore(snapshot);

        Require(restored.ReadModel.StateHash == runtime.ReadModel.StateHash, "恢复后 canonical hash 必须一致");
        var duplicate = restored.EnqueueMoveArmy(command);
        var originalDuplicate = runtime.EnqueueMoveArmy(command);
        var duplicateResult = restored.AdvanceTo(restored.ReadModel.GameTime);
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(duplicate.Queued && duplicateResult.CommandResults.Single().Accepted, "恢复后相同命令必须保持幂等结果");
        Require(originalDuplicate.Queued, "原运行时也应该接纳相同的幂等重试");
        var target = new GameTime(runtime.ReadModel.GameTime.Value.AddHours(2));
        var originalArrival = runtime.AdvanceTo(target);
        var restoredArrival = restored.AdvanceTo(target);
        Require(originalArrival.ReadModel.StateHash == restoredArrival.ReadModel.StateHash, "恢复后的队列推进必须与原运行时一致");
    }

    private static void ShouldRejectExpiredCommandByExpectedWorldVersion()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddHours(1)));
        var command = CreateMove(runtime, "expired", FixedUtc, expectedVersion: 0, travelHours: 1);
        Require(runtime.EnqueueMoveArmy(command).Queued, "过期命令仍应进入收件箱等待安全点判定");
        var result = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(!result.CommandResults.Single().Accepted, "过期命令不能被接纳");
        Require(result.CommandResults.Single().Errors.Any(error => error.Code == "STATE_VERSION_CONFLICT"), "过期命令必须返回版本冲突");
    }

    private static void ShouldDisableLegacyTurnCommitPath()
    {
        var kernel = new MingSim.Simulation.SimulationKernel();
        var result = kernel.ResolveTurn(CreateWorld(), []);
        Require(!result.Committed, "旧回合路径不能提交");
        Require(result.Errors.Any(error => error.Code == "LEGACY_TURN_PATH_DISABLED"), "旧回合路径必须明确返回封存错误");
    }

    private static MoveArmyCommand CreateMove(
        RealtimeSimulationRuntime runtime,
        string commandId,
        DateTimeOffset submittedAt,
        long expectedVersion = 0,
        int travelHours = 2) =>
        new(commandId, new CharacterId("war"), new ArmyId("army-1"), new ProvinceId("capital"), submittedAt, expectedVersion, travelHours);

    private static WorldState CreateWorld(string worldId = "smoke-world", string characterName = "兵部角色")
    {
        var map = new MapDefinition(
            "smoke-map",
            [
                new ProvinceDefinition(new ProvinceId("frontier"), "边地", [new ProvinceId("capital")]),
                new ProvinceDefinition(new ProvinceId("capital"), "京师", [new ProvinceId("frontier")]),
            ]);
        return WorldState.CreateInitial(
            new WorldId(worldId),
            1,
            200_000,
            map,
            [new CharacterState(new CharacterId("war"), characterName,
                new CharacterAttributes(60, 40, 80, 50, 60),
                new CharacterPersonality(true, true, true, false))],
            capabilityGrants: [new CapabilityGrant(new CharacterId("war"), GameCapability.MoveArmy, "army-1")],
            armies: [new ArmyState(new ArmyId("army-1"), "测试军", new ProvinceId("frontier"), 10_000, 3_000)]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"应该抛出 {typeof(TException).Name}。");
    }
}
