namespace MingSim.Domain.Common;

/// <summary>
/// 世界的唯一编号。
/// </summary>
/// <remarks>
/// 这里没有直接使用数据库自增整数，是因为以后可能同时存在多个剧本、多个存档，
/// 用一个有意义的字符串编号更容易在日志和调试器中读懂。
/// </remarks>
public readonly record struct WorldId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>角色的唯一编号。</summary>
public readonly record struct CharacterId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>机构的唯一编号，例如户部、工部、兵部。</summary>
public readonly record struct InstitutionId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>地区的唯一编号。第一版可以先把省、府、县都当成字符串处理。</summary>
public readonly record struct ProvinceId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>军队编制的唯一编号。</summary>
public readonly record struct ArmyId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>工坊或工厂的唯一编号。</summary>
public readonly record struct FacilityId(string Value)
{
    public override string ToString() => Value;
}
