using System.Text;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Realtime;
using MingSim.Persistence.Sqlite;
using MingSim.Simulation.Realtime;

namespace MingSim.SmokeTests;

/// <summary>
/// SQLite 单事务提交/恢复/篡改验收（仅在 MingSimEnableSqliteStore=true 时编译，CI 联网执行）。
/// 覆盖任务红线：单事务原子性、崩溃恢复一致性、任一字节篡改即失败且不发布半状态、
/// 恢复后继续推进与未重启实例确定性一致、重复启动/重复恢复无副作用。
/// </summary>
internal static class SqliteStoreAcceptance
{
    public static void RunAll()
    {
        SqliteCommitPersistsStateJournalAndSnapshotAtomically();
        SqliteRestoreContinuesDeterministically();
        SqliteUncommittedBatchHasNoEffect();
        SqliteTamperedFileFailsRestore();
        SqliteRestoreIsIdempotent();
        SqliteRepeatedCommitIsIdempotent();
        SqliteVersionRegressionIsRejected();
    }

    private static void SqliteCommitPersistsStateJournalAndSnapshotAtomically()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "sqlite-5000", 5_000)).Queued,
                "SQLite 5000 石命令应该进入收件箱");
            var accepted = runtime.AdvanceTo(runtime.ReadModel.GameTime);
            Program.Require(accepted.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit, "提交前必须先进入在途");
            var snapshot = runtime.CaptureSnapshot();

            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, snapshot);

                // 端口读回：当前状态 / 事件日志 / 当前快照 三个面都必须可读
                var loaded = store.Load(worldId);
                Program.Require(loaded.WorldVersion == runtime.ReadModel.WorldVersion &&
                                loaded.GameTime == runtime.ReadModel.GameTime &&
                                loaded.CommitId == runtime.ReadModel.CommitId,
                    "Load 必须返回与提交一致的状态面");
                Program.Require(loaded.Logistics.Stockpiles[new StockpileId("capital-granary")].GrainQuantity == 15_000,
                    "Load 返回的状态必须包含已扣 5000 石的起点库存");
                var journal = store.Read(worldId);
                Program.Require(journal.Count == runtime.OutboxEvents.Count, "事件日志必须完整持久化全部 outbox 事件");
                Program.Require(store.Current is not null && store.Current.StateHash.Length == 64,
                    "校验通过的快照必须提升为当前快照");
            }

            // 模拟崩溃：旧实例被丢弃，从 SQLite 重新恢复
            var restoredSnapshot = SqliteCommitStore.RestoreLatest(dbPath, worldId);
            var restored = RealtimeSimulationRuntime.Restore(restoredSnapshot);
            Program.Require(restored.StateHash == runtime.StateHash, "恢复后 canonical hash 必须与提交时一致");
            Program.Require(restored.ReadModel.WorldVersion == runtime.ReadModel.WorldVersion &&
                            restored.ReadModel.GameTime == runtime.ReadModel.GameTime,
                "恢复后权威时间与 WorldVersion 必须与提交时一致");
            Program.Require(restored.ReadModel.Stockpiles.Single(item => item.Id == new StockpileId("ningyuan-granary")).GrainQuantity == 0 &&
                            restored.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit,
                "恢复后必须回到在途状态而不是发布半状态");
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    private static void SqliteRestoreContinuesDeterministically()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "sqlite-continue", 5_000)).Queued,
                "确定性推进命令应该进入收件箱");
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            var snapshot = runtime.CaptureSnapshot();
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, snapshot);
            }

            var restored = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId));
            var target = new GameTime(runtime.ReadModel.GameTime.Value.AddDays(12));
            var original = runtime.AdvanceTo(target);
            var replayed = restored.AdvanceTo(target);
            Program.Require(original.ReadModel.StateHash == replayed.ReadModel.StateHash,
                "SQLite 恢复实例推进到同一目标必须与原实例 canonical hash 一致");
            Program.Require(original.ReadModel.WorldVersion == replayed.ReadModel.WorldVersion &&
                            original.ReadModel.GameTime == replayed.ReadModel.GameTime,
                "SQLite 恢复实例推进后权威时间/WorldVersion 必须一致");
            Program.Require(Program.EventFingerprints(original.Events).SequenceEqual(Program.EventFingerprints(replayed.Events)),
                "SQLite 恢复实例推进后事件流必须一致");
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    private static void SqliteUncommittedBatchHasNoEffect()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "sqlite-batch-a", 5_000)).Queued,
                "第一笔命令应该进入收件箱");
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            var firstSnapshot = runtime.CaptureSnapshot();
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, firstSnapshot);
            }

            var firstHash = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId)).StateHash;

            // 第二次提交只暂存不 CommitAll（模拟提交中断/崩溃），库必须保持第一次提交
            runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddHours(1)));
            var secondSnapshot = runtime.CaptureSnapshot();
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                var state = Program.GetSnapshotState(secondSnapshot);
                var events = Program.GetSnapshotOutbox(secondSnapshot);
                store.Commit(state);
                store.Append(worldId, events);
                store.Promote(store.Prepare(state, events));
                // 故意不调用 CommitAll
            }

            var afterHash = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId)).StateHash;
            Program.Require(afterHash == firstHash, "未 CommitAll 的批次不得产生任何持久化效果（全有或全无）");
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    private static void SqliteTamperedFileFailsRestore()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "sqlite-tamper", 5_000)).Queued,
                "篡改测试命令应该进入收件箱");
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, runtime.CaptureSnapshot());
            }

            // (a) 翻转 SQLite 文件头：打开即失败，恢复抛异常且不返回快照
            var header = File.ReadAllBytes(dbPath);
            header[0] ^= 0xFF;
            File.WriteAllBytes(dbPath, header);
            Program.RequireThrowsAny(() => SqliteCommitStore.RestoreLatest(dbPath, worldId));

            // (b) 在干净库上翻转"世界编号"内容字节：校验和/世界号校验失败
            DeleteDbFiles(dbPath); // 文件头已被破坏，删掉重来
            using (var rewrite = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(rewrite, worldId, runtime.CaptureSnapshot());
            }

            var bytes = File.ReadAllBytes(dbPath);
            var needle = Encoding.UTF8.GetBytes(worldId.Value);
            var index = IndexOf(bytes, needle);
            Program.Require(index >= 0, "库文件中必须能找到世界编号字节（用于定向篡改）");
            bytes[index] ^= 0x01;
            File.WriteAllBytes(dbPath, bytes);
            Program.RequireThrowsAny(() => SqliteCommitStore.RestoreLatest(dbPath, worldId));
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    private static void SqliteRestoreIsIdempotent()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "sqlite-idem", 5_000)).Queued,
                "幂等测试命令应该进入收件箱");
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, runtime.CaptureSnapshot());
            }

            var beforeBytes = File.ReadAllBytes(dbPath);
            var first = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId));
            var second = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId));
            Program.Require(first.StateHash == second.StateHash, "重复启动/恢复必须得到同一快照");
            var afterBytes = File.ReadAllBytes(dbPath);
            Program.Require(beforeBytes.SequenceEqual(afterBytes), "恢复必须只读，不得修改库文件（无副作用）");
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    private static void SqliteRepeatedCommitIsIdempotent()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "sqlite-recommit", 5_000)).Queued,
                "重复提交测试命令应该进入收件箱");
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            var snapshot = runtime.CaptureSnapshot();
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, snapshot);
                var journalCount = store.Read(worldId).Count;
                StageAndCommit(store, worldId, snapshot); // 同一版本同一载荷 → 幂等 no-op
                Program.Require(store.Read(worldId).Count == journalCount, "重复提交同一快照不得重复追加事件日志");
                var firstHash = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId)).StateHash;
                var secondHash = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId)).StateHash;
                Program.Require(firstHash == secondHash, "重复提交后恢复结果必须保持一致");
            }
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    private static void SqliteVersionRegressionIsRejected()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "sqlite-version", 5_000)).Queued,
                "版本测试命令应该进入收件箱");
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            var firstSnapshot = runtime.CaptureSnapshot();
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, firstSnapshot);
            }

            var committedVersion = runtime.ReadModel.WorldVersion;
            runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddHours(1)));
            var secondSnapshot = runtime.CaptureSnapshot();
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, secondSnapshot);
                // 尝试重新提交旧版本（版本回退）→ 事务内拒绝并整体回滚
                var oldState = Program.GetSnapshotState(firstSnapshot);
                var oldEvents = Program.GetSnapshotOutbox(firstSnapshot);
                store.Commit(oldState);
                store.Append(worldId, oldEvents);
                store.Promote(store.Prepare(oldState, oldEvents));
                Program.RequireThrows<InvalidOperationException>(() => store.CommitAll(firstSnapshot));

                var loaded = store.Load(worldId);
                Program.Require(loaded.WorldVersion == committedVersion + 1,
                    "版本回退被拒绝后库必须保持最新提交");
            }
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    private static void StageAndCommit(SqliteCommitStore store, WorldId worldId, RealtimeSnapshot snapshot)
    {
        var state = Program.GetSnapshotState(snapshot);
        var events = Program.GetSnapshotOutbox(snapshot);
        store.Commit(state);
        store.Append(worldId, events);
        var preparation = store.Prepare(state, events);
        Program.Require(preparation.IsValid, "SQLite Prepare 必须校验通过");
        store.Promote(preparation);
        store.CommitAll(snapshot);
    }

    private static string CreateDbPath() =>
        Path.Combine(Path.GetTempPath(), $"mingsim-g2-{Guid.NewGuid():N}.db");

    private static void DeleteDbFiles(string dbPath)
    {
        // 清理前清空连接池：即使某个连接串漏写 Pooling=false，池中句柄也会阻止删除，
        // ClearAllPools 强制关闭全部池化连接，保证文件句柄释放后再删文件。
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = dbPath + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[start + offset] != needle[offset])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return start;
            }
        }

        return -1;
    }
}
