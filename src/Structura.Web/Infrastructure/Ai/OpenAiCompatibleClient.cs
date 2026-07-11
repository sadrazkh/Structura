using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Http;

namespace Structura.Web.Infrastructure.Ai;

public sealed record AiMessage(string Role, string Content);

public sealed record AiCompletionResult(
    string? Content, string? FinishReason, int InputTokens, int OutputTokens,
    string RawResponse, int DurationMs);

public sealed class AiProviderException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public bool IsRetryable => StatusCode is 429 or >= 500 or 0;
}

/// <summary>
/// One adapter for every OpenAI-compatible chat-completions endpoint
/// (OpenRouter, NVIDIA, custom). All traffic flows through SafeHttp (AiProvider profile).
/// </summary>
public sealed class OpenAiCompatibleClient(SafeHttpClientFactory safeHttp)
{
    public async Task<AiCompletionResult> CompleteAsync(
        AiConfigDocument config, string apiKey, IReadOnlyList<AiMessage> messages,
        JsonObject? responseFormat, int? maxTokensOverride, CancellationToken ct)
    {
        var url = new Uri(config.BaseUrl.TrimEnd('/') + "/chat/completions");
        var payload = new JsonObject
        {
            ["model"] = config.Model,
            ["temperature"] = config.Temperature,
            ["max_tokens"] = maxTokensOverride ?? config.MaxOutputTokens,
            ["messages"] = new JsonArray(messages
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content })
                .ToArray()),
        };
        if (responseFormat is not null) payload["response_format"] = responseFormat;

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        if (config.Provider == AiProviders.OpenRouter)
        {
            request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://structura.local");
            request.Headers.TryAddWithoutValidation("X-Title", "Structura");
        }

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        string body;
        try
        {
            response = await safeHttp.SendAsync(request, SafeHttpProfile.AiProvider,
                TimeSpan.FromSeconds(config.TimeoutSeconds), ct);
            body = await SafeHttpClientFactory.ReadBodyCappedAsync(response, SafeHttpProfile.AiProvider, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiProviderException(0, $"Provider request timed out after {config.TimeoutSeconds}s.");
        }
        catch (HttpRequestException e)
        {
            throw new AiProviderException(0, $"Provider is unreachable: {e.Message}");
        }
        stopwatch.Stop();

        using var _ = response;
        if (!response.IsSuccessStatusCode)
            throw new AiProviderException((int)response.StatusCode, ExtractErrorMessage(body, (int)response.StatusCode));

        try
        {
            var json = JsonNode.Parse(body)!;
            var choice = json["choices"]?[0];
            return new AiCompletionResult(
                Content: choice?["message"]?["content"]?.GetValue<string>(),
                FinishReason: choice?["finish_reason"]?.GetValue<string>(),
                InputTokens: json["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0,
                OutputTokens: json["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0,
                RawResponse: body,
                DurationMs: (int)stopwatch.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new AiProviderException(0, "Provider returned a response that is not valid OpenAI-compatible JSON.");
        }
    }

    private static string ExtractErrorMessage(string body, int statusCode)
    {
        try
        {
            var message = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message)) return $"Provider error {statusCode}: {message}";
        }
        catch (JsonException)
        {
            // fall through to the generic message
        }
        return statusCode switch
        {
            401 or 403 => $"Provider rejected the API key (HTTP {statusCode}).",
            404 => "Provider endpoint or model was not found (HTTP 404). Check the base URL and model.",
            429 => "Provider rate limit reached (HTTP 429).",
            _ => $"Provider returned HTTP {statusCode}.",
        };
    }
}
