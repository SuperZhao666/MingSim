namespace MingSim.Agents.Decision;

/// <summary>
/// 规则式大臣本回合关注的政策方向。
/// </summary>
/// <remarks>
/// 这是一个非常小但很重要的“决策输入”：
/// 它只决定代理要观察哪一类问题，不会绕过权限校验，也不会直接修改世界状态。
/// </remarks>
public enum MinisterFocus
{
    /// <summary>关注工坊、产能和物资生产。</summary>
    Industry,

    /// <summary>关注军队编制和前线战备。</summary>
    Military,
}
