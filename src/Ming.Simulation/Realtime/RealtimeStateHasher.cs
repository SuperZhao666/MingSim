using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MingSim.Domain;
using MingSim.Domain.Realtime;

namespace MingSim.Simulation.Realtime;

/// <summary>
/// 实时世界的规范化状态哈希。
/// </summary>
/// <remarks>
/// 字段和集合顺序全部显式写出。这样哈希不依赖 Dictionary 枚举、对象地址、
/// 运行时默认哈希或渲染帧切分；调度器的时间、阶段、优先级和创建序号也会进入哈希。
/// </remarks>
public static class RealtimeStateHasher
{
    public static string Compute(
        WorldState state,
        IEnumerable<ScheduledSimulationEvent> scheduledEvents,
        string randomState,
        IEnumerable<string>? commandOutcomes = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scheduledEvents);
        ArgumentNullException.ThrowIfNull(randomState);

        var builder = new StringBuilder();
        Append(builder, "schema", "realtime-state-v1");
        Append(builder, "world", state.Id.Value);
        Append(builder, "game_time_ticks", state.GameTime.Value.Ticks.ToString(CultureInfo.InvariantCulture));
        Append(builder, "world_version", state.WorldVersion.ToString(CultureInfo.InvariantCulture));
        Append(builder, "commit_id", state.CommitId);
        Append(builder, "random_state", randomState);
        Append(builder, "treasury", state.Economy.Treasury.Silver.ToString(CultureInfo.InvariantCulture));

        if (commandOutcomes is not null)
        {
            foreach (var outcome in commandOutcomes.Order(StringComparer.Ordinal))
            {
                Append(builder, "command_outcome", outcome);
            }
        }

        foreach (var stock in state.Economy.Inventory.Stocks.Values.OrderBy(item => item.ResourceType, StringComparer.Ordinal))
        {
            Append(builder, "stock", $"{stock.ResourceType}:{stock.Quantity}:{stock.Reserved}");
        }

        foreach (var stockpile in state.Logistics.Stockpiles.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            Append(builder, "stockpile", $"{stockpile.Id.Value}:{stockpile.LocationId.Value}:{stockpile.Capacity}:{stockpile.GrainQuantity}");
        }

        foreach (var route in state.Logistics.Routes.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            Append(builder, "route", $"{route.Id.Value}:{route.FromStockpileId.Value}:{route.ToStockpileId.Value}:{route.Capacity}:{route.TravelHours}:{route.LossPerThousand}");
        }

        foreach (var shipment in state.Logistics.Shipments.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            Append(builder, "shipment", string.Join(":", shipment.Id.Value, shipment.RouteId.Value,
                shipment.GrainQuantity, shipment.Status, shipment.PlannedAt.Value.Ticks,
                shipment.DepartedAt?.Value.Ticks, shipment.ArrivedAt?.Value.Ticks,
                shipment.DeliveredGrain, shipment.LossGrain));
        }

        foreach (var facility in state.Industry.Facilities.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            Append(builder, "facility", string.Join(
                ":",
                facility.Id.Value,
                facility.LocationId.Value,
                facility.Type,
                facility.Status,
                facility.BaseCapacity,
                facility.Workforce,
                facility.BuildTurnsRemaining,
                facility.CreatedTurn,
                facility.ProducedThisTurn));
        }

        foreach (var army in state.Military.Armies.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            Append(builder, "army", string.Join(
                ":",
                army.Id.Value,
                army.LocationId.Value,
                army.Auxiliaries,
                army.LineInfantry,
                army.TrainingDays));
        }

        foreach (var scheduled in scheduledEvents.OrderBy(item => item.DueGameTime)
                     .ThenBy(item => item.Phase)
                     .ThenBy(item => item.Priority)
                     .ThenBy(item => item.CreationSequence))
        {
            var data = string.Join(
                ",",
                scheduled.Data.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}={item.Value}"));
            Append(builder, "scheduled", string.Join(
                ":",
                scheduled.EventId,
                scheduled.DueGameTime.Value.Ticks,
                scheduled.Phase,
                scheduled.Priority,
                scheduled.CreationSequence,
                scheduled.EventType,
                data));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Append(StringBuilder builder, string key, string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(key)).Append(':').Append(key)
            .Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value).Append('|');
    }
}
