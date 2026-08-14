using System.Diagnostics;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Map;
using MingSim.Domain.Realtime;
using MingSim.Persistence.InMemory;
using MingSim.Simulation.Realtime;

namespace MingSim.SoakTests;

/// <summary>
/// 一年（365 日）合成世界长跑（doc 11 §10：先记录量级，不伪造性能门槛）。
/// 纯物流合成世界：两条路线交替运粮 + 365 个每日心跳；输出每次推进的 CPU 时间分布
/// 与内存量级摘要；任何推进失败或命令拒绝都会让整个测试失败（无异常验收）。
/// 中途（第 183 日）从提交商店恢复第二个实例继续推进，验证长程恢复后的终局 hash
/// 与不间断运行完全一致（doc 08 §20 Replay 行的长程形态）。
/// </summary>
internal static class SyntheticYearLongRun
{
    private const int Days = 365;
    // 选第 183 日（183 % 7 != 0）作为恢复点：该日推进结束后收件箱已全部接纳提交，
    // 商店最新快照即完整状态（若选 182 日，第 182 天结束时刚入箱的批次尚未提交，恢复实例会漏掉它）。
    private const int MidRunRestoreDay = 183;

    internal static void RunAll()
    {
        Console.WriteLine("== 一年（365 日）合成世界长跑（无异常 + 量级摘要）==");
        var store = new InMemoryCommitStore();
        var runtime = new RealtimeSimulationRuntime(CreateSyntheticWorld(), store);
        var timings = new List<long>();
        var peakManaged = 0L;

        // 世界初始时刻是两条推进路径的共同纪元：恢复实例也必须推进到同一绝对目标时间。
        var epoch = runtime.ReadModel.GameTime;
        var beforeCpu = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        // 前半段（1..第 183 日）：不间断主实例推进，商店最新提交即第 183 日完整状态。
        RunYearScript(runtime, epoch, fromDay: 0, toDay: MidRunRestoreDay, timings, ref peakManaged);
        var restored = RealtimeSimulationRuntime.RestoreFromStore(store);
        Program.Require(restored.ReadModel.GameTime.Value == epoch.Value.AddDays(MidRunRestoreDay),
            $"恢复点必须等于第 {MidRunRestoreDay} 日（由商店最新提交保证）：{restored.ReadModel.GameTime.Value} vs {epoch.Value.AddDays(MidRunRestoreDay)}");
        // 后半段（184..365 日）：主实例继续推进；恢复实例从同一状态推进同一输入流。
        RunYearScript(runtime, epoch, fromDay: MidRunRestoreDay, toDay: Days, timings, ref peakManaged);
        RunYearScript(restored, epoch, fromDay: MidRunRestoreDay, toDay: Days, timings, ref peakManaged);
        wall.Stop();
        var cpuUsed = Process.GetCurrentProcess().TotalProcessorTime - beforeCpu;

        var finalHash = runtime.StateHash;
        var finalVersion = runtime.ReadModel.WorldVersion;
        var eventCount = runtime.OutboxEvents.Count;

        Program.Require(restored.StateHash == finalHash,
            $"中途恢复继续推进的终局 StateHash 必须与不间断运行一致：{restored.StateHash} vs {finalHash}");

        Console.WriteLine($"  推进 {timings.Count} 次；总墙钟 {wall.Elapsed.TotalSeconds:F2} 秒；总 CPU {cpuUsed.TotalSeconds:F2} 秒");
        Console.WriteLine($"  每次推进时间分布：{TimingSummary.Format(timings)}");
        Console.WriteLine($"  事件 {eventCount} 条；最终 WorldVersion {finalVersion}（≈提交次数）；终局 hash={finalHash[..12]}…");
        Console.WriteLine($"  内存量级：峰值托管堆 {peakManaged / 1024 / 1024}MB；结束托管堆 {GC.GetTotalMemory(forceFullCollection: false) / 1024 / 1024}MB；" +
            $"工作集 {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
        Console.WriteLine($"  中途恢复（第 {MidRunRestoreDay} 日）继续推进 → 终局 hash 与不间断运行一致");
    }

    /// <summary>
    /// 固定年度脚本：每 7 个游戏日发出一个 10000 石批次，路线 A/B 交替
    /// （A：30 日行程/5‰损耗；B：20 日行程/8‰损耗），运力都不触发容量拒绝，
    /// 让 365 个每日心跳与运输生命周期持续跑满一整年。
    /// </summary>
    private static void RunYearScript(
        RealtimeSimulationRuntime runtime, GameTime epoch, int fromDay, int toDay,
        List<long> timings, ref long peakManaged)
    {
        if (fromDay == 0)
        {
            runtime.EnqueueCreateShipment(new CreateShipmentCommand(
                "synthetic-365-ship-0", new CharacterId("works"), new ShipmentId("synthetic-365-ship-0"),
                new RouteId("route-a"), 10_000, epoch.Value, runtime.ReadModel.WorldVersion));
        }

        for (var day = fromDay + 1; day <= toDay; day++)
        {
            var advance = Stopwatch.StartNew();
            var result = runtime.AdvanceTo(new GameTime(epoch.Value.AddDays(day)));
            advance.Stop();
            timings.Add(advance.Elapsed.Ticks / 10);
            Program.Require(result.Succeeded && result.Errors.Count == 0,
                $"合成世界第 {day} 天推进失败：{string.Join("; ", result.Errors.Select(error => error.Code))}");
            // 与 90 日脚本一致：任何命令拒绝都显式失败，不允许补给链静默退化。
            Program.Require(result.CommandResults.All(item => item.Accepted),
                $"合成世界第 {day} 天出现命令拒绝：{string.Join("; ", result.CommandResults.Where(item => !item.Accepted).Select(item => $"{item.CommandId}:{string.Join(",", item.Errors.Select(error => error.Code))}"))}");

            if (day % 7 == 0)
            {
                var route = (day / 7) % 2 == 0 ? new RouteId("route-a") : new RouteId("route-b");
                runtime.EnqueueCreateShipment(new CreateShipmentCommand(
                    $"synthetic-365-ship-{day}", new CharacterId("works"), new ShipmentId($"synthetic-365-ship-{day}"),
                    route, 10_000, runtime.ReadModel.GameTime.Value, runtime.ReadModel.WorldVersion));
            }

            var managed = GC.GetTotalMemory(forceFullCollection: false);
            if (managed > peakManaged)
            {
                peakManaged = managed;
            }
        }
    }

    /// <summary>合成世界：两处粮仓、两条路线；没有场景规则（Scenario 不激活），专注物流与每日心跳长跑。</summary>
    private static WorldState CreateSyntheticWorld()
    {
        var map = new MapDefinition(
            "synthetic-map",
            [
                new ProvinceDefinition(new ProvinceId("source"), "产粮地", [new ProvinceId("frontier")]),
                new ProvinceDefinition(new ProvinceId("frontier"), "边地", [new ProvinceId("source")]),
            ]);
        return WorldState.CreateInitial(
            new WorldId("synthetic-365"),
            1,
            200_000,
            map,
            characters:
            [
                new CharacterState(new CharacterId("works"), "合成运粮官",
                    new CharacterAttributes(80, 60, 30, 40, 70),
                    new CharacterPersonality(true, false, true, true)),
            ],
            capabilityGrants:
            [
                new CapabilityGrant(new CharacterId("works"), GameCapability.PlanLogistics, null),
            ],
            stockpiles:
            [
                new StockpileState(new StockpileId("source-granary"), new ProvinceId("source"), 2_000_000, 1_500_000),
                new StockpileState(new StockpileId("frontier-granary"), new ProvinceId("frontier"), 1_000_000, 0),
            ],
            routes:
            [
                new RouteState(new RouteId("route-a"),
                    new StockpileId("source-granary"), new StockpileId("frontier-granary"),
                    capacity: 100_000, travelHours: 24 * 30, lossPerThousand: 50),
                new RouteState(new RouteId("route-b"),
                    new StockpileId("source-granary"), new StockpileId("frontier-granary"),
                    capacity: 100_000, travelHours: 24 * 20, lossPerThousand: 80),
            ]);
    }
}
