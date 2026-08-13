using Godot;
using System;
using System.Collections.Generic;

namespace Ming.Godot;

/// <summary>
/// 御案页面壳。这里的按钮只改变展示态或生成“待核验”的演示反馈，绝不写 WorldState。
/// </summary>
public partial class MainUi : Control
{
    private const string DemoNotice = "演示壳：尚未接入 Simulation，不会推进时间或修改世界状态。";
    private readonly Dictionary<string, string> _placeDescriptions = new()
    {
        ["beijing"] = "中枢 · 直属仓与六部文书汇集处。",
        ["tongzhou"] = "漕运节点 · 粮道瓶颈与仓储上报待复核。",
        ["shanhaiguan"] = "关隘节点 · 前线道路信息为 OPEN 草稿。",
        ["ningyuan"] = "辽西前线 · 急报提示当前最高优先级。",
        ["jinzhou"] = "前线节点 · 驻军与粮秣尚未接入只读快照。",
        ["dengzhou"] = "海运节点 · 海路方案仅作交互比较演示。"
    };
    private MapView _map = null!;
    private Label _dateLabel = null!;
    private Label _speedLabel = null!;
    private Label _pauseLabel = null!;
    private Label _selectionLabel = null!;
    private Label _selectionBody = null!;
    private Label _noticeLabel = null!;
    private Label _layerLabel = null!;
    private Button _pauseButton = null!;
    private Button _overviewButton = null!;
    private Button _liaoxiButton = null!;
    private Button _routeButton = null!;
    private Button _historyButton = null!;
    private Button _draftButton = null!;
    private Button _confirmButton = null!;
    private Panel _draftPanel = null!;
    private Panel _confirmPanel = null!;
    private bool _paused = true;
    private double _speed = 1;

    public override void _Ready()
    {
        _map = GetNode<MapView>("MapView");
        BuildUi();
        _map.PlaceSelected += OnPlaceSelected;
        OnPlaceSelected(_map.SelectedPlaceId);
        ShowNotice(DemoNotice);
    }

    private void BuildUi()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddColorRect("#101A1C", new Rect2(0, 0, 1, 1), true);
        var paper = GD.Load<Texture2D>("res://assets/ui/generated/ming-imperial-paper-background.png");
        if (paper != null)
        {
            var background = new TextureRect { Texture = paper, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore };
            background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            background.Modulate = new Color(1, 1, 1, 0.2f);
            AddChild(background);
            MoveChild(background, 0);
        }

        var topBar = AddPanel(this, "#172529", "#B59047", new Rect2(20, 16, 1560, 90), 8);
        AddLabel(topBar, "大明御案", new Vector2(24, 18), 28, "#F4E9D8");
        AddLabel(topBar, "崇祯二年 · 春分前后", new Vector2(26, 55), 13, "#B7A795");
        _dateLabel = AddLabel(topBar, "1629-03-18 06:00", new Vector2(250, 21), 22, "#F0D39A");
        _pauseLabel = AddLabel(topBar, "PAUSED · 演示状态", new Vector2(250, 55), 13, "#E7B46B");
        _pauseButton = AddButton(topBar, "▶ 解除暂停", new Rect2(490, 22, 138, 44));
        _pauseButton.Pressed += TogglePause;
        _speedLabel = AddLabel(topBar, "速度 1×", new Vector2(650, 36), 14, "#D9C5A4");
        foreach (var speed in new[] { 0.5, 1.0, 2.0, 3.0 })
        {
            var button = AddButton(topBar, $"{speed:0.#}×", new Rect2(715 + (float)(speed * 48), 22, 44, 44));
            var selectedSpeed = speed;
            button.Pressed += () => SetSpeed(selectedSpeed);
        }
        AddLabel(topBar, "模型增强 · 离线规则", new Vector2(1030, 27), 13, "#9EC3AD");
        AddLabel(topBar, "● 安全快照", new Vector2(1030, 52), 13, "#6A9A8B");
        AddLabel(topBar, "1391 近似 / 1629 OPEN", new Vector2(1260, 27), 13, "#E3C27E");
        AddLabel(topBar, "未完整建模诸政权", new Vector2(1260, 52), 13, "#C7B19B");

        var left = AddPanel(this, "#192526", "#866943", new Rect2(20, 122, 308, 710), 8);
        AddLabel(left, "御案 · 待处理", new Vector2(22, 20), 20, "#F4E9D8");
        AddLabel(left, "3 件事务 · 1 件高优先级", new Vector2(22, 50), 12, "#B7A795");
        AddMemorial(left, "辽西急报", "宁远 · 06:00送达", "前线粮秣告急 · OPEN", "#C64A3B", 88);
        AddMemorial(left, "户部请旨", "京师 · 昨日午后", "漕运拨款待决定 · DESIGN", "#D0A85C", 194);
        AddMemorial(left, "御史弹劾", "京师 · 昨日辰刻", "证据不足 · 待复核", "#7EA39A", 300);
        AddLabel(left, "事件流", new Vector2(22, 408), 15, "#E8D4B0");
        AddLabel(left, "05:42  宁远驿报抵京", new Vector2(22, 440), 13, "#D7C4AB");
        AddLabel(left, "04:10  通州仓上报延迟", new Vector2(22, 470), 13, "#D7C4AB");
        AddLabel(left, "昨日  漕运方案仍未签发", new Vector2(22, 500), 13, "#D7C4AB");
        AddLabel(left, "提示", new Vector2(22, 560), 13, "#E7B46B");
        AddLabel(left, "暂停、倍速、库存、政令均未接 Simulation。", new Vector2(22, 586), 12, "#B7A795");
        AddLabel(left, "此处只展示命令草案与反馈。", new Vector2(22, 610), 12, "#B7A795");

        var right = AddPanel(this, "#192526", "#866943", new Rect2(1272, 122, 308, 710), 8);
        AddLabel(right, "所选 · 节点详情", new Vector2(22, 20), 20, "#F4E9D8");
        _selectionLabel = AddLabel(right, "京师", new Vector2(22, 66), 24, "#F0D39A");
        _selectionBody = AddLabel(right, "", new Vector2(22, 104), 13, "#D7C4AB");
        _selectionBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _selectionBody.Size = new Vector2(264, 160);
        AddLabel(right, "玩家可见情报", new Vector2(22, 258), 14, "#E8D4B0");
        AddInfoRow(right, "粮秣", "约 8,400 石 · 昨日上报", 286);
        AddInfoRow(right, "军情", "可见度 62% · 待复核", 326);
        AddInfoRow(right, "路线", "京师—通州 · DESIGN", 366);
        AddLabel(right, "图层与焦点", new Vector2(22, 428), 14, "#E8D4B0");
        _overviewButton = AddButton(right, "天下概览", new Rect2(22, 460, 122, 38));
        _overviewButton.Pressed += () => { _map.LoadManifest("res://assets/maps/generated/ming_1629/map-manifest.json"); ShowNotice("已切换：东亚概览 · 只读清单"); };
        _liaoxiButton = AddButton(right, "辽西细节", new Rect2(160, 460, 122, 38));
        _liaoxiButton.Pressed += () => { _map.LoadManifest("res://assets/maps/generated/ming_1629_liaoxi/map-manifest.json"); ShowNotice("已切换：辽西细节 · 只读清单"); };
        _routeButton = AddButton(right, "路线层  ON", new Rect2(22, 508, 122, 38));
        _routeButton.Pressed += ToggleRoutes;
        _historyButton = AddButton(right, "历史层  ON", new Rect2(160, 508, 122, 38));
        _historyButton.Pressed += ToggleHistory;
        _layerLabel = AddLabel(right, "图层只改变显示，不改变世界。", new Vector2(22, 560), 12, "#B7A795");
        _layerLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _layerLabel.Size = new Vector2(264, 60);

        var bottom = AddPanel(this, "#172529", "#B59047", new Rect2(20, 848, 1560, 92), 8);
        AddLabel(bottom, "行动区", new Vector2(22, 15), 14, "#E8D4B0");
        foreach (var (label, x) in new[] { ("召见", 112f), ("要求复核", 186f), ("拟旨", 278f), ("交部议", 352f), ("留中", 426f) })
        {
            var actionButton = AddButton(bottom, label, new Rect2(x, 18, 68, 38));
            actionButton.Pressed += () => ShowNotice($"已生成“{label}”待核验 Intent · 尚未执行");
        }
        _draftButton = AddButton(bottom, "拟定结构化政令", new Rect2(520, 18, 164, 38));
        _draftButton.Pressed += ShowDraft;
        _noticeLabel = AddLabel(bottom, DemoNotice, new Vector2(716, 17), 12, "#D8C6AE");
        _noticeLabel.Name = "SimulationNotice";
        _noticeLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _noticeLabel.Size = new Vector2(812, 48);
        _draftPanel = BuildDraftPanel();
        _confirmPanel = BuildConfirmPanel();
        _map.MoveToFront();
    }

    private Panel BuildDraftPanel()
    {
        var panel = AddPanel(this, "#202E2F", "#C6A25F", new Rect2(500, 280, 420, 340), 8);
        panel.Visible = false;
        AddLabel(panel, "拟定结构化政令", new Vector2(24, 20), 20, "#F4E9D8");
        AddLabel(panel, "目标 · 宁远急饷", new Vector2(24, 68), 14, "#F0D39A");
        AddLabel(panel, "方案 · 陆运 / 海运 / 两路并行", new Vector2(24, 102), 13, "#D7C4AB");
        AddLabel(panel, "期限 · 请选择后再提交", new Vector2(24, 134), 13, "#D7C4AB");
        AddLabel(panel, "范围 · 预计影响京师—通州—宁远", new Vector2(24, 166), 13, "#D7C4AB");
        AddLabel(panel, "状态 · 草案，不代表已经执行", new Vector2(24, 198), 13, "#E7B46B");
        var close = AddButton(panel, "返回", new Rect2(24, 252, 92, 42));
        close.Pressed += () => panel.Visible = false;
        var submit = AddButton(panel, "查看确认", new Rect2(280, 252, 116, 42));
        submit.Pressed += () => { panel.Visible = false; _confirmPanel.Visible = true; };
        return panel;
    }

    private Panel BuildConfirmPanel()
    {
        var panel = AddPanel(this, "#202E2F", "#C6A25F", new Rect2(470, 224, 520, 450), 8);
        panel.Visible = false;
        AddLabel(panel, "确认前 · 方案对比", new Vector2(24, 20), 20, "#F4E9D8");
        AddLabel(panel, "这是提交申请，不是已执行。", new Vector2(24, 56), 13, "#E7B46B");
        AddComparison(panel, "陆运", "约 12 日 · 约 4,600 石 · 风险中", 96, "#6A9A8B");
        AddComparison(panel, "海运", "约 8 日 · 区间值 · 风险较高", 166, "#C6A25F");
        AddComparison(panel, "两路并行", "分批到达 · 银耗合计 · 风险分散", 236, "#A987B2");
        AddLabel(panel, "缺失信息：期限、承办人和最终容量仍待 Simulation 核验。", new Vector2(24, 324), 12, "#D7C4AB");
        var cancel = AddButton(panel, "返回修改", new Rect2(24, 374, 116, 42));
        cancel.Pressed += () => panel.Visible = false;
        _confirmButton = AddButton(panel, "提交待核验 Intent", new Rect2(348, 374, 148, 42));
        _confirmButton.Pressed += () => { panel.Visible = false; ShowNotice("已提交待核验 Intent · Simulation 尚未接入"); };
        return panel;
    }

    private void OnPlaceSelected(string placeId)
    {
        var name = placeId switch { "beijing" => "京师", "tongzhou" => "通州", "shanhaiguan" => "山海关", "ningyuan" => "宁远", "jinzhou" => "锦州", "dengzhou" => "登州", _ => "未知节点" };
        _selectionLabel.Text = name;
        _selectionBody.Text = _placeDescriptions.TryGetValue(placeId, out var body) ? body + "\n\n来源状态：OPEN / DESIGN，非完整 1629 势力图。" : "节点信息不可用。";
        ShowNotice($"已选中 {name} · 只读呈现，不会直接修改世界状态");
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _pauseButton.Text = _paused ? "▶ 解除暂停" : "Ⅱ 暂停";
        _pauseLabel.Text = _paused ? "PAUSED · 演示状态" : "RUNNING · 演示状态";
        ShowNotice(_paused ? "展示态已暂停 · 未接入 Simulation" : "展示态已运行 · 不会自行推进权威时间");
    }

    private void SetSpeed(double speed)
    {
        _speed = speed;
        _speedLabel.Text = $"速度 {_speed:0.#}×";
        ShowNotice($"展示态速度切换为 {_speed:0.#}× · 尚未接入 Simulation");
    }

    private void ToggleRoutes()
    {
        _map.ToggleRoutes();
        _routeButton.Text = _map.RoutesVisible ? "路线层  ON" : "路线层  OFF";
        ShowNotice("路线层只改变显示，不改变拓扑或世界状态");
    }

    private void ToggleHistory()
    {
        _map.ToggleHistoricalLayer();
        _historyButton.Text = _map.HistoricalLayerVisible ? "历史层  ON" : "历史层  OFF";
        ShowNotice("历史层只改变显示；1391 近似基线仍标为 OPEN");
    }

    private void ShowDraft() => _draftPanel.Visible = true;
    private void ShowNotice(string message) => _noticeLabel.Text = message;

    private Panel AddMemorial(Control parent, string title, string meta, string status, string accent, float y)
    {
        var card = AddPanel(parent, "#243133", accent, new Rect2(18, y, 272, 88), 6);
        AddLabel(card, title, new Vector2(14, 12), 16, "#F4E9D8");
        AddLabel(card, meta, new Vector2(14, 38), 12, "#C8B7A1");
        AddLabel(card, status, new Vector2(14, 61), 12, accent);
        return card;
    }

    private void AddComparison(Control parent, string title, string body, float y, string accent)
    {
        var card = AddPanel(parent, "#273536", accent, new Rect2(22, y, 476, 54), 5);
        AddLabel(card, title, new Vector2(14, 8), 14, accent);
        AddLabel(card, body, new Vector2(92, 9), 12, "#E4D4BD");
    }

    private void AddInfoRow(Control parent, string key, string value, float y)
    {
        AddLabel(parent, key, new Vector2(22, y), 12, "#B59047");
        AddLabel(parent, value, new Vector2(76, y), 12, "#D7C4AB");
    }

    private Label AddLabel(Control parent, string text, Vector2 position, int size, string color)
    {
        var label = new Label { Text = text, Position = position };
        if (!string.IsNullOrWhiteSpace(text)) label.Name = text;
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", new Color(color));
        parent.AddChild(label);
        return label;
    }

    private Button AddButton(Control parent, string text, Rect2 rect)
    {
        var button = new Button { Text = text, Position = rect.Position, Size = rect.Size, FocusMode = Control.FocusModeEnum.All };
        button.AddThemeStyleboxOverride("normal", MakePanel("#2B3B3C", "#866943", 1, 4));
        button.AddThemeStyleboxOverride("hover", MakePanel("#3A4D4A", "#D0A85C", 1, 4));
        button.AddThemeStyleboxOverride("pressed", MakePanel("#6E302D", "#F0C46B", 2, 4));
        button.AddThemeColorOverride("font_color", new Color("#F4E9D8"));
        button.AddThemeColorOverride("font_hover_color", new Color("#FFF2D2"));
        button.AddThemeFontSizeOverride("font_size", 13);
        parent.AddChild(button);
        return button;
    }

    private Panel AddPanel(Control parent, string background, string border, Rect2 rect, int radius)
    {
        var panel = new Panel { Position = rect.Position, Size = rect.Size };
        panel.AddThemeStyleboxOverride("panel", MakePanel(background, border, 1, radius));
        parent.AddChild(panel);
        return panel;
    }

    private ColorRect AddColorRect(string color, Rect2 normalizedRect, bool normalized)
    {
        var rect = new ColorRect { Color = new Color(color), MouseFilter = MouseFilterEnum.Ignore };
        rect.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(rect);
        return rect;
    }

    private static StyleBoxFlat MakePanel(string background, string border, int width, int radius)
    {
        var style = new StyleBoxFlat { BgColor = new Color(background), BorderColor = new Color(border) };
        style.SetBorderWidthAll(width);
        style.SetCornerRadiusAll(radius);
        return style;
    }
}
