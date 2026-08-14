using Godot;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ming.Godot;

/// <summary>
/// 只读地图呈现层：它从 map-manifest.json 读取底图、历史层、路线和可选节点。
/// 这个类不认识 WorldState，也不推进时间；点击只回报 place id 给页面壳。
/// </summary>
public partial class MapView : Control
{
    private readonly struct MapViewportTransform
    {
        public MapViewportTransform(Rect2 contentRect, float scale, Transform2D mapToViewport)
        {
            ContentRect = contentRect;
            Scale = scale;
            MapToViewport = mapToViewport;
        }

        public Rect2 ContentRect { get; }
        public float Scale { get; }
        public Transform2D MapToViewport { get; }
        public Transform2D ViewportToMap => MapToViewport.AffineInverse();
        public Rect2 ContentViewportRect => new(
            MapToViewport * ContentRect.Position,
            ContentRect.Size * Scale);
    }

    private readonly struct LabelLayout
    {
        public LabelLayout(PlaceDefinition place, Vector2 baseline, int fontSize)
        {
            Place = place;
            Baseline = baseline;
            FontSize = fontSize;
        }

        public PlaceDefinition Place { get; }
        public Vector2 Baseline { get; }
        public int FontSize { get; }
    }

    /// <summary>
    /// 地图只保存呈现模式，不保存或修改任何世界状态。
    /// DeskOverview 是御案上的静态舆图；StrategicMap 是 MainUi 展开后的全屏策略地图。
    /// </summary>
    public enum MapMode
    {
        DeskOverview,
        StrategicMap
    }

    private const float MinimumStrategicZoom = 1.0f;
    private const float MaximumStrategicZoom = 4.0f;
    private const int AuthoritativeCanvasWidth = 2400;
    private const int AuthoritativeCanvasHeight = 1600;

    private readonly record struct PresentationRasterContract(
        string Path,
        string Sha256,
        int Width,
        int Height);

    private sealed class Manifest
    {
        public Dictionary<string, string>? Assets { get; set; }
        [JsonPropertyName("asset_sha256")]
        public Dictionary<string, string>? AssetSha256 { get; set; }
        [JsonPropertyName("canvas")]
        public CanvasDefinition? Canvas { get; set; }
        [JsonPropertyName("historical_content")]
        public HistoricalContent? HistoricalContent { get; set; }
        [JsonPropertyName("research_baseline")]
        public ResearchBaseline? ResearchBaseline { get; set; }
        public List<PlaceDefinition>? Places { get; set; }
        public List<RouteDefinition>? Routes { get; set; }
    }

    private sealed class CanvasDefinition
    {
        public int Width { get; set; }
        public int Height { get; set; }
        [JsonPropertyName("content_rect")]
        public List<double>? ContentRect { get; set; }
    }

    private sealed class HistoricalContent
    {
        [JsonPropertyName("snapshot_date")]
        public string? SnapshotDate { get; set; }
        public string? Warning { get; set; }
        [JsonPropertyName("claim_status")]
        public string? ClaimStatus { get; set; }
        [JsonPropertyName("geometry_role")]
        public string? GeometryRole { get; set; }
    }

    private sealed class ResearchBaseline
    {
        [JsonPropertyName("geometry_depict_date")]
        public string? GeometryDepictDate { get; set; }
        [JsonPropertyName("historical_fit_status")]
        public string? HistoricalFitStatus { get; set; }
        public bool Visible { get; set; }
    }

    private sealed class PlaceDefinition
    {
        public string? Id { get; set; }
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }
        [JsonPropertyName("name_zh")]
        public string? NameZh { get; set; }
        [JsonPropertyName("review_status")]
        public string? ReviewStatus { get; set; }
        [JsonPropertyName("coordinate_epoch")]
        public string? CoordinateEpoch { get; set; }
        [JsonPropertyName("evidence_status")]
        public string? EvidenceStatus { get; set; }
        [JsonPropertyName("historical_site_status")]
        public string? HistoricalSiteStatus { get; set; }
        [JsonPropertyName("map_representation")]
        public string? MapRepresentation { get; set; }
        [JsonPropertyName("map_x")]
        public double MapX { get; set; }
        [JsonPropertyName("map_y")]
        public double MapY { get; set; }
    }

    private sealed class RouteDefinition
    {
        [JsonPropertyName("from_place_id")]
        public string? FromPlaceId { get; set; }
        [JsonPropertyName("to_place_id")]
        public string? ToPlaceId { get; set; }
        public List<MapPoint>? Points { get; set; }
        public string? Id { get; set; }
        [JsonPropertyName("review_status")]
        public string? ReviewStatus { get; set; }
        [JsonPropertyName("evidence_status")]
        public string? EvidenceStatus { get; set; }
        [JsonPropertyName("claim_status")]
        public string? ClaimStatus { get; set; }
    }

    private sealed class MapPoint
    {
        [JsonPropertyName("map_x")]
        public double MapX { get; set; }
        [JsonPropertyName("map_y")]
        public double MapY { get; set; }
    }

    private readonly Dictionary<string, PlaceDefinition> _placesById = new(StringComparer.Ordinal);
    private Manifest? _manifest;
    private Texture2D? _physicalTexture;
    private Texture2D? _historyTexture;
    private float _zoom = 1.0f;
    private Vector2 _pan = Vector2.Zero;
    private string _selectedPlaceId = "beijing";
    private bool _routesVisible = true;
    private bool _historicalLayerVisible = true;
    private bool _isDragging;
    private Vector2 _dragStart;
    private string _loadError = "地图清单不可用；已停止显示。";
    private MapMode _mode = MapMode.DeskOverview;
    // UI 纸色图是玩家真正看到的像素，不能只验证正式研究底图后再偷偷替换。
    // 路径、字节 SHA 和固定画布都在代码侧构成权威契约；manifest 不能自报一套新尺寸/哈希来绕过。
    private static readonly Dictionary<string, PresentationRasterContract> PresentationPhysicalTextures = new(StringComparer.Ordinal)
    {
        ["res://assets/maps/generated/ming_1629/map-manifest.json"] = new(
            "res://assets/ui/generated/ming_ui_v2/maps/ming_1629-physical.png",
            "2963036ed798a2d3c1713008a438df75af7b3d63c6db161138010c7cbdb0f0a2",
            AuthoritativeCanvasWidth,
            AuthoritativeCanvasHeight),
        ["res://assets/maps/generated/ming_1629_liaoxi/map-manifest.json"] = new(
            "res://assets/ui/generated/ming_ui_v2/maps/ming_1629_liaoxi-physical.png",
            "d5591ab2d2ba7efa2bbbe05b663e4a760a4a634f847da0104999f10541bf496f",
            AuthoritativeCanvasWidth,
            AuthoritativeCanvasHeight)
    };

    public event Action<string>? PlaceSelected;
    public event Action? ExitRequested;
    public string ManifestPath { get; private set; } = "";
    public bool LoadedFromManifest => _manifest != null && _physicalTexture != null && _historyTexture != null;
    public bool HistoricalLayerVisible => _historicalLayerVisible;
    public bool RoutesVisible => _routesVisible;
    public float Zoom => _zoom;
    public string SelectedPlaceId => _selectedPlaceId;
    public string SelectedPlaceSummary => _placesById.TryGetValue(_selectedPlaceId, out var place)
        ? FormatPlaceSemanticSummary(place)
        : "OPEN · 未选择有效地图节点";
    // 合并后正式清单为 DESIGN 准入草稿（PR #9 契约）：语义层区分 DESIGN/INFERENCE/FACT。
    public string SemanticLegend => "空心环=OPEN/modern_anchor/approximate_point；虚线=INFERENCE；实线=FACT；DESIGN=设计准入草稿";
    public string RouteSemanticLegend => BuildRouteSemanticLegend();
    public string GeometryDepictDate => _manifest?.ResearchBaseline?.GeometryDepictDate ?? "OPEN";
    public string SnapshotDate => _manifest?.HistoricalContent?.SnapshotDate ?? "OPEN";
    public string HistoricalWarning => _manifest?.HistoricalContent?.Warning ?? _loadError;
    public string LoadError => _loadError;
    public Vector2 Pan => _pan;
    public MapMode Mode => _mode;
    public bool IsStrategicView => _mode == MapMode.StrategicMap;
    public float MinimumZoom => MinimumStrategicZoom;
    public float MaximumZoom => IsStrategicView ? MaximumStrategicZoom : MinimumStrategicZoom;
    public Rect2 ContentViewportRect => CalculateContentViewportRect();
    public int PlaceCount => _manifest?.Places?.Count ?? 0;
    public int RouteCount => _manifest?.Routes?.Count ?? 0;
    public int VisiblePlaceCount => _manifest?.Places?.Count(ShouldDrawPlace) ?? 0;
    public int VisibleLabelCount => TryGetMapViewportTransform(out var transform)
        ? BuildVisibleLabelLayout(transform).Count
        : 0;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        LoadManifest("res://assets/maps/generated/ming_1629/map-manifest.json");
        QueueRedraw();
    }

    public void LoadManifest(string manifestPath)
    {
        ManifestPath = manifestPath;
        _manifest = null;
        _placesById.Clear();
        _physicalTexture = null;
        _historyTexture = null;
        _loadError = "地图清单不可用；已停止显示。";

        try
        {
            var filePath = ProjectSettings.GlobalizePath(manifestPath);
            if (!File.Exists(filePath))
            {
                GD.PushWarning($"地图清单不存在，已安全关闭地图层：{manifestPath}");
                return;
            }

            var json = File.ReadAllText(filePath);
            var candidate = JsonSerializer.Deserialize<Manifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            var validationError = "地图清单不可用；已停止显示。";
            if (candidate == null || !ValidateManifest(candidate, out validationError))
            {
                _loadError = validationError;
                GD.PushWarning($"地图清单校验失败，已安全关闭地图层：{_loadError}");
                return;
            }

            var physicalPath = candidate.Assets!["physical_base"];
            var historyPath = candidate.Assets!["history_overlay"];
            // 正式地图及其 manifest 始终由地图构建器校验；UI 纸本处理位于独立派生路径，
            // 不反写、不污染 assets/maps/generated/** 的研究与构建契约。
            var presentationPhysicalPath = physicalPath;
            if (PresentationPhysicalTextures.TryGetValue(manifestPath, out var presentationContract))
            {
                if (!ValidateRasterAssetAgainstFixedContract(presentationContract, "界面显示底图", out validationError))
                {
                    _loadError = validationError;
                    GD.PushWarning($"界面显示底图校验失败，已安全关闭地图层：{_loadError}");
                    return;
                }
                presentationPhysicalPath = presentationContract.Path;
            }
            var physicalTexture = GD.Load<Texture2D>(presentationPhysicalPath);
            var historyTexture = GD.Load<Texture2D>(historyPath);
            if (physicalTexture == null || historyTexture == null)
            {
                _loadError = "地图纹理不可用；已停止显示。";
                GD.PushWarning($"地图纹理加载失败，已安全关闭地图层：{manifestPath}");
                return;
            }

            if (physicalTexture.GetWidth() != candidate.Canvas!.Width || physicalTexture.GetHeight() != candidate.Canvas.Height ||
                historyTexture.GetWidth() != candidate.Canvas.Width || historyTexture.GetHeight() != candidate.Canvas.Height)
            {
                _loadError = "地图纹理尺寸与清单画布不一致；已停止显示。";
                GD.PushWarning($"地图纹理尺寸校验失败，已安全关闭地图层：{manifestPath}");
                return;
            }

            var candidatePlaces = candidate.Places!.ToDictionary(place => place.Id!, StringComparer.Ordinal);
            var candidateSelectedPlaceId = candidatePlaces.ContainsKey(_selectedPlaceId)
                ? _selectedPlaceId
                : candidate.Places![0].Id!;

            // 候选清单、两张必需图片的文件哈希/尺寸、导入纹理尺寸和全部索引均通过后，
            // 才一次性发布给绘制与输入路径；此前的任何失败都只会留下中性错误层。
            _manifest = candidate;
            _physicalTexture = physicalTexture;
            _historyTexture = historyTexture;
            foreach (var pair in candidatePlaces)
            {
                _placesById.Add(pair.Key, pair.Value);
            }
            _selectedPlaceId = candidateSelectedPlaceId;
            _loadError = "";
            ClampPan();
        }
        catch (Exception error)
        {
            _loadError = "地图清单不可解析；已停止显示。";
            GD.PushWarning($"地图清单加载失败，已安全关闭地图层：{error.Message}");
        }
        finally
        {
            QueueRedraw();
        }
    }

    public void SelectPlace(string placeId)
    {
        if (!_placesById.ContainsKey(placeId)) return;
        _selectedPlaceId = placeId;
        PlaceSelected?.Invoke(placeId);
        QueueRedraw();
    }

    public void ToggleHistoricalLayer()
    {
        _historicalLayerVisible = !_historicalLayerVisible;
        QueueRedraw();
    }

    public void ToggleRoutes()
    {
        _routesVisible = !_routesVisible;
        QueueRedraw();
    }

    public string GetRouteSemanticSummary(string routeId)
    {
        var route = _manifest?.Routes?.FirstOrDefault(candidate => string.Equals(candidate.Id, routeId, StringComparison.Ordinal));
        if (route == null) return "OPEN · 未找到路线";
        return $"{RouteKnowledgeClass(route)} · review_status={route.ReviewStatus} · evidence_status={route.EvidenceStatus} · claim_status={route.ClaimStatus}";
    }

    public void ResetView()
    {
        _zoom = MinimumStrategicZoom;
        _pan = Vector2.Zero;
        ClampPan();
        QueueRedraw();
    }

    /// <summary>
    /// 进入全屏策略地图。这里只改变 MapView 的呈现状态，MainUi 负责实际布局和转场。
    /// </summary>
    public void EnterStrategicView()
    {
        if (IsStrategicView) return;
        _mode = MapMode.StrategicMap;
        _zoom = Mathf.Clamp(_zoom, MinimumStrategicZoom, MaximumStrategicZoom);
        _pan = Vector2.Zero;
        _isDragging = false;
        ClampPan();
        QueueRedraw();
    }

    /// <summary>
    /// 回到御案桌面舆图。桌面态固定为最远视野，避免桌面卷轴残留全屏缩放和平移。
    /// </summary>
    public void ExitStrategicView()
    {
        if (!IsStrategicView) return;
        _mode = MapMode.DeskOverview;
        _zoom = MinimumStrategicZoom;
        _pan = Vector2.Zero;
        _isDragging = false;
        ClampPan();
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex == MouseButton.WheelUp && mouseButton.Pressed)
            {
                // 桌面卷轴本身不缩放；MainUi 可监听滚轮后调用 EnterStrategicView 并执行全屏转场。
                if (!IsStrategicView) return;
                _zoom = Mathf.Min(MaximumStrategicZoom, _zoom + 0.2f);
                ClampPan();
                QueueRedraw();
                AcceptEvent();
            }
            else if (mouseButton.ButtonIndex == MouseButton.WheelDown && mouseButton.Pressed)
            {
                if (!IsStrategicView) return;
                if (_zoom <= MinimumStrategicZoom + 0.001f)
                {
                    ExitRequested?.Invoke();
                    AcceptEvent();
                    return;
                }
                // 1.0 是“完整内容刚好装入视口”的最远视图。再缩小只会露出清单之外的蓝海。
                _zoom = Mathf.Max(MinimumStrategicZoom, _zoom - 0.2f);
                ClampPan();
                QueueRedraw();
                AcceptEvent();
            }
            else if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                if (mouseButton.Pressed)
                {
                    // 两种模式都保留点击碰撞；只有战略态允许拖动改变平移。
                    _isDragging = true;
                    _dragStart = mouseButton.Position;
                    AcceptEvent();
                }
                else
                {
                    var wasClick = _isDragging && mouseButton.Position.DistanceTo(_dragStart) < 8;
                    _isDragging = false;
                    if (wasClick) SelectPlaceAt(mouseButton.Position);
                    AcceptEvent();
                }
            }
        }
        else if (@event is InputEventMouseMotion motion && _isDragging)
        {
            if (IsStrategicView)
            {
                _pan += motion.Relative;
                ClampPan();
                QueueRedraw();
            }
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("#242A23"), true);
        if (!LoadedFromManifest || _manifest == null)
        {
            DrawString(ThemeDB.FallbackFont, new Vector2(26, 48), _loadError, HorizontalAlignment.Left, -1, 18, new Color("#F4E9D8"));
            return;
        }

        if (!TryGetMapViewportTransform(out var mapTransform))
        {
            DrawString(ThemeDB.FallbackFont, new Vector2(26, 48), "地图变换不可用；已停止显示。", HorizontalAlignment.Left, -1, 18, new Color("#F4E9D8"));
            return;
        }
        var transform = mapTransform.MapToViewport;

        DrawTextureTransform(_physicalTexture!, transform, new Color("#D8CFAD"));
        // 矿物青绿只给证据层一层克制的罩染，不让 GIS 蓝色成为主视觉。
        if (_historicalLayerVisible) DrawTextureTransform(_historyTexture!, transform, new Color(0.72f, 0.84f, 0.76f, 0.82f));

        if (_routesVisible)
        {
            foreach (var route in _manifest.Routes!)
            {
                for (var i = 1; i < route.Points!.Count; i++)
                {
                    var from = transform * ToVector(route.Points[i - 1]);
                    var to = transform * ToVector(route.Points[i]);
                    // DESIGN 准入草稿用土色虚线，不冒充 FACT 实线；INFERENCE 用朱红虚线。
                    if (string.Equals(route.ClaimStatus, "design_topology", StringComparison.Ordinal))
                        DrawDashedLine(from, to, new Color(0.45f, 0.42f, 0.36f, 0.72f), Mathf.Clamp(1.25f * _zoom, 1.1f, 2.0f), 5.0f, true);
                    else if (IsInferenceRoute(route))
                        DrawDashedLine(from, to, new Color(0.55f, 0.19f, 0.16f, 0.76f), Mathf.Clamp(1.25f * _zoom, 1.1f, 2.0f), 7.0f, true);
                    else
                        DrawLine(from, to, new Color("#6F1F1B"), Mathf.Clamp(1.55f * _zoom, 1.4f, 2.5f), true);
                }
            }
        }

        foreach (var place in _manifest.Places!)
        {
            if (!ShouldDrawPlace(place)) continue;
            var point = transform * new Vector2((float)place.MapX, (float)place.MapY);
            var selected = place.Id == _selectedPlaceId;
            // 标记与地图共同缩放，但限制屏幕尺寸；远景不会互相覆盖，近景仍可辨认。
            var radius = Mathf.Clamp((selected ? 4.4f : 3.1f) * _zoom, selected ? 4.4f : 3.1f, selected ? 8.2f : 5.8f);
            if (IsApproximatePoint(place))
            {
                // 空心双环明确表示“现代锚点上的近似位置”，不能误读为精确城址。
                DrawArc(point, radius + 1.8f, 0, Mathf.Tau, 28, new Color(0.12f, 0.10f, 0.07f, 0.88f), 2.0f, true);
                DrawArc(point, radius - 0.8f, 0, Mathf.Tau, 24, selected ? new Color("#D5A54B") : new Color("#6C9288"), 1.35f, true);
            }
            else
            {
                // 只有未来清单明确声明的精确事实点才使用实心标记。
                DrawCircle(point, radius + 1.8f, new Color(0.12f, 0.10f, 0.07f, 0.88f));
                DrawCircle(point, radius, selected ? new Color("#A83B32") : new Color("#E7D9B6"));
            }
        }

        foreach (var layout in BuildVisibleLabelLayout(mapTransform))
        {
            var place = layout.Place;
            var selected = place.Id == _selectedPlaceId;
            var ink = selected ? new Color("#7E231F") : new Color("#26241F");
            DrawStringOutline(ThemeDB.FallbackFont, layout.Baseline, place.NameZh!, HorizontalAlignment.Left, -1, layout.FontSize, 1, new Color(0.90f, 0.85f, 0.70f, 0.72f));
            DrawString(ThemeDB.FallbackFont, layout.Baseline, place.NameZh!, HorizontalAlignment.Left, -1, layout.FontSize, ink);
        }

        var legendWidth = Mathf.Max(120f, Mathf.Min(Size.X - 36f, 1180f));
        DrawStyleBox(MakePanel(new Color(0.86f, 0.80f, 0.63f, 0.90f), new Color("#5E5040"), 1, 2), new Rect2(18, Size.Y - 96, legendWidth, 78));
        var geometryDate = string.IsNullOrWhiteSpace(GeometryDepictDate) ? "OPEN" : GeometryDepictDate;
        var snapshotDate = string.IsNullOrWhiteSpace(SnapshotDate) ? "OPEN" : SnapshotDate;
        DrawString(ThemeDB.FallbackFont, new Vector2(32, Size.Y - 73), $"{SemanticLegend} · 呈现层 {geometryDate}/{snapshotDate}", HorizontalAlignment.Left, -1, 12, new Color("#332B20"));
        DrawString(ThemeDB.FallbackFont, new Vector2(32, Size.Y - 52), RouteSemanticLegend, HorizontalAlignment.Left, -1, 11, new Color("#332B20"));
        DrawString(ThemeDB.FallbackFont, new Vector2(32, Size.Y - 31), SelectedPlaceSummary, HorizontalAlignment.Left, -1, 11, new Color("#332B20"));
    }

    private static Vector2 ToVector(MapPoint point) => new((float)point.MapX, (float)point.MapY);

    private static Transform2D MakeTransform(float scale, Vector2 origin) => new(
        new Vector2(scale, 0),
        new Vector2(0, scale),
        origin);

    private void SelectPlaceAt(Vector2 position)
    {
        if (_manifest == null || !TryGetMapViewportTransform(out var transform)) return;
        var mapPoint = transform.ViewportToMap * position;
        var hitRadius = 14f / transform.Scale;
        var nearest = "";
        var nearestDistance = hitRadius;
        foreach (var place in _manifest.Places!)
        {
            // 交互候选与绘制 LOD 完全一致；未画出的节点绝不可能抢走点击。
            if (!ShouldDrawPlace(place)) continue;
            var distance = mapPoint.DistanceTo(new Vector2((float)place.MapX, (float)place.MapY));
            if (distance < nearestDistance)
            {
                nearest = place.Id;
                nearestDistance = distance;
            }
        }
        if (!string.IsNullOrEmpty(nearest)) SelectPlace(nearest);
    }

    private bool ShouldDrawPlace(PlaceDefinition place)
    {
        if (_zoom >= 1.8f) return true;
        if (_zoom >= 1.3f) return place.Kind is "capital" or "pass" or "fortress";
        return place.Kind is "capital" or "pass";
    }

    private bool ShouldDrawLabel(PlaceDefinition place)
    {
        // CK3 式层级：最远层级完全不写城镇名；中层只写首都/关隘；近层才写前线与交通节点。
        if (!IsStrategicView) return false;
        if (_zoom < 1.35f) return false;
        if (_zoom < 1.8f) return place.Kind is "capital" or "pass";
        if (_zoom < 2.5f) return place.Kind is "capital" or "pass" or "fortress";
        return true;
    }

    private static int PlacePriority(PlaceDefinition place) => place.Kind switch
    {
        "capital" => 0,
        "pass" => 1,
        "fortress" => 2,
        "port" => 3,
        "transport_hub" => 4,
        _ => 5
    };

    private List<LabelLayout> BuildVisibleLabelLayout(MapViewportTransform transform)
    {
        var layouts = new List<LabelLayout>();
        if (_manifest?.Places == null || Size.X <= 12 || Size.Y <= 12) return layouts;

        var occupied = new List<Rect2>();
        var viewportRect = new Rect2(6, 6, Size.X - 12, Size.Y - 12);
        var candidates = _manifest.Places
            .Where(ShouldDrawLabel)
            .OrderBy(PlacePriority);

        foreach (var place in candidates)
        {
            var point = transform.MapToViewport * new Vector2((float)place.MapX, (float)place.MapY);
            var selected = place.Id == _selectedPlaceId;
            var fontSize = Mathf.RoundToInt(Mathf.Clamp((selected ? 12f : 10f) * Mathf.Sqrt(_zoom), 10f, selected ? 16f : 14f));
            var textSize = ThemeDB.FallbackFont.GetStringSize(place.NameZh!, HorizontalAlignment.Left, -1, fontSize);
            var preferred = place.Id switch
            {
                "beijing" => new Vector2(-18, -18),
                "tongzhou" => new Vector2(14, 22),
                "shanhaiguan" => new Vector2(14, 20),
                "ningyuan" => new Vector2(-48, -18),
                "jinzhou" => new Vector2(16, -18),
                "dengzhou" => new Vector2(-14, 24),
                _ => new Vector2(12, -10)
            };
            var alternatives = new[]
            {
                preferred,
                new Vector2(12, -10),
                new Vector2(12, textSize.Y + 8),
                new Vector2(-textSize.X - 12, -10),
                new Vector2(-textSize.X - 12, textSize.Y + 8),
                new Vector2(-textSize.X / 2f, -16),
                new Vector2(-textSize.X / 2f, textSize.Y + 15)
            };

            foreach (var offset in alternatives)
            {
                var baseline = point + offset;
                var labelRect = new Rect2(baseline.X - 2, baseline.Y - textSize.Y, textSize.X + 4, textSize.Y + 3);
                if (!viewportRect.Encloses(labelRect)) continue;
                if (occupied.Any(rect => rect.Intersects(labelRect))) continue;
                occupied.Add(labelRect);
                layouts.Add(new LabelLayout(place, baseline, fontSize));
                break;
            }
        }

        return layouts;
    }

    private bool TryGetMapViewportTransform(out MapViewportTransform transform)
    {
        transform = default;
        if (_manifest?.Canvas?.ContentRect is not { Count: 4 } contentRect || Size.X <= 0 || Size.Y <= 0)
            return false;

        var content = new Rect2(
            (float)contentRect[0],
            (float)contentRect[1],
            (float)contentRect[2],
            (float)contentRect[3]);
        ClampPan();
        var scale = CalculateCoverScale(content.Size) * _zoom;
        if (!float.IsFinite(scale) || scale <= 0) return false;
        var contentViewportOrigin = (Size - content.Size * scale) / 2f + _pan;
        var mapToViewport = MakeTransform(scale, contentViewportOrigin - content.Position * scale);
        transform = new MapViewportTransform(content, scale, mapToViewport);
        return true;
    }

    private static bool IsApproximatePoint(PlaceDefinition place) =>
        string.Equals(place.MapRepresentation, "approximate_point", StringComparison.Ordinal);

    private static bool IsInferenceRoute(RouteDefinition route) =>
        route.ClaimStatus?.Contains("inference", StringComparison.OrdinalIgnoreCase) == true;

    private static string PlaceKnowledgeClass(PlaceDefinition place)
    {
        // 合并后的正式契约：地点/路线都是 DESIGN 准入草稿（PR #9 的证据准入机器），
        // 尚未通过史料复核，语义层如实标 DESIGN 而不是冒充 FACT。
        var evidenceClass = string.Equals(place.EvidenceStatus, "draft", StringComparison.Ordinal)
            ? "DESIGN"
            : "INFERENCE";
        var openClass = string.Equals(place.HistoricalSiteStatus, "open", StringComparison.OrdinalIgnoreCase)
            ? "OPEN"
            : "";
        return string.IsNullOrEmpty(openClass) ? evidenceClass : $"{evidenceClass}/{openClass}";
    }

    private static string RouteKnowledgeClass(RouteDefinition route)
    {
        if (string.Equals(route.ClaimStatus, "design_topology", StringComparison.Ordinal)) return "DESIGN";
        return IsInferenceRoute(route) ? "INFERENCE" : "FACT";
    }

    private string BuildRouteSemanticLegend()
    {
        if (_manifest?.Routes is not { Count: > 0 } routes) return "路线 OPEN";
        var statuses = routes
            .Select(route => $"{RouteKnowledgeClass(route)}(claim_status={route.ClaimStatus}, evidence_status={route.EvidenceStatus})")
            .Distinct(StringComparer.Ordinal);
        return "路线：" + string.Join("；", statuses);
    }

    private static string FormatPlaceSemanticSummary(PlaceDefinition place) =>
        $"{place.NameZh} [{PlaceKnowledgeClass(place)}] · review_status={place.ReviewStatus} · evidence_status={place.EvidenceStatus} · coordinate_epoch={place.CoordinateEpoch} · map_representation={place.MapRepresentation} · historical_site_status={place.HistoricalSiteStatus}";

    public Vector2 GetViewportPointForPlace(string placeId)
    {
        if (!_placesById.TryGetValue(placeId, out var place) || !TryGetMapViewportTransform(out var transform))
            return new Vector2(-1, -1);
        return transform.MapToViewport * new Vector2((float)place.MapX, (float)place.MapY);
    }

    private void ClampPan()
    {
        if (_manifest?.Canvas?.ContentRect is not { Count: 4 } contentRect || Size.X <= 0 || Size.Y <= 0)
            return;
        var content = new Rect2((float)contentRect[0], (float)contentRect[1], (float)contentRect[2], (float)contentRect[3]);
        _zoom = Mathf.Clamp(_zoom, MinimumStrategicZoom, MaximumZoom);
        if (!IsStrategicView)
        {
            _zoom = MinimumStrategicZoom;
            _pan = Vector2.Zero;
        }
        var fit = CalculateCoverScale(content.Size) * _zoom;
        var scaledSize = content.Size * fit;
        // 边缘约束：缩放内容比视口大时，左右/上下边缘最多刚好贴住视口；
        // 内容比视口小时则固定居中，不能继续拖出清单之外的海面。
        var maxPan = new Vector2(
            scaledSize.X > Size.X ? (scaledSize.X - Size.X) / 2f : 0,
            scaledSize.Y > Size.Y ? (scaledSize.Y - Size.Y) / 2f : 0);
        _pan = new Vector2(
            Mathf.Clamp(_pan.X, -maxPan.X, maxPan.X),
            Mathf.Clamp(_pan.Y, -maxPan.Y, maxPan.Y));
    }

    private float CalculateCoverScale(Vector2 contentSize)
    {
        if (contentSize.X <= 0 || contentSize.Y <= 0 || Size.X <= 0 || Size.Y <= 0)
            return 0;
        // cover 而不是 contain：content_rect 的四边始终在视口之外或正好贴边，
        // 因而玩家无论怎样拖动都看不到清单定义范围之外的纹理区域。
        return Mathf.Max(Size.X / contentSize.X, Size.Y / contentSize.Y);
    }

    private Rect2 CalculateContentViewportRect()
    {
        return TryGetMapViewportTransform(out var transform) ? transform.ContentViewportRect : new Rect2();
    }

    private bool ValidateManifest(Manifest manifest, out string error)
    {
        error = "地图清单不可用；已停止显示。";
        if (manifest.Assets is not { Count: > 0 } assets || manifest.AssetSha256 is not { Count: > 0 } hashes)
        {
            error = "地图清单缺少资源或资产哈希；已停止显示。";
            return false;
        }
        if (manifest.Canvas is null || manifest.Canvas.Width != AuthoritativeCanvasWidth ||
            manifest.Canvas.Height != AuthoritativeCanvasHeight)
        {
            error = $"地图清单画布必须为权威 {AuthoritativeCanvasWidth}×{AuthoritativeCanvasHeight}；已停止显示。";
            return false;
        }
        if (
            manifest.Canvas.ContentRect is not { Count: 4 } rect || rect.Any(value => !double.IsFinite(value)) ||
            rect[2] <= 0 || rect[3] <= 0 || rect[0] < 0 || rect[1] < 0 ||
            rect[0] + rect[2] > manifest.Canvas.Width || rect[1] + rect[3] > manifest.Canvas.Height)
        {
            error = "地图清单的画布范围无效；已停止显示。";
            return false;
        }
        if (manifest.HistoricalContent is null || string.IsNullOrWhiteSpace(manifest.HistoricalContent.SnapshotDate) ||
            string.IsNullOrWhiteSpace(manifest.HistoricalContent.Warning) ||
            manifest.HistoricalContent.ClaimStatus != "design_only_no_reviewed_evidence" ||
            manifest.HistoricalContent.GeometryRole is null || !manifest.HistoricalContent.GeometryRole.Contains("presentation_only_not_simulation_topology", StringComparison.Ordinal))
        {
            error = "地图清单缺少明确的历史呈现语义；已停止显示。";
            return false;
        }
        if (manifest.ResearchBaseline is null || string.IsNullOrWhiteSpace(manifest.ResearchBaseline.GeometryDepictDate) ||
            manifest.ResearchBaseline.HistoricalFitStatus != "research_baseline_only" || manifest.ResearchBaseline.Visible)
        {
            error = "地图清单缺少明确的研究基线语义；已停止显示。";
            return false;
        }
        if (manifest.Places is null || manifest.Routes is null || manifest.Places.Count == 0)
        {
            error = "地图清单缺少节点或路线契约；已停止显示。";
            return false;
        }

        var placeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var place in manifest.Places)
        {
            if (string.IsNullOrWhiteSpace(place.Id) || !placeIds.Add(place.Id) || string.IsNullOrWhiteSpace(place.Kind) ||
                string.IsNullOrWhiteSpace(place.NameZh) || !double.IsFinite(place.MapX) || !double.IsFinite(place.MapY) ||
                place.MapX < 0 || place.MapX > manifest.Canvas.Width || place.MapY < 0 || place.MapY > manifest.Canvas.Height ||
                place.ReviewStatus != "draft" || place.EvidenceStatus != "draft" ||
                place.HistoricalSiteStatus != "open" || place.CoordinateEpoch != "modern_anchor" || place.MapRepresentation != "approximate_point")
            {
                error = "地图节点契约或历史状态无效；已停止显示。";
                return false;
            }
        }

        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in manifest.Routes)
        {
            if (string.IsNullOrWhiteSpace(route.Id) || !routeIds.Add(route.Id) || string.IsNullOrWhiteSpace(route.FromPlaceId) ||
                string.IsNullOrWhiteSpace(route.ToPlaceId) || route.FromPlaceId == route.ToPlaceId || !placeIds.Contains(route.FromPlaceId) ||
                !placeIds.Contains(route.ToPlaceId) || route.Points is not { Count: >= 2 } points || route.ReviewStatus != "draft" ||
                route.EvidenceStatus != "draft" || route.ClaimStatus != "design_topology")
            {
                error = "地图路线契约或历史状态无效；已停止显示。";
                return false;
            }
            foreach (var point in points)
            {
                if (!double.IsFinite(point.MapX) || !double.IsFinite(point.MapY) || point.MapX < 0 || point.MapX > manifest.Canvas.Width ||
                    point.MapY < 0 || point.MapY > manifest.Canvas.Height)
                {
                    error = "地图路线坐标无效；已停止显示。";
                    return false;
                }
            }
            var from = manifest.Places.First(place => place.Id == route.FromPlaceId);
            var to = manifest.Places.First(place => place.Id == route.ToPlaceId);
            if (Math.Abs(points[0].MapX - from.MapX) > 0.01 || Math.Abs(points[0].MapY - from.MapY) > 0.01 ||
                Math.Abs(points[^1].MapX - to.MapX) > 0.01 || Math.Abs(points[^1].MapY - to.MapY) > 0.01)
            {
                error = "地图路线端点与节点不一致；已停止显示。";
                return false;
            }
        }

        if (!assets.TryGetValue("physical_base", out var physicalPath) || !assets.TryGetValue("history_overlay", out var historyPath) ||
            string.IsNullOrWhiteSpace(physicalPath) || string.IsNullOrWhiteSpace(historyPath) ||
            !physicalPath.StartsWith("res://", StringComparison.Ordinal) || !historyPath.StartsWith("res://", StringComparison.Ordinal))
        {
            error = "地图清单缺少可显示的底图资源；已停止显示。";
            return false;
        }

        // 两张运行时必需纹理优先做存在性、必需哈希、实际哈希和文件尺寸校验。
        // 这样“缺少必需哈希”不会被其他可选资产的遍历掩盖。
        if (!ValidateRequiredRasterAsset(physicalPath, hashes, manifest.Canvas, "物理底图", out error) ||
            !ValidateRequiredRasterAsset(historyPath, hashes, manifest.Canvas, "历史叠加层", out error))
            return false;

        foreach (var pair in hashes)
        {
            if (pair.Key is null || pair.Value is null || pair.Value.Length != 64 || pair.Value.Any(character => !Uri.IsHexDigit(character)))
            {
                error = "地图资产哈希格式无效；已停止显示。";
                return false;
            }
            var assetPath = assets.Values.FirstOrDefault(path => Path.GetFileName(path) == pair.Key);
            if (assetPath is null || !assetPath.StartsWith("res://", StringComparison.Ordinal))
            {
                error = "地图资产哈希缺少对应资源；已停止显示。";
                return false;
            }
            var absoluteAssetPath = ProjectSettings.GlobalizePath(assetPath);
            if (!File.Exists(absoluteAssetPath))
            {
                error = "地图资产文件不可用；已停止显示。";
                return false;
            }
            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absoluteAssetPath))).ToLowerInvariant();
            if (!string.Equals(actualHash, pair.Value, StringComparison.OrdinalIgnoreCase))
            {
                error = "地图资产校验失败；已停止显示。";
                return false;
            }
        }
        return true;
    }

    private static bool ValidateRequiredRasterAsset(
        string assetPath,
        IReadOnlyDictionary<string, string> hashes,
        CanvasDefinition canvas,
        string role,
        out string error)
    {
        error = "地图清单不可用；已停止显示。";
        var fileName = Path.GetFileName(assetPath);
        if (string.IsNullOrWhiteSpace(fileName) || !hashes.TryGetValue(fileName, out var expectedHash) ||
            string.IsNullOrWhiteSpace(expectedHash) || expectedHash.Length != 64 || expectedHash.Any(character => !Uri.IsHexDigit(character)))
        {
            error = $"{role}缺少必需的 SHA-256；已停止显示。";
            return false;
        }

        var absolutePath = ProjectSettings.GlobalizePath(assetPath);
        if (!File.Exists(absolutePath))
        {
            error = $"{role}文件不可用；已停止显示。";
            return false;
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolutePath))).ToLowerInvariant();
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{role} SHA-256 不匹配；已停止显示。";
            return false;
        }

        if (!TryReadPngDimensions(absolutePath, out var width, out var height) || width != canvas.Width || height != canvas.Height)
        {
            error = $"{role}文件尺寸与清单画布不一致；已停止显示。";
            return false;
        }

        return true;
    }

    private static bool ValidateRasterAssetAgainstFixedContract(
        PresentationRasterContract contract,
        string role,
        out string error)
    {
        error = "地图清单不可用；已停止显示。";
        if (string.IsNullOrWhiteSpace(contract.Path) || !contract.Path.StartsWith("res://", StringComparison.Ordinal) ||
            contract.Sha256.Length != 64 || contract.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
            contract.Width != AuthoritativeCanvasWidth || contract.Height != AuthoritativeCanvasHeight)
        {
            error = $"{role}权威契约无效；已停止显示。";
            return false;
        }

        var absolutePath = ProjectSettings.GlobalizePath(contract.Path);
        if (!File.Exists(absolutePath))
        {
            error = $"{role}文件不可用；已停止显示。";
            return false;
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolutePath))).ToLowerInvariant();
        if (!string.Equals(actualHash, contract.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{role} SHA-256 不匹配；已停止显示。";
            return false;
        }

        if (!TryReadPngDimensions(absolutePath, out var width, out var height) ||
            width != contract.Width || height != contract.Height)
        {
            error = $"{role}必须为权威 {contract.Width}×{contract.Height}；已停止显示。";
            return false;
        }

        return true;
    }

    private static bool TryReadPngDimensions(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length) return false;
        if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47 ||
            header[4] != 0x0D || header[5] != 0x0A || header[6] != 0x1A || header[7] != 0x0A ||
            header[12] != 0x49 || header[13] != 0x48 || header[14] != 0x44 || header[15] != 0x52)
            return false;

        width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        return width > 0 && height > 0;
    }

    private void DrawTextureTransform(Texture2D texture, Transform2D transform, Color modulate)
    {
        var size = texture.GetSize();
        DrawSetTransform(transform.Origin, transform.Rotation, transform.Scale);
        DrawTextureRect(texture, new Rect2(0, 0, size.X, size.Y), false, modulate);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    private static StyleBoxFlat MakePanel(Color background, Color border, int width, int radius)
    {
        var style = new StyleBoxFlat { BgColor = background, BorderColor = border };
        style.SetBorderWidthAll(width);
        style.SetCornerRadiusAll(radius);
        return style;
    }
}
