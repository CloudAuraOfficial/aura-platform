using Amazon;
using Amazon.CloudFormation;
using Amazon.EC2;
using Amazon.ECS;
using Amazon.IdentityManagement;
using Amazon.Runtime;
using Amazon.S3;

namespace Aura.Worker.Operations.Aws;

/// <summary>
/// Builds AWS service clients from BYOS credentials passed in via envVars.
///
/// Required keys:  AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, AWS_REGION
/// Optional key:   AWS_SESSION_TOKEN (for STS / federated creds)
///
/// If AWS_ACCESS_KEY_ID is missing the SDK's default credential chain runs —
/// useful for local development against an instance profile or aws-cli session.
/// Region falls back to AURA_DEFAULT_REGION, then us-east-1.
/// </summary>
public static class AwsClientFactory
{
    public static AmazonEC2Client CreateEc2(Dictionary<string, string> envVars) =>
        new(GetCredentials(envVars), GetRegion(envVars));

    public static AmazonS3Client CreateS3(Dictionary<string, string> envVars) =>
        new(GetCredentials(envVars), GetRegion(envVars));

    public static AmazonECSClient CreateEcs(Dictionary<string, string> envVars) =>
        new(GetCredentials(envVars), GetRegion(envVars));

    public static AmazonCloudFormationClient CreateCloudFormation(Dictionary<string, string> envVars) =>
        new(GetCredentials(envVars), GetRegion(envVars));

    public static AmazonIdentityManagementServiceClient CreateIam(Dictionary<string, string> envVars) =>
        new(GetCredentials(envVars), GetRegion(envVars));

    private static AWSCredentials GetCredentials(Dictionary<string, string> envVars)
    {
        if (!envVars.TryGetValue("AWS_ACCESS_KEY_ID", out var accessKey) ||
            !envVars.TryGetValue("AWS_SECRET_ACCESS_KEY", out var secretKey))
        {
            return FallbackCredentialsFactory.GetCredentials();
        }

        return envVars.TryGetValue("AWS_SESSION_TOKEN", out var sessionToken)
            && !string.IsNullOrEmpty(sessionToken)
            ? new SessionAWSCredentials(accessKey, secretKey, sessionToken)
            : new BasicAWSCredentials(accessKey, secretKey);
    }

    private static RegionEndpoint GetRegion(Dictionary<string, string> envVars)
    {
        var regionName = envVars.GetValueOrDefault("AWS_REGION")
            ?? envVars.GetValueOrDefault("AURA_DEFAULT_REGION")
            ?? "us-east-1";

        return RegionEndpoint.GetBySystemName(regionName);
    }
}
