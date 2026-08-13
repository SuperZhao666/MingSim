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
            ShouldRejectMissingOrBlankContent();
            ShouldPropagateCancellation();
            ShouldRedactTransportExceptionDetails();

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
            return Task.FromResult(JsonResponse("{\"choices\":[{\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}]}"));
        });
        using var client = CreateClient(handler);
        var provider = new OpenAiCompatibleModelProvider(client, "deepseek-v4-pro-test");

        var response = provider.GenerateAsync(Request).GetAwaiter().GetResult();

        Require(response.Succeeded, "request contract setup should succeed");
        Require(capturedMethod == HttpMethod.Post, "provider should use POST");
        Require(capturedUri == new Uri("https://provider.test/v1/chat/completions"), "provider should use chat completions path");

        using var document = JsonDocument.Parse(capturedBody!);
        var root = document.RootElement;
        Require(root.GetProperty("model").GetString() == "deepseek-v4-pro-test", "request should contain configured model name");
        Require(root.GetProperty("response_format").GetProperty("type").GetString() == "json_object", "request should enable JSON mode");

        var messages = root.GetProperty("messages");
        Require(messages.GetArrayLength() == 2, "request should contain system and user messages");
        Require(messages[0].GetProperty("role").GetString() == "system", "first message should be system message");
        Require(messages[0].GetProperty("content").GetString() == Request.SystemInstruction, "system instruction should be preserved");
        Require(messages[1].GetProperty("role").GetString() == "user", "second message should be user message");
        var userContent = messages[1].GetProperty("content").GetString()!;
        Require(userContent.Contains(Request.UserInput, StringComparison.Ordinal), "user input should be preserved");
        Require(userContent.Contains(Request.ExpectedOutputSchema, StringComparison.Ordinal), "expected schema should enter the prompt");
    }

    private static void ShouldReturnSuccessfulContent()
    {
        using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("{\"choices\":[{\"message\":{\"content\":\"  {\\\"intent_type\\\":\\\"propose\\\"}  \"}}]}"))));
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
                Content = new StringContent(privateResponseBody),
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

    private static void ShouldRejectMissingOrBlankContent()
    {
        var responses = new[]
        {
            "{}",
            "[]",
            "{\"choices\":[]}",
            "{\"choices\":[null]}",
            "{\"choices\":[\"not-an-object\"]}",
            "{\"choices\":[{\"message\":{}}]}",
            "{\"choices\":[{\"message\":null}]}",
            "{\"choices\":[{\"message\":{\"content\":\"   \"}}]}",
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

    private static HttpClient CreateClient(FakeHttpMessageHandler handler) =>
        new(handler)
        {
            BaseAddress = new Uri("https://provider.test/v1/"),
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
}
