using FluentValidation;

namespace Structura.Web.Infrastructure.Validation;

/// <summary>Endpoint filter that runs the registered FluentValidation validator for TRequest.</summary>
public sealed class ValidationFilter<TRequest> : IEndpointFilter where TRequest : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        if (request is not null)
        {
            var validator = context.HttpContext.RequestServices.GetService<IValidator<TRequest>>();
            if (validator is not null)
            {
                var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);
                if (!result.IsValid) throw new ValidationException(result.Errors);
            }
        }
        return await next(context);
    }
}

public static class ValidationFilterExtensions
{
    public static RouteHandlerBuilder Validate<TRequest>(this RouteHandlerBuilder builder) where TRequest : class
        => builder.AddEndpointFilter<ValidationFilter<TRequest>>();
}
