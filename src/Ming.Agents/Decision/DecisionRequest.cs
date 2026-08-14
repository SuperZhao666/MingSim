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

/// <summary>模型路径被跳过或放弃的具体原因；Source == Model 时为 null。</summary>
/// <remarks>
/// 让“回退”成为显式结果而不是静默成功（doc 07 §12）：UI 可以据此显示
/// 规则模式/预算耗尽/离线等状态（doc 09），审计也可以只靠类别复现原因。
/// </remarks>
public enum ModelFallbackReason
{
    /// <summary>未配置 Provider，模型路径从未参与。</summary>
    NotConfigured,

    /// <summary>预算耗尽，调用在发起前被拦截。</summary>
    BudgetExceeded,

    /// <summary>Provider 失败或超时（含抛异常）。</summary>
    ProviderFailed,

    /// <summary>模型输出未通过白名单结构化解析。</summary>
    ParseFailed,

    /// <summary>模型结果到达时已过期（半开区间），被丢弃。</summary>
    Expired,
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
/// FallbackReason 说明模型路径为何被跳过/放弃（Source == Rules 时），防止静默成功。
/// </remarks>
public sealed record DecisionResult(
    string DecisionId,
    DecisionSource Source,
    IReadOnlyList<WorldIntent> Intents,
    GameTime AcceptedGameTime,
    ModelFallbackReason? FallbackReason = null);
