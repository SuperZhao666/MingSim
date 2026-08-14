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
        GameCapability requiredCapability,
        string? requiredResourceId,
        string? linkedShipmentId,
        DateTimeOffset submittedAt,
        long expectedWorldVersion) =>
        _runtime.EnqueueCreateDecree(new CreateDecreeCommand(
            commandId, actorId, decreeId, goal, regionScope, budget, responsibleActorId, deadline,
            restrictions, remarks, requiredCapability, requiredResourceId, linkedShipmentId,
            submittedAt, expectedWorldVersion));
}
