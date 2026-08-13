using MingSim.Domain;
using MingSim.Domain.Errors;
using MingSim.Domain.Intents;

namespace MingSim.Simulation;

/// <summary>
/// 旧应用层编排器仍需的类型兼容面；它不再允许产生第二套权威提交。
/// 新玩法必须使用 <c>RealtimeSimulationRuntime.AdvanceTo(GameTime)</c>。
/// </summary>
[Obsolete("ResolveTurn 已封存；请使用 RealtimeSimulationRuntime.AdvanceTo(GameTime)。")]
public interface ISimulationKernel
{
    TurnResolution ResolveTurn(WorldState frozenState, IReadOnlyList<WorldIntent> intents);
}

/// <summary>封存的回合兼容壳，不再执行任何世界写入。</summary>
[Obsolete("ResolveTurn 已封存；请使用 RealtimeSimulationRuntime.AdvanceTo(GameTime)。")]
public sealed class SimulationKernel : ISimulationKernel
{
    public TurnResolution ResolveTurn(WorldState frozenState, IReadOnlyList<WorldIntent> intents)
    {
        ArgumentNullException.ThrowIfNull(frozenState);
        ArgumentNullException.ThrowIfNull(intents);

        return TurnResolution.Reject(
            frozenState,
            [new SimulationError(
                "LEGACY_TURN_PATH_DISABLED",
                "旧回合提交路径已封存；请通过唯一的 GameTime + Scheduler 实时管线提交命令。")]);
    }
}
