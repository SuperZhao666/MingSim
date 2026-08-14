using MingSim.Domain.Common;
using MingSim.Domain.Realtime;

namespace MingSim.Domain.Decrees;

/// <summary>
/// 一道已经进入权威世界的结构化政令（P0 玩法规则）。
/// </summary>
/// <remarks>
/// 政令不是一句自由文本：玩家确认的是 目标/范围/预算/承办人/期限/限制/备注 组成的模板
/// （doc 03 §4 P0 政令行）。它描述"要达成什么"，不直接拥有粮食或部队；
/// 只有 Simulation 的唯一命令管线与到期动作能改变它。
/// 为什么复用 <see cref="DecreeStatus"/>：MVP 只有一套政令状态，避免第二份容易漂移的枚举。
/// </remarks>
public sealed class DecreeState
{
    public DecreeState(
        DecreeId id,
        CharacterId issuerId,
        string goal,
        ProvinceId regionScope,
        long budget,
        CharacterId responsibleActorId,
        GameTime deadline,
        string restrictions,
        string remarks,
        GameCapability requiredCapability,
        string? requiredResourceId = null,
        string? linkedShipmentId = null)
    {
        Id = id;
        IssuerId = issuerId;
        Goal = goal;
        RegionScope = regionScope;
        Budget = budget;
        ResponsibleActorId = responsibleActorId;
        Deadline = deadline;
        Restrictions = restrictions;
        Remarks = remarks;
        RequiredCapability = requiredCapability;
        RequiredResourceId = requiredResourceId;
        LinkedShipmentId = linkedShipmentId;
        // 命令被接纳即视为已进入执行：命令拒绝根本不会创建政令状态，
        // 因此这里不需要 Draft/Submitted 过渡态。
        Status = DecreeStatus.Executing;
    }

    public DecreeId Id { get; }

    public CharacterId IssuerId { get; }

    /// <summary>目标：要达成什么，例如"向宁远调运 5000 石军粮"。</summary>
    public string Goal { get; }

    /// <summary>范围：作用于哪个地区。MVP 只支持单一地区。</summary>
    public ProvinceId RegionScope { get; }

    /// <summary>预算：批准并立即从国库扣除的银两。</summary>
    public long Budget { get; }

    /// <summary>承办人：对期限负责的角色。</summary>
    public CharacterId ResponsibleActorId { get; }

    /// <summary>期限：承办人必须在此时刻前完成；逾期即甩责。</summary>
    public GameTime Deadline { get; }

    /// <summary>限制：执行政令时的约束说明，例如"不得征发民夫"。</summary>
    public string Restrictions { get; }

    /// <summary>备注：给玩家和审计读的一句说明。</summary>
    public string Remarks { get; }

    /// <summary>承办人必须具备的能力；CreateDecreeCommand 据此做权限检查。</summary>
    public GameCapability RequiredCapability { get; }

    /// <summary>能力的作用范围（例如路线号）；为空表示任意范围。</summary>
    public string? RequiredResourceId { get; }

    /// <summary>可选绑定：政令与哪张粮运单绑定；绑定单抵达即视为政令完成。</summary>
    public string? LinkedShipmentId { get; }

    public DecreeStatus Status { get; private set; }

    internal void Complete() => Status = DecreeStatus.Completed;

    internal void Expire() => Status = DecreeStatus.Expired;

    internal DecreeState Clone() => new(
        Id, IssuerId, Goal, RegionScope, Budget, ResponsibleActorId, Deadline,
        Restrictions, Remarks, RequiredCapability, RequiredResourceId, LinkedShipmentId)
    {
        Status = Status,
    };
}
