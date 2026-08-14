using System.Collections.ObjectModel;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Decrees;
using MingSim.Domain.Economy;
using MingSim.Domain.Institutions;
using MingSim.Domain.Map;
using MingSim.Domain.Military;
using MingSim.Domain.Realtime;
using MingSim.Domain.Scenario;

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
    private readonly Dictionary<ArmyId, MovementState> _movements = [];
    private readonly Dictionary<DecreeId, DecreeState> _decrees = [];

    internal WorldState(
        WorldId id,
        int turnNumber,
        long treasurySilver,
        MapDefinition? map = null,
        DateTimeOffset? currentTime = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("世界编号不能为空。", nameof(id));
        }

        if (turnNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(turnNumber), "回合号必须从 1 开始。");
        }

        Id = id;
        TurnNumber = turnNumber;
        Economy = new EconomyState(treasurySilver);
        Map = map ?? MapDefinition.Empty(id.Value);
        GameTime = new(currentTime ?? SimulationEpoch.DefaultForTurn(turnNumber));
    }

    /// <summary>受控剧本初始化入口；初始化完成后写入权只属于 Simulation。</summary>
    public static WorldState CreateInitial(
        WorldId id,
        int turnNumber,
        long treasurySilver,
        MapDefinition map,
        IEnumerable<CharacterState>? characters = null,
        IEnumerable<InstitutionState>? institutions = null,
        IEnumerable<CapabilityGrant>? capabilityGrants = null,
        IEnumerable<(string ResourceType, long Quantity)>? inventory = null,
        IEnumerable<ArmyState>? armies = null,
        IEnumerable<StockpileState>? stockpiles = null,
        IEnumerable<RouteState>? routes = null,
        ScenarioState? scenario = null)
    {
        var world = new WorldState(id, turnNumber, treasurySilver, map)
        {
            Scenario = scenario ?? new ScenarioState(),
        };
        world.Scenario.SetScenarioStart(world.GameTime);
        foreach (var character in characters ?? []) world.AddCharacter(character);
        foreach (var institution in institutions ?? []) world.AddInstitution(institution);
        foreach (var grant in capabilityGrants ?? []) world.GrantCapability(grant);
        foreach (var (resourceType, quantity) in inventory ?? []) world.Economy.Inventory.GetOrCreate(resourceType).Add(quantity);
        foreach (var army in armies ?? []) world.Military.Add(army);
        foreach (var stockpile in stockpiles ?? []) world.Logistics.AddStockpile(stockpile);
        foreach (var route in routes ?? []) world.Logistics.AddRoute(route);
        return world;
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
    public GameTime GameTime { get; private set; }

    /// <summary>
    /// 旧代码读取时间的兼容属性；写入仍只能通过 Simulation 的 GameTime API 完成。
    /// </summary>
    public DateTimeOffset CurrentTime => GameTime.Value;

    /// <summary>权威世界提交次数；它不随渲染帧切分而变化。</summary>
    public long WorldVersion { get; private set; }

    /// <summary>最近一次权威提交的稳定身份，不使用 Guid 或现实时间。</summary>
    public string CommitId { get; private set; } = "genesis";

    public EconomyState Economy { get; }

    /// <summary>
    /// 当前世界使用的静态地图拓扑。
    /// 地图本身不可通过 WorldState 的公开属性修改；动态占领和驻军仍在各自领域状态中。
    /// </summary>
    public MapDefinition Map { get; }

    public IndustryState Industry { get; } = new();

    public MilitaryState Military { get; } = new();

    /// <summary>实时物流的权威状态；只能由 Simulation 的命令和到期动作改变。</summary>
    public LogisticsState Logistics { get; } = new();

    /// <summary>场景级状态（地方负担/大臣信任/场景规则参数）；只能由 Simulation 改变。</summary>
    public ScenarioState Scenario { get; private set; } = new();

    /// <summary>前线战备（P0 最小抽象）；只能由 Simulation 的日耗规则改变。</summary>
    public ReadinessState Readiness { get; private set; } = new();

    /// <summary>已提交政令；只能由 Simulation 的命令和到期动作改变。</summary>
    public IReadOnlyDictionary<DecreeId, DecreeState> Decrees =>
        new ReadOnlyDictionary<DecreeId, DecreeState>(_decrees);

    public IReadOnlyDictionary<CharacterId, CharacterState> Characters =>
        new ReadOnlyDictionary<CharacterId, CharacterState>(_characters);

    public IReadOnlyDictionary<InstitutionId, InstitutionState> Institutions =>
        new ReadOnlyDictionary<InstitutionId, InstitutionState>(_institutions);

    public IReadOnlyList<CapabilityGrant> CapabilityGrants =>
        new ReadOnlyCollection<CapabilityGrant>(_capabilityGrants);

    public IReadOnlyDictionary<ArmyId, MovementState> Movements =>
        new ReadOnlyDictionary<ArmyId, MovementState>(_movements);

    internal void SetMovement(MovementState movement) => _movements[movement.ArmyId] = movement;

    internal bool RemoveMovement(ArmyId armyId) => _movements.Remove(armyId);

    internal void AddCharacter(CharacterState character)
    {
        if (!_characters.TryAdd(character.Id, character))
        {
            throw new InvalidOperationException($"角色 {character.Id} 已经存在。");
        }
    }

    internal void AddInstitution(InstitutionState institution)
    {
        if (!_institutions.TryAdd(institution.Id, institution))
        {
            throw new InvalidOperationException($"机构 {institution.Id} 已经存在。");
        }
    }

    internal void GrantCapability(CapabilityGrant grant) => _capabilityGrants.Add(grant);

    internal void AddDecree(DecreeState decree)
    {
        if (!_decrees.TryAdd(decree.Id, decree))
        {
            throw new InvalidOperationException($"政令 {decree.Id} 已经存在。");
        }
    }

    public bool TryGetCharacter(CharacterId characterId, out CharacterState? character) =>
        _characters.TryGetValue(characterId, out character);

    internal void AdvanceTurn() => TurnNumber++;

    /// <summary>
    /// 只推进世界时间，不自动改变回合号。
    ///
    /// 这正是实时模拟和传统回合制的分界：
    /// 世界可以经过 1 小时、6 小时或 3 天，期间由调度器执行到期事件。
    /// </summary>
    internal void AdvanceTime(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), "世界时间必须向前推进。");
        }

        AdvanceTo(GameTime.Add(elapsed));
    }

    /// <summary>由单写者 Simulation 推进到明确的权威游戏时刻。</summary>
    internal void AdvanceTo(GameTime target)
    {
        if (target < GameTime)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "游戏时间不能倒退。");
        }

        GameTime = target;
    }

    /// <summary>记录一次已经完成的实时提交。</summary>
    internal void CommitRealtime(long worldVersion, string commitId)
    {
        if (string.IsNullOrWhiteSpace(commitId))
        {
            throw new ArgumentException("提交身份不能为空。", nameof(commitId));
        }

        if (WorldVersion == long.MaxValue || worldVersion != WorldVersion + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(worldVersion), "世界版本必须恰好递增 1，不能同号或跳号。");
        }

        WorldVersion = worldVersion;
        CommitId = commitId;
    }

    /// <summary>
    /// 回合冻结的关键操作：深拷贝后，所有代理都只能基于这份副本做决定。
    /// </summary>
    public WorldState Clone()
    {
        // MapDefinition 是经过校验的静态对象，工作区可以安全地共享它；
        // 经济、人物、军队等会变化的数据仍然全部深拷贝。
        var clone = new WorldState(Id, TurnNumber, Economy.Treasury.Silver, Map, CurrentTime)
        {
            GameTime = GameTime,
            WorldVersion = WorldVersion,
            CommitId = CommitId,
        };

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

        foreach (var (armyId, movement) in _movements)
        {
            clone._movements.Add(armyId, movement);
        }

        clone.Scenario = Scenario.Clone();
        clone.Readiness = Readiness.Clone();
        foreach (var (decreeId, decree) in _decrees)
        {
            clone._decrees.Add(decreeId, decree.Clone());
        }

        CopyEconomy(clone);
        CopyIndustry(clone);
        CopyMilitary(clone);
        CopyLogistics(clone);
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

    private void CopyLogistics(WorldState clone)
    {
        foreach (var stockpile in Logistics.Stockpiles.Values)
        {
            clone.Logistics.AddStockpile(stockpile.Clone());
        }

        foreach (var route in Logistics.Routes.Values)
        {
            clone.Logistics.AddRoute(route);
        }

        foreach (var shipment in Logistics.Shipments.Values)
        {
            clone.Logistics.AddShipment(shipment.Clone());
        }
    }
}
