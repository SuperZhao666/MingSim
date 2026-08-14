using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MingSim.Agents.Providers;

/// <summary>
/// 通过 OpenAI Chat Completions 兼容接口生成不可信的模型文本。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HttpClient"/> 必须由组合根预先配置好认证头；这个适配器不接收、读取、记录或返回 API Key。
/// 构造时会校验 BaseAddress（必须是绝对 http/https、无 userinfo/query/fragment、路径以斜杠结尾），
/// 并把安全的 chat-completions 绝对端点冻结为只读字段；此后调用方再修改 <c>HttpClient.BaseAddress</c>
/// 也不会改变请求目标，防止把 userinfo 或其他主机/路径注入到已校验端点之外。
/// </para>
/// <para>
/// 返回值仍然只是模型提供的文本。这里不把文本反序列化成 WorldIntent，也不接触 WorldState；后续边界必须再次做结构校验、权限检查和 Simulation 提交。
/// </para>
/// </remarks>
public sealed class OpenAiCompatibleModelProvider : IModelProvider
{
    private const int DefaultMaxResponseBytes = 1_048_576;
    private const int DefaultMaxTokens = 512;
    private static readonly TimeSpan DefaultTotalTimeout = TimeSpan.FromSeconds(30);
    // 结构化决策草案不需要接近模型上下文上限；硬上限防止调用方用极大整数关闭响应保护。
    private const int MaximumMaxResponseBytes = 4 * 1024 * 1024;
    private const int MaximumMaxTokens = 8_192;
    private static readonly TimeSpan MaximumTotalTimeout = TimeSpan.FromMinutes(5);
    private const string JsonOnlyInstruction =
        "Return exactly one JSON object (for example, {}) and no markdown, prose, or code fences.";

    private static readonly Uri ChatCompletionsPath = new("chat/completions", UriKind.Relative);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpClient _httpClient;
    private readonly string _modelName;
    private readonly int _maxResponseBytes;
    private readonly int _maxTokens;
    private readonly TimeSpan _totalTimeout;
    // 构造时从已校验的 BaseAddress 冻结出的安全绝对端点；发送只使用它，不读可变的 HttpClient.BaseAddress。
    private readonly Uri _chatCompletionsEndpoint;

    public OpenAiCompatibleModelProvider(
        HttpClient httpClient,
        string modelName,
        int maxResponseBytes = DefaultMaxResponseBytes,
        int maxTokens = DefaultMaxTokens,
        TimeSpan? totalTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var resolvedTotalTimeout = totalTimeout ?? DefaultTotalTimeout;

        if (httpClient.BaseAddress is not { } baseAddress ||
            !baseAddress.IsAbsoluteUri ||
            (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseAddress.UserInfo) ||
            !string.IsNullOrEmpty(baseAddress.Query) ||
            !string.IsNullOrEmpty(baseAddress.Fragment) ||
            !baseAddress.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The configured HttpClient BaseAddress must be an absolute HTTP or HTTPS URI with no user info, query, or fragment and a trailing slash.",
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

        if (resolvedTotalTimeout <= TimeSpan.Zero || resolvedTotalTimeout > MaximumTotalTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalTimeout),
                $"The total timeout must be greater than zero and no more than {MaximumTotalTimeout}.");
        }

        _httpClient = httpClient;
        _modelName = modelName;
        _maxResponseBytes = maxResponseBytes;
        _maxTokens = maxTokens;
        _totalTimeout = resolvedTotalTimeout;
        _chatCompletionsEndpoint = new Uri(baseAddress, ChatCompletionsPath);
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

        using var totalTimeoutCancellation = new CancellationTokenSource(_totalTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            totalTimeoutCancellation.Token);
        var operationCancellationToken = linkedCancellation.Token;

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
            // 请求只用构造时冻结的绝对端点；HttpClient 只在请求 URI 是相对时才拼接 BaseAddress，因此外部修改 BaseAddress 不会影响请求目标。
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsEndpoint)
            {
                Content = content,
            };

            // 只把令牌传给底层不足以形成硬边界：不合作的自定义 handler 或 stream 可以合法忽略令牌并永久等待。
            // 因此每一步都用同一个链接令牌做 WaitAsync 硬边界，边界一到就放弃底层任务而不是继续等它。
            var sendTask = _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, operationCancellationToken);
            using var response = await WaitWithinDeadlineAsync(sendTask, operationCancellationToken)
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

            var responseBytes = await ReadResponseBytesAsync(response.Content, _maxResponseBytes, operationCancellationToken)
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
        catch (OperationCanceledException) when (totalTimeoutCancellation.IsCancellationRequested)
        {
            return Failure("Provider request timed out.");
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
        CancellationToken operationCancellationToken)
    {
        // 取流和每次读都受同一总超时硬边界约束，不能只依赖流自觉响应取消。
        var streamTask = content.ReadAsStreamAsync(operationCancellationToken);
        var responseStream = await WaitWithinDeadlineAsync(streamTask, operationCancellationToken)
            .ConfigureAwait(false);

        try
        {
            using var responseBytes = new MemoryStream(capacity: Math.Min(maxResponseBytes, 81_920));
            var readBuffer = new byte[81_920];
            long totalBytesRead = 0;

            while (true)
            {
                var remainingBytesIncludingOverflow = (long)maxResponseBytes + 1 - totalBytesRead;
                var bytesToRead = (int)Math.Min(readBuffer.Length, remainingBytesIncludingOverflow);
                var readTask = responseStream
                    .ReadAsync(readBuffer.AsMemory(0, bytesToRead), operationCancellationToken)
                    .AsTask();
                var bytesRead = await WaitWithinDeadlineAsync(readTask, operationCancellationToken)
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
        finally
        {
            try
            {
                await responseStream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 释放流失败不能覆盖主结果（例如挂起的读操作让某些流在释放时抛错），也不外泄细节。
            }
        }
    }

    /// <summary>
    /// 对任意一步底层操作施加同一取消令牌的硬边界。
    /// </summary>
    /// <remarks>
    /// 底层任务可能合法忽略令牌并永不完成；WaitAsync 在令牌触发时立刻放弃等待，
    /// 被放弃的任务改由后台观察，避免未观察任务异常污染进程。
    /// </remarks>
    private static async Task<T> WaitWithinDeadlineAsync<T>(
        Task<T> task,
        CancellationToken operationCancellationToken)
    {
        try
        {
            return await task.WaitAsync(operationCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = ObserveInBackgroundAsync(task);
            throw;
        }
    }

    private static async Task ObserveInBackgroundAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // 硬边界之后才完成的任务只用于吞掉未观察异常；具体错误已由主路径统一脱敏。
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
