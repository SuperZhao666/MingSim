using MingSim.Application.Workflows;
using MingSim.Application.Ports;
using MingSim.Application.Scenarios;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Intents;
using MingSim.Domain.Military;
using MingSim.Domain.Map;
using MingSim.Domain.Realtime;
using MingSim.Persistence.InMemory;
using MingSim.Simulation;
using MingSim.Simulation.Realtime;

namespace MingSim.SmokeTests;

/// <summary>
/// 不依赖第三方测试框架的冒烟测试。
/// </summary>
/// <remarks>
/// 这样即使还没有恢复完整 .NET 工具链，也能清楚看到第一版必须守住的行为契约。
/// 工具链可用后，可以把这些断言迁移到 xUnit/MSTest，而不改变测试内容。
/// </remarks>
internal static class Program
{
    private static int Main()
    {
        try
        {
            ShouldCommitValidIntentsAtomically();
            ShouldRejectUnauthorizedIntentWithoutChangingWorld();
            ShouldKeepSnapshotAndAuditAfterOrchestration();
            ShouldLoadAndValidateScenarioMap();
            ShouldNotAdvanceWhenPaused();
            ShouldAdvanceByExplicitGameTimeAndMoveAdjacentArmy();
            ShouldKeepSpeedAsACompatibilityTimeMapping();
            ShouldKeepSameTimeEventsInCreationOrder();
            ShouldKeepHashIndependentOfAdvanceFrameSplitting();
            ShouldApplyDuplicateCommandOnlyOnce();
            Console.WriteLine("MingSim 冒烟测试全部通过。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"冒烟测试失败：{exception.Message}");
            Console.Error.WriteLine(exception.StackTrace);
            return 1;
        }
    }

    private static void ShouldCommitValidIntentsAtomically()
    {
        var world = CreateWorld();
        var resolution = new SimulationKernel().ResolveTurn(
            world,
            [
                new BuildFacilityIntent(
                    "test-build",
                    new CharacterId("works"),
                    1,
                    "test-build-key",
                    new FacilityId("facility-1"),
                    new ProvinceId("capital"),
                    FacilityType.FlintlockWorkshop,
                    50_000,
                    800,
                    80),
                new ConvertArmyIntent(
                    "test-convert",
                    new CharacterId("war"),
                    1,
                    "test-convert-key",
                    new ArmyId("army-1"),
                    1_000),
            ]);

        Require(resolution.Committed, "合法意图应该提交");
        Require(resolution.State.TurnNumber == 2, "提交后应该进入下一回合");
        Require(resolution.State.Economy.Treasury.Silver == 150_000, "工坊预算应该从国库扣除");
        Require(resolution.State.Military.Armies[new ArmyId("army-1")].Auxiliaries == 9_000, "辅兵应该减少");
        Require(resolution.State.Military.Armies[new ArmyId("army-1")].LineInfantry == 4_000, "列装步兵应该增加");
        Require(resolution.State.Economy.Inventory.GetOrCreate("flintlock").Quantity == 9_000, "装备应该真实消耗");
        Require(resolution.Events.Any(domainEvent => domainEvent.EventType == "TurnCommitted"), "应该生成回合提交事件");
    }

    private static void ShouldRejectUnauthorizedIntentWithoutChangingWorld()
    {
        var world = CreateWorld();
        var resolution = new SimulationKernel().ResolveTurn(
            world,
            [new BuildFacilityIntent(
                "test-unauthorized-build",
                new CharacterId("war"),
                1,
                "test-unauthorized-build-key",
                new FacilityId("facility-denied"),
                new ProvinceId("capital"),
                FacilityType.FlintlockWorkshop,
                50_000,
                800,
                80)]);

        Require(!resolution.Committed, "无权限意图不能提交");
        Require(resolution.Errors.Any(error => error.Code == "TOOL_SCOPE_DENIED"), "应该返回权限错误");
        Require(world.Economy.Treasury.Silver == 200_000, "拒绝后原世界国库不能变化");
        Require(world.Industry.Facilities.Count == 0, "拒绝后原世界不能出现工坊");
    }

    private static void ShouldKeepSnapshotAndAuditAfterOrchestration()
    {
        var world = CreateWorld();
        var store = new InMemoryWorldStore(world);
        var audit = new InMemoryAuditJournal();
        var snapshots = new InMemorySnapshotStore();
        var orchestrator = new TurnOrchestrator(store, audit, snapshots, new SimulationKernel());

        var result = orchestrator.ExecuteTurn(
            world.Id,
            [new ConvertArmyIntent(
                "test-orchestrated-convert",
                new CharacterId("war"),
                1,
                "test-orchestrated-convert-key",
                new ArmyId("army-1"),
                1_000)]);

        Require(result.Committed, "编排器应该提交合法回合");
        Require(audit.Read(world.Id).Count == result.EventCount, "审计事件数应该与回合结果一致");
        Require(snapshots.Current is not null && snapshots.Current.IsValid, "当前快照应该通过校验");
        Require(store.Load(world.Id).TurnNumber == 2, "存储中的回合应该已经前进");
    }

    private static void ShouldLoadAndValidateScenarioMap()
    {
        var scenarioPath = Path.GetFullPath(Path.Combine("content", "ming_1627", "world.json"));
        var world = new ScenarioLoader().Load(scenarioPath);

        Require(world.Map.Id == "ming_1627_demo_map", "剧本应该加载独立的地图编号");
        Require(world.Map.Contains(new ProvinceId("capital")), "地图应该包含京师");
        Require(world.Map.Contains(new ProvinceId("liaodong")), "地图应该包含辽东");
        Require(
            world.Map.IsAdjacent(new ProvinceId("capital"), new ProvinceId("liaodong")),
            "地图应该保留京师到辽东的邻接关系");

        // MapDefinition 会在构造时拒绝自环、重复邻接和未知引用。
        var rejected = false;
        try
        {
            _ = new MapDefinition(
                "invalid-map",
                [new ProvinceDefinition(
                    new ProvinceId("capital"),
                    "京师",
                    [new ProvinceId("missing")])]);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Require(rejected, "地图引用不存在的省份时应该被拒绝");
    }

    private static void ShouldNotAdvanceWhenPaused()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        runtime.SetPaused(true);
        var before = runtime.StateHash;
        var beforeTime = runtime.State.GameTime;

        var result = runtime.AdvanceTo(new GameTime(beforeTime.Value.AddDays(2)));

        Require(runtime.State.GameTime == beforeTime, "暂停时游戏时间不能推进");
        Require(runtime.StateHash == before, "暂停时权威状态哈希不能变化");
        Require(result.GameTimeAdvanced == TimeSpan.Zero, "暂停时报告的推进时长必须为零");
    }

    private static void ShouldAdvanceByExplicitGameTimeAndMoveAdjacentArmy()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        var command = new MoveArmyCommand(
            "move-adjacent",
            new CharacterId("war"),
            new ArmyId("army-1"),
            new ProvinceId("capital"),
            runtime.State.CurrentTime,
            TravelHours: 2);

        Require(runtime.EnqueueMoveArmy(command).Accepted, "相邻行军命令应该被接纳");
        var result = runtime.AdvanceTo(new GameTime(runtime.State.CurrentTime.AddHours(2)));

        Require(result.ProcessedScheduledEvents == 1, "目标时刻应该处理一条抵达事件");
        Require(result.State.Military.Armies[new ArmyId("army-1")].LocationId == new ProvinceId("capital"), "抵达事件应该由模拟内核修改军队位置");
        Require(result.Events.Any(domainEvent => domainEvent.EventType == "ArmyArrived"), "应该产生军队抵达事实事件");
    }

    private static void ShouldKeepSpeedAsACompatibilityTimeMapping()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        runtime.SetSpeed(2.0);

        runtime.Advance(TimeSpan.FromSeconds(1));

        Require(runtime.State.CurrentTime == SimulationEpoch.DefaultForTurn(1).AddHours(12), "倍速只应改变兼容入口的游戏时间换算");
    }

    private static void ShouldKeepSameTimeEventsInCreationOrder()
    {
        var first = new RealtimeSimulationRuntime(CreateWorld());
        var second = new RealtimeSimulationRuntime(CreateWorld());
        var firstCommand = CreateMoveCommand(first, "same-time-1", "capital");
        var secondCommand = CreateMoveCommand(first, "same-time-2", "capital");
        var sameTarget = new GameTime(first.State.CurrentTime.AddHours(2));

        Require(first.EnqueueMoveArmy(firstCommand).Accepted, "第一条同刻命令应该被接纳");
        Require(first.EnqueueMoveArmy(secondCommand).Accepted, "第二条同刻命令应该被接纳");
        var firstResult = first.AdvanceTo(sameTarget);

        Require(second.EnqueueMoveArmy(CreateMoveCommand(second, "same-time-1", "capital")).Accepted, "重放第一条同刻命令应该被接纳");
        Require(second.EnqueueMoveArmy(CreateMoveCommand(second, "same-time-2", "capital")).Accepted, "重放第二条同刻命令应该被接纳");
        var replay = second.AdvanceTo(sameTarget);

        Require(firstResult.Events
            .Where(domainEvent => domainEvent.EventType == "ArmyArrived")
            .Select(domainEvent => domainEvent.EventId)
            .SequenceEqual(["army-arrival-same-time-1", "army-arrival-same-time-2"]), "同刻事件应该按创建序号稳定排序");
        Require(first.StateHash == second.StateHash, "同刻事件创建顺序在重复运行中必须稳定");
        Require(replay.Events.Count(domainEvent => domainEvent.EventType == "ArmyArrived") == 2, "同刻命令应该各自处理且不依赖优先队列偶然顺序");
    }

    private static void ShouldKeepHashIndependentOfAdvanceFrameSplitting()
    {
        var oneShot = new RealtimeSimulationRuntime(CreateWorld());
        var split = new RealtimeSimulationRuntime(CreateWorld());
        EnqueueStandardMove(oneShot, "frame-split");
        EnqueueStandardMove(split, "frame-split");
        var target = new GameTime(oneShot.State.CurrentTime.AddHours(3));

        oneShot.AdvanceTo(target);
        split.AdvanceTo(new GameTime(split.State.CurrentTime.AddHours(1)));
        split.AdvanceTo(target);

        Require(oneShot.StateHash == split.StateHash, "把 AdvanceTo 切成多次不能改变最终状态哈希");
    }

    private static void ShouldApplyDuplicateCommandOnlyOnce()
    {
        var runtime = new RealtimeSimulationRuntime(CreateWorld());
        var command = CreateMoveCommand(runtime, "duplicate", "capital", travelHours: 2);

        var first = runtime.EnqueueMoveArmy(command);
        var duplicate = runtime.EnqueueMoveArmy(command);
        var result = runtime.AdvanceTo(new GameTime(runtime.State.CurrentTime.AddHours(2)));

        Require(first == duplicate, "重复命令应该返回相同 Outcome");
        Require(result.ProcessedScheduledEvents == 1, "重复命令不能重复创建调度事件");
        Require(result.Events.Count(domainEvent => domainEvent.EventType == "ArmyArrived") == 1, "重复命令不能重复生效");
    }

    private static MoveArmyCommand CreateMoveCommand(
        RealtimeSimulationRuntime runtime,
        string commandId,
        string destination,
        int travelHours = 2) =>
        new(
            commandId,
            new CharacterId("war"),
            new ArmyId("army-1"),
            new ProvinceId(destination),
            runtime.State.CurrentTime,
            travelHours);

    private static void EnqueueStandardMove(RealtimeSimulationRuntime runtime, string commandId) =>
        Require(
            runtime.EnqueueMoveArmy(CreateMoveCommand(runtime, commandId, "capital", travelHours: 2)).Accepted,
            "标准行军命令应该被接纳");

    private static WorldState CreateWorld()
    {
        var map = new MapDefinition(
            "smoke-map",
            [
                new ProvinceDefinition(new ProvinceId("frontier"), "边地", [new ProvinceId("capital")]),
                new ProvinceDefinition(new ProvinceId("capital"), "京师", [new ProvinceId("frontier")]),
                new ProvinceDefinition(new ProvinceId("liaodong"), "辽东", [new ProvinceId("frontier")]),
            ]);
        var world = new WorldState(new WorldId("smoke-world"), 1, 200_000, map);
        world.AddCharacter(new CharacterState(
            new CharacterId("works"),
            "工部角色",
            new CharacterAttributes(80, 60, 30, 40, 70),
            new CharacterPersonality(true, false, true, true)));
        world.AddCharacter(new CharacterState(
            new CharacterId("war"),
            "兵部角色",
            new CharacterAttributes(60, 40, 80, 50, 60),
            new CharacterPersonality(true, true, true, false)));
        world.GrantCapability(new CapabilityGrant(
            new CharacterId("works"),
            GameCapability.BuildIndustry,
            "capital"));
        world.GrantCapability(new CapabilityGrant(
            new CharacterId("war"),
            GameCapability.ConvertArmy,
            "army-1"));
        world.GrantCapability(new CapabilityGrant(
            new CharacterId("war"),
            GameCapability.MoveArmy,
            "army-1"));
        world.Economy.Inventory.GetOrCreate("flintlock").Add(10_000);
        world.Military.Add(new ArmyState(
            new ArmyId("army-1"),
            "测试军",
            new ProvinceId("frontier"),
            auxiliaries: 10_000,
            lineInfantry: 3_000));
        return world;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
