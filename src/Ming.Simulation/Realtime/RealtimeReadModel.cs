using System.Collections.ObjectModel;
using MingSim.Domain;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Realtime;

namespace MingSim.Simulation.Realtime;

/// <summary>不可变 ReadModel：UI 可以读取，但拿不到任何 WorldState 或领域写入口。</summary>
public sealed record RealtimeReadModel(
    WorldId WorldId,
    int TurnNumber,
    GameTime GameTime,
    long WorldVersion,
    string CommitId,
    IReadOnlyList<ArmyReadModel> Armies,
    IReadOnlyList<MovementReadModel> Movements,
    IReadOnlyList<StockpileReadModel> Stockpiles,
    IReadOnlyList<ShipmentReadModel> Shipments,
    IReadOnlyList<ScheduledActionReadModel> ScheduledActions,
    IReadOnlyList<CommandOutcome> CommandOutcomes,
    int OutboxEventCount,
    string StateHash)
{
    internal static RealtimeReadModel From(
        WorldState state,
        IEnumerable<ScheduledSimulationEvent> scheduled,
        IEnumerable<CommandOutcome> outcomes,
        int outboxEventCount,
        string hash) =>
        new(
            state.Id,
            state.TurnNumber,
            state.GameTime,
            state.WorldVersion,
            state.CommitId,
            new ReadOnlyCollection<ArmyReadModel>(state.Military.Armies.Values
                .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
                .Select(item => new ArmyReadModel(item.Id, item.Name, item.LocationId, item.Auxiliaries, item.LineInfantry, item.TrainingDays))
                .ToArray()),
            new ReadOnlyCollection<MovementReadModel>(state.Movements.Values
                .OrderBy(item => item.ArmyId.Value, StringComparer.Ordinal)
                .Select(item => new MovementReadModel(item.ActionId, item.ArmyId, item.Origin, item.Destination, item.DueGameTime))
                .ToArray()),
            new ReadOnlyCollection<StockpileReadModel>(state.Logistics.Stockpiles.Values
                .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
                .Select(item => new StockpileReadModel(item.Id, item.LocationId, item.Capacity, item.GrainQuantity))
                .ToArray()),
            new ReadOnlyCollection<ShipmentReadModel>(state.Logistics.Shipments.Values
                .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
                .Select(item => new ShipmentReadModel(item.Id, item.RouteId, item.Status, item.GrainQuantity, item.DeliveredGrain, item.LossGrain))
                .ToArray()),
            new ReadOnlyCollection<ScheduledActionReadModel>(scheduled.OrderBy(item => item.DueGameTime).ThenBy(item => item.Phase).ThenBy(item => item.Priority).ThenBy(item => item.CreationSequence)
                .Select(item => new ScheduledActionReadModel(item.EventId, item.DueGameTime, item.Phase, item.Priority, item.CreationSequence, item.EventType))
                .ToArray()),
            new ReadOnlyCollection<CommandOutcome>(outcomes.OrderBy(item => item.IngressSequence).ToArray()),
            outboxEventCount,
            hash);
}

public sealed record ArmyReadModel(ArmyId Id, string Name, ProvinceId LocationId, long Auxiliaries, long LineInfantry, int TrainingDays);

public sealed record MovementReadModel(string ActionId, ArmyId ArmyId, ProvinceId Origin, ProvinceId Destination, GameTime DueGameTime);

public sealed record StockpileReadModel(StockpileId Id, ProvinceId LocationId, long Capacity, long GrainQuantity);

public sealed record ShipmentReadModel(ShipmentId Id, RouteId RouteId, ShipmentStatus Status, long GrainQuantity, long DeliveredGrain, long LossGrain);

public sealed record ScheduledActionReadModel(string EventId, GameTime DueGameTime, int Phase, int Priority, long CreationSequence, string EventType);
