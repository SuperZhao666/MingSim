using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Map;
using MingSim.Domain.Realtime;
using MingSim.Domain.Scenario;
using MingSim.Persistence.InMemory;
using MingSim.Simulation.Realtime;

namespace MingSim.SoakTests;

/// <summary>
/// M5 硬失败自动暂停验收（doc 08 §19"重大游戏事件"行、14 矩阵 SYS-013"危险阈值触发一次暂停"）：
/// - ScenarioHardFailure 报告后，运行时在同一个提交里一次性自动暂停（IsPaused=true）；
/// - 暂停必须进入 RealtimeSnapshot 与 canonical hash：从快照/提交商店恢复后 IsPaused 与 hash 保持一致；
/// - 自动暂停不抢玩家手动暂停：玩家可以手动恢复，恢复后不再二次自动暂停（只报告一次）；
/// - 无硬失败的推进（含风险样本）绝不暂停。
/// </summary>
internal static class AutoPauseAcceptance
{
    internal static void RunAll()
    {
        Console.WriteLine("== 硬失败自动暂停（一次性 / 快照哈希一致 / 不抢手动暂停）==");
        HardFailurePausesExactlyOnce();
        NoFailureNeverPauses();
        Console.WriteLine("  自动暂停验收通过。");
    }

    private static void HardFailurePausesExactlyOnce()
    {
        var store = new InMemoryCommitStore();
        var runtime = new RealtimeSimulationRuntime(CreateScenarioWorld(destinationGrain: 0), store);
        var start = runtime.ReadModel.GameTime;

        // 一次推进直接越过失败点（目标第 21 天）：连续 7 日可用粮为 0 在第 7 天每日心跳报告
        // ScenarioHardFailure 并自动暂停，世界必须停在第 7 天而不是走到目标时间（doc 08 §126）。
        var jumped = runtime.AdvanceTo(new GameTime(start.Value.AddDays(21)));
        Program.Require(jumped.Events.Count(item => item.EventType == "ScenarioHardFailure") == 1,
            "越过失败点必须恰好报告一次硬失败");
        Program.Require(jumped.IsPaused && runtime.IsPaused && runtime.ReadModel.IsPaused,
            "硬失败报告后 AdvanceResult / 运行时 / ReadModel 三处 IsPaused 必须同时为 true");
        Program.Require(jumped.ReadModel.GameTime == new GameTime(start.Value.AddDays(7)),
            "自动暂停立即生效：一次推进越过失败点必须停在第 7 天，不能继续推进到目标第 21 天");

        // 暂停后推进不再改变世界（时间/版本/hash 全部冻结），也不会重复报告硬失败。
        var before = runtime.ReadModel;
        var paused = runtime.AdvanceTo(new GameTime(start.Value.AddDays(20)));
        Program.Require(paused.Succeeded, "暂停状态下的推进请求本身不是错误");
        Program.Require(paused.ReadModel.GameTime == before.GameTime &&
                        paused.ReadModel.WorldVersion == before.WorldVersion &&
                        paused.ReadModel.StateHash == before.StateHash,
            "自动暂停后游戏时间/世界版本/canonical hash 不能继续变化");
        Program.Require(paused.Events.Count(item => item.EventType == "ScenarioHardFailure") == 0,
            "暂停后不能重复报告硬失败");

        // 快照/恢复一致性：IsPaused 必须随快照进入恢复后的运行时，且 canonical hash 一致。
        var snapshot = runtime.CaptureSnapshot();
        var restored = RealtimeSimulationRuntime.Restore(snapshot);
        Program.Require(restored.ReadModel.IsPaused && restored.IsPaused,
            "从快照恢复后 IsPaused 必须保持为 true（IsPaused 必须进入快照）");
        Program.Require(restored.StateHash == runtime.StateHash,
            "从快照恢复后 canonical hash 必须与自动暂停后的运行时一致");

        // 经提交商店恢复也必须一致（doc 04 §5）。
        var fromStore = RealtimeSimulationRuntime.RestoreFromStore(store);
        Program.Require(fromStore.ReadModel.IsPaused && fromStore.StateHash == runtime.StateHash,
            "从提交商店恢复后 IsPaused 与 canonical hash 必须一致");

        // 不抢玩家手动暂停：玩家恢复后世界继续推进，且不再二次自动暂停（硬失败只报告一次）。
        runtime.SetPaused(false);
        var resumed = runtime.AdvanceTo(new GameTime(start.Value.AddDays(21)));
        Program.Require(resumed.CommandResults.Single().Accepted && !resumed.IsPaused && !runtime.ReadModel.IsPaused,
            "玩家必须能手动解除自动暂停");
        Program.Require(resumed.ReadModel.GameTime == new GameTime(start.Value.AddDays(21)),
            "解除暂停后世界必须恢复推进");
        Program.Require(runtime.OutboxEvents.Count(item => item.EventType == "ScenarioHardFailure") == 1,
            "硬失败只能报告一次；恢复后继续推进不得再次自动暂停");
    }

    private static void NoFailureNeverPauses()
    {
        var runtime = new RealtimeSimulationRuntime(CreateScenarioWorld(destinationGrain: 5_400));
        runtime.ScheduleScenarioRiskSamples();
        var start = runtime.ReadModel.GameTime;
        // 推进 15 天：越过第 12 天固定天气风险样本，证明无硬失败（含风险事件）绝不暂停。
        for (var day = 1; day <= 15; day++)
        {
            var result = runtime.AdvanceTo(new GameTime(start.Value.AddDays(day)));
            Program.Require(result.Succeeded && result.Errors.Count == 0,
                $"无硬失败推进第 {day} 天必须成功");
            Program.Require(!result.IsPaused && !runtime.ReadModel.IsPaused,
                $"无硬失败推进第 {day} 天绝不能自动暂停");
        }

        Program.Require(runtime.OutboxEvents.Count(item => item.EventType == "ScenarioHardFailure") == 0,
            "无硬失败时绝不能报告硬失败");
        Program.Require(runtime.ReadModel.GameTime == new GameTime(start.Value.AddDays(15)),
            "无硬失败推进必须完整到达第 15 天");
    }

    /// <summary>最小宁远场景世界：前线粮仓 + 场景规则开启；destinationGrain 控制是否硬失败。</summary>
    private static WorldState CreateScenarioWorld(long destinationGrain)
    {
        var map = new MapDefinition(
            "auto-pause-map",
            [
                new ProvinceDefinition(new ProvinceId("capital"), "京师", [new ProvinceId("liaodong")]),
                new ProvinceDefinition(new ProvinceId("liaodong"), "辽东", [new ProvinceId("capital")]),
            ]);
        return WorldState.CreateInitial(
            new WorldId("auto-pause-1629"),
            1,
            200_000,
            map,
            currentTime: new DateTimeOffset(1629, 1, 1, 0, 0, 0, TimeSpan.Zero),
            characters:
            [
                new CharacterState(new CharacterId("works"), "运粮官",
                    new CharacterAttributes(80, 60, 30, 40, 70),
                    new CharacterPersonality(true, false, true, true)),
            ],
            capabilityGrants:
            [
                new CapabilityGrant(new CharacterId("works"), GameCapability.PlanLogistics, "capital-ningyuan-grain"),
            ],
            stockpiles:
            [
                new StockpileState(new StockpileId("capital-granary"), new ProvinceId("capital"), 30_000, 20_000),
                new StockpileState(new StockpileId("ningyuan-granary"), new ProvinceId("liaodong"), 30_000, destinationGrain),
            ],
            routes:
            [
                new RouteState(new RouteId("capital-ningyuan-grain"),
                    new StockpileId("capital-granary"), new StockpileId("ningyuan-granary"),
                    6_000, 24 * 12, 80),
            ],
            scenario: new ScenarioState(frontStockpileId: new StockpileId("ningyuan-granary")));
    }
}
