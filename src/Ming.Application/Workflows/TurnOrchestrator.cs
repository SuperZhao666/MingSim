using MingSim.Domain.Common;
using MingSim.Domain.Errors;
using MingSim.Domain.Intents;

namespace MingSim.Application.Workflows;

/// <summary>旧回合入口的明确拒绝结果；正式玩法只使用实时 Simulation runtime。</summary>
public sealed record TurnExecutionResult(
    bool Committed,
    WorldId WorldId,
    int PreviousTurn,
    int? NewTurn,
    string? StateHash,
    IReadOnlyList<SimulationError> Errors,
    int EventCount);

/// <summary>
/// 兼容旧调用方的隔离壳。它不接收 Kernel、Store 或任何可替换提交依赖，因而不能形成第二权威路径。
/// </summary>
public sealed class TurnOrchestrator
{
    public TurnExecutionResult ExecuteTurn(WorldId worldId, IReadOnlyList<WorldIntent> intents)
    {
        ArgumentNullException.ThrowIfNull(intents);
        return new TurnExecutionResult(false, worldId, 0, null, null,
            [new SimulationError("LEGACY_TURN_PATH_DISABLED",
                "旧回合提交路径已隔离；请通过唯一的 GameTime + Scheduler 实时管线提交 Command。")], 0);
    }
}
