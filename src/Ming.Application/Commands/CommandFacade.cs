using MingSim.Domain.Authorization;
using MingSim.Domain.Common;
using MingSim.Domain.Decrees;
using MingSim.Domain.Economy;
using MingSim.Domain.Realtime;
using MingSim.Simulation.Realtime;

namespace MingSim.Application.Commands;

/// <summary>
/// 应用层命令门面：UI 只认识这里的窄方法，把参数组装成不可变 RealtimeCommand 后
/// 交给唯一的 Simulation 收件箱。门面不做任何业务校验——权限、资源、前置条件都在
/// Simulation 安全点判定；门面也不接触 WorldState，只返回结构化回执。
/// </summary>
public sealed class CommandFacade
{
    private readonly RealtimeSimulationRuntime _runtime;

    public CommandFacade(RealtimeSimulationRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public RealtimeCommandReceipt EnqueuePause(bool paused, CharacterId actorId, DateTimeOffset submittedAt, long expectedWorldVersion) =>
        _runtime.EnqueueSetPaused(new SetPausedCommand(
            $"ui-pause-{expectedWorldVersion}-{paused.ToString().ToLowerInvariant()}",
            actorId, paused, submittedAt, expectedWorldVersion));

    public RealtimeCommandReceipt EnqueueSetSpeed(double speed, CharacterId actorId, DateTimeOffset submittedAt, long expectedWorldVersion) =>
        _runtime.EnqueueSetSimulationSpeed(new SetSimulationSpeedCommand(
            $"ui-speed-{expectedWorldVersion}-{BitConverter.DoubleToInt64Bits(speed)}",
            actorId, speed, submittedAt, expectedWorldVersion));

    /// <summary>为一次调粮从权威路线中选择当前可执行路线（P1-UI-01 修复，只读投影）。</summary>
    public RouteId? ResolveRouteForGrainShipment(CharacterId actorId, long grainQuantity) =>
        _runtime.ResolveRouteForGrainShipment(actorId, grainQuantity);

    public RealtimeCommandReceipt EnqueueCreateShipment(
        string commandId,
        CharacterId actorId,
        ShipmentId shipmentId,
        RouteId routeId,
        long grainQuantity,
        bool escort,
        DateTimeOffset submittedAt,
        long expectedWorldVersion) =>
        _runtime.EnqueueCreateShipment(new CreateShipmentCommand(
            commandId, actorId, shipmentId, routeId, grainQuantity, submittedAt, expectedWorldVersion, escort));

    /// <summary>
    /// 提交一道政令：命令只携带业务意图（含 DecreeKind），不携带任何审核策略
    /// （P1-AUTH-01/02 修复）——签发人/承办人能力/资源域由内核 trusted 映射决定。
    /// </summary>
    public RealtimeCommandReceipt EnqueueCreateDecree(
        string commandId,
        CharacterId actorId,
        DecreeId decreeId,
        string goal,
        ProvinceId regionScope,
        long budget,
        CharacterId responsibleActorId,
        GameTime deadline,
        string restrictions,
        string remarks,
        string? linkedShipmentId,
        DecreeKind kind,
        DateTimeOffset submittedAt,
        long expectedWorldVersion) =>
        _runtime.EnqueueCreateDecree(new CreateDecreeCommand(
            commandId, actorId, decreeId, goal, regionScope, budget, responsibleActorId, deadline,
            restrictions, remarks, linkedShipmentId, submittedAt, expectedWorldVersion, kind));

    /// <summary>批准一道已提交的请饷奏疏（批准时才扣预算并转可执行）。</summary>
    public RealtimeCommandReceipt EnqueueApproveDecree(
        string commandId,
        CharacterId actorId,
        DecreeId decreeId,
        DateTimeOffset submittedAt,
        long expectedWorldVersion) =>
        _runtime.EnqueueApproveDecree(new ApproveDecreeCommand(
            commandId, actorId, decreeId, submittedAt, expectedWorldVersion));
}
