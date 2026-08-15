using System.Diagnostics;
using MingSim.Application.Scenarios;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Events;
using MingSim.Domain.Map;
using MingSim.Domain.Realtime;
using MingSim.Domain.Scenario;
using MingSim.Persistence.InMemory;
using MingSim.Simulation.Realtime;

namespace MingSim.SoakTests;

/// <summary>
/// 90 日宁远急饷场景 × 20 固定种子的确定性重放（doc 08 §20 测试矩阵"MVP 长跑"行）。
/// 当前内核的确定性随机（DeterministicRandom）只由 worldId|turn|eventId 派生，
/// 因此"种子"就是世界编号：固定清单写在本文件（验收可复现），
/// 每个编号用 CreateInitial 重建一份结构完全相同的剧本世界，风险样本抽取随种子变化。
/// 验收：
/// - 同一种子两次独立运行必须得到同一 StateHash、同一事件流、同一终局评估；
/// - 每次运行结束再从 InMemoryCommitStore 恢复，也必须得到同一 StateHash 与事件流；
/// - 20 个种子必须覆盖至少两条不同的风险轨迹（天气延误/袭粮/报告抽取去重后 &gt; 1）。
/// </summary>
internal static class NingyuanSeedReplay
{
    /// <summary>固定 20 种子清单（DESIGN：确定性重放验收；改成随机生成会破坏可复现性）。</summary>
    private static readonly string[] Seeds =
    [
        "soak-1629-01", "soak-1629-02", "soak-1629-03", "soak-1629-04", "soak-1629-05",
        "soak-1629-06", "soak-1629-07", "soak-1629-08", "soak-1629-09", "soak-1629-10",
        "soak-1629-11", "soak-1629-12", "soak-1629-13", "soak-1629-14", "soak-1629-15",
        "soak-1629-16", "soak-1629-17", "soak-1629-18", "soak-1629-19", "soak-1629-20",
    ];

    /// <summary>一次 90 日运行的完整验收结果。</summary>
    private sealed record RunRecord(
        string StateHash,
        IReadOnlyList<string> EventFingerprints,
        int EventCount,
        EndgameEvaluation Endgame,
        TimeSpan WallTime,
        string RiskSummary);

    internal static void RunAll()
    {
        Console.WriteLine("== 90 日宁远场景 × 20 种子确定性重放（同种子同 StateHash/事件流）==");
        var timings = new List<long>();
        var distinctHashes = new HashSet<string>(StringComparer.Ordinal);
        var riskSummaries = new HashSet<string>(StringComparer.Ordinal);
        var totalWall = Stopwatch.StartNew();
        foreach (var seedId in Seeds)
        {
            var first = RunSeed(seedId, timings);
            var replay = RunSeed(seedId, timings);
            Program.Require(first.StateHash == replay.StateHash,
                $"种子 {seedId} 重放 StateHash 不一致：{first.StateHash} vs {replay.StateHash}");
            Program.Require(first.EventFingerprints.SequenceEqual(replay.EventFingerprints),
                $"种子 {seedId} 重放事件流不一致");
            Program.Require(first.Endgame == replay.Endgame,
                $"种子 {seedId} 重放终局评估不一致");
            distinctHashes.Add(first.StateHash);
            riskSummaries.Add(first.RiskSummary);
            Console.WriteLine($"  种子 {seedId}: hash={first.StateHash[..12]}… 事件={first.EventCount} " +
                $"终局={first.Endgame.Outcome} 首跑={first.WallTime.TotalMilliseconds:F0}ms 重放={replay.WallTime.TotalMilliseconds:F0}ms");
        }

        totalWall.Stop();
        Program.Require(distinctHashes.Count == Seeds.Length,
            $"20 个种子必须产生 {Seeds.Length} 个不同 StateHash，实际 {distinctHashes.Count} 个");
        Program.Require(riskSummaries.Count >= 2,
            $"20 个种子必须覆盖至少两条不同的风险轨迹，实际去重后只有 {riskSummaries.Count} 条");
        Console.WriteLine($"  20 种子全部确定性重放一致；风险轨迹去重 {riskSummaries.Count} 条；不同 StateHash {distinctHashes.Count} 个");
        Console.WriteLine($"  20 种子 ×2 重放总墙钟：{totalWall.Elapsed.TotalSeconds:F1} 秒");
        Console.WriteLine($"  每次推进时间分布：{TimingSummary.Format(timings)}");
    }

    private static RunRecord RunSeed(string seedId, List<long> timings)
    {
        var store = new InMemoryCommitStore();
        var runtime = new RealtimeSimulationRuntime(LoadScenarioWorldForSeed(seedId), store);
        runtime.ScheduleScenarioRiskSamples();
        var stopwatch = Stopwatch.StartNew();
        RunNinetyDayScript(runtime, seedId, timings);
        stopwatch.Stop();

        var endgame = runtime.EvaluateEndgame();
        var hash = runtime.StateHash;
        var fingerprints = Program.EventFingerprints(runtime.OutboxEvents);

        // 持久化往返：从提交商店恢复最后一个完整提交，也必须得到同一 hash 与事件流（doc 04 §5）。
        var restored = RealtimeSimulationRuntime.RestoreFromStore(store);
        Program.Require(restored.StateHash == hash,
            $"种子 {seedId} 经提交商店恢复后 StateHash 不一致");
        Program.Require(Program.EventFingerprints(restored.OutboxEvents).SequenceEqual(fingerprints),
            $"种子 {seedId} 经提交商店恢复后事件流不一致");

        return new RunRecord(hash, fingerprints, runtime.OutboxEvents.Count, endgame,
            stopwatch.Elapsed, ExtractRiskSummary(runtime.OutboxEvents));
    }

    /// <summary>
    /// 固定的 90 日脚本（输入流本身确定，重放必然一致）：
    /// - 第 0 天发出海运首批（登州→觉华岛，带护卫），再签发一道催饷政令绑定该批（P1-DECREE-03：绑定须先存在）；
    /// - 第 10 天发出陆运首批（北京→通州）与海运第二批；第 20 天发出陆运第二批（两处粮源用尽）；
    /// - 每天推进后把当日抵达的运输单沿路线网中继续运到下一段（纯读模型决策）；
    /// - 风险样本（第 12 天天气延误、第 24 天袭粮、第 30 天报告）由 ScheduleScenarioRiskSamples 安排，
    ///   抽取结果随种子（世界编号）变化，因此 20 条轨迹互不相同。
    /// </summary>
    private static void RunNinetyDayScript(RealtimeSimulationRuntime runtime, string seedId, List<long> timings)
    {
        var start = runtime.ReadModel.GameTime;
        // 第 0 天命令基于初始版本 0；此后每次推进后都要重新从权威版本锚定计数——
        // 每日心跳/抵达/出发等调度事件提交也会递增 WorldVersion，不能只按命令数累加。
        var version = 0L;

        // P1-DECREE-03 绑定不变量：政令接纳时绑定运输单必须已存在（Planned/InTransit），
        // 因此先发运输单（version 0）、再发绑定它的政令（version 1），同批收件箱按序接纳。
        runtime.EnqueueCreateShipment(new CreateShipmentCommand(
            $"{seedId}-sea-a1", new CharacterId("duliaoxiang-slot"), new ShipmentId(ShipmentId(seedId, "sea-a1")),
            new RouteId("route-dengzhou-juehuadao"), 7_000, start.Value, version++, Escort: true));
        runtime.EnqueueCreateDecree(new CreateDecreeCommand(
            $"{seedId}-decree-1", new CharacterId("zhu-youjian"), new DecreeId($"{seedId}-decree-1"),
            "催饷令：向宁远调运军粮（长跑测试）", new ProvinceId("ningyuan"), 5_000,
            new CharacterId("duliaoxiang-slot"), new GameTime(start.Value.AddDays(40)),
            "", "长跑测试政令", ShipmentId(seedId, "sea-a1"), start.Value, version++,
            DecreeKind.ExpediteSupply));

        for (var day = 1; day <= 90; day++)
        {
            var advance = Stopwatch.StartNew();
            var result = runtime.AdvanceTo(new GameTime(start.Value.AddDays(day)));
            advance.Stop();
            timings.Add(advance.Elapsed.Ticks / 10);
            Program.Require(result.Succeeded && result.Errors.Count == 0,
                $"种子 {seedId} 第 {day} 天推进失败：{string.Join("; ", result.Errors.Select(error => error.Code))}");
            // 本日接纳的命令必须全部成功：任何拒绝都意味着剧本脚本与内核规则失配，
            // 宁可显式失败也不让补给链静默退化（重放本身仍会一致，但轨迹失去意义）。
            Program.Require(result.CommandResults.All(item => item.Accepted),
                $"种子 {seedId} 第 {day} 天出现命令拒绝：{string.Join("; ", result.CommandResults.Where(item => !item.Accepted).Select(item => $"{item.CommandId}:{string.Join(",", item.Errors.Select(error => error.Code))}"))}");
            // 推进结束后，后续命令的预期版本必须从当前权威版本重新锚定。
            version = runtime.ReadModel.WorldVersion;

            // 中继：本日抵达的运输单沿路线网续运下一段；决策只看读模型，输入流确定。
            var relays = result.Events
                .Where(domainEvent => domainEvent.EventType == "ShipmentArrived")
                .Select(domainEvent => domainEvent.Data["shipment_id"])
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            foreach (var arrivedId in relays)
            {
                var arrived = runtime.ReadModel.Shipments.Single(item => item.Id.Value == arrivedId);
                if (arrived.DeliveredGrain <= 0)
                {
                    continue;
                }

                var nextRoute = NextLegRoute(arrived.RouteId);
                if (nextRoute is null)
                {
                    continue;
                }

                var relayShipmentId = $"{arrivedId}-relay";
                runtime.EnqueueCreateShipment(new CreateShipmentCommand(
                    $"{seedId}-relay-{arrivedId}", new CharacterId("duliaoxiang-slot"), new ShipmentId(relayShipmentId),
                    nextRoute.Value, arrived.DeliveredGrain, runtime.ReadModel.GameTime.Value, version++));
            }

            // 固定批次补给：第 10 天陆运首批 + 海运第二批；第 20 天陆运第二批（把两处粮源用尽）。
            if (day == 10)
            {
                runtime.EnqueueCreateShipment(CreateFirstLeg(seedId, "land-b1", new RouteId("route-beijing-tongzhou"),
                    5_000, runtime.ReadModel.GameTime.Value, version++));
                runtime.EnqueueCreateShipment(CreateFirstLeg(seedId, "sea-b1", new RouteId("route-dengzhou-juehuadao"),
                    7_000, runtime.ReadModel.GameTime.Value, version++));
            }
            else if (day == 20)
            {
                runtime.EnqueueCreateShipment(CreateFirstLeg(seedId, "land-c1", new RouteId("route-beijing-tongzhou"),
                    5_000, runtime.ReadModel.GameTime.Value, version++));
            }
        }
    }

    /// <summary>路线网中继表：运到下一段路线；终段返回 null（不再续运）。</summary>
    private static RouteId? NextLegRoute(RouteId routeId) =>
        routeId.Value switch
        {
            "route-beijing-tongzhou" => new RouteId("route-tongzhou-shanhaiguan"),
            "route-tongzhou-shanhaiguan" => new RouteId("route-shanhaiguan-ningyuan"),
            "route-dengzhou-juehuadao" => new RouteId("route-juehuadao-ningyuan"),
            _ => null,
        };

    private static CreateShipmentCommand CreateFirstLeg(
        string seedId, string name, RouteId route, long quantity, DateTimeOffset submittedAt, long expectedVersion) =>
        new($"{seedId}-{name}", new CharacterId("duliaoxiang-slot"), new ShipmentId(ShipmentId(seedId, name)),
            route, quantity, submittedAt, expectedVersion);

    private static string ShipmentId(string seedId, string name) => $"{seedId}-{name}";

    /// <summary>种子 → 剧本世界变体：只有世界编号不同，其余与 Ningyuan1629InitialWorld.Load() 完全一致。</summary>
    private static WorldState LoadScenarioWorldForSeed(string seedId)
    {
        var baseWorld = Ningyuan1629InitialWorld.Load();
        return WorldState.CreateInitial(
            new WorldId(seedId),
            baseWorld.TurnNumber,
            baseWorld.Economy.Treasury.Silver,
            baseWorld.Map,
            baseWorld.CurrentTime,
            baseWorld.Characters.Values,
            baseWorld.Institutions.Values,
            baseWorld.CapabilityGrants,
            inventory: baseWorld.Economy.Inventory.Stocks.Select(item => (item.Key, item.Value.Quantity)),
            armies: baseWorld.Military.Armies.Values,
            stockpiles: baseWorld.Logistics.Stockpiles.Values,
            routes: baseWorld.Logistics.Routes.Values,
            appointments: baseWorld.Appointments,
            scenario: baseWorld.Scenario);
    }

    /// <summary>风险样本抽取摘要：用于证明 20 个种子覆盖了不同轨迹（去重后 &gt; 1）。</summary>
    private static string ExtractRiskSummary(IReadOnlyList<DomainEvent> events)
    {
        string Value(string eventType, string key)
        {
            var match = events.FirstOrDefault(domainEvent => domainEvent.EventType == eventType);
            return match is null || !match.Data.TryGetValue(key, out var value) ? "none" : value;
        }

        var reports = string.Join(",", events
            .Where(domainEvent => domainEvent.EventType == "ScenarioReportReceived")
            .Select(domainEvent => domainEvent.Data.GetValueOrDefault("credibility")));
        return $"delay={Value("ShipmentDelayed", "delay_days")};raid={Value("ShipmentAttacked", "loss_percent")};reports=[{reports}]";
    }
}
