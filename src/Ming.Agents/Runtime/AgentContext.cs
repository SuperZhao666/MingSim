using MingSim.Domain.Common;
using MingSim.Domain.Realtime;

namespace MingSim.Agents.Runtime;

/// <summary>代理能看到的军队摘要。</summary>
public sealed record ArmyObservation(
    ArmyId ArmyId,
    string Name,
    ProvinceId LocationId,
    long Auxiliaries,
    long LineInfantry,
    int TrainingDays,
    IReadOnlyList<ProvinceId> AdjacentDestinations);

/// <summary>
/// 代理能看到的粮运路线候选摘要（从权威世界状态裁剪，只含最小可行动字段）。
/// </summary>
/// <remarks>
/// 候选集只容纳"可行动"路线：起点有粮、目的地有余量、路线在途未满；
/// 模型/规则都只能从该候选集选择路线，不能自行发明不存在的路线（P1-AGENT-01/02）。
/// </remarks>
public sealed record RouteObservation(
    RouteId RouteId,
    ProvinceId From,
    ProvinceId To,
    long SourceGrain,
    long DestinationHeadroom,
    long RouteCapacity,
    long InTransitGrain,
    int TravelHours,
    int LossPerThousand)
{
    /// <summary>可行动：起点有粮、目的地有余量、路线在途未满。</summary>
    public bool IsActionable =>
        SourceGrain > 0 && DestinationHeadroom > 0 && InTransitGrain < RouteCapacity;
}

/// <summary>
/// 上下文编译器给角色代理准备的最小观察集。
/// </summary>
/// <remarks>
/// 这里故意没有把整个 WorldState 原样塞给模型。
/// 后续可以按角色身份、职责和事件相关性继续裁剪，形成"有限认知"，降低成本也更符合玩法。
/// <see cref="Routes"/> 只含已过滤的可行动路线候选，<see cref="Armies"/> 携带军队位置与
/// 邻接合法目的地，让规则与模型都只能选择真实存在的世界对象。
/// </remarks>
public sealed record AgentContext(
    CharacterId ActorId,
    int TurnNumber,
    long TreasurySilver,
    int FacilityCount,
    IReadOnlyList<ArmyObservation> Armies,
    IReadOnlyList<RouteObservation> Routes,
    IReadOnlySet<GameCapability> Capabilities,
    long WorldVersion,
    GameTime GameTime);
