using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Institutions;
using MingSim.Domain.Map;
using MingSim.Domain.Military;
using MingSim.Domain.Realtime;

namespace MingSim.Domain;

/// <summary>
/// 整个游戏世界的权威内存状态。
/// </summary>
/// <remarks>
/// 这是“单一真相”的核心：模拟器、存档和 UI 最终都围绕它工作。
/// Godot 场景、AI 文本和数据库缓存都不能拥有另一份可以自行修改的世界真相。
/// </remarks>
public sealed class WorldState
{
    private readonly Dictionary<CharacterId, CharacterState> _characters = [];
    private readonly Dictionary<InstitutionId, InstitutionState> _institutions = [];
    private readonly List<CapabilityGrant> _capabilityGrants = [];

    public WorldState(
        WorldId id,
        int turnNumber,
        long treasurySilver,
        MapDefinition? map = null,
        DateTime? currentTime = null)
    {
        Id = id;
        TurnNumber = turnNumber;
        Economy = new EconomyState(treasurySilver);
        Map = map ?? MapDefinition.Empty(id.Value);
        CurrentTime = currentTime ?? SimulationEpoch.DefaultForTurn(turnNumber);
    }

    public WorldId Id { get; }

    /// <summary>当前回合。所有回合意图都必须声明自己基于哪个回合。</summary>
    public int TurnNumber { get; private set; }

    /// <summary>
    /// 世界当前的游戏时间。
    ///
    /// 以前的 TurnNumber 仍然保留，是为了兼容旧存档和旧意图；
    /// 但实时推演真正依赖的是这个时间，而不是“点击一次按钮就加一回合”。
    /// </summary>
    public DateTime CurrentTime { get; private set; }

    public EconomyState Economy { get; }

    /// <summary>
    /// 当前世界使用的静态地图拓扑。
    /// 地图本身不可通过 WorldState 的公开属性修改；动态占领和驻军仍在各自领域状态中。
    /// </summary>
    public MapDefinition Map { get; }

    public IndustryState Industry { get; } = new();

    public MilitaryState Military { get; } = new();

    public IReadOnlyDictionary<CharacterId, CharacterState> Characters => _characters;

    public IReadOnlyDictionary<InstitutionId, InstitutionState> Institutions => _institutions;

    public IReadOnlyList<CapabilityGrant> CapabilityGrants => _capabilityGrants;

    public void AddCharacter(CharacterState character)
    {
        if (!_characters.TryAdd(character.Id, character))
        {
            throw new InvalidOperationException($"角色 {character.Id} 已经存在。");
        }
    }

    public void AddInstitution(InstitutionState institution)
    {
        if (!_institutions.TryAdd(institution.Id, institution))
        {
            throw new InvalidOperationException($"机构 {institution.Id} 已经存在。");
        }
    }

    public void GrantCapability(CapabilityGrant grant) => _capabilityGrants.Add(grant);

    public bool TryGetCharacter(CharacterId characterId, out CharacterState? character) =>
        _characters.TryGetValue(characterId, out character);

    public void AdvanceTurn() => TurnNumber++;

    /// <summary>
    /// 只推进世界时间，不自动改变回合号。
    ///
    /// 这正是实时模拟和传统回合制的分界：
    /// 世界可以经过 1 小时、6 小时或 3 天，期间由调度器执行到期事件。
    /// </summary>
    public void AdvanceTime(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), "世界时间必须向前推进。");
        }

        CurrentTime = CurrentTime.Add(elapsed);
    }

    /// <summary>
    /// 回合冻结的关键操作：深拷贝后，所有代理都只能基于这份副本做决定。
    /// </summary>
    public WorldState Clone()
    {
        // MapDefinition 是经过校验的静态对象，工作区可以安全地共享它；
        // 经济、人物、军队等会变化的数据仍然全部深拷贝。
        var clone = new WorldState(Id, TurnNumber, Economy.Treasury.Silver, Map, CurrentTime);

        foreach (var (id, character) in _characters)
        {
            clone._characters.Add(id, character.Clone());
        }

        foreach (var (id, institution) in _institutions)
        {
            clone._institutions.Add(id, institution.Clone());
        }

        foreach (var grant in _capabilityGrants)
        {
            clone._capabilityGrants.Add(grant);
        }

        CopyEconomy(clone);
        CopyIndustry(clone);
        CopyMilitary(clone);
        return clone;
    }

    private void CopyEconomy(WorldState clone)
    {
        foreach (var (resourceType, stock) in Economy.Inventory.Stocks)
        {
            var destination = clone.Economy.Inventory.GetOrCreate(resourceType);
            destination.Add(stock.Quantity);
            if (stock.Reserved > 0)
            {
                destination.TryReserve(stock.Reserved);
            }
        }
    }

    private void CopyIndustry(WorldState clone)
    {
        foreach (var facility in Industry.Facilities.Values)
        {
            clone.Industry.Add(facility.Clone());
        }
    }

    private void CopyMilitary(WorldState clone)
    {
        foreach (var army in Military.Armies.Values)
        {
            clone.Military.Add(army.Clone());
        }
    }
}
