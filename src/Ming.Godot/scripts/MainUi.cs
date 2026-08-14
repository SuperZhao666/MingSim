using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Ming.Godot.ReadModels;
using MingSim.Application.Commands;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Simulation.Realtime;

namespace Ming.Godot;

/// <summary>
/// 御书房页面壳：玩家面对御案，桌面物件才是入口；中央舆图滚轮放大后进入策略地图。
/// 这里仍只改展示状态，不推进 GameTime，也不写任何游戏权威状态。
/// </summary>
public partial class MainUi : Control
{
    private const float TransitionDurationSeconds = 0.42f;
    private static readonly Rect2 DeskMapRect = new(new Vector2(337, 458), new Vector2(1021, 254));
    private static readonly Rect2 StrategicMapRect = new(new Vector2(0, 72), new Vector2(1600, 888));

    private readonly Dictionary<string, string> _placeDescriptions = new()
    {
        ["beijing"] = "京师中枢 · 六部文书与直属仓奏报汇集处。",
        ["tongzhou"] = "通州漕运 · 粮道、仓储与转运瓶颈。",
        ["shanhaiguan"] = "山海关 · 关宁防线的西端门户。",
        ["ningyuan"] = "宁远前线 · 当前急报最高优先级。",
        ["jinzhou"] = "锦州前线 · 驻军与粮秣仍待只读快照接入。",
        ["dengzhou"] = "登州海运 · 海路方案仅作演示比较。"
    };

    private MapView _map = null!;
    private Control _deskLayer = null!;
    private Control _memorialLayer = null!;
    private Control _mapLayer = null!;
    private Control _transitionInputBlocker = null!;
    private Panel _memorialSheet = null!;
    private Panel _decreeSheet = null!;
    private Label _sheetTitle = null!;
    private Label _sheetMeta = null!;
    private Label _sheetBody = null!;
    private Label _notice = null!;
    private Label _readModelNotice = null!;
    private Label _mapNotice = null!;
    private Label _selectedPlace = null!;
    private TextureRect _selectedPlaceBadge = null!;
    private Font _titleFont = null!;
    private Font _bodyFont = null!;
    private MemorialDeskReadModel _readModel = MemorialDeskReadModel.CreateDefaultDesignPreview();
    private RealtimeSimulationRuntime? _runtime;
    private CommandFacade? _facade;
    private DecreePanel _decreePanel = null!;
    private EndgameReportPanel _endgameReportPanel = null!;
    private GuidePanel _guidePanel = null!;
    private Label _realtimeClock = null!;
    private Label _realtimeStockpiles = null!;
    private Label _realtimeOutcome = null!;
    private bool _uiPauseRequested;
    private Tween? _transitionTween;
    private bool _strategicView;
    private bool _transitioning;
    private bool _transitionTargetStrategic;
    private float _transitionProgress = 1.0f;
    private float _transitionStartAmount;
    private float _transitionEndAmount;
    private float _strategicVisualAmount;

    public int PendingMemorialCount => _readModel.PendingMemorials.Count;
    public int RenderedMemorialCount => IsInstanceValid(_memorialLayer) ? _memorialLayer.GetChildCount() : 0;
    public string ReadModelClassification => _readModel.Classification;
    public string ReadModelSourceNotice => _readModel.SourceNotice;
    public bool StrategicView => _strategicView;
    public bool MemorialOpen => IsInstanceValid(_memorialSheet) && _memorialSheet.Visible;
    public bool Transitioning => _transitioning;
    public bool InputLocked => IsInstanceValid(_transitionInputBlocker) && _transitionInputBlocker.Visible;
    public float TransitionProgress => _transitionProgress;
    public Rect2 TransitionMapRect => IsInstanceValid(_map) ? new Rect2(_map.Position, _map.Size) : default;

    public override void _Ready()
    {
        _titleFont = new SystemFont
        {
            FontNames = ["华文楷体", "KaiTi", "楷体", "Noto Serif CJK SC"],
            AllowSystemFallback = true,
            MultichannelSignedDistanceField = true
        };
        _bodyFont = new SystemFont
        {
            FontNames = ["华文仿宋", "FangSong", "仿宋", "Noto Serif CJK SC"],
            AllowSystemFallback = true,
            MultichannelSignedDistanceField = true
        };

        _map = GetNode<MapView>("MapView");
        _map.PlaceSelected += OnPlaceSelected;
        _map.ExitRequested += () => SetStrategicView(false);
        BuildDesk();
        BuildStrategicMap();
        BuildRealtimeBar();
        // 面板必须在 BuildRealtimeBar 之后接 runtime：全场景只有桥装配的那一份内核与门面。
        _decreePanel = GetNode<DecreePanel>("DecreePanel");
        _endgameReportPanel = GetNode<EndgameReportPanel>("EndgameReportPanel");
        _guidePanel = GetNode<GuidePanel>("GuidePanel");
        _decreePanel.ConnectRuntime(_runtime, _facade);
        _endgameReportPanel.ConnectRuntime(_runtime);
        OnPlaceSelected(_map.SelectedPlaceId);
        ApplyStrategicStateImmediately(false);
        UpdateDeskNotice();
    }

    public override void _Process(double delta)
    {
        // 渲染帧只请求推进，Simulation 自己决定完整、稳定的游戏时间边界（doc 08 §3）。
        // 未接入内核（无 runtime）时什么都不做，保持演示 DTO 模式可运行。
        if (_runtime is null) return;
        _runtime.Advance(TimeSpan.FromSeconds(delta));
        RefreshRealtimeLabels();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (_transitioning) return;
        if (@event is not InputEventMouseButton { Pressed: true } wheel) return;
        if (!_strategicView && wheel.ButtonIndex == MouseButton.WheelUp)
        {
            SetStrategicView(true);
            AcceptEvent();
        }
        else if (_strategicView && wheel.ButtonIndex == MouseButton.WheelDown && _map.Zoom <= 1.001f)
        {
            SetStrategicView(false);
            AcceptEvent();
        }
    }

    public void SetStrategicView(bool enabled)
    {
        if (_transitioning && _transitionTargetStrategic == enabled) return;
        if (!_transitioning && _strategicView == enabled) return;

        _strategicView = enabled;
        _transitionTargetStrategic = enabled;
        if (enabled) _map.EnterStrategicView();
        else _map.ExitStrategicView();

        _transitionTween?.Kill();
        _transitioning = true;
        _transitionProgress = 0.0f;
        _transitionStartAmount = _strategicVisualAmount;
        _transitionEndAmount = enabled ? 1.0f : 0.0f;

        _deskLayer.Visible = true;
        _mapLayer.Visible = true;
        _map.Visible = true;
        _map.MouseFilter = MouseFilterEnum.Ignore;
        _transitionInputBlocker.Visible = true;
        _transitionInputBlocker.MoveToFront();

        var distance = Mathf.Abs(_transitionEndAmount - _transitionStartAmount);
        var duration = Mathf.Max(0.12f, TransitionDurationSeconds * distance);
        _transitionTween = CreateTween();
        _transitionTween.SetProcessMode(Tween.TweenProcessMode.Idle);
        _transitionTween.TweenMethod(
                Callable.From<float>(ApplyTransitionProgress),
                0.0f,
                1.0f,
                duration)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.InOut);
        _transitionTween.Finished += CompleteTransition;
        UpdateDeskNotice();
    }

    /// <summary>
    /// 注入一个已经冻结为只读快照的奏疏列表。界面只重建桌面实体，不发命令、不推进时间。
    /// </summary>
    public void SetReadModel(MemorialDeskReadModel readModel)
    {
        _readModel = readModel ?? throw new ArgumentNullException(nameof(readModel));
        if (IsInstanceValid(_memorialLayer))
        {
            RenderMemorials();
            UpdateDeskNotice();
        }
    }

    /// <summary>
    /// 给 headless 验收使用的公开注入缝：可稳定构造 0、1、多条只读演示数据。
    /// </summary>
    public void InjectAcceptanceReadModel(int pendingCount) =>
        SetReadModel(MemorialDeskReadModel.CreateAcceptanceSample(pendingCount));

    private void BuildDesk()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddBackground();
        _deskLayer = AddFullRectLayer("DeskLayer");

        var titleWash = AddInkWash(_deskLayer, "DeskTitleWash", new Rect2(34, 24, 392, 78));
        AddLabel(titleWash, "大明御书房", new Vector2(20, 10), 30, "#F0E2C8", true);
        AddLabel(titleWash, "崇祯二年 · 春分前后", new Vector2(24, 48), 14, "#D3C19F");
        var provenanceWash = AddInkWash(_deskLayer, "BackgroundProvenanceWash", new Rect2(34, 112, 244, 42));
        var backgroundProvenance = AddLabel(provenanceWash, "DESIGN · 艺术合成背景", new Vector2(16, 10), 13, "#D3C19F");
        backgroundProvenance.Name = "BackgroundProvenance";
        var timeWash = AddInkWash(_deskLayer, "DeskTimeWash", new Rect2(1194, 24, 370, 78));
        AddLabel(timeWash, "1629-03-18 06:00", new Vector2(86, 12), 18, "#E4CB91");
        _readModelNotice = AddLabel(timeWash, "", new Vector2(16, 48), 12, "#B4C9BF");
        _readModelNotice.Name = "ReadModelNotice";

        var deskMapTexture = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/maps/ming_1629-physical.png");
        var deskMapPreview = new Polygon2D
        {
            Name = "DeskMapPreview",
            Polygon =
            [
                new Vector2(438, 463),
                new Vector2(1208, 463),
                new Vector2(1358, 708),
                new Vector2(337, 708)
            ],
            UV =
            [
                Vector2.Zero,
                new Vector2(deskMapTexture.GetWidth(), 0),
                new Vector2(deskMapTexture.GetWidth(), deskMapTexture.GetHeight()),
                new Vector2(0, deskMapTexture.GetHeight())
            ],
            Texture = deskMapTexture,
            Color = new Color(0.91f, 0.90f, 0.78f, 0.72f)
        };
        _deskLayer.AddChild(deskMapPreview);

        var deskMap = new Button
        {
            Name = "DeskMapScroll",
            Position = new Vector2(337, 458),
            Size = new Vector2(1021, 254),
            Flat = true,
            FocusMode = FocusModeEnum.None,
            MouseForcePassScrollEvents = true,
            TooltipText = "滚轮向上：展开东亚舆图"
        };
        deskMap.Pressed += () => SetStrategicView(true);
        deskMap.GuiInput += OnDeskMapInput;
        _deskLayer.AddChild(deskMap);
        AddLabel(deskMap, "东亚舆图", new Vector2(150, 14), 22, "#493C2A", true);
        AddLabel(deskMap, "案上铺图 · 点击或滚轮进入战略视域", new Vector2(152, 48), 12, "#665945");

        _memorialLayer = new Control { Name = "DeskMemorialLayer", MouseFilter = MouseFilterEnum.Ignore };
        _memorialLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _deskLayer.AddChild(_memorialLayer);
        RenderMemorials();

        _notice = AddLabel(_deskLayer, "", new Vector2(525, 728), 12, "#CBB994");
        _notice.Name = "SimulationNotice";
        _notice.Size = new Vector2(620, 28);
        _notice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _memorialSheet = BuildMemorialSheet();
        _decreeSheet = BuildDecreeSheet();
    }

    private void RenderMemorials()
    {
        foreach (var child in _memorialLayer.GetChildren())
        {
            _memorialLayer.RemoveChild(child);
            child.QueueFree();
        }

        for (var index = 0; index < _readModel.PendingMemorials.Count; index++)
            AddDeskMemorial(_readModel.PendingMemorials[index], index);

        if (IsInstanceValid(_memorialSheet)) _memorialSheet.Visible = false;
        if (IsInstanceValid(_readModelNotice))
            _readModelNotice.Text = $"{_readModel.Classification} 演示数据 · {_readModel.SourceNotice}";
    }

    private void AddDeskMemorial(MemorialItemDto entry, int index)
    {
        var column = index % 4;
        var row = index / 4;
        var button = new TextureButton
        {
            Name = $"DeskMemorial-{index + 1}",
            // 背景图的真实桌面从约 y=700 开始；三封奏疏沿桌面纵深错落摆放，
            // 不再悬在窗格、灯笼或砚台前方。
            Position = new Vector2(430 + column * 260, 794 - row * 118 + (column % 2) * 18),
            Size = new Vector2(232, 104),
            Rotation = Mathf.DegToRad(column switch { 0 => -2.2f, 1 => 1.1f, 2 => -0.8f, _ => 1.8f }),
            PivotOffset = new Vector2(116, 52),
            TextureNormal = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/memorials/memorial-normal.png"),
            TextureHover = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/memorials/memorial-hover.png"),
            TexturePressed = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/memorials/memorial-selected.png"),
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.Scale,
            TooltipText = entry.Title
        };
        button.Pressed += () => OpenMemorial(entry);
        _memorialLayer.AddChild(button);
        AddStatusBadge(button, entry.Status, new Rect2(192, 4, 28, 62));
        AddLabel(button, entry.Title, new Vector2(18, 23), 16, "#4A3022", true);
        AddLabel(button, entry.Status == "OPEN" ? "待阅" : "待拟", new Vector2(180, 70), 10,
            entry.Status == "OPEN" ? "#6B302A" : "#8A5B24", true);
    }

    private Panel BuildMemorialSheet()
    {
        var sheet = new Panel
        {
            Name = "MemorialSheet",
            Position = new Vector2(452, 150),
            Size = new Vector2(696, 610),
            Visible = false
        };
        sheet.AddThemeStyleboxOverride("panel", MakePaperStyle());
        _deskLayer.AddChild(sheet);
        _sheetTitle = AddLabel(sheet, "", new Vector2(58, 48), 28, "#342A1F", true);
        _sheetMeta = AddLabel(sheet, "", new Vector2(60, 98), 13, "#816B4C");
        _sheetBody = AddLabel(sheet, "", new Vector2(60, 152), 17, "#3D352A");
        _sheetBody.Size = new Vector2(576, 300);
        _sheetBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var close = AddPaperButton(sheet, "合卷", new Rect2(60, 492, 136, 54));
        close.Name = "CloseMemorial";
        close.Pressed += () => sheet.Visible = false;
        var compare = AddPaperButton(sheet, "查看方略", new Rect2(474, 492, 162, 54));
        compare.Name = "OpenDecreeDraft";
        compare.Pressed += OpenDecreeSheet;
        return sheet;
    }

    private Panel BuildDecreeSheet()
    {
        var sheet = new Panel
        {
            Name = "EdictConfirmPanel",
            Position = new Vector2(474, 170),
            Size = new Vector2(652, 570),
            Visible = false
        };
        sheet.AddThemeStyleboxOverride("panel", MakePaperStyle());
        _deskLayer.AddChild(sheet);
        AddLabel(sheet, "御前方略 · 只读预览", new Vector2(56, 42), 27, "#342A1F", true);
        AddLabel(sheet, "辽西粮运案", new Vector2(58, 96), 17, "#7E231F", true);
        var body = AddLabel(sheet,
            "拟令户部核清漕运拨款，兵部复核陆路转输，\n待证据与权限校验通过后，方可形成 WorldIntent。\n\n此处只展示交互样式；不会推进时间，也不会直接修改世界状态。",
            new Vector2(58, 142), 17, "#3D352A");
        body.Size = new Vector2(536, 240);
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        var close = AddPaperButton(sheet, "退回奏疏", new Rect2(58, 452, 148, 56));
        close.Name = "CloseEdictConfirm";
        close.Pressed += () => sheet.Visible = false;

        var seal = AddSealButton(sheet, "朱批候核", new Rect2(414, 434, 180, 92));
        seal.Name = "ConfirmSealButton";
        seal.Pressed += () =>
        {
            _notice.Text = "朱批预览已确认 · 未提交 Intent，等待 Simulation 接入。";
            sheet.Visible = false;
        };
        return sheet;
    }

    private void OpenDecreeSheet()
    {
        _memorialSheet.Visible = false;
        _decreeSheet.Visible = true;
        _decreeSheet.MoveToFront();
        _notice.Text = "仅打开方略草案 · 未创建、未提交 Intent。";
    }

    private void OnDeskMapInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }) return;
        SetStrategicView(true);
        AcceptEvent();
    }

    private void OpenMemorial(MemorialItemDto entry)
    {
        _sheetTitle.Text = entry.Title;
        _sheetMeta.Text = $"{entry.Meta} · {entry.Status}";
        _sheetBody.Text = entry.Summary + "\n\n此页只读呈现奏报，不推进时间，不修改世界状态。";
        _memorialSheet.Visible = true;
        _memorialSheet.MoveToFront();
    }

    private void BuildStrategicMap()
    {
        _mapLayer = AddFullRectLayer("StrategicMapLayer");
        _mapLayer.MouseFilter = MouseFilterEnum.Ignore;
        _map.Position = new Vector2(0, 72);
        _map.Size = new Vector2(1600, 888);
        _map.MoveToFront();

        var topWash = new ColorRect { Position = new Vector2(0, 0), Size = new Vector2(1600, 72), Color = new Color(0.05f, 0.075f, 0.065f, 0.90f), MouseFilter = MouseFilterEnum.Ignore };
        _mapLayer.AddChild(topWash);
        AddLabel(topWash, "东亚舆图", new Vector2(32, 18), 26, "#E5D4B2", true);
        _selectedPlace = AddLabel(topWash, "所选：京师", new Vector2(220, 25), 15, "#D1B875");
        _selectedPlace.Size = new Vector2(720, 32);
        _selectedPlace.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _selectedPlaceBadge = AddStatusBadge(topWash, "OPEN", new Rect2(188, 7, 24, 58));
        _selectedPlaceBadge.Name = "SelectedPlaceStatusBadge";
        _mapNotice = AddLabel(topWash, "滚轮放大后逐级显示地名 · 拖动边界受限", new Vector2(980, 25), 13, "#9EB2A5");
        var returnButton = AddPaperButton(topWash, "收卷归案", new Rect2(1430, 14, 134, 42));
        returnButton.Name = "ReturnToDesk";
        returnButton.Pressed += () => SetStrategicView(false);
        _mapLayer.MoveToFront();

        _transitionInputBlocker = AddFullRectLayer("TransitionInputBlocker");
        _transitionInputBlocker.MouseFilter = MouseFilterEnum.Stop;
        _transitionInputBlocker.Visible = false;
        _transitionInputBlocker.MoveToFront();
    }

    private void OnPlaceSelected(string placeId)
    {
        var body = _placeDescriptions.TryGetValue(placeId, out var description) ? description : "节点信息不可用。";
        var summary = _map.SelectedPlaceSummary;
        var status = summary.Contains("OPEN", StringComparison.Ordinal) ? "OPEN" : "FACT";
        _selectedPlace.Text = $"所选：{body} · {status} · modern_anchor · approximate_point";
        _selectedPlace.TooltipText = summary;
        _selectedPlaceBadge.Texture = LoadStatusBadgeTexture(status);
    }

    private void AddBackground()
    {
        var texture = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/backgrounds/ming-imperial-study-desk-map.png");
        var background = new TextureRect
        {
            Name = "StudyBackground",
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(background);
        MoveChild(background, 0);
    }

    private Control AddFullRectLayer(string name)
    {
        var layer = new Control { Name = name };
        layer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(layer);
        return layer;
    }

    private static ColorRect AddInkWash(Control parent, string name, Rect2 rect)
    {
        var wash = new ColorRect
        {
            Name = name,
            Position = rect.Position,
            Size = rect.Size,
            Color = new Color(0.035f, 0.045f, 0.035f, 0.78f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        parent.AddChild(wash);
        return wash;
    }

    private static TextureRect AddStatusBadge(Control parent, string status, Rect2 rect)
    {
        var badge = new TextureRect
        {
            Name = $"StatusBadge-{status}",
            Position = rect.Position,
            Size = rect.Size,
            Texture = LoadStatusBadgeTexture(status),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            TooltipText = $"证据语义：{status}"
        };
        parent.AddChild(badge);
        return badge;
    }

    private static Texture2D LoadStatusBadgeTexture(string status)
    {
        var fileName = status switch
        {
            "FACT" => "badge-fact.png",
            "DESIGN" => "badge-design.png",
            _ => "badge-open.png"
        };
        return GD.Load<Texture2D>($"res://assets/ui/generated/ming_ui_v2/badges/{fileName}");
    }

    private Label AddLabel(Control parent, string text, Vector2 position, int size, string color, bool title = false)
    {
        var label = new Label { Text = text, Position = position, MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", title ? _titleFont : _bodyFont);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", new Color(color));
        label.AddThemeColorOverride("font_outline_color", new Color(0.07f, 0.06f, 0.045f, title ? 0.78f : 0.52f));
        label.AddThemeConstantOverride("outline_size", title ? 3 : 1);
        parent.AddChild(label);
        return label;
    }

    private Button AddPaperButton(Control parent, string text, Rect2 rect)
    {
        var button = new Button { Text = text, Position = rect.Position, Size = rect.Size, FocusMode = FocusModeEnum.All };
        button.AddThemeFontOverride("font", _bodyFont);
        button.AddThemeFontSizeOverride("font_size", 16);
        button.AddThemeColorOverride("font_color", new Color("#35291E"));
        button.AddThemeStyleboxOverride("normal", MakeTextureStyle("buttons/primary-normal.png", 10, 12));
        button.AddThemeStyleboxOverride("hover", MakeTextureStyle("buttons/primary-hover.png", 10, 12));
        button.AddThemeStyleboxOverride("pressed", MakeTextureStyle("buttons/primary-selected.png", 10, 12));
        button.AddThemeStyleboxOverride("disabled", MakeTextureStyle("buttons/primary-disabled.png", 10, 12));
        parent.AddChild(button);
        return button;
    }

    private Button AddSealButton(Control parent, string text, Rect2 rect)
    {
        var button = new Button { Text = text, Position = rect.Position, Size = rect.Size, FocusMode = FocusModeEnum.All };
        button.AddThemeFontOverride("font", _titleFont);
        button.AddThemeFontSizeOverride("font_size", 19);
        button.AddThemeColorOverride("font_color", new Color("#F4E5C5"));
        button.AddThemeColorOverride("font_disabled_color", new Color("#4A241C"));
        button.AddThemeColorOverride("font_outline_color", new Color("#2A1510"));
        button.AddThemeColorOverride("font_disabled_outline_color", new Color("#F1DEC0"));
        button.AddThemeConstantOverride("outline_size", 2);
        button.AddThemeStyleboxOverride("normal", MakeTextureStyle("buttons/seal-normal.png", 22, 8));
        button.AddThemeStyleboxOverride("hover", MakeTextureStyle("buttons/seal-hover.png", 22, 8));
        button.AddThemeStyleboxOverride("pressed", MakeTextureStyle("buttons/seal-pressed.png", 22, 8));
        button.AddThemeStyleboxOverride("disabled", MakeTextureStyle("buttons/seal-disabled.png", 22, 8));
        parent.AddChild(button);
        return button;
    }

    /// <summary>纸张九宫格样式；奏疏、政令、复盘等面板共用（两个以上真实消费者）。</summary>
    public static StyleBoxTexture MakePaperStyle()
    {
        var style = new StyleBoxTexture
        {
            Texture = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/cards/ming-booklet-paper-ninepatch.png"),
            AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch,
            AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
            DrawCenter = true
        };
        // 源图的左侧题签、右上折角和四周纸边都位于固定区；中央空白纸面才允许拉伸。
        // 两个真实消费者分别为 696x610 与 652x570，以下边距之和均小于目标尺寸。
        style.SetTextureMargin(Side.Left, 220);
        style.SetTextureMargin(Side.Top, 210);
        style.SetTextureMargin(Side.Right, 330);
        style.SetTextureMargin(Side.Bottom, 90);
        style.SetContentMarginAll(32);
        return style;
    }

    /// <summary>纹理按钮九宫格样式；MainUi 与面板共用。</summary>
    public static StyleBoxTexture MakeTextureStyle(string relativePath, float margin, float contentMargin)
    {
        var style = new StyleBoxTexture
        {
            Texture = GD.Load<Texture2D>($"res://assets/ui/generated/ming_ui_v2/{relativePath}"),
            AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch,
            AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
            DrawCenter = true
        };
        style.SetTextureMarginAll(margin);
        style.SetContentMarginAll(contentMargin);
        return style;
    }

    private void ApplyTransitionProgress(float progress)
    {
        _transitionProgress = Mathf.Clamp(progress, 0.0f, 1.0f);
        var amount = Mathf.Lerp(_transitionStartAmount, _transitionEndAmount, _transitionProgress);
        ApplyStrategicVisualAmount(amount);
    }

    private void ApplyStrategicVisualAmount(float amount)
    {
        _strategicVisualAmount = Mathf.Clamp(amount, 0.0f, 1.0f);
        _map.Position = DeskMapRect.Position.Lerp(StrategicMapRect.Position, _strategicVisualAmount);
        _map.Size = DeskMapRect.Size.Lerp(StrategicMapRect.Size, _strategicVisualAmount);
        _deskLayer.Modulate = new Color(1, 1, 1, 1.0f - _strategicVisualAmount);
        _mapLayer.Modulate = new Color(1, 1, 1, _strategicVisualAmount);
        _map.Modulate = new Color(1, 1, 1, Mathf.Lerp(0.18f, 1.0f, _strategicVisualAmount));
    }

    private void CompleteTransition()
    {
        _transitionProgress = 1.0f;
        ApplyStrategicVisualAmount(_transitionTargetStrategic ? 1.0f : 0.0f);
        _transitioning = false;
        _transitionInputBlocker.Visible = false;

        _deskLayer.Visible = !_transitionTargetStrategic;
        _mapLayer.Visible = _transitionTargetStrategic;
        _map.Visible = _transitionTargetStrategic;
        _map.MouseFilter = _transitionTargetStrategic ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        UpdateDeskNotice();
    }

    private void ApplyStrategicStateImmediately(bool enabled)
    {
        _transitionTween?.Kill();
        _strategicView = enabled;
        _transitionTargetStrategic = enabled;
        _transitioning = false;
        _transitionProgress = 1.0f;
        _transitionInputBlocker.Visible = false;
        if (enabled) _map.EnterStrategicView();
        else _map.ExitStrategicView();
        ApplyStrategicVisualAmount(enabled ? 1.0f : 0.0f);
        _deskLayer.Visible = !enabled;
        _mapLayer.Visible = enabled;
        _map.Visible = enabled;
        _map.MouseFilter = enabled ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
    }

    /// <summary>
    /// 实时内核快捷栏：顶栏显示权威 GameTime/WorldVersion/库存概要，提供暂停、倍速与
    /// 调粮按钮。所有写入都经 CommandFacade 进入唯一内核管线，UI 不触碰 WorldState。
    /// </summary>
    private void BuildRealtimeBar()
    {
        try
        {
            var (runtime, facade) = RealtimeWorldBridge.Create();
            _runtime = runtime;
            _facade = facade;
        }
        catch (Exception exception)
        {
            // 剧本装配失败必须可见（fail-closed），不能静默退回演示数据。
            _runtime = null;
            _facade = null;
            GD.PushError($"宁远 1629 剧本装配失败：{exception.Message}");
        }

        var bar = new Control { Name = "RealtimeBar", Position = new Vector2(0, 812), Size = new Vector2(1600, 88) };
        var wash = new ColorRect { Position = Vector2.Zero, Size = bar.Size, Color = new Color(0.05f, 0.075f, 0.065f, 0.92f), MouseFilter = MouseFilterEnum.Ignore };
        bar.AddChild(wash);
        AddChild(bar);

        _realtimeClock = AddLabel(bar, "内核未接入", new Vector2(16, 8), 15, "#E4CB91");
        _realtimeClock.Name = "RealtimeClock";
        _realtimeStockpiles = AddLabel(bar, "", new Vector2(16, 36), 12, "#B4C9BF");
        _realtimeStockpiles.Name = "RealtimeStockpiles";
        _realtimeStockpiles.Size = new Vector2(1020, 40);
        _realtimeOutcome = AddLabel(bar, "", new Vector2(16, 64), 12, "#D9A05B");
        _realtimeOutcome.Name = "RealtimeOutcome";
        _realtimeOutcome.Size = new Vector2(1200, 20);
        _realtimeOutcome.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;

        var pause = AddPaperButton(bar, "暂停", new Rect2(1060, 10, 80, 34));
        pause.Name = "RealtimePause";
        pause.Pressed += () => SubmitPause(true);
        var resume = AddPaperButton(bar, "继续", new Rect2(1148, 10, 80, 34));
        resume.Name = "RealtimeResume";
        resume.Pressed += () => SubmitPause(false);
        (string Text, double Value)[] speeds = [("1x", 1.0), ("2x", 2.0), ("3x", 3.0), ("5x", 5.0)];
        for (var index = 0; index < speeds.Length; index++)
        {
            var speed = speeds[index];
            var button = AddPaperButton(bar, speed.Text, new Rect2(1236 + index * 64, 10, 56, 34));
            button.Name = $"RealtimeSpeed{speed.Text}";
            button.Pressed += () => SubmitSpeed(speed.Value);
        }
        var dispatch = AddPaperButton(bar, "调粮五千石", new Rect2(1060, 48, 252, 34));
        dispatch.Name = "RealtimeDispatchGrain";
        dispatch.Pressed += SubmitGrainShipment;
        var decreeButton = AddPaperButton(bar, "政令", new Rect2(1320, 48, 88, 34));
        decreeButton.Name = "OpenDecreePanel";
        decreeButton.Pressed += OpenDecreePanel;
        var endgameButton = AddPaperButton(bar, "终局复盘", new Rect2(1416, 48, 128, 34));
        endgameButton.Name = "OpenEndgameReport";
        endgameButton.Pressed += OpenEndgameReport;
        var guideButton = AddPaperButton(bar, "新手引导", new Rect2(1488, 10, 112, 34));
        guideButton.Name = "OpenGuidePanel";
        guideButton.Pressed += OpenGuidePanel;

        RefreshRealtimeLabels();
    }

    private void OpenGuidePanel()
    {
        if (!IsInstanceValid(_guidePanel))
        {
            return;
        }
        _decreePanel.Close();
        _endgameReportPanel.Close();
        _guidePanel.Open();
    }

    private void OpenDecreePanel()
    {
        _endgameReportPanel.Close();
        _guidePanel.Close();
        _decreePanel.Open();
    }

    private void OpenEndgameReport()
    {
        _decreePanel.Close();
        _guidePanel.Close();
        _endgameReportPanel.Open();
    }

    private void SubmitPause(bool paused)
    {
        _uiPauseRequested = paused;
        _facade?.EnqueuePause(
            paused, new CharacterId("zhu-youjian"), _runtime?.ReadModel.GameTime.Value ?? default, _runtime?.ReadModel.WorldVersion ?? 0);
    }

    private void SubmitSpeed(double speed) => _facade?.EnqueueSetSpeed(
        speed, new CharacterId("zhu-youjian"), _runtime?.ReadModel.GameTime.Value ?? default, _runtime?.ReadModel.WorldVersion ?? 0);

    private void SubmitGrainShipment()
    {
        if (_runtime is null || _facade is null) return;
        var model = _runtime.ReadModel;
        _facade.EnqueueCreateShipment(
            $"ui-grain-{model.WorldVersion}-5000",
            new CharacterId("duliaoxiang-slot"),
            new ShipmentId($"shipment-ui-grain-{model.WorldVersion}"),
            new RouteId("route-shanhaiguan-ningyuan"),
            5000,
            escort: false,
            model.GameTime.Value,
            model.WorldVersion);
        RefreshRealtimeLabels();
    }

    private void RefreshRealtimeLabels()
    {
        if (_runtime is null) return;
        var model = _runtime.ReadModel;
        _realtimeClock.Text =
            $"崇祯二年 · {model.GameTime.Value:yyyy-MM-dd HH:mm} · WorldVersion {model.WorldVersion} · " +
            $"{(_uiPauseRequested ? "已暂停" : "运行中")} · 战备 {model.Readiness.Value} · 负担 {model.Scenario.LocalBurden} · 信任 {model.Scenario.MinisterTrust}";
        var stocks = string.Join(" · ", model.Stockpiles
            .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
            .Select(item => $"{item.Id.Value.Replace("sp-", "", StringComparison.Ordinal)}:{item.GrainQuantity}石"));
        _realtimeStockpiles.Text = $"已知库存（DESIGN 数值） {stocks} · 在途 {model.Shipments.Count(item => item.Status != ShipmentStatus.Arrived)} 批";
        var latest = model.CommandOutcomes.LastOrDefault();
        _realtimeOutcome.Text = latest is null
            ? "尚无命令结果。"
            : $"最近命令 {latest.CommandId}：{(latest.Accepted ? "受理" : "拒绝")} · {string.Join("；", latest.ErrorCodes.Select(CommandFailureText.Translate))}";
    }

    private void UpdateDeskNotice()
    {
        if (!IsInstanceValid(_notice)) return;
        _notice.Text = _strategicView
            ? "舆图正在铺展或已铺展 · 仅作显示动画，不控制 GameTime。"
            : $"御案上有 {PendingMemorialCount} 封待阅奏疏 · {_readModel.Classification} · {_readModel.SourceNotice}。";
    }
}
