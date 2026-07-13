using System.Text.Json;
using FluentValidation;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Ai;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Secrets;
using Structura.Web.Infrastructure.Validation;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Schema;

public sealed record UpdateSchemaRequest(List<FieldSpec> Fields);
public sealed record GenerateSchemaRequest(string Description, string? SampleText);

public sealed class GenerateSchemaRequestValidator : AbstractValidator<GenerateSchemaRequest>
{
    public GenerateSchemaRequestValidator()
    {
        RuleFor(x => x.Description).NotEmpty().WithMessage("Describe what you want to extract.")
            .MaximumLength(4000);
        RuleFor(x => x.SampleText).MaximumLength(20_000);
    }
}

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
        group.MapPost("/generate", GenerateAsync).Validate<GenerateSchemaRequest>();
    }

    private static async Task<object> GenerateAsync(
        Guid projectId, GenerateSchemaRequest request, ProjectAccessService access, AppDbContext db,
        ISecretProtector secrets, SchemaGenerator generator, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);

        var config = AiConfigDocument.ParseOrNull(project.AiConfig);
        if (config?.ApiKeyProtected is null || string.IsNullOrWhiteSpace(config.Model))
            throw new ConflictException("configuration_incomplete",
                "Configure the AI provider (API key and model) in AI Settings before generating a schema.");

        try
        {
            var generated = await generator.GenerateAsync(
                config, secrets.Unprotect(config.ApiKeyProtected),
                request.Description, request.SampleText, ct);
            return new
            {
                fields = generated.Fields,
                systemInstruction = generated.SystemInstruction,
                extractionInstruction = generated.ExtractionInstruction,
            };
        }
        catch (AiProviderException e)
        {
            throw new AppException(StatusCodes.Status502BadGateway, "provider_error", e.Message);
        }
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
