using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Structura.Web.Domain;
using Structura.Web.Features.Auth;
using Structura.Web.Features.Projects;
using Structura.Web.Features.Users;
using Structura.Web.Infrastructure;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Persistence;

var builder = WebApplication.CreateBuilder(args);

// ---------- Logging ----------
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext());

// ---------- Configuration ----------
var connectionString =
    builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings__Default is not configured.");

var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
builder.Services.Configure<JwtOptions>(jwtSection);
// Env var JWT_SIGNING_KEY overrides the config section (docs/.env convention).
var signingKey = builder.Configuration["JWT_SIGNING_KEY"] ?? jwtSection["SigningKey"] ?? "";
builder.Services.PostConfigure<JwtOptions>(o => o.SigningKey = signingKey);

// ---------- Persistence ----------
builder.Services.AddSingleton<TimestampInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, options) => options
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

// ---------- Auth ----------
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<SecurityStampCache>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<ProjectAccessService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep raw claim names (sub, email, name)
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwtSection["Issuer"] ?? "structura",
            ValidAudience = jwtSection["Audience"] ?? "structura",
            IssuerSigningKey = JwtTokenService.CreateKey(signingKey),
            ClockSkew = TimeSpan.FromMinutes(2),
            RoleClaimType = AppClaims.Role,
            NameClaimType = JwtRegisteredClaimNames.Name,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var sub = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                var stamp = context.Principal?.FindFirst(AppClaims.SecurityStamp)?.Value;
                if (sub is null || stamp is null || !Guid.TryParse(sub, out var userId))
                {
                    context.Fail("Invalid token claims.");
                    return;
                }
                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var stampCache = context.HttpContext.RequestServices.GetRequiredService<SecurityStampCache>();
                if (!await stampCache.IsValidAsync(db, userId, stamp, context.HttpContext.RequestAborted))
                    context.Fail("Session is no longer valid.");
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Administrator", policy => policy.RequireRole(UserRole.Administrator));
});

// ---------- Rate limiting ----------
var loginPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:LoginPermitLimit") ?? 5;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = loginPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// ---------- Validation & JSON ----------
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

// ---------- Pipeline ----------
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseSerilogRequestLogging();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<GateMiddleware>();

// ---------- Endpoints ----------
app.MapGet("/api/health", async (AppDbContext db, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    return canConnect
        ? Results.Ok(new { status = "healthy" })
        : Results.Json(new { status = "unhealthy", reason = "database" }, statusCode: 503);
}).AllowAnonymous();

AuthEndpoints.Map(app);
UserEndpoints.Map(app);
ProjectEndpoints.Map(app);

// Unknown API routes must return problem+json, not the SPA shell.
app.MapFallback("/api/{**path}", () => { throw new NotFoundException("Endpoint"); });
// SPA fallback (production build output in wwwroot).
app.MapFallbackToFile("index.html");

// ---------- Startup ----------
await DatabaseInitializer.InitializeAsync(app.Services);

app.Run();

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program;
