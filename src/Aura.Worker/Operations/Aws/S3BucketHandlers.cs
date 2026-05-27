using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Aws;

/// <summary>
/// S3 bucket create/delete. S3 buckets are global (no VPC) and identified
/// by the bucketName itself — no tag lookup needed. Tags are still applied
/// (aura:layer, aura:managed) for inventory and cost-allocation reports.
/// </summary>
public class CreateS3BucketHandler : IOperationHandler
{
    private readonly ILogger<CreateS3BucketHandler> _logger;
    public CreateS3BucketHandler(ILogger<CreateS3BucketHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("bucketName", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: bucketName");

        var bucketName = nameProp.GetString()!;
        var versioning = parameters.TryGetProperty("versioning", out var vProp) && vProp.GetBoolean();
        var publicAccessBlock = !parameters.TryGetProperty("publicAccessBlock", out var pabProp) || pabProp.GetBoolean();

        using var s3 = AwsClientFactory.CreateS3(envVars);

        try
        {
            _logger.LogInformation("Creating S3 bucket {BucketName}", bucketName);
            await s3.PutBucketAsync(new PutBucketRequest
            {
                BucketName = bucketName,
                UseClientRegion = true,
            }, ct);

            await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
            {
                BucketName = bucketName,
                TagSet = new List<Tag>
                {
                    new() { Key = "Name", Value = bucketName },
                    new() { Key = "aura:layer", Value = layerName },
                    new() { Key = "aura:managed", Value = "true" },
                },
            }, ct);

            if (versioning)
            {
                await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
                {
                    BucketName = bucketName,
                    VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
                }, ct);
            }

            if (publicAccessBlock)
            {
                await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
                {
                    BucketName = bucketName,
                    PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
                    {
                        BlockPublicAcls = true,
                        BlockPublicPolicy = true,
                        IgnorePublicAcls = true,
                        RestrictPublicBuckets = true,
                    },
                }, ct);
            }

            return new LayerExecutionResult(true, $"S3 bucket '{bucketName}' created (versioning={versioning}, publicAccessBlock={publicAccessBlock}).");
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogError(ex, "Failed to create S3 bucket {BucketName}", bucketName);
            return new LayerExecutionResult(false, $"Failed to create S3 bucket: {ex.ErrorCode} — {ex.Message}");
        }
    }
}

public class DeleteS3BucketHandler : IOperationHandler
{
    private readonly ILogger<DeleteS3BucketHandler> _logger;
    public DeleteS3BucketHandler(ILogger<DeleteS3BucketHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("bucketName", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: bucketName");

        var bucketName = nameProp.GetString()!;
        var force = parameters.TryGetProperty("force", out var fProp) && fProp.GetBoolean();

        using var s3 = AwsClientFactory.CreateS3(envVars);
        var deletedObjects = 0;

        try
        {
            if (force)
            {
                deletedObjects = await EmptyBucketAsync(s3, bucketName, ct);
            }

            _logger.LogInformation("Deleting S3 bucket {BucketName}", bucketName);
            await s3.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucketName }, ct);

            return new LayerExecutionResult(true,
                force
                    ? $"S3 bucket '{bucketName}' deleted (emptied {deletedObjects} object(s)/version(s) first)."
                    : $"S3 bucket '{bucketName}' deleted.");
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
        {
            return new LayerExecutionResult(true, $"S3 bucket '{bucketName}' not found — nothing to delete.");
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogError(ex, "Failed to delete S3 bucket {BucketName}", bucketName);
            return new LayerExecutionResult(false, $"Failed to delete S3 bucket: {ex.ErrorCode} — {ex.Message}");
        }
    }

    private static async Task<int> EmptyBucketAsync(AmazonS3Client s3, string bucketName, CancellationToken ct)
    {
        var total = 0;
        string? keyMarker = null;
        string? versionIdMarker = null;

        do
        {
            var versions = await s3.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucketName,
                KeyMarker = keyMarker,
                VersionIdMarker = versionIdMarker,
            }, ct);

            if (versions.Versions.Count > 0)
            {
                await s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = bucketName,
                    Objects = versions.Versions.Select(v => new KeyVersion
                    {
                        Key = v.Key,
                        VersionId = v.VersionId,
                    }).ToList(),
                    Quiet = true,
                }, ct);
                total += versions.Versions.Count;
            }

            keyMarker = versions.NextKeyMarker;
            versionIdMarker = versions.NextVersionIdMarker;
        } while (!string.IsNullOrEmpty(keyMarker));

        return total;
    }
}
