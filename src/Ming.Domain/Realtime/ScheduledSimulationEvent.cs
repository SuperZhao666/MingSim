using System.Collections.ObjectModel;

namespace MingSim.Domain.Realtime;

/// <summary>调度器中的不可变事件；排序键完整包含四个稳定字段。</summary>
public sealed record ScheduledSimulationEvent
{
    public ScheduledSimulationEvent(
        string eventId,
        GameTime dueGameTime,
        int phase,
        int priority,
        long creationSequence,
        string eventType,
        IReadOnlyDictionary<string, string> data,
        string? causalCommandId = null,
        int schemaVersion = 1)
    {
        EventId = eventId;
        DueGameTime = dueGameTime;
        Phase = phase;
        Priority = priority;
        CreationSequence = creationSequence;
        EventType = eventType;
        CausalCommandId = causalCommandId;
        SchemaVersion = schemaVersion;
        Data = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(data, StringComparer.Ordinal));
    }

    public string EventId { get; }

    public GameTime DueGameTime { get; }

    public int Phase { get; }

    public int Priority { get; }

    public long CreationSequence { get; }

    public string EventType { get; }

    public string? CausalCommandId { get; }

    public int SchemaVersion { get; }

    public IReadOnlyDictionary<string, string> Data { get; }

    /// <summary>旧原型读取时间的兼容别名；权威排序使用 DueGameTime。</summary>
    public DateTimeOffset DueAt => DueGameTime.Value;
}
