using MingSim.Domain.Economy;

namespace MingSim.Simulation.Realtime;

/// <summary>粮运第一版的具体规则：只处理 grain，不抽象成通用物流框架。</summary>
internal static class GrainLogisticsRules
{
    public const string ResourceType = "grain";

    public static bool HasEnoughSourceGrain(StockpileState source, long quantity) =>
        quantity > 0 && source.GrainQuantity >= quantity;

    public static bool FitsRouteCapacity(LogisticsState logistics, RouteState route, long quantity) =>
        quantity > 0 && quantity <= route.Capacity &&
        logistics.InTransitGrain(route.Id) <= route.Capacity - quantity;

    public static bool FitsDestinationCapacity(
        LogisticsState logistics,
        StockpileState destination,
        long quantity)
    {
        if (quantity <= 0)
        {
            return false;
        }

        var available = destination.Capacity - destination.GrainQuantity;
        var reserved = logistics.ReservedIncomingGrain(destination.Id);
        return reserved <= available && quantity <= available - reserved;
    }

    public static bool TryCalculateArrival(RouteState route, long quantity, out long delivered, out long loss) =>
        route.TryCalculateDeliveredGrain(quantity, out delivered, out loss);
}
