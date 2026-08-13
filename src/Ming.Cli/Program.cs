using MingSim.Agents.Decision;
using MingSim.Agents.Runtime;
using MingSim.Application.Scenarios;
using MingSim.Application.Workflows;
using MingSim.Domain.Common;
using MingSim.Persistence.InMemory;
using MingSim.Simulation;

namespace MingSim.Cli;

/// <summary>
/// 这是整个程序的“入口文件”（命令行版本）。
/// 你可以把它理解为：
/// 1) 读场景文件（世界初始状态）
/// 2) 组装执行工具（模拟内核、世界仓库、审计日志、快照）
/// 3) 让多个代理人给出动作意图
/// 4) 执行这一回合
/// 5) 打印结果
/// 
/// 这里故意是“最小化可运行版本”，没有 UI（图形界面），
/// 适合先验证规则和数据流是否正确。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 命令行主函数。
    /// 返回码约定：
    /// 0 = 成功
    /// 1 = 回合执行失败（规则拒绝）
    /// 2 = 输入文件不存在（路径问题）
    /// </summary>
    private static int Main(string[] args)
    {
        // ===== 步骤 1：确定要加载的场景文件 =====
        // args 是命令行参数数组。
        // 如果你启动程序时写了参数：Ming.Cli.exe xxx/world.json，那么 args[0] 有值。
        // 如果没写参数，就使用内置默认场景。
        // Path.GetFullPath(...) 把用户传入/默认拼接出的路径，转成绝对路径，
        // 这样报错时路径可读性更强。
        var scenarioPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine("content", "ming_1627", "world.json"));

        // ===== 步骤 2：文件存在性检查 =====
        // 先确认文件存在，比后续 Load 更早发现错误更安全。
        if (!File.Exists(scenarioPath))
        {
            // 写入标准错误流，便于脚本区分“正常输出”和“错误输出”。
            Console.Error.WriteLine($"未找到场景文件：{scenarioPath}");
            Console.Error.WriteLine("请确认工作目录和 JSON 文件路径是否正确，例如：content\\ming_1627\\world.json");
            // 返回 2：表示输入文件缺失，和回合失败的 1 区分开。
            return 2;
        }

        // ===== 步骤 3：加载初始世界状态 =====
        // ScenarioLoader 把 world.json 转换成世界对象（WorldState）。
        // 这一步会把以下内容装进世界里：
        // - 世界 id、当前回合号、国库银两
        // - 角色（minister-works/minister-war 等）
        // - 机构与能力授权（谁能做什么）
        // - 军队、库存、设施等基础数据
        var initialWorld = new ScenarioLoader().Load(scenarioPath);

        // ===== 步骤 4：组装应用层服务（内存版）===== 
        // worldStore：读写世界状态；
        // auditJournal：记录本回合事件日志；
        // snapshotStore：生成快照、计算 hash、做有效性校验；
        // kernel：核心规则引擎；
        // orchestrator：将“加载 -> 计算 -> 校验 -> 提交”统一成一个流程。
        var worldStore = new InMemoryWorldStore(initialWorld);
        var auditJournal = new InMemoryAuditJournal();
        var snapshotStore = new InMemorySnapshotStore();
        var kernel = new SimulationKernel();
        var orchestrator = new TurnOrchestrator(worldStore, auditJournal, snapshotStore, kernel);

        // ===== 步骤 5：收集代理人的动作意图 =====
        // 注意：这里的 Agent 只“提出动作”，并不直接改世界。
        // 真正改世界的是后续的 SimulationKernel。
        var decisionSources = new AgentRuntime().CollectDecisions(
            worldStore.Load(initialWorld.Id),
            [
                // 给 id=minister-works 的角色，挂工业策略。
                // 工业策略通常会尝试建厂。
                new AgentRegistration(
                    new CharacterId("minister-works"),
                    new RuleBasedMinisterAgent(MinisterFocus.Industry)),
                // 给 id=minister-war 的角色，挂军事策略。
                // 军事策略通常会尝试转换军队兵种。
                new AgentRegistration(
                    new CharacterId("minister-war"),
                    new RuleBasedMinisterAgent(MinisterFocus.Military)),
            ]);

        // ===== 步骤 6：打印执行前信息（方便你先确认输入） =====
        Console.WriteLine("=== MingSim 本轮执行 ===");
        Console.WriteLine($"世界：{initialWorld.Id}，当前回合：{initialWorld.TurnNumber}");
        Console.WriteLine($"本轮代理输出意图数：{decisionSources.Count}");

        // 把每个意图列出来，便于你追踪“是谁在本回合做了什么”。
        foreach (var intent in decisionSources)
        {
            Console.WriteLine($"  - 意图：{intent.IntentId}，角色：{intent.ActorId}，目标回合：{intent.ExpectedTurn}");
        }

        // ===== 步骤 7：执行回合 =====
        // 这里把世界 id + 意图列表丢给编排器。
        // 编排器内部会调用：
        // 1) 将当前世界克隆为工作副本
        // 2) 逐条处理意图并生成新状态/事件
        // 3) 运行不变量检查
        // 4) 做快照校验
        // 5) 全部通过则提交世界、新增事件
        var result = orchestrator.ExecuteTurn(initialWorld.Id, decisionSources);

        // ===== 步骤 8：失败分支处理 =====
        // 如果没提交成功，世界应保持不变，返回错误原因列表。
        if (!result.Committed)
        {
            Console.WriteLine("回合执行失败，世界状态未发生更改。");
            foreach (var error in result.Errors)
            {
                Console.WriteLine($"  错误码[{error.Code}]：{error.Message}");
            }

            // 返回 1：表示“执行层面失败”。
            return 1;
        }

        // ===== 步骤 9：成功分支，读取提交后的世界 =====
        // 为了拿到最终结果，重新从 worldStore 读一次。
        var committedWorld = worldStore.Load(initialWorld.Id);
        // 示例里固定读取这两个对象来展示：
        // - army-frontier（军队）
        // - flintlock（库存资源）
        var army = committedWorld.Military.Armies[new ArmyId("army-frontier")];
        var flintlock = committedWorld.Economy.Inventory.GetOrCreate("flintlock");

        // ===== 步骤 10：打印成功结果 =====
        Console.WriteLine("回合执行成功：");
        Console.WriteLine($"  回合：{result.PreviousTurn} -> {result.NewTurn}");
        Console.WriteLine($"  库存银两：{committedWorld.Economy.Treasury.Silver}");
        Console.WriteLine($"  军队（army-frontier）：辅助兵={army.Auxiliaries}，正兵={army.LineInfantry}");
        Console.WriteLine($"  火绳枪库存：{flintlock.Quantity}");
        // 从审计日志读事件数量，做执行审计核对（结果里也有 EventCount）。
        Console.WriteLine($"  审计事件数量：{auditJournal.Read(initialWorld.Id).Count}");
        Console.WriteLine($"  快照哈希：{result.StateHash}");

        // ===== 步骤 11：返回 0 表示完全成功 =====
        return 0;
    }
}
