using System.Collections.ObjectModel;
using MingSim.Domain.Events;
using MingSim.Persistence.Sqlite;
using MingSim.Simulation.Realtime;

namespace MingSim.Persistence.InMemory;

/// <summary>
/// 测试与无存档场景用的内存提交商店：行为与 SQLite 适配器一致——
/// 按 WorldVersion 保存完整快照与事件日志增量，拒绝结果单独保存，恢复时校验版本连续性。
/// </summary>
public sealed class InMemoryCommitStore : ICommitStore
{
    private readonly Dictionary<long, RealtimeSnapshot> _snapshots = new();
    private readonly List<DomainEvent> _journal = [];
    private readonly Dictionary<string, InputOutcome> _outcomes = new(StringComparer.Ordinal);
    private long _latestVersion = -1;

    public CommitReceipt CommitWorld(CommitPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var version = SnapshotReflection.GetState(package.Snapshot).WorldVersion;
        if (version < _latestVersion)
        {
            return new CommitReceipt(false, version, $"提交版本回退：库中当前版本 {_latestVersion}。");
        }

        if (version == _latestVersion &&
            _snapshots.TryGetValue(version, out var existing) &&
            existing.StateHash == package.Snapshot.StateHash &&
            existing.PayloadChecksum == package.Snapshot.PayloadChecksum)
        {
            // 幂等重提交：同一版本同一完整快照，什么都不写。
            return new CommitReceipt(true, version, null);
        }

        // 先完整校验，再一次性发布到内存结构；不能边验证边 Append，
        // 否则增量中后半段有缺号时会留下“日志写了一半、快照没写”的半提交。
        var fullOutbox = SnapshotReflection.GetOutboxEvents(package.Snapshot);
        if (fullOutbox.Count != _journal.Count + package.JournalEvents.Count)
        {
            return new CommitReceipt(false, version,
                $"快照 outbox 数量 {fullOutbox.Count} 与已提交 journal {_journal.Count} + 增量 {package.JournalEvents.Count} 不一致。");
        }

        for (var index = 0; index < _journal.Count; index++)
        {
            if (!SnapshotCodec.SerializeEvent(_journal[index]).AsSpan()
                    .SequenceEqual(SnapshotCodec.SerializeEvent(fullOutbox[index])))
            {
                return new CommitReceipt(false, version, $"快照 outbox 与 journal 前缀在序号 {index} 处不一致。");
            }
        }

        var expected = _journal.Count == 0 ? 0L : _journal[^1].EventSequence + 1;
        foreach (var domainEvent in package.JournalEvents)
        {
            if (domainEvent.EventSequence != expected)
            {
                return new CommitReceipt(false, version,
                    $"事件日志增量不连续：期望 {expected}，实际 {domainEvent.EventSequence}。");
            }

            var fullEvent = fullOutbox[(int)expected];
            if (!SnapshotCodec.SerializeEvent(domainEvent).AsSpan()
                    .SequenceEqual(SnapshotCodec.SerializeEvent(fullEvent)))
            {
                return new CommitReceipt(false, version,
                    $"事件日志增量不是快照 outbox 的精确后缀（序号 {expected}）。");
            }
            expected++;
        }

        _journal.AddRange(package.JournalEvents);
        foreach (var outcome in package.InputOutcomes)
        {
            _outcomes[outcome.CommandId] = outcome;
        }

        _snapshots[version] = package.Snapshot;
        _latestVersion = version;
        return new CommitReceipt(true, version, null);
    }

    public LoadedWorld? LoadCommittedWorld()
    {
        if (_latestVersion < 0 || !_snapshots.TryGetValue(_latestVersion, out var snapshot))
        {
            return null;
        }

        return new LoadedWorld(
            snapshot,
            new ReadOnlyCollection<DomainEvent>(_journal.ToArray()));
    }

    /// <summary>已持久化的拒绝/过期结果，供测试与诊断读取。</summary>
    public IReadOnlyDictionary<string, InputOutcome> Outcomes =>
        new ReadOnlyDictionary<string, InputOutcome>(_outcomes);
}
