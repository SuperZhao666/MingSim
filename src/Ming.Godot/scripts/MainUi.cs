using Godot;
using System;
using System.Collections.Generic;

namespace Ming.Godot;

/// <summary>
/// 御书房页面壳：玩家面对御案，桌面物件才是入口；中央舆图滚轮放大后进入策略地图。
/// 这里仍只改展示状态，不推进 GameTime，也不写 WorldState。
/// </summary>
public partial class MainUi : Control
{
    private sealed record MemorialEntry(string Id, string Title, string Meta, string Summary, string Status);

    private readonly List<MemorialEntry> _pendingMemorials =
    [
        new("liaoxi", "辽西急报", "宁远 · 卯初送达", "前线粮秣告急，须核漕运与陆路转输。", "OPEN"),
        new("revenue", "户部请旨", "京师 · 昨日申刻", "漕运拨款待决定，容量与承办人尚待核验。", "DESIGN"),
        new("censor", "御史弹章", "京师 · 昨日辰刻", "证据不足，暂列待复核。", "OPEN")
    ];

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
    private Control _mapLayer = null!;
    private Panel _memorialSheet = null!;
    private Panel _decreeSheet = null!;
    private Label _sheetTitle = null!;
    private Label _sheetMeta = null!;
    private Label _sheetBody = null!;
    private Label _notice = null!;
    private Label _mapNotice = null!;
    private Label _selectedPlace = null!;
    private Font _titleFont = null!;
    private Font _bodyFont = null!;
    private bool _strategicView;

    public int PendingMemorialCount => _pendingMemorials.Count;
    public bool StrategicView => _strategicView;
    public bool MemorialOpen => _memorialSheet.Visible;

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
        SetStrategicView(false);
    }

    public override void _GuiInput(InputEvent @event)
    {
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
        _strategicView = enabled;
        _deskLayer.Visible = !enabled;
        _mapLayer.Visible = enabled;
        _map.Visible = enabled;
        _map.MouseFilter = enabled ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        if (enabled) _map.EnterStrategicView();
        else _map.ExitStrategicView();
        _notice.Text = enabled
            ? "舆图已铺展 · 滚轮缩放，拖动移图；缩到最远再向下滚轮返回御案。"
            : $"御案上有 {_pendingMemorials.Count} 封待阅奏疏 · 点击翻阅；在中央舆图上向上滚轮进入策略地图。";
    }

    private void BuildDesk()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddBackground();
        _deskLayer = AddFullRectLayer("DeskLayer");

        AddLabel(_deskLayer, "大明御书房", new Vector2(54, 34), 30, "#E8D8B9", true);
        AddLabel(_deskLayer, "崇祯二年 · 春分前后", new Vector2(58, 76), 14, "#BBA98D");
        AddLabel(_deskLayer, "1629-03-18 06:00", new Vector2(1280, 44), 18, "#D7BC7B");
        AddLabel(_deskLayer, "演示预览 · 未接 Simulation", new Vector2(1282, 74), 12, "#8AA79D");

        var deskMapTexture = GD.Load<Texture2D>("res://assets/maps/generated/ming_1629/physical-base.png");
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

        for (var index = 0; index < _pendingMemorials.Count; index++)
            AddDeskMemorial(_pendingMemorials[index], index);

        _notice = AddLabel(_deskLayer, "", new Vector2(525, 728), 12, "#CBB994");
        _notice.Name = "SimulationNotice";
        _notice.Size = new Vector2(620, 28);
        _notice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _memorialSheet = BuildMemorialSheet();
        _decreeSheet = BuildDecreeSheet();
    }

    private void AddDeskMemorial(MemorialEntry entry, int index)
    {
        var button = new TextureButton
        {
            Name = $"DeskMemorial-{index + 1}",
            // 背景图的真实桌面从约 y=700 开始；三封奏疏沿桌面纵深错落摆放，
            // 不再悬在窗格、灯笼或砚台前方。
            Position = new Vector2(430 + index * 285, 794 + (index % 2) * 18),
            Size = new Vector2(232, 104),
            Rotation = Mathf.DegToRad(index switch { 0 => -2.2f, 1 => 1.1f, _ => -0.8f }),
            PivotOffset = new Vector2(116, 52),
            TextureNormal = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/memorials/memorial-normal.png"),
            TextureHover = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/memorials/memorial-hover.png"),
            TexturePressed = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/memorials/memorial-selected.png"),
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.Scale,
            TooltipText = entry.Title
        };
        button.Pressed += () => OpenMemorial(entry);
        _deskLayer.AddChild(button);
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

    private void OpenMemorial(MemorialEntry entry)
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
        _mapNotice = AddLabel(topWash, "滚轮放大后逐级显示地名 · 拖动边界受限", new Vector2(980, 25), 13, "#9EB2A5");
        var returnButton = AddPaperButton(topWash, "收卷归案", new Rect2(1430, 14, 134, 42));
        returnButton.Name = "ReturnToDesk";
        returnButton.Pressed += () => SetStrategicView(false);
        _mapLayer.MoveToFront();
    }

    private void OnPlaceSelected(string placeId)
    {
        var body = _placeDescriptions.TryGetValue(placeId, out var description) ? description : "节点信息不可用。";
        _selectedPlace.Text = "所选：" + body;
    }

    private void AddBackground()
    {
        var texture = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/backgrounds/ming-imperial-study-desk-map.png");
        var background = new TextureRect
        {
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

    private TextureRect AddImage(Control parent, string path, Rect2 rect)
    {
        var image = new TextureRect
        {
            Texture = GD.Load<Texture2D>(path),
            Position = rect.Position,
            Size = rect.Size,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        parent.AddChild(image);
        return image;
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
        button.AddThemeStyleboxOverride("normal", MakeTextureStyle("buttons/seal-normal.png", 22, 8));
        button.AddThemeStyleboxOverride("hover", MakeTextureStyle("buttons/seal-hover.png", 22, 8));
        button.AddThemeStyleboxOverride("pressed", MakeTextureStyle("buttons/seal-pressed.png", 22, 8));
        button.AddThemeStyleboxOverride("disabled", MakeTextureStyle("buttons/seal-disabled.png", 22, 8));
        parent.AddChild(button);
        return button;
    }

    private static StyleBoxTexture MakePaperStyle()
    {
        var style = new StyleBoxTexture
        {
            Texture = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/cards/ming-booklet-paper-ninepatch.png"),
            AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch,
            AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
            DrawCenter = true
        };
        style.SetTextureMarginAll(38);
        style.SetContentMarginAll(32);
        return style;
    }

    private static StyleBoxTexture MakeTextureStyle(string relativePath, float margin, float contentMargin)
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
}
