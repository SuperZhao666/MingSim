using MingSim.Application.Ports;
using MingSim.Domain;
using MingSim.Domain.Common;
using MingSim.Domain.Events;
using MingSim.Domain.Realtime;

namespace MingSim.Persistence.InMemory;

/// <summary>
/// 内存版快照适配器：保存 Runtime 生成的完整实时快照并在提升前校验 canonical hash。
/// </summary>
public sealed class InMemorySnapshotStore : ISnapshotStore
{
    private SnapshotPreparation? _current;

    public SnapshotPreparation? Current => _current;

    public SnapshotPreparation Prepare(WorldState state, IReadOnlyList<DomainEvent> events)
    {
        var snapshotState = state.Clone();
        var stateHash = CanonicalStateHasher.Compute(
            snapshotState,
            [],
            0,
            0,
            [],
            "schema=1;streams=none",
            events,
            0m,
            state.GameTime,
            state.WorldVersion,
            0,
            false,
            1.0,
            []);
        var valid = stateHash.Length == 64 && events.All(domainEvent => domainEvent.WorldId == state.Id);

        return new SnapshotPreparation(
            state.Id,
            state.TurnNumber,
            stateHash,
            valid,
            snapshotState,
            events.ToArray());
    }

    public void Promote(SnapshotPreparation preparation)
    {
        if (!preparation.IsValid)
        {
            throw new InvalidOperationException("不能提升一个未通过校验的快照。");
        }

        _current = preparation;
    }
}
