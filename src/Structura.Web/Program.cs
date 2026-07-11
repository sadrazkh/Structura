using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
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

// ---------- Data directory, secrets, outbound HTTP ----------
var dataDir = builder.Configuration["DATA_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(Path.Combine(dataDir, "keys"));
builder.Services.AddDataProtection()
    .SetApplicationName("Structura")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
builder.Services.AddSingleton<Structura.Web.Infrastructure.Secrets.ISecretProtector,
    Structura.Web.Infrastructure.Secrets.SecretProtector>();
builder.Services.AddSingleton<Structura.Web.Infrastructure.Http.IDnsResolver,
    Structura.Web.Infrastructure.Http.SystemDnsResolver>();
builder.Services.AddSingleton(new Structura.Web.Infrastructure.Http.SafeHttpOptions
{
    AllowInsecureHttp = builder.Configuration.GetValue<bool>("ALLOW_INSECURE_HTTP"),
    AllowPrivateAiEndpoints = builder.Configuration.GetValue<bool>("ALLOW_PRIVATE_AI_ENDPOINTS"),
    AllowPrivateConnectorTargets = builder.Configuration.GetValue<bool>("ALLOW_PRIVATE_CONNECTOR_TARGETS"),
    OutboundProxyUrl = builder.Configuration["OUTBOUND_PROXY_URL"],
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 60 * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    options.MultipartBodyLengthLimit = 55 * 1024 * 1024);
builder.Services.AddSingleton<Structura.Web.Infrastructure.Http.SafeHttpClientFactory>();
builder.Services.AddSingleton<Structura.Web.Infrastructure.Ai.OpenAiCompatibleClient>();

// ---------- Background workers & realtime ----------
builder.Services.AddSignalR();
builder.Services.AddSingleton<Structura.Web.Infrastructure.Ai.ExtractionPipeline>();
builder.Services.AddHostedService<Structura.Web.Infrastructure.Import.ImportWorker>();
builder.Services.AddHostedService<Structura.Web.Infrastructure.Processing.ProcessingWorker>();

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
            // SignalR websockets cannot send Authorization headers; the token arrives as a query param.
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken)
                    && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
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
Structura.Web.Features.Schema.SchemaEndpoints.Map(app);
Structura.Web.Features.AiSettings.AiSettingsEndpoints.Map(app);
Structura.Web.Features.Imports.ImportEndpoints.Map(app);
Structura.Web.Features.Imports.ApiInputEndpoints.Map(app);
Structura.Web.Features.Records.RecordEndpoints.Map(app);
Structura.Web.Features.Processing.RunEndpoints.Map(app);
app.MapHub<Structura.Web.Infrastructure.Realtime.ProgressHub>("/hubs/progress");

// Unknown API routes must return problem+json, not the SPA shell.
app.MapFallback("/api/{**path}", () => { throw new NotFoundException("Endpoint"); });
// SPA fallback (production build output in wwwroot).
app.MapFallbackToFile("index.html");

// ---------- Startup ----------
await DatabaseInitializer.InitializeAsync(app.Services);

app.Run();

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program;
