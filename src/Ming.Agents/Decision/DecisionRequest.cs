using MingSim.Domain.Common;
using MingSim.Domain.Intents;
using MingSim.Domain.Realtime;

namespace MingSim.Agents.Decision;

/// <summary>一次决策最终由哪条路径胜出。</summary>
public enum DecisionSource
{
    /// <summary>模型路径：模型输出被解析成功且未过期。</summary>
    Model,

    /// <summary>规则路径：Utility AI 的确定性评分结果（默认回退）。</summary>
    Rules,
}

/// <summary>
/// 一次结构化决策请求：程序绑定身份、观察到的世界版本和决策截止时刻。
/// </summary>
/// <remarks>
/// 截止时刻使用唯一的权威 <see cref="GameTime"/>，不新增第二套时钟。
/// 过期语义是半开区间：结果必须在截止之前（AcceptedGameTime &lt; Deadline）到达才有效，
/// 等于或超过截止一律视为过期丢弃，不能“适配一下”悄悄执行（doc 07 §12）。
/// </remarks>
public sealed record DecisionRequest(
    string DecisionId,
    CharacterId ActorId,
    long ObservedWorldVersion,
    GameTime IssuedAt,
    GameTime Deadline)
{
    /// <summary>半开区间过期判定：AcceptedGameTime 达到截止时刻即过期。</summary>
    public bool IsExpired(GameTime acceptedGameTime) => acceptedGameTime >= Deadline;
}

/// <summary>
/// 一次决策的结构化结果封装：说明由哪条路径胜出、产出哪些意图，以及被接受时的权威游戏时刻。
/// </summary>
/// <remarks>
/// 结果只携带意图列表，不携带模型原始文本或思维链（doc 07 §15）。
/// AcceptedGameTime 既是过期判定的输入，也用于审计“结果何时被世界接受”。
/// </remarks>
public sealed record DecisionResult(
    string DecisionId,
    DecisionSource Source,
    IReadOnlyList<WorldIntent> Intents,
    GameTime AcceptedGameTime);
