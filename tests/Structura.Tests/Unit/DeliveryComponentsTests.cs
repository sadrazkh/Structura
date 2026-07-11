using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Structura.Web.Infrastructure.Delivery;
using Structura.Web.Infrastructure.Export;
using Xunit;

namespace Structura.Tests.Unit;

public class DeliveryComponentsTests
{
    private static BodyTemplateRenderer.Context Ctx(JsonObject? output = null) => new(
        RecordId: Guid.Parse("019f50b2-2dea-7a0b-82f9-83e6fb6ccad7"),
        ExternalId: "C-001",
        Output: output ?? new JsonObject { ["category"] = "Theft", ["count"] = 3, ["urgent"] = true },
        ReviewerName: "Sara Ahmadi",
        ReviewerEmail: "sara@example.com",
        ApprovedAt: new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero));

    // ---------- template rendering ----------

    [Fact]
    public void Double_brace_emits_escaped_string_triple_brace_emits_raw_json()
    {
        var template = """{"id":"{{record.externalId}}","category":"{{output.category}}","count":{{{output.count}}},"urgent":{{{output.urgent}}}}""";
        var rendered = BodyTemplateRenderer.Render(template, Ctx());

        // Must be valid JSON with the right value kinds.
        using var doc = JsonDocument.Parse(rendered);
        doc.RootElement.GetProperty("id").GetString().Should().Be("C-001");
        doc.RootElement.GetProperty("category").GetString().Should().Be("Theft");
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("urgent").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Review_tokens_resolve()
    {
        var template = """{"reviewer":"{{review.reviewer}}","email":"{{review.reviewerEmail}}","approved":{{{review.isApproved}}}}""";
        using var doc = JsonDocument.Parse(BodyTemplateRenderer.Render(template, Ctx()));
        doc.RootElement.GetProperty("reviewer").GetString().Should().Be("Sara Ahmadi");
        doc.RootElement.GetProperty("email").GetString().Should().Be("sara@example.com");
        doc.RootElement.GetProperty("approved").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void String_values_with_quotes_and_newlines_stay_valid_json()
    {
        var output = new JsonObject { ["note"] = "He said \"hi\"\nnew line\tand tab" };
        var rendered = BodyTemplateRenderer.Render("""{"note":"{{output.note}}"}""", Ctx(output));
        using var doc = JsonDocument.Parse(rendered); // must not throw
        doc.RootElement.GetProperty("note").GetString().Should().Be("He said \"hi\"\nnew line\tand tab");
    }

    [Fact]
    public void Injection_attempt_in_value_cannot_break_out_of_the_string()
    {
        // A value that tries to inject JSON structure must be neutralized by escaping.
        var output = new JsonObject { ["category"] = "\",\"admin\":true,\"x\":\"" };
        var rendered = BodyTemplateRenderer.Render(
            """{"category":"{{output.category}}"}""", Ctx(output));
        using var doc = JsonDocument.Parse(rendered);
        doc.RootElement.EnumerateObject().Should().ContainSingle();
        doc.RootElement.GetProperty("category").GetString().Should().Be("\",\"admin\":true,\"x\":\"");
        doc.RootElement.TryGetProperty("admin", out _).Should().BeFalse("the injected key must not appear");
    }

    [Fact]
    public void Missing_optional_output_renders_as_null()
    {
        var rendered = BodyTemplateRenderer.Render("""{"maybe":{{{output.missing}}}}""", Ctx());
        using var doc = JsonDocument.Parse(rendered);
        doc.RootElement.GetProperty("maybe").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---------- template validation ----------

    [Fact]
    public void Validation_flags_unknown_tokens_only()
    {
        var keys = new[] { "category", "count" };
        BodyTemplateRenderer.Validate("""{"c":"{{output.category}}","id":"{{record.id}}"}""", keys)
            .Should().BeEmpty();
        BodyTemplateRenderer.Validate("""{"x":"{{output.nope}}","y":"{{bogus.token}}"}""", keys)
            .Should().HaveCount(2);
    }

    // ---------- excel sanitization ----------

    [Theory]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("+1234567890", "'+1234567890")]
    [InlineData("-5", "'-5")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tTabbed", "'\tTabbed")]
    [InlineData("normal text", "normal text")]
    [InlineData("در تاریخ ۱۲ مرداد", "در تاریخ ۱۲ مرداد")]
    [InlineData("", "")]
    public void Formula_injection_is_neutralized(string input, string expected) =>
        ExcelExporter.Sanitize(input).Should().Be(expected);
}
