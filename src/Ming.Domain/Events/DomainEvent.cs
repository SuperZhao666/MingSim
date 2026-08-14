using System.Collections.ObjectModel;
using MingSim.Domain.Common;

namespace MingSim.Domain.Events;

/// <summary>
/// 已经发生、可以写入审计日志的不可变领域事件。
/// </summary>
public sealed record DomainEvent
{
    public DomainEvent(
        string eventId,
        WorldId worldId,
        int turnNumber,
        string eventType,
        string description,
        IReadOnlyDictionary<string, string> data,
        DateTimeOffset? occurredAt = null,
        long eventSequence = 0,
        long worldVersion = 0,
        string commitId = "unknown",
        string? causalCommandId = null)
    {
        EventId = eventId;
        WorldId = worldId;
        TurnNumber = turnNumber;
        EventType = eventType;
        Description = description;
        Data = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(data, StringComparer.Ordinal));
        OccurredAt = occurredAt;
        EventSequence = eventSequence;
        WorldVersion = worldVersion;
        CommitId = commitId;
        CausalCommandId = causalCommandId;
    }

    public string EventId { get; }

    public WorldId WorldId { get; }

    public int TurnNumber { get; }

    public string EventType { get; }

    public string Description { get; }

    public IReadOnlyDictionary<string, string> Data { get; }

    public DateTimeOffset? OccurredAt { get; }

    /// <summary>事件自己的单调序号；它与 CommandId 分离，避免业务事件复用命令身份。</summary>
    public long EventSequence { get; }

    public long WorldVersion { get; }

    public string CommitId { get; }

    public string? CausalCommandId { get; }
}
