namespace MingSim.Agents.Providers;

/// <summary>模型网关的最小请求格式。</summary>
public sealed record ModelRequest(
    string SystemInstruction,
    string UserInput,
    string ExpectedOutputSchema);

/// <summary>模型网关的最小响应格式。</summary>
public sealed record ModelResponse(
    bool Succeeded,
    string Content,
    string? ErrorMessage = null);

/// <summary>
/// 兼容 OpenAI 风格接口的抽象。
/// </summary>
/// <remarks>
/// Domain 和 Simulation 不引用这个接口，因此模型供应商换成 DeepSeek、Qwen、GLM 或本地模型时，
/// 世界规则不需要改动。模型最终仍应输出可反序列化、可验证的 WorldIntent。
/// </remarks>
public interface IModelProvider
{
    Task<ModelResponse> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}
