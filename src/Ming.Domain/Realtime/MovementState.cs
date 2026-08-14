using MingSim.Domain.Common;

namespace MingSim.Domain.Realtime;

/// <summary>
/// 一支军队的一条唯一在途行动。
/// </summary>
/// <remarks>
/// 军队仍停留在 Origin，只有到期事件通过这些字段复核成功后才会改变 LocationId。
/// 这样“图标正在移动”和“权威军队已经抵达”不会混成两个状态。
/// </remarks>
public sealed record MovementState(
    string ActionId,
    ArmyId ArmyId,
    ProvinceId Origin,
    ProvinceId Destination,
    GameTime DueGameTime,
    string RouteFingerprint);
