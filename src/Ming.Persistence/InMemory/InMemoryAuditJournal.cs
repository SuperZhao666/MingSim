using MingSim.Application.Ports;
using MingSim.Domain.Common;
using MingSim.Domain.Events;

namespace MingSim.Persistence.InMemory;

/// <summary>只追加审计日志的最小内存实现。</summary>
public sealed class InMemoryAuditJournal : IAuditJournal
{
    private readonly List<DomainEvent> _events = [];
    private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);

    public void Append(WorldId worldId, IReadOnlyList<DomainEvent> events)
    {
        foreach (var domainEvent in events)
        {
            if (domainEvent.WorldId != worldId)
            {
                throw new InvalidOperationException("审计事件所属世界与目标世界不一致。");
            }

            if (!_eventIds.Add(domainEvent.EventId))
            {
                throw new InvalidOperationException($"重复的 DomainEvent EventId：{domainEvent.EventId}。");
            }

            _events.Add(domainEvent);
        }
    }

    public IReadOnlyList<DomainEvent> Read(WorldId worldId) =>
        _events.Where(domainEvent => domainEvent.WorldId == worldId).ToArray();
}
