using MingSim.Domain.Common;

namespace MingSim.Domain.Characters;

/// <summary>
/// 一个角色在世界中的当前状态。
/// </summary>
/// <remarks>
/// 角色是“持久化 Actor”的数据部分，但它本身不是 AI。
/// AI 只是读取角色状态后提出意图；角色状态的最终修改仍然由模拟内核完成。
/// </remarks>
public sealed class CharacterState
{
    private readonly List<MemoryNote> _privateMemories = [];

    public CharacterState(
        CharacterId id,
        string name,
        CharacterAttributes attributes,
        CharacterPersonality personality)
    {
        Id = id;
        Name = name;
        Attributes = attributes;
        Personality = personality;
    }

    public CharacterId Id { get; }

    public string Name { get; }

    public CharacterAttributes Attributes { get; }

    public CharacterPersonality Personality { get; }

    /// <summary>当前官职编号。没有官职时为空。</summary>
    public string? OfficeId { get; private set; }

    /// <summary>角色所在地区。</summary>
    public ProvinceId LocationId { get; private set; } = new("capital");

    /// <summary>越高表示角色越愿意承受风险和不便；范围约定为 0 到 100。</summary>
    public int Loyalty { get; private set; } = 50;

    /// <summary>人格与现实冲突时累积的心理/政治压力。</summary>
    public int Stress { get; private set; }

    /// <summary>角色的私有记忆，不应直接当作世界真相。</summary>
    public IReadOnlyList<MemoryNote> PrivateMemories => _privateMemories;

    public void AssignOffice(string? officeId) => OfficeId = officeId;

    public void MoveTo(ProvinceId locationId) => LocationId = locationId;

    public void ChangeLoyalty(int delta) => Loyalty = Math.Clamp(Loyalty + delta, 0, 100);

    public void AddStress(int delta) => Stress = Math.Max(0, Stress + delta);

    public void Remember(MemoryNote memory) => _privateMemories.Add(memory);

    /// <summary>
    /// 创建一份深拷贝，用于回合冻结和失败回滚。
    /// </summary>
    public CharacterState Clone()
    {
        var clone = new CharacterState(Id, Name, Attributes, Personality)
        {
            OfficeId = OfficeId,
            LocationId = LocationId,
            Loyalty = Loyalty,
            Stress = Stress,
        };

        foreach (var memory in _privateMemories)
        {
            clone._privateMemories.Add(memory);
        }

        return clone;
    }
}

/// <summary>角色的可比较能力。数值只描述能力，不直接决定角色一定会怎么做。</summary>
public sealed record CharacterAttributes(
    int Administration,
    int Finance,
    int Martial,
    int Intrigue,
    int Learning)
{
    public CharacterAttributes Normalize() => new(
        Math.Clamp(Administration, 0, 100),
        Math.Clamp(Finance, 0, 100),
        Math.Clamp(Martial, 0, 100),
        Math.Clamp(Intrigue, 0, 100),
        Math.Clamp(Learning, 0, 100));
}

/// <summary>
/// 第一版只保留少量容易理解的人格维度，后续可以扩展成数据驱动的特质集合。
/// </summary>
public sealed record CharacterPersonality(
    bool Honest,
    bool Bold,
    bool LoyalToRuler,
    bool Compassionate);

/// <summary>角色记忆的一条最小记录。</summary>
public sealed record MemoryNote(
    int TurnNumber,
    string Subject,
    string Content,
    bool IsVerifiedFact);
