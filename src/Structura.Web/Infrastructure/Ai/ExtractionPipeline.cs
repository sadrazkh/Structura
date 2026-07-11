using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Structura.Web.Domain;

namespace Structura.Web.Infrastructure.Ai;

public sealed record ExtractionAttempt(
    bool Succeeded,
    string? Output,          // normalized JSON (null on failure)
    string? Error,           // readable failure detail
    string? RawResponse,     // last raw model response (kept for traceability)
    int InputTokens,
    int OutputTokens,
    int DurationMs);

/// <summary>
/// The full per-record extraction flow (docs/05):
/// BuildPrompt → CallProvider (1 transport retry) → Parse → [Repair] → [1 re-ask] → Validate.
/// Structured output is requested first; providers that reject `response_format`
/// fall back to plain mode once, remembered per project+model.
/// </summary>
public sealed class ExtractionPipeline(OpenAiCompatibleClient client)
{
    private static readonly ConcurrentDictionary<string, bool> PlainModeCache = new();

    public async Task<ExtractionAttempt> ExtractAsync(
        Guid projectId, AiConfigDocument config, string apiKey,
        SchemaDocument schema, PromptConfigDocument prompt, string recordText,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = PromptBuilder.Build(schema, prompt, recordText);
        var cacheKey = $"{projectId}:{config.Model}";
        var usePlainMode = PlainModeCache.GetValueOrDefault(cacheKey);
        var responseFormat = usePlainMode ? null : ExtractionSchemaBuilder.BuildResponseFormat(schema);

        var totalInputTokens = 0;
        var totalOutputTokens = 0;
        string? lastRaw = null;

        AiCompletionResult completion;
        try
        {
            completion = await CallWithRetriesAsync(config, apiKey, messages, responseFormat, cacheKey, ct);
        }
        catch (AiProviderException e)
        {
            return Failure($"provider_error: {e.Message}", lastRaw, totalInputTokens, totalOutputTokens, stopwatch);
        }
        totalInputTokens += completion.InputTokens;
        totalOutputTokens += completion.OutputTokens;
        lastRaw = completion.RawResponse;

        if (!JsonOutputParser.TryParse(completion.Content, out var parsed))
        {
            // One re-ask: feed the invalid output back and demand corrected JSON.
            var reAskMessages = new List<AiMessage>(messages)
            {
                new("assistant", completion.Content ?? ""),
                new("user", "Your previous response was not valid JSON. Return only the corrected JSON object, nothing else."),
            };
            AiCompletionResult reAsk;
            try
            {
                reAsk = await CallWithRetriesAsync(config, apiKey, reAskMessages,
                    PlainModeCache.GetValueOrDefault(cacheKey) ? null : ExtractionSchemaBuilder.BuildResponseFormat(schema),
                    cacheKey, ct);
            }
            catch (AiProviderException e)
            {
                return Failure($"provider_error: {e.Message}", lastRaw, totalInputTokens, totalOutputTokens, stopwatch);
            }
            totalInputTokens += reAsk.InputTokens;
            totalOutputTokens += reAsk.OutputTokens;
            lastRaw = reAsk.RawResponse;

            if (!JsonOutputParser.TryParse(reAsk.Content, out parsed))
                return Failure("invalid_json: the model did not return a valid JSON object after a repair attempt and one retry.",
                    lastRaw, totalInputTokens, totalOutputTokens, stopwatch);
        }

        var outcome = OutputValidator.Validate(schema, parsed);
        if (!outcome.IsValid)
            return Failure($"validation_failed: {string.Join(" ", outcome.Errors)}",
                lastRaw, totalInputTokens, totalOutputTokens, stopwatch);

        stopwatch.Stop();
        return new ExtractionAttempt(
            Succeeded: true,
            Output: outcome.NormalizedOutput.ToJsonString(),
            Error: null,
            RawResponse: lastRaw,
            InputTokens: totalInputTokens,
            OutputTokens: totalOutputTokens,
            DurationMs: (int)stopwatch.ElapsedMilliseconds);
    }

    /// <summary>One transport retry on 429/5xx/timeout; one structured-output fallback on 400.</summary>
    private async Task<AiCompletionResult> CallWithRetriesAsync(
        AiConfigDocument config, string apiKey, IReadOnlyList<AiMessage> messages,
        JsonObject? responseFormat, string cacheKey, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await client.CompleteAsync(config, apiKey, messages, responseFormat, null, ct);
            }
            catch (AiProviderException e) when (
                e.StatusCode == 400 && responseFormat is not null
                && e.Message.Contains("response_format", StringComparison.OrdinalIgnoreCase))
            {
                // Provider/model without structured-output support: remember and go plain.
                PlainModeCache[cacheKey] = true;
                responseFormat = null;
            }
            catch (AiProviderException e) when (e.IsRetryable && attempt == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static ExtractionAttempt Failure(
        string error, string? raw, int inputTokens, int outputTokens, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return new ExtractionAttempt(false, null, error, raw, inputTokens, outputTokens,
            (int)stopwatch.ElapsedMilliseconds);
    }
}
