using System.Text.Json.Nodes;
using Structura.Web.Domain;

namespace Structura.Web.Infrastructure.Ai;

/// <summary>
/// Builds the OpenAI structured-output JSON Schema for a project schema snapshot.
/// Strict mode rules: every property listed in `required`; optionality expressed
/// through nullable types; additionalProperties always false.
/// </summary>
public static class ExtractionSchemaBuilder
{
    public static JsonObject BuildResponseFormat(SchemaDocument schema) => new()
    {
        ["type"] = "json_schema",
        ["json_schema"] = new JsonObject
        {
            ["name"] = "extraction",
            ["strict"] = true,
            ["schema"] = BuildSchema(schema),
        },
    };

    public static JsonObject BuildSchema(SchemaDocument schema)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var field in schema.Fields.OrderBy(f => f.DisplayOrder))
        {
            properties[field.Key] = BuildProperty(field);
            required.Add(field.Key);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject BuildProperty(FieldSpec field)
    {
        var property = field.Type switch
        {
            FieldTypes.Integer => Typed("integer", field.Required),
            FieldTypes.Decimal => Typed("number", field.Required),
            FieldTypes.Boolean => Typed("boolean", field.Required),
            FieldTypes.Date => Typed("string", field.Required),
            FieldTypes.SingleSelect => SingleSelect(field),
            FieldTypes.MultiSelect => MultiSelect(field),
            _ => Typed("string", field.Required),
        };

        var description = string.Join(" — ", new[]
        {
            field.Label,
            field.Description,
            field.ExtractionInstruction,
            field.Type == FieldTypes.Date ? "ISO 8601 date (YYYY-MM-DD)" : null,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (description.Length > 0) property["description"] = description;

        return property;
    }

    private static JsonObject Typed(string type, bool required) => new()
    {
        ["type"] = required ? type : new JsonArray(type, "null"),
    };

    private static JsonObject SingleSelect(FieldSpec field)
    {
        var values = new JsonArray((field.AllowedValues ?? []).Select(v => (JsonNode?)v).ToArray());
        if (!field.Required) values.Add((string?)null);
        var property = Typed("string", field.Required);
        property["enum"] = values;
        return property;
    }

    private static JsonObject MultiSelect(FieldSpec field)
    {
        var property = Typed("array", field.Required);
        property["items"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray((field.AllowedValues ?? []).Select(v => (JsonNode?)v).ToArray()),
        };
        return property;
    }
}
