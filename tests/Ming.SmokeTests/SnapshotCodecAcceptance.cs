using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Realtime;
using MingSim.Persistence.Sqlite;
using MingSim.Simulation.Realtime;

namespace MingSim.SmokeTests;

/// <summary>
/// 快照规范化编解码（SnapshotCodec）验收：本地离线沙箱无法还原 Microsoft.Data.Sqlite，
/// 因此 SQLite 层最核心的"序列化/反序列化与恢复校验"先在这里等价验收：
/// 字节级往返、确定性、篡改即失败、恢复后继续推进与未重启实例一致。
/// </summary>
internal static class SnapshotCodecAcceptance
{
    public static void RunAll()
    {
        CodecRoundTripsArmyWorld();
        CodecRoundTripsGrainInTransitWorld();
        CodecSerializeIsDeterministic();
        CodecRejectsTamperedPayload();
        CodecRejectsUnknownFormatVersion();
        CodecRestoredInstanceContinuesDeterministically();
        CodecRoundTripsPendingInboxCommands();
        CodecMigratesV1PayloadToV2AndRestoresSameWorld();
        CodecMigratesRealV1HashSampleAndRestoresSameWorld();
        CodecMigrationFailureFailsClosed();
        CodecFallsBackFromCorruptedNewSnapshotToPreviousReady();
        CodecReadsV1WorldAndEventPayloads();
    }

    private static void CodecRoundTripsArmyWorld()
    {
        var runtime = new RealtimeSimulationRuntime(Program.CreateWorld());
        Program.Require(runtime.EnqueueMoveArmy(Program.CreateMove(runtime, "codec-move", Program.FixedUtc, 0, 2)).Queued,
            "编解码行军测试命令应该进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Program.Require(runtime.ReadModel.Movements.Count == 1, "行军必须先建立在途 MovementState");

        var snapshot = runtime.CaptureSnapshot();
        var restored = RealtimeSimulationRuntime.Restore(SnapshotCodec.Deserialize(SnapshotCodec.Serialize(snapshot)));
        Program.Require(restored.StateHash == runtime.StateHash, "军队世界快照往返后 canonical hash 必须一致");
        Program.Require(restored.ReadModel.Movements.Count == 1 && restored.ReadModel.Armies.Single().LocationId == new ProvinceId("frontier"),
            "军队世界快照往返后 MovementState 与军队位置必须一致");
        Program.Require(restored.ReadModel.WorldVersion == runtime.ReadModel.WorldVersion &&
                        restored.ReadModel.CommitId == runtime.ReadModel.CommitId,
            "军队世界快照往返后 WorldVersion/CommitId 必须一致");
    }

    private static void CodecRoundTripsGrainInTransitWorld()
    {
        var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
        Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "codec-5000", 5_000)).Queued,
            "编解码粮运测试命令应该进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        Program.Require(runtime.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit, "编解码测试必须先进入在途");

        var snapshot = runtime.CaptureSnapshot();
        var restored = RealtimeSimulationRuntime.Restore(SnapshotCodec.Deserialize(SnapshotCodec.Serialize(snapshot)));
        Program.Require(restored.StateHash == runtime.StateHash, "粮运在途快照往返后 canonical hash 必须一致");
        Program.Require(restored.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit &&
                        restored.ReadModel.Shipments.Single().GrainQuantity == 5_000,
            "在途运输单必须完整往返");
        Program.Require(restored.ReadModel.Stockpiles.Single(item => item.Id == new StockpileId("capital-granary")).GrainQuantity == 15_000,
            "在途状态必须先扣除起点库存");
    }

    private static void CodecSerializeIsDeterministic()
    {
        var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
        Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "codec-deterministic", 300)).Queued,
            "确定性测试命令应该进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        var snapshot = runtime.CaptureSnapshot();
        var first = SnapshotCodec.Serialize(snapshot);
        var second = SnapshotCodec.Serialize(snapshot);
        Program.Require(first.SequenceEqual(second), "同一快照的两次序列化必须逐字节一致（确定性）");
    }

    private static void CodecRejectsTamperedPayload()
    {
        var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
        Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "codec-tamper", 500)).Queued,
            "篡改测试命令应该进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        var payload = SnapshotCodec.Serialize(runtime.CaptureSnapshot());

        // 中间字节：可能破坏格式（解码抛）或改变内容（Restore 校验抛），两者都必须失败
        var middle = (byte[])payload.Clone();
        middle[payload.Length / 2] ^= 0xFF;
        Program.RequireThrowsAny(() => TryRestore(middle));

        // 末尾字节属于 NextEventSequence，内容变化后 payload checksum 必然对不上
        var tail = (byte[])payload.Clone();
        tail[^1] ^= 0x01;
        Program.RequireThrowsAny(() => TryRestore(tail));
    }

    private static void CodecRejectsUnknownFormatVersion()
    {
        var runtime = new RealtimeSimulationRuntime(Program.CreateWorld());
        var payload = SnapshotCodec.Serialize(runtime.CaptureSnapshot());
        var badVersion = (byte[])payload.Clone();
        badVersion[5] = 99; // MSNAP(5 字节) 之后的格式版本字节
        Program.RequireThrows<InvalidDataException>(() => SnapshotCodec.Deserialize(badVersion));
    }

    private static void CodecRestoredInstanceContinuesDeterministically()
    {
        var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
        Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "codec-continue", 5_000)).Queued,
            "确定性推进测试命令应该进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);

        var restored = RealtimeSimulationRuntime.Restore(
            SnapshotCodec.Deserialize(SnapshotCodec.Serialize(runtime.CaptureSnapshot())));
        var target = new GameTime(runtime.ReadModel.GameTime.Value.AddDays(12));
        var original = runtime.AdvanceTo(target);
        var replayed = restored.AdvanceTo(target);
        Program.Require(original.ReadModel.StateHash == replayed.ReadModel.StateHash,
            "编解码恢复实例推进到同一目标必须与原实例 canonical hash 一致");
        Program.Require(original.ReadModel.WorldVersion == replayed.ReadModel.WorldVersion &&
                        original.ReadModel.GameTime == replayed.ReadModel.GameTime,
            "编解码恢复实例推进后权威时间/WorldVersion 必须一致");
        Program.Require(Program.EventFingerprints(original.Events).SequenceEqual(Program.EventFingerprints(replayed.Events)),
            "编解码恢复实例推进后事件流必须一致");
    }

    private static void CodecRoundTripsPendingInboxCommands()
    {
        var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
        Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "codec-pending", 200)).Queued,
            "待处理命令测试应该进入收件箱");
        var snapshot = runtime.CaptureSnapshot(); // 快照时命令仍在收件箱（未推进）
        var restored = RealtimeSimulationRuntime.Restore(SnapshotCodec.Deserialize(SnapshotCodec.Serialize(snapshot)));
        Program.Require(restored.StateHash == runtime.StateHash, "含待处理收件箱的快照往返后 hash 必须一致");
        var originalResult = runtime.AdvanceTo(runtime.ReadModel.GameTime);
        var restoredResult = restored.AdvanceTo(restored.ReadModel.GameTime);
        Program.Require(originalResult.ReadModel.StateHash == restoredResult.ReadModel.StateHash &&
                        originalResult.ReadModel.WorldVersion == restoredResult.ReadModel.WorldVersion,
            "恢复后的待处理命令必须按同一顺序接纳并产生同一结果");
    }

    /// <summary>
    /// v1→v2 迁移（本地等价验收；SQLite 全量路径见 SqliteStoreAcceptance.SqliteV1ArchiveMigratesAndRestoresSameWorld）：
    /// 从 git 历史（#28 之前的 SnapshotCodec v1 格式）手工构造的 v1 载荷迁移到 v2 后，
    /// 恢复出与原始快照相同的世界（canonical hash、WorldVersion/GameTime、在途运输一致）。
    /// </summary>
    private static void CodecMigratesV1PayloadToV2AndRestoresSameWorld()
    {
        var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
        Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "codec-migrate", 5_000)).Queued,
            "迁移测试命令应该进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        var snapshot = runtime.CaptureSnapshot();
        var v2Payload = SnapshotCodec.Serialize(snapshot);
        var v1Payload = Program.DowngradePayloadToV1(v2Payload, "MSNAP"u8.ToArray());

        // 严格入口保持 fail-closed：Deserialize 不静默接受 v1，必须显式迁移
        Program.RequireThrows<InvalidDataException>(() => SnapshotCodec.Deserialize(v1Payload));

        var migrated = SnapshotCodec.MigrateV1ToV2(v1Payload);
        Program.Require(migrated[5] == 2, "迁移后必须写回当前 v2 格式版本字节");
        var migratedV2 = SnapshotCodec.MigrateV1ToV2(v2Payload);
        Program.Require(migratedV2.SequenceEqual(v2Payload), "已是 v2 的载荷迁移必须幂等原样返回");

        var migratedSnapshot = SnapshotCodec.Deserialize(migrated);
        Program.Require(Program.GetSnapshotState(migratedSnapshot).Appointments.Count == 0,
            "v1 世界没有任命段，迁移后任命必须为空");
        var restored = RealtimeSimulationRuntime.Restore(migratedSnapshot);
        Program.Require(restored.StateHash == runtime.StateHash,
            "v1 载荷迁移到 v2 后恢复，canonical hash 必须与迁移前一致（同一世界）");
        Program.Require(restored.ReadModel.WorldVersion == runtime.ReadModel.WorldVersion &&
                        restored.ReadModel.GameTime == runtime.ReadModel.GameTime &&
                        restored.ReadModel.CommitId == runtime.ReadModel.CommitId,
            "迁移恢复后 WorldVersion/GameTime/CommitId 必须一致");
        Program.Require(restored.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit &&
                        restored.ReadModel.Shipments.Single().GrainQuantity == 5_000,
            "迁移恢复后必须回到同一在途运输状态");
    }

    /// <summary>
    /// P1 回归样本：带真实 v1 哈希（schema4、无任命段，见 Program.RealV1StateHash）的载荷
    /// 迁移后必须通过 RealtimeSimulationRuntime.Restore 权威校验且 hash 一致。
    /// 旧实现（迁移原样保留 v1 哈希字段）在此必然失败：当前运行时按 schema5 重算无法复现
    /// schema4 哈希（已实证 HASHES DIFFER=True），因此 Restore 会抛"canonical state hash 校验失败"。
    /// </summary>
    private static void CodecMigratesRealV1HashSampleAndRestoresSameWorld()
    {
        // 夹具世界必须与 Program.RealV1StateHash 计算时完全一致（见常量出处注释）。
        var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
        Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "v1-real-fixture", 5_000)).Queued,
            "真实 v1 样本命令应该进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        var snapshot = runtime.CaptureSnapshot();
        var fixture = Program.BuildRealV1Fixture(snapshot, "MSNAP"u8.ToArray(), snapshot.StateHash, snapshot.PayloadChecksum);

        // 夹具前提：载荷携带的 StateHash 是 schema4 旧哈希，当前 hasher 无法复现（非自证式样本）。
        var migratedSnapshot = SnapshotCodec.Deserialize(SnapshotCodec.MigrateV1ToV2(fixture));
        Program.Require(!StringComparer.Ordinal.Equals(migratedSnapshot.StateHash, Program.RealV1StateHash),
            "迁移必须 re-seal：重新计算的 StateHash 不能再是 v1 时代哈希");
        Program.Require(StringComparer.Ordinal.Equals(migratedSnapshot.StateHash, runtime.StateHash),
            "re-seal 后 StateHash 必须等于当前 hasher 对同一世界的计算结果");

        // 权威恢复必须成功且 hash 一致（保留旧哈希的旧实现在这里失败——P1 回归点）。
        var restored = RealtimeSimulationRuntime.Restore(migratedSnapshot);
        Program.Require(restored.StateHash == runtime.StateHash,
            "真实 v1 哈希样本迁移后，权威恢复必须成功且 canonical hash 与原始世界一致");
        Program.Require(restored.ReadModel.WorldVersion == runtime.ReadModel.WorldVersion &&
                        restored.ReadModel.GameTime == runtime.ReadModel.GameTime,
            "真实 v1 哈希样本迁移恢复后 WorldVersion/GameTime 必须一致");
        Program.Require(restored.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit,
            "真实 v1 哈希样本迁移恢复后必须回到同一在途运输状态");
    }

    /// <summary>迁移失败 fail-closed：损坏/截断/未知版本的 v1 载荷必须抛异常，绝不返回半迁移结果。</summary>
    private static void CodecMigrationFailureFailsClosed()
    {
        var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
        var v2Payload = SnapshotCodec.Serialize(runtime.CaptureSnapshot());
        var v1Payload = Program.DowngradePayloadToV1(v2Payload, "MSNAP"u8.ToArray());

        // (a) 截断到一半的 v1 载荷：读不完所有段 → 迁移抛异常
        Program.RequireThrowsAny(() => SnapshotCodec.MigrateV1ToV2(v1Payload[..(v1Payload.Length / 2)]));

        // (b) 只丢末尾一个字节：nextEventSequence 读不完整 → 迁移抛异常
        Program.RequireThrowsAny(() => SnapshotCodec.MigrateV1ToV2(v1Payload[..^1]));

        // (c) 版本字节改成 99：既不是 v1 也不是 v2 → 显式拒绝
        var unknown = (byte[])v2Payload.Clone();
        unknown[5] = 99;
        Program.RequireThrows<InvalidDataException>(() => SnapshotCodec.MigrateV1ToV2(unknown));

        // (d) 魔数被破坏：迁移入口同样 fail-closed
        var noMagic = (byte[])v1Payload.Clone();
        noMagic[0] ^= 0xFF;
        Program.RequireThrows<InvalidDataException>(() => SnapshotCodec.MigrateV1ToV2(noMagic));
    }

    /// <summary>
    /// 快照失败回退回归样本 snapshot_failure_falls_back 的本地（InMemory 等价）路径：
    /// 新快照载荷损坏时旧 READY 快照仍可加载恢复（SQLite 全量路径——RestoreLatest 按序列
    /// 降序自动回退——见 SqliteStoreAcceptance.SqliteCorruptedNewSnapshotFallsBackToPreviousReady）。
    /// </summary>
    private static void CodecFallsBackFromCorruptedNewSnapshotToPreviousReady()
    {
        var runtime = new RealtimeSimulationRuntime(Program.CreateNingyuanWorld());
        Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "codec-fallback", 5_000)).Queued,
            "回退样本命令应该进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        var oldReadyHash = runtime.StateHash;
        var oldReadyPayload = SnapshotCodec.Serialize(runtime.CaptureSnapshot());

        runtime.AdvanceTo(new GameTime(runtime.ReadModel.GameTime.Value.AddHours(1)));
        var newPayload = SnapshotCodec.Serialize(runtime.CaptureSnapshot());
        // 结构级损坏：把新快照的格式版本字节改成未知版本（99）→ 解码必然拒绝，模拟"新快照损坏不可读"
        var corruptedNew = (byte[])newPayload.Clone();
        corruptedNew[5] = 99;

        // 新快照损坏：不可读、不可恢复
        Program.RequireThrows<InvalidDataException>(() => SnapshotCodec.Deserialize(corruptedNew));

        // 旧 READY 仍可加载：恢复出与新快照损坏前相同的历史世界
        var restoredOld = RealtimeSimulationRuntime.Restore(SnapshotCodec.Deserialize(oldReadyPayload));
        Program.Require(restoredOld.StateHash == oldReadyHash,
            "新快照损坏后，旧 READY 快照必须仍能加载出同一历史世界");
        Program.Require(restoredOld.ReadModel.Shipments.Single().Status == ShipmentStatus.InTransit,
            "旧 READY 快照恢复后必须回到可用的在途状态");
    }

    /// <summary>v1 状态/事件载荷：v1 世界段（无任命）与 v1 事件段（编码与 v2 相同）都能读取。</summary>
    private static void CodecReadsV1WorldAndEventPayloads()
    {
        var world = Program.CreateNingyuanWorld();
        var v2World = SnapshotCodec.SerializeWorld(world);
        var v1World = Program.DowngradePayloadToV1(v2World, "MSWLD"u8.ToArray());
        var restoredWorld = SnapshotCodec.DeserializeWorld(v1World);
        Program.Require(restoredWorld.Id == world.Id &&
                        restoredWorld.Economy.Treasury.Silver == world.Economy.Treasury.Silver,
            "v1 状态载荷读取后世界身份与国库必须一致");
        Program.Require(restoredWorld.Appointments.Count == 0, "v1 状态载荷没有任命段，任命必须为空");
        Program.Require(restoredWorld.Logistics.Stockpiles[new StockpileId("capital-granary")].GrainQuantity == 20_000,
            "v1 状态载荷读取后库存必须一致");

        var runtime = new RealtimeSimulationRuntime(world);
        Program.Require(runtime.EnqueueCreateShipment(Program.CreateShipment(runtime, "codec-v1event", 300)).Queued,
            "v1 事件测试命令应该进入收件箱");
        runtime.AdvanceTo(runtime.ReadModel.GameTime);
        var domainEvent = runtime.OutboxEvents.Single(item => item.EventType == "ShipmentPlanned");
        var v2Event = SnapshotCodec.SerializeEvent(domainEvent);
        var v1Event = (byte[])v2Event.Clone();
        v1Event[5] = 1; // MSEVT(5 字节) 之后的事件格式版本字节：v1 与 v2 事件段编码一致
        var restoredEvent = SnapshotCodec.DeserializeEvent(v1Event);
        Program.Require(restoredEvent.EventId == domainEvent.EventId &&
                        restoredEvent.EventType == domainEvent.EventType &&
                        restoredEvent.EventSequence == domainEvent.EventSequence,
            "v1 事件载荷必须按同一布局读取，事件字段逐项一致");
    }

    private static void TryRestore(byte[] payload)
    {
        var snapshot = SnapshotCodec.Deserialize(payload);
        _ = RealtimeSimulationRuntime.Restore(snapshot);
    }
}
