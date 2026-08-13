using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ming.Godot;

/// <summary>
/// 只读地图呈现层：它从 map-manifest.json 读取底图、历史层、路线和六个可选节点。
/// 这个类不认识 WorldState，也不推进时间；点击只回报 place id 给页面壳。
/// </summary>
public partial class MapView : Control
{
    private sealed class Manifest
    {
        public Dictionary<string, string> Assets { get; set; } = new();
        [JsonPropertyName("canvas")]
        public CanvasDefinition Canvas { get; set; } = new();
        [JsonPropertyName("historical_content")]
        public HistoricalContent HistoricalContent { get; set; } = new();
        public List<PlaceDefinition> Places { get; set; } = new();
        public List<RouteDefinition> Routes { get; set; } = new();
    }

    private sealed class CanvasDefinition
    {
        public int Width { get; set; }
        public int Height { get; set; }
        [JsonPropertyName("content_rect")]
        public List<double> ContentRect { get; set; } = new();
    }

    private sealed class HistoricalContent
    {
        [JsonPropertyName("geometry_depict_date")]
        public string GeometryDepictDate { get; set; } = "1391-01-01";
        [JsonPropertyName("snapshot_date")]
        public string SnapshotDate { get; set; } = "1629-01-01";
        public string Warning { get; set; } = "";
    }

    private sealed class PlaceDefinition
    {
        public string Id { get; set; } = "";
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "";
        [JsonPropertyName("name_zh")]
        public string NameZh { get; set; } = "";
        [JsonPropertyName("review_status")]
        public string ReviewStatus { get; set; } = "draft";
        [JsonPropertyName("map_x")]
        public double MapX { get; set; }
        [JsonPropertyName("map_y")]
        public double MapY { get; set; }
    }

    private sealed class RouteDefinition
    {
        [JsonPropertyName("from_place_id")]
        public string FromPlaceId { get; set; } = "";
        [JsonPropertyName("to_place_id")]
        public string ToPlaceId { get; set; } = "";
        public List<MapPoint> Points { get; set; } = new();
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
    private TextureRect? _physicalLayer;
    private TextureRect? _historyLayer;
    private float _zoom = 1.0f;
    private Vector2 _pan = Vector2.Zero;
    private string _selectedPlaceId = "beijing";
    private bool _routesVisible = true;
    private bool _historicalLayerVisible = true;
    private bool _isDragging;
    private Vector2 _dragStart;

    public event Action<string>? PlaceSelected;
    public string ManifestPath { get; private set; } = "";
    public bool LoadedFromManifest => _manifest != null && _physicalTexture != null && _historyTexture != null;
    public bool HistoricalLayerVisible => _historicalLayerVisible;
    public bool RoutesVisible => _routesVisible;
    public float Zoom => _zoom;
    public string SelectedPlaceId => _selectedPlaceId;
    public string GeometryDepictDate => _manifest?.HistoricalContent.GeometryDepictDate ?? "OPEN";
    public string SnapshotDate => _manifest?.HistoricalContent.SnapshotDate ?? "OPEN";
    public string HistoricalWarning => _manifest?.HistoricalContent.Warning ?? "地图清单未加载；历史层已关闭。";
    public int PlaceCount => _manifest?.Places.Count ?? 0;
    public int RouteCount => _manifest?.Routes.Count ?? 0;

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

        var filePath = ProjectSettings.GlobalizePath(manifestPath);
        if (!File.Exists(filePath))
        {
            _physicalLayer?.QueueFree();
            _historyLayer?.QueueFree();
            _physicalLayer = null;
            _historyLayer = null;
            QueueRedraw();
            return;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            _manifest = JsonSerializer.Deserialize<Manifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (_manifest == null || !_manifest.Assets.TryGetValue("physical_base", out var physicalPath) ||
                !_manifest.Assets.TryGetValue("history_overlay", out var historyPath))
            {
                _manifest = null;
                return;
            }

            _physicalTexture = GD.Load<Texture2D>(physicalPath);
            _historyTexture = GD.Load<Texture2D>(historyPath);
            if (_physicalTexture == null || _historyTexture == null)
            {
                _manifest = null;
                _physicalTexture = null;
                _historyTexture = null;
                return;
            }

            foreach (var place in _manifest.Places)
            {
                if (!string.IsNullOrWhiteSpace(place.Id) && place.MapX > 0 && place.MapY > 0)
                    _placesById[place.Id] = place;
            }

            BuildMapLayers();
        }
        catch (Exception error)
        {
            GD.PushWarning($"地图清单加载失败，已安全关闭地图层：{error.Message}");
            _manifest = null;
            _physicalTexture = null;
            _historyTexture = null;
        }
        QueueRedraw();
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
        if (_historyLayer != null) _historyLayer.Visible = _historicalLayerVisible && LoadedFromManifest;
        QueueRedraw();
    }

    public void ToggleRoutes()
    {
        _routesVisible = !_routesVisible;
        QueueRedraw();
    }

    public void ResetView()
    {
        _zoom = 1.0f;
        _pan = Vector2.Zero;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex == MouseButton.WheelUp && mouseButton.Pressed)
            {
                _zoom = Mathf.Min(2.2f, _zoom + 0.1f);
                QueueRedraw();
                AcceptEvent();
            }
            else if (mouseButton.ButtonIndex == MouseButton.WheelDown && mouseButton.Pressed)
            {
                _zoom = Mathf.Max(0.75f, _zoom - 0.1f);
                QueueRedraw();
                AcceptEvent();
            }
            else if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                if (mouseButton.Pressed)
                {
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
            _pan += motion.Relative;
            QueueRedraw();
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("#132B38"), true);
        if (!LoadedFromManifest || _manifest == null)
        {
            DrawString(ThemeDB.FallbackFont, new Vector2(26, 48), "地图资源不可用 · 已安全关闭", HorizontalAlignment.Left, -1, 18, new Color("#F4E9D8"));
            return;
        }

        var content = new Rect2((float)_manifest.Canvas.ContentRect[0], (float)_manifest.Canvas.ContentRect[1],
            (float)_manifest.Canvas.ContentRect[2], (float)_manifest.Canvas.ContentRect[3]);
        var viewport = new Rect2(0, 0, Size.X, Size.Y);
        var fit = Mathf.Min(viewport.Size.X / content.Size.X, viewport.Size.Y / content.Size.Y) * _zoom;
        var origin = viewport.Position + (viewport.Size - content.Size * fit) / 2f + _pan;
        var transform = MakeTransform(fit, origin - content.Position * fit);

        DrawTextureTransform(_physicalTexture!, transform, Colors.White);
        if (_historicalLayerVisible) DrawTextureTransform(_historyTexture!, transform, new Color(1, 1, 1, 0.75f));

        if (_routesVisible)
        {
            foreach (var route in _manifest.Routes)
            {
                if (route.Points.Count < 2) continue;
                for (var i = 1; i < route.Points.Count; i++)
                    DrawDashedLine(transform * ToVector(route.Points[i - 1]), transform * ToVector(route.Points[i]), new Color("#D0A85C"), 2.0f, 7.0f, true);
            }
        }

        foreach (var place in _manifest.Places)
        {
            var point = transform * new Vector2((float)place.MapX, (float)place.MapY);
            var selected = place.Id == _selectedPlaceId;
            var radius = selected ? 9f : 6f;
            DrawCircle(point, radius + 4, new Color(0.05f, 0.1f, 0.11f, 0.8f));
            DrawCircle(point, radius, selected ? new Color("#C64A3B") : new Color("#E2D0B5"));
            DrawArc(point, radius + 3, 0, Mathf.Tau, 24, selected ? new Color("#F0C46B") : new Color("#8EAA9F"), 1.5f, true);
            var labelOffset = place.Id switch
            {
                "beijing" => new Vector2(-18, -18),
                "tongzhou" => new Vector2(14, 22),
                "shanhaiguan" => new Vector2(14, 20),
                "ningyuan" => new Vector2(-48, -18),
                "jinzhou" => new Vector2(16, -18),
                "dengzhou" => new Vector2(-14, 24),
                _ => new Vector2(12, -10)
            };
            DrawString(ThemeDB.FallbackFont, point + labelOffset, place.NameZh, HorizontalAlignment.Left, -1, selected ? 16 : 14,
                selected ? new Color("#FFF1D1") : new Color("#F4E9D8"));
        }

        DrawStyleBox(MakePanel(new Color(0.04f, 0.09f, 0.1f, 0.78f), new Color("#C8A15B"), 1, 6), new Rect2(18, Size.Y - 54, 382, 34));
        DrawString(ThemeDB.FallbackFont, new Vector2(32, Size.Y - 32), "呈现层 · 1391 近似研究基线 · 1629 OPEN", HorizontalAlignment.Left, -1, 13, new Color("#F1D49B"));
    }

    private void BuildMapLayers()
    {
        _physicalLayer?.QueueFree();
        _historyLayer?.QueueFree();
        _physicalLayer = null;
        _historyLayer = null;
    }

    private static Vector2 ToVector(MapPoint point) => new((float)point.MapX, (float)point.MapY);

    private static Transform2D MakeTransform(float scale, Vector2 origin) => new(
        new Vector2(scale, 0),
        new Vector2(0, scale),
        origin);

    private void SelectPlaceAt(Vector2 position)
    {
        if (_manifest == null || _manifest.Canvas.ContentRect.Count < 4) return;
        var content = new Rect2((float)_manifest.Canvas.ContentRect[0], (float)_manifest.Canvas.ContentRect[1],
            (float)_manifest.Canvas.ContentRect[2], (float)_manifest.Canvas.ContentRect[3]);
        var fit = Mathf.Min(Size.X / content.Size.X, Size.Y / content.Size.Y) * _zoom;
        var origin = (Size - content.Size * fit) / 2f + _pan;
        var transform = MakeTransform(fit, origin - content.Position * fit);
        var inverse = transform.AffineInverse();
        var mapPoint = inverse * position;
        var hitRadius = Mathf.Max(18f, 28f / fit);
        var nearest = "";
        var nearestDistance = hitRadius;
        foreach (var place in _manifest.Places)
        {
            var distance = mapPoint.DistanceTo(new Vector2((float)place.MapX, (float)place.MapY));
            if (distance < nearestDistance)
            {
                nearest = place.Id;
                nearestDistance = distance;
            }
        }
        if (!string.IsNullOrEmpty(nearest)) SelectPlace(nearest);
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
