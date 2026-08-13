using MingSim.Domain.Common;

namespace MingSim.Domain.Authorization;

/// <summary>
/// 给某个角色授予一项能力。
/// </summary>
/// <param name="ActorId">被授权的角色。</param>
/// <param name="Capability">可以做什么。</param>
/// <param name="ResourceId">作用范围；为空表示整个世界。</param>
/// <param name="ExpiresAtTurn">在哪个回合之后失效；为空表示没有回合期限。</param>
public sealed record CapabilityGrant(
    CharacterId ActorId,
    GameCapability Capability,
    string? ResourceId = null,
    int? ExpiresAtTurn = null);

/// <summary>权限检查结果。</summary>
public sealed record AuthorizationDecision(
    bool Allowed,
    string Reason,
    CapabilityGrant? MatchedGrant = null);
