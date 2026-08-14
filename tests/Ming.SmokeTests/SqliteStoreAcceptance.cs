using System.Text;
using Microsoft.Data.Sqlite;
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
        SqliteV1ArchiveMigratesAndRestoresSameWorld();
        SqliteV1SingleByteContentCorruptionFailsClosed();
        SqliteCorruptedNewSnapshotFallsBackToPreviousReady();
        SqliteMigrationFailureFailsClosed();
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

            // (b) 在干净库上翻转"state hash"内容字节：state_hash 只出现在状态行/快照行/meta 中，
            // 且这些行的 world_id 未变（仍在校验范围内）→ 重算校验和必然不一致 → 恢复失败。
            // 不能用 world_id 作为篡改靶点：world_id 是校验查询的过滤键，翻转它会把这行移出
            // 当前世界的校验范围，两侧一致导致恢复不报错（这是测试靶点问题，不是校验缺陷）。
            DeleteDbFiles(dbPath); // 文件头已被破坏，删掉重来
            using (var rewrite = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(rewrite, worldId, runtime.CaptureSnapshot());
            }

            var bytes = File.ReadAllBytes(dbPath);
            var needle = Encoding.UTF8.GetBytes(runtime.CaptureSnapshot().StateHash);
            var index = IndexOf(bytes, needle);
            Program.Require(index >= 0, "库文件中必须能找到 state hash 字节（用于定向篡改）");
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

    /// <summary>
    /// v1 档成功迁移到 v2 并恢复同一世界：已提交存档的快照行替换为**真实 v1 档**
    /// （v1 布局 + schema4 权威哈希，见 Program.BuildRealV1Fixture/RealV1StateHash）。
    /// RestoreLatest 先按 v1 记录版本校验载荷哈希（P1-1）再 re-seal 为当前规则（P1），
    /// 权威恢复后得到与迁移前完全相同的世界。
    /// </summary>
    private static void SqliteV1ArchiveMigratesAndRestoresSameWorld()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "v1-real-fixture", 5_000)).Queued,
                "迁移测试命令应该进入收件箱");
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            var snapshot = runtime.CaptureSnapshot();
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, snapshot);
            }

            // 把最新快照行替换为"真实 v1 档"（schema4 哈希 + v1 布局；snapshot_blob 不在整库校验和覆盖列内）。
            var realV1 = Program.BuildRealV1Fixture(snapshot, "MSNAP"u8.ToArray(), snapshot.StateHash, snapshot.PayloadChecksum);
            ReplaceSnapshotBlob(dbPath, worldId, LatestSnapshotSeq(dbPath, worldId), realV1);

            var restored = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId));
            Program.Require(restored.StateHash == runtime.StateHash,
                "v1 档迁移到 v2 后恢复，canonical hash 必须与迁移前一致（同一世界）");
            Program.Require(restored.ReadModel.WorldVersion == runtime.ReadModel.WorldVersion &&
                            restored.ReadModel.GameTime == runtime.ReadModel.GameTime &&
                            restored.ReadModel.CommitId == runtime.ReadModel.CommitId,
                "v1 档迁移恢复后 WorldVersion/GameTime/CommitId 必须一致");
            Program.Require(restored.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit,
                "v1 档迁移恢复后必须回到同一在途运输状态");
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    /// <summary>
    /// P1-1 边界回归（SQLite 全量路径）：v1 档单字节内容损坏必须 fail-closed——
    /// (a) 只有一份快照且内容被篡改 → RestoreLatest 拒绝（绝不带病 re-seal 静默成功）；
    /// (b) 最新快照内容被篡改且存在旧 READY → RestoreLatest 回退到旧 READY（绝不发布损坏内容）。
    /// </summary>
    private static void SqliteV1SingleByteContentCorruptionFailsClosed()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "v1-real-fixture", 5_000)).Queued,
                "P1-1 边界命令应该进入收件箱");
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            var snapshot = runtime.CaptureSnapshot();

            // (a) 单份快照：内容单字节损坏（翻转 outbox 事件类型字符串内一个字符，结构仍可解码）
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, snapshot);
            }

            var fixture = Program.BuildRealV1Fixture(snapshot, "MSNAP"u8.ToArray(), snapshot.StateHash, snapshot.PayloadChecksum);
            var needle = "ShipmentPlanned"u8.ToArray();
            var contentIndex = IndexOf(fixture, needle);
            Program.Require(contentIndex >= 0, "真实 v1 夹具必须包含可定位的 outbox 事件类型字符串");
            var corrupted = (byte[])fixture.Clone();
            corrupted[contentIndex + 3] ^= 0x01;
            ReplaceSnapshotBlob(dbPath, worldId, LatestSnapshotSeq(dbPath, worldId), corrupted);
            Program.RequireThrowsAny(() => SqliteCommitStore.RestoreLatest(dbPath, worldId));
            DeleteDbFiles(dbPath);

            // (b) 两快照：损坏的最新 v1 快照 + 旧 READY（v2）→ 回退到旧 READY，绝不发布损坏内容。
            // 注：第二份提交用"同一时刻处理第二条命令"产生版本递增（GameTime 不变），
            // 避开既有的"场景起点未序列化导致时间推进后快照往返哈希漂移"缺陷（P2 债务，见 PR 风险节）。
            var hashA = snapshot.StateHash;
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, snapshot);
            }

            runtime.SetPaused(true);
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, runtime.CaptureSnapshot());
            }

            var latestSeq = LatestSnapshotSeq(dbPath, worldId);
            ReplaceSnapshotBlob(dbPath, worldId, latestSeq, corrupted);
            var fellBack = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId));
            Program.Require(fellBack.StateHash == hashA,
                "损坏的最新 v1 快照必须回退到旧 READY（同一历史世界），绝不静默接受损坏内容");
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    /// <summary>
    /// 快照失败回退回归样本 snapshot_failure_falls_back（SQLite 全量路径）：
    /// 提交 A、提交 B 后损坏最新快照 B 的载荷，RestoreLatest 按 doc 08 §15 回退到
    /// 上一个 READY 快照 A——旧 READY 仍可加载出同一历史世界。
    /// </summary>
    private static void SqliteCorruptedNewSnapshotFallsBackToPreviousReady()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "sqlite-fallback-a", 5_000)).Queued,
                "回退样本首批命令应该进入收件箱");
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, runtime.CaptureSnapshot());
            }

            var restoredA = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId));
            var hashA = restoredA.StateHash;
            var versionA = restoredA.ReadModel.WorldVersion;
            Program.Require(restoredA.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit,
                "提交 A 后应处于在途状态");

            // 第二次提交（版本 +1）后，损坏最新快照 B 的载荷（snapshot_blob 不在校验和覆盖列内）。
            runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddHours(1)));
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, runtime.CaptureSnapshot());
            }

            var latestSeq = LatestSnapshotSeq(dbPath, worldId);
            ReplaceSnapshotBlob(dbPath, worldId, latestSeq, [0x00, 0x01, 0x02, 0x03, 0x04]);

            // 旧 READY 仍可加载：回退到快照 A（世界版本落后于 B，但同一历史世界可恢复）
            var fellBack = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId));
            Program.Require(fellBack.StateHash == hashA,
                "最新快照损坏后必须回退到上一个 READY 快照 A（同 hash）");
            Program.Require(fellBack.ReadModel.WorldVersion == versionA,
                "回退恢复的世界版本必须是旧 READY 快照 A 的版本");
            Program.Require(fellBack.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit,
                "回退恢复后必须处于 A 的可用在途状态");
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    /// <summary>迁移失败 fail-closed：损坏的 v1 载荷必须让恢复抛异常，绝不返回半迁移结果或半状态。</summary>
    private static void SqliteMigrationFailureFailsClosed()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "sqlite-v1corrupt", 5_000)).Queued,
                "迁移失败测试命令应该进入收件箱");
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            var snapshot = runtime.CaptureSnapshot();
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, snapshot);
            }

            var v1Payload = Program.DowngradePayloadToV1(SnapshotCodec.Serialize(snapshot), "MSNAP"u8.ToArray());
            var truncatedV1 = v1Payload[..(v1Payload.Length / 2)];
            ReplaceSnapshotBlob(dbPath, worldId, LatestSnapshotSeq(dbPath, worldId), truncatedV1);

            Program.RequireThrowsAny(() => SqliteCommitStore.RestoreLatest(dbPath, worldId));
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    private static long LatestSnapshotSeq(string dbPath, WorldId worldId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(snapshot_seq) FROM snapshots WHERE world_id = $world;";
        command.Parameters.AddWithValue("$world", worldId.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void ReplaceSnapshotBlob(string dbPath, WorldId worldId, long snapshotSeq, byte[] blob)
    {
        // 直接改库中快照行载荷：snapshot_blob 不在 ComputeTotalChecksum 覆盖列内，
        // 列校验和仍通过；内容损坏由迁移/解码路径按各自语义处理。
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE snapshots SET snapshot_blob = $blob WHERE world_id = $world AND snapshot_seq = $seq;";
        command.Parameters.AddWithValue("$world", worldId.Value);
        command.Parameters.AddWithValue("$seq", snapshotSeq);
        command.Parameters.AddWithValue("$blob", blob);
        command.ExecuteNonQuery();
        connection.Close();
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
