using MingSim.Domain;
using MingSim.Domain.Errors;
using MingSim.Domain.Events;

namespace MingSim.Simulation;

/// <summary>一次回合结算的结果。</summary>
public sealed class TurnResolution
{
    private TurnResolution(
        bool committed,
        WorldState state,
        IReadOnlyList<DomainEvent> events,
        IReadOnlyList<SimulationError> errors)
    {
        Committed = committed;
        State = state;
        Events = events;
        Errors = errors;
    }

    /// <summary>只有为 true 时，State 才可以成为正式世界状态。</summary>
    public bool Committed { get; }

    /// <summary>成功时是新状态，失败时是原状态的副本。</summary>
    public WorldState State { get; }

    public IReadOnlyList<DomainEvent> Events { get; }

    public IReadOnlyList<SimulationError> Errors { get; }

    public static TurnResolution Commit(
        WorldState state,
        IReadOnlyList<DomainEvent> events) =>
        new(true, state, events, []);

    public static TurnResolution Reject(
        WorldState originalState,
        IReadOnlyList<SimulationError> errors) =>
        new(false, originalState.Clone(), [], errors);
}
