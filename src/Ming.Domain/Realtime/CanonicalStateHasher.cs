using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using MingSim.Domain.Events;
using MingSim.Domain;

namespace MingSim.Domain.Realtime;

/// <summary>
/// MingSim 唯一的权威状态哈希实现。
/// </summary>
/// <remarks>
/// 所有字段都用显式的 UTF-8 字节长度前缀写入二进制流；集合按稳定键排序，
/// 因此分隔符、文化格式、Dictionary 枚举顺序和反射序列化都不会参与结果。
/// </remarks>
public static class CanonicalStateHasher
{
    // Schema 4→5：新增 AppointmentState 段（任命影响授权裁决，必须进入权威哈希）。
    // Schema 5→6：ScenarioState 新增 RationReductionActive 标志（减耗令生效改变未来日耗/战备规则，必须进入权威哈希）。
    // 哈希 schema 改变就递增版本（doc 08 约定）。
    // 兼容性说明：schema 6 与 schema 5（本 PR 之前）的存档不兼容——旧存档的 StateHash 按 schema 5 计算，
    // 新代码按 schema 6 重算必然失配；恢复因此被显式拒绝（RealtimeSnapshotSchema.Version 已升到 7，
    // Restore 的版本门禁先于哈希校验返回"不支持实时快照版本"），而不是以哈希失配的偶然失败收场。
    // 旧存档不再可恢复（fail-closed），需要迁移或重建。
    public const int SchemaVersion = 6;

    /// <summary>任命段（AppointmentState）自 schema5 起进入哈希。</summary>
    public const int AppointmentsSchemaVersion = 5;

    /// <summary>减耗令标志（ScenarioState.RationReductionActive）自 schema6 起进入哈希。</summary>
    public const int RationReductionSchemaVersion = 6;

    /// <summary>#28 之前（v1 载荷时代）的哈希 schema：无 AppointmentState 段、无减耗令标志。</summary>
    public const int LegacySchemaVersionV1 = 4;

    public static string Compute(
        WorldState state,
        IEnumerable<ScheduledSimulationEvent> scheduledEvents,
        long nextCreationSequence,
        long nextIngressSequence,
        IEnumerable<CommandOutcome> commandOutcomes,
        string randomState,
        IEnumerable<DomainEvent> outboxEvents,
        decimal realGameTickRemainder,
        GameTime initialGameTime,
        long initialWorldVersion,
        long processedScheduledEventCount,
        bool isPaused,
        double speed,
        IEnumerable<string> pendingCommandFingerprints,
        long nextEventSequence = 0,
        int hashSchemaVersion = SchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scheduledEvents);
        ArgumentNullException.ThrowIfNull(commandOutcomes);
        ArgumentNullException.ThrowIfNull(outboxEvents);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        // 头部写调用方指定的哈希 schema：旧档按它记录的版本验证（doc 08 §14），默认当前版本。
        WriteInt32(writer, hashSchemaVersion);
        WriteString(writer, "realtime-state");
        WriteString(writer, state.Id.Value);
        WriteInt32(writer, state.TurnNumber);
        WriteInt64(writer, state.GameTime.Value.UtcTicks);
        WriteInt64(writer, state.WorldVersion);
        WriteString(writer, state.CommitId);
        WriteString(writer, randomState);
        WriteDecimal(writer, realGameTickRemainder);
        WriteInt64(writer, initialGameTime.Value.UtcTicks);
        WriteInt64(writer, initialWorldVersion);
        WriteInt64(writer, processedScheduledEventCount);
        writer.Write(isPaused);
        writer.Write(BitConverter.DoubleToInt64Bits(speed));
        var pending = pendingCommandFingerprints.ToArray();
        WriteInt32(writer, pending.Length);
        foreach (var fingerprint in pending)
        {
            WriteString(writer, fingerprint);
        }

        WriteMap(writer, state);
        WriteCharacters(writer, state);
        WriteInstitutions(writer, state);
        WriteCapabilities(writer, state);
        // 任命段自 schema5 起存在；v1 时代（schema4）无此段——按记录版本验证旧档时跳过。
        if (hashSchemaVersion >= AppointmentsSchemaVersion)
        {
            WriteAppointments(writer, state);
        }

        WriteEconomy(writer, state);
        WriteIndustry(writer, state);
        WriteMilitary(writer, state);
        WriteLogistics(writer, state);
        WriteMovements(writer, state);
        WriteScenario(writer, state, hashSchemaVersion);
        WriteReadiness(writer, state);
        WriteDecrees(writer, state);

        var actions = scheduledEvents
            .OrderBy(item => item.DueGameTime)
            .ThenBy(item => item.Phase)
            .ThenBy(item => item.Priority)
            .ThenBy(item => item.CreationSequence)
            .ToArray();
        WriteInt32(writer, actions.Length);
        foreach (var action in actions)
        {
            WriteString(writer, action.EventId);
            WriteInt64(writer, action.DueGameTime.Value.UtcTicks);
            WriteInt32(writer, action.Phase);
            WriteInt32(writer, action.Priority);
            WriteInt64(writer, action.CreationSequence);
            WriteString(writer, action.EventType);
            WriteNullableString(writer, action.CausalCommandId);
            WriteInt32(writer, action.SchemaVersion);
            WriteStringMap(writer, action.Data);
        }

        WriteInt64(writer, nextCreationSequence);
        WriteInt64(writer, nextIngressSequence);
        WriteInt64(writer, nextEventSequence);

        var outcomes = commandOutcomes.OrderBy(item => item.CommandId, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, outcomes.Length);
        foreach (var outcome in outcomes)
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

        // Outbox 是已发生历史，不是未来规则输入；它由快照单独保存，避免
        // 同一现实时间被拆成多帧后仅因审计事件数量不同而改变世界 hash。
        _ = outboxEvents;

        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteMap(BinaryWriter writer, WorldState state)
    {
        WriteString(writer, state.Map.Id);
        var provinces = state.Map.Provinces.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, provinces.Length);
        foreach (var province in provinces)
        {
            WriteString(writer, province.Id.Value);
            WriteString(writer, province.Name);
            WriteStringList(writer, province.AdjacentProvinces.OrderBy(item => item.Value, StringComparer.Ordinal).Select(item => item.Value));
        }
    }

    private static void WriteCharacters(BinaryWriter writer, WorldState state)
    {
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
    }

    private static void WriteInstitutions(BinaryWriter writer, WorldState state)
    {
        var institutions = state.Institutions.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, institutions.Length);
        foreach (var institution in institutions)
        {
            WriteString(writer, institution.Id.Value);
            WriteString(writer, institution.Name);
            WriteStringList(writer, institution.Capabilities.OrderBy(item => item).Select(item => item.ToString()));
            WriteStringList(writer, institution.Members.OrderBy(item => item.Value, StringComparer.Ordinal).Select(item => item.Value));
        }
    }

    private static void WriteCapabilities(BinaryWriter writer, WorldState state)
    {
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
    }

    /// <summary>
    /// 任命段：任命影响授权裁决（未来行为），必须按稳定键排序进入哈希。
    /// 排序键与 SnapshotCodec 完全一致，保证"同状态 → 同字节 → 同哈希"。
    /// </summary>
    private static void WriteAppointments(BinaryWriter writer, WorldState state)
    {
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

    private static void WriteEconomy(BinaryWriter writer, WorldState state)
    {
        WriteInt64(writer, state.Economy.Treasury.Silver);
        var stocks = state.Economy.Inventory.Stocks.Values.OrderBy(item => item.ResourceType, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, stocks.Length);
        foreach (var stock in stocks)
        {
            WriteString(writer, stock.ResourceType);
            WriteInt64(writer, stock.Quantity);
            WriteInt64(writer, stock.Reserved);
        }
    }

    private static void WriteIndustry(BinaryWriter writer, WorldState state)
    {
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
    }

    private static void WriteMilitary(BinaryWriter writer, WorldState state)
    {
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
    }

    private static void WriteLogistics(BinaryWriter writer, WorldState state)
    {
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
            writer.Write(shipment.Escort);
            WriteInt64(writer, shipment.RaidLossGrain);
        }
    }

    private static void WriteMovements(BinaryWriter writer, WorldState state)
    {
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
    }

    private static void WriteScenario(BinaryWriter writer, WorldState state, int hashSchemaVersion)
    {
        WriteInt32(writer, state.Scenario.LocalBurden);
        WriteInt32(writer, state.Scenario.MinisterTrust);
        WriteInt32(writer, state.Scenario.DailyGrainDemand);
        // 减耗令标志自 schema6 起进入哈希；按记录版本验证旧档（schema4/5）时跳过。
        if (hashSchemaVersion >= RationReductionSchemaVersion)
        {
            writer.Write(state.Scenario.RationReductionActive);
        }

        WriteNullableString(writer, state.Scenario.FrontStockpileId?.Value);
        WriteInt32(writer, state.Scenario.SecondHalfFromDay);
        WriteInt32(writer, state.Scenario.BurdenCooperationThreshold);
        WriteInt64(writer, state.Scenario.ScenarioSilverBudget);
        WriteInt64(writer, state.Scenario.SpentSilver);
        WriteInt64(writer, state.Scenario.ScenarioStartGameTime.Value.UtcTicks);
        writer.Write(state.Scenario.HardFailureReported);
    }

    private static void WriteReadiness(BinaryWriter writer, WorldState state)
    {
        WriteInt32(writer, state.Readiness.ValueBasisPoints);
        WriteInt64(writer, state.Readiness.ArrearsGrain);
        WriteInt32(writer, state.Readiness.ConsecutiveZeroGrainDays);
    }

    private static void WriteDecrees(BinaryWriter writer, WorldState state)
    {
        var decrees = state.Decrees.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, decrees.Length);
        foreach (var decree in decrees)
        {
            WriteString(writer, decree.Id.Value);
            WriteString(writer, decree.IssuerId.Value);
            WriteString(writer, decree.Goal);
            WriteString(writer, decree.RegionScope.Value);
            WriteInt64(writer, decree.Budget);
            WriteString(writer, decree.ResponsibleActorId.Value);
            WriteInt64(writer, decree.Deadline.Value.UtcTicks);
            WriteString(writer, decree.Restrictions);
            WriteString(writer, decree.Remarks);
            WriteString(writer, decree.RequiredCapability.ToString());
            WriteNullableString(writer, decree.RequiredResourceId);
            WriteNullableString(writer, decree.LinkedShipmentId);
            WriteString(writer, decree.Status.ToString());
        }
    }

    private static void WriteStringMap(BinaryWriter writer, IReadOnlyDictionary<string, string> values)
    {
        var entries = values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        WriteInt32(writer, entries.Length);
        foreach (var entry in entries)
        {
            WriteString(writer, entry.Key);
            WriteString(writer, entry.Value);
        }
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

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            WriteString(writer, value);
        }
    }

    private static void WriteInt32(BinaryWriter writer, int value) => writer.Write(value);

    private static void WriteInt64(BinaryWriter writer, long value) => writer.Write(value);

    private static void WriteDecimal(BinaryWriter writer, decimal value)
    {
        // decimal 的 scale 会记录表达式历史（0 与 0.0 数值相同但位表示不同）。
        // 规范化为不受拆帧运算影响的 InvariantCulture 文本，避免等价余数产生不同 hash。
        WriteString(writer, value.ToString("G29", CultureInfo.InvariantCulture));
    }

    private static void WriteNullableInt32(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
        {
            writer.Write(value.Value);
        }
    }

    private static void WriteNullableInt64(BinaryWriter writer, long? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
        {
            writer.Write(value.Value);
        }
    }
}
