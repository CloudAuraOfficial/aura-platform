using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Aura.Worker.Executors;
using Aura.Worker.Operations;
using Aura.Worker.Operations.Azure;
using Aura.Worker.Operations.Common;
using Aura.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Prometheus;
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
    services.AddTransient<OperationExecutor>();
    services.AddTransient<EmissionLoadExecutor>();

    // EmissionLoad container execution
    services.AddSingleton<IContainerExecutionService, DockerContainerExecutionService>();
    services.AddScoped<EmissionLoadResolver>();

    // Operation handlers
    services.AddTransient<CreateResourceGroupHandler>();
    services.AddTransient<CreateContainerRegistryHandler>();
    services.AddTransient<BuildContainerImageHandler>();
    services.AddTransient<PushContainerImageHandler>();
    services.AddTransient<ImportContainerImageHandler>();
    services.AddTransient<CreateContainerGroupHandler>();
    services.AddTransient<StopContainerGroupHandler>();
    services.AddTransient<DeleteContainerGroupHandler>();
    services.AddTransient<HttpHealthCheckHandler>();
    services.AddTransient<CreateVMHandler>();
    services.AddTransient<StartVMHandler>();
    services.AddTransient<StopVMHandler>();
    services.AddTransient<DeleteVMHandler>();
    services.AddTransient<DeployArmTemplateHandler>();
    services.AddHttpClient();

    // Operation registry
    var registry = new OperationRegistry();
    registry.Register<CreateResourceGroupHandler>("CreateResourceGroup");
    registry.Register<CreateContainerRegistryHandler>("CreateContainerRegistry");
    registry.Register<BuildContainerImageHandler>("BuildContainerImage");
    registry.Register<PushContainerImageHandler>("PushContainerImage");
    registry.Register<ImportContainerImageHandler>("ImportContainerImage");
    registry.Register<CreateContainerGroupHandler>("CreateContainerGroup");
    registry.Register<StopContainerGroupHandler>("StopContainerGroup");
    registry.Register<DeleteContainerGroupHandler>("DeleteContainerGroup");
    registry.Register<HttpHealthCheckHandler>("HttpHealthCheck");
    registry.Register<CreateVMHandler>("CreateVM");
    registry.Register<StartVMHandler>("StartVM");
    registry.Register<StopVMHandler>("StopVM");
    registry.Register<DeleteVMHandler>("DeleteVM");
    registry.Register<DeployArmTemplateHandler>("DeployArmTemplate");
    services.AddSingleton(registry);

    // Execution mode strategy (with in-process handler awareness)
    services.AddSingleton<IExecutionModeStrategy>(sp =>
        new ExecutionModeStrategy(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<ExecutionModeStrategy>>(),
            operationType => registry.HasHandler(operationType)));

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

// Expose Prometheus metrics on port 9091 (9090 is used by P3 Prometheus)
var metricsServer = new MetricServer(port: 9091);
metricsServer.Start();

await host.RunAsync();
