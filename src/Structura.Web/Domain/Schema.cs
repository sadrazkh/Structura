using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Structura.Web.Domain;

public static class FieldTypes
{
    public const string ShortText = "shortText";
    public const string LongText = "longText";
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string SingleSelect = "singleSelect";
    public const string MultiSelect = "multiSelect";

    public static readonly string[] All =
        [ShortText, LongText, Integer, Decimal, Boolean, Date, SingleSelect, MultiSelect];

    public static bool IsSelect(string type) => type is SingleSelect or MultiSelect;
}

public sealed class FieldSpec
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = FieldTypes.ShortText;
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("extractionInstruction")] public string? ExtractionInstruction { get; set; }
    [JsonPropertyName("allowedValues")] public List<string>? AllowedValues { get; set; }
    [JsonPropertyName("defaultValue")] public JsonNode? DefaultValue { get; set; }
    [JsonPropertyName("displayOrder")] public int DisplayOrder { get; set; }
}

public sealed class SchemaDocument
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("fields")] public List<FieldSpec> Fields { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SchemaDocument Parse(string json) =>
        JsonSerializer.Deserialize<SchemaDocument>(json, JsonOptions) ?? new SchemaDocument();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>Validates an admin-defined schema document (docs/03, V1 field set).</summary>
public static partial class SchemaValidator
{
    public const int MaxFields = 100;
    public const int MaxAllowedValues = 100;

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,63}$")]
    private static partial Regex KeyRegex();

    /// <summary>Returns (fieldIndex, message) pairs; empty list = valid.</summary>
    public static List<(int Index, string Message)> Validate(IReadOnlyList<FieldSpec> fields)
    {
        var errors = new List<(int, string)>();
        if (fields.Count > MaxFields)
            errors.Add((-1, $"A schema can have at most {MaxFields} fields."));

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (string.IsNullOrWhiteSpace(field.Key) || !KeyRegex().IsMatch(field.Key))
                errors.Add((i, "Key must start with a lowercase letter and contain only letters and digits (max 64)."));
            else if (!seenKeys.Add(field.Key))
                errors.Add((i, $"Duplicate key '{field.Key}'."));

            if (string.IsNullOrWhiteSpace(field.Label))
                errors.Add((i, "Label is required."));
            if (field.Label?.Length > 200)
                errors.Add((i, "Label must be at most 200 characters."));

            if (!FieldTypes.All.Contains(field.Type))
                errors.Add((i, $"Unknown field type '{field.Type}'."));

            if (FieldTypes.IsSelect(field.Type))
            {
                if (field.AllowedValues is null || field.AllowedValues.Count == 0)
                    errors.Add((i, "Select fields need at least one allowed value."));
                else if (field.AllowedValues.Count > MaxAllowedValues)
                    errors.Add((i, $"At most {MaxAllowedValues} allowed values."));
                else if (field.AllowedValues.Any(string.IsNullOrWhiteSpace))
                    errors.Add((i, "Allowed values cannot be empty."));
                else if (field.AllowedValues.Distinct().Count() != field.AllowedValues.Count)
                    errors.Add((i, "Allowed values must be unique."));
            }
            else if (field.AllowedValues is { Count: > 0 })
            {
                errors.Add((i, "Only select fields can have allowed values."));
            }

            if (field.DefaultValue is not null && !DefaultMatchesType(field))
                errors.Add((i, "Default value does not match the field type."));
        }
        return errors;
    }

    private static bool DefaultMatchesType(FieldSpec field)
    {
        var node = field.DefaultValue!;
        try
        {
            return field.Type switch
            {
                FieldTypes.ShortText or FieldTypes.LongText => AsString(node) is not null,
                FieldTypes.Integer => AsNumber(node) is { } n && n == decimal.Truncate(n),
                FieldTypes.Decimal => AsNumber(node) is not null,
                FieldTypes.Boolean => node.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
                FieldTypes.Date => AsString(node) is { } s && DateOnly.TryParse(s, out _),
                FieldTypes.SingleSelect => AsString(node) is { } s && field.AllowedValues?.Contains(s) == true,
                FieldTypes.MultiSelect => node is JsonArray array
                                          && array.All(item =>
                                              item is not null
                                              && AsString(item) is { } s
                                              && field.AllowedValues?.Contains(s) == true),
                _ => false,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    // Representation-agnostic accessors: JsonNodes may be backed by JsonElement (parsed
    // from HTTP) or by CLR values (built in memory); GetValue<T> only works for exact types.
    private static string? AsString(JsonNode node) =>
        node.GetValueKind() == JsonValueKind.String ? node.ToString() : null;

    private static decimal? AsNumber(JsonNode node) =>
        node.GetValueKind() == JsonValueKind.Number
        && decimal.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
