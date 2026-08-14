using MingSim.Domain.Common;
using MingSim.Domain.Realtime;

namespace MingSim.Simulation.Realtime;

/// <summary>政令种类：区分普通政令与减耗令（M5 通关杠杆，纸面推演 §3.2）。</summary>
public enum DecreeKind
{
    /// <summary>普通政令（默认）：维持既有语义。</summary>
    General = 0,

    /// <summary>减耗令：皇帝发布后前线日耗 300→240 石/日，足额供粮日战备恢复减半。</summary>
    RationReduction = 1,
}

/// <summary>
/// 玩家（皇帝）提交一道结构化政令的命令（P0 玩法规则）。
/// </summary>
/// <remarks>
/// 政令模板字段对齐 doc 03 §4 P0 政令行：目标/范围/预算/承办人/期限/限制/备注；
/// 另加承办所需能力（CreateDecreeCommand 据此做权限检查）、可选粮运绑定与政令种类。
/// 预算在命令接纳时立即从国库扣除；期限约束由到期动作执行（逾期即甩责）。
/// <see cref="Kind"/> 放在参数尾部并默认 <see cref="DecreeKind.General"/>：既有调用语义不变，
/// 减耗令由 Runtime 的接纳/生效路径读取并触发前线日耗 300→240（纸面推演 §3.2）。
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
    long ExpectedWorldVersion = 0,
    DecreeKind Kind = DecreeKind.General)
    : RealtimeCommand(CommandId, ActorId, SubmittedAt, ExpectedWorldVersion);
