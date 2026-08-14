using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MingSim.Domain;
using MingSim.Domain.Events;
using MingSim.Domain.Realtime;

namespace MingSim.Simulation.Realtime;

/// <summary>
/// 实时快照权威哈希/校验和的共享纯函数：同一套当前规则同时服务捕获、权威校验与存档迁移。
/// </summary>
/// <remarks>
/// 为什么下沉为独立纯函数：RealtimeSimulationRuntime（CaptureSnapshot/Restore/只读状态哈希）、
/// 存档迁移（SnapshotCodec.MigrateV1ToV2 的 re-seal：迁移后用当前 hasher 重算
/// StateHash/PayloadChecksum）都必须调用同一套哈希组装逻辑——任何一处各写一份，
/// 都会造成"迁移后的哈希与运行时权威校验失配"（独立审查 P1 根因：v1 时代哈希由
/// CanonicalStateHasher schema4 计算，当前运行时按 schema5 重算必然失配）。
/// 本类只做确定性计算，不持有任何可变状态；CanonicalStateHasher（Domain）是唯一权威哈希实现，
/// 本类只负责"从快照内容组装调用参数"。
/// </remarks>
public static class RealtimeSnapshotHash
{
    /// <summary>命令的稳定指纹：对规范化负载字节做 SHA-256（幂等窗口与 payload checksum 共用）。</summary>
    public static string Fingerprint(RealtimeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        switch (command)
        {
            case MoveArmyCommand move:
                WriteFingerprintString(writer, "move");
                WriteFingerprintString(writer, move.CommandId);
                WriteFingerprintString(writer, move.ActorId.Value);
                WriteFingerprintString(writer, move.ArmyId.Value);
                WriteFingerprintString(writer, move.DestinationId.Value);
                writer.Write(move.SubmittedAt.UtcTicks);
                writer.Write(move.ExpectedWorldVersion);
                writer.Write(move.TravelHours);
                break;
            case CreateShipmentCommand shipment:
                WriteFingerprintString(writer, "shipment");
                WriteFingerprintString(writer, shipment.CommandId);
                WriteFingerprintString(writer, shipment.ActorId.Value);
                WriteFingerprintString(writer, shipment.ShipmentId.Value);
                WriteFingerprintString(writer, shipment.RouteId.Value);
                writer.Write(shipment.GrainQuantity);
                writer.Write(shipment.Escort);
                writer.Write(shipment.SubmittedAt.UtcTicks);
                writer.Write(shipment.ExpectedWorldVersion);
                break;
            case CreateDecreeCommand decree:
                WriteFingerprintString(writer, "decree");
                WriteFingerprintString(writer, decree.CommandId);
                WriteFingerprintString(writer, decree.ActorId.Value);
                WriteFingerprintString(writer, decree.DecreeId.Value);
                WriteFingerprintString(writer, decree.Goal);
                WriteFingerprintString(writer, decree.RegionScope.Value);
                writer.Write(decree.Budget);
                WriteFingerprintString(writer, decree.ResponsibleActorId.Value);
                writer.Write(decree.Deadline.Value.UtcTicks);
                WriteFingerprintString(writer, decree.Restrictions);
                WriteFingerprintString(writer, decree.Remarks);
                WriteFingerprintString(writer, decree.RequiredCapability.ToString());
                WriteFingerprintString(writer, decree.RequiredResourceId ?? string.Empty);
                WriteFingerprintString(writer, decree.LinkedShipmentId ?? string.Empty);
                WriteFingerprintString(writer, decree.Kind.ToString());
                writer.Write(decree.SubmittedAt.UtcTicks);
                writer.Write(decree.ExpectedWorldVersion);
                break;
            case SetPausedCommand pause:
                WriteFingerprintString(writer, "pause");
                WriteFingerprintString(writer, pause.CommandId);
                WriteFingerprintString(writer, pause.ActorId.Value);
                writer.Write(pause.Paused);
                writer.Write(pause.SubmittedAt.UtcTicks);
                writer.Write(pause.ExpectedWorldVersion);
                break;
            case SetSimulationSpeedCommand speed:
                WriteFingerprintString(writer, "speed");
                WriteFingerprintString(writer, speed.CommandId);
                WriteFingerprintString(writer, speed.ActorId.Value);
                writer.Write(BitConverter.DoubleToInt64Bits(speed.Speed));
                writer.Write(speed.SubmittedAt.UtcTicks);
                writer.Write(speed.ExpectedWorldVersion);
                break;
            default:
                // 未知命令类型没有稳定的负载字段；用类型名兜底生成指纹。
                // 为什么：ValidateAndApplyCommand 的 switch 已经用 UNKNOWN_COMMAND
                // 结构化拒绝未知类型，Fingerprint 若在这里抛异常反而会在拒绝之前
                // 让整个推进崩溃；稳定的类型指纹让同一命令编号的重放得到同一拒绝结果。
                WriteFingerprintString(writer, "unknown");
                WriteFingerprintString(writer, command.GetType().FullName ?? command.GetType().Name);
                WriteFingerprintString(writer, command.CommandId);
                WriteFingerprintString(writer, command.ActorId.Value);
                writer.Write(command.SubmittedAt.UtcTicks);
                writer.Write(command.ExpectedWorldVersion);
                break;
        }

        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>按当前权威规则计算快照内容的 canonical StateHash。</summary>
    public static string ComputeStateHash(RealtimeSnapshot snapshot) => CanonicalStateHasher.Compute(
        snapshot.State, snapshot.ScheduledEvents, snapshot.NextCreationSequence, snapshot.NextIngressSequence,
        snapshot.CommandOutcomes, snapshot.RandomState, snapshot.OutboxEvents, snapshot.RealGameTickRemainder,
        snapshot.InitialGameTime, snapshot.InitialWorldVersion, snapshot.ProcessedScheduledEventCount, snapshot.IsPaused,
        snapshot.Speed, snapshot.PendingCommands.Select(Fingerprint), snapshot.NextEventSequence);

    /// <summary>按当前权威规则计算快照内容的 canonical StateHash（内容参数版，供运行时活状态路径使用）。</summary>
    public static string ComputeStateHash(
        WorldState state,
        IReadOnlyList<ScheduledSimulationEvent> scheduledEvents,
        IReadOnlyList<RealtimeCommand> pendingCommands,
        long nextCreationSequence,
        long nextIngressSequence,
        IEnumerable<CommandOutcome> commandOutcomes,
        string randomState,
        IReadOnlyList<DomainEvent> outboxEvents,
        decimal realGameTickRemainder,
        GameTime initialGameTime,
        long initialWorldVersion,
        long processedScheduledEventCount,
        bool isPaused,
        double speed,
        long nextEventSequence) =>
        CanonicalStateHasher.Compute(state, scheduledEvents, nextCreationSequence, nextIngressSequence,
            commandOutcomes, randomState, outboxEvents, realGameTickRemainder, initialGameTime, initialWorldVersion,
            processedScheduledEventCount, isPaused, speed, pendingCommands.Select(Fingerprint), nextEventSequence);

    /// <summary>按当前权威规则计算 payload checksum（与 <paramref name="stateHash"/> 配套）。</summary>
    public static string ComputePayloadChecksum(RealtimeSnapshot snapshot, string stateHash) =>
        ComputePayloadChecksum(stateHash, snapshot.PendingCommands, snapshot.OutboxEvents, snapshot.NextCreationSequence,
            snapshot.NextIngressSequence, snapshot.NextEventSequence, snapshot.ProcessedScheduledEventCount,
            snapshot.RealGameTickRemainder, snapshot.IsPaused, snapshot.Speed, snapshot.RandomState,
            RealtimeSnapshotSchema.Version);

    /// <summary>
    /// V1 校验专用入口（独立审查 P1-1/P1-2）：按 v1 时代规则重算 StateHash/PayloadChecksum——
    /// 状态哈希用 schema4（无任命段、无减耗令标志），payload checksum 头部显式写 v1 时代版本
    /// （<see cref="RealtimeSnapshotSchema.LegacyVersionV1"/> = 6，不是当前 7）。
    /// 迁移用它与 v1 载荷自带的校验字段逐字节比对——内容损坏但结构可解码的旧档会失配而被拒绝
    /// （fail-closed），绝不带病 re-seal 静默通过；校验基准与真实 v1 档一致，不会误拒。
    /// </summary>
    public static (string StateHash, string PayloadChecksum) ComputeV1Hashes(RealtimeSnapshot snapshot)
    {
        var stateHash = CanonicalStateHasher.Compute(
            snapshot.State, snapshot.ScheduledEvents, snapshot.NextCreationSequence, snapshot.NextIngressSequence,
            snapshot.CommandOutcomes, snapshot.RandomState, snapshot.OutboxEvents, snapshot.RealGameTickRemainder,
            snapshot.InitialGameTime, snapshot.InitialWorldVersion, snapshot.ProcessedScheduledEventCount, snapshot.IsPaused,
            snapshot.Speed, snapshot.PendingCommands.Select(Fingerprint), snapshot.NextEventSequence,
            hashSchemaVersion: CanonicalStateHasher.LegacySchemaVersionV1);
        return (stateHash, ComputePayloadChecksum(stateHash, snapshot.PendingCommands, snapshot.OutboxEvents,
            snapshot.NextCreationSequence, snapshot.NextIngressSequence, snapshot.NextEventSequence,
            snapshot.ProcessedScheduledEventCount, snapshot.RealGameTickRemainder, snapshot.IsPaused, snapshot.Speed,
            snapshot.RandomState, RealtimeSnapshotSchema.LegacyVersionV1));
    }

    /// <summary>按当前权威规则计算 StateHash + PayloadChecksum（快照对象版；迁移 re-seal 与测试使用）。</summary>
    public static (string StateHash, string PayloadChecksum) ComputeHashes(RealtimeSnapshot snapshot)
    {
        var stateHash = ComputeStateHash(snapshot);
        return (stateHash, ComputePayloadChecksum(snapshot, stateHash));
    }

    /// <summary>按当前权威规则计算 StateHash + PayloadChecksum（内容参数版；运行时捕获/恢复使用）。</summary>
    public static (string StateHash, string PayloadChecksum) ComputeHashes(
        WorldState state,
        IReadOnlyList<ScheduledSimulationEvent> scheduledEvents,
        IReadOnlyList<RealtimeCommand> pendingCommands,
        long nextCreationSequence,
        long nextIngressSequence,
        IEnumerable<CommandOutcome> commandOutcomes,
        string randomState,
        IReadOnlyList<DomainEvent> outboxEvents,
        decimal realGameTickRemainder,
        GameTime initialGameTime,
        long initialWorldVersion,
        long processedScheduledEventCount,
        bool isPaused,
        double speed,
        long nextEventSequence)
    {
        var stateHash = ComputeStateHash(state, scheduledEvents, pendingCommands, nextCreationSequence, nextIngressSequence,
            commandOutcomes, randomState, outboxEvents, realGameTickRemainder, initialGameTime, initialWorldVersion,
            processedScheduledEventCount, isPaused, speed, nextEventSequence);
        return (stateHash, ComputePayloadChecksum(stateHash, pendingCommands, outboxEvents, nextCreationSequence,
            nextIngressSequence, nextEventSequence, processedScheduledEventCount, realGameTickRemainder, isPaused, speed,
            randomState, RealtimeSnapshotSchema.Version));
    }

    private static string ComputePayloadChecksum(
        string stateHash,
        IReadOnlyList<RealtimeCommand> pendingCommands,
        IReadOnlyList<DomainEvent> outboxEvents,
        long nextCreationSequence,
        long nextIngressSequence,
        long nextEventSequence,
        long processedScheduledEventCount,
        decimal realGameTickRemainder,
        bool isPaused,
        double speed,
        string randomState,
        int snapshotSchemaVersion)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        // 头部写调用方指定的运行时快照 schema 版本：当前校验写当前版本，v1 时代校验显式写
        // LegacyVersionV1（=6）——用当前版本（7）比对 v6 时代旧档必然误拒（独立审查 P1-2）。
        writer.Write(snapshotSchemaVersion);
        writer.Write(stateHash);
        writer.Write(nextCreationSequence);
        writer.Write(nextIngressSequence);
        writer.Write(nextEventSequence);
        writer.Write(processedScheduledEventCount);
        writer.Write(realGameTickRemainder.ToString("G29", CultureInfo.InvariantCulture));
        writer.Write(isPaused);
        writer.Write(BitConverter.DoubleToInt64Bits(speed));
        writer.Write(randomState);
        foreach (var command in pendingCommands)
        {
            writer.Write(Fingerprint(command));
        }

        foreach (var domainEvent in outboxEvents)
        {
            writer.Write(domainEvent.EventId);
            writer.Write(domainEvent.WorldId.Value);
            writer.Write(domainEvent.TurnNumber);
            writer.Write(domainEvent.EventType);
            writer.Write(domainEvent.Description);
            writer.Write(domainEvent.OccurredAt.HasValue);
            if (domainEvent.OccurredAt.HasValue)
            {
                writer.Write(domainEvent.OccurredAt.Value.UtcTicks);
            }

            writer.Write(domainEvent.EventSequence);
            writer.Write(domainEvent.WorldVersion);
            writer.Write(domainEvent.CommitId);
            writer.Write(domainEvent.CausalCommandId ?? string.Empty);
            var data = domainEvent.Data.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            writer.Write(data.Length);
            foreach (var item in data)
            {
                writer.Write(item.Key);
                writer.Write(item.Value);
            }
        }

        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteFingerprintString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
