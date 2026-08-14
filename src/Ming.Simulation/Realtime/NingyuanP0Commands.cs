using MingSim.Domain.Common;
using MingSim.Domain.Realtime;

namespace MingSim.Simulation.Realtime;

/// <summary>
/// 政令种类：政令只表达业务意图（P1-AUTH-01/02 修复）。
/// 审核策略（签发人校验、承办人能力、资源域）由内核按种类内置的 trusted 映射决定，
/// 调用方不可覆盖；<see cref="CreateDecreeCommand"/> 不再携带 RequiredCapability/RequiredResourceId。
/// </summary>
public enum DecreeKind
{
    /// <summary>普通政令（默认）：维持既有语义，承办人须持调粮（PlanLogistics）能力。</summary>
    General = 0,

    /// <summary>减耗令：皇帝发布后前线日耗 300→240 石/日，足额供粮日战备恢复减半。</summary>
    RationReduction = 1,

    /// <summary>催饷令：催办粮饷按期起运与到边（world.json 模板 kind=催饷），承办机构须持调粮能力。</summary>
    ExpediteSupply = 2,

    /// <summary>拨饷令：批准并拨付粮银（world.json 模板 kind=拨饷），承办机构须持财粮会计能力。</summary>
    AllocateSupply = 3,

    /// <summary>请饷奏疏：前线向中枢请饷的请愿文书（world.json 模板 kind=请饷）。
    /// 创建时不扣中央预算、进入 <see cref="MingSim.Domain.Decrees.DecreeStatus.Submitted"/>；
    /// 后续经 <see cref="ApproveDecreeCommand"/> 批准才转为可执行并扣除批准预算。</summary>
    RequestSupply = 4,
}

/// <summary>
/// 玩家（皇帝）提交一道结构化政令的命令（P0 玩法规则）。
/// </summary>
/// <remarks>
/// 政令模板字段对齐 doc 03 §4 P0 政令行：目标/范围/预算/承办人/期限/限制/备注；
/// 另加可选粮运绑定与政令种类。预算在命令接纳时从国库扣除（请饷奏疏除外——请愿文书
/// 创建不扣预算，批准时才扣）；期限约束由到期动作执行（逾期即甩责）。
/// <see cref="Kind"/> 放在参数尾部并默认 <see cref="DecreeKind.General"/>：既有调用语义不变。
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
    string? LinkedShipmentId = null,
    DateTimeOffset SubmittedAt = default,
    long ExpectedWorldVersion = 0,
    DecreeKind Kind = DecreeKind.General)
    : RealtimeCommand(CommandId, ActorId, SubmittedAt, ExpectedWorldVersion);

/// <summary>
/// 批准一道已提交（<see cref="MingSim.Domain.Decrees.DecreeStatus.Submitted"/>）的请饷奏疏：
/// 扣除批准预算、转为可执行（P1-AUTH-01 请饷语义：创建不扣预算，批准才转可执行）。
/// </summary>
public sealed record ApproveDecreeCommand(
    string CommandId,
    CharacterId ActorId,
    DecreeId DecreeId,
    DateTimeOffset SubmittedAt,
    long ExpectedWorldVersion)
    : RealtimeCommand(CommandId, ActorId, SubmittedAt, ExpectedWorldVersion);
