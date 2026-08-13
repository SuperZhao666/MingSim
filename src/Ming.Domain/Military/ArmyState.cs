using MingSim.Domain.Common;

namespace MingSim.Domain.Military;

/// <summary>
/// 军队的最小可玩模型。
/// </summary>
/// <remarks>
/// 特意把辅兵和列装步兵拆成两个字段，避免“兵种升级”凭空增加战斗力。
/// 转换时必须同时消耗兵员和装备，任一条件不满足都不能提交。
/// </remarks>
public sealed class ArmyState
{
    public ArmyState(ArmyId id, string name, ProvinceId locationId, long auxiliaries, long lineInfantry)
    {
        Id = id;
        Name = name;
        LocationId = locationId;
        Auxiliaries = auxiliaries;
        LineInfantry = lineInfantry;
    }

    public ArmyId Id { get; }

    public string Name { get; }

    public ProvinceId LocationId { get; private set; }

    public long Auxiliaries { get; private set; }

    public long LineInfantry { get; private set; }

    public int TrainingDays { get; private set; }

    public bool TryConvertAuxiliariesToLineInfantry(long count)
    {
        if (count <= 0 || Auxiliaries < count)
        {
            return false;
        }

        Auxiliaries -= count;
        LineInfantry += count;
        return true;
    }

    public void AddTrainingDays(int days)
    {
        if (days < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(days));
        }

        TrainingDays += days;
    }

    /// <summary>
    /// 把军队正式放到一个新地区。
    ///
    /// 只有模拟内核在“行军抵达事件”里才应该调用它；
    /// UI 画面上的图标移动不能直接修改这个字段。
    /// </summary>
    public void ArriveAt(ProvinceId locationId) => LocationId = locationId;

    public ArmyState Clone()
    {
        var clone = new ArmyState(Id, Name, LocationId, Auxiliaries, LineInfantry);
        clone.AddTrainingDays(TrainingDays);
        return clone;
    }
}

/// <summary>世界中的军队集合。</summary>
public sealed class MilitaryState
{
    private readonly Dictionary<ArmyId, ArmyState> _armies = [];

    public IReadOnlyDictionary<ArmyId, ArmyState> Armies => _armies;

    public void Add(ArmyState army)
    {
        if (!_armies.TryAdd(army.Id, army))
        {
            throw new InvalidOperationException($"军队 {army.Id} 已经存在。");
        }
    }

    public MilitaryState Clone()
    {
        var clone = new MilitaryState();
        foreach (var (id, army) in _armies)
        {
            clone._armies.Add(id, army.Clone());
        }

        return clone;
    }
}
