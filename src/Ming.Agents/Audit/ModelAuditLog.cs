namespace MingSim.Agents.Audit;

/// <summary>一次模型调用（或一次被预算拦截的调用）的审计结果类别。</summary>
public enum ModelCallOutcome
{
    /// <summary>模型输出被解析成功且未过期，意图被采用。</summary>
    Accepted,

    /// <summary>Provider 失败或超时。</summary>
    ProviderFailed,

    /// <summary>模型输出解析失败（含解析器未预期异常）。</summary>
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
/// 内存模型审计日志：按调用顺序追加条目，只增不改；容量超限时截断最旧条目（P2-4）。
/// </summary>
/// <remarks>
/// 审计不进入世界状态、不参与存档/快照（持久化在后续任务按需接入）；
/// 模型后台任务与宿主线程可能并发追加，因此内部用一把最小锁保护（P2-2）：
/// 锁只覆盖列表操作，绝不包裹模型调用或世界推进。
/// 容量上限默认 1000 条：审计是"最近调用"的固定摘要视图，不是无限历史仓库；
/// 超出上限后丢弃最旧条目并保持剩余条目的追加顺序。Entries 返回快照，
/// 避免调用方借 IReadOnlyList 视图改写内部列表。
/// </remarks>
public sealed class ModelAuditLog
{
    /// <summary>默认容量：一次会话最近 1000 次模型调用（固定摘要，内存占用可忽略）。</summary>
    public const int DefaultCapacity = 1000;

    private readonly object _gate = new();
    private readonly List<ModelAuditEntry> _entries = [];
    private readonly int _capacity;

    public ModelAuditLog(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "审计日志容量必须大于 0。");
        }

        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Append(ModelAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > _capacity)
            {
                // 截断最旧条目，只保留最新 _capacity 条；List.RemoveRange 保持剩余顺序。
                _entries.RemoveRange(0, _entries.Count - _capacity);
            }
        }
    }

    public IReadOnlyList<ModelAuditEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }
}
