using MingSim.Domain.Common;

namespace MingSim.Domain.Events;

/// <summary>
/// 已经发生、可以写入审计日志的领域事件。
/// </summary>
/// <remarks>
/// 领域事件描述“事实”，例如“某工坊项目已经登记”，而不是描述“某个 AI 说它成功了”。
/// 奏报、时间线和回放都应该从这些事实生成。
/// </remarks>
public sealed record DomainEvent(
    string EventId,
    WorldId WorldId,
    int TurnNumber,
    string EventType,
    string Description,
    IReadOnlyDictionary<string, string> Data,
    DateTime? OccurredAt = null);
