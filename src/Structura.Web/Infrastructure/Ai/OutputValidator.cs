using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Structura.Web.Domain;

namespace Structura.Web.Infrastructure.Ai;

public sealed record ValidationOutcome(JsonObject NormalizedOutput, List<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates and normalizes an AI output object against a schema snapshot (docs/05):
/// safe type coercion, required checks, allowed-value checks; unknown keys stripped.
/// </summary>
public static class OutputValidator
{
    public static ValidationOutcome Validate(SchemaDocument schema, JsonObject output)
    {
        var normalized = new JsonObject();
        var errors = new List<string>();

        foreach (var field in schema.Fields.OrderBy(f => f.DisplayOrder))
        {
            output.TryGetPropertyValue(field.Key, out var raw);
            var (value, error) = Normalize(field, raw);
            if (error is not null)
            {
                errors.Add($"{field.Key}: {error}");
                continue;
            }
            if (value is null && field.Required)
            {
                errors.Add($"{field.Key}: required value is missing.");
                continue;
            }
            normalized[field.Key] = value;
        }

        return new ValidationOutcome(normalized, errors);
    }

    private static (JsonNode? Value, string? Error) Normalize(FieldSpec field, JsonNode? raw)
    {
        if (raw is null || raw.GetValueKind() == JsonValueKind.Null) return (null, null);

        switch (field.Type)
        {
            case FieldTypes.ShortText:
            case FieldTypes.LongText:
            {
                var text = AsScalarString(raw);
                if (text is null) return (null, "expected a text value.");
                return text.Length == 0 ? (null, null) : (JsonValue.Create(text), null);
            }
            case FieldTypes.Integer:
            {
                var number = AsNumber(raw);
                if (number is null) return (null, "expected an integer.");
                if (number != decimal.Truncate(number.Value)) return (null, "expected a whole number.");
                return (JsonValue.Create((long)number.Value), null);
            }
            case FieldTypes.Decimal:
            {
                var number = AsNumber(raw);
                return number is null ? (null, "expected a number.") : (JsonValue.Create(number.Value), null);
            }
            case FieldTypes.Boolean:
            {
                var kind = raw.GetValueKind();
                if (kind == JsonValueKind.True) return (JsonValue.Create(true), null);
                if (kind == JsonValueKind.False) return (JsonValue.Create(false), null);
                var text = AsScalarString(raw)?.ToLowerInvariant();
                return text switch
                {
                    "true" or "yes" => (JsonValue.Create(true), null),
                    "false" or "no" => (JsonValue.Create(false), null),
                    _ => (null, "expected true or false."),
                };
            }
            case FieldTypes.Date:
            {
                var text = AsScalarString(raw);
                if (string.IsNullOrEmpty(text)) return (null, null);
                if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var date))
                    return (JsonValue.Create(date.ToString("yyyy-MM-dd")), null);
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime))
                    return (JsonValue.Create(dateTime.ToString("yyyy-MM-dd")), null);
                return (null, $"'{Excerpt(text)}' is not a valid ISO date.");
            }
            case FieldTypes.SingleSelect:
            {
                var text = AsScalarString(raw);
                if (string.IsNullOrEmpty(text)) return (null, null);
                var match = MatchAllowed(field, text);
                return match is null
                    ? (null, $"'{Excerpt(text)}' is not one of the allowed values ({string.Join(", ", field.AllowedValues ?? [])}).")
                    : (JsonValue.Create(match), null);
            }
            case FieldTypes.MultiSelect:
            {
                // Coercion: a bare string becomes a one-item array.
                var items = raw is JsonArray array
                    ? array.Select(AsScalarStringOrNull).ToList()
                    : [AsScalarString(raw)];
                var resolved = new JsonArray();
                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(item)) continue;
                    var match = MatchAllowed(field, item);
                    if (match is null)
                        return (null, $"'{Excerpt(item)}' is not one of the allowed values ({string.Join(", ", field.AllowedValues ?? [])}).");
                    resolved.Add(match);
                }
                return resolved.Count == 0 ? (null, null) : (resolved, null);
            }
            default:
                return (null, $"unsupported field type '{field.Type}'.");
        }
    }

    /// <summary>Case-insensitive match returning the canonical allowed value.</summary>
    private static string? MatchAllowed(FieldSpec field, string value) =>
        (field.AllowedValues ?? [])
        .FirstOrDefault(allowed => string.Equals(allowed, value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static decimal? AsNumber(JsonNode raw)
    {
        var kind = raw.GetValueKind();
        var text = kind switch
        {
            JsonValueKind.Number => raw.ToJsonString(),
            JsonValueKind.String => raw.ToString().Trim(),
            _ => null,
        };
        return text is not null
               && decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? AsScalarString(JsonNode raw) =>
        raw.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            ? raw.ToString().Trim()
            : null;

    private static string? AsScalarStringOrNull(JsonNode? raw) => raw is null ? null : AsScalarString(raw);

    private static string Excerpt(string value) => value.Length > 60 ? value[..60] + "…" : value;
}
