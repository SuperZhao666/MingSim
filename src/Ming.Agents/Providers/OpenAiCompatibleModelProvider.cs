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
    private const int DefaultMaxResponseBytes = 1_048_576;
    private const int DefaultMaxTokens = 512;
    // 结构化决策草案不需要接近模型上下文上限；硬上限防止调用方用极大整数关闭响应保护。
    private const int MaximumMaxResponseBytes = 4 * 1024 * 1024;
    private const int MaximumMaxTokens = 8_192;
    private const string JsonOnlyInstruction =
        "Return exactly one JSON object (for example, {}) and no markdown, prose, or code fences.";

    private static readonly Uri ChatCompletionsPath = new("chat/completions", UriKind.Relative);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpClient _httpClient;
    private readonly string _modelName;
    private readonly int _maxResponseBytes;
    private readonly int _maxTokens;

    public OpenAiCompatibleModelProvider(
        HttpClient httpClient,
        string modelName,
        int maxResponseBytes = DefaultMaxResponseBytes,
        int maxTokens = DefaultMaxTokens)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        if (httpClient.BaseAddress is not { } baseAddress ||
            !baseAddress.IsAbsoluteUri ||
            (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseAddress.Query) ||
            !string.IsNullOrEmpty(baseAddress.Fragment) ||
            !baseAddress.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The configured HttpClient BaseAddress must be an absolute HTTP or HTTPS URI with no query or fragment and a trailing slash.",
                nameof(httpClient));
        }

        if (maxResponseBytes <= 0 || maxResponseBytes > MaximumMaxResponseBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResponseBytes),
                $"The response limit must be between 1 and {MaximumMaxResponseBytes} bytes.");
        }

        if (maxTokens <= 0 || maxTokens > MaximumMaxTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTokens),
                $"The max_tokens value must be between 1 and {MaximumMaxTokens}.");
        }

        _httpClient = httpClient;
        _modelName = modelName;
        _maxResponseBytes = maxResponseBytes;
        _maxTokens = maxTokens;
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
                    content = $"{JsonOnlyInstruction}\n\nUser request:\n{request.UserInput}\n\nExpected output schema or example:\n{request.ExpectedOutputSchema}",
                },
            },
            response_format = new
            {
                type = "json_object",
            },
            max_tokens = _maxTokens,
        };

        try
        {
            var payloadJson = JsonSerializer.Serialize(payload);
            using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsPath)
            {
                Content = content,
            };
            using var response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 不读取错误正文，避免把服务端可能包含的认证信息或敏感上下文带回调用方。
                return Failure($"Provider request failed with HTTP status {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > _maxResponseBytes)
            {
                return Failure("Provider response exceeded the response size limit.");
            }

            var responseBytes = await ReadResponseBytesAsync(response.Content, _maxResponseBytes, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                return ReadContent(StrictUtf8.GetString(responseBytes));
            }
            catch (DecoderFallbackException)
            {
                return Failure("Provider response was not valid JSON.");
            }
        }
        catch (ResponseSizeLimitExceededException)
        {
            return Failure("Provider response exceeded the response size limit.");
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

    private static async Task<byte[]> ReadResponseBytesAsync(
        HttpContent content,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        await using var responseStream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var responseBytes = new MemoryStream(capacity: Math.Min(maxResponseBytes, 81_920));
        var readBuffer = new byte[81_920];
        long totalBytesRead = 0;

        while (true)
        {
            var remainingBytesIncludingOverflow = (long)maxResponseBytes + 1 - totalBytesRead;
            var bytesToRead = (int)Math.Min(readBuffer.Length, remainingBytesIncludingOverflow);
            var bytesRead = await responseStream
                .ReadAsync(readBuffer.AsMemory(0, bytesToRead), cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead == 0)
            {
                return responseBytes.ToArray();
            }

            totalBytesRead += bytesRead;
            if (totalBytesRead > maxResponseBytes)
            {
                throw new ResponseSizeLimitExceededException();
            }

            responseBytes.Write(readBuffer, 0, bytesRead);
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
                !choice.TryGetProperty("finish_reason", out var finishReason) ||
                finishReason.ValueKind != JsonValueKind.String ||
                finishReason.GetString() != "stop")
            {
                return Failure("Provider response did not finish normally.");
            }

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

    private sealed class ResponseSizeLimitExceededException : Exception;
}
