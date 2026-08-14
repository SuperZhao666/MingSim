using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using MingSim.Application.Commands;
using MingSim.Domain.Common;
using MingSim.Domain.Decrees;
using MingSim.Domain.Realtime;
using MingSim.Simulation.Realtime;

namespace Ming.Godot;

/// <summary>
/// 政令草拟与执行状态面板（M3 御案补全）：
/// 草拟模板取自 world.json 的 decrees[]（DESIGN 文书模板，只读内容文件），
/// 提交经 <see cref="CommandFacade.EnqueueCreateDecree"/> 进入唯一内核管线，
/// 政令列表只读 <see cref="RealtimeReadModel.Decrees"/> 显示状态。
/// 本面板不持有任何权威可写状态：不碰 WorldState、不推进 GameTime、不自己判规则。
/// </summary>
public partial class DecreePanel : Panel
{
    /// <summary>world.json decrees[] 的模板最小视图（内容格式，不是运行时领域对象）。</summary>
    private sealed record DecreeTemplate(
        string Id, string Kind, string Name, string Description, IReadOnlyList<string> Fields);

    private Font _titleFont = null!;
    private Font _bodyFont = null!;
    private Label _subtitleLabel = null!;
    private OptionButton _templateSelect = null!;
    private VBoxContainer _fieldsBox = null!;
    private Button _submitButton = null!;
    private Label _resultLabel = null!;
    private Label _listHeader = null!;
    private VBoxContainer _decreeList = null!;
    private List<DecreeTemplate> _templates = [];
    private readonly List<(string Field, LineEdit Edit)> _fieldEdits = [];
    private RealtimeSimulationRuntime? _runtime;
    private CommandFacade? _facade;
    private int _draftSequence;
    private string? _lastCommandId;
    private string _listSignature = "";

    /// <summary>只读 ReadModel 中的政令总数（供 headless 验收与调试）。</summary>
    public int DecreeCount => _runtime is null ? 0 : _runtime.ReadModel.Decrees.Count;

    /// <summary>面板当前渲染的政令行数。</summary>
    public int RenderedDecreeCount => IsInstanceValid(_decreeList) ? _decreeList.GetChildCount() : 0;

    public bool PanelOpen => Visible;

    public override void _Ready()
    {
        _titleFont = MakeFont("华文楷体", "KaiTi", "楷体");
        _bodyFont = MakeFont("华文仿宋", "FangSong", "仿宋");
        AddThemeStyleboxOverride("panel", MainUi.MakePaperStyle());

        var title = AddLabel(this, "政令草拟与执行状态", new Vector2(56, 36), 27, "#342A1F", true);
        title.Name = "DecreePanelTitle";
        _subtitleLabel = AddLabel(this, "", new Vector2(56, 84), 13, "#816B4C");
        _subtitleLabel.Size = new Vector2(668, 36);
        _subtitleLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        var templateCaption = AddLabel(this, "草拟模板", new Vector2(56, 122), 15, "#3D352A");
        templateCaption.Name = "DecreeTemplateCaption";
        _templateSelect = new OptionButton
        {
            Name = "DecreeTemplateSelect",
            Position = new Vector2(164, 116),
            Size = new Vector2(300, 32),
        };
        _templateSelect.AddThemeFontOverride("font", _bodyFont);
        _templateSelect.AddThemeFontSizeOverride("font_size", 15);
        _templateSelect.ItemSelected += index => RebuildFields((int)index);
        AddChild(_templateSelect);

        // 字段区按最宽的拨饷令模板（9 个字段）预留高度：9 × (行高 26 + 间距 4) = 270。
        _fieldsBox = new VBoxContainer
        {
            Name = "DraftFields",
            Position = new Vector2(56, 150),
            Size = new Vector2(668, 272),
        };
        _fieldsBox.AddThemeConstantOverride("separation", 3);
        AddChild(_fieldsBox);

        var hint = AddLabel(this, "承办人/请饷人填角色编号：duliaoxiang-slot（持调粮能力）、hubu-slot（持财粮会计）",
            new Vector2(56, 420), 12, "#8A5B24");
        hint.Name = "DecreeDraftHint";
        hint.CustomMinimumSize = new Vector2(668, 26);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        _submitButton = AddButton(this, "提交政令", new Rect2(56, 456, 220, 44));
        _submitButton.Name = "SubmitDecree";
        _submitButton.Pressed += SubmitDecree;

        _resultLabel = AddLabel(this, "", new Vector2(56, 504), 13, "#7E231F");
        _resultLabel.Name = "DecreeSubmitResult";
        _resultLabel.Size = new Vector2(668, 32);
        _resultLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        _listHeader = AddLabel(this, "已提交政令（状态来自只读 ReadModel）", new Vector2(56, 542), 15, "#3D352A", true);
        _listHeader.Name = "DecreeListHeader";
        _decreeList = new VBoxContainer
        {
            Name = "DecreeList",
            Position = new Vector2(56, 568),
            Size = new Vector2(668, 122),
        };
        _decreeList.AddThemeConstantOverride("separation", 3);
        AddChild(_decreeList);

        var close = AddButton(this, "合上政令簿", new Rect2(616, 32, 132, 40));
        close.Name = "CloseDecreePanel";
        close.Pressed += Close;

        LoadTemplates();
    }

    /// <summary>
    /// 注入运行时只读视图与命令门面（MainUi 在 BuildRealtimeBar 之后调用）。
    /// 为什么在外部注入而不是自己 new：全场景只能由 RealtimeWorldBridge 装配一份 runtime，
    /// 面板自行 new 会产生第二套世界权威（doc 04 §3 / RealtimeWorldBridge 注释）。
    /// </summary>
    public void ConnectRuntime(RealtimeSimulationRuntime? runtime, CommandFacade? facade)
    {
        _runtime = runtime;
        _facade = facade;
        if (_runtime is null)
        {
            _resultLabel.Text = "内核未接入：无法提交政令。";
            _submitButton.Disabled = true;
        }
        RefreshFromReadModel();
    }

    public void Open()
    {
        Visible = true;
        MoveToFront();
        RefreshFromReadModel();
    }

    public void Close() => Visible = false;

    public override void _Process(double delta)
    {
        // 每帧用轻量签名比对只读 ReadModel；只有政令集合变化才重建列表。
        RefreshFromReadModel();
    }

    private void LoadTemplates()
    {
        try
        {
            var path = RealtimeWorldBridge.ResolveWorldJsonPath();
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("decrees", out var decrees) || decrees.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("world.json 缺少 decrees 数组。");
            }

            foreach (var item in decrees.EnumerateArray())
            {
                var fields = item.TryGetProperty("fields", out var fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Array
                    ? fieldsElement.EnumerateArray().Select(field => field.GetString() ?? "").Where(text => text.Length > 0).ToArray()
                    : [];
                _templates.Add(new DecreeTemplate(
                    GetString(item, "id"),
                    GetString(item, "kind"),
                    GetString(item, "name"),
                    item.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
                    fields));
            }

            foreach (var template in _templates)
            {
                _templateSelect.AddItem($"{template.Name}（{template.Kind}）", _templateSelect.ItemCount);
            }
            _subtitleLabel.Text = "模板来自 world.json decrees[]（DESIGN 文书模板）· 提交经 CommandFacade 进入唯一内核管线";
            if (_templates.Count > 0)
            {
                RebuildFields(0);
            }
            else
            {
                _subtitleLabel.Text = "world.json 未提供政令模板（decrees[] 为空），无法草拟。";
                _submitButton.Disabled = true;
            }
        }
        catch (Exception exception)
        {
            // 模板装配失败必须可见（fail-closed），不能静默开一个空表单。
            _subtitleLabel.Text = $"政令模板加载失败：{exception.Message}";
            _submitButton.Disabled = true;
            GD.PushError($"政令模板加载失败：{exception.Message}");
        }
    }

    private void RebuildFields(int templateIndex)
    {
        foreach (var child in _fieldsBox.GetChildren())
        {
            _fieldsBox.RemoveChild(child);
            child.QueueFree();
        }
        _fieldEdits.Clear();
        if (templateIndex < 0 || templateIndex >= _templates.Count)
        {
            return;
        }

        foreach (var field in _templates[templateIndex].Fields)
        {
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.Fill };
            row.AddThemeConstantOverride("separation", 8);
            var caption = new Label
            {
                Text = field,
                CustomMinimumSize = new Vector2(200, 24),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            caption.AddThemeFontOverride("font", _bodyFont);
            caption.AddThemeFontSizeOverride("font_size", 13);
            caption.AddThemeColorOverride("font_color", new Color("#3D352A"));
            var edit = new LineEdit
            {
                PlaceholderText = DefaultForField(field),
                Text = DefaultForField(field),
                CustomMinimumSize = new Vector2(0, 26),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            edit.AddThemeFontOverride("font", _bodyFont);
            edit.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(caption);
            row.AddChild(edit);
            _fieldsBox.AddChild(row);
            _fieldEdits.Add((field, edit));
        }
    }

    /// <summary>按字段名给默认草拟值：让"直接点提交"就能形成一道合法结构化政令（DESIGN 默认值）。</summary>
    private static string DefaultForField(string field) =>
        field switch
        {
            _ when field.Contains("石") && (field.Contains("数量") || field.Contains("粮数") || field.Contains("需求")) => "5000",
            _ when field.Contains("期限") => "10",
            _ when field.Contains("银两预算") => "1000",
            _ when field.Contains("承办机构") || field.Contains("承办人") || field.Contains("请饷人") || field.Contains("接收方") || field.Contains("拨付机构") => "duliaoxiang-slot",
            _ => "",
        };

    /// <summary>把表单字段映射到 CreateDecreeCommand 的实际签名参数（对齐 NingyuanP0Commands.cs）。</summary>
    private void SubmitDecree()
    {
        if (_runtime is null || _facade is null)
        {
            _resultLabel.Text = "内核未接入：无法提交政令。";
            return;
        }
        if (_templates.Count == 0 || _templateSelect.Selected < 0 || _templateSelect.Selected >= _templates.Count)
        {
            _resultLabel.Text = "没有可用的政令模板（world.json decrees[] 缺失）。";
            return;
        }

        var template = _templates[_templateSelect.Selected];
        var values = _fieldEdits.ToDictionary(entry => entry.Field, entry => entry.Edit.Text.Trim(), StringComparer.Ordinal);
        var goal = ComposeGoal(template, values);
        if (goal.Length == 0)
        {
            _resultLabel.Text = "政令目标为空，内核将拒绝（INVALID_DECREE_GOAL）。";
            return;
        }

        var model = _runtime.ReadModel;
        _draftSequence++;
        var commandId = $"ui-decree-{model.WorldVersion}-{_draftSequence}";
        var responsible = FirstNonEmpty(values,
            ["承办机构", "承办人", "请饷人", "接收方", "拨付机构"]) ?? "duliaoxiang-slot";
        var budget = ParseLong(values, "银两预算") ?? 1000;
        var days = ParseInt(values, "期限") ?? 10;
        var restrictions = string.Join("、", values
            .Where(entry => (entry.Key.Contains("限制") || entry.Key.Contains("加急原因")) && entry.Value.Length > 0)
            .Select(entry => entry.Value));
        var remarks = string.Join("；", values
            .Where(entry => (entry.Key.Contains("备注") || entry.Key.Contains("理由") || entry.Key.Contains("路线")) && entry.Value.Length > 0)
            .Select(entry => entry.Value));

        var receipt = _facade.EnqueueCreateDecree(
            commandId,
            new CharacterId("zhu-youjian"),
            new DecreeId($"decree-ui-{model.WorldVersion}-{_draftSequence}"),
            goal,
            new ProvinceId("ningyuan"),
            Math.Max(1, budget),
            new CharacterId(responsible),
            new GameTime(model.GameTime.Value.AddDays(Math.Max(1, days))),
            restrictions,
            remarks,
            GameCapability.PlanLogistics,
            requiredResourceId: null,
            linkedShipmentId: null,
            model.GameTime.Value,
            model.WorldVersion);
        _lastCommandId = commandId;
        _resultLabel.Text = receipt.Queued
            ? $"已提交：{commandId}（等待内核在下一推进点接纳）"
            : $"提交被拒：{CommandFailureText.Translate(receipt.Errors.FirstOrDefault()?.Code ?? "UNKNOWN")}";
        RefreshFromReadModel();
    }

    private static string ComposeGoal(DecreeTemplate template, IReadOnlyDictionary<string, string> values)
    {
        var quantity = values
            .Where(entry => entry.Key.Contains("石") && (entry.Key.Contains("数量") || entry.Key.Contains("粮数") || entry.Key.Contains("需求")))
            .Select(entry => entry.Value)
            .FirstOrDefault(value => value.Length > 0);
        return string.IsNullOrEmpty(quantity) ? template.Name : $"{template.Name}：{quantity} 石";
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string> values, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            foreach (var entry in values)
            {
                if (entry.Key.Contains(key) && entry.Value.Length > 0)
                {
                    return entry.Value;
                }
            }
        }
        return null;
    }

    private static long? ParseLong(IReadOnlyDictionary<string, string> values, string marker)
    {
        var text = values.FirstOrDefault(entry => entry.Key.Contains(marker)).Value;
        return long.TryParse(text, out var result) ? result : null;
    }

    private static int? ParseInt(IReadOnlyDictionary<string, string> values, string marker)
    {
        var text = values.FirstOrDefault(entry => entry.Key.Contains(marker)).Value;
        return int.TryParse(text, out var result) ? result : null;
    }

    /// <summary>政令状态用语对齐 doc 09 §7 状态语言，禁止把"执行中"说成"已完成"。</summary>
    private static string StatusText(DecreeStatus status) => status switch
    {
        DecreeStatus.Draft => "草拟中，尚未生效",
        DecreeStatus.Submitted => "已提交，等待世界处理",
        DecreeStatus.Executing => "执行中",
        DecreeStatus.Completed => "已完成",
        DecreeStatus.Rejected => "未执行（被拒绝）",
        DecreeStatus.Expired => "已过期限，未执行",
        _ => status.ToString(),
    };

    private void RefreshFromReadModel()
    {
        if (_runtime is null || !IsInstanceValid(_decreeList))
        {
            return;
        }

        var model = _runtime.ReadModel;
        var signature = string.Join("|", model.Decrees.Select(decree => $"{decree.Id.Value}:{(int)decree.Status}"));
        // 签名只用于"是否需要重建列表"；提交结果回填必须独立于签名（被拒时列表不变也要显示原因）。
        if (signature != _listSignature)
        {
            _listSignature = signature;
            foreach (var child in _decreeList.GetChildren())
            {
                _decreeList.RemoveChild(child);
                child.QueueFree();
            }
            RebuildDecreeRows(model.Decrees);
        }
        UpdateSubmitResult(model.CommandOutcomes);
    }

    private void RebuildDecreeRows(IReadOnlyList<DecreeReadModel> decrees)
    {
        for (var index = 0; index < decrees.Count; index++)
        {
            var decree = decrees[index];
            var row = new Label
            {
                Name = $"DecreeRow-{index}",
                Text = $"{decree.Goal}｜{StatusText(decree.Status)}｜承办 {decree.ResponsibleActorId.Value}｜期限 {decree.Deadline.Value:MM-dd HH:mm}",
                MouseFilter = MouseFilterEnum.Ignore,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            };
            row.AddThemeFontOverride("font", _bodyFont);
            row.AddThemeFontSizeOverride("font_size", 14);
            row.AddThemeColorOverride("font_color", new Color("#3D352A"));
            _decreeList.AddChild(row);
        }
    }

    /// <summary>提交结果回填：最近一次本面板提交的命令在内核安全点处理后的 Outcome（受理/拒绝 + 中文错误码）。</summary>
    private void UpdateSubmitResult(IReadOnlyList<CommandOutcome> outcomes)
    {
        if (_lastCommandId is null)
        {
            return;
        }
        var outcome = outcomes.LastOrDefault(item => item.CommandId == _lastCommandId);
        if (outcome is not null)
        {
            _resultLabel.Text = outcome.Accepted
                ? $"内核已接纳：{outcome.CommandId}（WorldVersion {outcome.ResultingWorldVersion}）"
                : $"内核已拒绝：{string.Join("；", outcome.ErrorCodes.Select(CommandFailureText.Translate))}";
        }
    }

    private static string GetString(JsonElement element, string property) =>
        element.GetProperty(property).GetString()
        ?? throw new InvalidDataException($"world.json 政令模板字段 {property} 不能为空。");

    private static Font MakeFont(params string[] names) => new SystemFont
    {
        FontNames = names,
        AllowSystemFallback = true,
        MultichannelSignedDistanceField = true
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
