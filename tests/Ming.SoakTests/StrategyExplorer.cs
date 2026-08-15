using MingSim.Application.Scenarios;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Realtime;
using MingSim.Simulation.Realtime;

namespace MingSim.SoakTests;

/// <summary>
/// M5 四策略 90 日探索（doc 11 §9 质量门"至少三种有意义策略能通关/失败"的运行证据）：
/// 陆运 / 海运 / 陆海并行 / 并行+减耗 × 各 6 个固定种子 × 90 日，复用 Ningyuan1629InitialWorld 与
/// SmokeTests 的 convoy 调度思路（首段发运 + 抵达中继 + 全程护卫），策略差异只在启用哪条补给链
/// 与是否按纸面推演日历（§3.2）发布减耗令（M5 通关杠杆）。
/// 调度是"贪心接力"：只要来源仓有粮、路线/目的仓可接纳就发下一批（确定性输入流，重放必一致）；
/// 风险样本（第 12 日天气延误/第 24 日袭粮/第 30 日报告）仍由 ScheduleScenarioRiskSamples 提供。
/// 输出每策略终局分档分布表（打印到 stdout，不写文档文件）。只做结构性断言：
/// 全部种子都进入终局、策略至少产生两种不同分档（策略差异可观察）、重放确定性、
/// "并行+减耗"全部种子达到 BarelyMaintained 或更好（减耗令通关证据，不钉死具体档位）；
/// 具体档位由总控/独立审查结合本表裁决。
/// </summary>
internal static class StrategyExplorer
{
    private enum StrategyMode
    {
        Land,        // 陆运：只走北京→通州→山海关→宁远
        Sea,         // 海运：只走登州→觉华岛→宁远
        Both,        // 陆海并行：两条链同时发运
        BothReduced, // 并行+减耗：两条链同时发运，第 21 日发布减耗令（纸面推演 §3.2 并行日历）
    }

    /// <summary>固定 6 种子清单（DESIGN：可复现；同一编号在四策略间共享，风险样本相同，差异只来自策略与减耗令）。</summary>
    private static readonly string[] Seeds =
    [
        "explore-1629-01", "explore-1629-02", "explore-1629-03",
        "explore-1629-04", "explore-1629-05", "explore-1629-06",
    ];

    private sealed record RunResult(
        EndgameOutcome Outcome,
        string? HardFailureReason,
        long AvailableGrainDays,
        int ReadinessValue,
        long TreasuryRemaining,
        int LocalBurden,
        int MinisterTrust,
        bool PausedAtEnd,
        string StateHash);

    internal static void RunAll()
    {
        Console.WriteLine("== 四策略 90 日探索（陆运 / 海运 / 陆海并行 / 并行+减耗 × 6 固定种子；M5 质量门运行证据）==");
        var results = new Dictionary<StrategyMode, List<RunResult>>();
        foreach (var strategy in new[] { StrategyMode.Land, StrategyMode.Sea, StrategyMode.Both, StrategyMode.BothReduced })
        {
            results[strategy] = [];
            foreach (var seed in Seeds)
            {
                var run = RunSeed(strategy, seed);
                results[strategy].Add(run);
                Console.WriteLine($"  {StrategyName(strategy),4} {seed}: {run.Outcome,-16}" +
                    $" 硬失败={run.HardFailureReason ?? "无"} 可用粮={run.AvailableGrainDays}日 战备={run.ReadinessValue} " +
                    $"负担={run.LocalBurden} 信任={run.MinisterTrust} 银余={run.TreasuryRemaining} 自动暂停={run.PausedAtEnd} hash={run.StateHash[..12]}…");
            }
        }

        // 确定性抽查：同一（策略，种子）重放必须得到同一终局与 canonical hash；
        // 用"并行+减耗"抽查，证明减耗令的接纳/生效/日耗变化全程重放确定。
        var spot = RunSeed(StrategyMode.BothReduced, Seeds[0]);
        var spotReplay = RunSeed(StrategyMode.BothReduced, Seeds[0]);
        Program.Require(spot.StateHash == spotReplay.StateHash && spot.Outcome == spotReplay.Outcome,
            $"策略探索重放必须确定（含减耗令路径）：{spot.StateHash} vs {spotReplay.StateHash}");

        PrintDistributionTable(results);

        // 结构性门（不钉具体档位）：
        Program.Require(results.Values.All(list => list.Count == Seeds.Length),
            "每个策略必须跑完全部种子");
        Program.Require(results.Values.All(list => list.All(item => item.Outcome != EndgameOutcome.InProgress)),
            "每个策略的每个种子都必须得到终局分档（不能停在 InProgress）");
        var distinctOutcomes = results.Values.SelectMany(list => list.Select(item => item.Outcome)).Distinct().ToArray();
        Program.Require(distinctOutcomes.Length >= 2,
            $"三种策略必须产生至少两种不同的终局分档（实际：{string.Join(",", distinctOutcomes)}）——" +
            "若全部相同，说明策略差异或当前平衡尚未产生可区分结果");
        // 陆海并行（当前唯一不硬失败的策略）全程不得触发自动暂停：与 AutoPauseAcceptance 互为印证。
        Program.Require(results[StrategyMode.Both].All(item => !item.PausedAtEnd),
            "陆海并行策略不应触发硬失败自动暂停（无硬失败推进不暂停）");
        // M5 通关证据（契约验收）：并行+减耗策略（减耗令杠杆）全部种子达到 BarelyMaintained 或更好；
        // 不钉死具体档位，但减耗令是纸面推演 §3.2 的 M5 通关杠杆，未达勉强维持即说明杠杆失效。
        Program.Require(results[StrategyMode.BothReduced].All(item =>
                item.Outcome is EndgameOutcome.BarelyMaintained or EndgameOutcome.Success or EndgameOutcome.Excellent),
            $"并行+减耗策略全部种子必须达到 BarelyMaintained 或更好（通关证据），实际：{string.Join(",", results[StrategyMode.BothReduced].Select(item => item.Outcome.ToString()))}");
        Console.WriteLine("  四策略探索器完成：终局分档分布表已打印（M5 质量门运行证据）。");
    }

    private static RunResult RunSeed(StrategyMode strategy, string seedId)
    {
        var world = Ningyuan1629InitialWorld.Load();
        var runtime = new RealtimeSimulationRuntime(CreateSeedWorld(seedId, world));
        runtime.ScheduleScenarioRiskSamples();
        var start = runtime.ReadModel.GameTime;
        var routes = world.Logistics.Routes.ToDictionary(item => item.Key.Value, item => item.Value, StringComparer.Ordinal);

        // 第 0 天先发首段：让补给链尽早启动（与 SmokeTests 第 0 天发首批一致）。
        foreach (var firstLeg in FirstLegs(strategy))
        {
            DispatchBestEffort(runtime, routes, firstLeg, $"first-{seedId}");
        }

        for (var day = 1; day <= EndgameEvaluator.ScenarioDurationDays; day++)
        {
            // 硬失败自动暂停后世界冻结：停止调度，终局评估自会给出 HardFailure。
            if (runtime.IsPaused)
            {
                break;
            }

            // 并行+减耗：第 21 日起减耗（纸面推演 §3.2 需求日历）。在推进到第 21 日之前接纳减耗令，
            // 使当天每日心跳按 240 石/日结算；预先计划（硬失败前发布）不扣大臣信任。
            if (strategy == StrategyMode.BothReduced && day == 21)
            {
                EnqueueRationReductionDecree(runtime, seedId, start);
                var enacted = runtime.AdvanceTo(runtime.ReadModel.GameTime);
                Program.Require(enacted.Succeeded && enacted.CommandResults.Single().Accepted,
                    $"{StrategyName(strategy)} {seedId} 第 21 日减耗令必须被接纳：{string.Join(";", enacted.CommandResults.SelectMany(item => item.Errors.Select(error => error.Code)))}");
                Program.Require(runtime.ReadModel.Scenario.DailyGrainDemand == 240,
                    $"{StrategyName(strategy)} {seedId} 第 21 日减耗令必须生效 240 石/日");
            }

            var result = runtime.AdvanceTo(new GameTime(start.Value.AddDays(day)));
            Program.Require(result.Succeeded && result.Errors.Count == 0,
                $"{StrategyName(strategy)} {seedId} 第 {day} 天推进失败：{string.Join("; ", result.Errors.Select(error => error.Code))}");

            // 中继/续运：中转仓有粮就沿链运下一段（下游优先，先清末端瓶颈再补上游）。
            foreach (var relayLeg in RelayOrder(strategy))
            {
                DispatchBestEffort(runtime, routes, relayLeg, $"relay-{seedId}");
            }

            // 首段继续按需发运：来源仓有粮且路线/目的仓可接纳。
            foreach (var firstLeg in FirstLegs(strategy))
            {
                DispatchBestEffort(runtime, routes, firstLeg, $"first-{seedId}");
            }
        }

        var evaluation = runtime.EvaluateEndgame();
        return new RunResult(
            evaluation.Outcome,
            evaluation.HardFailureReason,
            evaluation.AvailableGrainDays,
            evaluation.ReadinessValue,
            evaluation.TreasuryRemaining,
            evaluation.LocalBurden,
            evaluation.MinisterTrust,
            runtime.IsPaused,
            runtime.StateHash);
    }

    /// <summary>
    /// 贪心发运：只发"现在一定被接纳"的批次（预检镜像 ApplyShipment 的来源/路线/目的仓规则），
    /// 因此探索器不会因容量/库存拒绝而污染轨迹；每次发运后立即推进到当前时间接纳并出发。
    /// </summary>
    private static void DispatchBestEffort(
        RealtimeSimulationRuntime runtime,
        IReadOnlyDictionary<string, RouteState> routes,
        string routeId,
        string prefix)
    {
        if (!routes.TryGetValue(routeId, out var route))
        {
            return;
        }

        var model = runtime.ReadModel;
        var source = model.Stockpiles.FirstOrDefault(item => item.Id == route.FromStockpileId);
        if (source is null || source.GrainQuantity <= 0)
        {
            return;
        }

        var quantity = Math.Min(source.GrainQuantity, route.Capacity);
        if (!CanDispatch(model, routes, route, quantity))
        {
            return;
        }

        var shipmentId = $"{prefix}-{routeId}-{model.WorldVersion}";
        runtime.EnqueueCreateShipment(new CreateShipmentCommand(
            $"{prefix}-cmd-{routeId}-{model.WorldVersion}", new CharacterId("duliaoxiang-slot"),
            new ShipmentId(shipmentId), route.Id, quantity, model.GameTime.Value, model.WorldVersion, Escort: true));
        var drained = runtime.AdvanceTo(model.GameTime);
        Program.Require(drained.Succeeded && drained.CommandResults.Single().Accepted,
            $"探索器批次 {shipmentId} 必须被接纳：{string.Join(";", drained.CommandResults.SelectMany(item => item.Errors.Select(error => error.Code)))}");
    }

    /// <summary>预检镜像 ApplyShipment：来源有粮、路线在途容量、目的仓容量（预留按实到量计算）。</summary>
    private static bool CanDispatch(
        RealtimeReadModel model,
        IReadOnlyDictionary<string, RouteState> routes,
        RouteState route,
        long quantity)
    {
        if (quantity <= 0 || quantity > route.Capacity)
        {
            return false;
        }

        var inTransit = model.Shipments
            .Where(item => item.RouteId == route.Id && item.Status != ShipmentStatus.Arrived)
            .Sum(item => item.GrainQuantity);
        if (inTransit > route.Capacity - quantity)
        {
            return false;
        }

        var destination = model.Stockpiles.FirstOrDefault(item => item.Id == route.ToStockpileId);
        if (destination is null)
        {
            return false;
        }

        var available = destination.Capacity - destination.GrainQuantity;
        var reserved = ReservedIncoming(model, routes, route.ToStockpileId);
        return reserved <= available && quantity <= available - reserved;
    }

    /// <summary>目的地容量只预留最终可交付粮食（与 LogisticsState.ReservedIncomingGrain 同口径）。</summary>
    private static long ReservedIncoming(
        RealtimeReadModel model,
        IReadOnlyDictionary<string, RouteState> routes,
        StockpileId destinationId)
    {
        long total = 0;
        foreach (var shipment in model.Shipments.Where(item => item.Status != ShipmentStatus.Arrived))
        {
            if (!routes.TryGetValue(shipment.RouteId.Value, out var route) || route.ToStockpileId != destinationId)
            {
                continue;
            }

            if (!route.TryCalculateDeliveredGrain(shipment.GrainQuantity, out var delivered, out _))
            {
                return long.MaxValue;
            }

            total = checked(total + delivered);
        }

        return total;
    }

    /// <summary>策略 → 首段（来源仓直接发运的路线）：陆=北京→通州；海=登州→觉华岛；双路=两者。</summary>
    private static string[] FirstLegs(StrategyMode strategy) => strategy switch
    {
        StrategyMode.Land => ["route-beijing-tongzhou"],
        StrategyMode.Sea => ["route-dengzhou-juehuadao"],
        _ => ["route-beijing-tongzhou", "route-dengzhou-juehuadao"],
    };

    /// <summary>策略 → 中继续运顺序（下游优先：先清末端再补上游；双路两条链互不相交，顺序只影响同链内）。</summary>
    private static string[] RelayOrder(StrategyMode strategy) => strategy switch
    {
        StrategyMode.Land => ["route-shanhaiguan-ningyuan", "route-tongzhou-shanhaiguan"],
        StrategyMode.Sea => ["route-juehuadao-ningyuan"],
        _ => ["route-shanhaiguan-ningyuan", "route-juehuadao-ningyuan", "route-tongzhou-shanhaiguan"],
    };

    private static string StrategyName(StrategyMode strategy) => strategy switch
    {
        StrategyMode.Land => "陆运",
        StrategyMode.Sea => "海运",
        StrategyMode.BothReduced => "并行+减耗",
        _ => "陆海并行",
    };

    /// <summary>
    /// 第 21 日发布减耗令（纸面推演 §3.2 并行方案日历）：承办人=督辽饷槽位（world.json 持 PlanLogistics 任意辖区）。
    /// 期限设在 90 日窗口内，减耗令记录将在期限时进入 Expired 终态（sim 政令模型无"减耗完成"事件；
    /// 权威减耗状态在 ScenarioState.RationReductionActive，不受政令记录终态影响），保证终局审计链完整。
    /// </summary>
    private static void EnqueueRationReductionDecree(RealtimeSimulationRuntime runtime, string seedId, GameTime start)
    {
        var model = runtime.ReadModel;
        runtime.EnqueueCreateDecree(new CreateDecreeCommand(
            $"{seedId}-ration-reduction", new CharacterId("zhu-youjian"), new DecreeId($"{seedId}-ration-reduction"),
            "减耗令：前线日耗 300→240 石/日（纸面推演 §3.2）", new ProvinceId("ningyuan"), 100,
            new CharacterId("duliaoxiang-slot"), new GameTime(start.Value.AddDays(30)),
            "", "预先计划减耗", LinkedShipmentId: null, model.GameTime.Value, model.WorldVersion,
            DecreeKind.RationReduction));
    }

    /// <summary>种子 → 剧本世界变体：只有世界编号不同，其余与 Ningyuan1629InitialWorld.Load() 完全一致。</summary>
    private static WorldState CreateSeedWorld(string seedId, WorldState baseWorld) =>
        WorldState.CreateInitial(
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

    /// <summary>终局分档分布表：每策略各档种子数与通关率（成功+优秀），作为质量门可核验输出。</summary>
    private static void PrintDistributionTable(IReadOnlyDictionary<StrategyMode, List<RunResult>> results)
    {
        EndgameOutcome[] tiers = [EndgameOutcome.HardFailure, EndgameOutcome.Failed, EndgameOutcome.BarelyMaintained, EndgameOutcome.Success, EndgameOutcome.Excellent];
        Console.WriteLine();
        Console.WriteLine("  每策略终局分档分布（种子数 / 占比）：");
        Console.WriteLine("  策略      种子数  HardFailure  Failed  BarelyMaintained  Success  Excellent  通关率");
        foreach (var strategy in new[] { StrategyMode.Land, StrategyMode.Sea, StrategyMode.Both, StrategyMode.BothReduced })
        {
            var list = results[strategy];
            var counts = tiers.ToDictionary(tier => tier, _ => 0);
            foreach (var run in list)
            {
                counts[run.Outcome] = counts[run.Outcome] + 1;
            }

            var pass = counts[EndgameOutcome.Success] + counts[EndgameOutcome.Excellent];
            var cells = string.Join("  ", tiers.Select(tier => $"{counts[tier],-12}"));
            Console.WriteLine($"  {StrategyName(strategy),7}  {list.Count,-7}  {cells}  {100.0 * pass / list.Count:F0}%");
        }

        Console.WriteLine();
    }
}
