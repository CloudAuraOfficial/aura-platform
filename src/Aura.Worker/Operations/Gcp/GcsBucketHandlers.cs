using System.Text.Json;
using Aura.Worker.Executors;
using Google;
using Google.Apis.Storage.v1.Data;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Gcp;

/// <summary>
/// GCS bucket create/delete. Bucket names are globally unique so no per-
/// network lookup needed. Labels (aura-layer, aura-managed=true) applied
/// at create for inventory.
/// </summary>
public class CreateGcsBucketHandler : IOperationHandler
{
    private readonly ILogger<CreateGcsBucketHandler> _logger;
    public CreateGcsBucketHandler(ILogger<CreateGcsBucketHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars, CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("bucketName", out var nProp) || nProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: bucketName");

        var bucketName = nProp.GetString()!;
        var location = parameters.TryGetProperty("location", out var l) ? l.GetString()! : "US";
        var storageClass = parameters.TryGetProperty("storageClass", out var sc) ? sc.GetString()! : "STANDARD";
        var versioning = parameters.TryGetProperty("versioning", out var vProp) && vProp.GetBoolean();
        var uniformAccess = !parameters.TryGetProperty("uniformBucketLevelAccess", out var uaProp) || uaProp.GetBoolean();
        var projectId = GcpClientFactory.ResolveProjectId(envVars);

        try
        {
            var client = GcpClientFactory.CreateStorage(envVars);
            _logger.LogInformation("Creating GCS bucket {BucketName} in {Location}", bucketName, location);

            var bucket = new Bucket
            {
                Name = bucketName,
                Location = location,
                StorageClass = storageClass,
                Versioning = new Bucket.VersioningData { Enabled = versioning },
                IamConfiguration = new Bucket.IamConfigurationData
                {
                    UniformBucketLevelAccess = new Bucket.IamConfigurationData.UniformBucketLevelAccessData
                    {
                        Enabled = uniformAccess,
                    },
                },
                Labels = new Dictionary<string, string>
                {
                    ["aura-layer"] = SanitizeLabel(layerName),
                    ["aura-managed"] = "true",
                },
            };

            await client.CreateBucketAsync(projectId, bucket, cancellationToken: ct);
            return new LayerExecutionResult(true,
                $"GCS bucket '{bucketName}' created in {location} (storageClass={storageClass}, versioning={versioning}, uniformAccess={uniformAccess}).");
        }
        catch (GoogleApiException ex)
        {
            _logger.LogError(ex, "Failed to create GCS bucket {BucketName}", bucketName);
            return new LayerExecutionResult(false, $"Failed to create GCS bucket: {ex.HttpStatusCode} — {ex.Message}");
        }
    }

    private static string SanitizeLabel(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '-').ToArray())
            .TrimStart('-')[..Math.Min(63, s.Length)];
}

public class DeleteGcsBucketHandler : IOperationHandler
{
    private readonly ILogger<DeleteGcsBucketHandler> _logger;
    public DeleteGcsBucketHandler(ILogger<DeleteGcsBucketHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars, CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("bucketName", out var nProp) || nProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: bucketName");

        var bucketName = nProp.GetString()!;
        var force = parameters.TryGetProperty("force", out var fProp) && fProp.GetBoolean();

        try
        {
            var client = GcpClientFactory.CreateStorage(envVars);
            var deletedObjects = 0;

            if (force)
            {
                _logger.LogInformation("Force-emptying GCS bucket {BucketName}", bucketName);
                await foreach (var obj in client.ListObjectsAsync(bucketName, options: new Google.Cloud.Storage.V1.ListObjectsOptions
                {
                    Versions = true,
                }).WithCancellation(ct))
                {
                    await client.DeleteObjectAsync(obj, cancellationToken: ct);
                    deletedObjects++;
                }
            }

            _logger.LogInformation("Deleting GCS bucket {BucketName}", bucketName);
            await client.DeleteBucketAsync(bucketName, cancellationToken: ct);
            return new LayerExecutionResult(true,
                force
                    ? $"GCS bucket '{bucketName}' deleted (emptied {deletedObjects} object(s)/version(s) first)."
                    : $"GCS bucket '{bucketName}' deleted.");
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new LayerExecutionResult(true, $"GCS bucket '{bucketName}' not found — nothing to delete.");
        }
        catch (GoogleApiException ex)
        {
            _logger.LogError(ex, "Failed to delete GCS bucket {BucketName}", bucketName);
            return new LayerExecutionResult(false, $"Failed to delete GCS bucket: {ex.HttpStatusCode} — {ex.Message}");
        }
    }
}
