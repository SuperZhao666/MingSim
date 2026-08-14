using MingSim.Application.Scenarios;
using MingSim.Domain.Common;
using MingSim.Domain.Realtime;
using MingSim.Simulation.Realtime;

namespace MingSim.Cli;

/// <summary>
/// 命令行只演示唯一的实时 Simulation 管线：Scenario → Runtime → ReadModel。
/// 旧 TurnOrchestrator/ResolveTurn 不再是可运行玩法入口。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var scenarioPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine("content", "ming_1627", "world.json"));

        if (!File.Exists(scenarioPath))
        {
            Console.Error.WriteLine($"未找到场景文件：{scenarioPath}");
            return 2;
        }

        var initialWorld = new ScenarioLoader().Load(scenarioPath);
        var runtime = new RealtimeSimulationRuntime(initialWorld);
        var before = runtime.ReadModel;
        var target = new GameTime(before.GameTime.Value.AddDays(1));
        var result = runtime.AdvanceTo(target);

        Console.WriteLine("=== MingSim 实时推进 ===");
        Console.WriteLine($"世界：{before.WorldId}");
        Console.WriteLine($"游戏时间：{before.GameTime} -> {result.ReadModel.GameTime}");
        Console.WriteLine($"WorldVersion：{before.WorldVersion} -> {result.ReadModel.WorldVersion}");
        Console.WriteLine($"调度处理：{result.ProcessedScheduledEvents}，待处理：{result.PendingScheduledEvents}");
        Console.WriteLine($"事件数量：{result.Events.Count}，Outbox 总数：{result.ReadModel.OutboxEventCount}");
        Console.WriteLine($"StateHash：{result.StateHash}");

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine($"错误码[{error.Code}]：{error.Message}");
            }

            return 1;
        }

        return 0;
    }
}
