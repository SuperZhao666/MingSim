using MingSim.Domain.Common;

namespace MingSim.Domain.Map;

/// <summary>
/// 一张地图的静态拓扑定义。
/// </summary>
/// <remarks>
/// 地图定义描述“有哪些省份、它们叫什么、从哪里可以走到哪里”。
/// 它不保存本回合的占领者、军队数量或人物位置；这些变化属于 <see cref="WorldState" />。
///
/// 这里也刻意不保存多边形坐标。坐标、颜色和标签位置是 Godot 或地图编辑器的表现数据，
/// 不应该因为换一张底图就改变仿真的规则结果。这样既保留了 Open Historia 的“几何与文档状态分离”，
/// 也让命令行和服务器可以在没有图形环境时加载同一张地图。
/// </remarks>
public sealed class MapDefinition
{
    private readonly Dictionary<ProvinceId, ProvinceDefinition> _provinces = [];

    /// <summary>
    /// 创建一张地图并立即检查拓扑引用。
    /// </summary>
    /// <param name="id">地图的稳定编号。</param>
    /// <param name="provinces">地图中的静态省份。</param>
    /// <exception cref="ArgumentException">地图编号或省份字段为空时抛出。</exception>
    /// <exception cref="InvalidDataException">发现重复省份、自环或未知邻接省份时抛出。</exception>
    public MapDefinition(string id, IEnumerable<ProvinceDefinition> provinces)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("地图编号不能为空。", nameof(id));
        }

        Id = id.Trim();

        foreach (var province in provinces ?? throw new ArgumentNullException(nameof(provinces)))
        {
            if (!_provinces.TryAdd(province.Id, province))
            {
                throw new InvalidDataException($"地图 {Id} 存在重复省份 {province.Id}。 ");
            }
        }

        ValidateReferences();
    }

    /// <summary>创建一张没有省份的占位地图，供最小单元测试或尚未加载地图的世界使用。</summary>
    public static MapDefinition Empty(string id) => new(id, []);

    /// <summary>地图的稳定编号。</summary>
    public string Id { get; }

    /// <summary>只读省份索引。调用方不能通过这个属性直接增删省份。</summary>
    public IReadOnlyDictionary<ProvinceId, ProvinceDefinition> Provinces => _provinces;

    /// <summary>判断地图是否包含指定省份。</summary>
    public bool Contains(ProvinceId provinceId) => _provinces.ContainsKey(provinceId);

    /// <summary>
    /// 判断能否从一个省份直接前往另一个省份。
    /// </summary>
    /// <remarks>
    /// 邻接关系允许是有向的：山口、渡口或特殊通行规则都可能只允许 A → B。
    /// 如果历史内容要求双向通行，应在 JSON 中显式填写两个方向，而不是在这里偷偷补边。
    /// </remarks>
    public bool IsAdjacent(ProvinceId from, ProvinceId to) =>
        _provinces.TryGetValue(from, out var province) && province.AdjacentProvinces.Contains(to);

    private void ValidateReferences()
    {
        foreach (var province in _provinces.Values)
        {
            var seen = new HashSet<ProvinceId>();
            foreach (var adjacent in province.AdjacentProvinces)
            {
                if (adjacent == province.Id)
                {
                    throw new InvalidDataException($"省份 {province.Id} 不能把自己列为邻接省份。 ");
                }

                if (!seen.Add(adjacent))
                {
                    throw new InvalidDataException($"省份 {province.Id} 重复列出了邻接省份 {adjacent}。 ");
                }

                if (!_provinces.ContainsKey(adjacent))
                {
                    throw new InvalidDataException($"省份 {province.Id} 引用了不存在的邻接省份 {adjacent}。 ");
                }
            }
        }
    }
}

/// <summary>
/// 省份的静态信息。
/// </summary>
/// <remarks>
/// <see cref="AdjacentProvinces" /> 只描述地图拓扑，不代表本回合谁占领了这里。
/// 占领者、驻军、补给和战斗状态必须放在动态世界状态中。
/// </remarks>
public sealed record ProvinceDefinition
{
    public ProvinceDefinition(
        ProvinceId id,
        string name,
        IEnumerable<ProvinceId>? adjacentProvinces = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("省份编号不能为空。", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException($"省份 {id} 的名称不能为空。", nameof(name));
        }

        Id = id;
        Name = name.Trim();
        AdjacentProvinces = (adjacentProvinces ?? []).ToArray();
    }

    /// <summary>省份的稳定编号。</summary>
    public ProvinceId Id { get; }

    /// <summary>给玩家看的省份名称。</summary>
    public string Name { get; }

    /// <summary>从该省份出发可以直接到达的省份。</summary>
    public IReadOnlyList<ProvinceId> AdjacentProvinces { get; }
}
