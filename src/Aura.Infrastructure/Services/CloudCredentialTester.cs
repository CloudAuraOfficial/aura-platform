using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Aura.Core.DTOs;
using Aura.Core.Enums;
using Aura.Core.Interfaces;

namespace Aura.Infrastructure.Services;

public class CloudCredentialTester : ICloudCredentialTester
{
    private readonly IHttpClientFactory _httpFactory;

    public CloudCredentialTester(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<CredentialTestResponse> TestAsync(
        CloudProvider provider,
        string credentialsJson,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return provider switch
            {
                CloudProvider.Aws => Done(sw, await TestAwsAsync(credentialsJson, ct), provider),
                CloudProvider.Gcp => Done(sw, await TestGcpAsync(credentialsJson, ct), provider),
                CloudProvider.Azure => Done(sw, await TestAzureAsync(credentialsJson, ct), provider),
                _ => new CredentialTestResponse(false, provider.ToString(), null,
                        $"Unsupported provider: {provider}", (int)sw.ElapsedMilliseconds)
            };
        }
        catch (JsonException ex)
        {
            return new CredentialTestResponse(false, provider.ToString(), null,
                $"Credentials are not valid JSON: {ex.Message}", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new CredentialTestResponse(false, provider.ToString(), null,
                ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    private static CredentialTestResponse Done(Stopwatch sw, (bool ok, string? id, string? err) r, CloudProvider p)
        => new(r.ok, p.ToString(), r.id, r.err, (int)sw.ElapsedMilliseconds);

    // ---------- AWS ----------
    private static async Task<(bool, string?, string?)> TestAwsAsync(string json, CancellationToken ct)
    {
        var creds = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? throw new InvalidOperationException("Empty credentials");

        if (!creds.TryGetValue("AWS_ACCESS_KEY_ID", out var ak) || string.IsNullOrWhiteSpace(ak))
            return (false, null, "Missing AWS_ACCESS_KEY_ID");
        if (!creds.TryGetValue("AWS_SECRET_ACCESS_KEY", out var sk) || string.IsNullOrWhiteSpace(sk))
            return (false, null, "Missing AWS_SECRET_ACCESS_KEY");

        var region = creds.TryGetValue("AWS_REGION", out var r) && !string.IsNullOrWhiteSpace(r)
            ? r : "us-east-1";

        var client = new AmazonSecurityTokenServiceClient(
            new BasicAWSCredentials(ak, sk),
            RegionEndpoint.GetBySystemName(region));

        var resp = await client.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
        return (true, resp.Arn, null);
    }

    // ---------- GCP ----------
    private async Task<(bool, string?, string?)> TestGcpAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("client_email", out var emailProp) ||
            !root.TryGetProperty("private_key", out var keyProp) ||
            !root.TryGetProperty("token_uri", out var tokenUriProp))
        {
            return (false, null, "Missing client_email, private_key, or token_uri");
        }

        var clientEmail = emailProp.GetString()!;
        var privateKey = keyProp.GetString()!;
        var tokenUri = tokenUriProp.GetString()!;

        var jwt = BuildGcpJwt(clientEmail, privateKey, tokenUri);

        using var http = _httpFactory.CreateClient("cloud-cred-test");
        var resp = await http.PostAsync(tokenUri, new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
            new KeyValuePair<string, string>("assertion", jwt),
        }), ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return (false, null, $"GCP token endpoint returned {(int)resp.StatusCode}: {Truncate(body, 300)}");

        return (true, clientEmail, null);
    }

    private static string BuildGcpJwt(string clientEmail, string pemKey, string audience)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = new { alg = "RS256", typ = "JWT" };
        var payload = new
        {
            iss = clientEmail,
            scope = "https://www.googleapis.com/auth/cloud-platform.read-only",
            aud = audience,
            iat = now,
            exp = now + 3600
        };

        var headerB64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadB64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{headerB64}.{payloadB64}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pemKey);
        var sig = rsa.SignData(Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64UrlEncode(sig)}";
    }

    // ---------- Azure ----------
    private async Task<(bool, string?, string?)> TestAzureAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("tenantId", out var tid) ||
            !root.TryGetProperty("clientId", out var cid) ||
            !root.TryGetProperty("clientSecret", out var sec))
        {
            return (false, null,
                "Azure test requires tenantId, clientId, and clientSecret in credentials JSON. " +
                "Update this account with full service-principal credentials to enable testing.");
        }

        var tenantId = tid.GetString()!;
        var clientId = cid.GetString()!;
        var clientSecret = sec.GetString()!;

        using var http = _httpFactory.CreateClient("cloud-cred-test");
        var resp = await http.PostAsync(
            $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("scope", "https://management.azure.com/.default"),
            }), ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return (false, null, $"Azure token endpoint returned {(int)resp.StatusCode}: {Truncate(body, 300)}");

        var identity = root.TryGetProperty("subscriptionId", out var sub)
            ? $"sp:{clientId} sub:{sub.GetString()}"
            : $"sp:{clientId}";
        return (true, identity, null);
    }

    // ---------- helpers ----------
    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "...";
}
