using MingSim.Domain.Common;
using MingSim.Domain.Realtime;

namespace MingSim.Simulation.Realtime;

/// <summary>
/// 玩家（皇帝）提交一道结构化政令的命令（P0 玩法规则）。
/// </summary>
/// <remarks>
/// 政令模板字段对齐 doc 03 §4 P0 政令行：目标/范围/预算/承办人/期限/限制/备注；
/// 另加承办所需能力（CreateDecreeCommand 据此做权限检查）和可选粮运绑定。
/// 预算在命令接纳时立即从国库扣除；期限约束由到期动作执行（逾期即甩责）。
/// </remarks>
public sealed record CreateDecreeCommand(
    string CommandId,
    CharacterId ActorId,
    DecreeId DecreeId,
    string Goal,
    ProvinceId RegionScope,
    long Budget,
    CharacterId ResponsibleActorId,
    GameTime Deadline,
    string Restrictions,
    string Remarks,
    GameCapability RequiredCapability,
    string? RequiredResourceId = null,
    string? LinkedShipmentId = null,
    DateTimeOffset SubmittedAt = default,
    long ExpectedWorldVersion = 0)
    : RealtimeCommand(CommandId, ActorId, SubmittedAt, ExpectedWorldVersion);
