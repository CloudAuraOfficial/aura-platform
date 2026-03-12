using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Aura.Worker.Executors;
using Aura.Worker.Services;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureServices((context, services) =>
{
    var pgHost = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
    var pgPort = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
    var pgDb = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "aura";
    var pgUser = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "aura";
    var pgPass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "changeme";
    var connectionString = $"Host={pgHost};Port={pgPort};Database={pgDb};Username={pgUser};Password={pgPass}";

    services.AddDbContext<AuraDbContext>((sp, options) =>
    {
        options.UseNpgsql(connectionString);
    });

    // Crypto
    var encryptionKey = Environment.GetEnvironmentVariable("ENCRYPTION_KEY")
        ?? throw new InvalidOperationException("ENCRYPTION_KEY is required");
    services.AddSingleton<ICryptoService>(new AesCryptoService(encryptionKey));

    // Webhook
    services.AddHttpClient<WebhookService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    });

    // Executors
    services.AddTransient<PowerShellExecutor>();
    services.AddTransient<PythonExecutor>();
    services.AddTransient<CSharpSdkExecutor>();

    // Redis + log streaming
    var redisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "localhost";
    var redisPort = Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379";
    services.AddSingleton<IConnectionMultiplexer>(
        ConnectionMultiplexer.Connect($"{redisHost}:{redisPort},abortConnect=false"));
    services.AddSingleton<ILogStreamService, RedisLogStreamService>();

    // Orchestration (used by scheduler to create runs)
    services.AddScoped<IDeploymentOrchestrationService, DeploymentOrchestrationService>();

    // Tenant context (worker uses unscoped / IgnoreQueryFilters)
    services.AddSingleton<ITenantContext, WorkerTenantContext>();

    // Background services
    services.AddHostedService<RunWorkerService>();
    services.AddHostedService<DeploymentSchedulerService>();
});

var host = builder.Build();
await host.RunAsync();
