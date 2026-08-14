using System.Collections.Concurrent;
using System.Reflection;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Decrees;
using MingSim.Domain.Economy;
using MingSim.Domain.Events;
using MingSim.Domain.Map;
using MingSim.Domain.Military;
using MingSim.Domain.Realtime;
using MingSim.Domain.Scenario;
using MingSim.Persistence.InMemory;
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
            ShouldRejectInvalidApplicationKernelInjection();
            ShouldEnforceStrictWorldVersionIncrement();
            ShouldKeepSubhourDueEventExactAndFrameStreamStable();
            ShouldKeepPausedRemainderFrozen();
            ShouldCompleteGrainShipmentAndKeepLedgerBalanced();
            ShouldRejectGrainShipmentPreconditionsWithoutMutation();
            ShouldReplayGrainShipmentIdempotently();
            ShouldRetryBlockedShipmentArrivalAfterCapacityRelease();
            ShouldIgnoreDuplicateShipmentArrivalWithoutDoubleDelivery();
            ShouldRestoreShipmentCheckpointsAndPendingInbox();
            ShouldRejectTamperedSnapshotOutbox();
            ShouldRejectUnknownCommandTypeWithoutMutation();
            ShouldEnforceInTransitRouteAndDestinationReservations();
            ShouldKeepLossDeterministicAcrossHaulLengths();
            ShouldRejectTamperedSnapshotState();
            ShouldCompleteFiveThousandGrainNingyuanClosedLoop();
            ShouldCaptureSnapshotsAtomicallyUnderConcurrency();
            ShouldKeepEventIdentityAndCommitMetadataStable();
            ShouldAuditPauseAndSpeedAsCommands();
            ShouldDisableLegacyTurnCommitPath();
            ShouldRejectDecreeWithoutResponsibleCapability();
            ShouldRejectDecreeWhenTreasuryOrDeadlineInvalid();
            ShouldAcceptDecreeDeductBudgetAndBindShipment();
            ShouldExpireDecreeAtDeadlineAndDropTrust();
            ShouldKeepDecreeIdempotent();
            ShouldTrackFrontierReadinessDaily();
            ShouldFailHardAfterSevenZeroGrainDays();
            ShouldEvaluateEndgameTiers();
            ShouldReplayRiskSamplesDeterministically();
            ShouldKeepShipmentEscortSettlementAndRaidCap();
            ShouldDropEscortWhenSettlementFails();
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
        var control = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(control.CommandResults.Single().Accepted, "暂停控制命令必须经过安全点接纳");
        var before = control.ReadModel;
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
        Require(oneFrame.ReadModel.WorldVersion == splitFrames.ReadModel.WorldVersion &&
                oneFrame.ReadModel.CommitId == splitFrames.ReadModel.CommitId,
            "现实帧切分不能改变最终 Commit");
        Require(oneFrame.StateHash == splitFrames.StateHash,
            $"现实帧切分不能改变未来行为 hash：one={oneFrame.StateHash} split={splitFrames.StateHash} remainderOne={SnapshotRemainder(oneFrame)} remainderSplit={SnapshotRemainder(splitFrames)}");
        Require(EventFingerprints(oneFrame.OutboxEvents).SequenceEqual(EventFingerprints(splitFrames.OutboxEvents)),
            "现实帧切分不能改变完整 outbox/事件流");
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
        Require(EventFingerprints(oneFrame.OutboxEvents).SequenceEqual(EventFingerprints(splitFrames.OutboxEvents)),
            "跨越到期事件时，帧切分不能改变 Commit/事件/outbox 流");
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

    private static void ShouldRejectInvalidApplicationKernelInjection()
    {
        var orchestrator = new MingSim.Application.Workflows.TurnOrchestrator();
        var result = orchestrator.ExecuteTurn(new WorldId("smoke-world"), []);
        Require(!result.Committed && result.Errors.Any(error => error.Code == "LEGACY_TURN_PATH_DISABLED"),
            "旧 Application 编排器必须只能返回隔离拒绝，不能注入可替换 Kernel 提交");
        Require(typeof(MingSim.Application.Workflows.TurnOrchestrator).GetConstructors()
                    .All(constructor => constructor.GetParameters().Length == 0),
            "旧 Application 编排器不能再通过构造函数注入恶意 Kernel/Store");
        Require(typeof(MingSim.Simulation.SimulationKernel).GetInterfaces().Length == 0,
            "旧 SimulationKernel 不能再实现可替换 ISimulationKernel");
    }

    private static void ShouldEnforceStrictWorldVersionIncrement()
    {
        var world = CreateWorld();
        RequireThrows<ArgumentOutOfRangeException>(() => InvokeCommitRealtime(world, 0, "same"));
        RequireThrows<ArgumentOutOfRangeException>(() => InvokeCommitRealtime(world, 2, "skip"));
        InvokeCommitRealtime(world, 1, "first");
        Require(world.WorldVersion == 1 && world.CommitId == "first", "连续实时提交必须严格递增 1");
    }

    private static void ShouldKeepSubhourDueEventExactAndFrameStreamStable()
    {
        var oneFrame = new RealtimeSimulationRuntime(CreateWorld());
        var splitFrames = new RealtimeSimulationRuntime(CreateWorld());
        var dueAt = oneFrame.ReadModel.GameTime.Add(TimeSpan.FromMinutes(30));
        AddScheduledHeartbeat(oneFrame, dueAt, "subhour-heartbeat");
        AddScheduledHeartbeat(splitFrames, dueAt, "subhour-heartbeat");
        var one = oneFrame.Advance(TimeSpan.FromSeconds(10));
        var splitA = splitFrames.Advance(TimeSpan.FromSeconds(5));
        var splitB = splitFrames.Advance(TimeSpan.FromSeconds(5));
        Require(oneFrame.ReadModel.GameTime == splitFrames.ReadModel.GameTime, "同 elapsed 的帧切分必须得到同一游戏时间");
        Require(oneFrame.ReadModel.StateHash == splitFrames.ReadModel.StateHash, "同 elapsed 的帧切分必须得到同一 hash");
        Require(oneFrame.ReadModel.CommitId == splitFrames.ReadModel.CommitId &&
                oneFrame.ReadModel.WorldVersion == splitFrames.ReadModel.WorldVersion,
            "同 elapsed 的帧切分必须得到同一最终 Commit");
        Require(EventFingerprints(oneFrame.OutboxEvents).SequenceEqual(EventFingerprints(splitFrames.OutboxEvents)),
            "同 elapsed 的帧切分必须得到同一 outbox 流");
        var subhourEvents = oneFrame.OutboxEvents.Where(item => item.EventType == "DailySimulationTick").ToArray();
        Require(subhourEvents.Any(item => item.OccurredAt == dueAt.Value),
            "小时边界内到期的离散事件必须在精确 DueAt 结算");
        Require(splitA.Events.Concat(splitB.Events).Any(item => item.OccurredAt == dueAt.Value),
            "拆帧推进也不能把小时内到期事件延迟到整点");
    }

    private static void ShouldKeepPausedRemainderFrozen()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        runtime.Advance(TimeSpan.FromMilliseconds(100));
        var before = runtime.CaptureSnapshot();
        runtime.SetPaused(true);
        var paused = runtime.Advance(TimeSpan.FromSeconds(5));
        Require(paused.CommandResults.Single().Accepted, "暂停必须在现实推进前作为命令接纳");
        runtime.SetPaused(false);
        var resumed = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(resumed.CommandResults.Single().Accepted, "恢复运行必须作为命令接纳");
        var after = runtime.CaptureSnapshot();
        var remainderProperty = typeof(RealtimeSnapshot).GetProperty("RealGameTickRemainder", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Require((decimal)remainderProperty.GetValue(after)! == (decimal)remainderProperty.GetValue(before)!, "暂停期间不能偷跑现实时间余数");
    }

    private static void ShouldCompleteGrainShipmentAndKeepLedgerBalanced()
    {
        var runtime = new RealtimeSimulationRuntime(CreateLogisticsWorld());
        var before = runtime.ReadModel;
        var beforeLedger = GrainLedgerTotal(before);
        var command = CreateShipment(runtime, "grain-normal", 300);

        Require(runtime.EnqueueCreateShipment(command).Queued, "正常粮运命令应该进入收件箱");
        var departed = runtime.AdvanceTo(before.GameTime);
        Require(departed.CommandResults.Single().Accepted, "正常粮运命令应该在安全点接纳");
        Require(departed.Events.Any(item => item.EventType == "ShipmentDeparted"),
            "ShipmentDeparture 必须恢复并可观察");
        Require(departed.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit,
            "出发后运输单必须进入在途状态");
        Require(departed.ReadModel.Stockpiles.Single(item => item.Id == new StockpileId("capital-granary")).GrainQuantity == 700,
            "计划粮运必须先扣除起点库存");
        Require(GrainLedgerTotal(departed.ReadModel) == beforeLedger, "出发后粮食账本必须守恒");

        var arrived = runtime.AdvanceTo(new GameTime(before.GameTime.Value.AddHours(2)));
        var shipment = arrived.ReadModel.Shipments.Single();
        Require(shipment.Status == ShipmentStatus.Arrived, "ShipmentArrival 必须恢复并完成抵达");
        Require(shipment.DeliveredGrain == 270 && shipment.LossGrain == 30,
            "300 粮食按 100‰ 损耗后应交付 270、损耗 30");
        Require(arrived.ReadModel.Stockpiles.Single(item => item.Id == new StockpileId("ningyuan-granary")).GrainQuantity == 270,
            "抵达后目的地必须收到实际交付量");
        Require(GrainLedgerTotal(arrived.ReadModel) == beforeLedger, "抵达损耗也必须满足粮食守恒");
        Require(arrived.Events.Any(item => item.EventType == "ShipmentArrived" &&
                                           item.Data["delivered_grain"] == "270" &&
                                           item.Data["loss_grain"] == "30"),
            "抵达事件必须记录交付量和损耗量");
    }

    private static void ShouldRejectGrainShipmentPreconditionsWithoutMutation()
    {
        var insufficient = new RealtimeSimulationRuntime(CreateLogisticsWorld());
        var insufficientBefore = insufficient.ReadModel;
        Require(insufficient.EnqueueCreateShipment(CreateShipment(insufficient, "grain-short", 1_001)).Queued,
            "库存不足命令应该进入收件箱等待安全点判定");
        var insufficientResult = insufficient.AdvanceTo(insufficientBefore.GameTime);
        Require(!insufficientResult.CommandResults.Single().Accepted &&
                insufficientResult.CommandResults.Single().Errors.Any(item => item.Code == "INSUFFICIENT_GRAIN"),
            "库存不足必须结构化拒绝");
        Require(insufficient.ReadModel.Shipments.Count == 0 &&
                insufficient.ReadModel.WorldVersion == insufficientBefore.WorldVersion,
            "库存不足不能创建运输单或提交世界版本");

        var routeCapacity = new RealtimeSimulationRuntime(CreateLogisticsWorld(routeCapacity: 500));
        var routeBefore = routeCapacity.ReadModel;
        Require(routeCapacity.EnqueueCreateShipment(CreateShipment(routeCapacity, "grain-route-capacity", 501)).Queued,
            "路线容量测试命令应该进入收件箱");
        var routeResult = routeCapacity.AdvanceTo(routeBefore.GameTime);
        Require(routeResult.CommandResults.Single().Errors.Any(item => item.Code == "ROUTE_CAPACITY_EXCEEDED"),
            "超过路线容量必须拒绝");
        Require(routeCapacity.ReadModel.Stockpiles.Single(item => item.Id == new StockpileId("capital-granary")).GrainQuantity == 1_000,
            "路线容量拒绝不能扣除起点库存");

        var destinationCapacity = new RealtimeSimulationRuntime(CreateLogisticsWorld(destinationCapacity: 100));
        var destinationBefore = destinationCapacity.ReadModel;
        Require(destinationCapacity.EnqueueCreateShipment(CreateShipment(destinationCapacity, "grain-destination-capacity", 300)).Queued,
            "目的地容量测试命令应该进入收件箱");
        var destinationResult = destinationCapacity.AdvanceTo(destinationBefore.GameTime);
        Require(destinationResult.CommandResults.Single().Errors.Any(item => item.Code == "DESTINATION_CAPACITY_EXCEEDED"),
            "目的地容量不足必须拒绝");
        Require(destinationCapacity.ReadModel.Shipments.Count == 0 &&
                GrainLedgerTotal(destinationCapacity.ReadModel) == GrainLedgerTotal(destinationBefore),
            "目的地容量拒绝不能改变账本");

        var deliveredCapacity = new RealtimeSimulationRuntime(CreateLogisticsWorld(destinationCapacity: 270));
        Require(deliveredCapacity.EnqueueCreateShipment(CreateShipment(deliveredCapacity, "grain-delivered-capacity", 300)).Queued,
            "实际交付量容量测试命令应该进入收件箱");
        var deliveredCapacityResult = deliveredCapacity.AdvanceTo(deliveredCapacity.ReadModel.GameTime);
        Require(deliveredCapacityResult.CommandResults.Single().Accepted,
            "目的地容量应按实际交付量而非含损耗毛量预留");

        // 拆单不能规避每单向上取整的损耗：4 石拆成两单 2 石，每单损耗 1、总损耗 2，
        // 而单笔 4 石只损耗 1；用可送达的 2 石/单（实到 1）确保断言非空集真空通过。
        var splitLoss = new RealtimeSimulationRuntime(CreateLogisticsWorld(destinationCapacity: 20));
        Require(splitLoss.EnqueueCreateShipment(CreateShipment(splitLoss, "grain-split-a", 2)).Queued,
            "拆单损耗第一条命令应该进入收件箱");
        splitLoss.AdvanceTo(splitLoss.ReadModel.GameTime);
        Require(splitLoss.EnqueueCreateShipment(CreateShipment(splitLoss, "grain-split-b", 2)).Queued,
            "拆单损耗第二条命令应该进入收件箱");
        splitLoss.AdvanceTo(splitLoss.ReadModel.GameTime);
        var splitArrived = splitLoss.AdvanceTo(new GameTime(splitLoss.ReadModel.GameTime.Value.AddHours(2)));
        Require(splitArrived.ReadModel.Shipments.Count == 2 &&
                splitArrived.ReadModel.Shipments.All(item => item.Status == ShipmentStatus.Arrived && item.LossGrain == 1),
            "每单损耗取整必须不能通过拆单规避");
        Require(splitArrived.ReadModel.Shipments.Sum(item => item.LossGrain) == 2 &&
                splitArrived.ReadModel.Shipments.Sum(item => item.DeliveredGrain) == 2,
            "拆单后的总损耗必须按单累加，不能免费合并");

        var denied = new RealtimeSimulationRuntime(CreateLogisticsWorld());
        var deniedBefore = denied.ReadModel;
        var deniedCommand = CreateShipment(denied, "grain-denied", 100, new CharacterId("war"));
        Require(denied.EnqueueCreateShipment(deniedCommand).Queued, "越权命令应该进入收件箱等待判定");
        var deniedResult = denied.AdvanceTo(deniedBefore.GameTime);
        Require(deniedResult.CommandResults.Single().Errors.Any(item => item.Code == "TOOL_SCOPE_DENIED"),
            "没有物流权限的角色不能创建粮运");
        Require(denied.ReadModel.Shipments.Count == 0 && denied.ReadModel.WorldVersion == deniedBefore.WorldVersion,
            "越权命令不能改变世界");
    }

    private static void ShouldReplayGrainShipmentIdempotently()
    {
        var first = new RealtimeSimulationRuntime(CreateLogisticsWorld());
        var replay = new RealtimeSimulationRuntime(CreateLogisticsWorld());
        var firstCommand = CreateShipment(first, "grain-replay", 200);
        var replayCommand = CreateShipment(replay, "grain-replay", 200);
        Require(first.EnqueueCreateShipment(firstCommand).Queued && replay.EnqueueCreateShipment(replayCommand).Queued,
            "重放命令应该进入两个相同收件箱");

        var firstDeparture = first.AdvanceTo(first.ReadModel.GameTime);
        var replayDeparture = replay.AdvanceTo(replay.ReadModel.GameTime);
        Require(EventFingerprints(firstDeparture.Events).SequenceEqual(EventFingerprints(replayDeparture.Events)),
            "相同粮运输入的接纳/出发事件必须一致");

        var firstDuplicate = first.EnqueueCreateShipment(firstCommand);
        var replayDuplicate = replay.EnqueueCreateShipment(replayCommand);
        var firstConflict = first.EnqueueCreateShipment(firstCommand with { GrainQuantity = 201 });
        var replayConflict = replay.EnqueueCreateShipment(replayCommand with { GrainQuantity = 201 });
        Require(firstDuplicate.Queued && replayDuplicate.Queued && firstConflict.Queued && replayConflict.Queued,
            "幂等重试和冲突命令都应进入安全点判定");

        var firstDuplicateResult = first.AdvanceTo(first.ReadModel.GameTime);
        var replayDuplicateResult = replay.AdvanceTo(replay.ReadModel.GameTime);
        Require(firstDuplicateResult.CommandResults.Select(item => $"{item.Accepted}:{string.Join(',', item.Errors.Select(error => error.Code))}")
                    .SequenceEqual(replayDuplicateResult.CommandResults.Select(item => $"{item.Accepted}:{string.Join(',', item.Errors.Select(error => error.Code))}")),
            "幂等重放必须返回同一 Outcome");

        first.Advance(TimeSpan.FromSeconds(1));
        replay.Advance(TimeSpan.FromMilliseconds(500));
        replay.Advance(TimeSpan.FromMilliseconds(500));
        Require(first.ReadModel.Shipments.Single().Status == ShipmentStatus.Arrived &&
                replay.ReadModel.Shipments.Single().Status == ShipmentStatus.Arrived,
            "重放不能改变运输终态");
        Require(first.StateHash == replay.StateHash &&
                EventFingerprints(first.OutboxEvents).SequenceEqual(EventFingerprints(replay.OutboxEvents)),
            "相同命令、重试和帧切分必须得到同一 hash 与事件流");
    }

    private static void ShouldRetryBlockedShipmentArrivalAfterCapacityRelease()
    {
        var runtime = new RealtimeSimulationRuntime(CreateLogisticsWorld(destinationCapacity: 100));
        var start = runtime.ReadModel.GameTime;
        Require(runtime.EnqueueCreateShipment(CreateShipment(runtime, "grain-retry", 100)).Queued,
            "重试测试命令应该进入收件箱");
        runtime.AdvanceTo(start);

        var destination = GetRuntimeState(runtime).Logistics.Stockpiles[new StockpileId("ningyuan-granary")];
        Require(InvokeStockpileMutation(destination, "TryStoreGrain", 20), "测试需要先占用目的地剩余容量");
        var blocked = runtime.AdvanceTo(new GameTime(start.Value.AddHours(2)));
        Require(blocked.Events.Any(item => item.EventType == "ShipmentArrivalBlocked"),
            "目的地满时必须保留在途状态并安排重试");
        Require(blocked.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit,
            "受阻抵达不能把运输单错误标记为已完成");

        destination = GetRuntimeState(runtime).Logistics.Stockpiles[new StockpileId("ningyuan-granary")];
        Require(InvokeStockpileMutation(destination, "TryTakeGrain", 20), "测试应释放目的地容量");
        var retry = runtime.AdvanceTo(new GameTime(blocked.ReadModel.GameTime.Value.AddHours(1)));
        Require(retry.Events.Any(item => item.EventType == "ShipmentArrived") &&
                retry.ReadModel.Shipments.Single().Status == ShipmentStatus.Arrived,
            "释放容量后下一次 ShipmentArrival 重试必须成功");
        Require(GrainLedgerTotal(retry.ReadModel) == GrainLedgerTotal(new RealtimeSimulationRuntime(CreateLogisticsWorld()).ReadModel),
            "重试成功后粮食账本仍必须守恒");
    }

    private static void ShouldIgnoreDuplicateShipmentArrivalWithoutDoubleDelivery()
    {
        var runtime = new RealtimeSimulationRuntime(CreateLogisticsWorld());
        var start = runtime.ReadModel.GameTime;
        Require(runtime.EnqueueCreateShipment(CreateShipment(runtime, "grain-duplicate-arrival", 100)).Queued,
            "重复抵达测试命令应该进入收件箱");
        runtime.AdvanceTo(start);

        var actions = (List<ScheduledSimulationEvent>)typeof(RealtimeSimulationRuntime)
            .GetField("_scheduledEvents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime)!;
        var arrival = actions.Single(item => item.EventType == "ShipmentArrival");
        var duplicate = new ScheduledSimulationEvent("duplicate-shipment-arrival", arrival.DueGameTime,
            arrival.Phase, arrival.Priority, actions.Max(item => item.CreationSequence) + 1,
            arrival.EventType, arrival.Data, arrival.CausalCommandId, arrival.SchemaVersion);
        actions.Add(duplicate);

        var result = runtime.AdvanceTo(new GameTime(start.Value.AddHours(2)));
        Require(result.Events.Any(item => item.EventType == "ShipmentArrived") &&
                result.Events.Any(item => item.EventType == "ShipmentArrivalIgnored"),
            "重复抵达动作必须留下可审计的幂等忽略事件");
        Require(result.ReadModel.Stockpiles.Single(item => item.Id == new StockpileId("ningyuan-granary")).GrainQuantity == 90,
            "重复抵达不能二次入库");
    }

    private static void ShouldRestoreShipmentCheckpointsAndPendingInbox()
    {
        var planned = new RealtimeSimulationRuntime(CreateLogisticsWorld());
        var plannedCommand = CreateShipment(planned, "checkpoint-planned", 200);
        Require(planned.EnqueueCreateShipment(plannedCommand).Queued, "计划阶段命令应该进入收件箱");
        var plannedSnapshot = planned.CaptureSnapshot();
        var plannedRestored = RealtimeSimulationRuntime.Restore(plannedSnapshot);
        Require(plannedRestored.StateHash == planned.StateHash, "恢复前的待处理收件箱 hash 必须一致");
        var plannedOriginal = planned.AdvanceTo(planned.ReadModel.GameTime);
        var plannedReplay = plannedRestored.AdvanceTo(plannedRestored.ReadModel.GameTime);
        Require(plannedOriginal.ReadModel.StateHash == plannedReplay.ReadModel.StateHash &&
                EventFingerprints(plannedOriginal.Events).SequenceEqual(EventFingerprints(plannedReplay.Events)),
            "恢复后的计划运输必须按同一顺序接纳和出发");

        var inTransitSnapshot = planned.CaptureSnapshot();
        var inTransitRestored = RealtimeSimulationRuntime.Restore(inTransitSnapshot);
        var target = new GameTime(planned.ReadModel.GameTime.Value.AddHours(2));
        var originalArrival = planned.AdvanceTo(target);
        var restoredArrival = inTransitRestored.AdvanceTo(target);
        Require(originalArrival.ReadModel.StateHash == restoredArrival.ReadModel.StateHash &&
                EventFingerprints(originalArrival.Events).SequenceEqual(EventFingerprints(restoredArrival.Events)),
            "恢复后的在途运输必须按同一队列和幂等状态抵达");
    }

    private static void ShouldRejectTamperedSnapshotOutbox()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        Require(runtime.EnqueueMoveArmy(CreateMove(runtime, "snapshot-outbox", FixedUtc)).Queued,
            "快照 outbox 测试命令应该进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        var snapshot = runtime.CaptureSnapshot();
        var outbox = (DomainEvent[])typeof(RealtimeSnapshot)
            .GetProperty("OutboxEvents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(snapshot)!;
        Require(outbox.Length > 0, "篡改测试必须有可验证的 outbox 事件");
        var original = outbox[0];
        outbox[0] = new DomainEvent(original.EventId, original.WorldId, original.TurnNumber, original.EventType,
            original.Description + " tampered", original.Data, original.OccurredAt, original.EventSequence,
            original.WorldVersion, original.CommitId, original.CausalCommandId);
        RequireThrows<InvalidDataException>(() => RealtimeSimulationRuntime.Restore(snapshot));
    }

    private static void ShouldRejectUnknownCommandTypeWithoutMutation()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        var before = runtime.ReadModel;
        var inbox = (ConcurrentQueue<RealtimeCommand>)typeof(RealtimeSimulationRuntime)
            .GetField("_inbox", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime)!;
        inbox.Enqueue(new UnknownCommand("unknown-command", new CharacterId("war"), FixedUtc, before.WorldVersion));

        var result = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(result.CommandResults.Single().Errors.Any(error => error.Code == "UNKNOWN_COMMAND"),
            "未知命令类型必须结构化拒绝而不是崩溃");
        // 拒绝必须不改变权威版本与游戏时间；Outcome 仍会按设计原子记录，
        // 因此 StateHash 允许变化（幂等记录本身属于权威状态），但不产生业务状态。
        Require(result.ReadModel.WorldVersion == before.WorldVersion && result.ReadModel.GameTime == before.GameTime,
            "未知命令拒绝不能改变世界版本或游戏时间");
        Require(runtime.ReadModel.Shipments.Count == 0 && runtime.ReadModel.Movements.Count == 0,
            "未知命令拒绝不能产生任何业务状态");

        inbox.Enqueue(new UnknownCommand("unknown-command", new CharacterId("war"), FixedUtc, before.WorldVersion));
        var replay = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(replay.CommandResults.Single().Errors.Any(error => error.Code == "UNKNOWN_COMMAND") &&
                replay.CommandResults.Single().Message.Contains("幂等", StringComparison.Ordinal),
            "未知命令重放必须按幂等记录返回原拒绝结果");
    }

    private static void ShouldEnforceInTransitRouteAndDestinationReservations()
    {
        // 路线在途预留：第一单 300 在途后，第二单 201 超过剩余 200 容量必须拒绝
        var routeRuntime = new RealtimeSimulationRuntime(CreateLogisticsWorld(routeCapacity: 500));
        Require(routeRuntime.EnqueueCreateShipment(CreateShipment(routeRuntime, "grain-route-a", 300)).Queued,
            "路线预留第一单应进入收件箱");
        var routeFirst = routeRuntime.AdvanceTo(routeRuntime.ReadModel.GameTime);
        Require(routeFirst.CommandResults.Single().Accepted, "路线预留第一单必须接纳");
        Require(routeRuntime.EnqueueCreateShipment(CreateShipment(routeRuntime, "grain-route-b", 201)).Queued,
            "路线预留第二单应进入收件箱");
        var routeSecond = routeRuntime.AdvanceTo(routeRuntime.ReadModel.GameTime);
        Require(!routeSecond.CommandResults.Single().Accepted &&
                routeSecond.CommandResults.Single().Errors.Any(error => error.Code == "ROUTE_CAPACITY_EXCEEDED"),
            "在途运输必须计入路线容量，超出的第二单必须拒绝");
        Require(routeRuntime.ReadModel.Stockpiles.Single(item => item.Id == new StockpileId("capital-granary")).GrainQuantity == 1_000 - 300,
            "路线容量拒绝不能扣除第二单起点库存");

        // 目的地在途预留：第一单 500（实到 450 预留）后，第二单 60（实到 54）超过剩余 50 必须拒绝。
        // 路线容量放大到 1000，确保先命中的是目的地容量而不是路线容量。
        var destinationRuntime = new RealtimeSimulationRuntime(CreateLogisticsWorld(destinationCapacity: 500, routeCapacity: 1_000));
        Require(destinationRuntime.EnqueueCreateShipment(CreateShipment(destinationRuntime, "grain-dest-a", 500)).Queued,
            "目的地预留第一单应进入收件箱");
        var destinationFirst = destinationRuntime.AdvanceTo(destinationRuntime.ReadModel.GameTime);
        Require(destinationFirst.CommandResults.Single().Accepted, "目的地预留第一单必须接纳");
        Require(destinationRuntime.EnqueueCreateShipment(CreateShipment(destinationRuntime, "grain-dest-b", 60)).Queued,
            "目的地预留第二单应进入收件箱");
        var destinationSecond = destinationRuntime.AdvanceTo(destinationRuntime.ReadModel.GameTime);
        Require(!destinationSecond.CommandResults.Single().Accepted &&
                destinationSecond.CommandResults.Single().Errors.Any(error => error.Code == "DESTINATION_CAPACITY_EXCEEDED"),
            "在途实到量必须计入目的地容量预留，超出的第二单必须拒绝");
        Require(destinationRuntime.ReadModel.Shipments.Count(item => item.Id.Value.StartsWith("shipment-grain-dest")) == 1,
            "目的地容量拒绝不能创建第二张运输单");
    }

    private static void ShouldKeepLossDeterministicAcrossHaulLengths()
    {
        // 同一损耗率下，长途与短途的实到/损耗必须一致（损耗只由数量与损耗率决定，与行程时长无关）
        var shortHaul = new RealtimeSimulationRuntime(CreateNingyuanWorld(travelHours: 2));
        var longHaul = new RealtimeSimulationRuntime(CreateNingyuanWorld(travelHours: 24 * 30));
        Require(shortHaul.EnqueueCreateShipment(CreateShipment(shortHaul, "short-haul", 5_000)).Queued,
            "短途命令应进入收件箱");
        Require(longHaul.EnqueueCreateShipment(CreateShipment(longHaul, "long-haul", 5_000)).Queued,
            "长途命令应进入收件箱");
        shortHaul.AdvanceTo(shortHaul.ReadModel.GameTime);
        longHaul.AdvanceTo(longHaul.ReadModel.GameTime);

        var early = longHaul.AdvanceTo(new GameTime(longHaul.ReadModel.GameTime.Value.AddDays(29)));
        Require(early.ReadModel.Shipments.Single(item => item.Id.Value == "shipment-long-haul").Status == ShipmentStatus.InTransit,
            "长途运输在计划到达前必须仍然在途，不能被提前结算");
        var shortArrival = shortHaul.AdvanceTo(new GameTime(shortHaul.ReadModel.GameTime.Value.AddHours(2)));
        var longArrival = longHaul.AdvanceTo(new GameTime(longHaul.ReadModel.GameTime.Value.AddDays(30)));
        var shortShipment = shortArrival.ReadModel.Shipments.Single(item => item.Id.Value == "shipment-short-haul");
        var longShipment = longArrival.ReadModel.Shipments.Single(item => item.Id.Value == "shipment-long-haul");
        Require(shortShipment.DeliveredGrain == 4_600 && shortShipment.LossGrain == 400,
            "短途 5000 石按 8% 损耗应实到 4600、损耗 400");
        Require(longShipment.DeliveredGrain == 4_600 && longShipment.LossGrain == 400,
            "长途 5000 石按 8% 损耗必须与短途完全一致");
        Require(GrainLedgerTotal(shortArrival.ReadModel) == GrainLedgerTotal(new RealtimeSimulationRuntime(CreateNingyuanWorld(travelHours: 2)).ReadModel),
            "短途抵达后粮食账本必须守恒");

        // 每单向上取整与行程时长无关：1 石在 8% 损耗下损耗 1 石、实到 0。
        // 直接验证领域公式的确定性；实到 0 的运输单会在创建时被目的地容量检查拒绝，
        // 所以这里同时断言“拒绝”这一端到端结果，而不是虚构一次送达。
        var roundingShort = new RouteState(new RouteId("rounding-short"), new StockpileId("a"), new StockpileId("b"), 10_000, 2, 80);
        var roundingLong = new RouteState(new RouteId("rounding-long"), new StockpileId("a"), new StockpileId("b"), 10_000, 24 * 30, 80);
        var shortCalculable = roundingShort.TryCalculateDeliveredGrain(1, out var oneShortDelivered, out var oneShortLoss);
        var longCalculable = roundingLong.TryCalculateDeliveredGrain(1, out var oneLongDelivered, out var oneLongLoss);
        Require(shortCalculable && longCalculable, "单石损耗公式必须可计算");
        Require(oneShortDelivered == 0 && oneShortLoss == 1 &&
                oneLongDelivered == oneShortDelivered && oneLongLoss == oneShortLoss,
            "单石向上取整损耗必须确定，且与行程时长无关");

        var single = new RealtimeSimulationRuntime(CreateNingyuanWorld(travelHours: 24 * 30));
        Require(single.EnqueueCreateShipment(CreateShipment(single, "long-single", 1)).Queued,
            "单石长途命令应进入收件箱");
        var singleBefore = single.ReadModel.WorldVersion;
        var singleResult = single.AdvanceTo(single.ReadModel.GameTime);
        Require(!singleResult.CommandResults.Single().Accepted &&
                singleResult.CommandResults.Single().Errors.Any(error => error.Code == "DESTINATION_CAPACITY_EXCEEDED"),
            "实到为 0 的单石运输必须在创建时被目的地容量检查拒绝");
        Require(single.ReadModel.WorldVersion == singleBefore && single.ReadModel.Shipments.Count == 0,
            "单石运输拒绝不能创建运输单或提交版本");
    }

    private static void ShouldRejectTamperedSnapshotState()
    {
        var runtime = new RealtimeSimulationRuntime(CreateLogisticsWorld());
        Require(runtime.EnqueueCreateShipment(CreateShipment(runtime, "tamper-state", 200)).Queued,
            "状态篡改测试命令应进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        var snapshot = runtime.CaptureSnapshot();
        var stockpile = GetSnapshotState(snapshot).Logistics.Stockpiles[new StockpileId("capital-granary")];
        Require(InvokeStockpileMutation(stockpile, "TryTakeGrain", 1), "测试必须先篡改库存字节");
        RequireThrows<InvalidDataException>(() => RealtimeSimulationRuntime.Restore(snapshot));
    }

    private static void ShouldCompleteFiveThousandGrainNingyuanClosedLoop()
    {
        // 宁远急饷纸面推演（docs/玩法验证/宁远急饷90日纸面推演.md）L1 批次：
        // 5000 石、8% 损耗、12 游戏日到达、实到 4600；这里是内核级验收，不是纸面复算。
        var runtime = new RealtimeSimulationRuntime(CreateNingyuanWorld());
        var before = runtime.ReadModel;
        var beforeLedger = GrainLedgerTotal(before);
        var command = CreateShipment(runtime, "ningyuan-5000", 5_000);
        var store = new InMemorySnapshotStore();
        var journal = new InMemoryAuditJournal();

        // 1) 出发提交原子性：命令与出发恰好两个 +1 Commit，事件与状态同批可见
        Require(runtime.EnqueueCreateShipment(command).Queued, "5000 石命令应进入收件箱");
        var accepted = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(accepted.CommandResults.Single().Accepted, "5000 石计划必须接纳");
        Require(accepted.ReadModel.WorldVersion == before.WorldVersion + 2,
            "出发提交必须恰好包含命令与出发两个 +1 Commit");
        Require(accepted.Events.Count(item => item.EventType == "ShipmentPlanned") == 1,
            "计划事件只能出现一次");
        Require(accepted.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit,
            "出发后必须进入在途");
        Require(accepted.ReadModel.Stockpiles.Single(item => item.Id == new StockpileId("capital-granary")).GrainQuantity == 20_000 - 5_000,
            "出发必须先扣 5000 石");
        Require(GrainLedgerTotal(accepted.ReadModel) == beforeLedger, "出发后粮食账本必须守恒");

        // 2) 持久化往返：在途快照 Prepare/Promote 与真实 Restore 两条路径都必须一致
        var inTransitSnapshot = runtime.CaptureSnapshot();
        var prepared = store.Prepare(GetSnapshotState(inTransitSnapshot), runtime.OutboxEvents);
        Require(prepared.IsValid, "在途快照 Prepare 必须校验通过");
        store.Promote(prepared);
        Require(store.Current is not null && store.Current.StateHash == prepared.StateHash,
            "校验通过的快照必须提升为当前快照");
        var restored = RealtimeSimulationRuntime.Restore(inTransitSnapshot);
        Require(restored.ReadModel.StateHash == runtime.ReadModel.StateHash,
            "恢复实例的 canonical hash 必须与原始一致");

        // 3) 幂等重放：同一命令不得二次扣粮、不得再产生计划/出发事件
        Require(restored.EnqueueCreateShipment(command).Queued, "重放命令应进入恢复实例收件箱");
        var replay = restored.AdvanceTo(restored.ReadModel.GameTime);
        Require(replay.CommandResults.Single().Accepted && replay.CommandResults.Single().Message.Contains("幂等", StringComparison.Ordinal),
            "重放必须按幂等记录返回原结果");
        Require(replay.Events.All(item => item.EventType is not ("ShipmentPlanned" or "ShipmentDeparted")),
            "重放不得再次产生计划或出发事件");
        Require(restored.ReadModel.Stockpiles.Single(item => item.Id == new StockpileId("capital-granary")).GrainQuantity == 15_000,
            "重放不得二次扣粮");
        Require(restored.ReadModel.WorldVersion == accepted.ReadModel.WorldVersion,
            "幂等重放不得再增加 WorldVersion");

        // 4) 并发：并行投递 + 快照捕获必须原子一致；同一版本并发命令只有一个生效
        var concurrent = new RealtimeSimulationRuntime(CreateNingyuanWorld());
        Require(concurrent.EnqueueCreateShipment(CreateShipment(concurrent, "ningyuan-5000", 5_000)).Queued,
            "并发实例主命令应进入收件箱");
        var concurrentMain = concurrent.AdvanceTo(concurrent.ReadModel.GameTime);
        Require(concurrentMain.CommandResults.Single().Accepted, "并发实例主命令必须接纳");
        var concurrentVersion = concurrent.ReadModel.WorldVersion;
        RealtimeSnapshot? captured = null;
        var captureTask = Task.Run(() =>
        {
            for (var index = 0; index < 60; index++)
            {
                captured = concurrent.CaptureSnapshot();
            }
        });
        // 每个并发命令装 2 石：80‰ 损耗下实到 1，确保业务检查能通过，
        // 只有第一个（版本匹配）会被接纳，其余都因版本过期被拒绝。
        var enqueueTasks = Enumerable.Range(0, 40)
            .Select(index => Task.Run(() => concurrent.EnqueueCreateShipment(new CreateShipmentCommand(
                $"concurrent-{index}", new CharacterId("works"), new ShipmentId($"shipment-concurrent-{index}"),
                new RouteId("capital-ningyuan-grain"), 2, FixedUtc, concurrentVersion))))
            .ToArray();
        Task.WaitAll(enqueueTasks.Append(captureTask).ToArray());
        Require(captured is not null, "并发快照线程必须至少捕获一次快照");
        _ = RealtimeSimulationRuntime.Restore(captured!);
        var drained = concurrent.AdvanceTo(concurrent.ReadModel.GameTime);
        Require(drained.CommandResults.Count == 40, "并发命令必须全部进入安全点判定");
        Require(drained.CommandResults.Count(result => result.Accepted) == 1,
            "同一版本并发命令只能有一个被接纳，其余必须版本冲突拒绝");
        Require(concurrent.ReadModel.Shipments.Count(item => item.Id.Value.StartsWith("shipment-concurrent")) == 1,
            "并发只允许一个运输单生效");
        Require(concurrent.ReadModel.Stockpiles.Single(item => item.Id == new StockpileId("capital-granary")).GrainQuantity == 20_000 - 5_000 - 2,
            "并发接纳必须恰好只扣一次粮");

        // 5) 12 游戏日抵达：实到 4600、损耗 400、账本守恒、事件唯一；恢复实例推进到同一目标必须一致
        var target = new GameTime(runtime.ReadModel.GameTime.Value.AddDays(12));
        var arrived = runtime.AdvanceTo(target);
        var shipment = arrived.ReadModel.Shipments.Single(item => item.Id.Value == "shipment-ningyuan-5000");
        Require(shipment.Status == ShipmentStatus.Arrived && shipment.DeliveredGrain == 4_600 && shipment.LossGrain == 400,
            "5000 石按 8% 损耗必须实到 4600、损耗 400");
        Require(arrived.ReadModel.Stockpiles.Single(item => item.Id == new StockpileId("ningyuan-granary")).GrainQuantity == 4_600,
            "宁远库存必须收到实到量");
        Require(GrainLedgerTotal(arrived.ReadModel) == beforeLedger, "全程粮食账本必须守恒");
        Require(arrived.Events.Count(item => item.EventType == "ShipmentArrived") == 1,
            "到达事件只能发生一次");
        Require(arrived.ReadModel.WorldVersion > accepted.ReadModel.WorldVersion,
            "到达推进必须产生新的权威提交");
        // 用一个“没有重放过命令”的独立恢复实例做推进一致性比较：
        // 重放本身会写入 CommandDeduplicated 审计事件并消耗事件序号，属于不同的输入流，
        // 因此与“原实例未收到重复投递”的事件编号必然错位，不能混在同一次比较里。
        var arrivalRestored = RealtimeSimulationRuntime.Restore(inTransitSnapshot);
        var restoredArrival = arrivalRestored.AdvanceTo(target);
        Require(restoredArrival.ReadModel.StateHash == arrived.ReadModel.StateHash &&
                EventFingerprints(restoredArrival.Events).SequenceEqual(EventFingerprints(arrived.Events)),
            "无重放的恢复实例推进到同一目标必须与原实例哈希、事件流一致");

        // 6) 审计持久化：outbox 事件必须可完整写入只追加日志并读回
        journal.Append(runtime.ReadModel.WorldId, runtime.OutboxEvents);
        Require(journal.Read(runtime.ReadModel.WorldId).Count == runtime.OutboxEvents.Count,
            "审计日志必须完整持久化全部 outbox 事件");
    }

    private static void ShouldCaptureSnapshotsAtomicallyUnderConcurrency()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        RealtimeSnapshot? latest = null;
        var captureTask = Task.Run(() =>
        {
            for (var index = 0; index < 80; index++)
            {
                latest = runtime.CaptureSnapshot();
            }
        });
        var enqueueTasks = Enumerable.Range(0, 120)
            .Select(index => Task.Run(() => runtime.EnqueueMoveArmy(CreateMove(runtime, $"concurrent-{index}", FixedUtc))))
            .ToArray();
        Task.WaitAll(enqueueTasks.Append(captureTask).ToArray());
        Require(latest is not null, "并发快照线程必须至少捕获一次快照");
        _ = RealtimeSimulationRuntime.Restore(latest!);

        var finalSnapshot = runtime.CaptureSnapshot();
        var restored = RealtimeSimulationRuntime.Restore(finalSnapshot);
        Require(restored.StateHash == runtime.StateHash, "同一原子边界捕获的最终快照必须可恢复到同一 hash");
    }

    private static void ShouldKeepEventIdentityAndCommitMetadataStable()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        var command = CreateMove(runtime, "event-metadata", FixedUtc, 0, 1);
        Require(runtime.EnqueueMoveArmy(command).Queued, "事件元数据测试命令应该进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddHours(1)));
        var events = runtime.OutboxEvents;
        Require(events.Count > 0 && events.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() == events.Count,
            "所有 DomainEvent 必须拥有唯一 EventId");
        Require(events.Select(item => item.EventSequence).SequenceEqual(Enumerable.Range(0, events.Count).Select(index => (long)index)),
            "EventJournal 序号必须由单写者连续分配");
        Require(events.All(item => item.EventId != command.CommandId && item.WorldVersion > 0 && !string.IsNullOrWhiteSpace(item.CommitId)),
            "业务事件不能复用 CommandId，且必须带 Commit 元数据");
        Require(events.Single(item => item.EventType == "ArmyMarchStarted").CausalCommandId == command.CommandId &&
                events.Single(item => item.EventType == "ArmyArrived").CausalCommandId == command.CommandId,
            "Started/Arrived 必须保留原始命令因果链，而不是复用调度事件编号");

        var journal = new InMemoryAuditJournal();
        journal.Append(runtime.ReadModel.WorldId, events);
        RequireThrows<InvalidOperationException>(() => journal.Append(runtime.ReadModel.WorldId, [events[0]]));
    }

    private static void ShouldAuditPauseAndSpeedAsCommands()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        runtime.Advance(TimeSpan.FromMilliseconds(100));
        var before = runtime.ReadModel;
        runtime.SetPaused(true);
        var paused = runtime.Advance(TimeSpan.FromSeconds(5));
        Require(paused.CommandResults.Single().Accepted && paused.IsPaused,
            "暂停必须作为已接纳控制命令提交");
        Require(paused.ReadModel.WorldVersion == before.WorldVersion + 1 &&
                paused.Events.Any(item => item.EventType == "CommandAccepted"),
            "暂停命令必须有 WorldVersion/Commit/Outcome/事件审计");

        runtime.SetPaused(false);
        var resumed = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(resumed.CommandResults.Single().Accepted && !resumed.IsPaused,
            "恢复运行也必须经过唯一命令管线");

        runtime.SetSpeed(2.0);
        var speed = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(speed.CommandResults.Single().Accepted && speed.ReadModel.WorldVersion == resumed.ReadModel.WorldVersion + 1 &&
                speed.Speed == 2.0 && speed.Events.Any(item => item.EventType == "CommandAccepted"),
            "倍速切换必须作为可审计命令提交，而不能直接改运行时字段");
        var remainderProperty = typeof(RealtimeSnapshot).GetProperty("RealGameTickRemainder", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var remainderBeforeSpeedElapsed = (decimal)remainderProperty.GetValue(runtime.CaptureSnapshot())!;
        Require(remainderBeforeSpeedElapsed > 0, "倍速切换测试必须保留一个未提交的游戏时间余数");
        var gameTimeBeforeSpeedElapsed = runtime.ReadModel.GameTime;
        runtime.Advance(TimeSpan.FromMilliseconds(100));
        var remainderAfterSpeedElapsed = (decimal)remainderProperty.GetValue(runtime.CaptureSnapshot())!;
        Require(runtime.ReadModel.GameTime.Value == gameTimeBeforeSpeedElapsed.Value.AddHours(1) &&
                remainderAfterSpeedElapsed > remainderBeforeSpeedElapsed,
            "切换倍速后必须继续使用既有余数，并按新速度累计后再提交整小时");
    }

    // ===== P0 玩法规则（I2A 宁远急饷）新增验收 =====

    /// <summary>政令权限拒绝：承办人无对应 CapabilityGrant 时，命令被结构化拒绝且世界不变。</summary>
    private static void ShouldRejectDecreeWithoutResponsibleCapability()
    {
        var runtime = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld());
        var before = runtime.ReadModel;
        var command = CreateDecree(runtime, "decree-denied", deadlineDays: 10, responsible: new CharacterId("war"));
        Require(runtime.EnqueueCreateDecree(command).Queued, "政令命令应进入收件箱");
        var result = runtime.AdvanceTo(before.GameTime);

        Require(!result.CommandResults.Single().Accepted, "承办人无能力时必须拒绝政令");
        Require(result.CommandResults.Single().Errors.Any(error => error.Code == "DECREE_RESPONSIBLE_UNAUTHORIZED"),
            "拒绝必须带结构化权限错误码");
        Require(result.Events.Any(domainEvent => domainEvent.EventType == "DecreeRejected"),
            "拒绝必须产生可审计 DecreeRejected 事件");
        Require(runtime.ReadModel.Decrees.Count == 0, "拒绝不能创建政令状态");
        Require(runtime.ReadModel.Scenario.SpentSilver == 0, "拒绝不能扣除预算");
        Require(runtime.ReadModel.WorldVersion == before.WorldVersion && runtime.ReadModel.GameTime == before.GameTime,
            "业务拒绝不能推进世界版本或时间");
    }

    /// <summary>政令预算超国库/期限已过：结构化拒绝，不产生任何世界变化。</summary>
    private static void ShouldRejectDecreeWhenTreasuryOrDeadlineInvalid()
    {
        var runtime = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld());
        var before = runtime.ReadModel;
        var overBudget = CreateDecree(runtime, "decree-over-budget", deadlineDays: 10, budget: long.MaxValue);
        Require(runtime.EnqueueCreateDecree(overBudget).Queued, "超预算政令应进入收件箱");
        var budgetResult = runtime.AdvanceTo(before.GameTime);
        Require(!budgetResult.CommandResults.Single().Accepted &&
                budgetResult.CommandResults.Single().Errors.Any(error => error.Code == "DECREE_BUDGET_EXCEEDS_TREASURY"),
            "预算超过国库必须拒绝 DECREE_BUDGET_EXCEEDS_TREASURY");

        var pastDeadline = CreateDecree(runtime, "decree-past", deadlineDays: 0);
        Require(runtime.EnqueueCreateDecree(pastDeadline).Queued, "过期期限政令应进入收件箱");
        var deadlineResult = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(!deadlineResult.CommandResults.Single().Accepted &&
                deadlineResult.CommandResults.Single().Errors.Any(error => error.Code == "DECREE_DEADLINE_IN_PAST"),
            "期限不晚于当前时间必须拒绝 DECREE_DEADLINE_IN_PAST");
        Require(runtime.ReadModel.Decrees.Count == 0 && runtime.ReadModel.Scenario.SpentSilver == 0,
            "两类非法政令都不能创建状态或扣预算");
    }

    /// <summary>政令接纳：预算扣除、DecreeAccepted 事件；绑定粮运抵达后政令完成。</summary>
    private static void ShouldAcceptDecreeDeductBudgetAndBindShipment()
    {
        var runtime = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld());
        var before = runtime.ReadModel;
        var decree = CreateDecree(runtime, "decree-grain", deadlineDays: 20, budget: 5_000,
            responsible: new CharacterId("works"), linkedShipmentId: "shipment-decree-grain");
        Require(runtime.EnqueueCreateDecree(decree).Queued, "有权限承办人的政令应进入收件箱");
        var accepted = runtime.AdvanceTo(before.GameTime);

        Require(accepted.CommandResults.Single().Accepted, "承办人有对应能力时必须接纳政令");
        Require(accepted.Events.Any(domainEvent => domainEvent.EventType == "DecreeAccepted"),
            "接纳必须产生 DecreeAccepted 事件");
        Require(runtime.ReadModel.Scenario.SpentSilver == 5_000, "接纳必须把预算计入场景支出");
        Require(runtime.ReadModel.Decrees.Single().Status == DecreeStatus.Executing, "接纳后政令进入执行状态");

        var shipment = new CreateShipmentCommand(
            "shipment-decree-grain", new CharacterId("works"), new ShipmentId("shipment-decree-grain"),
            new RouteId("capital-ningyuan-grain"), 5_000, runtime.ReadModel.GameTime.Value, runtime.ReadModel.WorldVersion);
        Require(runtime.EnqueueCreateShipment(shipment).Queued, "绑定粮运应进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        var arrived = runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddDays(12)));

        Require(arrived.Events.Any(domainEvent => domainEvent.EventType == "DecreeCompleted"),
            "绑定单抵达必须触发 DecreeCompleted");
        Require(runtime.ReadModel.Decrees.Single().Status == DecreeStatus.Completed, "绑定单抵达后政令必须完成");
    }

    /// <summary>甩责：政令到期未完成 → DecreeDeadlineExpired 事件、大臣信任 -5。</summary>
    private static void ShouldExpireDecreeAtDeadlineAndDropTrust()
    {
        var runtime = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld());
        var before = runtime.ReadModel;
        var decree = CreateDecree(runtime, "decree-expire", deadlineDays: 2, budget: 2_000);
        Require(runtime.EnqueueCreateDecree(decree).Queued, "期限测试政令应进入收件箱");
        var accepted = runtime.AdvanceTo(before.GameTime);
        Require(accepted.CommandResults.Single().Accepted && runtime.ReadModel.Scenario.MinisterTrust == 50,
            "接纳政令本身不能改变大臣信任");

        var expired = runtime.AdvanceTo(new GameTime(before.GameTime.Value.AddDays(3)));
        Require(expired.Events.Any(domainEvent => domainEvent.EventType == "DecreeDeadlineExpired"),
            "逾期必须产生甩责事件");
        Require(runtime.ReadModel.Decrees.Single().Status == DecreeStatus.Expired, "逾期政令必须作废");
        Require(runtime.ReadModel.Scenario.MinisterTrust == 45, "甩责使大臣信任下降 5 点（DESIGN）");
    }

    /// <summary>政令幂等：同 CommandId 重放返回原结果且不二次扣预算；同号不同内容返回冲突。</summary>
    private static void ShouldKeepDecreeIdempotent()
    {
        var runtime = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld());
        var before = runtime.ReadModel;
        var decree = CreateDecree(runtime, "decree-idem", deadlineDays: 10, budget: 3_000);
        Require(runtime.EnqueueCreateDecree(decree).Queued, "幂等政令应进入收件箱");
        var accepted = runtime.AdvanceTo(before.GameTime);
        Require(accepted.CommandResults.Single().Accepted && runtime.ReadModel.Scenario.SpentSilver == 3_000,
            "首条政令必须接纳并扣预算");

        Require(runtime.EnqueueCreateDecree(decree).Queued, "幂等重试应进入安全点判定");
        var replay = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(replay.CommandResults.Single().Accepted && replay.CommandResults.Single().Message.Contains("幂等", StringComparison.Ordinal),
            "同号同内容必须按幂等记录返回原结果");
        Require(runtime.ReadModel.Scenario.SpentSilver == 3_000 && runtime.ReadModel.Decrees.Count == 1,
            "幂等重放不能二次扣预算或创建第二道政令");

        var conflict = decree with { Budget = 9_999 };
        Require(runtime.EnqueueCreateDecree(conflict).Queued, "同号不同内容应进入安全点判定");
        var conflictResult = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Require(!conflictResult.CommandResults.Single().Accepted &&
                conflictResult.CommandResults.Single().Errors.Any(error => error.Code == "IDEMPOTENCY_CONFLICT"),
            "同 CommandId 携带不同内容必须返回 IDEMPOTENCY_CONFLICT");
        Require(runtime.ReadModel.Scenario.SpentSilver == 3_000, "冲突拒绝不能扣预算");
    }

    /// <summary>前线战备：足额供粮日缓慢恢复、断粮日逐日下降、欠饷累计；到粮后恢复。</summary>
    private static void ShouldTrackFrontierReadinessDaily()
    {
        var runtime = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld());
        var before = runtime.ReadModel;
        Require(before.Readiness.Value == 60 && before.Scenario.LocalBurden == 20 && before.Scenario.MinisterTrust == 50,
            "场景初始值必须符合 doc 03 §7.1：战备 60 / 负担 20 / 信任 50");

        var day10 = runtime.AdvanceTo(new GameTime(before.GameTime.Value.AddDays(10)));
        Require(day10.ReadModel.Stockpiles.Single(item => item.Id.Value == "ningyuan-granary").GrainQuantity == 5_400 - 10 * 300,
            "10 天足额供粮应消耗 3000 石");
        Require(day10.ReadModel.Readiness.ValueBasisPoints == 6_000 + 10 * ReadinessState.DesignFullDayGainBasisPoints,
            "10 天足额供粮应恢复战备 +1（+100 基点）");
        Require(day10.ReadModel.Readiness.ArrearsGrain == 0, "足额供粮不应产生欠饷");

        var day24 = runtime.AdvanceTo(new GameTime(before.GameTime.Value.AddDays(24)));
        Require(day24.ReadModel.Stockpiles.Single(item => item.Id.Value == "ningyuan-granary").GrainQuantity == 0,
            "第 24 天应已完全断粮");
        Require(day24.ReadModel.Readiness.ConsecutiveZeroGrainDays == 6, "第 19..24 天应连续断粮 6 天");
        Require(day24.ReadModel.Readiness.ArrearsGrain == 6 * 300, "6 天断粮应累计欠饷 1800 石");
        Require(day24.ReadModel.Readiness.ValueBasisPoints == 6_100 + 8 * 10 - 6 * 200,
            "战备必须体现足额恢复与断粮衰减（DESIGN 公式）");

        // 到粮缓慢恢复：补运 5000 石（12 日到达），到粮后足额供粮日每天 +0.1。
        Require(runtime.EnqueueCreateShipment(CreateShipment(runtime, "recover", 5_000)).Queued, "补粮命令应进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        var arrival = runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddDays(12)));
        Require(arrival.ReadModel.Stockpiles.Single(item => item.Id.Value == "ningyuan-granary").GrainQuantity > 0,
            "粮队必须到达并恢复库存");
        var atArrival = arrival.ReadModel.Readiness.ValueBasisPoints;
        var afterRecovery = runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddDays(5)));
        Require(afterRecovery.ReadModel.Readiness.ValueBasisPoints == atArrival + 5 * ReadinessState.DesignFullDayGainBasisPoints,
            "到粮后足额供粮日必须每天缓慢恢复 +0.1（+10 基点）");
        Require(afterRecovery.ReadModel.Readiness.ArrearsGrain == arrival.ReadModel.Readiness.ArrearsGrain,
            "恢复供粮后不能再新增欠饷");
    }

    /// <summary>硬失败：连续 7 日可用粮为 0 → EvaluateEndgame 判 HardFailure 并只报告一次。</summary>
    private static void ShouldFailHardAfterSevenZeroGrainDays()
    {
        var runtime = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld(destinationGrain: 0));
        var before = runtime.ReadModel;
        var day7 = runtime.AdvanceTo(new GameTime(before.GameTime.Value.AddDays(7)));
        Require(day7.ReadModel.Readiness.ConsecutiveZeroGrainDays == 7, "第 7 天必须连续断粮 7 天");
        Require(day7.Events.Count(domainEvent => domainEvent.EventType == "ScenarioHardFailure") == 1,
            "达到硬失败条件必须且只能报告一次");

        var evaluation = runtime.EvaluateEndgame();
        Require(evaluation.Outcome == EndgameOutcome.HardFailure, "评估必须判硬失败");
        Require(evaluation.HardFailureReason == "连续7日可用粮为0", "硬失败原因必须明确可解释");
        Require(evaluation.Explanation.Contains("宁远可用粮", StringComparison.Ordinal) &&
                evaluation.Explanation.Contains("前线战备", StringComparison.Ordinal) &&
                evaluation.Explanation.Contains("中央财政", StringComparison.Ordinal) &&
                evaluation.Explanation.Contains("地方负担", StringComparison.Ordinal) &&
                evaluation.Explanation.Contains("大臣信任", StringComparison.Ordinal) &&
                evaluation.Explanation.Contains("执行与审计", StringComparison.Ordinal),
            "终局评估必须输出 doc 03 §7.3 的六个解释维度");
    }

    /// <summary>90 日终局分档：勉强维持/成功/优秀/失败 与 90 日前 InProgress。</summary>
    private static void ShouldEvaluateEndgameTiers()
    {
        var early = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld());
        Require(early.EvaluateEndgame().Outcome == EndgameOutcome.InProgress,
            "90 日前且未硬失败必须返回 InProgress");

        // 失败：终局存粮 0 日（27000 石正好被 90 日消耗光），未达"勉强维持"。
        var failed = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld(destinationGrain: 27_000));
        failed.AdvanceTo(new GameTime(failed.ReadModel.GameTime.Value.AddDays(90)));
        Require(failed.EvaluateEndgame().Outcome == EndgameOutcome.Failed,
            "终局存粮不足 7 日且未硬失败必须判失败");

        // 勉强维持：终局存粮 10 日、战备 69，但不足 18 日成功线。
        var barely = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld(destinationGrain: 30_000));
        barely.AdvanceTo(new GameTime(barely.ReadModel.GameTime.Value.AddDays(90)));
        var barelyEvaluation = barely.EvaluateEndgame();
        Require(barelyEvaluation.AvailableGrainDays == 10 && barelyEvaluation.Outcome == EndgameOutcome.BarelyMaintained,
            $"终局存粮 10 日应判勉强维持，实际 {barelyEvaluation.Outcome}");

        // 成功：终局存粮 20 日、战备 69、负担 20、银未透支。
        var success = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld(destinationGrain: 33_000));
        success.AdvanceTo(new GameTime(success.ReadModel.GameTime.Value.AddDays(90)));
        var successEvaluation = success.EvaluateEndgame();
        Require(successEvaluation.AvailableGrainDays == 20 && successEvaluation.Outcome == EndgameOutcome.Success,
            $"终局存粮 20 日应判成功，实际 {successEvaluation.Outcome}");

        // 优秀：存粮 30 日、战备 80+、信任 60、负担 20、银未透支（战备/信任由测试注入，评估本身可自动检查）。
        var excellentWorld = CreateNingyuanScenarioWorld(destinationGrain: 36_000);
        ReplaceReadiness(excellentWorld, new ReadinessState(7_200));
        ChangeScenarioTrust(excellentWorld, +10);
        var excellent = new RealtimeSimulationRuntime(excellentWorld);
        excellent.AdvanceTo(new GameTime(excellent.ReadModel.GameTime.Value.AddDays(90)));
        var excellentEvaluation = excellent.EvaluateEndgame();
        Require(excellentEvaluation.AvailableGrainDays == 30 && excellentEvaluation.Outcome == EndgameOutcome.Excellent,
            $"终局存粮 30 日且战备/信任达标应判优秀，实际 {excellentEvaluation.Outcome}");
    }

    /// <summary>固定风险样本：相同种子重放同一事件流与 hash；天气延误、袭粮、三份报告各一次。</summary>
    private static void ShouldReplayRiskSamplesDeterministically()
    {
        var first = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld(travelHours: 24 * 24));
        var replay = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld(travelHours: 24 * 24));
        first.ScheduleScenarioRiskSamples();
        replay.ScheduleScenarioRiskSamples();
        Require(first.EnqueueCreateShipment(CreateShipment(first, "risk-a", 5_000)).Queued, "风险样本首批应进入收件箱");
        Require(replay.EnqueueCreateShipment(CreateShipment(replay, "risk-a", 5_000)).Queued, "重放首批应进入收件箱");
        first.AdvanceTo(first.ReadModel.GameTime);
        replay.AdvanceTo(replay.ReadModel.GameTime);

        var firstResult = first.AdvanceTo(new GameTime(first.ReadModel.GameTime.Value.AddDays(40)));
        var replayResult = replay.AdvanceTo(new GameTime(replay.ReadModel.GameTime.Value.AddDays(40)));
        Require(firstResult.ReadModel.StateHash == replayResult.ReadModel.StateHash,
            "相同种子重放必须得到同一 canonical hash");
        Require(EventFingerprints(firstResult.Events).SequenceEqual(EventFingerprints(replayResult.Events)),
            "相同种子重放必须得到同一事件流");

        var delayed = firstResult.Events.Where(domainEvent => domainEvent.EventType == "ShipmentDelayed").ToArray();
        Require(delayed.Length == 1, "固定风险样本必须恰好一次天气延误");
        var delayDays = int.Parse(delayed.Single().Data["delay_days"]);
        Require(delayDays is >= 1 and <= 3, "天气延误天数必须落在确定性抽取的 1..3 日（DESIGN）");

        var attacked = firstResult.Events.Where(domainEvent => domainEvent.EventType == "ShipmentAttacked").ToArray();
        Require(attacked.Length == 1, "固定风险样本必须恰好一次袭粮");
        var lossPercent = int.Parse(attacked.Single().Data["loss_percent"]);
        Require(lossPercent is >= 0 and <= 20, "无护卫批次袭粮损失必须落在 0..20% 上限内");
        var expectedRaidLoss = 5_000L * lossPercent / 100;
        Require(firstResult.ReadModel.Shipments.Single(item => item.Id.Value == "shipment-risk-a").RaidLossGrain == expectedRaidLoss,
            "袭粮损失必须按确定性比例计入运输单");

        var reports = firstResult.Events.Where(domainEvent => domainEvent.EventType == "ScenarioReportReceived").ToArray();
        Require(reports.Length == 3, "固定风险样本必须恰好三份报告");
        Require(reports.Select(item => item.Data["report_id"]).Distinct(StringComparer.Ordinal).Count() == 3,
            "三份报告必须各有独立 report_id");
        foreach (var report in reports)
        {
            Require(int.Parse(report.Data["credibility"]) is >= 50 and <= 95,
                "报告可信度必须落在确定性抽取的 50..95");
            Require(int.Parse(report.Data["age_days"]) is >= 1 and <= 10,
                "报告时效必须落在确定性抽取的 1..10 日");
        }

        Require(firstResult.Events.Any(domainEvent => domainEvent.EventType == "ShipmentArrived"),
            "延误与袭粮后粮队仍必须抵达");
    }

    /// <summary>护卫：出发时每批 +400 两结算；袭粮损失上限更低；抵达仍守恒。</summary>
    private static void ShouldKeepShipmentEscortSettlementAndRaidCap()
    {
        var escorted = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld(travelHours: 24 * 24));
        var unescorted = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld(travelHours: 24 * 24));
        escorted.ScheduleScenarioRiskSamples();
        unescorted.ScheduleScenarioRiskSamples();
        Require(escorted.EnqueueCreateShipment(new CreateShipmentCommand(
            "escort-1", new CharacterId("works"), new ShipmentId("shipment-escort-1"),
            new RouteId("capital-ningyuan-grain"), 5_000, escorted.ReadModel.GameTime.Value, 0, Escort: true)).Queued,
            "护卫批次应进入收件箱");
        Require(unescorted.EnqueueCreateShipment(new CreateShipmentCommand(
            "plain-1", new CharacterId("works"), new ShipmentId("shipment-plain-1"),
            new RouteId("capital-ningyuan-grain"), 5_000, unescorted.ReadModel.GameTime.Value, 0)).Queued,
            "无护卫批次应进入收件箱");
        // 出发安全点：命令接纳后同一推进内完成出发与护卫结算，事件必须在此可见。
        var escortedDeparture = escorted.AdvanceTo(escorted.ReadModel.GameTime);
        var unescortedDeparture = unescorted.AdvanceTo(unescorted.ReadModel.GameTime);

        var escortedResult = escorted.AdvanceTo(new GameTime(escorted.ReadModel.GameTime.Value.AddDays(40)));
        var unescortedResult = unescorted.AdvanceTo(new GameTime(unescorted.ReadModel.GameTime.Value.AddDays(40)));

        Require(escortedDeparture.Events.Any(domainEvent => domainEvent.EventType == "EscortSettlement"),
            "护卫批次出发时必须结算 +400 两");
        Require(escortedResult.Events.All(domainEvent => domainEvent.EventType != "EscortSettlementFailed"),
            "国库充足时护卫结算不能失败");
        Require(!unescortedResult.Events.Any(domainEvent => domainEvent.EventType == "EscortSettlement"),
            "无护卫批次不能产生护卫结算");
        Require(escortedResult.ReadModel.Scenario.SpentSilver == 400 &&
                unescortedResult.ReadModel.Scenario.SpentSilver == 0,
            "护卫费用必须计入场景支出");

        var escortedRaid = escortedResult.Events.Single(domainEvent => domainEvent.EventType == "ShipmentAttacked");
        var unescortedRaid = unescortedResult.Events.Single(domainEvent => domainEvent.EventType == "ShipmentAttacked");
        Require(int.Parse(escortedRaid.Data["loss_percent"]) <= 5, "护卫批次袭粮损失上限必须降到 5%");
        Require(int.Parse(unescortedRaid.Data["loss_percent"]) <= 20, "无护卫批次袭粮损失上限为 20%");

        var escortedShipment = escortedResult.ReadModel.Shipments.Single(item => item.Id.Value == "shipment-escort-1");
        Require(escortedShipment.DeliveredGrain + escortedShipment.LossGrain == escortedShipment.GrainQuantity,
            "袭粮后抵达仍必须满足粮食守恒（实到 + 损耗 = 计划量）");
    }

    /// <summary>护卫费用结算失败：出发前国库被政令耗尽 → 护卫无法成行、护卫标记清除、袭粮按无护卫上限。</summary>
    private static void ShouldDropEscortWhenSettlementFails()
    {
        var runtime = new RealtimeSimulationRuntime(CreateNingyuanScenarioWorld(travelHours: 24 * 24));
        runtime.ScheduleScenarioRiskSamples();
        var before = runtime.ReadModel;

        // 先规划护卫运输（规划时国库 200000 足以支付 400 两护卫费），再发政令把国库扣到 300。
        Require(runtime.EnqueueCreateShipment(new CreateShipmentCommand(
            "escort-drop", new CharacterId("works"), new ShipmentId("shipment-escort-drop"),
            new RouteId("capital-ningyuan-grain"), 5_000, before.GameTime.Value, 0, Escort: true)).Queued,
            "护卫运输应进入收件箱");
        // 政令预期版本 = 运输接纳后的版本：同一帧先接纳运输（版本 +1），政令再以最新版本提交，
        // 这样出发事件结算时国库已被政令扣空，才能验证"规划时够、出发时不够"的护卫失效路径。
        var drainDecree = CreateDecree(runtime, "decree-drain", deadlineDays: 20, budget: 199_700);
        Require(runtime.EnqueueCreateDecree(drainDecree with { ExpectedWorldVersion = 1 }).Queued,
            "扣库政令应进入收件箱");
        var departure = runtime.AdvanceTo(before.GameTime);

        Require(departure.Events.Any(domainEvent => domainEvent.EventType == "EscortSettlementFailed"),
            "出发时国库不足必须产生护卫结算失败事件");
        Require(departure.Events.All(domainEvent => domainEvent.EventType != "EscortSettlement"),
            "护卫未成行不能产生 +400 两结算");
        Require(runtime.ReadModel.Shipments.Single(item => item.Id.Value == "shipment-escort-drop").Escort == false,
            "护卫结算失败后必须清除护卫标记（否则袭粮按错误的上限结算）");
        Require(runtime.ReadModel.Scenario.SpentSilver == 199_700,
            "护卫未成行不能计入护卫费用");

        // 第 24 天袭粮：无护卫批次损失上限回到 20%。
        var risked = runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddDays(40)));
        var attacked = risked.Events.Single(domainEvent => domainEvent.EventType == "ShipmentAttacked");
        Require(attacked.Data["escorted"] == "False", "袭粮事件必须记录无护卫");
        Require(int.Parse(attacked.Data["loss_percent"]) <= 20, "护卫失效后袭粮损失上限为 20%");
    }

    private static void InvokeCommitRealtime(WorldState world, long version, string commitId)
    {
        try
        {
            typeof(WorldState).GetMethod("CommitRealtime", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(world, [version, commitId]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static MoveArmyCommand CreateMove(
        RealtimeSimulationRuntime runtime,
        string commandId,
        DateTimeOffset submittedAt,
        long expectedVersion = 0,
        int travelHours = 2) =>
        new(commandId, new CharacterId("war"), new ArmyId("army-1"), new ProvinceId("capital"), submittedAt, expectedVersion, travelHours);

    private static CreateShipmentCommand CreateShipment(
        RealtimeSimulationRuntime runtime,
        string commandId,
        long grainQuantity,
        CharacterId? actorId = null,
        long? expectedVersion = null) =>
        new(commandId,
            actorId ?? new CharacterId("works"),
            new ShipmentId($"shipment-{commandId}"),
            new RouteId("capital-ningyuan-grain"),
            grainQuantity,
            runtime.ReadModel.GameTime.Value,
            expectedVersion ?? runtime.ReadModel.WorldVersion);

    private static WorldState GetRuntimeState(RealtimeSimulationRuntime runtime) =>
        (WorldState)typeof(RealtimeSimulationRuntime)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime)!;

    private static WorldState GetSnapshotState(RealtimeSnapshot snapshot) =>
        (WorldState)typeof(RealtimeSnapshot)
            .GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(snapshot)!;

    /// <summary>只用于测试：模拟调用方错误地投递了内核不认识的命令子类型。
    /// 必须是 record 才能继承抽象的 RealtimeCommand record；不声明自己的位置参数，
    /// 避免与基类的 CommandId/ActorId 等属性重名产生隐藏警告。</summary>
    private sealed record UnknownCommand : RealtimeCommand
    {
        public UnknownCommand(string commandId, CharacterId actorId, DateTimeOffset submittedAt, long expectedWorldVersion)
            : base(commandId, actorId, submittedAt, expectedWorldVersion)
        {
        }
    }

    private static bool InvokeStockpileMutation(StockpileState stockpile, string methodName, long quantity) =>
        (bool)typeof(StockpileState)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(stockpile, [quantity])!;

    private static void AddScheduledHeartbeat(RealtimeSimulationRuntime runtime, GameTime dueAt, string eventId)
    {
        var actions = (List<ScheduledSimulationEvent>)typeof(RealtimeSimulationRuntime)
            .GetField("_scheduledEvents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime)!;
        var nextSequence = actions.Count == 0 ? 0 : actions.Max(item => item.CreationSequence) + 1;
        actions.Add(new ScheduledSimulationEvent(eventId, dueAt, 2, 0, nextSequence,
            "DailyHeartbeat", new Dictionary<string, string>()));
    }

    private static IReadOnlyList<string> EventFingerprints(IEnumerable<DomainEvent> events) =>
        events.Select(item => string.Join("\u001f", [
            item.EventId,
            item.EventSequence.ToString(),
            item.EventType,
            item.WorldVersion.ToString(),
            item.CommitId,
            item.CausalCommandId ?? "",
            item.OccurredAt?.UtcTicks.ToString() ?? "",
            string.Join("\u001e", item.Data.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"))])).ToArray();

    private static long GrainLedgerTotal(RealtimeReadModel model) => checked(
        model.Stockpiles.Sum(item => item.GrainQuantity) +
        model.Shipments.Where(item => item.Status != ShipmentStatus.Arrived).Sum(item => item.GrainQuantity) +
        model.Shipments.Where(item => item.Status == ShipmentStatus.Arrived).Sum(item => item.LossGrain));

    private static decimal SnapshotRemainder(RealtimeSimulationRuntime runtime) =>
        (decimal)typeof(RealtimeSnapshot).GetProperty("RealGameTickRemainder", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime.CaptureSnapshot())!;

    /// <summary>宁远急饷 L1 批次的世界：5000 石、8% 损耗、12 游戏日到达（纸面推演 DESIGN 数值）。</summary>
    private static WorldState CreateNingyuanWorld(
        long sourceGrain = 20_000,
        long routeCapacity = 6_000,
        long destinationCapacity = 30_000,
        int travelHours = 12 * 24,
        int lossPerThousand = 80)
    {
        var map = new MapDefinition(
            "ningyuan-map",
            [
                new ProvinceDefinition(new ProvinceId("capital"), "京师", [new ProvinceId("liaodong")]),
                new ProvinceDefinition(new ProvinceId("liaodong"), "辽东", [new ProvinceId("capital")]),
            ]);
        return WorldState.CreateInitial(
            new WorldId("ningyuan-1629"),
            1,
            200_000,
            map,
            characters:
            [
                new CharacterState(new CharacterId("works"), "户部运粮官",
                    new CharacterAttributes(80, 60, 30, 40, 70),
                    new CharacterPersonality(true, false, true, true)),
            ],
            capabilityGrants:
            [
                new CapabilityGrant(new CharacterId("works"), GameCapability.PlanLogistics, "capital-ningyuan-grain"),
            ],
            stockpiles:
            [
                new StockpileState(new StockpileId("capital-granary"), new ProvinceId("capital"), 30_000, sourceGrain),
                new StockpileState(new StockpileId("ningyuan-granary"), new ProvinceId("liaodong"), destinationCapacity, 0),
            ],
            routes:
            [
                new RouteState(new RouteId("capital-ningyuan-grain"),
                    new StockpileId("capital-granary"), new StockpileId("ningyuan-granary"),
                    routeCapacity, travelHours, lossPerThousand),
            ]);
    }

    private static WorldState CreateLogisticsWorld(
        long destinationCapacity = 1_000,
        long destinationGrain = 0,
        long routeCapacity = 500,
        int travelHours = 2,
        int lossPerThousand = 100)
    {
        var map = new MapDefinition(
            "logistics-map",
            [
                new ProvinceDefinition(new ProvinceId("capital"), "京师", [new ProvinceId("liaodong")]),
                new ProvinceDefinition(new ProvinceId("liaodong"), "辽东", [new ProvinceId("capital")]),
            ]);
        return WorldState.CreateInitial(
            new WorldId("logistics-world"),
            1,
            200_000,
            map,
            characters:
            [
                new CharacterState(new CharacterId("works"), "物流角色",
                    new CharacterAttributes(80, 60, 30, 40, 70),
                    new CharacterPersonality(true, false, true, true)),
                new CharacterState(new CharacterId("war"), "无物流权限角色",
                    new CharacterAttributes(60, 40, 80, 50, 60),
                    new CharacterPersonality(true, true, true, false)),
            ],
            capabilityGrants:
            [
                new CapabilityGrant(new CharacterId("works"), GameCapability.PlanLogistics, "capital-ningyuan-grain"),
            ],
            stockpiles:
            [
                new StockpileState(new StockpileId("capital-granary"), new ProvinceId("capital"), 2_000, 1_000),
                new StockpileState(new StockpileId("ningyuan-granary"), new ProvinceId("liaodong"), destinationCapacity, destinationGrain),
            ],
            routes:
            [
                new RouteState(new RouteId("capital-ningyuan-grain"),
                    new StockpileId("capital-granary"), new StockpileId("ningyuan-granary"),
                    routeCapacity, travelHours, lossPerThousand),
            ]);
    }

    /// <summary>宁远急饷场景世界：携带场景状态（前线粮仓），用于战备/负担/信任/风险样本/终局验收。</summary>
    private static WorldState CreateNingyuanScenarioWorld(
        long sourceGrain = 20_000,
        long destinationGrain = 5_400,
        long routeCapacity = 6_000,
        long destinationCapacity = 50_000,
        int travelHours = 12 * 24,
        int lossPerThousand = 80)
    {
        var map = new MapDefinition(
            "ningyuan-map",
            [
                new ProvinceDefinition(new ProvinceId("capital"), "京师", [new ProvinceId("liaodong")]),
                new ProvinceDefinition(new ProvinceId("liaodong"), "辽东", [new ProvinceId("capital")]),
            ]);
        return WorldState.CreateInitial(
            new WorldId("ningyuan-1629"),
            1,
            200_000,
            map,
            characters:
            [
                new CharacterState(new CharacterId("emperor"), "崇祯皇帝",
                    new CharacterAttributes(70, 55, 40, 65, 80),
                    new CharacterPersonality(true, true, true, false)),
                new CharacterState(new CharacterId("works"), "户部运粮官",
                    new CharacterAttributes(80, 60, 30, 40, 70),
                    new CharacterPersonality(true, false, true, true)),
                new CharacterState(new CharacterId("war"), "无物流权限角色",
                    new CharacterAttributes(60, 40, 80, 50, 60),
                    new CharacterPersonality(true, true, true, false)),
            ],
            capabilityGrants:
            [
                new CapabilityGrant(new CharacterId("works"), GameCapability.PlanLogistics, "capital-ningyuan-grain"),
            ],
            stockpiles:
            [
                new StockpileState(new StockpileId("capital-granary"), new ProvinceId("capital"), 30_000, sourceGrain),
                new StockpileState(new StockpileId("ningyuan-granary"), new ProvinceId("liaodong"), destinationCapacity, destinationGrain),
            ],
            routes:
            [
                new RouteState(new RouteId("capital-ningyuan-grain"),
                    new StockpileId("capital-granary"), new StockpileId("ningyuan-granary"),
                    routeCapacity, travelHours, lossPerThousand),
            ],
            scenario: new ScenarioState(frontStockpileId: new StockpileId("ningyuan-granary")));
    }

    private static CreateDecreeCommand CreateDecree(
        RealtimeSimulationRuntime runtime,
        string id,
        int deadlineDays,
        long budget = 5_000,
        CharacterId? responsible = null,
        string? linkedShipmentId = null) =>
        new(id, new CharacterId("emperor"), new DecreeId(id), $"向宁远调运军粮 {id}", new ProvinceId("liaodong"),
            budget, responsible ?? new CharacterId("works"),
            new GameTime(runtime.ReadModel.GameTime.Value.AddDays(deadlineDays)),
            "", "测试政令", GameCapability.PlanLogistics, "capital-ningyuan-grain",
            linkedShipmentId, runtime.ReadModel.GameTime.Value, runtime.ReadModel.WorldVersion);

    /// <summary>只用于测试：注入终局分档所需的战备初值（评估函数本身可自动检查）。</summary>
    private static void ReplaceReadiness(WorldState world, ReadinessState readiness) =>
        typeof(WorldState).GetProperty("Readiness", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(world, readiness);

    private static void ChangeScenarioTrust(WorldState world, int delta) =>
        typeof(ScenarioState).GetMethod("ChangeMinisterTrust", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(world.Scenario, [delta]);

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