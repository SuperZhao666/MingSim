using System;
using Godot;
using MingSim.Simulation.Realtime;

namespace Ming.Godot;

/// <summary>
/// 终局复盘面板（M3 御案补全）：调用内核只读评估 <see cref="RealtimeSimulationRuntime.EvaluateEndgame"/>，
/// 展示结局分档（doc 03 §7.2）、硬失败原因与六维解释（doc 03 §7.3）。
/// 面板不持有任何权威可写状态，也不推进 GameTime；评估本身是内核的只读快照函数。
/// </summary>
public partial class EndgameReportPanel : Panel
{
    private Font _titleFont = null!;
    private Font _bodyFont = null!;
    private Label _outcomeLabel = null!;
    private Label _failureLabel = null!;
    private Label _body = null!;
    private RealtimeSimulationRuntime? _runtime;

    /// <summary>终局正文（六维解释），供 headless 验收断言维度子串。</summary>
    public string ReportText => IsInstanceValid(_body) ? _body.Text : "";

    public bool PanelOpen => Visible;

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

        var title = AddLabel(this, "终局复盘（EvaluateEndgame）", new Vector2(56, 40), 27, "#342A1F", true);
        title.Name = "EndgameReportTitle";
        var provenance = AddLabel(this, "分档门槛为 doc 03 §7.2 首轮调参基线（DESIGN）· 评估来自内核只读快照",
            new Vector2(56, 86), 13, "#816B4C");
        provenance.Name = "EndgameProvenance";
        _outcomeLabel = AddLabel(this, "结局分档：—", new Vector2(56, 124), 20, "#342A1F", true);
        _outcomeLabel.Name = "EndgameOutcomeLabel";
        _failureLabel = AddLabel(this, "失败原因：—", new Vector2(56, 160), 15, "#7E231F");
        _failureLabel.Name = "EndgameFailureLabel";
        _body = AddLabel(this, "", new Vector2(56, 200), 16, "#3D352A");
        _body.Name = "EndgameReportBody";
        _body.Size = new Vector2(628, 340);
        _body.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        // 水墨插画（候选资产，纯展示）：加载失败时静默跳过，不影响面板功能。
        var art = GD.Load<Texture2D>("res://assets/ui/generated/ming_ui_v2/art/panels/endgame-review.png");
        if (art != null)
        {
            // 注意：ExpandMode 必须先于 Texture/Size 赋值，否则 Size 会被纹理最小尺寸钳制。
            var illustration = new TextureRect
            {
                Name = "EndgameArtIllustration",
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
                Texture = art,
                Position = new Vector2(568, 20),
                Size = new Vector2(148, 107),
            };
            AddChild(illustration);
        }

        var close = AddButton(this, "合上复盘", new Rect2(560, 548, 128, 40));
        close.Name = "CloseEndgameReport";
        close.Pressed += Close;
    }

    /// <summary>注入内核只读视图（MainUi 在 BuildRealtimeBar 之后调用）。</summary>
    public void ConnectRuntime(RealtimeSimulationRuntime? runtime)
    {
        _runtime = runtime;
        Refresh();
    }

    public void Open()
    {
        Visible = true;
        MoveToFront();
        Refresh();
    }

    public void Close() => Visible = false;

    private void Refresh()
    {
        if (_runtime is null)
        {
            _outcomeLabel.Text = "结局分档：内核未接入";
            _failureLabel.Text = "失败原因：—";
            _body.Text = "无法读取终局评估：宁远 1629 剧本未装配。";
            return;
        }

        var evaluation = _runtime.EvaluateEndgame();
        _outcomeLabel.Text = $"结局分档：{OutcomeText(evaluation.Outcome)}";
        _failureLabel.Text = $"失败原因：{evaluation.HardFailureReason ?? "无（当前未触发硬失败）"}";
        _body.Text = evaluation.Explanation;
    }

    /// <summary>分档用语对齐 doc 03 §7.2 结局表。</summary>
    private static string OutcomeText(EndgameOutcome outcome) => outcome switch
    {
        EndgameOutcome.InProgress => "进行中（尚未到 90 日终局判定时点）",
        EndgameOutcome.HardFailure => "硬失败",
        EndgameOutcome.BarelyMaintained => "勉强维持",
        EndgameOutcome.Success => "成功",
        EndgameOutcome.Excellent => "优秀",
        EndgameOutcome.Failed => "失败（未达勉强维持门槛）",
        _ => outcome.ToString(),
    };

    private Label AddLabel(Control parent, string text, Vector2 position, int size, string color, bool title = false)
    {
        var label = new Label { Text = text, Position = position, MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", title ? _titleFont : _bodyFont);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", new Color(color));
        parent.AddChild(label);
        return label;
    }

    private Button AddButton(Control parent, string text, Rect2 rect)
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
