using MingSim.Domain.Common;
using MingSim.Domain;

namespace MingSim.Domain.Authorization;

/// <summary>
/// 负责回答"这个角色现在能不能调用这项能力"。
/// </summary>
/// <remarks>
/// 权限判断集中在这里，避免每个工具各自写一套容易漏条件的 if。
/// 授权由两条能力来源组合（doc 05 §4.3 / doc 06 §4.5）：
/// 1. 直接 CapabilityGrant：给某个角色显式授一项能力（可带资源范围和回合期限）；
/// 2. 任命推导：角色当前在任的职位机构暴露的能力，且目标落在任命辖区。
/// 直接授权保持原有语义不变；任命只是追加一条能力来源，换任后下一次 Check 立即生效。
/// </remarks>
public sealed class CapabilityAuthorizer
{
    public AuthorizationDecision Check(
        WorldState world,
        CharacterId actorId,
        GameCapability capability,
        string? resourceId = null,
        long? amount = null)
    {
        if (amount is < 0)
        {
            return new AuthorizationDecision(false, "授权检查的数量/金额不能为负数。");
        }

        // 角色不存在时无论有没有任命都拒绝：任命是"某人的任职事实"，
        // 不能反过来让一条任命凭空创造一个可以调能力的 Actor（防伪造）。
        if (!world.Characters.ContainsKey(actorId))
        {
            return new AuthorizationDecision(false, $"角色 {actorId} 不存在。");
        }

        var grant = world.CapabilityGrants.FirstOrDefault(candidate =>
            candidate.ActorId == actorId &&
            candidate.Capability == capability &&
            (candidate.ResourceId is null || candidate.ResourceId == resourceId) &&
            (candidate.ExpiresAtTurn is null || world.TurnNumber <= candidate.ExpiresAtTurn));

        if (grant is not null)
        {
            return new AuthorizationDecision(true, "授权通过。", grant);
        }

        // 任命推导：直接授权没有命中时，检查当前在任职位是否提供这项能力。
        // 为什么放在直接授权之后：CapabilityGrant 的判定顺序与结果完全不变；
        // 为什么按世界时间过滤：任命是时间窗口事实，到期即失去权限（doc 06 §4.3 不变量），
        // 半开区间由 AppointmentState.IsActiveAt 定义，到期时刻精确失效。
        // 为什么用循环而不是 LINQ：需要拿到匹配的职位名称写进原因，方便排查"哪条任命授的权"。
        foreach (var appointment in world.Appointments)
        {
            if (appointment.PersonId != actorId || !appointment.IsActiveAt(world.GameTime))
            {
                continue;
            }

            if (appointment.Scope is not null && appointment.Scope != resourceId)
            {
                continue;
            }

            // 任命额度只在调用方提供本次动作数量/金额时参与裁决；未提供 amount 表示该能力
            // 不是数量型动作或上层尚无该维度。额度为半开之外的硬上限：amount == Limit 允许，超过拒绝。
            if (amount is not null && appointment.Limit is not null && amount.Value > appointment.Limit.Value)
            {
                continue;
            }

            // 职位机构不存在或机构未暴露该能力时跳过：悬空引用必须 fail-closed，
            // 不能因为任命指向一个不存在的机构就放行。
            if (!world.Institutions.TryGetValue(appointment.OfficeId, out var office) ||
                !office.Capabilities.Contains(capability))
            {
                continue;
            }

            return new AuthorizationDecision(
                true,
                $"角色 {actorId} 在任职位 {office.Name} 提供能力 {capability}（任命推导授权）。");
        }

        return new AuthorizationDecision(false, $"角色 {actorId} 没有能力 {capability} 的授权。");
    }
}
