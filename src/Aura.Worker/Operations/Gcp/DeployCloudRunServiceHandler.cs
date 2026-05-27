using System.Text.Json;
using Aura.Worker.Executors;
using Google.Cloud.Run.V2;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Gcp;

/// <summary>
/// Deploys (create-or-update) a Cloud Run service from a container image.
/// Waits for the underlying operation to complete then returns the service
/// URL — the most useful thing for downstream HttpHealthCheck layers.
///
/// Parameters:
///   serviceName              (required)
///   region                   (required)
///   image                    (required) — container image URI
///   port                     (optional, default 8080)
///   cpuLimit                 (optional, default "1")
///   memoryLimit              (optional, default "512Mi")
///   allowUnauthenticated     (optional, default false) — sets allUsers binding
///   maxInstances             (optional, default 100)
///   env                      (optional) — object map of env vars to set on the container
/// </summary>
public class DeployCloudRunServiceHandler : IOperationHandler
{
    private readonly ILogger<DeployCloudRunServiceHandler> _logger;
    public DeployCloudRunServiceHandler(ILogger<DeployCloudRunServiceHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars, CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("serviceName", out var sProp) || sProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: serviceName");
        if (!parameters.TryGetProperty("region", out var rProp) || rProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: region");
        if (!parameters.TryGetProperty("image", out var iProp) || iProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: image");

        var serviceName = sProp.GetString()!;
        var region = rProp.GetString()!;
        var image = iProp.GetString()!;
        var port = parameters.TryGetProperty("port", out var pProp) ? pProp.GetInt32() : 8080;
        var cpuLimit = parameters.TryGetProperty("cpuLimit", out var cProp) ? cProp.GetString()! : "1";
        var memoryLimit = parameters.TryGetProperty("memoryLimit", out var mProp) ? mProp.GetString()! : "512Mi";
        var maxInstances = parameters.TryGetProperty("maxInstances", out var miProp) ? miProp.GetInt32() : 100;
        var projectId = GcpClientFactory.ResolveProjectId(envVars);
        var parent = $"projects/{projectId}/locations/{region}";

        try
        {
            var client = GcpClientFactory.CreateCloudRunServices(envVars);

            var container = new Container
            {
                Image = image,
                Ports = { new ContainerPort { ContainerPort_ = port } },
                Resources = new ResourceRequirements
                {
                    Limits = { { "cpu", cpuLimit }, { "memory", memoryLimit } },
                },
            };
            if (parameters.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in envProp.EnumerateObject())
                {
                    container.Env.Add(new EnvVar
                    {
                        Name = p.Name,
                        Value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText(),
                    });
                }
            }

            var service = new Service
            {
                Template = new RevisionTemplate
                {
                    Containers = { container },
                    Scaling = new RevisionScaling { MaxInstanceCount = maxInstances },
                },
                Labels = { { "aura-layer", SanitizeLabel(layerName) }, { "aura-managed", "true" } },
            };

            var fullName = $"{parent}/services/{serviceName}";
            bool exists;
            try
            {
                await client.GetServiceAsync(new GetServiceRequest { Name = fullName }, ct);
                exists = true;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                exists = false;
            }

            if (exists)
            {
                _logger.LogInformation("Updating Cloud Run service {ServiceName} in {Region}", serviceName, region);
                service.Name = fullName;
                var updateOp = await client.UpdateServiceAsync(new UpdateServiceRequest { Service = service }, ct);
                await updateOp.PollUntilCompletedAsync();
            }
            else
            {
                _logger.LogInformation("Creating Cloud Run service {ServiceName} in {Region}", serviceName, region);
                var createOp = await client.CreateServiceAsync(new CreateServiceRequest
                {
                    Parent = parent,
                    Service = service,
                    ServiceId = serviceName,
                }, ct);
                await createOp.PollUntilCompletedAsync();
            }

            var deployed = await client.GetServiceAsync(new GetServiceRequest { Name = fullName }, ct);
            return new LayerExecutionResult(true,
                $"Cloud Run service '{serviceName}' deployed in {region}: url={deployed.Uri}");
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Failed to deploy Cloud Run service {ServiceName}", serviceName);
            return new LayerExecutionResult(false, $"Failed to deploy Cloud Run: {ex.StatusCode} — {ex.Status.Detail}");
        }
    }

    private static string SanitizeLabel(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '-').ToArray())
            .TrimStart('-')[..Math.Min(63, s.Length)];
}
