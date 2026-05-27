using System.Diagnostics;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Aura.Worker.Executors;
using Aura.Worker.Operations;
using Aura.Worker.Operations.Azure;
using Aura.Worker.Operations.Aws;
using Aura.Worker.Operations.Gcp;
using Aura.Worker.Operations.Common;
using Aura.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using StackExchange.Redis;

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureLogging(logging =>
{
    logging.Configure(opts => opts.ActivityTrackingOptions =
        ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
});

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
    services.AddTransient<DeleteResourceGroupHandler>();

    // AWS handlers (Epic 1)
    services.AddTransient<CreateVpcHandler>();
    services.AddTransient<DeleteVpcHandler>();
    services.AddTransient<CreateEc2InstanceHandler>();
    services.AddTransient<StartEc2InstanceHandler>();
    services.AddTransient<StopEc2InstanceHandler>();
    services.AddTransient<TerminateEc2InstanceHandler>();
    services.AddTransient<CreateS3BucketHandler>();
    services.AddTransient<DeleteS3BucketHandler>();
    services.AddTransient<RunEcsTaskHandler>();

    // GCP handlers (Epic 2)
    services.AddTransient<CreateNetworkHandler>();
    services.AddTransient<DeleteNetworkHandler>();
    services.AddTransient<CreateGceInstanceHandler>();
    services.AddTransient<StartGceInstanceHandler>();
    services.AddTransient<StopGceInstanceHandler>();
    services.AddTransient<DeleteGceInstanceHandler>();
    services.AddTransient<CreateGcsBucketHandler>();
    services.AddTransient<DeleteGcsBucketHandler>();
    services.AddTransient<CreateFirewallRuleHandler>();
    services.AddTransient<DeployCloudRunServiceHandler>();
    services.AddTransient<CreateServiceAccountHandler>();
    services.AddTransient<DeployCloudFormationHandler>();
    services.AddTransient<CreateIamRoleHandler>();

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
    registry.Register<DeleteResourceGroupHandler>("DeleteResourceGroup");
    registry.Register<CreateVpcHandler>("CreateVpc");
    registry.Register<DeleteVpcHandler>("DeleteVpc");
    registry.Register<CreateEc2InstanceHandler>("CreateEc2Instance");
    registry.Register<StartEc2InstanceHandler>("StartEc2Instance");
    registry.Register<StopEc2InstanceHandler>("StopEc2Instance");
    registry.Register<TerminateEc2InstanceHandler>("TerminateEc2Instance");
    registry.Register<CreateS3BucketHandler>("CreateS3Bucket");
    registry.Register<DeleteS3BucketHandler>("DeleteS3Bucket");
    registry.Register<RunEcsTaskHandler>("RunEcsTask");
    registry.Register<DeployCloudFormationHandler>("DeployCloudFormation");
    registry.Register<CreateIamRoleHandler>("CreateIamRole");
    registry.Register<CreateNetworkHandler>("CreateNetwork");
    registry.Register<DeleteNetworkHandler>("DeleteNetwork");
    registry.Register<CreateGceInstanceHandler>("CreateGceInstance");
    registry.Register<StartGceInstanceHandler>("StartGceInstance");
    registry.Register<StopGceInstanceHandler>("StopGceInstance");
    registry.Register<DeleteGceInstanceHandler>("DeleteGceInstance");
    registry.Register<CreateGcsBucketHandler>("CreateGcsBucket");
    registry.Register<DeleteGcsBucketHandler>("DeleteGcsBucket");
    registry.Register<CreateFirewallRuleHandler>("CreateFirewallRule");
    registry.Register<DeployCloudRunServiceHandler>("DeployCloudRunService");
    registry.Register<CreateServiceAccountHandler>("CreateServiceAccount");
    services.AddSingleton(registry);

    // Execution mode strategy (with in-process handler awareness)
    services.AddSingleton<IExecutionModeStrategy>(sp =>
        new ExecutionModeStrategy(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<ExecutionModeStrategy>>(),
            operationType => registry.HasHandler(operationType)));

    // OpenTelemetry tracing
    var otelEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") ?? "http://localhost:4317";
    services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService("aura-worker", serviceVersion: "1.0.0"))
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddSource("Aura.Worker")
            .AddSource("Aura.Worker.Operations")
            .AddOtlpExporter(opts =>
            {
                opts.Endpoint = new Uri(otelEndpoint);
            }));

    // H5: Redis with optional authentication
    var redisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "localhost";
    var redisPort = Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379";
    var redisPassword = Environment.GetEnvironmentVariable("REDIS_PASSWORD") ?? "";
    var redisConnStr = string.IsNullOrEmpty(redisPassword)
        ? $"{redisHost}:{redisPort},abortConnect=false"
        : $"{redisHost}:{redisPort},password={redisPassword},abortConnect=false";
    services.AddSingleton<IConnectionMultiplexer>(
        ConnectionMultiplexer.Connect(redisConnStr));
    services.AddSingleton<ILogStreamService, RedisLogStreamService>();

    // Cloud cost estimators — factory selects per run's CloudProvider
    services.AddSingleton<ICloudCostEstimator, AzureCostEstimator>();
    services.AddSingleton<ICloudCostEstimator, AwsCostEstimator>();
    services.AddSingleton<ICloudCostEstimator, GcpCostEstimator>();
    services.AddSingleton<ICloudCostEstimatorFactory, CloudCostEstimatorFactory>();

    // Orchestration (used by scheduler to create runs)
    services.AddScoped<IDeploymentOrchestrationService, DeploymentOrchestrationService>();
    services.AddScoped<IExperimentService, ExperimentService>();

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
