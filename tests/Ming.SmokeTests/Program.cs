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
using MingSim.Persistence.InMemory;
using MingSim.Simulation;

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

    private static WorldState CreateWorld()
    {
        var world = new WorldState(new WorldId("smoke-world"), 1, 200_000);
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
