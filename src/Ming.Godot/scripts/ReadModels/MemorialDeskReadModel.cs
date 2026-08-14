using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Ming.Godot.ReadModels;

/// <summary>
/// 一封待阅奏疏的只读展示数据。
/// 它只描述界面要显示什么，不携带命令，也没有任何修改游戏权威状态的能力。
/// </summary>
public sealed partial class MemorialItemDto : RefCounted
{
    public MemorialItemDto()
        : this(string.Empty, string.Empty, string.Empty, string.Empty, "DESIGN")
    {
    }

    public MemorialItemDto(string id, string title, string meta, string summary, string status)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Meta = meta ?? throw new ArgumentNullException(nameof(meta));
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
        Status = status ?? throw new ArgumentNullException(nameof(status));
    }

    public string Id { get; }
    public string Title { get; }
    public string Meta { get; }
    public string Summary { get; }
    public string Status { get; }
}

/// <summary>
/// 御案奏疏列表的不可变只读模型。
/// 构造时会复制传入序列，调用方随后修改原集合也不会改变已经交给界面的快照。
/// </summary>
public sealed partial class MemorialDeskReadModel : RefCounted
{
    public const string DesignClassification = "DESIGN";
    public const string DefaultSourceNotice = "尚未接入真实 Simulation";

    private readonly ReadOnlyCollection<MemorialItemDto> _pendingMemorials;

    public MemorialDeskReadModel()
        : this(Array.Empty<MemorialItemDto>(), DesignClassification, DefaultSourceNotice)
    {
    }

    public MemorialDeskReadModel(
        IEnumerable<MemorialItemDto> pendingMemorials,
        string classification,
        string sourceNotice)
    {
        ArgumentNullException.ThrowIfNull(pendingMemorials);
        Classification = string.IsNullOrWhiteSpace(classification)
            ? DesignClassification
            : classification;
        SourceNotice = string.IsNullOrWhiteSpace(sourceNotice)
            ? DefaultSourceNotice
            : sourceNotice;

        var copy = new List<MemorialItemDto>();
        foreach (var item in pendingMemorials)
            copy.Add(item ?? throw new ArgumentException("奏疏只读模型不能包含空条目。", nameof(pendingMemorials)));
        _pendingMemorials = copy.AsReadOnly();
    }

    public IReadOnlyList<MemorialItemDto> PendingMemorials => _pendingMemorials;
    public string Classification { get; }
    public string SourceNotice { get; }

    public static MemorialDeskReadModel CreateDefaultDesignPreview() => new(
    [
        new MemorialItemDto("liaoxi", "辽西急报", "宁远 · 卯初送达", "前线粮秣告急，须核漕运与陆路转输。", "OPEN"),
        new MemorialItemDto("revenue", "户部请旨", "京师 · 昨日申刻", "漕运拨款待决定，容量与承办人尚待核验。", "DESIGN"),
        new MemorialItemDto("censor", "御史弹章", "京师 · 昨日辰刻", "证据不足，暂列待复核。", "OPEN")
    ],
        DesignClassification,
        DefaultSourceNotice);

    /// <summary>
    /// 只供自动验收注入 0、1、多条数据；正式接线应直接构造只读模型并调用 MainUi.SetReadModel。
    /// </summary>
    public static MemorialDeskReadModel CreateAcceptanceSample(int pendingCount)
    {
        if (pendingCount < 0)
            throw new ArgumentOutOfRangeException(nameof(pendingCount), "奏疏数量不能为负数。");

        var items = new List<MemorialItemDto>(pendingCount);
        for (var index = 0; index < pendingCount; index++)
        {
            var number = index + 1;
            items.Add(new MemorialItemDto(
                $"acceptance-{number}",
                $"验收奏疏 {number}",
                "只读注入 · DESIGN",
                "此条仅用于验证桌面实体数量与只读列表一致。",
                "DESIGN"));
        }

        return new MemorialDeskReadModel(items, DesignClassification, DefaultSourceNotice);
    }
}
