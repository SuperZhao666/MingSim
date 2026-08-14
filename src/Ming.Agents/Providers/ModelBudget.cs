namespace MingSim.Agents.Providers;

/// <summary>模型调用预算上限（长整型，避免浮点金额误差）。</summary>
/// <remarks>
/// 金额单位统一用“毫厘”（1/1000 元），全部用 long 计，避免浮点累计误差；
/// token 与金额双上限，任一耗尽即视为预算耗尽。价格与上限都是易变配置，
/// 由调用方（组合根/玩家设置）提供，不写死成永久规则（doc 07 §13.2）。
/// </remarks>
public sealed record ModelBudget(
    long MaxTokens,
    long MaxCostMillis,
    long CostPerTokenMillis);

/// <summary>一次已提交的预算预留：发起调用前原子占用的 token 额度。</summary>
/// <remarks>
/// <see cref="ModelBudgetTracker.TryReserve"/> 成功时返回预留句柄；调用结束后必须
/// 恰好结算一次（<see cref="ModelBudgetTracker.Settle"/>）：按实际用量补记差额、
/// 未用额度返还、全额返还（取消/未发起调用路径）。预留句柄是不可变的纯数据，
/// 不携带锁或对预算的引用。
/// </remarks>
public readonly struct ModelBudgetReservation
{
    internal ModelBudgetReservation(long reservedTokens)
    {
        ReservedTokens = reservedTokens;
    }

    /// <summary>本次预留占用的 token 额度。</summary>
    public long ReservedTokens { get; }
}

/// <summary>
/// 预算记账器：以原子预留（reserve）驱动模型调用，调用结束后按实际用量结算（settle）。
/// </summary>
/// <remarks>
/// P1-AGENT-04（Wave 5A 审计）：修复前闸门预检（CanAfford）与记账（RecordUsage）分离，
/// 两个并发调用可能同时通过“检查+提交”两步之间留出的窗口，总额超限。
/// 现在模型路径唯一入口是 <see cref="TryReserve"/>：在锁内一次性完成“检查+提交”，
/// 不足即拒绝；调用结束后 <see cref="Settle"/> 修正为实际用量。预留与记账分离的
/// 含义是：锁只覆盖整数累加与闸门判断，绝不包裹网络 await 或世界推进——模型调用
/// 期间不持有任何预算锁。
/// 预算只影响“是否发起模型调用”，不进入世界状态、不参与存档/快照。溢出按饱和处理
/// （视为预算耗尽），绝不让记账数值回绕变成“还有钱”。
/// <see cref="CanAfford"/> / <see cref="RecordUsage"/> 保留为纯预检/直接记账的兼容入口
/// （测试与外部调用方使用），生产模型路径必须走 TryReserve/Settle。
/// </remarks>
public sealed class ModelBudgetTracker
{
    private readonly object _gate = new();
    private readonly ModelBudget _budget;
    private long _spentTokens;
    private long _spentCostMillis;

    public ModelBudgetTracker(ModelBudget budget)
    {
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        if (budget.MaxTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "MaxTokens 必须大于 0。");
        }

        if (budget.MaxCostMillis <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "MaxCostMillis 必须大于 0。");
        }

        if (budget.CostPerTokenMillis < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "CostPerTokenMillis 不能为负数。");
        }
    }

    /// <summary>已消耗 token（长整型累计；含尚未结算的预留占用）。</summary>
    public long SpentTokens
    {
        get
        {
            lock (_gate)
            {
                return _spentTokens;
            }
        }
    }

    /// <summary>已消耗金额（毫厘，长整型累计；含尚未结算的预留占用）。</summary>
    public long SpentCostMillis
    {
        get
        {
            lock (_gate)
            {
                return _spentCostMillis;
            }
        }
    }

    /// <summary>任一上限已耗尽。</summary>
    public bool IsExhausted
    {
        get
        {
            lock (_gate)
            {
                return IsExhaustedCore;
            }
        }
    }

    /// <summary>按单位价格换算 token 的金额（毫厘）；溢出饱和到 long.MaxValue。纯函数，无需加锁。</summary>
    public long CostFor(long tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        return MultiplySaturating(tokens, _budget.CostPerTokenMillis);
    }

    /// <summary>
    /// 预算闸门（兼容预检入口）：只检查“加上本次预估 token 后是否仍在双上限内”，不提交任何记账。
    /// 已耗尽（含溢出饱和到上限）时一律拒绝新调用。生产模型路径必须使用
    /// <see cref="TryReserve"/>（原子检查+提交），不能先 CanAfford 再调用，否则并发窗口会超限。
    /// </summary>
    public bool CanAfford(long estimatedTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedTokens);
        lock (_gate)
        {
            if (IsExhaustedCore)
            {
                return false;
            }

            if (estimatedTokens == 0)
            {
                return true;
            }

            var projectedTokens = AddSaturating(_spentTokens, estimatedTokens);
            var projectedCost = AddSaturating(_spentCostMillis, MultiplySaturating(estimatedTokens, _budget.CostPerTokenMillis));
            return projectedTokens <= _budget.MaxTokens && projectedCost <= _budget.MaxCostMillis;
        }
    }

    /// <summary>
    /// 原子预留（P1-AGENT-04 主入口）：在锁内一次性完成“检查+提交”——
    /// 预算不足（含已耗尽与饱和封顶）时返回 false 且不改变任何记账；
    /// 成功时立即把预估额度计入已消耗并返回预留句柄，后续并发预留只能看到
    /// 被占用的余量，因此“恰在上限的两个并发预留只有一个成功”。
    /// 调用结束后必须调用 <see cref="Settle"/> 恰好一次，未用额度返还。
    /// </summary>
    public bool TryReserve(long estimatedTokens, out ModelBudgetReservation reservation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedTokens);
        lock (_gate)
        {
            if (IsExhaustedCore)
            {
                reservation = default;
                return false;
            }

            if (estimatedTokens == 0)
            {
                reservation = new ModelBudgetReservation(0);
                return true;
            }

            var projectedTokens = AddSaturating(_spentTokens, estimatedTokens);
            var projectedCost = AddSaturating(_spentCostMillis, MultiplySaturating(estimatedTokens, _budget.CostPerTokenMillis));
            if (projectedTokens > _budget.MaxTokens || projectedCost > _budget.MaxCostMillis)
            {
                reservation = default;
                return false;
            }

            _spentTokens = projectedTokens;
            _spentCostMillis = projectedCost;
            reservation = new ModelBudgetReservation(estimatedTokens);
            return true;
        }
    }

    /// <summary>
    /// 结算一次预留：把预留额度修正为实际用量（actualTokens ≥ 0）。
    /// - 实际 &gt; 预留（如响应 token）：补记差额；
    /// - 实际 &lt; 预留：返还未用额度；
    /// - 实际 == 0（取消/未发起调用）：全额返还。
    /// 每次成功的 <see cref="TryReserve"/> 必须恰好结算一次；预留占用期间
    /// 不持有任何锁外的网络/世界调用。
    /// </summary>
    public void Settle(ModelBudgetReservation reservation, long actualTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actualTokens);
        lock (_gate)
        {
            var delta = actualTokens - reservation.ReservedTokens;
            if (delta > 0)
            {
                _spentTokens = AddSaturating(_spentTokens, delta);
                _spentCostMillis = AddSaturating(_spentCostMillis, MultiplySaturating(delta, _budget.CostPerTokenMillis));
            }
            else if (delta < 0)
            {
                // 未用额度返还；防御性饱和到 0，绝不回绕成负数“凭空有钱”。
                var refundTokens = checked(-delta);
                _spentTokens = SubtractSaturatingAtZero(_spentTokens, refundTokens);
                _spentCostMillis = SubtractSaturatingAtZero(_spentCostMillis, MultiplySaturating(refundTokens, _budget.CostPerTokenMillis));
            }
        }
    }

    /// <summary>直接按实际（估算）token 记账的兼容入口；并发安全，不丢更新。</summary>
    public void RecordUsage(long tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        lock (_gate)
        {
            _spentTokens = AddSaturating(_spentTokens, tokens);
            _spentCostMillis = AddSaturating(_spentCostMillis, MultiplySaturating(tokens, _budget.CostPerTokenMillis));
        }
    }

    // 调用方已持锁时的内部判定，避免公开属性重入锁。
    private bool IsExhaustedCore =>
        _spentTokens >= _budget.MaxTokens || _spentCostMillis >= _budget.MaxCostMillis;

    private static long MultiplySaturating(long value, long factor)
    {
        if (factor == 0)
        {
            return 0;
        }

        return value > long.MaxValue / factor ? long.MaxValue : value * factor;
    }

    private static long AddSaturating(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        return left + right;
    }

    private static long SubtractSaturatingAtZero(long left, long right)
    {
        if (right <= 0)
        {
            return left;
        }

        return left >= right ? left - right : 0;
    }
}
