namespace MingSim.Domain.Errors;

/// <summary>
/// 结构化的规则错误。
/// </summary>
/// <remarks>
/// 初学者可以把它理解为“系统拒绝一项政令时给出的标准理由”。
/// 统一错误码后，UI、代理和测试就不需要靠解析中文句子来判断失败原因。
/// </remarks>
public sealed record SimulationError(
    string Code,
    string Message,
    bool Retryable = false,
    IReadOnlyDictionary<string, string>? Details = null);
