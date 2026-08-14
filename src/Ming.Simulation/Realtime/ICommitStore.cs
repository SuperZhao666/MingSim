using MingSim.Domain;
using MingSim.Domain.Events;

namespace MingSim.Simulation.Realtime;

/// <summary>一次权威提交要落盘的最小包：完整快照 + 本次新产生的事件日志增量 + 可选的拒绝结果。</summary>
/// <remarks>
/// Snapshot 由单写者在提交点原子捕获，是本次提交的权威事实（含 WorldVersion/CommitId/状态哈希/
/// 待处理命令指纹/outbox）；JournalEvents 只放本次提交新产生的事件增量（delta append，禁止整本日志重复）。
/// Outcome 是"未改变世界的拒绝/过期结果"：与 snapshot/outbox 同一事务落盘，保证重试得到同一结论、
/// 且单点崩溃后 outcome 与快照要么都持久化要么都不持久化。实现方必须在同一个事务里
/// 写入 state/delta events/snapshot/outcome/meta，禁止拆成多个独立写。
/// </remarks>
public sealed record CommitPackage(
    RealtimeSnapshot Snapshot,
    IReadOnlyList<DomainEvent> JournalEvents,
    InputOutcome? Outcome = null);

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
    IReadOnlyList<DomainEvent> JournalEvents);

/// <summary>
/// Simulation 定义的唯一持久化写端口：只表达"原子提交/记录结果/恢复"，不出现 SQL 类型。
/// Persistence 适配器（内存或 SQLite）实现它；Simulation 单写者掌握提交时机。
/// </summary>
public interface ICommitStore
{
    /// <summary>原子写入权威变化（快照 + 事件日志增量），并递增 WorldVersion。</summary>
    CommitReceipt CommitWorld(CommitPackage package);

    /// <summary>原子记录一个未改变世界的拒绝/过期结果；WorldVersion 不变。</summary>
    CommitReceipt RecordOutcome(InputOutcome outcome);

    /// <summary>加载最后一个完整提交；没有任何提交时返回 null。</summary>
    LoadedWorld? LoadCommittedWorld();
}
