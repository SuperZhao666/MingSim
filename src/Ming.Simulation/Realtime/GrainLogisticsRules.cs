using MingSim.Domain.Economy;

namespace MingSim.Simulation.Realtime;

/// <summary>粮运第一版的具体规则：只处理 grain，不抽象成通用物流框架。</summary>
internal static class GrainLogisticsRules
{
    public const string ResourceType = "grain";

    public static bool HasEnoughSourceGrain(StockpileState source, long quantity) =>
        quantity > 0 && source.GrainQuantity >= quantity;

    public static bool FitsRouteCapacity(LogisticsState logistics, RouteState route, long quantity) =>
        quantity > 0 && logistics.InTransitGrain(route.Id) + quantity <= route.Capacity;

    public static bool FitsDestinationCapacity(
        LogisticsState logistics,
        StockpileState destination,
        long quantity) =>
        quantity > 0 && logistics.ReservedIncomingGrain(destination.Id) + destination.GrainQuantity + quantity <=
        destination.Capacity;

    public static (long Delivered, long Loss) CalculateArrival(RouteState route, long quantity)
    {
        var loss = checked(quantity * route.LossPerThousand / 1000);
        return (quantity - loss, loss);
    }
}
