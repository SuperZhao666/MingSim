namespace MingSim.Agents.Providers;

/// <summary>
/// 模型文本 token 的确定性估算：按字符数/4 估算，只用于预算记账与审计摘要。
/// </summary>
/// <remarks>
/// 这是预算闸门与审计的输入，不是计费真相：真实账单以 Provider usage 为准（后续接入时替换）。
/// 估算规则固定、纯函数、无状态，保证同一文本在任何运行中得出同一数字（确定性）。
/// </remarks>
public static class TokenEstimation
{
    /// <summary>按每 4 个字符约 1 个 token 估算；空文本记 0，非空至少记 1。</summary>
    public static long FromText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return Math.Max(1, (text.Length + 3) / 4);
    }
}
