namespace MingSim.Domain.Common;

/// <summary>
/// 游戏世界允许调用的能力清单。
/// </summary>
/// <remarks>
/// 这不是“模型可以使用的工具名称”，而是更底层的权限原语。
/// 工具在真正执行前必须先检查调用者是否拥有对应能力。
/// </remarks>
public enum GameCapability
{
    ReadFinance,
    AllocateFinance,
    BuildIndustry,
    TrainArmy,
    ConvertArmy,
    PlanLogistics,
    MoveArmy,
}
