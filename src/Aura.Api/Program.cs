using System.Diagnostics;
using System.Text;
using System.Threading.RateLimiting;
using Aura.Api.Middleware;
using Aura.Api.Services;
using Aura.Core.DTOs;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
// PreflightValidationService registered in DI below
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Configuration from environment variables
var pgHost = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
var pgPort = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
var pgDb = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "aura";
var pgUser = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "aura";
var pgPass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "changeme";
var connectionString = $"Host={pgHost};Port={pgPort};Database={pgDb};Username={pgUser};Password={pgPass}";

var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? throw new InvalidOperationException("JWT_SECRET is required");
var jwtIssuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "aura-platform";
var jwtAudience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "aura-platform";

var allowedOrigins = Environment.GetEnvironmentVariable("CORS_ORIGINS")?.Split(',', StringSplitOptions.RemoveEmptyEntries)
    ?? [];

// EF Core + PostgreSQL
builder.Services.AddDbContext<AuraDbContext>((sp, options) =>
{
    options.UseNpgsql(connectionString);
});

// JWT Authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
        };
    });
builder.Services.AddAuthorization();

// H6: CORS — require explicit origins, no AllowAnyOrigin fallback
if (allowedOrigins.Length == 0)
    Console.WriteLine("WARNING: CORS_ORIGINS not set — CORS will reject all cross-origin requests.");

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
            policy.WithOrigins(allowedOrigins);

        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

// Rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        var error = new ErrorResponse("rate_limited", "Too many requests. Try again later.", 429);
        await context.HttpContext.Response.WriteAsJsonAsync(error, ct);
    };

    options.AddPolicy("global", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));

    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1)
            }));

    // M2: Strict rate limit for deploy webhook
    options.AddPolicy("deploy", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromHours(1)
            }));
});

// DI
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddScoped<IAuditService, AuditService>();

var encryptionKey = Environment.GetEnvironmentVariable("ENCRYPTION_KEY")
    ?? throw new InvalidOperationException("ENCRYPTION_KEY is required");
builder.Services.AddSingleton<ICryptoService>(new AesCryptoService(encryptionKey));
builder.Services.AddScoped<IDeploymentOrchestrationService, DeploymentOrchestrationService>();
builder.Services.AddScoped<Aura.Api.Services.PreflightValidationService>();
builder.Services.AddScoped<IExperimentService, ExperimentService>();
builder.Services.AddScoped<UserAiKeyService>();

// LLM providers for AI essence generation
builder.Services.AddHttpClient("llm");
builder.Services.AddSingleton<ILlmProviderFactory>(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var providers = new ILlmProvider[]
    {
        new OpenAiLlmProvider(httpFactory.CreateClient("llm")),
        new AnthropicLlmProvider(httpFactory.CreateClient("llm"))
    };
    return new LlmProviderFactory(providers);
});
builder.Services.AddScoped<AiEssenceBuilderService>();

// H5: Redis with optional authentication
var redisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "localhost";
var redisPort = Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379";
var redisPassword = Environment.GetEnvironmentVariable("REDIS_PASSWORD") ?? "";
var redisConnStr = string.IsNullOrEmpty(redisPassword)
    ? $"{redisHost}:{redisPort},abortConnect=false"
    : $"{redisHost}:{redisPort},password={redisPassword},abortConnect=false";
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnStr));
builder.Services.AddSingleton<ILogStreamService, RedisLogStreamService>();

// OpenTelemetry tracing
var otelEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") ?? "http://localhost:4317";
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault()
            .AddService("aura-api", serviceVersion: "1.0.0"))
        .AddAspNetCoreInstrumentation(opts =>
        {
            opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/metrics")
                               && !ctx.Request.Path.StartsWithSegments("/health");
        })
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddOtlpExporter(opts =>
        {
            opts.Endpoint = new Uri(otelEndpoint);
        }));

// Correlate logs with trace/span IDs
builder.Logging.Configure(opts => opts.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddRazorPages();

var app = builder.Build();

// Middleware pipeline
app.UseMiddleware<ExceptionHandlerMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseRateLimiter();
app.UseCors();

// H7: Security headers
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none';";
    await next();
});

app.UseStatusCodePagesWithReExecute("/error/{0}");

// Health endpoint
app.MapGet("/health", () => Results.Ok(new { Status = "healthy", Timestamp = DateTime.UtcNow }));
app.MapMethods("/health", new[] { "HEAD" }, () => Results.Ok());

// Internal deploy webhook — triggers git pull + rebuild on the VPS
var deploySecret = Environment.GetEnvironmentVariable("DEPLOY_WEBHOOK_SECRET");
app.MapPost("/api/internal/deploy", (HttpContext ctx) =>
{
    if (string.IsNullOrEmpty(deploySecret))
        return Results.NotFound();

    var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
    if (authHeader is null || !authHeader.StartsWith("Bearer ") || authHeader[7..] != deploySecret)
        return Results.Unauthorized();

    // Fire-and-forget: run deploy script in background
    _ = Task.Run(() =>
    {
        try
        {
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "deploy.sh");
            // Fallback: look in home directory
            if (!File.Exists(scriptPath))
                scriptPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "aura-platform", "scripts", "deploy.sh");

            if (File.Exists(scriptPath))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("bash", scriptPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch { /* best-effort */ }
    });

    return Results.Ok(new { Status = "deploy_triggered", Timestamp = DateTime.UtcNow });
}).RequireRateLimiting("deploy");

// Prometheus metrics endpoint
app.UseHttpMetrics();
app.MapMetrics("/metrics");

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers().RequireRateLimiting("global");
app.MapRazorPages().RequireRateLimiting("global");
app.MapGet("/", () => Results.Redirect("/dashboard"));

// Auto-migrate database on startup (safe for single-instance deployments)
if (Environment.GetEnvironmentVariable("AUTO_MIGRATE") == "true")
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AuraDbContext>();
    var retries = 5;
    for (int i = 0; i < retries; i++)
    {
        try
        {
            Console.WriteLine($"Migration attempt {i + 1}...");
            db.Database.Migrate();
            Console.WriteLine("Database migration completed successfully.");
            break;
        }
        catch (Exception ex) when (i < retries - 1)
        {
            Console.WriteLine($"Migration attempt {i + 1} failed: {ex.Message}. Retrying in 3s...");
            Thread.Sleep(3000);
        }
    }
}

app.Run();
