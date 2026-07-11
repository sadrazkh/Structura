using System.Text.Json;
using System.Text.Json.Nodes;

namespace Structura.Web.Infrastructure.Ai;

/// <summary>
/// Parses model output into a JSON object with one deterministic repair pass
/// (docs/05: strip markdown fences / cut to the outermost braces).
/// </summary>
public static class JsonOutputParser
{
    public static bool TryParse(string? content, out JsonObject result)
    {
        result = new JsonObject();
        if (string.IsNullOrWhiteSpace(content)) return false;

        if (TryParseObject(content, out result)) return true;
        var repaired = Repair(content);
        return repaired is not null && TryParseObject(repaired, out result);
    }

    private static bool TryParseObject(string text, out JsonObject result)
    {
        result = new JsonObject();
        try
        {
            if (JsonNode.Parse(text) is JsonObject obj)
            {
                result = obj;
                return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Cut the string down to the outermost {...} block (drops fences/prose).</summary>
    internal static string? Repair(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return content[start..(end + 1)];
    }
}
