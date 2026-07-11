using System.Text.Json;
using System.Text.Json.Serialization;

namespace Structura.Web.Domain;

/// <summary>Stored in projects.api_output_config (JSONB). Credentials are encrypted.</summary>
public sealed class ApiOutputConfig
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("method")] public string Method { get; set; } = "POST";
    [JsonPropertyName("headers")] public Dictionary<string, string> Headers { get; set; } = [];
    [JsonPropertyName("authType")] public string AuthType { get; set; } = "none"; // none | bearer | apiKey
    [JsonPropertyName("tokenProtected")] public string? TokenProtected { get; set; }
    [JsonPropertyName("apiKeyHeaderName")] public string ApiKeyHeaderName { get; set; } = "X-Api-Key";
    [JsonPropertyName("bodyTemplate")] public string BodyTemplate { get; set; } = "";
    [JsonPropertyName("successStatusCodes")] public List<int> SuccessStatusCodes { get; set; } = [200, 201, 202, 204];
    /// <summary>JSONPath-lite (dot path) into the response body for an external id to record.</summary>
    [JsonPropertyName("responseIdPath")] public string? ResponseIdPath { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    public static ApiOutputConfig? ParseOrNull(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<ApiOutputConfig>(json, AiConfigDocument.JsonOptions);

    public string ToJson() => JsonSerializer.Serialize(this, AiConfigDocument.JsonOptions);
}
