using System.Security.Cryptography;
using System.Text;
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
    public const int SchemaVersion = 2;

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
        IEnumerable<string> pendingCommandFingerprints)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scheduledEvents);
        ArgumentNullException.ThrowIfNull(commandOutcomes);
        ArgumentNullException.ThrowIfNull(outboxEvents);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        WriteInt32(writer, SchemaVersion);
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
        WriteEconomy(writer, state);
        WriteIndustry(writer, state);
        WriteMilitary(writer, state);
        WriteLogistics(writer, state);
        WriteMovements(writer, state);

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
        foreach (var part in decimal.GetBits(value))
        {
            writer.Write(part);
        }
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
