using MingSim.Domain.Common;

namespace MingSim.Agents.Runtime;

/// <summary>代理能看到的军队摘要。</summary>
public sealed record ArmyObservation(
    ArmyId ArmyId,
    string Name,
    long Auxiliaries,
    long LineInfantry,
    int TrainingDays);

/// <summary>
/// 上下文编译器给角色代理准备的最小观察集。
/// </summary>
/// <remarks>
/// 这里故意没有把整个 WorldState 原样塞给模型。
/// 后续可以按角色身份、职责和事件相关性继续裁剪，形成“有限认知”，降低成本也更符合玩法。
/// </remarks>
public sealed record AgentContext(
    CharacterId ActorId,
    int TurnNumber,
    long TreasurySilver,
    int FacilityCount,
    IReadOnlyList<ArmyObservation> Armies,
    IReadOnlySet<GameCapability> Capabilities);
