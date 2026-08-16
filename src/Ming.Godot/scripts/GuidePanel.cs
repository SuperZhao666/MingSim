using Godot;

namespace Ming.Godot;

/// <summary>
/// 新手引导面板（M3）：仅展示引导文案与步骤切换，不写 WorldState、 不推进游戏时间，不新增时钟。
/// 使用现有按钮作为引导目标（调粮 / 政令 / 终局复盘），只提示交互入口。
/// </summary>
public partial class GuidePanel : Panel
{
    private sealed record GuideStep(string Heading, string Body, string ActionHint);

    private readonly GuideStep[] _steps =
    [
        new(
            "先看底部横栏：认识当下局势",
            "先读屏幕底部横栏：左侧是当前时间与库存，紧接着是战备、负担、信任。它是只读快照，不是你直接控制全局时钟。",
            "回到主界面后，观察“崇祯二年 …”这类 ReadModel 文案。"
        ),
        new(
            "发布一批调粮",
            "先按按钮“调粮五千石”发起一次示范调粮。内核只接受命令门面，不会由面板直接改世界状态。",
            "点击底部横栏右侧的“调粮五千石”按钮。"
        ),
        new(
            "看一眼政令模板",
            "去政令面板，查看可复用模板与草拟草稿，确认命令是按模板提交的，而不是手写字符串。",
            "点击“政令”，面板会展示 world.json 的模板并可直接提交。"
        ),
        new(
            "看终局六维说明",
            "最后打开终局复盘，确认结局分档与六维指标如何描述“前线粮运”“军备”“财政”等现实语义。",
            "点击“终局复盘”，查看“终局复盘”正文后继续。"
        ),
    ];

    private Font _titleFont = null!;
    private Font _bodyFont = null!;
    private Label _heading = null!;
    private Label _content = null!;
    private Label _actionHint = null!;
    private Button _next = null!;
    private Button _skip = null!;
    private Button _close = null!;
    private int _index;

    public bool PanelOpen => Visible;
    public int CurrentStep => _index + 1;
    public int TotalSteps => _steps.Length;

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

        AddThemeStyleboxOverride("panel", MainUi.MakePaperStyle());
        AddThemeConstantOverride("shadow_size", 0);
        // 锚点保持场景默认的 TopLeft；此前误用 Center 预设会让 Position 叠加上半屏偏移，面板整体被移出视口。
        Size = new Vector2(760, 560);
        Position = new Vector2(420, 200);
        CustomMinimumSize = Size;
        Visible = false;

        // 顶部水墨横幅与小导师肖像（候选资产，纯展示）：加载失败时静默跳过，不影响引导功能。
        var portraitArt = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/art/panels/minister-portrait.png");
        if (portraitArt != null)
        {
            // 注意：ExpandMode 必须先于 Texture/Size 赋值，否则 Size 会被纹理最小尺寸钳制。
            var portrait = new TextureRect
            {
                Name = "GuideArtPortrait",
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
                Texture = portraitArt,
                Position = new Vector2(52, 8),
                Size = new Vector2(72, 76),
            };
            AddChild(portrait);
        }
        var bannerArt = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/art/panels/guide-banner.png");
        if (bannerArt != null)
        {
            var banner = new TextureRect
            {
                Name = "GuideArtBanner",
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = MouseFilterEnum.Ignore,
                Texture = bannerArt,
                Position = new Vector2(140, 8),
                Size = new Vector2(560, 68),
            };
            AddChild(banner);
        }

        _heading = AddLabel(this, "", new Vector2(52, 88), 28, "#342A1F", true);
        _heading.Name = "GuideStepTitle";
        _content = AddLabel(this, "", new Vector2(52, 136), 18, "#3D352A");
        _content.Name = "GuideStepBody";
        _content.Size = new Vector2(660, 228);
        _content.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        _actionHint = AddLabel(this, "", new Vector2(52, 372), 14, "#8A5B24");
        _actionHint.Name = "GuideStepHint";
        _actionHint.Size = new Vector2(660, 108);
        _actionHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        _next = AddPaperButton(this, "下一步", new Rect2(548, 500, 128, 40));
        _next.Name = "GuideNext";
        _next.Pressed += OnNextPressed;

        _skip = AddPaperButton(this, "跳过", new Rect2(404, 500, 108, 40));
        _skip.Name = "GuideSkip";
        _skip.Pressed += () => Close();

        _close = AddPaperButton(this, "关闭", new Rect2(52, 500, 108, 40));
        _close.Name = "GuideClose";
        _close.Pressed += () => Close();
        Refresh();
    }

    public void Open()
    {
        _index = 0;
        Refresh();
        Visible = true;
        MoveToFront();
    }

    public void Close()
    {
        Visible = false;
    }

    private void OnNextPressed()
    {
        if (_index + 1 >= _steps.Length)
        {
            Close();
            return;
        }

        _index++;
        Refresh();
    }

    private void Refresh()
    {
        if (!IsInstanceValid(_heading) || !IsInstanceValid(_content) || !IsInstanceValid(_actionHint) || !IsInstanceValid(_next))
        {
            return;
        }

        var step = _steps[_index];
        _heading.Text = $"新手引导（{_index + 1}/{_steps.Length}）· {step.Heading}";
        _content.Text = step.Body;
        _actionHint.Text = $"引导动作：{step.ActionHint}";
        _next.Text = _index + 1 >= _steps.Length ? "完成" : "下一步";
    }

    private Label AddLabel(Control parent, string text, Vector2 position, int size, string color, bool title = false)
    {
        var label = new Label { Text = text, Position = position, MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", title ? _titleFont : _bodyFont);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", new Color(color));
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        parent.AddChild(label);
        return label;
    }

    private Button AddPaperButton(Control parent, string text, Rect2 rect)
    {
        var button = new Button { Text = text, Position = rect.Position, Size = rect.Size, FocusMode = FocusModeEnum.All };
        button.AddThemeFontOverride("font", _bodyFont);
        button.AddThemeFontSizeOverride("font_size", 16);
        button.AddThemeColorOverride("font_color", new Color("#35291E"));
        button.AddThemeStyleboxOverride("normal", MainUi.MakeTextureStyle("buttons/primary-normal.png", 10, 12));
        button.AddThemeStyleboxOverride("hover", MainUi.MakeTextureStyle("buttons/primary-hover.png", 10, 12));
        button.AddThemeStyleboxOverride("pressed", MainUi.MakeTextureStyle("buttons/primary-selected.png", 10, 12));
        button.AddThemeStyleboxOverride("disabled", MainUi.MakeTextureStyle("buttons/primary-disabled.png", 10, 12));
        parent.AddChild(button);
        return button;
    }
}
