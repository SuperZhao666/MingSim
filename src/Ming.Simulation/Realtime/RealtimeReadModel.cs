using System.Collections.ObjectModel;
using MingSim.Domain;
using MingSim.Domain.Common;
using MingSim.Domain.Decrees;
using MingSim.Domain.Economy;
using MingSim.Domain.Realtime;

namespace MingSim.Simulation.Realtime;

/// <summary>不可变 ReadModel：UI 可以读取，但拿不到任何 WorldState 或领域写入口。</summary>
public sealed record RealtimeReadModel(
    WorldId WorldId,
    int TurnNumber,
    GameTime GameTime,
    bool IsPaused,
    long WorldVersion,
    string CommitId,
    IReadOnlyList<ArmyReadModel> Armies,
    IReadOnlyList<MovementReadModel> Movements,
    IReadOnlyList<StockpileReadModel> Stockpiles,
    IReadOnlyList<ShipmentReadModel> Shipments,
    IReadOnlyList<ScheduledActionReadModel> ScheduledActions,
    IReadOnlyList<CommandOutcome> CommandOutcomes,
    ScenarioReadModel Scenario,
    ReadinessReadModel Readiness,
    IReadOnlyList<DecreeReadModel> Decrees,
    int OutboxEventCount,
    string StateHash)
{
    internal static RealtimeReadModel From(
        WorldState state,
        IEnumerable<ScheduledSimulationEvent> scheduled,
        IEnumerable<CommandOutcome> outcomes,
        int outboxEventCount,
        bool isPaused,
        string hash) =>
        new(
            state.Id,
            state.TurnNumber,
            state.GameTime,
            isPaused,
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
                .Select(item => new ShipmentReadModel(item.Id, item.RouteId, item.Status, item.GrainQuantity, item.DeliveredGrain, item.LossGrain, item.Escort, item.RaidLossGrain))
                .ToArray()),
            new ReadOnlyCollection<ScheduledActionReadModel>(scheduled.OrderBy(item => item.DueGameTime).ThenBy(item => item.Phase).ThenBy(item => item.Priority).ThenBy(item => item.CreationSequence)
                .Select(item => new ScheduledActionReadModel(item.EventId, item.DueGameTime, item.Phase, item.Priority, item.CreationSequence, item.EventType))
                .ToArray()),
            new ReadOnlyCollection<CommandOutcome>(outcomes.OrderBy(item => item.IngressSequence).ToArray()),
            new ScenarioReadModel(
                state.Scenario.LocalBurden,
                state.Scenario.MinisterTrust,
                state.Scenario.DailyGrainDemand,
                state.Scenario.FrontStockpileId,
                state.Scenario.SecondHalfFromDay,
                state.Scenario.BurdenCooperationThreshold,
                state.Scenario.ScenarioSilverBudget,
                state.Scenario.SpentSilver,
                state.Scenario.IsScenarioActive),
            new ReadinessReadModel(
                state.Readiness.ValueBasisPoints,
                state.Readiness.Value,
                state.Readiness.ArrearsGrain,
                state.Readiness.ConsecutiveZeroGrainDays),
            new ReadOnlyCollection<DecreeReadModel>(state.Decrees.Values
                .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
                .Select(item => new DecreeReadModel(
                    item.Id, item.IssuerId, item.Goal, item.RegionScope, item.Budget,
                    item.ResponsibleActorId, item.Deadline, item.Restrictions, item.Remarks,
                    item.RequiredCapability, item.RequiredResourceId, item.LinkedShipmentId, item.Status))
                .ToArray()),
            outboxEventCount,
            hash);
}

public sealed record ArmyReadModel(ArmyId Id, string Name, ProvinceId LocationId, long Auxiliaries, long LineInfantry, int TrainingDays);

public sealed record MovementReadModel(string ActionId, ArmyId ArmyId, ProvinceId Origin, ProvinceId Destination, GameTime DueGameTime);

public sealed record StockpileReadModel(StockpileId Id, ProvinceId LocationId, long Capacity, long GrainQuantity);

public sealed record ShipmentReadModel(ShipmentId Id, RouteId RouteId, ShipmentStatus Status, long GrainQuantity, long DeliveredGrain, long LossGrain, bool Escort, long RaidLossGrain);

public sealed record ScheduledActionReadModel(string EventId, GameTime DueGameTime, int Phase, int Priority, long CreationSequence, string EventType);

/// <summary>场景级只读视图（地方负担/大臣信任/场景规则参数）。</summary>
public sealed record ScenarioReadModel(
    int LocalBurden,
    int MinisterTrust,
    int DailyGrainDemand,
    StockpileId? FrontStockpileId,
    int SecondHalfFromDay,
    int BurdenCooperationThreshold,
    long ScenarioSilverBudget,
    long SpentSilver,
    bool IsScenarioActive);

/// <summary>前线战备只读视图。</summary>
public sealed record ReadinessReadModel(
    int ValueBasisPoints,
    int Value,
    long ArrearsGrain,
    int ConsecutiveZeroGrainDays);

/// <summary>政令只读视图。</summary>
public sealed record DecreeReadModel(
    DecreeId Id,
    CharacterId IssuerId,
    string Goal,
    ProvinceId RegionScope,
    long Budget,
    CharacterId ResponsibleActorId,
    GameTime Deadline,
    string Restrictions,
    string Remarks,
    GameCapability RequiredCapability,
    string? RequiredResourceId,
    string? LinkedShipmentId,
    DecreeStatus Status);
