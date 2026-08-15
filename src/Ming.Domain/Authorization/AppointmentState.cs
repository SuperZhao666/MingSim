using MingSim.Domain.Common;
using MingSim.Domain.Realtime;

namespace MingSim.Domain.Authorization;

/// <summary>
/// 一条"某人在某段时间担任某职位"的任命事实（doc 06 §4.3）。
/// </summary>
/// <remarks>
/// 为什么用独立类型而不是塞进 CharacterState：
/// 1. 一个人可以先后或兼任多个职位，任命有自己的生命周期（生效/到期/撤换）；
/// 2. 授权解析需要按时间窗口过滤"当前在任"的职位，独立记录才能保持检查为纯函数；
/// 3. 换任 = 增删世界状态里的任命项，下一次授权检查立即反映变化，
///    不需要任何缓存或通知机制——这正是"权限随任命即时变化"的最小实现。
///
/// 为什么用 record：任命是值语义的事实（相同字段=相同任命），
/// 便于快照往返后逐字段比较，也便于在哈希/编码中按稳定键排序。
///
/// 范围约定：
/// - <see cref="Scope"/>：辖区范围，与 CapabilityGrant.ResourceId 相同的精确匹配语义；
///   为空表示不限辖区。Limit 为额度上限（石/两等）；为空表示无额度限制。
///   <see cref="CapabilityAuthorizer"/> 的 Check 可接收可选 amount；数量/金额型动作传入 amount 时，
///   `amount &gt; Limit` 必须拒绝，`amount == Limit` 允许。非数量型动作可不传 amount。
/// - 生效区间是半开区间 [EffectiveFrom, EffectiveTo)：到期时刻即失效，
///   保证"已结束任职不能继续提供权限"（doc 06 §4.3 不变量）。
/// </remarks>
public sealed record AppointmentState(
    CharacterId PersonId,
    InstitutionId OfficeId,
    string? Scope,
    long? Limit,
    GameTime EffectiveFrom,
    GameTime? EffectiveTo)
{
    /// <summary>该任命在给定时刻是否有效（半开区间：开始时刻含，到期时刻不含）。</summary>
    public bool IsActiveAt(GameTime time) =>
        EffectiveFrom <= time && (EffectiveTo is null || time < EffectiveTo);
}
