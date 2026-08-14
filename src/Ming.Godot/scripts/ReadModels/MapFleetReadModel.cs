using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using MingSim.Domain.Economy;
using MingSim.Domain.Realtime;
using MingSim.Simulation.Realtime;

namespace Ming.Godot.ReadModels;

/// <summary>
/// 一个库存节点的只读呈现数据：只描述界面要显示什么，不携带任何写入口。
/// AlertLevel 由 MapFleetReadModel.ComputeAlertLevel 纯函数计算（DESIGN 阈值）。
/// </summary>
public sealed partial class MapStockpileDto : RefCounted
{
    public MapStockpileDto()
        : this(string.Empty, string.Empty, 0, 0, MapFleetReadModel.NormalLevel)
    {
    }

    public MapStockpileDto(string stockpileId, string locationId, long grainQuantity, long capacity, string alertLevel)
    {
        StockpileId = stockpileId ?? throw new ArgumentNullException(nameof(stockpileId));
        LocationId = locationId ?? throw new ArgumentNullException(nameof(locationId));
        GrainQuantity = grainQuantity;
        Capacity = capacity;
        AlertLevel = string.IsNullOrWhiteSpace(alertLevel) ? MapFleetReadModel.NormalLevel : alertLevel;
    }

    public string StockpileId { get; }
    public string LocationId { get; }
    public long GrainQuantity { get; }
    public long Capacity { get; }
    public string AlertLevel { get; }
}

/// <summary>
/// 一条粮运路线的只读呈现数据；端点用库存点所在地点 id（与地图节点 id 同一套命名）。
/// </summary>
public sealed partial class MapRouteDto : RefCounted
{
    public MapRouteDto()
        : this(string.Empty, string.Empty, string.Empty, 0)
    {
    }

    public MapRouteDto(string routeId, string fromLocationId, string toLocationId, int travelHours)
    {
        RouteId = routeId ?? throw new ArgumentNullException(nameof(routeId));
        FromLocationId = fromLocationId ?? throw new ArgumentNullException(nameof(fromLocationId));
        ToLocationId = toLocationId ?? throw new ArgumentNullException(nameof(toLocationId));
        TravelHours = travelHours;
    }

    public string RouteId { get; }
    public string FromLocationId { get; }
    public string ToLocationId { get; }
    public int TravelHours { get; }
}

/// <summary>
/// 一批粮队的只读呈现数据。VisualProgress 是只读快照给出的“目标显示进度”
/// （0=起点、1=终点），由 Shipment 状态与到货时刻纯函数计算，供 MapView 做纯表现插值；
/// 这里不保存任何模拟位置，也不存在可写的在途状态。
/// </summary>
public sealed partial class MapShipmentDto : RefCounted
{
    public MapShipmentDto()
        : this(string.Empty, string.Empty, string.Empty, 0, 0)
    {
    }

    public MapShipmentDto(string shipmentId, string routeId, string status, long grainQuantity, double visualProgress)
    {
        ShipmentId = shipmentId ?? throw new ArgumentNullException(nameof(shipmentId));
        RouteId = routeId ?? throw new ArgumentNullException(nameof(routeId));
        Status = status ?? throw new ArgumentNullException(nameof(status));
        GrainQuantity = grainQuantity;
        VisualProgress = double.IsFinite(visualProgress) ? Math.Clamp(visualProgress, 0.0, 1.0) : 0.0;
    }

    public string ShipmentId { get; }
    public string RouteId { get; }
    public string Status { get; }
    public long GrainQuantity { get; }
    public double VisualProgress { get; }
}

/// <summary>世界剧本中一条静态粮运路线的定义（只读内容，非动态状态）。</summary>
public sealed record StaticRoute(string RouteId, string FromStockpileId, string ToStockpileId, int TravelHours);

/// <summary>
/// 粮运地图的不可变只读模型（Presenter 产物，doc 09 §15）：把
/// RealtimeReadModel 的库存/粮队/时间与剧本静态路线定义折叠成 Godot 表现层
/// 直接消费的 DTO。MapView 只接收本对象，永远拿不到 Simulation 可写类型。
/// 插值所需的目标进度是 (Status, GameTime, 到货时刻, 行程时长) 的纯函数；
/// 表现层不缓存任何可写状态，也不推进时间。
/// </summary>
public sealed partial class MapFleetReadModel : RefCounted
{
    public const string NormalLevel = "Normal";
    public const string WarningLevel = "Warning";
    public const string CriticalLevel = "Critical";

    private readonly ReadOnlyCollection<MapStockpileDto> _stockpiles;
    private readonly ReadOnlyCollection<MapRouteDto> _routes;
    private readonly ReadOnlyCollection<MapShipmentDto> _shipments;

    public MapFleetReadModel()
        : this(string.Empty, 0, string.Empty, Array.Empty<MapStockpileDto>(), Array.Empty<MapRouteDto>(), Array.Empty<MapShipmentDto>())
    {
    }

    public MapFleetReadModel(
        string gameTimeLabel,
        long worldVersion,
        string sourceNotice,
        IEnumerable<MapStockpileDto> stockpiles,
        IEnumerable<MapRouteDto> routes,
        IEnumerable<MapShipmentDto> shipments)
    {
        ArgumentNullException.ThrowIfNull(stockpiles);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(shipments);
        GameTimeLabel = gameTimeLabel ?? string.Empty;
        WorldVersion = worldVersion;
        SourceNotice = sourceNotice ?? string.Empty;
        _stockpiles = new ReadOnlyCollection<MapStockpileDto>(stockpiles.ToArray());
        _routes = new ReadOnlyCollection<MapRouteDto>(routes.ToArray());
        _shipments = new ReadOnlyCollection<MapShipmentDto>(shipments.ToArray());
    }

    public string GameTimeLabel { get; }
    public long WorldVersion { get; }
    public string SourceNotice { get; }
    public IReadOnlyList<MapStockpileDto> Stockpiles => _stockpiles;
    public IReadOnlyList<MapRouteDto> Routes => _routes;
    public IReadOnlyList<MapShipmentDto> Shipments => _shipments;

    /// <summary>
    /// 从权威只读模型构建粮运呈现快照。staticRoutes 缺省时从剧本 world.json 读取
    /// 静态路线目录（路线是内容定义，ReadModel 只给出粮队引用的 RouteId）。
    /// 无法解析路线目录时抛异常（fail-closed）：运行时本身就来自同一剧本，
    /// 缺失说明内容契约已损坏，不应静默画半张地图。
    /// </summary>
    public static MapFleetReadModel Create(RealtimeReadModel model, IReadOnlyList<StaticRoute>? staticRoutes = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var routes = staticRoutes ?? LoadStaticRoutes();

        var stockpileLocations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var stockpile in model.Stockpiles)
            stockpileLocations[stockpile.Id.Value] = stockpile.LocationId.Value;

        var routeDtos = new List<MapRouteDto>();
        foreach (var route in routes)
        {
            // 引用不存在的库存点说明内容与 ReadModel 不一致；跳过而不是画悬空路线。
            if (!stockpileLocations.TryGetValue(route.FromStockpileId, out var from) ||
                !stockpileLocations.TryGetValue(route.ToStockpileId, out var to))
                continue;
            routeDtos.Add(new MapRouteDto(route.RouteId, from, to, route.TravelHours));
        }

        var stockpileDtos = new List<MapStockpileDto>();
        foreach (var stockpile in model.Stockpiles)
        {
            stockpileDtos.Add(new MapStockpileDto(
                stockpile.Id.Value,
                stockpile.LocationId.Value,
                stockpile.GrainQuantity,
                stockpile.Capacity,
                ComputeAlertLevel(
                    stockpile.GrainQuantity,
                    stockpile.Capacity,
                    model.Scenario.DailyGrainDemand,
                    model.Scenario.FrontStockpileId == stockpile.Id)));
        }

        var travelHoursByRoute = routes.ToDictionary(route => route.RouteId, route => route.TravelHours, StringComparer.Ordinal);
        var shipmentDtos = new List<MapShipmentDto>();
        foreach (var shipment in model.Shipments)
        {
            shipmentDtos.Add(new MapShipmentDto(
                shipment.Id.Value,
                shipment.RouteId.Value,
                shipment.Status.ToString(),
                shipment.GrainQuantity,
                ComputeVisualProgress(
                    shipment.Status,
                    model.GameTime,
                    FindArrivalTime(model, shipment.Id.Value),
                    travelHoursByRoute.GetValueOrDefault(shipment.RouteId.Value, 0))));
        }

        return new MapFleetReadModel(
            model.GameTime.Value.ToString("yyyy-MM-dd HH:mm"),
            model.WorldVersion,
            "RealtimeReadModel · 只读快照",
            stockpileDtos,
            routeDtos,
            shipmentDtos);
    }

    /// <summary>
    /// 只读的库存告急级别（DESIGN 阈值）：
    /// 零库存或前线不足 3 日耗、不足 10% 仓容 → Critical；前线不足 7 日耗、
    /// 不足 25% 仓容 → Warning；其余 Normal。纯函数，无隐藏状态。
    /// </summary>
    public static string ComputeAlertLevel(long grainQuantity, long capacity, int dailyDemand, bool isFrontStockpile)
    {
        if (grainQuantity <= 0) return CriticalLevel;
        if (isFrontStockpile && dailyDemand > 0)
        {
            if (grainQuantity < dailyDemand * 3L) return CriticalLevel;
            if (grainQuantity < dailyDemand * 7L) return WarningLevel;
        }
        if (capacity > 0)
        {
            if (grainQuantity < capacity / 10) return CriticalLevel;
            if (grainQuantity < capacity / 4) return WarningLevel;
        }
        return NormalLevel;
    }

    /// <summary>
    /// 在途粮队的显示进度 = 1 - 剩余行程 / 总行程，是 (Status, GameTime, 到货时刻,
    /// 行程时长) 的纯函数。Planned=起点、Arrived=终点；没有任何快照能改变它的输出
    /// 以外的表现状态（表现层只对这个目标值做视觉插值）。
    /// </summary>
    public static double ComputeVisualProgress(ShipmentStatus status, GameTime gameTime, GameTime? arrivalAt, int travelHours)
    {
        if (status == ShipmentStatus.Planned) return 0.0;
        if (status == ShipmentStatus.Arrived) return 1.0;
        if (arrivalAt is null || travelHours <= 0) return 0.0;
        var remainingHours = (arrivalAt.Value.Value - gameTime.Value).TotalHours;
        return Math.Clamp(1.0 - remainingHours / travelHours, 0.0, 1.0);
    }

    /// <summary>只供自动验收注入固定样本；正式接线应调用 Create(RealtimeReadModel)。</summary>
    public static MapFleetReadModel CreateAcceptanceSample(int shipmentCount)
    {
        if (shipmentCount < 0)
            throw new ArgumentOutOfRangeException(nameof(shipmentCount), "粮队数量不能为负数。");

        var gameTime = new GameTime(new DateTimeOffset(1629, 1, 5, 0, 0, 0, TimeSpan.Zero));
        var inTransitArrival = new GameTime(new DateTimeOffset(1629, 1, 7, 0, 0, 0, TimeSpan.Zero));
        const int dailyDemand = 300;

        var stockpiles = new List<MapStockpileDto>
        {
            new("sp-beijing", "beijing", 10000, 30000, ComputeAlertLevel(10000, 30000, dailyDemand, false)),
            new("sp-tongzhou", "tongzhou", 0, 5000, ComputeAlertLevel(0, 5000, dailyDemand, false)),
            new("sp-shanhaiguan", "shanhaiguan", 0, 5000, ComputeAlertLevel(0, 5000, dailyDemand, false)),
            new("sp-ningyuan", "ningyuan", 5400, 30000, ComputeAlertLevel(5400, 30000, dailyDemand, true)),
            new("sp-juehuadao", "juehuadao", 0, 7000, ComputeAlertLevel(0, 7000, dailyDemand, false)),
            new("sp-dengzhou", "dengzhou", 14000, 30000, ComputeAlertLevel(14000, 30000, dailyDemand, false)),
        };

        var routes = new List<MapRouteDto>
        {
            new("route-beijing-tongzhou", "beijing", "tongzhou", 48),
            new("route-tongzhou-shanhaiguan", "tongzhou", "shanhaiguan", 120),
            new("route-shanhaiguan-ningyuan", "shanhaiguan", "ningyuan", 120),
            new("route-dengzhou-juehuadao", "dengzhou", "juehuadao", 96),
            new("route-juehuadao-ningyuan", "juehuadao", "ningyuan", 96),
        };

        // 固定三档样本：在途(0.6)、已抵达(1.0)、计划(0.0)；超过 3 批循环复用，数值确定可重放。
        var shipments = new List<MapShipmentDto>();
        var samples = new[]
        {
            (Status: ShipmentStatus.InTransit, Route: "route-shanhaiguan-ningyuan", Grain: 5000L, Arrival: (GameTime?)inTransitArrival, Travel: 120),
            (Status: ShipmentStatus.Arrived, Route: "route-dengzhou-juehuadao", Grain: 6650L, Arrival: (GameTime?)null, Travel: 96),
            (Status: ShipmentStatus.Planned, Route: "route-beijing-tongzhou", Grain: 5000L, Arrival: (GameTime?)null, Travel: 48),
        };
        for (var index = 0; index < shipmentCount; index++)
        {
            var sample = samples[index % samples.Length];
            shipments.Add(new MapShipmentDto(
                $"shipment-acceptance-{index + 1}",
                sample.Route,
                sample.Status.ToString(),
                sample.Grain,
                ComputeVisualProgress(sample.Status, gameTime, sample.Arrival, sample.Travel)));
        }

        return new MapFleetReadModel(
            gameTime.Value.ToString("yyyy-MM-dd HH:mm"),
            42,
            "acceptance · DESIGN 数值",
            stockpiles,
            routes,
            shipments);
    }

    /// <summary>从剧本 world.json 读取静态粮运路线目录（id/端点库存/行程时长）。</summary>
    public static IReadOnlyList<StaticRoute> LoadStaticRoutes(string? worldJsonPath = null)
    {
        var path = worldJsonPath ?? ResolveWorldJsonPath();
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var routes = new List<StaticRoute>();
        foreach (var route in document.RootElement.GetProperty("routes").EnumerateArray())
        {
            routes.Add(new StaticRoute(
                GetString(route, "id"),
                GetString(route, "fromStockpileId"),
                GetString(route, "toStockpileId"),
                route.GetProperty("travelHours").GetInt32()));
        }
        if (routes.Count == 0)
            throw new InvalidDataException("世界剧本没有粮运路线内容。");
        return routes;
    }

    /// <summary>在调度队列里找该粮队的到货事件时刻（正常与容量重试两种事件名都匹配）。</summary>
    private static GameTime? FindArrivalTime(RealtimeReadModel model, string shipmentId)
    {
        // 一次运输可能因仓容不足重试（retry-）或协作延误（-delayed）而改期，原到货事件
        // 仍留在调度队列里且更早到期；因此必须取同一粮队所有到货事件中“最晚”的一次，
        // 它才是当前权威的预期到货时刻（ScheduledActions 已按到期时间升序）。
        GameTime? latest = null;
        foreach (var scheduled in model.ScheduledActions)
        {
            if (scheduled.EventType != "ShipmentArrival") continue;
            if (!IsShipmentArrivalEvent(scheduled.EventId, shipmentId)) continue;
            if (latest is null || scheduled.DueGameTime > latest.Value)
                latest = scheduled.DueGameTime;
        }
        return latest;
    }

    /// <summary>该粮队的到货事件 id 匹配（正常 / 重试 / 延误三种命名）。</summary>
    private static bool IsShipmentArrivalEvent(string eventId, string shipmentId)
    {
        const string prefix = "shipment-arrival-";
        if (eventId == prefix + shipmentId) return true;
        if (eventId.StartsWith(prefix + "retry-" + shipmentId + "-", StringComparison.Ordinal)) return true;
        return eventId.StartsWith(prefix + shipmentId + "-", StringComparison.Ordinal);
    }

    private static string ResolveWorldJsonPath()
    {
        const string relative = "content/scenarios/ming_1629/world.json";
        if (File.Exists(relative)) return relative;
        var fromRes = ProjectSettings.GlobalizePath("res://../../content/scenarios/ming_1629/world.json");
        if (File.Exists(fromRes)) return fromRes;
        throw new FileNotFoundException($"找不到宁远 1629 剧本：已尝试 {relative} 与 {fromRes}。", relative);
    }

    private static string GetString(JsonElement element, string property) =>
        element.GetProperty(property).GetString()
        ?? throw new InvalidDataException($"剧本字段 {property} 不能为空。");
}
