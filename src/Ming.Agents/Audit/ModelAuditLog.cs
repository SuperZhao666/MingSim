namespace MingSim.Agents.Audit;

/// <summary>一次模型调用（或一次被预算拦截的调用）的审计结果类别。</summary>
public enum ModelCallOutcome
{
    /// <summary>模型输出被解析成功且未过期，意图被采用。</summary>
    Accepted,

    /// <summary>Provider 失败或超时。</summary>
    ProviderFailed,

    /// <summary>模型输出解析失败。</summary>
    ParseFailed,

    /// <summary>模型结果到达时已过期，被丢弃。</summary>
    Expired,

    /// <summary>预算耗尽，调用在发起前被拦截。</summary>
    BudgetExceeded,
}

/// <summary>
/// 一次模型调用的审计记录：只记固定摘要，绝不包含密钥、Authorization 头、
/// 请求/响应原文或模型思维链（doc 07 §15）。
/// </summary>
/// <remarks>
/// 字段均为固定形态（ID、枚举、token 估算、耗时），任何自由文本都不进审计，
/// 因此即使 Provider 异常文本或模型输出携带密钥形态文本，也不可能漏进审计。
/// </remarks>
public sealed record ModelAuditEntry(
    string DecisionId,
    string ProviderName,
    ModelCallOutcome Outcome,
    long RequestTokens,
    long ResponseTokens,
    long CostMillis,
    TimeSpan Duration,
    DateTimeOffset RecordedAt);

/// <summary>
/// 内存模型审计日志：按调用顺序追加条目，只增不改。
/// </summary>
/// <remarks>
/// 审计不进入世界状态、不参与存档/快照（持久化在后续任务按需接入）；
/// 调用方按单写者语义串行追加。Entries 返回快照，避免调用方借 IReadOnlyList 视图改写内部列表。
/// </remarks>
public sealed class ModelAuditLog
{
    private readonly List<ModelAuditEntry> _entries = [];

    public int Count => _entries.Count;

    public void Append(ModelAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
    }

    public IReadOnlyList<ModelAuditEntry> Entries => _entries.ToArray();
}
