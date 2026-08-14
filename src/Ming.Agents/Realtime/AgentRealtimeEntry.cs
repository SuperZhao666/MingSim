using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Common;
using MingSim.Domain.Intents;
using MingSim.Simulation.Realtime;

namespace MingSim.Agents.Realtime;

/// <summary>入口对单个 Agent 意图的提交结果；错误码与内核保持一致。</summary>
public sealed record AgentIntentResult(
    string IntentId,
    bool Accepted,
    string Message,
    string? ErrorCode,
    string? CommandId);

/// <summary>
/// Agent 意图 → 实时内核的唯一入口，也是 Agent 改写世界的唯一合法通道。
/// </summary>
/// <remarks>
/// 职责只有三步：
/// 1. 把规则/模型 Agent 产出的结构化意图转换成内核已接受的 RealtimeCommand；
/// 2. 携带 ActorId 走 <see cref="CapabilityAuthorizer"/> 权限预检，未授权意图在进入
///    内核前就结构化拒绝（TOOL_SCOPE_DENIED），不产生任何副作用；
/// 3. 把通过预检的命令投递给 <see cref="RealtimeSimulationRuntime"/> 收件箱。
///
/// 边界约束：
/// - 本类不写 WorldState、不建第二时钟，也不接触任何模型 Provider——模型路径是可选的，
///   规则路径默认可用；
/// - 内核在安全点会再次校验权限、版本与前置条件，入口预检只是尽早拒绝，不是唯一防线；
/// - 命令编号直接采用意图的 IdempotencyKey，保证同一意图的重复提交被内核幂等去重。
/// </remarks>
public sealed class AgentRealtimeEntry
{
    private const string UnsupportedIntentCode = "UNSUPPORTED_INTENT";

    private readonly RealtimeSimulationRuntime _runtime;
    private readonly CapabilityAuthorizer _authorizer = new();

    public AgentRealtimeEntry(RealtimeSimulationRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>按输入顺序提交一组意图；每个意图独立返回结构化结果。</summary>
    public IReadOnlyList<AgentIntentResult> Submit(WorldState world, IReadOnlyList<WorldIntent> intents)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(intents);
        var results = new List<AgentIntentResult>(intents.Count);
        foreach (var intent in intents)
        {
            results.Add(SubmitOne(world, intent));
        }

        return results;
    }

    private AgentIntentResult SubmitOne(WorldState world, WorldIntent intent)
    {
        switch (intent)
        {
            case PlanLogisticsIntent logistics:
                return SubmitLogistics(world, logistics);
            case MoveArmyIntent move:
                return SubmitMove(world, move);
            default:
                return new AgentIntentResult(
                    intent.IntentId,
                    false,
                    $"意图类型 {intent.GetType().Name} 不在实时内核支持范围内。",
                    UnsupportedIntentCode,
                    null);
        }
    }

    private AgentIntentResult SubmitLogistics(WorldState world, PlanLogisticsIntent intent)
    {
        var authorization = _authorizer.Check(
            world, intent.ActorId, GameCapability.PlanLogistics, intent.RouteId.Value);
        if (!authorization.Allowed)
        {
            return Denied(intent.IntentId, authorization.Reason);
        }

        var command = new CreateShipmentCommand(
            intent.IdempotencyKey,
            intent.ActorId,
            new ShipmentId($"shipment-{intent.IdempotencyKey}"),
            intent.RouteId,
            intent.GrainQuantity,
            intent.SubmittedAt,
            intent.ExpectedWorldVersion);
        return ToResult(intent.IntentId, _runtime.EnqueueCreateShipment(command));
    }

    private AgentIntentResult SubmitMove(WorldState world, MoveArmyIntent intent)
    {
        var authorization = _authorizer.Check(
            world, intent.ActorId, GameCapability.MoveArmy, intent.ArmyId.Value);
        if (!authorization.Allowed)
        {
            return Denied(intent.IntentId, authorization.Reason);
        }

        var command = new MoveArmyCommand(
            intent.IdempotencyKey,
            intent.ActorId,
            intent.ArmyId,
            intent.DestinationId,
            intent.SubmittedAt,
            intent.ExpectedWorldVersion,
            intent.TravelHours);
        return ToResult(intent.IntentId, _runtime.EnqueueMoveArmy(command));
    }

    private static AgentIntentResult Denied(string intentId, string reason) =>
        new(intentId, false, reason, "TOOL_SCOPE_DENIED", null);

    private static AgentIntentResult ToResult(string intentId, RealtimeCommandReceipt receipt)
    {
        if (receipt.Queued)
        {
            return new AgentIntentResult(intentId, true, receipt.Message, null, receipt.CommandId);
        }

        var error = receipt.Errors.FirstOrDefault();
        return new AgentIntentResult(intentId, false, receipt.Message, error?.Code, receipt.CommandId);
    }
}
