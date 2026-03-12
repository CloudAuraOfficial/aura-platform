using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Worker.Services;
using Microsoft.EntityFrameworkCore;

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

    services.AddSingleton<ITenantContext, WorkerTenantContext>();
    services.AddHostedService<DeploymentSchedulerService>();
});

var host = builder.Build();
await host.RunAsync();
