using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MingSim.Agents.Providers;

/// <summary>
/// 通过 OpenAI Chat Completions 兼容接口生成不可信的模型文本。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HttpClient"/> 必须由组合根预先配置好 BaseAddress、超时和认证头；这个适配器不接收、读取、记录或返回 API Key。
/// BaseAddress 应指向兼容 API 的版本根路径，例如以 <c>/v1/</c> 结尾，这样相对路径 <c>chat/completions</c> 才能解析到正确端点。
/// </para>
/// <para>
/// 返回值仍然只是模型提供的文本。这里不把文本反序列化成 WorldIntent，也不接触 WorldState；后续边界必须再次做结构校验、权限检查和 Simulation 提交。
/// </para>
/// </remarks>
public sealed class OpenAiCompatibleModelProvider : IModelProvider
{
    private static readonly Uri ChatCompletionsPath = new("chat/completions", UriKind.Relative);

    private readonly HttpClient _httpClient;
    private readonly string _modelName;

    public OpenAiCompatibleModelProvider(HttpClient httpClient, string modelName)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        if (httpClient.BaseAddress is null)
        {
            throw new ArgumentException(
                "The configured HttpClient must have a BaseAddress.",
                nameof(httpClient));
        }

        _httpClient = httpClient;
        _modelName = modelName;
    }

    public async Task<ModelResponse> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SystemInstruction);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserInput);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedOutputSchema);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = new
        {
            model = _modelName,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = request.SystemInstruction,
                },
                new
                {
                    role = "user",
                    content = $"{request.UserInput}\n\nExpected output schema:\n{request.ExpectedOutputSchema}",
                },
            },
            response_format = new
            {
                type = "json_object",
            },
        };

        try
        {
            var payloadJson = JsonSerializer.Serialize(payload);
            using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            using var response = await _httpClient
                .PostAsync(ChatCompletionsPath, content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 不读取错误正文，避免把服务端可能包含的认证信息或敏感上下文带回调用方。
                return Failure($"Provider request failed with HTTP status {(int)response.StatusCode}.");
            }

            var responseJson = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            return ReadContent(responseJson);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消必须保留为取消信号，不能伪装成普通模型失败。
            throw;
        }
        catch (Exception)
        {
            // 外部传输边界只返回固定摘要；异常文本可能包含 URL、认证头或服务端实现细节。
            return Failure("Provider request failed.");
        }
    }

    private static ModelResponse ReadContent(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return Failure("Provider response did not contain message content.");
            }

            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object ||
                !choice.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
            {
                return Failure("Provider response did not contain message content.");
            }

            var text = content.GetString();
            return string.IsNullOrWhiteSpace(text)
                ? Failure("Provider response did not contain message content.")
                : new ModelResponse(true, text);
        }
        catch (JsonException)
        {
            return Failure("Provider response was not valid JSON.");
        }
    }

    private static ModelResponse Failure(string message) => new(false, string.Empty, message);
}
