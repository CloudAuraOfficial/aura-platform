using Aura.Worker.Operations.Azure;
using Aura.Worker.Operations.Gcp;
using Xunit;

namespace Aura.Tests;

/// <summary>
/// Verifies the factories accept both canonical (SCREAMING_SNAKE) and
/// dashboard-friendly (camelCase / flattened SA) credential shapes.
/// No live cloud calls — we only assert client construction succeeds.
/// </summary>
public class CredentialShapeToleranceTests
{
    [Fact]
    public void AzureFactory_AcceptsCanonicalShape()
    {
        var env = new Dictionary<string, string>
        {
            ["AZURE_TENANT_ID"] = "065ee089-fa06-43ae-9902-a01119765089",
            ["AZURE_CLIENT_ID"] = "1f5d3489-37a6-40fd-b9a3-13794f65ae1b",
            ["AZURE_CLIENT_SECRET"] = "dummy-secret",
        };
        var client = AzureClientFactory.Create(env);
        Assert.NotNull(client);
    }

    [Fact]
    public void AzureFactory_AcceptsCamelCaseShape()
    {
        var env = new Dictionary<string, string>
        {
            ["tenantId"] = "065ee089-fa06-43ae-9902-a01119765089",
            ["clientId"] = "1f5d3489-37a6-40fd-b9a3-13794f65ae1b",
            ["clientSecret"] = "dummy-secret",
        };
        var client = AzureClientFactory.Create(env);
        Assert.NotNull(client);
    }

    [Fact]
    public void AzureFactory_FallsThroughToDefaultCredentialWhenAllKeysMissing()
    {
        // No SP keys → SDK builds the DefaultAzureCredential chain.
        // We can't easily prove that without calling Azure, so just assert no throw on construction.
        var client = AzureClientFactory.Create(new Dictionary<string, string>());
        Assert.NotNull(client);
    }

    [Fact]
    public void GcpFactory_ResolvesProjectIdFromCanonicalKey()
    {
        var env = new Dictionary<string, string> { ["GCP_PROJECT_ID"] = "my-proj" };
        Assert.Equal("my-proj", GcpClientFactory.ResolveProjectId(env));
    }

    [Fact]
    public void GcpFactory_ResolvesProjectIdFromFlattenedSnakeKey()
    {
        var env = new Dictionary<string, string> { ["project_id"] = "my-proj" };
        Assert.Equal("my-proj", GcpClientFactory.ResolveProjectId(env));
    }

    [Fact]
    public void GcpFactory_ResolvesProjectIdFromCamelKey()
    {
        var env = new Dictionary<string, string> { ["projectId"] = "my-proj" };
        Assert.Equal("my-proj", GcpClientFactory.ResolveProjectId(env));
    }

    [Fact]
    public void GcpFactory_ThrowsWhenNoProjectIdAnywhere()
    {
        Assert.Throws<InvalidOperationException>(() =>
            GcpClientFactory.ResolveProjectId(new Dictionary<string, string>()));
    }
}
