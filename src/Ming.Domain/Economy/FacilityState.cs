using MingSim.Domain.Common;

namespace MingSim.Domain.Economy;

/// <summary>工坊的生命周期。</summary>
public enum FacilityStatus
{
    Building,
    Active,
    Damaged,
}

/// <summary>第一版示例用的工坊类型。</summary>
public enum FacilityType
{
    FlintlockWorkshop,
    GrainDepot,
    Shipyard,
}

/// <summary>
/// 一个真实存在的工坊，而不是“账面上有一座工厂”的描述文本。
/// </summary>
public sealed class FacilityState
{
    public FacilityState(
        FacilityId id,
        ProvinceId locationId,
        FacilityType type,
        long baseCapacity,
        int workforce,
        int buildTurnsRemaining,
        int createdTurn)
    {
        Id = id;
        LocationId = locationId;
        Type = type;
        BaseCapacity = baseCapacity;
        Workforce = workforce;
        BuildTurnsRemaining = buildTurnsRemaining;
        CreatedTurn = createdTurn;
        Status = FacilityStatus.Building;
    }

    public FacilityId Id { get; }

    public ProvinceId LocationId { get; }

    public FacilityType Type { get; }

    public FacilityStatus Status { get; private set; }

    public long BaseCapacity { get; }

    public int Workforce { get; }

    public int BuildTurnsRemaining { get; private set; }

    public int CreatedTurn { get; }

    public long ProducedThisTurn { get; private set; }

    public void AdvanceConstruction()
    {
        if (Status != FacilityStatus.Building)
        {
            return;
        }

        BuildTurnsRemaining = Math.Max(0, BuildTurnsRemaining - 1);
        if (BuildTurnsRemaining == 0)
        {
            Status = FacilityStatus.Active;
        }
    }

    public void RecordProduction(long quantity) => ProducedThisTurn = Math.Max(0, quantity);

    public FacilityState Clone()
    {
        var clone = new FacilityState(
            Id,
            LocationId,
            Type,
            BaseCapacity,
            Workforce,
            BuildTurnsRemaining,
            CreatedTurn)
        {
            Status = Status,
            ProducedThisTurn = ProducedThisTurn,
        };

        return clone;
    }
}

/// <summary>工坊集合。</summary>
public sealed class IndustryState
{
    private readonly Dictionary<FacilityId, FacilityState> _facilities = [];

    public IReadOnlyDictionary<FacilityId, FacilityState> Facilities => _facilities;

    public bool Contains(FacilityId facilityId) => _facilities.ContainsKey(facilityId);

    public void Add(FacilityState facility)
    {
        if (!_facilities.TryAdd(facility.Id, facility))
        {
            throw new InvalidOperationException($"工坊 {facility.Id} 已经存在。");
        }
    }

    public IndustryState Clone()
    {
        var clone = new IndustryState();
        foreach (var (id, facility) in _facilities)
        {
            clone._facilities.Add(id, facility.Clone());
        }

        return clone;
    }
}
