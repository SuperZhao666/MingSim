using Godot;
using MingSim.Application.Commands;
using MingSim.Application.Scenarios;
using MingSim.Simulation.Realtime;

namespace Ming.Godot;

/// <summary>
/// Godot 侧接入实时内核的唯一装配点（Composition Root 的最小 Godot 化身）。
/// 为什么只有这里能创建运行时：UI 节点各自 new runtime 会产生多套世界权威；
/// 全场景只通过本桥拿同一份 runtime 与命令门面，保证 UI 只读 ReadModel、只经门面提交命令。
/// 为什么 Godot 不引用 Persistence：正式 SQLite 存档由宿主进程（Composition Root，doc 04 §3）
/// 装配 ICommitStore 后注入；本原型缺省 store=null（纯内存），存档接线是宿主职责而非 UI 职责。
/// </summary>
public static class RealtimeWorldBridge
{
    /// <summary>装配 1629 宁远急饷剧本；store 由宿主进程注入，缺省纯内存。</summary>
    public static (RealtimeSimulationRuntime Runtime, CommandFacade Facade) Create(ICommitStore? store = null)
    {
        // 剧本数据全部来自 content/scenarios/ming_1629/world.json（DESIGN 数值），
        // 失败会抛异常而不是静默开一个空世界。
        // Godot 的进程工作目录是工程目录（src/Ming.Godot），因此按 res:// 相对定位仓库根下的内容文件。
        var worldJson = ResolveWorldJsonPath();
        var initialWorld = Ningyuan1629InitialWorld.Load(worldJson);
        var runtime = new RealtimeSimulationRuntime(initialWorld, store);
        runtime.ScheduleScenarioRiskSamples();
        return (runtime, new CommandFacade(runtime));
    }

    /// <summary>解析剧本 world.json 的磁盘路径（先相对工作目录，再按 res:// 定位仓库根）。</summary>
    public static string ResolveWorldJsonPath()
    {
        const string relative = "content/scenarios/ming_1629/world.json";
        if (File.Exists(relative)) return relative;

        // project.godot 位于仓库根，因此正式 Godot 资源路径是 res://content/...。
        // 保留旧 src/Ming.Godot 工作目录回退，仅用于历史 worktree/工具调用兼容。
        var fromProject = ProjectSettings.GlobalizePath("res://content/scenarios/ming_1629/world.json");
        if (File.Exists(fromProject)) return fromProject;
        var legacy = ProjectSettings.GlobalizePath("res://../../content/scenarios/ming_1629/world.json");
        if (File.Exists(legacy)) return legacy;
        throw new FileNotFoundException(
            $"找不到宁远 1629 剧本：已尝试 {relative}、{fromProject} 与 {legacy}。", relative);
    }
}
