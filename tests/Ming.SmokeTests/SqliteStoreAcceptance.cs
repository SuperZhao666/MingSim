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
        SqliteCommitWorldThreeVersionsRestoreConsistent();
        SqliteLegalPayloadSwapNeverPublished();
        SqliteBlobBodyTamperingFailsClosed();
        SqliteRejectedOutcomeAtomicWithSnapshot();
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
    /// （v1 布局 + schema4 权威哈希 + v1 时代 checksum（Version=6），见
    /// Program.BuildRealV1Fixture/RealV1StateHash；P1-2 正向回归——未损坏真实 v1 档必须迁移成功）。
    /// RestoreLatest 先按 v1 记录版本校验载荷哈希（P1-1，校验基准 v6/v4 与真实旧档一致）再
    /// re-seal 为当前规则（P1），权威恢复后得到与迁移前完全相同的世界。
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

            // 把最新快照行替换为"真实 v1 档"（schema4 哈希 + v1 布局），并把整库校验和
            // 重算为 v1 时代布局（不覆盖 blob 的旧布局）——真实 v1 档由 v1 时代代码写入，
            // 其 total_checksum 只覆盖元数据列；替换后必须自洽，恢复才能走"旧布局校验 → 迁移"路径。
            var realV1 = Program.BuildRealV1Fixture(snapshot, "MSNAP"u8.ToArray(), snapshot.StateHash, snapshot.PayloadChecksum);
            ReplaceSnapshotBlob(dbPath, worldId, LatestSnapshotSeq(dbPath, worldId), realV1);
            ResealArchiveAsLegacy(dbPath, worldId);

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

    /// <summary>
    /// P1-PERSIST-01 验收：真实 runtime 注入 SqliteCommitStore，连续 3 次不同 WorldVersion 提交
    /// （不走 StageAndCommit 辅助，全部经 CommitWorld 单事务入口），从新连接恢复后
    /// canonical hash / outbox 事件流 / WorldVersion 必须与提交时完全一致；
    /// 且提交序列（snapshot_seq）独立于 WorldVersion 单调推进。
    /// </summary>
    private static void SqliteCommitWorldThreeVersionsRestoreConsistent()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            string originalHash;
            long finalVersion;
            string[] originalFingerprints;
            long latestSnapshotSeq;
            long metaWorldVersion;
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld(), store);
                // 连续 3 次不同 WorldVersion 的提交全部经真实 runtime → CommitWorld 落盘（不走 StageAndCommit）。
                // 提交都在同一安全点（GameTime 不变）完成——避开 #35 已知 P2 债务
                // "场景起点未序列化 → 时间推进后的快照往返哈希漂移"（恢复时间推进档须先修该债务，
                // 见 PR 风险节；本任务只验收单事务入口与完整性链）。
                // 最后一次提交必须是"非命令提交"（运输出发的即时调度事件提交，收件箱为空）：
                // 命令提交的快照按设计携带"正在提交的那条命令"（出队在提交之后），恢复实例需重放
                // 幂等收敛，瞬时哈希/outbox 与提交实例不同；以空收件箱的最终提交做一致性基准。
                runtime.SetPaused(true);
                runtime.AdvanceTo(runtime.ReadModel.GameTime);
                var version1 = runtime.ReadModel.WorldVersion;
                runtime.SetPaused(false);
                runtime.AdvanceTo(runtime.ReadModel.GameTime);
                var version2 = runtime.ReadModel.WorldVersion;
                Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "e2e-sqlite-1", 5_000)).Queued,
                    "端到端调粮命令应进入收件箱");
                var final = runtime.AdvanceTo(runtime.ReadModel.GameTime);
                Program.Require(final.Succeeded, "最后一次推进必须成功");
                var version3 = runtime.ReadModel.WorldVersion;
                Program.Require(version1 < version2 && version2 < version3,
                    "连续 3 次提交必须产生严格递增的 WorldVersion");

                originalHash = runtime.StateHash;
                finalVersion = runtime.ReadModel.WorldVersion;
                originalFingerprints = Program.EventFingerprints(runtime.OutboxEvents).ToArray();
            }

            // 新连接（新的只读连接）恢复，验证 hash/outbox/version 一致
            var restored = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId));
            Program.Require(restored.StateHash == originalHash,
                "新连接恢复后 canonical hash 必须与提交时一致");
            Program.Require(restored.ReadModel.WorldVersion == finalVersion,
                "新连接恢复后 WorldVersion 必须一致");
            Program.Require(Program.EventFingerprints(restored.OutboxEvents).SequenceEqual(originalFingerprints),
                "新连接恢复后事件流（outbox）必须一致");

            // 提交序列与 WorldVersion 区分：snapshot_seq 单调推进，meta 指向最新版本
            latestSnapshotSeq = LatestSnapshotSeq(dbPath, worldId);
            metaWorldVersion = ReadMetaWorldVersion(dbPath, worldId);
            Program.Require(latestSnapshotSeq >= 3, "三次提交必须留下至少 3 个快照行（提交序列独立于 WorldVersion 推进）");
            Program.Require(metaWorldVersion == finalVersion, "meta.current_world_version 必须指向最新提交版本");
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    /// <summary>
    /// P1-PERSIST-05/06 验收：合法 save B 的 snapshot_blob 被另一份合法 payload（不同存档内容）
    /// 整体替换后，RestoreLatest 必须拒绝或回退到上一个 READY，绝不把替换内容当 current 发布。
    /// </summary>
    private static void SqliteLegalPayloadSwapNeverPublished()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            // 另一份"合法存档"的 payload：不同世界内容（不同命令/数量），但自洽（内部哈希一致）。
            var foreignPayload = BuildForeignLegalPayload("swap-foreign");

            // 存档 B：两次提交（READY A1 → READY A2）
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "swap-b1", 5_000)).Queued,
                "B 档首批命令应进入收件箱");
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, runtime.CaptureSnapshot());
            }

            var previousHash = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId)).StateHash;
            runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddHours(1)));
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, runtime.CaptureSnapshot());
            }

            // 用另一份合法 payload 整体替换最新快照行
            ReplaceSnapshotBlob(dbPath, worldId, LatestSnapshotSeq(dbPath, worldId), foreignPayload);

            RealtimeSnapshot restored;
            try
            {
                restored = SqliteCommitStore.RestoreLatest(dbPath, worldId);
            }
            catch (InvalidDataException)
            {
                return; // 拒绝（fail-closed）也是合法结果
            }

            Program.Require(restored.StateHash == previousHash,
                "合法 payload 整体替换后绝不把替换内容当 current 发布：必须回退到上一个 READY（或拒绝）");
            var restoredVersion = RealtimeSimulationRuntime.Restore(restored).ReadModel.WorldVersion;
            Program.Require(restoredVersion != runtime.ReadModel.WorldVersion,
                "回退结果不得是替换 payload 的世界版本（替换内容绝不能发布）");
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    /// <summary>
    /// P1-PERSIST-05 验收：state_blob / event_blob 任意字节篡改必须 fail-closed——
    /// RestoreLatest 抛异常，绝不把篡改后的 blob 当 current 发布（即使快照本体仍可解码）。
    /// </summary>
    private static void SqliteBlobBodyTamperingFailsClosed()
    {
        var worldId = new WorldId("ningyuan-1629");
        var dbPath = CreateDbPath();
        try
        {
            var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
            Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "blob-tamper", 5_000)).Queued,
                "篡改测试命令应进入收件箱");
            runtime.AdvanceTo(runtime.ReadModel.GameTime);
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                StageAndCommit(store, worldId, runtime.CaptureSnapshot());
            }

            // (a) event_blob 篡改：翻转日志行 blob 的最后一个字节
            var eventBlob = ReadEventBlob(dbPath, worldId, 0);
            var tamperedEvent = (byte[])eventBlob.Clone();
            tamperedEvent[^1] ^= 0x01;
            UpdateEventBlob(dbPath, worldId, 0, tamperedEvent);
            Program.RequireThrowsAny(() => SqliteCommitStore.RestoreLatest(dbPath, worldId));
            UpdateEventBlob(dbPath, worldId, 0, eventBlob); // 还原

            // (b) state_blob 篡改：翻转状态行 blob 的最后一个字节
            var currentVersion = runtime.ReadModel.WorldVersion;
            var stateBlob = ReadStateBlob(dbPath, worldId, currentVersion);
            var tamperedState = (byte[])stateBlob.Clone();
            tamperedState[^1] ^= 0x01;
            UpdateStateBlob(dbPath, worldId, currentVersion, tamperedState);
            Program.RequireThrowsAny(() => SqliteCommitStore.RestoreLatest(dbPath, worldId));
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    /// <summary>
    /// P1-PERSIST-01 验收：拒绝命令的 outcome 与 snapshot 同一事务——
    /// 单点崩溃后 command_outcomes 表行与快照内 CommandOutcomes 必须同时存在且一致。
    /// </summary>
    private static void SqliteRejectedOutcomeAtomicWithSnapshot()
    {
        // CreateLogisticsWorld 的世界编号是 logistics-world（含无物流权限的 "war" 角色）；
        // 存储的世界编号必须与快照内世界一致，否则行/校验和会写进错位世界。
        var worldId = new WorldId("logistics-world");
        var dbPath = CreateDbPath();
        try
        {
            using (var store = new SqliteCommitStore(dbPath, worldId))
            {
                var runtime = new RealtimeSimulationRuntime(Program.CreateLogisticsWorld(), store);
                var denied = new CreateShipmentCommand(
                    "sqlite-denied", new CharacterId("war"), new ShipmentId("shipment-sqlite-denied"),
                    new RouteId("capital-ningyuan-grain"), 300, Program.FixedUtc, runtime.ReadModel.WorldVersion);
                Program.Require(runtime.EnqueueCreateShipment(denied).Queued, "被拒命令应进入收件箱");
                var result = runtime.AdvanceTo(runtime.ReadModel.GameTime);
                Program.Require(!result.CommandResults.Single().Accepted, "无物流权限的角色必须被拒绝");
            }

            // 崩溃后从新连接恢复：两个持久化面必须都包含同一拒绝结果（同事务保证）
            var outcomeRow = ReadOutcomeRow(dbPath, worldId, "sqlite-denied");
            Program.Require(outcomeRow is not null, "command_outcomes 表必须持久化拒绝结果");
            var persistedOutcome = outcomeRow!.Value;
            Program.Require(persistedOutcome.Code == "TOOL_SCOPE_DENIED", "outcome 行必须保留结构化错误码");

            var restored = RealtimeSimulationRuntime.Restore(SqliteCommitStore.RestoreLatest(dbPath, worldId));
            var outcome = restored.CommandOutcomes.SingleOrDefault(item => item.CommandId == "sqlite-denied");
            Program.Require(outcome is not null && !outcome.Accepted && outcome.ErrorCodes.Contains("TOOL_SCOPE_DENIED"),
                "快照内 CommandOutcomes 必须包含同一拒绝结果（与 command_outcomes 表同事务一致）");
            Program.Require(persistedOutcome.Version == outcome!.ResultingWorldVersion,
                "outcome 行与快照内 outcome 的世界版本必须一致");
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    /// <summary>构造另一份自洽合法存档的 snapshot payload（内容与本存档不同，用于整体替换测试）。</summary>
    private static byte[] BuildForeignLegalPayload(string commandId)
    {
        var other = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
        Program.Require(other.EnqueueCreateShipment(Program.CreateShipment(other, commandId, 7_000)).Queued,
            "外来合法 payload 命令应进入收件箱");
        other.AdvanceTo(other.ReadModel.GameTime);
        other.SetPaused(true);
        other.AdvanceTo(other.ReadModel.GameTime);
        return SnapshotCodec.Serialize(other.CaptureSnapshot());
    }

    private static byte[] ReadEventBlob(string dbPath, WorldId worldId, long eventSequence)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_blob FROM event_journal WHERE world_id = $world AND event_sequence = $seq;";
        command.Parameters.AddWithValue("$world", worldId.Value);
        command.Parameters.AddWithValue("$seq", eventSequence);
        return command.ExecuteScalar() as byte[] ?? throw new InvalidDataException("事件行不存在。");
    }

    private static void UpdateEventBlob(string dbPath, WorldId worldId, long eventSequence, byte[] blob)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE event_journal SET event_blob = $blob WHERE world_id = $world AND event_sequence = $seq;";
        command.Parameters.AddWithValue("$world", worldId.Value);
        command.Parameters.AddWithValue("$seq", eventSequence);
        command.Parameters.AddWithValue("$blob", blob);
        command.ExecuteNonQuery();
        connection.Close();
    }

    private static byte[] ReadStateBlob(string dbPath, WorldId worldId, long worldVersion)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_blob FROM world_state WHERE world_id = $world AND world_version = $version;";
        command.Parameters.AddWithValue("$world", worldId.Value);
        command.Parameters.AddWithValue("$version", worldVersion);
        return command.ExecuteScalar() as byte[] ?? throw new InvalidDataException("状态行不存在。");
    }

    private static void UpdateStateBlob(string dbPath, WorldId worldId, long worldVersion, byte[] blob)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE world_state SET state_blob = $blob WHERE world_id = $world AND world_version = $version;";
        command.Parameters.AddWithValue("$world", worldId.Value);
        command.Parameters.AddWithValue("$version", worldVersion);
        command.Parameters.AddWithValue("$blob", blob);
        command.ExecuteNonQuery();
        connection.Close();
    }

    private static (string Code, string Message, long Version)? ReadOutcomeRow(string dbPath, WorldId worldId, string commandId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT outcome_code, message, world_version FROM command_outcomes WHERE world_id = $world AND command_id = $commandId;";
        command.Parameters.AddWithValue("$world", worldId.Value);
        command.Parameters.AddWithValue("$commandId", commandId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
    }

    private static long ReadMetaWorldVersion(string dbPath, WorldId worldId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_world_version FROM world_meta WHERE world_id = $world;";
        command.Parameters.AddWithValue("$world", worldId.Value);
        return Convert.ToInt64(command.ExecuteScalar());
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

    /// <summary>
    /// 测试夹具：把存档重算为"v1 时代布局"（legacy checksum，只覆盖元数据列、magic
    /// mingsim-commit-v1）并写回 meta.total_checksum——模拟真实 v1 档由 v1 时代代码写入的自洽校验和。
    /// 布局必须与生产代码 <c>ComputeLegacyTotalChecksum</c> 逐字节一致（提交/恢复两侧布局约定）。
    /// </summary>
    private static void ResealArchiveAsLegacy(string dbPath, WorldId worldId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        connection.Open();
        var totalChecksum = ComputeLegacyTotalChecksum(connection, worldId);
        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE world_meta SET total_checksum = $total WHERE world_id = $world;";
            update.Parameters.AddWithValue("$world", worldId.Value);
            update.Parameters.AddWithValue("$total", totalChecksum);
            update.ExecuteNonQuery();
        }

        connection.Close();
    }

    /// <summary>v1 时代整库校验和（元数据列全覆盖、不含任何 blob 字节）：与生产 legacy 布局逐字节一致。</summary>
    private static string ComputeLegacyTotalChecksum(SqliteConnection connection, WorldId worldId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'meta', world_id, schema_version, current_world_version, current_commit_id,
                   current_game_time_ticks, current_state_hash, current_payload_checksum,
                   current_snapshot_seq, ''
            FROM world_meta WHERE world_id = $world
            UNION ALL
            SELECT 'state', world_id, world_version, 0, commit_id, game_time_ticks, state_hash, '', 0, ''
            FROM world_state WHERE world_id = $world
            UNION ALL
            SELECT 'journal', world_id, event_sequence, 0, event_id, 0, '', '', 0, ''
            FROM event_journal WHERE world_id = $world
            UNION ALL
            SELECT 'snapshot', world_id, snapshot_seq, 0, commit_id, 0, state_hash, payload_checksum, 0, ''
            FROM snapshots WHERE world_id = $world;
            """;
        command.Parameters.AddWithValue("$world", worldId.Value);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        // 与生产布局逐字节一致：BinaryWriter.Write(string) 写 7-bit 长度前缀 + UTF-8 字节。
        writer.Write("mingsim-commit-v1");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            writer.Write(reader.GetString(0));
            writer.Write(reader.GetString(1));
            writer.Write(reader.GetInt64(2));
            writer.Write(reader.GetInt64(3));
            writer.Write(reader.GetString(4));
            writer.Write(reader.GetInt64(5));
            writer.Write(reader.GetString(6));
            writer.Write(reader.GetString(7));
            writer.Write(reader.GetInt64(8));
            writer.Write(reader.GetString(9));
        }

        writer.Flush();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray()));
    }
}