using MingSim.Domain;
using MingSim.Domain.Economy;
using MingSim.Domain.Events;

namespace MingSim.Simulation.Systems;

/// <summary>
/// 工业系统的自动结算。
/// </summary>
/// <remarks>
/// 生产不是给代理暴露的“按钮”。它是世界经过一个回合后，根据真实工坊、工人和产能自动发生的结果。
/// 这样可以避免模型重试一次就把生产重复执行两遍。
/// </remarks>
public sealed class IndustrySettlement
{
    public IReadOnlyList<DomainEvent> Settle(WorldState world)
    {
        var events = new List<DomainEvent>();

        foreach (var facility in world.Industry.Facilities.Values)
        {
            facility.AdvanceConstruction();

            if (facility.Status != FacilityStatus.Active)
            {
                facility.RecordProduction(0);
                continue;
            }

            var workforceCapacity = facility.Workforce * 10L;
            var quantity = Math.Min(facility.BaseCapacity, workforceCapacity);
            var resourceType = facility.Type switch
            {
                FacilityType.FlintlockWorkshop => "flintlock",
                FacilityType.GrainDepot => "grain",
                FacilityType.Shipyard => "transport_capacity",
                _ => "unknown",
            };

            world.Economy.Inventory.GetOrCreate(resourceType).Add(quantity);
            facility.RecordProduction(quantity);

            events.Add(new DomainEvent(
                $"production-{world.TurnNumber}-{facility.Id}",
                world.Id,
                world.TurnNumber,
                "ProductionCompleted",
                $"{facility.Id} 本回合实际产出 {quantity} 单位 {resourceType}。",
                new Dictionary<string, string>
                {
                    ["facility_id"] = facility.Id.Value,
                    ["resource_type"] = resourceType,
                    ["quantity"] = quantity.ToString(),
                }));
        }

        return events;
    }
}
