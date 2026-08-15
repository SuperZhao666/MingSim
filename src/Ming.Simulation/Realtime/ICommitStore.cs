using MingSim.Domain;
using MingSim.Domain.Events;

namespace MingSim.Simulation.Realtime;

/// <summary>一次权威提交要落盘的最小包：完整快照 + 本次事件增量 + 本次输入终态增量。</summary>
/// <remarks>
/// Snapshot 由单写者在提交点原子捕获，含 WorldVersion/CommitId/状态哈希/待处理命令指纹；
/// JournalEvents 只放本次提交新产生的事件，InputOutcomes 只放本次新增/更新的输入终态；
/// 三者必须由适配器在同一个 durable transaction 内提交。
/// </remarks>
public sealed record CommitPackage(
    RealtimeSnapshot Snapshot,
    IReadOnlyList<DomainEvent> JournalEvents,
    IReadOnlyList<InputOutcome> InputOutcomes);

/// <summary>提交结果回执：成功时 WorldVersion 即已持久化版本；失败时 Error 给出原因。</summary>
public sealed record CommitReceipt(bool Success, long WorldVersion, string? Error);

/// <summary>未改变世界的拒绝/过期结果，也要原子持久化，保证重试得到同一结论。</summary>
public sealed record InputOutcome(
    string CommandId,
    string OutcomeCode,
    string Message,
    long WorldVersion);

/// <summary>恢复加载结果：快照与事件日志一起交给运行时做校验与确定性重放。</summary>
public sealed record LoadedWorld(
    RealtimeSnapshot Snapshot,
    IReadOnlyList<DomainEvent> JournalEvents,
    bool IsReadOnlySalvage = false,
    string? RecoveryMessage = null);

/// <summary>
/// Simulation 定义的唯一持久化写端口：只表达"原子提交/记录结果/恢复"，不出现 SQL 类型。
/// Persistence 适配器（内存或 SQLite）实现它；Simulation 单写者掌握提交时机。
/// </summary>
public interface ICommitStore
{
    /// <summary>原子写入权威变化（快照 + 事件日志增量），并递增 WorldVersion。</summary>
    CommitReceipt CommitWorld(CommitPackage package);

    /// <summary>加载最后一个完整提交；没有任何提交时返回 null。</summary>
    LoadedWorld? LoadCommittedWorld();
}
