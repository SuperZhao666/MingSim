namespace MingSim.Agents.Providers;

/// <summary>
/// 模型调用预算上限（长整型，避免浮点金额误差）。
/// </summary>
/// <remarks>
/// 金额单位统一用“毫厘”（1/1000 元），全部用 long 计，避免浮点累计误差；
/// token 与金额双上限，任一耗尽即视为预算耗尽。价格与上限都是易变配置，
/// 由调用方（组合根/玩家设置）提供，不写死成永久规则（doc 07 §13.2）。
/// </remarks>
public sealed record ModelBudget(
    long MaxTokens,
    long MaxCostMillis,
    long CostPerTokenMillis);

/// <summary>
/// 预算记账器：累计已消耗 token/金额，并在发起模型调用前做预算闸门预检。
/// </summary>
/// <remarks>
/// 调用方（决策管线）在单写者语义下串行访问本类；预算只影响“是否发起模型调用”，
/// 不进入世界状态、不参与存档/快照。溢出按饱和处理（视为预算耗尽），
/// 绝不让记账数值回绕变成“还有钱”。
/// </remarks>
public sealed class ModelBudgetTracker
{
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

    /// <summary>已消耗 token（长整型累计）。</summary>
    public long SpentTokens => _spentTokens;

    /// <summary>已消耗金额（毫厘，长整型累计）。</summary>
    public long SpentCostMillis => _spentCostMillis;

    /// <summary>任一上限已耗尽。</summary>
    public bool IsExhausted =>
        _spentTokens >= _budget.MaxTokens || _spentCostMillis >= _budget.MaxCostMillis;

    /// <summary>按单位价格换算 token 的金额（毫厘）；溢出饱和到 long.MaxValue。</summary>
    public long CostFor(long tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        return MultiplySaturating(tokens, _budget.CostPerTokenMillis);
    }

    /// <summary>
    /// 预算闸门：发起调用前检查“加上本次预估 token 后是否仍在双上限内”。
    /// 返回 false 时调用方必须停止新模型请求并回退规则路径。
    /// </summary>
    public bool CanAfford(long estimatedTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedTokens);
        if (estimatedTokens == 0)
        {
            return !IsExhausted;
        }

        var projectedTokens = AddSaturating(_spentTokens, estimatedTokens);
        var projectedCost = AddSaturating(_spentCostMillis, MultiplySaturating(estimatedTokens, _budget.CostPerTokenMillis));
        return projectedTokens <= _budget.MaxTokens && projectedCost <= _budget.MaxCostMillis;
    }

    /// <summary>调用结束后按实际（估算）token 记账；失败调用同样消耗请求 token，防止反复锤打故障 Provider。</summary>
    public void RecordUsage(long tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        _spentTokens = AddSaturating(_spentTokens, tokens);
        _spentCostMillis = AddSaturating(_spentCostMillis, MultiplySaturating(tokens, _budget.CostPerTokenMillis));
    }

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
}
