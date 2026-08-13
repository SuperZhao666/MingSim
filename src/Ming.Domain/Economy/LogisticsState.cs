using MingSim.Domain.Common;
using MingSim.Domain.Realtime;

namespace MingSim.Domain.Economy;

/// <summary>最小粮运闭环只处理粮食，并把每一批货物的生命周期显式记录下来。</summary>
public enum ShipmentStatus
{
    Planned,
    InTransit,
    Arrived,
}

/// <summary>一个可装卸粮食的库存点。</summary>
public sealed class StockpileState
{
    public const string ResourceType = "grain";

    public StockpileState(
        StockpileId id,
        ProvinceId locationId,
        long capacity,
        long grainQuantity = 0)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("库存点编号不能为空。", nameof(id));
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "库存点容量必须为正数。");
        }

        if (grainQuantity < 0 || grainQuantity > capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(grainQuantity), "初始粮食必须落在库存点容量内。");
        }

        Id = id;
        LocationId = locationId;
        Capacity = capacity;
        GrainQuantity = grainQuantity;
    }

    public StockpileId Id { get; }

    public ProvinceId LocationId { get; }

    public long Capacity { get; }

    public long GrainQuantity { get; private set; }

    /// <summary>给 UI 和玩家看的简短别名；这个库存点只保存 grain。</summary>
    public long Quantity => GrainQuantity;

    internal bool CanStore(long quantity) => quantity >= 0 && quantity <= Capacity - GrainQuantity;

    internal bool TryTakeGrain(long quantity)
    {
        if (quantity <= 0 || quantity > GrainQuantity)
        {
            return false;
        }

        GrainQuantity -= quantity;
        return true;
    }

    internal bool TryStoreGrain(long quantity)
    {
        if (!CanStore(quantity))
        {
            return false;
        }

        GrainQuantity += quantity;
        return true;
    }

    internal StockpileState Clone() => new(Id, LocationId, Capacity, GrainQuantity);
}

/// <summary>一条固定的粮运路线，同时定义在途容量、行程时间和损耗率。</summary>
public sealed record RouteState
{
    public RouteState(
        RouteId id,
        StockpileId fromStockpileId,
        StockpileId toStockpileId,
        long capacity,
        int travelHours,
        int lossPerThousand)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("路线编号不能为空。", nameof(id));
        }

        if (fromStockpileId == toStockpileId)
        {
            throw new ArgumentException("路线的起点和终点不能相同。", nameof(toStockpileId));
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "路线容量必须为正数。");
        }

        if (travelHours <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(travelHours), "路线行程必须至少一小时。");
        }

        if (lossPerThousand is < 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(lossPerThousand), "损耗率必须在 0 到 1000‰ 之间。");
        }

        Id = id;
        FromStockpileId = fromStockpileId;
        ToStockpileId = toStockpileId;
        Capacity = capacity;
        TravelHours = travelHours;
        LossPerThousand = lossPerThousand;
    }

    public RouteId Id { get; }

    public StockpileId FromStockpileId { get; }

    public StockpileId ToStockpileId { get; }

    public long Capacity { get; }

    public int TravelHours { get; }

    public int LossPerThousand { get; }
}

/// <summary>一次粮运的权威状态；货物在抵达前仍由 Shipment 账本持有。</summary>
public sealed class ShipmentState
{
    public ShipmentState(
        ShipmentId id,
        RouteId routeId,
        long grainQuantity,
        GameTime plannedAt)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("运输单编号不能为空。", nameof(id));
        }

        if (grainQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(grainQuantity), "运输粮食数量必须为正数。");
        }

        Id = id;
        RouteId = routeId;
        GrainQuantity = grainQuantity;
        PlannedAt = plannedAt;
    }

    public ShipmentId Id { get; }

    public RouteId RouteId { get; }

    public long GrainQuantity { get; }

    public ShipmentStatus Status { get; private set; } = ShipmentStatus.Planned;

    public GameTime PlannedAt { get; }

    public GameTime? DepartedAt { get; private set; }

    public GameTime? ArrivedAt { get; private set; }

    public long DeliveredGrain { get; private set; }

    public long LossGrain { get; private set; }

    public long RemainingGrain => Status == ShipmentStatus.Arrived ? 0 : GrainQuantity;

    internal void MarkInTransit(GameTime departedAt)
    {
        if (Status != ShipmentStatus.Planned)
        {
            throw new InvalidOperationException($"运输单 {Id} 只能从计划状态出发。");
        }

        Status = ShipmentStatus.InTransit;
        DepartedAt = departedAt;
    }

    internal void MarkArrived(GameTime arrivedAt, long deliveredGrain, long lossGrain)
    {
        if (Status != ShipmentStatus.InTransit)
        {
            throw new InvalidOperationException($"运输单 {Id} 只能从在途状态抵达。");
        }

        if (deliveredGrain < 0 || lossGrain < 0 || deliveredGrain + lossGrain != GrainQuantity)
        {
            throw new InvalidOperationException($"运输单 {Id} 的抵达数量不满足粮食守恒。");
        }

        Status = ShipmentStatus.Arrived;
        ArrivedAt = arrivedAt;
        DeliveredGrain = deliveredGrain;
        LossGrain = lossGrain;
    }

    public ShipmentState Clone()
    {
        var clone = new ShipmentState(Id, RouteId, GrainQuantity, PlannedAt)
        {
            Status = Status,
            DepartedAt = DepartedAt,
            ArrivedAt = ArrivedAt,
            DeliveredGrain = DeliveredGrain,
            LossGrain = LossGrain,
        };
        return clone;
    }
}

/// <summary>物流领域的最小状态集合：两个或更多库存点、路线和运输单。</summary>
public sealed class LogisticsState
{
    private readonly Dictionary<StockpileId, StockpileState> _stockpiles = [];
    private readonly Dictionary<RouteId, RouteState> _routes = [];
    private readonly Dictionary<ShipmentId, ShipmentState> _shipments = [];

    public IReadOnlyDictionary<StockpileId, StockpileState> Stockpiles =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<StockpileId, StockpileState>(_stockpiles);

    public IReadOnlyDictionary<RouteId, RouteState> Routes =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<RouteId, RouteState>(_routes);

    public IReadOnlyDictionary<ShipmentId, ShipmentState> Shipments =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<ShipmentId, ShipmentState>(_shipments);

    internal void AddStockpile(StockpileState stockpile)
    {
        if (!_stockpiles.TryAdd(stockpile.Id, stockpile))
        {
            throw new InvalidOperationException($"库存点 {stockpile.Id} 已经存在。");
        }
    }

    internal void AddRoute(RouteState route)
    {
        if (!_stockpiles.ContainsKey(route.FromStockpileId) || !_stockpiles.ContainsKey(route.ToStockpileId))
        {
            throw new InvalidDataException($"路线 {route.Id} 引用了不存在的库存点。");
        }

        if (!_routes.TryAdd(route.Id, route))
        {
            throw new InvalidOperationException($"路线 {route.Id} 已经存在。");
        }
    }

    internal void AddShipment(ShipmentState shipment)
    {
        if (!_shipments.TryAdd(shipment.Id, shipment))
        {
            throw new InvalidOperationException($"运输单 {shipment.Id} 已经存在。");
        }
    }

    public long InTransitGrain(RouteId routeId) =>
        _shipments.Values
            .Where(shipment => shipment.RouteId == routeId && shipment.Status != ShipmentStatus.Arrived)
            .Sum(shipment => shipment.GrainQuantity);

    public long ReservedIncomingGrain(StockpileId stockpileId) =>
        _shipments.Values
            .Where(shipment => shipment.Status != ShipmentStatus.Arrived &&
                               _routes.TryGetValue(shipment.RouteId, out var route) &&
                               route.ToStockpileId == stockpileId)
            .Sum(shipment => shipment.GrainQuantity);

    /// <summary>库存、在途货物和已经损耗的货物之和；用于证明闭环没有凭空增减粮食。</summary>
    public long GrainLedgerTotal() =>
        checked(_stockpiles.Values.Sum(stockpile => stockpile.GrainQuantity) +
                _shipments.Values.Where(shipment => shipment.Status != ShipmentStatus.Arrived)
                    .Sum(shipment => shipment.GrainQuantity) +
                _shipments.Values.Where(shipment => shipment.Status == ShipmentStatus.Arrived)
                    .Sum(shipment => shipment.LossGrain));

    internal LogisticsState Clone()
    {
        var clone = new LogisticsState();
        foreach (var stockpile in _stockpiles.Values)
        {
            clone.AddStockpile(stockpile.Clone());
        }

        foreach (var route in _routes.Values)
        {
            clone.AddRoute(route);
        }

        foreach (var shipment in _shipments.Values)
        {
            clone.AddShipment(shipment.Clone());
        }

        return clone;
    }
}
