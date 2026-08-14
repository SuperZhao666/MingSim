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
    private readonly Dictionary<long, List<DomainEvent>> _journal = new();
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
            existing.StateHash == package.Snapshot.StateHash)
        {
            // 幂等重提交：同一版本同一状态哈希，什么都不写。
            return new CommitReceipt(true, version, null);
        }

        _snapshots[version] = package.Snapshot;
        _journal[version] = package.JournalEvents.ToList();
        _latestVersion = version;
        return new CommitReceipt(true, version, null);
    }

    public CommitReceipt RecordOutcome(InputOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _outcomes[outcome.CommandId] = outcome;
        return new CommitReceipt(true, outcome.WorldVersion, null);
    }

    public LoadedWorld? LoadCommittedWorld()
    {
        if (_latestVersion < 0 || !_snapshots.TryGetValue(_latestVersion, out var snapshot))
        {
            return null;
        }

        return new LoadedWorld(
            snapshot,
            new ReadOnlyCollection<DomainEvent>(_journal[_latestVersion]));
    }

    /// <summary>已持久化的拒绝/过期结果，供测试与诊断读取。</summary>
    public IReadOnlyDictionary<string, InputOutcome> Outcomes =>
        new ReadOnlyDictionary<string, InputOutcome>(_outcomes);
}
