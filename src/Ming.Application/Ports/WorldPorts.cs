using MingSim.Domain;
using MingSim.Domain.Common;
using MingSim.Domain.Events;

namespace MingSim.Application.Ports;

/// <summary>世界状态的读取与提交边界。</summary>
/// <remarks>
/// 模拟内核不直接知道状态来自内存、SQLite 还是云端；它只通过这个端口读写。
/// 这就是六边形架构里“应用层依赖抽象，而不是依赖具体数据库”的一个简单例子。
/// </remarks>
public interface IWorldStore
{
    WorldState Load(WorldId worldId);

    void Commit(WorldState newState);
}

/// <summary>只追加的审计日志。</summary>
public interface IAuditJournal
{
    void Append(WorldId worldId, IReadOnlyList<DomainEvent> events);

    IReadOnlyList<DomainEvent> Read(WorldId worldId);
}

/// <summary>快照准备与当前指针切换的边界。</summary>
public interface ISnapshotStore
{
    SnapshotPreparation Prepare(WorldState state, IReadOnlyList<DomainEvent> events);

    void Promote(SnapshotPreparation preparation);
}

/// <summary>尚未成为“当前快照”的候选快照。</summary>
public sealed record SnapshotPreparation(
    WorldId WorldId,
    int TurnNumber,
    string StateHash,
    bool IsValid,
    WorldState State,
    IReadOnlyList<DomainEvent> Events);
