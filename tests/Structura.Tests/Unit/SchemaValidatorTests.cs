using System.Text.Json.Nodes;
using FluentAssertions;
using Structura.Web.Domain;
using Xunit;

namespace Structura.Tests.Unit;

public class SchemaValidatorTests
{
    private static FieldSpec Field(string key, string type = FieldTypes.ShortText, Action<FieldSpec>? mutate = null)
    {
        var field = new FieldSpec { Key = key, Label = key, Type = type };
        mutate?.Invoke(field);
        return field;
    }

    [Fact]
    public void Valid_schema_with_every_type_passes()
    {
        var fields = new List<FieldSpec>
        {
            Field("firstName"),
            Field("notes", FieldTypes.LongText),
            Field("age", FieldTypes.Integer, f => f.DefaultValue = JsonValue.Create(0)),
            Field("score", FieldTypes.Decimal, f => f.DefaultValue = JsonValue.Create(1.5)),
            Field("isUrgent", FieldTypes.Boolean, f => f.DefaultValue = JsonValue.Create(false)),
            Field("incidentDate", FieldTypes.Date, f => f.DefaultValue = JsonValue.Create("2026-01-01")),
            Field("category", FieldTypes.SingleSelect, f =>
            {
                f.AllowedValues = ["Theft", "Fire"];
                f.DefaultValue = JsonValue.Create("Theft");
            }),
            Field("tags", FieldTypes.MultiSelect, f =>
            {
                f.AllowedValues = ["a", "b"];
                f.DefaultValue = new JsonArray("a");
            }),
        };
        SchemaValidator.Validate(fields).Should().BeEmpty();
    }

    [Theory]
    [InlineData("FirstName")]  // must start lowercase
    [InlineData("1name")]      // must start with a letter
    [InlineData("first_name")] // no underscores
    [InlineData("first name")] // no spaces
    [InlineData("")]
    public void Invalid_keys_are_rejected(string key) =>
        SchemaValidator.Validate([Field(key)]).Should().NotBeEmpty();

    [Fact]
    public void Duplicate_keys_are_rejected() =>
        SchemaValidator.Validate([Field("name"), Field("name")])
            .Should().ContainSingle(e => e.Message.Contains("Duplicate"));

    [Fact]
    public void Select_without_allowed_values_is_rejected() =>
        SchemaValidator.Validate([Field("cat", FieldTypes.SingleSelect)])
            .Should().ContainSingle(e => e.Message.Contains("allowed value"));

    [Fact]
    public void Non_select_with_allowed_values_is_rejected() =>
        SchemaValidator.Validate([Field("name", FieldTypes.ShortText, f => f.AllowedValues = ["x"])])
            .Should().NotBeEmpty();

    [Fact]
    public void Unknown_type_is_rejected() =>
        SchemaValidator.Validate([Field("x", "nestedObject")])
            .Should().ContainSingle(e => e.Message.Contains("Unknown field type"));

    [Fact]
    public void Default_value_must_match_type()
    {
        SchemaValidator.Validate([Field("age", FieldTypes.Integer, f => f.DefaultValue = JsonValue.Create("ten"))])
            .Should().NotBeEmpty();
        SchemaValidator.Validate([Field("age", FieldTypes.Integer, f => f.DefaultValue = JsonValue.Create(1.5))])
            .Should().NotBeEmpty();
        SchemaValidator.Validate([Field("d", FieldTypes.Date, f => f.DefaultValue = JsonValue.Create("not-a-date"))])
            .Should().NotBeEmpty();
        SchemaValidator.Validate([
            Field("cat", FieldTypes.SingleSelect, f =>
            {
                f.AllowedValues = ["A"];
                f.DefaultValue = JsonValue.Create("B"); // not in allowed values
            })
        ]).Should().NotBeEmpty();
    }

    [Fact]
    public void Too_many_fields_are_rejected()
    {
        var fields = Enumerable.Range(0, SchemaValidator.MaxFields + 1)
            .Select(i => Field($"f{i}")).ToList();
        SchemaValidator.Validate(fields).Should().Contain(e => e.Message.Contains("at most"));
    }
}
