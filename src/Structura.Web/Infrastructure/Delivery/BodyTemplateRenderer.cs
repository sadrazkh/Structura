using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Structura.Web.Infrastructure.Delivery;

/// <summary>
/// Injection-proof mustache subset for API-output body templates (docs/05).
/// Only a fixed set of tokens is allowed — no logic, loops, or expressions.
/// <c>{{token}}</c> emits the value escaped for embedding inside a JSON string literal;
/// <c>{{{token}}}</c> emits the raw JSON value (for numbers/booleans/objects/arrays/null).
/// </summary>
public static class BodyTemplateRenderer
{
    /// <summary>Everything the renderer can resolve for one delivered record.</summary>
    public sealed record Context(
        Guid RecordId,
        string ExternalId,
        JsonObject Output,
        string? ReviewerName,
        string? ReviewerEmail,
        DateTimeOffset? ApprovedAt);

    /// <summary>Validates a template at save time. Returns error messages (empty = valid).</summary>
    public static List<string> Validate(string template, IReadOnlyCollection<string> outputKeys)
    {
        var errors = new List<string>();
        foreach (var (token, _) in EnumerateTokens(template))
        {
            if (!IsKnownToken(token, outputKeys))
                errors.Add($"Unknown placeholder '{{{{{token}}}}}'.");
        }
        return errors;
    }

    public static string Render(string template, Context context)
    {
        var result = new StringBuilder(template.Length + 64);
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{' && Peek(template, i, '{', '{', '{'))
            {
                var end = template.IndexOf("}}}", i + 3, StringComparison.Ordinal);
                if (end > 0)
                {
                    var token = template[(i + 3)..end].Trim();
                    result.Append(RawJson(Resolve(token, context)));
                    i = end + 3;
                    continue;
                }
            }
            if (template[i] == '{' && Peek(template, i, '{', '{'))
            {
                var end = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    var token = template[(i + 2)..end].Trim();
                    result.Append(EscapedString(Resolve(token, context)));
                    i = end + 2;
                    continue;
                }
            }
            result.Append(template[i]);
            i++;
        }
        return result.ToString();
    }

    private static bool Peek(string s, int i, params char[] chars) =>
        i + chars.Length <= s.Length && chars.Select((c, k) => s[i + k] == c).All(x => x);

    private static IEnumerable<(string Token, bool Raw)> EnumerateTokens(string template)
    {
        var i = 0;
        while (i < template.Length)
        {
            if (Peek(template, i, '{', '{', '{'))
            {
                var end = template.IndexOf("}}}", i + 3, StringComparison.Ordinal);
                if (end > 0) { yield return (template[(i + 3)..end].Trim(), true); i = end + 3; continue; }
            }
            if (Peek(template, i, '{', '{'))
            {
                var end = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end > 0) { yield return (template[(i + 2)..end].Trim(), false); i = end + 2; continue; }
            }
            i++;
        }
    }

    private static bool IsKnownToken(string token, IReadOnlyCollection<string> outputKeys)
    {
        if (token is "record.id" or "record.externalId"
            or "review.reviewer" or "review.reviewerEmail" or "review.approvedAt" or "review.isApproved")
            return true;
        if (token.StartsWith("output.", StringComparison.Ordinal))
            return outputKeys.Contains(token["output.".Length..]);
        return false;
    }

    private static JsonNode? Resolve(string token, Context ctx) => token switch
    {
        "record.id" => ctx.RecordId.ToString(),
        "record.externalId" => ctx.ExternalId,
        "review.reviewer" => ctx.ReviewerName,
        "review.reviewerEmail" => ctx.ReviewerEmail,
        "review.approvedAt" => ctx.ApprovedAt?.ToString("o"),
        "review.isApproved" => JsonValue.Create(true),
        _ when token.StartsWith("output.", StringComparison.Ordinal) =>
            ctx.Output.TryGetPropertyValue(token["output.".Length..], out var v) ? v?.DeepClone() : null,
        _ => throw new InvalidOperationException($"Unknown placeholder '{token}'."),
    };

    /// <summary>Value as text, then JSON-escaped for embedding inside a "..." literal (no quotes).</summary>
    private static string EscapedString(JsonNode? node)
    {
        var text = node is null ? "" : node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
        // JsonEncodedText produces the escaped inner content of a JSON string.
        var encoded = JsonEncodedText.Encode(text);
        return encoded.ToString();
    }

    private static string RawJson(JsonNode? node) => node?.ToJsonString() ?? "null";
}
