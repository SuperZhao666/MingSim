using System.Security.Cryptography;
using System.Text;
using MingSim.Application.Ports;
using MingSim.Domain;
using MingSim.Domain.Common;
using MingSim.Domain.Events;

namespace MingSim.Persistence.InMemory;

/// <summary>
/// 用内存模拟“准备快照 → 校验 → 切换当前快照指针”。
/// </summary>
public sealed class InMemorySnapshotStore : ISnapshotStore
{
    private SnapshotPreparation? _current;

    public SnapshotPreparation? Current => _current;

    public SnapshotPreparation Prepare(WorldState state, IReadOnlyList<DomainEvent> events)
    {
        var snapshotState = state.Clone();
        var stateHash = StateHasher.Compute(snapshotState);
        var valid = stateHash.Length == 64 && events.All(domainEvent => domainEvent.WorldId == state.Id);

        return new SnapshotPreparation(
            state.Id,
            state.TurnNumber,
            stateHash,
            valid,
            snapshotState,
            events.ToArray());
    }

    public void Promote(SnapshotPreparation preparation)
    {
        if (!preparation.IsValid)
        {
            throw new InvalidOperationException("不能提升一个未通过校验的快照。");
        }

        _current = preparation;
    }
}

/// <summary>
/// 生成稳定的状态哈希。
/// </summary>
/// <remarks>
/// 不能直接对 Dictionary 做 JSON 序列化后哈希，因为字典枚举顺序可能变化。
/// 这里先按 ID 排序，再拼出规范文本，保证相同状态得到相同哈希。
/// </remarks>
internal static class StateHasher
{
    public static string Compute(WorldState state)
    {
        var builder = new StringBuilder();
        builder.Append("world|").Append(state.Id.Value).Append('|').Append(state.TurnNumber).Append('|');
        builder.Append("map=").Append(state.Map.Id).Append('|');
        foreach (var province in state.Map.Provinces.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            builder.Append("province=")
                .Append(province.Id.Value).Append(':')
                .Append(province.Name).Append(':');

            foreach (var adjacent in province.AdjacentProvinces.OrderBy(item => item.Value, StringComparer.Ordinal))
            {
                builder.Append(adjacent.Value).Append(',');
            }

            builder.Append('|');
        }

        builder.Append("silver=").Append(state.Economy.Treasury.Silver).Append('|');

        foreach (var stock in state.Economy.Inventory.Stocks.Values.OrderBy(item => item.ResourceType, StringComparer.Ordinal))
        {
            builder.Append("stock=")
                .Append(stock.ResourceType).Append(':')
                .Append(stock.Quantity).Append(':')
                .Append(stock.Reserved).Append('|');
        }

        foreach (var army in state.Military.Armies.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            builder.Append("army=")
                .Append(army.Id.Value).Append(':')
                .Append(army.Auxiliaries).Append(':')
                .Append(army.LineInfantry).Append(':')
                .Append(army.TrainingDays).Append('|');
        }

        foreach (var facility in state.Industry.Facilities.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            builder.Append("facility=")
                .Append(facility.Id.Value).Append(':')
                .Append(facility.LocationId.Value).Append(':')
                .Append(facility.Type).Append(':')
                .Append(facility.Status).Append(':')
                .Append(facility.BuildTurnsRemaining).Append(':')
                .Append(facility.BaseCapacity).Append(':')
                .Append(facility.Workforce).Append(':')
                .Append(facility.ProducedThisTurn).Append('|');
        }

        foreach (var institution in state.Institutions.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            builder.Append("institution=").Append(institution.Id.Value).Append(':').Append(institution.Name).Append('|');
            foreach (var capability in institution.Capabilities.OrderBy(item => item))
            {
                builder.Append("institution-capability=").Append(institution.Id.Value).Append(':').Append(capability).Append('|');
            }
        }

        foreach (var grant in state.CapabilityGrants
                     .OrderBy(item => item.ActorId.Value, StringComparer.Ordinal)
                     .ThenBy(item => item.Capability)
                     .ThenBy(item => item.ResourceId, StringComparer.Ordinal))
        {
            builder.Append("grant=")
                .Append(grant.ActorId.Value).Append(':')
                .Append(grant.Capability).Append(':')
                .Append(grant.ResourceId).Append(':')
                .Append(grant.ExpiresAtTurn).Append('|');
        }

        foreach (var character in state.Characters.Values.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            builder.Append("character=")
                .Append(character.Id.Value).Append(':')
                .Append(character.LocationId.Value).Append(':')
                .Append(character.Loyalty).Append(':')
                .Append(character.Stress).Append('|');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
