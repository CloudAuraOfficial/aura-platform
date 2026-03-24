using Aura.Worker.Operations;
using Xunit;

namespace Aura.Tests;

public class ByosResolverTests
{
    [Fact]
    public void Resolve_Replaces_Single_Reference()
    {
        var json = """{"password": "${BYOS_DB_PASSWORD}"}""";
        var creds = new Dictionary<string, string> { ["DB_PASSWORD"] = "secret123" };

        var result = ByosResolver.Resolve(json, creds);

        Assert.Equal("""{"password": "secret123"}""", result);
    }

    [Fact]
    public void Resolve_Replaces_Multiple_References()
    {
        var json = """{"user": "${BYOS_DB_USER}", "pass": "${BYOS_DB_PASS}"}""";
        var creds = new Dictionary<string, string>
        {
            ["DB_USER"] = "admin",
            ["DB_PASS"] = "s3cret"
        };

        var result = ByosResolver.Resolve(json, creds);

        Assert.Equal("""{"user": "admin", "pass": "s3cret"}""", result);
    }

    [Fact]
    public void Resolve_Missing_Key_Throws_InvalidOperationException()
    {
        var json = """{"password": "${BYOS_MISSING_KEY}"}""";
        var creds = new Dictionary<string, string>();

        Assert.Throws<InvalidOperationException>(() =>
            ByosResolver.Resolve(json, creds));
    }

    [Fact]
    public void Resolve_No_References_Returns_Unchanged()
    {
        var json = """{"host": "localhost", "port": 5432}""";
        var creds = new Dictionary<string, string> { ["SOME_KEY"] = "value" };

        var result = ByosResolver.Resolve(json, creds);

        Assert.Equal(json, result);
    }

    [Fact]
    public void Resolve_Handles_Nested_Json()
    {
        var json = """{"config": {"db": {"password": "${BYOS_PW}"}}}""";
        var creds = new Dictionary<string, string> { ["PW"] = "nested-secret" };

        var result = ByosResolver.Resolve(json, creds);

        Assert.Equal("""{"config": {"db": {"password": "nested-secret"}}}""", result);
    }

    [Fact]
    public void PopulateEnvVars_Adds_All_Credentials()
    {
        var envVars = new Dictionary<string, string> { ["EXISTING"] = "keep" };
        var creds = new Dictionary<string, string>
        {
            ["DB_HOST"] = "localhost",
            ["DB_PORT"] = "5432"
        };

        ByosResolver.PopulateEnvVars(envVars, creds);

        Assert.Equal("keep", envVars["EXISTING"]);
        Assert.Equal("localhost", envVars["DB_HOST"]);
        Assert.Equal("5432", envVars["DB_PORT"]);
        Assert.Equal(3, envVars.Count);
    }
}
