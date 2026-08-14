using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using MingSim.Agents.Providers;

namespace MingSim.Agents.ContractTests;

internal static class Program
{
    private static readonly ModelRequest Request = new(
        "You are a cautious adviser.",
        "Suggest one legal action.",
        "{\"type\":\"object\",\"properties\":{\"intent_type\":{\"type\":\"string\"}}}");

    private static int Main()
    {
        try
        {
            ShouldSendOpenAiCompatibleRequest();
            ShouldReturnSuccessfulContent();
            ShouldSummarizeHttpErrorsWithoutResponseBody();
            ShouldRejectInvalidJson();
            ShouldRejectNoContent();
            ShouldRejectNonCompleteFinishReasons();
            ShouldRejectMissingOrBlankContent();
            ShouldEnforceResponseSizeLimit();
            ShouldTimeoutWhenResponseBodyStalls();
            ShouldValidateBaseAddressAndLimits();
            ShouldPropagateCancellation();
            ShouldRedactTimeoutAndDisposeFailures();
            ShouldSupportConcurrentCalls();
            ShouldRedactTransportExceptionDetails();
            ShouldIgnoreBaseAddressMutationsAfterConstruction();
            ShouldHardStopWhenHeadersStall();
            ShouldHardStopWhenGetStreamStalls();
            ShouldHardStopWhenBodyReadStalls();
            ShouldIsolateConcurrentStalledCalls();

            Console.WriteLine("Ming.Agents OpenAI-compatible contract tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Ming.Agents contract tests failed: {exception.Message}");
            Console.Error.WriteLine(exception.StackTrace);
            return 1;
        }
    }

    private static void ShouldSendOpenAiCompatibleRequest()
    {
        HttpMethod? capturedMethod = null;
        Uri? capturedUri = null;
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            capturedMethod = request.Method;
            capturedUri = request.RequestUri;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(JsonResponse("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}]}"));
        });
        using var client = CreateClient(handler);
        var provider = new OpenAiCompatibleModelProvider(client, "deepseek-v4-pro-test", maxTokens: 8_192);

        var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

        Require(response.Succeeded, "request contract setup should succeed");
        Require(capturedMethod == HttpMethod.Post, "provider should use POST");
        Require(capturedUri == new Uri("https://provider.test/v1/chat/completions"), "provider should use chat completions path");

        using var document = JsonDocument.Parse(capturedBody!);
        var root = document.RootElement;
        Require(root.GetProperty("model").GetString() == "deepseek-v4-pro-test", "request should contain configured model name");
        Require(root.GetProperty("response_format").GetProperty("type").GetString() == "json_object", "request should enable JSON mode");
        Require(root.GetProperty("max_tokens").GetInt32() == 8_192, "request should contain the configured bounded max_tokens value");

        var messages = root.GetProperty("messages");
        Require(messages.GetArrayLength() == 2, "request should contain system and user messages");
        Require(messages[0].GetProperty("role").GetString() == "system", "first message should be system message");
        Require(messages[0].GetProperty("content").GetString() == Request.SystemInstruction, "system instruction should be preserved");
        Require(messages[1].GetProperty("role").GetString() == "user", "second message should be user message");
        var userContent = messages[1].GetProperty("content").GetString()!;
        Require(userContent.Contains("Return exactly one JSON object", StringComparison.Ordinal), "user message should contain an explicit JSON-only instruction");
        Require(userContent.Contains("{}", StringComparison.Ordinal), "JSON-only instruction should contain a literal JSON object example");
        Require(userContent.Contains(Request.UserInput, StringComparison.Ordinal), "user input should be preserved");
        Require(userContent.Contains(Request.ExpectedOutputSchema, StringComparison.Ordinal), "expected schema should enter the prompt");
    }

    private static void ShouldReturnSuccessfulContent()
    {
        using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"  {\\\"intent_type\\\":\\\"propose\\\"}  \"}}]}"))));
        var provider = new OpenAiCompatibleModelProvider(client, "test-model");

        var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

        Require(response.Succeeded, "valid response should succeed");
        Require(response.Content == "  {\"intent_type\":\"propose\"}  ", "provider should return content as untrusted text");
        Require(response.ErrorMessage is null, "successful response should not contain an error");
    }

    private static void ShouldSummarizeHttpErrorsWithoutResponseBody()
    {
        const string privateResponseBody = "private-auth-detail";
        using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new ThrowOnReadContent(privateResponseBody),
            })));
        var provider = new OpenAiCompatibleModelProvider(client, "test-model");

        var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

        Require(!response.Succeeded, "non-success status should fail");
        Require(response.ErrorMessage == "Provider request failed with HTTP status 401.", "HTTP error should be a safe status summary");
        Require(!response.ErrorMessage!.Contains(privateResponseBody, StringComparison.Ordinal), "HTTP error should not echo response body");
    }

    private static void ShouldRejectInvalidJson()
    {
        using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("not-json"))));
        var provider = new OpenAiCompatibleModelProvider(client, "test-model");

        var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

        Require(!response.Succeeded, "invalid JSON should fail");
        Require(response.ErrorMessage == "Provider response was not valid JSON.", "invalid JSON should have a stable error");
    }

    private static void ShouldRejectNoContent()
    {
        using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));
        var provider = new OpenAiCompatibleModelProvider(client, "test-model");

        var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

        Require(!response.Succeeded, "204 response should not succeed");
        Require(response.ErrorMessage == "Provider response was not valid JSON.", "204 response should have a stable malformed-body error");
    }

    private static void ShouldRejectNonCompleteFinishReasons()
    {
        var responses = new[]
        {
            "{\"choices\":[{\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}]}",
            "{\"choices\":[{\"finish_reason\":null,\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"{\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"content_filter\",\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"unexpected\",\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}]}",
        };

        foreach (var responseJson in responses)
        {
            using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
                Task.FromResult(JsonResponse(responseJson))));
            var provider = new OpenAiCompatibleModelProvider(client, "test-model");
            var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

            Require(!response.Succeeded, "only finish_reason=stop should succeed");
            Require(response.ErrorMessage == "Provider response did not finish normally.", "non-complete finish reason should have a stable error");
        }
    }

    private static void ShouldRejectMissingOrBlankContent()
    {
        var responses = new[]
        {
            "{}",
            "[]",
            "{\"choices\":[]}",
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{}}]}",
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":null}]}",
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"   \"}}]}",
        };

        foreach (var responseJson in responses)
        {
            using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
                Task.FromResult(JsonResponse(responseJson))));
            var provider = new OpenAiCompatibleModelProvider(client, "test-model");
            var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

            Require(!response.Succeeded, "missing or blank content should fail");
            Require(response.ErrorMessage == "Provider response did not contain message content.", "missing content should have a stable error");
        }
    }

    private static void ShouldEnforceResponseSizeLimit()
    {
        const int configuredLimit = 64;
        const string responseJson = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{}\"}}]}";
        var responseBytes = System.Text.Encoding.UTF8.GetBytes(responseJson);

        {
            using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[configuredLimit + 1]),
                })));
            var provider = new OpenAiCompatibleModelProvider(client, "test-model", configuredLimit);
            var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

            Require(!response.Succeeded, "known response content longer than the limit should fail");
            Require(response.ErrorMessage == "Provider response exceeded the response size limit.", "known over-limit response should have a stable error");
        }

        {
            using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(responseBytes),
                })));
            var provider = new OpenAiCompatibleModelProvider(client, "test-model", responseBytes.Length);
            var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

            Require(response.Succeeded, "response exactly at the limit should succeed");
        }

        var unknownLengthContent = new UnknownLengthContent(responseBytes, out var trackingStream);
        {
            using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = unknownLengthContent,
                })));
            var provider = new OpenAiCompatibleModelProvider(client, "test-model", configuredLimit);
            var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

            Require(!response.Succeeded, "unknown-length response longer than the limit should fail");
            Require(response.ErrorMessage == "Provider response exceeded the response size limit.", "unknown over-limit response should have a stable error");
            Require(trackingStream.BytesRead == configuredLimit + 1, $"unknown-length stream should stop after limit plus one byte (read {trackingStream.BytesRead})");
        }
    }

    private static void ShouldTimeoutWhenResponseBodyStalls()
    {
        using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                // 头部立即返回，正文使用未知长度的真实 Stream 永久等待。
                Content = new StallingContent(),
            })));
        var provider = new OpenAiCompatibleModelProvider(
            client,
            "test-model",
            totalTimeout: TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();

        var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

        stopwatch.Stop();
        Require(!response.Succeeded, "a stalled response body should fail");
        Require(response.ErrorMessage == "Provider request timed out.", "body timeout should have a stable error");
        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"body timeout should finish promptly (took {stopwatch.Elapsed})");
    }

    private static void ShouldValidateBaseAddressAndLimits()
    {
        using (var client = CreateClient(new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("{}")))))
        {
            _ = new OpenAiCompatibleModelProvider(client, "test-model", 4 * 1024 * 1024, 8_192);
        }

        foreach (var baseAddress in new[]
        {
            "https://provider.test/v1",
            "https://provider.test/v1/?tenant=one",
            "https://provider.test/v1/#fragment",
            "ftp://provider.test/v1/",
            "/v1/",
        })
        {
            try
            {
                using var client = CreateClient(new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("{}"))), baseAddress);
                RequireThrows<ArgumentException>(
                    () => _ = new OpenAiCompatibleModelProvider(client, "test-model"),
                    $"invalid BaseAddress should be rejected: {baseAddress}");
            }
            catch (ArgumentException)
            {
                // HttpClient 也可以在设置明显非法的相对 BaseAddress 时提前拒绝。
            }
        }

        var userInfoHandlerCalls = 0;
        using (var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
                   {
                       Interlocked.Increment(ref userInfoHandlerCalls);
                       return Task.FromResult(JsonResponse("{}"));
                   }), "https://user@provider.test/v1/"))
        {
            try
            {
                _ = new OpenAiCompatibleModelProvider(client, "test-model");
                throw new InvalidOperationException("BaseAddress user info should be rejected");
            }
            catch (ArgumentException exception)
            {
                Require(exception.Message.Contains("no user info", StringComparison.Ordinal), "BaseAddress error should explain the user-info restriction");
                Require(!exception.Message.Contains("user@", StringComparison.Ordinal), "BaseAddress error should not echo user info");
            }
        }

        Require(userInfoHandlerCalls == 0, "a BaseAddress with user info must be rejected before any request is sent");

        using (var client = CreateClient(new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("{}")))))
        {
            RequireThrows<ArgumentOutOfRangeException>(
                () => _ = new OpenAiCompatibleModelProvider(client, "test-model", 0),
                "zero response limit should be rejected");
            RequireThrows<ArgumentOutOfRangeException>(
                () => _ = new OpenAiCompatibleModelProvider(client, "test-model", -1),
                "negative response limit should be rejected");
            RequireThrows<ArgumentOutOfRangeException>(
                () => _ = new OpenAiCompatibleModelProvider(client, "test-model", 4 * 1024 * 1024 + 1),
                "response limit above the hard maximum should be rejected");
            RequireThrows<ArgumentOutOfRangeException>(
                () => _ = new OpenAiCompatibleModelProvider(client, "test-model", 1, 0),
                "zero max_tokens should be rejected");
            RequireThrows<ArgumentOutOfRangeException>(
                () => _ = new OpenAiCompatibleModelProvider(client, "test-model", 1, -1),
                "negative max_tokens should be rejected");
            RequireThrows<ArgumentOutOfRangeException>(
                () => _ = new OpenAiCompatibleModelProvider(client, "test-model", 1, 8_193),
                "max_tokens above the hard maximum should be rejected");
            RequireThrows<ArgumentOutOfRangeException>(
                () => _ = new OpenAiCompatibleModelProvider(client, "test-model", 1, 1, TimeSpan.Zero),
                "zero total timeout should be rejected");
            RequireThrows<ArgumentOutOfRangeException>(
                () => _ = new OpenAiCompatibleModelProvider(client, "test-model", 1, 1, Timeout.InfiniteTimeSpan),
                "infinite total timeout should be rejected");
            RequireThrows<ArgumentOutOfRangeException>(
                () => _ = new OpenAiCompatibleModelProvider(client, "test-model", 1, 1, TimeSpan.FromMinutes(5).Add(TimeSpan.FromMilliseconds(1))),
                "total timeout above the hard maximum should be rejected");
        }
    }

    private static void ShouldPropagateCancellation()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var client = CreateClient(new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            entered.SetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }));
        var provider = new OpenAiCompatibleModelProvider(client, "test-model");
        var operation = provider.GenerateAsync(Request, cancellation.Token);

        entered.Task.GetAwaiter().GetResult();
        cancellation.Cancel();

        RequireThrows<OperationCanceledException>(
            () => operation.GetAwaiter().GetResult(),
            "cancellation should propagate to the caller");
    }

    private static void ShouldRedactTimeoutAndDisposeFailures()
    {
        using (var client = CreateClient(new FakeHttpMessageHandler(async (_, cancellationToken) =>
                   {
                       await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                       throw new InvalidOperationException("unreachable");
                   })))
        {
            client.Timeout = TimeSpan.FromMilliseconds(50);
            var provider = new OpenAiCompatibleModelProvider(client, "test-model");
            var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

            Require(!response.Succeeded, "HttpClient timeout should fail");
            Require(response.ErrorMessage == "Provider request failed.", "HttpClient timeout should use a fixed safe summary");
        }

        var disposedClient = CreateClient(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("{}"))));
        var disposedProvider = new OpenAiCompatibleModelProvider(disposedClient, "test-model");
        disposedClient.Dispose();
        var disposedResponse = disposedProvider.GenerateAsync(Request).GetAwaiter().GetResult();

        Require(!disposedResponse.Succeeded, "disposed HttpClient should fail");
        Require(disposedResponse.ErrorMessage == "Provider request failed.", "disposed HttpClient should use a fixed safe summary");
    }

    private static void ShouldSupportConcurrentCalls()
    {
        var callCount = 0;
        using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(JsonResponse("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{}\"}}]}"));
        }));
        var provider = new OpenAiCompatibleModelProvider(client, "test-model");
        var operations = Enumerable.Range(0, 8)
            .Select(_ => provider.GenerateAsync(Request))
            .ToArray();
        var responses = Task.WhenAll(operations).GetAwaiter().GetResult();

        Require(responses.All(response => response.Succeeded), "concurrent provider calls should all succeed");
        Require(callCount == operations.Length, "concurrent provider calls should not be dropped or duplicated");
    }

    private static void ShouldRedactTransportExceptionDetails()
    {
        const string privateTransportDetail = "private transport detail";
        using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
            throw new HttpRequestException(privateTransportDetail)));
        var provider = new OpenAiCompatibleModelProvider(client, "test-model");

        var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

        Require(!response.Succeeded, "transport exception should fail");
        Require(response.ErrorMessage == "Provider request failed.", "transport failure should use a fixed safe summary");
        Require(!response.ErrorMessage!.Contains(privateTransportDetail, StringComparison.Ordinal), "transport failure should not echo exception details");
    }

    private static void ShouldIgnoreBaseAddressMutationsAfterConstruction()
    {
        // 构造后调用方可以把可变的 HttpClient.BaseAddress 改成带 userinfo 的恶意目标；
        // 请求必须仍然只到达构造时冻结的安全绝对端点，不泄露 userinfo，也不换 host/path。
        var capturedUris = new List<Uri>();
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            lock (capturedUris)
            {
                capturedUris.Add(request.RequestUri!);
            }

            return Task.FromResult(JsonResponse("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{}\"}}]}"));
        });
        using var client = CreateClient(handler, "https://provider.test/v1/");
        var provider = new OpenAiCompatibleModelProvider(client, "test-model");

        // 首次 GenerateAsync 之前把 BaseAddress 换成带 userinfo 的恶意 URI。
        client.BaseAddress = new Uri("https://attacker:secret@evil.test/steal/");

        var userInfoResponse = provider.GenerateAsync(Request).GetAwaiter().GetResult();

        Require(userInfoResponse.Succeeded, "frozen endpoint should still serve valid responses");
        Require(capturedUris.Count == 1, "exactly one request should be sent");
        var userInfoCaptured = capturedUris[0];
        Require(userInfoCaptured == new Uri("https://provider.test/v1/chat/completions"), "request must keep the frozen safe endpoint");
        Require(string.IsNullOrEmpty(userInfoCaptured.UserInfo), "request must not carry user info");
        Require(!userInfoCaptured.ToString().Contains("attacker", StringComparison.Ordinal) &&
                !userInfoCaptured.ToString().Contains("secret", StringComparison.Ordinal),
            "request must not leak mutated credentials");
        Require(!userInfoCaptured.ToString().Contains("evil.test", StringComparison.Ordinal), "request must not reach a mutated host");

        // 换成其他 host 和 path 同样无效（HttpClient 首次发送后也会拒绝再改 BaseAddress，这里用全新实例验证）。
        var hijackUris = new List<Uri>();
        var hijackHandler = new FakeHttpMessageHandler((request, _) =>
        {
            lock (hijackUris)
            {
                hijackUris.Add(request.RequestUri!);
            }

            return Task.FromResult(JsonResponse("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{}\"}}]}"));
        });
        using var hijackClient = CreateClient(hijackHandler, "https://provider.test/v1/");
        var hijackProvider = new OpenAiCompatibleModelProvider(hijackClient, "test-model");
        hijackClient.BaseAddress = new Uri("https://other.test/hijack/");

        var hijackResponse = hijackProvider.GenerateAsync(Request).GetAwaiter().GetResult();

        Require(hijackResponse.Succeeded, "host/path mutation should still use the frozen endpoint");
        Require(hijackUris.Count == 1, "host/path mutation should still send exactly one request");
        Require(hijackUris[0] == new Uri("https://provider.test/v1/chat/completions"), "host/path mutation must not redirect the request");
    }

    private static void ShouldHardStopWhenHeadersStall()
    {
        // 不合作 handler：完全忽略取消令牌，SendAsync 永不返回。
        CancellationToken? capturedToken = null;
        var handler = new FakeHttpMessageHandler((_, cancellationToken) =>
        {
            capturedToken = cancellationToken;
            return new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        });
        using var client = CreateClient(handler);
        var provider = new OpenAiCompatibleModelProvider(
            client,
            "test-model",
            totalTimeout: TimeSpan.FromMilliseconds(80));
        var stopwatch = Stopwatch.StartNew();

        var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

        stopwatch.Stop();
        Require(!response.Succeeded, "a stalled header response should fail");
        Require(response.ErrorMessage == "Provider request timed out.", "header stall should use the fixed timeout summary");
        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"header stall should finish within tolerance (took {stopwatch.Elapsed})");
        Require(capturedToken is { IsCancellationRequested: true }, "the cooperative token must be signaled at the hard boundary");
    }

    private static void ShouldHardStopWhenGetStreamStalls()
    {
        // 头部立即返回，但取正文流的调用永不完成（HttpContent 不合作）。
        using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StallingGetStreamContent(),
            })));
        var provider = new OpenAiCompatibleModelProvider(
            client,
            "test-model",
            totalTimeout: TimeSpan.FromMilliseconds(80));
        var stopwatch = Stopwatch.StartNew();

        var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

        stopwatch.Stop();
        Require(!response.Succeeded, "a stalled GetStream call should fail");
        Require(response.ErrorMessage == "Provider request timed out.", "GetStream stall should use the fixed timeout summary");
        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"GetStream stall should finish within tolerance (took {stopwatch.Elapsed})");
    }

    private static void ShouldHardStopWhenBodyReadStalls()
    {
        // 头部立即返回、长度未知，但 ReadAsync 永久等待且合法忽略取消令牌。
        var content = new NonCooperativeStallingContent(out var stallingStream);
        using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            })));
        var provider = new OpenAiCompatibleModelProvider(
            client,
            "test-model",
            totalTimeout: TimeSpan.FromMilliseconds(80));
        var stopwatch = Stopwatch.StartNew();

        var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

        stopwatch.Stop();
        Require(!response.Succeeded, "a stalled unknown-length body should fail");
        Require(response.ErrorMessage == "Provider request timed out.", "body stall should use the fixed timeout summary");
        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"body stall should finish within tolerance (took {stopwatch.Elapsed})");
        Require(stallingStream.IsDisposed, "the stalled body stream should be disposed after the hard boundary");
    }

    private static void ShouldIsolateConcurrentStalledCalls()
    {
        // headers 停滞与 body 停滞并发：各自独立的总超时互不干扰，且都在容差内结束。
        var stallingBody = new NonCooperativeStallingContent(out var stallingStream);
        using var headersClient = CreateClient(new FakeHttpMessageHandler((_, _) =>
            new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously).Task));
        using var bodyClient = CreateClient(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = stallingBody,
            })));
        var headersProvider = new OpenAiCompatibleModelProvider(headersClient, "test-model", totalTimeout: TimeSpan.FromMilliseconds(80));
        var bodyProvider = new OpenAiCompatibleModelProvider(bodyClient, "test-model", totalTimeout: TimeSpan.FromMilliseconds(80));
        var stopwatch = Stopwatch.StartNew();

        var responses = Task.WhenAll(
            headersProvider.GenerateAsync(Request),
            bodyProvider.GenerateAsync(Request)).GetAwaiter().GetResult();

        stopwatch.Stop();
        Require(responses.All(response => !response.Succeeded && response.ErrorMessage == "Provider request timed out."),
            "both stalled calls should time out with the fixed summary");
        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"concurrent stalls should not extend each other (took {stopwatch.Elapsed})");
        Require(stallingStream.IsDisposed, "the stalled body stream should be disposed after the hard boundary");
    }

    private static HttpClient CreateClient(
        FakeHttpMessageHandler handler,
        string baseAddress = "https://provider.test/v1/") =>
        new(handler)
        {
            BaseAddress = new Uri(baseAddress, UriKind.RelativeOrAbsolute),
        };

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(message + $" (got {exception.GetType().Name})");
        }

        throw new InvalidOperationException(message);
    }

    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class ThrowOnReadContent(string detail) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException(detail);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StallingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("serialization should not be used by the provider");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new StallingStream());
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override int Read(Span<byte> buffer) => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken = default) =>
            WaitForCancellationAsync(cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            WaitForCancellationAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        private static async ValueTask<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class StallingGetStreamContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("serialization should not be used by the provider");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }

    private sealed class NonCooperativeStallingContent : HttpContent
    {
        public NonCooperativeStallingContent(out NonCooperativeStallingStream stream)
        {
            stream = new NonCooperativeStallingStream();
            Stream = stream;
        }

        public NonCooperativeStallingStream Stream { get; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("serialization should not be used by the provider");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(Stream);
    }

    private sealed class NonCooperativeStallingStream : Stream
    {
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override int Read(Span<byte> buffer) => throw new NotSupportedException();

        // 永久等待且完全忽略取消令牌：ReadAsync 返回的任务从不完成。
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken = default) =>
            NeverCompletingRead();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(NeverCompletingRead());

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Interlocked.Exchange(ref _disposed, 1);
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return base.DisposeAsync();
        }

        private static Task<int> NeverCompletingRead() =>
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnknownLengthContent(byte[] bytes, out TrackingStream trackingStream)
        {
            _bytes = bytes;
            trackingStream = new TrackingStream(bytes);
            TrackingStream = trackingStream;
        }

        public TrackingStream TrackingStream { get; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(TrackingStream);
    }

    private sealed class TrackingStream(byte[] bytes) : Stream
    {
        private readonly byte[] _bytes = bytes;
        private int _position;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _bytes.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _position = (int)value;
            }
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadCore(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            return ReadCore(buffer);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _bytes.Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = newPosition;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        private int ReadCore(Span<byte> buffer)
        {
            var bytesToRead = Math.Min(buffer.Length, _bytes.Length - _position);
            if (bytesToRead <= 0)
            {
                return 0;
            }

            _bytes.AsSpan(_position, bytesToRead).CopyTo(buffer);
            _position += bytesToRead;
            BytesRead += bytesToRead;
            return bytesToRead;
        }
    }
}
