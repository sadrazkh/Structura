using System.Text.Json;
using FluentValidation;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Validation;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Schema;

public sealed record UpdateSchemaRequest(List<FieldSpec> Fields);

public sealed class UpdateSchemaRequestValidator : AbstractValidator<UpdateSchemaRequest>
{
    public UpdateSchemaRequestValidator()
    {
        RuleFor(x => x.Fields).NotNull();
        RuleFor(x => x).Custom((request, context) =>
        {
            foreach (var (index, message) in SchemaValidator.Validate(request.Fields))
            {
                var name = index >= 0 ? $"fields[{index}]" : "fields";
                context.AddFailure(name, message);
            }
        });
    }
}

public static class SchemaEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/schema").RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapPut("/", UpdateAsync).Validate<UpdateSchemaRequest>();
    }

    private static async Task<object> GetAsync(
        Guid projectId, ProjectAccessService access, CancellationToken ct)
    {
        var project = await access.EnsureCanViewAsync(projectId, ct);
        var document = SchemaDocument.Parse(project.SchemaFields);
        return new { version = project.SchemaVersion, fields = document.Fields.OrderBy(f => f.DisplayOrder) };
    }

    private static async Task<object> UpdateAsync(
        Guid projectId, UpdateSchemaRequest request, ProjectAccessService access,
        AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);

        var ordered = request.Fields
            .Select((field, index) => { field.DisplayOrder = index; return field; })
            .ToList();

        var newDocument = new SchemaDocument { Version = project.SchemaVersion, Fields = ordered };
        var currentFields = SchemaDocument.Parse(project.SchemaFields).Fields;

        // Only a real change bumps the version (idempotent saves stay cheap).
        var changed = JsonSerializer.Serialize(currentFields, SchemaDocument.JsonOptions)
                      != JsonSerializer.Serialize(ordered, SchemaDocument.JsonOptions);
        if (changed)
        {
            project.SchemaVersion += 1;
            newDocument.Version = project.SchemaVersion;
            project.SchemaFields = newDocument.ToJson();
            await db.SaveChangesAsync(ct);
        }

        return new { version = project.SchemaVersion, fields = ordered, changed };
    }
}
