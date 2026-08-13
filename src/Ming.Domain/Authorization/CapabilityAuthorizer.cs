using MingSim.Domain.Common;
using MingSim.Domain;

namespace MingSim.Domain.Authorization;

/// <summary>
/// 负责回答“这个角色现在能不能调用这项能力”。
/// </summary>
/// <remarks>
/// 权限判断集中在这里，避免每个工具各自写一套容易漏条件的 if。
/// 第一版使用 RBAC 思想的简化版：角色拿到能力授权，授权还可以带资源范围和回合期限。
/// </remarks>
public sealed class CapabilityAuthorizer
{
    public AuthorizationDecision Check(
        WorldState world,
        CharacterId actorId,
        GameCapability capability,
        string? resourceId = null)
    {
        if (!world.Characters.ContainsKey(actorId))
        {
            return new AuthorizationDecision(false, $"角色 {actorId} 不存在。");
        }

        var grant = world.CapabilityGrants.FirstOrDefault(candidate =>
            candidate.ActorId == actorId &&
            candidate.Capability == capability &&
            (candidate.ResourceId is null || candidate.ResourceId == resourceId) &&
            (candidate.ExpiresAtTurn is null || world.TurnNumber <= candidate.ExpiresAtTurn));

        return grant is null
            ? new AuthorizationDecision(false, $"角色 {actorId} 没有能力 {capability} 的授权。")
            : new AuthorizationDecision(true, "授权通过。", grant);
    }
}
