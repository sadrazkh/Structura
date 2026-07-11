using System.Text.Json;
using System.Text.Json.Serialization;

namespace Structura.Web.Domain;

public static class AiProviders
{
    public const string OpenRouter = "OpenRouter";
    public const string Nvidia = "Nvidia";
    public static readonly string[] All = [OpenRouter, Nvidia];

    public static string DefaultBaseUrl(string provider) => provider switch
    {
        OpenRouter => "https://openrouter.ai/api/v1",
        Nvidia => "https://integrate.api.nvidia.com/v1",
        _ => "",
    };
}

/// <summary>Stored in projects.ai_config (JSONB). The API key is kept encrypted.</summary>
public sealed class AiConfigDocument
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = AiProviders.OpenRouter;
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = AiProviders.DefaultBaseUrl(AiProviders.OpenRouter);
    [JsonPropertyName("apiKeyProtected")] public string? ApiKeyProtected { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.1;
    [JsonPropertyName("maxOutputTokens")] public int MaxOutputTokens { get; set; } = 2048;
    [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; set; } = 60;
    [JsonPropertyName("concurrency")] public int Concurrency { get; set; } = 5;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AiConfigDocument? ParseOrNull(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<AiConfigDocument>(json, JsonOptions);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>Stored in projects.prompt_config (JSONB).</summary>
public sealed class PromptConfigDocument
{
    [JsonPropertyName("systemInstruction")] public string SystemInstruction { get; set; } = "";
    [JsonPropertyName("extractionInstruction")] public string ExtractionInstruction { get; set; } = "";

    public static PromptConfigDocument Parse(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new PromptConfigDocument()
            : JsonSerializer.Deserialize<PromptConfigDocument>(json, AiConfigDocument.JsonOptions)
              ?? new PromptConfigDocument();

    public string ToJson() => JsonSerializer.Serialize(this, AiConfigDocument.JsonOptions);
}
