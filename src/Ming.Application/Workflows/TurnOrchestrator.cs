using MingSim.Application.Ports;
using MingSim.Domain.Common;
using MingSim.Domain.Errors;
using MingSim.Domain.Intents;
using MingSim.Simulation;

namespace MingSim.Application.Workflows;

/// <summary>应用层向外报告的回合执行结果。</summary>
public sealed record TurnExecutionResult(
    bool Committed,
    WorldId WorldId,
    int PreviousTurn,
    int? NewTurn,
    string? StateHash,
    IReadOnlyList<SimulationError> Errors,
    int EventCount);

/// <summary>
/// 回合编排器：负责把“世界存储、模拟内核、审计、快照”串成一个完整工作流。
/// </summary>
/// <remarks>
/// 注意这里没有等待 LLM。模型什么时候思考、思考结果怎么产生，属于 Agents 层；
/// 编排器只接收已经形成的结构化意图，并保证它们按统一流程结算。
/// </remarks>
public sealed class TurnOrchestrator
{
    private readonly IWorldStore _worldStore;
    private readonly IAuditJournal _auditJournal;
    private readonly ISnapshotStore _snapshotStore;
    private readonly ISimulationKernel _simulationKernel;

    public TurnOrchestrator(
        IWorldStore worldStore,
        IAuditJournal auditJournal,
        ISnapshotStore snapshotStore,
        ISimulationKernel simulationKernel)
    {
        _worldStore = worldStore;
        _auditJournal = auditJournal;
        _snapshotStore = snapshotStore;
        _simulationKernel = simulationKernel;
    }

    public TurnExecutionResult ExecuteTurn(
        WorldId worldId,
        IReadOnlyList<WorldIntent> intents)
    {
        // 1. 冻结：读取一份当前状态。后续代理的决定都必须基于这个回合版本。
        var frozenState = _worldStore.Load(worldId);
        var previousTurn = frozenState.TurnNumber;

        // 2. 结算：内核要么给出新状态，要么完整拒绝，绝不返回半成功状态。
        var resolution = _simulationKernel.ResolveTurn(frozenState, intents);
        if (!resolution.Committed)
        {
            return new TurnExecutionResult(
                false,
                worldId,
                previousTurn,
                null,
                null,
                resolution.Errors,
                0);
        }

        // 3. 快照先准备、先校验。快照不合格时，当前世界指针不能向前移动。
        var preparation = _snapshotStore.Prepare(resolution.State, resolution.Events);
        if (!preparation.IsValid)
        {
            return new TurnExecutionResult(
                false,
                worldId,
                previousTurn,
                null,
                null,
                [new SimulationError(
                    "SNAPSHOT_VALIDATION_FAILED",
                    "新回合的快照校验失败，因此没有提交世界状态。")],
                0);
        }

        // 4. 短事务提交：生产、兵员、库存和财政都在这里一起成为正式历史。
        _worldStore.Commit(resolution.State);
        _auditJournal.Append(worldId, resolution.Events);
        _snapshotStore.Promote(preparation);

        return new TurnExecutionResult(
            true,
            worldId,
            previousTurn,
            resolution.State.TurnNumber,
            preparation.StateHash,
            [],
            resolution.Events.Count);
    }
}
