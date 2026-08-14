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

    private static void TryRestore(byte[] payload)
    {
        var snapshot = SnapshotCodec.Deserialize(payload);
        _ = RealtimeSimulationRuntime.Restore(snapshot);
    }
}
