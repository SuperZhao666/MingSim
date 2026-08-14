using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Events;
using MingSim.Domain.Institutions;
using MingSim.Domain.Map;
using MingSim.Domain.Military;
using MingSim.Domain.Realtime;
using MingSim.Simulation.Realtime;

namespace MingSim.Persistence.Sqlite;

/// <summary>
/// RealtimeSnapshot 的规范化二进制编解码（序列化/反序列化）。
/// </summary>
/// <remarks>
/// 为什么不用 JSON/反射序列化器：权威恢复要求字节级往返一致与版本可控，任何文化格式、
/// 字典枚举顺序或反射字段名变化都会破坏确定性。这里沿用 CanonicalStateHasher 的编码风格
/// （NFC 归一化、长度前缀、显式端序、集合稳定排序），保证"写入库里的字节 == 恢复出来的对象"。
/// 格式带版本头：未来字段变化时递增版本，未知更高版本明确拒绝。
/// 格式版本 1→2 只存在一条真实差异：WorldState 末尾新增 AppointmentState 段（#28 引入）。
/// 对 v1 旧档先尝试迁移（<see cref="MigrateV1ToV2"/>：读 v1 段 → 写 v2 段，任命为空），
/// 迁移失败即拒绝（fail-closed），绝不把旧档按新格式静默误读；不预造版本注册表。
/// 反序列化会重建 WorldState 等对象，但不会绕过 RealtimeSimulationRuntime.Restore 的
/// canonical hash / payload checksum 校验，调用方拿到的仍是未验证的候选快照。
/// </remarks>
public static class SnapshotCodec
{
    // 格式版本 1→2：WorldState 编码新增 AppointmentState 段（任命必须随快照持久化）。
    // v1 旧档先尝试迁移到 v2（任命为空）再读取，未知更高版本仍显式拒绝（fail-closed）。
    private const byte FormatVersion = 2;
    private const byte FormatVersionV1 = 1;

    private static readonly byte[] Magic = "MSNAP"u8.ToArray();
    private static readonly byte[] WorldMagic = "MSWLD"u8.ToArray();
    private static readonly byte[] EventMagic = "MSEVT"u8.ToArray();

    /// <summary>对任意字节计算 SHA-256 十六进制校验和；用于快照载荷与整库提交校验。</summary>
    public static string ComputeChecksum(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>把一次捕获的完整快照编码为规范化字节。同一快照编码结果稳定可复现。</summary>
    public static byte[] Serialize(RealtimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        WriteInt32(writer, snapshot.SchemaVersion);
        WriteWorldState(writer, SnapshotReflection.GetState(snapshot));
        WriteScheduledEvents(writer, SnapshotReflection.GetScheduledEvents(snapshot));
        WritePendingCommands(writer, SnapshotReflection.GetPendingCommands(snapshot));
        WriteInt64(writer, SnapshotReflection.GetNextCreationSequence(snapshot));
        WriteInt64(writer, SnapshotReflection.GetNextIngressSequence(snapshot));
        WriteCommandOutcomes(writer, SnapshotReflection.GetCommandOutcomes(snapshot));
        WriteString(writer, SnapshotReflection.GetRandomState(snapshot));
        WriteOutboxEvents(writer, SnapshotReflection.GetOutboxEvents(snapshot));
        WriteDecimal(writer, SnapshotReflection.GetRealGameTickRemainder(snapshot));
        WriteInt64(writer, SnapshotReflection.GetInitialGameTime(snapshot).Value.UtcTicks);
        WriteInt64(writer, SnapshotReflection.GetInitialWorldVersion(snapshot));
        WriteInt64(writer, SnapshotReflection.GetProcessedScheduledEventCount(snapshot));
        writer.Write(SnapshotReflection.GetIsPaused(snapshot));
        writer.Write(BitConverter.DoubleToInt64Bits(SnapshotReflection.GetSpeed(snapshot)));
        WriteString(writer, snapshot.StateHash);
        WriteString(writer, snapshot.PayloadChecksum);
        WriteInt64(writer, SnapshotReflection.GetNextEventSequence(snapshot));
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>从规范化字节恢复快照；任何结构/内容损坏都会抛异常，绝不返回半成品。</summary>
    public static RealtimeSnapshot Deserialize(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false), leaveOpen: true);
        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("快照载荷缺少 MSNAP 魔数，不是本适配器写入的格式。");
        }

        var formatVersion = reader.ReadByte();
        if (formatVersion != FormatVersion)
        {
            throw new InvalidDataException($"不支持快照载荷格式版本 {formatVersion}（当前支持 {FormatVersion}）。");
        }

        var schemaVersion = ReadInt32(reader);
        var state = ReadWorldState(reader, readAppointments: true);
        var scheduledEvents = ReadScheduledEvents(reader);
        var pendingCommands = ReadPendingCommands(reader);
        var nextCreationSequence = ReadInt64(reader);
        var nextIngressSequence = ReadInt64(reader);
        var commandOutcomes = ReadCommandOutcomes(reader);
        var randomState = ReadString(reader);
        var outboxEvents = ReadOutboxEvents(reader);
        var realGameTickRemainder = ReadDecimal(reader);
        var initialGameTime = new GameTime(new DateTimeOffset(ReadInt64(reader), TimeSpan.Zero));
        var initialWorldVersion = ReadInt64(reader);
        var processedScheduledEventCount = ReadInt64(reader);
        var isPaused = reader.ReadBoolean();
        var speed = BitConverter.Int64BitsToDouble(ReadInt64(reader));
        var stateHash = ReadString(reader);
        var payloadChecksum = ReadString(reader);
        var nextEventSequence = ReadInt64(reader);
        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new InvalidDataException("快照载荷末尾存在多余字节，格式不一致。");
        }

        return SnapshotReflection.CreateSnapshot(
            schemaVersion,
            state,
            scheduledEvents,
            pendingCommands,
            nextCreationSequence,
            nextIngressSequence,
            commandOutcomes,
            randomState,
            outboxEvents,
            realGameTickRemainder,
            stateHash,
            payloadChecksum,
            initialGameTime,
            initialWorldVersion,
            processedScheduledEventCount,
            isPaused,
            speed,
            nextEventSequence);
    }

    /// <summary>
    /// 快照载荷格式迁移：v1 → v2（读 v1 段 → 写 v2 段）。
    /// </summary>
    /// <remarks>
    /// 只实现真实存在的 v1→v2 一条路径，不预造版本注册表：
    /// - v1 载荷：按 v1 布局读取全部段（WorldState 无任命段，任命为空），再用当前格式重新序列化；
    /// - 已是 v2 的载荷原样返回（幂等，调用方无需先探版本）；
    /// - 未知更高版本抛异常（fail-closed），绝不把旧档按新格式静默误读。
    /// re-seal（独立审查 P1）：v1 时代载荷携带的 StateHash/PayloadChecksum 由旧哈希规则
    /// （CanonicalStateHasher schema4、无任命段）计算，当前运行时按 schema5 重算必然失配；
    /// 迁移后必须用当前规则对重建的快照内容重算二者（<see cref="RealtimeSnapshotHash"/>），
    /// 才能通过 <see cref="RealtimeSimulationRuntime.Restore"/> 的权威校验并恢复同一世界。
    /// </remarks>
    public static byte[] MigrateV1ToV2(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false), leaveOpen: true);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("快照载荷缺少 MSNAP 魔数，不是本适配器写入的格式。");
        }

        var formatVersion = reader.ReadByte();
        if (formatVersion == FormatVersion)
        {
            return payload;
        }

        if (formatVersion != FormatVersionV1)
        {
            throw new InvalidDataException($"不支持快照载荷格式版本 {formatVersion}（当前支持 {FormatVersion}）。");
        }

        // re-seal：v1 载荷的 StateHash/PayloadChecksum 由旧哈希规则计算，当前运行时无法复现；
        // 读 v1 段 → 用当前规则重算 → 写 v2 段。任何一步失败都会抛出（fail-closed）。
        var migrated = ReadV1Snapshot(reader);
        var (stateHash, payloadChecksum) = RealtimeSnapshotHash.ComputeHashes(migrated);
        return Serialize(SnapshotReflection.Reseal(migrated, stateHash, payloadChecksum));
    }

    /// <summary>按 v1 布局读取 v1 快照载荷的剩余段（WorldState 无任命段，任命为空）。</summary>
    private static RealtimeSnapshot ReadV1Snapshot(BinaryReader reader)
    {
        // 写入顺序与 Deserialize 完全一致，仅 WorldState 段按 v1 布局读取（无任命段）。
        var schemaVersion = ReadInt32(reader);
        var state = ReadWorldState(reader, readAppointments: false);
        var scheduledEvents = ReadScheduledEvents(reader);
        var pendingCommands = ReadPendingCommands(reader);
        var nextCreationSequence = ReadInt64(reader);
        var nextIngressSequence = ReadInt64(reader);
        var commandOutcomes = ReadCommandOutcomes(reader);
        var randomState = ReadString(reader);
        var outboxEvents = ReadOutboxEvents(reader);
        var realGameTickRemainder = ReadDecimal(reader);
        var initialGameTime = new GameTime(new DateTimeOffset(ReadInt64(reader), TimeSpan.Zero));
        var initialWorldVersion = ReadInt64(reader);
        var processedScheduledEventCount = ReadInt64(reader);
        var isPaused = reader.ReadBoolean();
        var speed = BitConverter.Int64BitsToDouble(ReadInt64(reader));
        var stateHash = ReadString(reader);
        var payloadChecksum = ReadString(reader);
        var nextEventSequence = ReadInt64(reader);
        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new InvalidDataException("v1 快照载荷末尾存在多余字节，迁移失败（fail-closed）。");
        }

        return SnapshotReflection.CreateSnapshot(
            schemaVersion,
            state,
            scheduledEvents,
            pendingCommands,
            nextCreationSequence,
            nextIngressSequence,
            commandOutcomes,
            randomState,
            outboxEvents,
            realGameTickRemainder,
            stateHash,
            payloadChecksum,
            initialGameTime,
            initialWorldVersion,
            processedScheduledEventCount,
            isPaused,
            speed,
            nextEventSequence);
    }

    /// <summary>只编码 WorldState（world_state 表使用）；与快照内嵌的状态编码完全一致。</summary>
    public static byte[] SerializeWorld(WorldState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(WorldMagic);
        writer.Write(FormatVersion);
        WriteWorldState(writer, state);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>从 world_state 行字节恢复 WorldState；结构损坏直接抛异常。</summary>
    /// <remarks>格式版本 1（v1 存档的状态行）按 v1 布局读取：WorldState 无任命段，任命为空。</remarks>
    public static WorldState DeserializeWorld(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false), leaveOpen: true);
        if (!reader.ReadBytes(WorldMagic.Length).AsSpan().SequenceEqual(WorldMagic))
        {
            throw new InvalidDataException("状态载荷缺少 MSWLD 魔数。");
        }

        var formatVersion = reader.ReadByte();
        if (formatVersion == FormatVersionV1)
        {
            return ReadWorldState(reader, readAppointments: false);
        }

        if (formatVersion != FormatVersion)
        {
            throw new InvalidDataException($"状态载荷格式版本 {formatVersion} 不支持。");
        }

        return ReadWorldState(reader, readAppointments: true);
    }

    /// <summary>只编码一条 DomainEvent（event_journal 行使用）；与快照内嵌的事件编码一致。</summary>
    public static byte[] SerializeEvent(DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(EventMagic);
        writer.Write(FormatVersion);
        WriteEvent(writer, domainEvent);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>从 event_journal 行字节恢复 DomainEvent；结构损坏直接抛异常。</summary>
    /// <remarks>事件段编码在格式 1→2 之间未变化（#28 只改了 WorldState 的任命段），
    /// v1 存档的事件行仅版本字节不同，按同一布局读取。</remarks>
    public static DomainEvent DeserializeEvent(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false), leaveOpen: true);
        if (!reader.ReadBytes(EventMagic.Length).AsSpan().SequenceEqual(EventMagic))
        {
            throw new InvalidDataException("事件载荷缺少 MSEVT 魔数。");
        }

        var formatVersion = reader.ReadByte();
        if (formatVersion is not (FormatVersionV1 or FormatVersion))
        {
            throw new InvalidDataException($"事件载荷格式版本 {formatVersion} 不支持。");
        }

        return ReadEvent(reader);
    }

    private static void WriteEvent(BinaryWriter writer, DomainEvent domainEvent)
    {
        WriteString(writer, domainEvent.EventId);
        WriteString(writer, domainEvent.WorldId.Value);
        WriteInt32(writer, domainEvent.TurnNumber);
        WriteString(writer, domainEvent.EventType);
        WriteString(writer, domainEvent.Description);
        WriteNullableInt64(writer, domainEvent.OccurredAt?.UtcTicks);
        WriteInt64(writer, domainEvent.EventSequence);
        WriteInt64(writer, domainEvent.WorldVersion);
        WriteString(writer, domainEvent.CommitId);
        WriteNullableString(writer, domainEvent.CausalCommandId);
        WriteStringMap(writer, domainEvent.Data);
    }

    private static DomainEvent ReadEvent(BinaryReader reader)
    {
        // 写入顺序：eventId、worldId、turn、eventType、description、occurredAt、sequence、version、commitId、causalCommandId、data
        var eventId = ReadString(reader);
        var worldId = new WorldId(ReadString(reader));
        var turnNumber = ReadInt32(reader);
        var eventType = ReadString(reader);
        var description = ReadString(reader);
        var occurredAt = ReadNullableOccurredAt(reader);
        var eventSequence = ReadInt64(reader);
        var worldVersion = ReadInt64(reader);
        var commitId = ReadString(reader);
        var causalCommandId = ReadNullableString(reader);
        var data = ReadStringMap(reader);
        return new DomainEvent(eventId, worldId, turnNumber, eventType, description, data,
            occurredAt, eventSequence, worldVersion, commitId, causalCommandId);
    }

    private static void WriteWorldState(BinaryWriter writer, WorldState state)
    {
        WriteString(writer, state.Id.Value);
        WriteInt32(writer, state.TurnNumber);
        WriteInt64(writer, state.GameTime.Value.UtcTicks);
        WriteInt64(writer, state.WorldVersion);
        WriteString(writer, state.CommitId);

        WriteInt64(writer, state.Economy.Treasury.Silver);
        var stocks = state.Economy.Inventory.Stocks.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, stocks.Length);
        foreach (var (resourceType, stock) in stocks)
        {
            WriteString(writer, resourceType);
            WriteInt64(writer, stock.Quantity);
            WriteInt64(writer, stock.Reserved);
        }

        WriteString(writer, state.Map.Id);
        var provinces = state.Map.Provinces.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, provinces.Length);
        foreach (var province in provinces)
        {
            WriteString(writer, province.Id.Value);
            WriteString(writer, province.Name);
            WriteStringList(writer, province.AdjacentProvinces.OrderBy(item => item.Value, StringComparer.Ordinal).Select(item => item.Value));
        }

        var characters = state.Characters.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, characters.Length);
        foreach (var character in characters)
        {
            WriteString(writer, character.Id.Value);
            WriteString(writer, character.Name);
            WriteInt32(writer, character.Attributes.Administration);
            WriteInt32(writer, character.Attributes.Finance);
            WriteInt32(writer, character.Attributes.Martial);
            WriteInt32(writer, character.Attributes.Intrigue);
            WriteInt32(writer, character.Attributes.Learning);
            writer.Write(character.Personality.Honest);
            writer.Write(character.Personality.Bold);
            writer.Write(character.Personality.LoyalToRuler);
            writer.Write(character.Personality.Compassionate);
            WriteNullableString(writer, character.OfficeId);
            WriteString(writer, character.LocationId.Value);
            WriteInt32(writer, character.Loyalty);
            WriteInt32(writer, character.Stress);
            var memories = character.PrivateMemories
                .OrderBy(item => item.TurnNumber)
                .ThenBy(item => item.Subject, StringComparer.Ordinal)
                .ThenBy(item => item.Content, StringComparer.Ordinal)
                .ToArray();
            WriteInt32(writer, memories.Length);
            foreach (var memory in memories)
            {
                WriteInt32(writer, memory.TurnNumber);
                WriteString(writer, memory.Subject);
                WriteString(writer, memory.Content);
                writer.Write(memory.IsVerifiedFact);
            }
        }

        var institutions = state.Institutions.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, institutions.Length);
        foreach (var institution in institutions)
        {
            WriteString(writer, institution.Id.Value);
            WriteString(writer, institution.Name);
            WriteStringList(writer, institution.Capabilities.OrderBy(item => item).Select(item => item.ToString()));
            WriteStringList(writer, institution.Members.OrderBy(item => item.Value, StringComparer.Ordinal).Select(item => item.Value));
        }

        var grants = state.CapabilityGrants
            .OrderBy(item => item.ActorId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Capability)
            .ThenBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToArray();
        WriteInt32(writer, grants.Length);
        foreach (var grant in grants)
        {
            WriteString(writer, grant.ActorId.Value);
            WriteString(writer, grant.Capability.ToString());
            WriteNullableString(writer, grant.ResourceId);
            WriteNullableInt32(writer, grant.ExpiresAtTurn);
        }

        var facilities = state.Industry.Facilities.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, facilities.Length);
        foreach (var facility in facilities)
        {
            WriteString(writer, facility.Id.Value);
            WriteString(writer, facility.LocationId.Value);
            WriteString(writer, facility.Type.ToString());
            WriteString(writer, facility.Status.ToString());
            WriteInt64(writer, facility.BaseCapacity);
            WriteInt32(writer, facility.Workforce);
            WriteInt32(writer, facility.BuildTurnsRemaining);
            WriteInt32(writer, facility.CreatedTurn);
            WriteInt64(writer, facility.ProducedThisTurn);
        }

        var armies = state.Military.Armies.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, armies.Length);
        foreach (var army in armies)
        {
            WriteString(writer, army.Id.Value);
            WriteString(writer, army.Name);
            WriteString(writer, army.LocationId.Value);
            WriteInt64(writer, army.Auxiliaries);
            WriteInt64(writer, army.LineInfantry);
            WriteInt32(writer, army.TrainingDays);
        }

        var stockpiles = state.Logistics.Stockpiles.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, stockpiles.Length);
        foreach (var stockpile in stockpiles)
        {
            WriteString(writer, stockpile.Id.Value);
            WriteString(writer, stockpile.LocationId.Value);
            WriteInt64(writer, stockpile.Capacity);
            WriteInt64(writer, stockpile.GrainQuantity);
        }

        var routes = state.Logistics.Routes.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, routes.Length);
        foreach (var route in routes)
        {
            WriteString(writer, route.Id.Value);
            WriteString(writer, route.FromStockpileId.Value);
            WriteString(writer, route.ToStockpileId.Value);
            WriteInt64(writer, route.Capacity);
            WriteInt32(writer, route.TravelHours);
            WriteInt32(writer, route.LossPerThousand);
        }

        var shipments = state.Logistics.Shipments.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, shipments.Length);
        foreach (var shipment in shipments)
        {
            WriteString(writer, shipment.Id.Value);
            WriteString(writer, shipment.RouteId.Value);
            WriteInt64(writer, shipment.GrainQuantity);
            WriteString(writer, shipment.Status.ToString());
            WriteInt64(writer, shipment.PlannedAt.Value.UtcTicks);
            WriteNullableInt64(writer, shipment.DepartedAt?.Value.UtcTicks);
            WriteNullableInt64(writer, shipment.ArrivedAt?.Value.UtcTicks);
            WriteInt64(writer, shipment.DeliveredGrain);
            WriteInt64(writer, shipment.LossGrain);
        }

        var movements = state.Movements.Values.OrderBy(item => item.ArmyId.Value, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, movements.Length);
        foreach (var movement in movements)
        {
            WriteString(writer, movement.ActionId);
            WriteString(writer, movement.ArmyId.Value);
            WriteString(writer, movement.Origin.Value);
            WriteString(writer, movement.Destination.Value);
            WriteInt64(writer, movement.DueGameTime.Value.UtcTicks);
            WriteString(writer, movement.RouteFingerprint);
        }

        // 任命段（格式版本 2 起）：排序键与 CanonicalStateHasher.WriteAppointments 完全一致，
        // 保证"同状态 → 同字节 → 同哈希"，也保证快照字节往返稳定。
        var appointments = state.Appointments
            .OrderBy(item => item.PersonId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.OfficeId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Scope, StringComparer.Ordinal)
            .ThenBy(item => item.EffectiveFrom.Value.UtcTicks)
            .ToArray();
        WriteInt32(writer, appointments.Length);
        foreach (var appointment in appointments)
        {
            WriteString(writer, appointment.PersonId.Value);
            WriteString(writer, appointment.OfficeId.Value);
            WriteNullableString(writer, appointment.Scope);
            WriteNullableInt64(writer, appointment.Limit);
            WriteInt64(writer, appointment.EffectiveFrom.Value.UtcTicks);
            WriteNullableInt64(writer, appointment.EffectiveTo?.Value.UtcTicks);
        }
    }

    private static WorldState ReadWorldState(BinaryReader reader, bool readAppointments)
    {
        var worldId = new WorldId(ReadString(reader));
        var turnNumber = ReadInt32(reader);
        var gameTimeTicks = ReadInt64(reader);
        var worldVersion = ReadInt64(reader);
        var commitId = ReadString(reader);
        var treasurySilver = ReadInt64(reader);

        var stockCount = ReadInt32(reader);
        var inventory = new List<(string ResourceType, long Quantity)>(stockCount);
        var reservedByType = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var index = 0; index < stockCount; index++)
        {
            var resourceType = ReadString(reader);
            var quantity = ReadInt64(reader);
            var reserved = ReadInt64(reader);
            inventory.Add((resourceType, quantity));
            reservedByType[resourceType] = reserved;
        }

        var mapId = ReadString(reader);
        var provinceCount = ReadInt32(reader);
        var provinces = new List<ProvinceDefinition>(provinceCount);
        for (var index = 0; index < provinceCount; index++)
        {
            provinces.Add(new ProvinceDefinition(
                new ProvinceId(ReadString(reader)),
                ReadString(reader),
                ReadStringList(reader).Select(id => new ProvinceId(id))));
        }

        var map = new MapDefinition(mapId, provinces);

        var characterCount = ReadInt32(reader);
        var characters = new List<CharacterState>(characterCount);
        var characterDetails = new List<(CharacterState Character, int Loyalty, int Stress, List<MemoryNote> Memories)>(characterCount);
        for (var index = 0; index < characterCount; index++)
        {
            // 写入顺序：id、name、属性、人格、officeId（可空）、locationId；构造参数顺序是 locationId 在前。
            var characterId = new CharacterId(ReadString(reader));
            var characterName = ReadString(reader);
            var attributes = new CharacterAttributes(ReadInt32(reader), ReadInt32(reader), ReadInt32(reader), ReadInt32(reader), ReadInt32(reader));
            var personality = new CharacterPersonality(reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean());
            var officeId = ReadNullableString(reader);
            var locationId = new ProvinceId(ReadString(reader));
            var character = new CharacterState(characterId, characterName, attributes, personality, locationId, officeId);
            var loyalty = ReadInt32(reader);
            var stress = ReadInt32(reader);
            var memoryCount = ReadInt32(reader);
            var memories = new List<MemoryNote>(memoryCount);
            for (var memoryIndex = 0; memoryIndex < memoryCount; memoryIndex++)
            {
                memories.Add(new MemoryNote(ReadInt32(reader), ReadString(reader), ReadString(reader), reader.ReadBoolean()));
            }

            characters.Add(character);
            characterDetails.Add((character, loyalty, stress, memories));
        }

        var institutionCount = ReadInt32(reader);
        var institutions = new List<InstitutionState>(institutionCount);
        for (var index = 0; index < institutionCount; index++)
        {
            institutions.Add(new InstitutionState(
                new InstitutionId(ReadString(reader)),
                ReadString(reader),
                ReadStringList(reader).Select(capability => ParseEnum<GameCapability>(capability)),
                ReadStringList(reader).Select(member => new CharacterId(member))));
        }

        var grantCount = ReadInt32(reader);
        var grants = new List<CapabilityGrant>(grantCount);
        for (var index = 0; index < grantCount; index++)
        {
            grants.Add(new CapabilityGrant(
                new CharacterId(ReadString(reader)),
                ParseEnum<GameCapability>(ReadString(reader)),
                ReadNullableString(reader),
                ReadNullableInt32(reader)));
        }

        var facilityCount = ReadInt32(reader);
        var facilities = new List<(FacilityState Facility, FacilityStatus Status, long Produced)>(facilityCount);
        for (var index = 0; index < facilityCount; index++)
        {
            // 写入顺序：id、location、type、status、baseCapacity、workforce、buildTurns、createdTurn、produced
            var facilityId = new FacilityId(ReadString(reader));
            var facilityLocation = new ProvinceId(ReadString(reader));
            var facilityType = ParseEnum<FacilityType>(ReadString(reader));
            var status = ParseEnum<FacilityStatus>(ReadString(reader));
            var baseCapacity = ReadInt64(reader);
            var workforce = ReadInt32(reader);
            var buildTurnsRemaining = ReadInt32(reader);
            var createdTurn = ReadInt32(reader);
            var produced = ReadInt64(reader);
            var facility = new FacilityState(facilityId, facilityLocation, facilityType,
                baseCapacity, workforce, buildTurnsRemaining, createdTurn);
            facilities.Add((facility, status, produced));
        }

        var armyCount = ReadInt32(reader);
        var armies = new List<ArmyState>(armyCount);
        var trainingDays = new List<int>(armyCount);
        for (var index = 0; index < armyCount; index++)
        {
            armies.Add(new ArmyState(
                new ArmyId(ReadString(reader)),
                ReadString(reader),
                new ProvinceId(ReadString(reader)),
                ReadInt64(reader),
                ReadInt64(reader)));
            trainingDays.Add(ReadInt32(reader));
        }

        var stockpileCount = ReadInt32(reader);
        var stockpiles = new List<StockpileState>(stockpileCount);
        for (var index = 0; index < stockpileCount; index++)
        {
            stockpiles.Add(new StockpileState(
                new StockpileId(ReadString(reader)),
                new ProvinceId(ReadString(reader)),
                ReadInt64(reader),
                ReadInt64(reader)));
        }

        var routeCount = ReadInt32(reader);
        var routes = new List<RouteState>(routeCount);
        for (var index = 0; index < routeCount; index++)
        {
            routes.Add(new RouteState(
                new RouteId(ReadString(reader)),
                new StockpileId(ReadString(reader)),
                new StockpileId(ReadString(reader)),
                ReadInt64(reader),
                ReadInt32(reader),
                ReadInt32(reader)));
        }

        var shipmentCount = ReadInt32(reader);
        var shipments = new List<(ShipmentState Shipment, ShipmentStatus Status, GameTime? DepartedAt, GameTime? ArrivedAt, long Delivered, long Loss)>(shipmentCount);
        for (var index = 0; index < shipmentCount; index++)
        {
            // 写入顺序：id、routeId、grain、status、plannedTicks、departed、arrived、delivered、loss
            var shipmentId = new ShipmentId(ReadString(reader));
            var shipmentRoute = new RouteId(ReadString(reader));
            var grainQuantity = ReadInt64(reader);
            var status = ParseEnum<ShipmentStatus>(ReadString(reader));
            var plannedAt = new GameTime(new DateTimeOffset(ReadInt64(reader), TimeSpan.Zero));
            var departedAt = ReadNullableTicks(reader);
            var arrivedAt = ReadNullableTicks(reader);
            var delivered = ReadInt64(reader);
            var loss = ReadInt64(reader);
            var shipment = new ShipmentState(shipmentId, shipmentRoute, grainQuantity, plannedAt);
            shipments.Add((shipment, status, departedAt, arrivedAt, delivered, loss));
        }

        var movementCount = ReadInt32(reader);
        var movements = new List<MovementState>(movementCount);
        for (var index = 0; index < movementCount; index++)
        {
            movements.Add(new MovementState(
                ReadString(reader),
                new ArmyId(ReadString(reader)),
                new ProvinceId(ReadString(reader)),
                new ProvinceId(ReadString(reader)),
                new GameTime(new DateTimeOffset(ReadInt64(reader), TimeSpan.Zero)),
                ReadString(reader)));
        }

        // 任命段（格式版本 2 起）：写入顺序 personId、officeId、scope、limit、effectiveFrom、effectiveTo。
        // v1 布局（readAppointments=false）没有该段，任命为空——与 v1 时代的世界语义一致。
        var appointments = readAppointments ? ReadAppointments(reader) : [];


        var world = WorldState.CreateInitial(
            worldId,
            turnNumber,
            treasurySilver,
            map,
            currentTime: new DateTimeOffset(gameTimeTicks, TimeSpan.Zero),
            characters,
            institutions,
            grants,
            inventory,
            armies,
            stockpiles,
            routes,
            appointments);

        for (var index = 0; index < characters.Count; index++)
        {
            var (character, loyalty, stress, memories) = characterDetails[index];
            SnapshotReflection.SetCharacterDetails(character, loyalty, stress, memories);
        }

        for (var index = 0; index < armies.Count; index++)
        {
            SnapshotReflection.SetArmyTrainingDays(armies[index], trainingDays[index]);
        }

        foreach (var (facility, status, produced) in facilities)
        {
            SnapshotReflection.AddFacility(world.Industry, facility, status, produced);
        }

        foreach (var (shipment, status, departedAt, arrivedAt, delivered, loss) in shipments)
        {
            SnapshotReflection.SetShipmentCompletion(shipment, status, departedAt, arrivedAt, delivered, loss);
            SnapshotReflection.AddShipment(world.Logistics, shipment);
        }

        foreach (var movement in movements)
        {
            SnapshotReflection.AddMovement(world, movement);
        }

        SnapshotReflection.SetWorldCommitState(world, new GameTime(new DateTimeOffset(gameTimeTicks, TimeSpan.Zero)), worldVersion, commitId);
        return world;
    }

    /// <summary>读取任命段（格式版本 2 起）；写入顺序 personId、officeId、scope、limit、effectiveFrom、effectiveTo。</summary>
    private static IReadOnlyList<AppointmentState> ReadAppointments(BinaryReader reader)
    {
        var appointmentCount = ReadInt32(reader);
        var appointments = new List<AppointmentState>(appointmentCount);
        for (var index = 0; index < appointmentCount; index++)
        {
            appointments.Add(new AppointmentState(
                new CharacterId(ReadString(reader)),
                new InstitutionId(ReadString(reader)),
                ReadNullableString(reader),
                ReadNullableInt64(reader),
                new GameTime(new DateTimeOffset(ReadInt64(reader), TimeSpan.Zero)),
                ReadNullableTicks(reader)));
        }

        return appointments;
    }

    private static void WriteScheduledEvents(BinaryWriter writer, IReadOnlyList<ScheduledSimulationEvent> scheduledEvents)
    {
        var events = scheduledEvents
            .OrderBy(item => item.DueGameTime)
            .ThenBy(item => item.Phase)
            .ThenBy(item => item.Priority)
            .ThenBy(item => item.CreationSequence)
            .ToArray();
        WriteInt32(writer, events.Length);
        foreach (var scheduled in events)
        {
            WriteString(writer, scheduled.EventId);
            WriteInt64(writer, scheduled.DueGameTime.Value.UtcTicks);
            WriteInt32(writer, scheduled.Phase);
            WriteInt32(writer, scheduled.Priority);
            WriteInt64(writer, scheduled.CreationSequence);
            WriteString(writer, scheduled.EventType);
            WriteNullableString(writer, scheduled.CausalCommandId);
            WriteInt32(writer, scheduled.SchemaVersion);
            WriteStringMap(writer, scheduled.Data);
        }
    }

    private static IReadOnlyList<ScheduledSimulationEvent> ReadScheduledEvents(BinaryReader reader)
    {
        var count = ReadInt32(reader);
        var events = new List<ScheduledSimulationEvent>(count);
        for (var index = 0; index < count; index++)
        {
            // 写入顺序：eventId、due、phase、priority、creationSequence、eventType、causalCommandId、schemaVersion、data
            var eventId = ReadString(reader);
            var due = new GameTime(new DateTimeOffset(ReadInt64(reader), TimeSpan.Zero));
            var phase = ReadInt32(reader);
            var priority = ReadInt32(reader);
            var creationSequence = ReadInt64(reader);
            var eventType = ReadString(reader);
            var causalCommandId = ReadNullableString(reader);
            var schemaVersion = ReadInt32(reader);
            var data = ReadStringMap(reader);
            events.Add(new ScheduledSimulationEvent(eventId, due, phase, priority, creationSequence, eventType, data, causalCommandId, schemaVersion));
        }

        return events;
    }

    private static void WriteCommandOutcomes(BinaryWriter writer, IReadOnlyList<CommandOutcome> outcomes)
    {
        var ordered = outcomes.OrderBy(item => item.CommandId, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, ordered.Length);
        foreach (var outcome in ordered)
        {
            WriteString(writer, outcome.CommandId);
            WriteString(writer, outcome.Fingerprint);
            writer.Write(outcome.Accepted);
            WriteStringList(writer, outcome.ErrorCodes);
            WriteInt64(writer, outcome.IngressSequence);
            WriteInt64(writer, outcome.AcceptedGameTime.Value.UtcTicks);
            WriteInt64(writer, outcome.ExpectedWorldVersion);
            WriteInt64(writer, outcome.ResultingWorldVersion);
            WriteNullableString(writer, outcome.CommitId);
            WriteInt32(writer, outcome.SchemaVersion);
        }
    }

    private static IReadOnlyList<CommandOutcome> ReadCommandOutcomes(BinaryReader reader)
    {
        var count = ReadInt32(reader);
        var outcomes = new List<CommandOutcome>(count);
        for (var index = 0; index < count; index++)
        {
            outcomes.Add(new CommandOutcome(
                ReadString(reader),
                ReadString(reader),
                reader.ReadBoolean(),
                ReadStringList(reader),
                ReadInt64(reader),
                new GameTime(new DateTimeOffset(ReadInt64(reader), TimeSpan.Zero)),
                ReadInt64(reader),
                ReadInt64(reader),
                ReadNullableString(reader),
                ReadInt32(reader)));
        }

        return outcomes;
    }

    private static void WriteOutboxEvents(BinaryWriter writer, IReadOnlyList<DomainEvent> outboxEvents)
    {
        WriteInt32(writer, outboxEvents.Count);
        foreach (var domainEvent in outboxEvents)
        {
            WriteEvent(writer, domainEvent);
        }
    }

    private static IReadOnlyList<DomainEvent> ReadOutboxEvents(BinaryReader reader)
    {
        var count = ReadInt32(reader);
        var events = new List<DomainEvent>(count);
        for (var index = 0; index < count; index++)
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    private static IReadOnlyList<RealtimeCommand> ReadPendingCommands(BinaryReader reader)
    {
        var count = ReadInt32(reader);
        var commands = new List<RealtimeCommand>(count);
        for (var index = 0; index < count; index++)
        {
            var typeTag = ReadString(reader);
            var commandId = ReadString(reader);
            var actorId = new CharacterId(ReadString(reader));
            var submittedTicks = ReadInt64(reader);
            var expectedVersion = ReadInt64(reader);
            commands.Add(typeTag switch
            {
                "move" => new MoveArmyCommand(commandId, actorId, new ArmyId(ReadString(reader)),
                    new ProvinceId(ReadString(reader)), new DateTimeOffset(submittedTicks, TimeSpan.Zero), expectedVersion, ReadInt32(reader)),
                "shipment" => new CreateShipmentCommand(commandId, actorId, new ShipmentId(ReadString(reader)),
                    new RouteId(ReadString(reader)), ReadInt64(reader), new DateTimeOffset(submittedTicks, TimeSpan.Zero), expectedVersion),
                "pause" => new SetPausedCommand(commandId, actorId, reader.ReadBoolean(),
                    new DateTimeOffset(submittedTicks, TimeSpan.Zero), expectedVersion),
                "speed" => new SetSimulationSpeedCommand(commandId, actorId, BitConverter.Int64BitsToDouble(ReadInt64(reader)),
                    new DateTimeOffset(submittedTicks, TimeSpan.Zero), expectedVersion),
                _ => throw new InvalidDataException($"未知待处理命令类型 {typeTag}。"),
            });
        }

        return commands;
    }

    private static void WritePendingCommands(BinaryWriter writer, IReadOnlyList<RealtimeCommand> pendingCommands)
    {
        WriteInt32(writer, pendingCommands.Count);
        foreach (var command in pendingCommands)
        {
            switch (command)
            {
                case MoveArmyCommand move:
                    WriteString(writer, "move");
                    WriteString(writer, move.CommandId);
                    WriteString(writer, move.ActorId.Value);
                    WriteInt64(writer, move.SubmittedAt.UtcTicks);
                    WriteInt64(writer, move.ExpectedWorldVersion);
                    WriteString(writer, move.ArmyId.Value);
                    WriteString(writer, move.DestinationId.Value);
                    WriteInt32(writer, move.TravelHours);
                    break;
                case CreateShipmentCommand shipment:
                    WriteString(writer, "shipment");
                    WriteString(writer, shipment.CommandId);
                    WriteString(writer, shipment.ActorId.Value);
                    WriteInt64(writer, shipment.SubmittedAt.UtcTicks);
                    WriteInt64(writer, shipment.ExpectedWorldVersion);
                    WriteString(writer, shipment.ShipmentId.Value);
                    WriteString(writer, shipment.RouteId.Value);
                    WriteInt64(writer, shipment.GrainQuantity);
                    break;
                case SetPausedCommand pause:
                    WriteString(writer, "pause");
                    WriteString(writer, pause.CommandId);
                    WriteString(writer, pause.ActorId.Value);
                    WriteInt64(writer, pause.SubmittedAt.UtcTicks);
                    WriteInt64(writer, pause.ExpectedWorldVersion);
                    writer.Write(pause.Paused);
                    break;
                case SetSimulationSpeedCommand speed:
                    WriteString(writer, "speed");
                    WriteString(writer, speed.CommandId);
                    WriteString(writer, speed.ActorId.Value);
                    WriteInt64(writer, speed.SubmittedAt.UtcTicks);
                    WriteInt64(writer, speed.ExpectedWorldVersion);
                    writer.Write(BitConverter.DoubleToInt64Bits(speed.Speed));
                    break;
                default:
                    throw new InvalidDataException($"未知待处理命令类型 {command.GetType().FullName}。");
            }
        }
    }

    private static void WriteStringMap(BinaryWriter writer, IReadOnlyDictionary<string, string> values)
    {
        var entries = values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, entries.Length);
        foreach (var (key, value) in entries)
        {
            WriteString(writer, key);
            WriteString(writer, value);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(BinaryReader reader)
    {
        var count = ReadInt32(reader);
        var entries = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            entries[ReadString(reader)] = ReadString(reader);
        }

        return entries;
    }

    private static void WriteStringList(BinaryWriter writer, IEnumerable<string> values)
    {
        var entries = values.ToArray();
        WriteInt32(writer, entries.Length);
        foreach (var entry in entries)
        {
            WriteString(writer, entry);
        }
    }

    private static IReadOnlyList<string> ReadStringList(BinaryReader reader)
    {
        var count = ReadInt32(reader);
        var entries = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            entries.Add(ReadString(reader));
        }

        return entries;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = ReadInt32(reader);
        if (length < 0)
        {
            throw new InvalidDataException("字符串长度不能为负数。");
        }

        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            WriteString(writer, value);
        }
    }

    private static string? ReadNullableString(BinaryReader reader) => reader.ReadBoolean() ? ReadString(reader) : null;

    private static void WriteNullableInt32(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
        {
            writer.Write(value.Value);
        }
    }

    private static int? ReadNullableInt32(BinaryReader reader) => reader.ReadBoolean() ? ReadInt32(reader) : null;

    private static void WriteNullableInt64(BinaryWriter writer, long? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
        {
            writer.Write(value.Value);
        }
    }

    private static long? ReadNullableInt64(BinaryReader reader) => reader.ReadBoolean() ? ReadInt64(reader) : null;

    private static GameTime? ReadNullableTicks(BinaryReader reader) =>
        reader.ReadBoolean() ? new GameTime(new DateTimeOffset(ReadInt64(reader), TimeSpan.Zero)) : null;

    /// <summary>DomainEvent.OccurredAt 是 DateTimeOffset?，与 GameTime? 分开读取。</summary>
    private static DateTimeOffset? ReadNullableOccurredAt(BinaryReader reader) =>
        reader.ReadBoolean() ? new DateTimeOffset(ReadInt64(reader), TimeSpan.Zero) : null;

    private static void WriteInt32(BinaryWriter writer, int value) => writer.Write(value);

    private static int ReadInt32(BinaryReader reader) => reader.ReadInt32();

    private static void WriteInt64(BinaryWriter writer, long value) => writer.Write(value);

    private static long ReadInt64(BinaryReader reader) => reader.ReadInt64();

    private static void WriteDecimal(BinaryWriter writer, decimal value)
    {
        // decimal 的位表示会记录 scale 历史（0 与 0.0 不同），统一按 InvariantCulture 文本规范化，
        // 与 CanonicalStateHasher 处理余数的方式保持一致，保证字节级往返稳定。
        WriteString(writer, value.ToString("G29", CultureInfo.InvariantCulture));
    }

    private static decimal ReadDecimal(BinaryReader reader) =>
        decimal.Parse(ReadString(reader), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: false, out var result))
        {
            return result;
        }

        throw new InvalidDataException($"未知枚举值 {typeof(TEnum).Name}.{value}，可能是被篡改或版本不兼容的存档。");
    }
}
