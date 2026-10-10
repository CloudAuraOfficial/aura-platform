using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Aura.Api.Controllers;
using Aura.Api.Middleware;
using Aura.Api.Services;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

// End-to-end coverage of POST /api/v1/essences/generate with a FAKE LLM provider:
// the real EssencesController -> AiEssenceBuilderService -> OpenAiCompatibleLlmProvider
// chain runs, but the HTTP transport is a scripted stub. No network, no real keys.
public class EssenceGenerationE2ETests
{
    private const string FakeKey = "test-key-not-real";
    private const string ProviderName = "openrouter";

    // A valid essence in the same shape as the Essencefile the worker actually runs.
    private const string ValidEssence = """
        {
          "baseEssence": { "cloudProvider": "Azure", "defaultRegion": "eastus", "baseLoad": "EmissionLoadVM", "uniqueId": "demo" },
          "layers": {
            "create-rg": {
              "isEnabled": true, "operationType": "CreateResourceGroup",
              "parameters": { "resourceGroup": "rg-demo", "location": "eastus" }, "dependsOn": []
            },
            "deploy-vm": {
              "isEnabled": true, "operationType": "CreateVM",
              "parameters": { "resourceGroup": "rg-demo", "vmName": "vm-demo", "location": "eastus" },
              "dependsOn": ["create-rg"]
            }
          }
        }
        """;

    // Scripted OpenAI-compatible transport. Each entry produces one provider response.
    private sealed class ScriptedTransport : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _script;
        public List<string> RequestBodies { get; } = new();

        public ScriptedTransport(params Func<HttpResponseMessage>[] script) => _script = new(script);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return _script.Dequeue()();
        }
    }

    private static Func<HttpResponseMessage> ChatReply(string content, int inputTokens = 10, int outputTokens = 5) => () =>
    {
        var body = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = inputTokens, completion_tokens = outputTokens }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    };

    private static Func<HttpResponseMessage> HttpError(HttpStatusCode status) => () =>
        new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"upstream\"}") };

    private static Func<HttpResponseMessage> Timeout() => () =>
        throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

    private sealed class Harness
    {
        public required AuraDbContext Db { get; init; }
        public required EssencesController Controller { get; init; }
        public required ScriptedTransport Transport { get; init; }
        public required Guid CloudAccountId { get; init; }
    }

    private static Harness Build(bool withKey, params Func<HttpResponseMessage>[] script)
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AuraDbContext(options);
        var userId = Guid.NewGuid();
        var cloudId = Guid.NewGuid();

        db.CloudAccounts.Add(new CloudAccount
        {
            Id = cloudId, TenantId = Guid.Empty, Provider = Aura.Core.Enums.CloudProvider.Azure, Label = "acct"
        });
        if (withKey)
        {
            db.UserAiProviders.Add(new UserAiProvider
            {
                TenantId = Guid.Empty, UserId = userId, ProviderName = ProviderName, EncryptedApiKey = FakeKey
            });
        }
        db.SaveChanges();

        var transport = new ScriptedTransport(script);
        var provider = new OpenAiCompatibleLlmProvider(new HttpClient(transport), ProviderName,
            "http://fake-llm.invalid/v1/chat/completions", "stub-model");
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>())).Returns<string>(s => s);

        var builder = new AiEssenceBuilderService(
            new LlmProviderFactory(new ILlmProvider[] { provider }),
            new UserAiKeyService(db, crypto.Object),
            db,
            Mock.Of<ILogger<AiEssenceBuilderService>>());

        var tenant = new Mock<ITenantContext>();
        tenant.Setup(t => t.TenantId).Returns(Guid.Empty);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"))
        };
        var controller = new EssencesController(db, tenant.Object, Mock.Of<IAuditService>(), builder)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return new Harness { Db = db, Controller = controller, Transport = transport, CloudAccountId = cloudId };
    }

    private static GenerateEssenceRequest Request(Guid cloudId, string provider = ProviderName) =>
        new("Create a resource group and a small Ubuntu VM in eastus", cloudId, provider);

    private static (int status, ErrorResponse error) AssertError(IActionResult result)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(obj.Value);
        Assert.Equal(error.StatusCode, obj.StatusCode);
        return (obj.StatusCode!.Value, error);
    }

    // (a) Valid prompt -> essence that passes the project's own essence validation.
    [Fact]
    public async Task Generate_valid_prompt_returns_essence_that_passes_project_validation()
    {
        var h = Build(withKey: true, ChatReply(ValidEssence));

        var result = await h.Controller.Generate(Request(h.CloudAccountId));

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GenerateEssenceResponse>(ok.Value);
        Assert.Equal(1, response.Iterations);
        Assert.Equal(10, response.InputTokens);
        Assert.Equal(5, response.OutputTokens);

        // The same parser the worker uses at run creation must accept it and order it.
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(response.EssenceJson, Guid.NewGuid());
        Assert.Equal(2, layers.Count);
        Assert.Equal("create-rg", layers[0].LayerName);
        Assert.Equal("deploy-vm", layers[1].LayerName);

        // Generation returns the draft only; nothing is persisted until the user saves it.
        Assert.Empty(h.Db.Essences);
    }

    [Fact]
    public async Task Generate_accepts_essence_wrapped_in_markdown_fences()
    {
        var fenced = "```json\n" + ValidEssence + "\n```";
        var h = Build(withKey: true, ChatReply(fenced));

        var result = await h.Controller.Generate(Request(h.CloudAccountId));

        var response = Assert.IsType<GenerateEssenceResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, DeploymentOrchestrationService.ParseAndSortLayers(response.EssenceJson, Guid.NewGuid()).Count);
    }

    // (b) No provider key -> clear error in the standard shape; no provider call, no usage row.
    [Fact]
    public async Task Generate_without_provider_key_returns_clear_standard_error()
    {
        var h = Build(withKey: false);

        var result = await h.Controller.Generate(Request(h.CloudAccountId));

        var (status, error) = AssertError(result);
        Assert.Equal(422, status);
        Assert.Equal("generation_failed", error.Error);
        Assert.Contains("No API key configured for provider 'openrouter'", error.Message);
        Assert.DoesNotContain("   at ", error.Message);
        Assert.Equal(0, h.Transport.RequestBodies.Count);
        Assert.Empty(h.Db.AiGenerationLogs);
        Assert.Empty(h.Db.Essences);
    }

    [Fact]
    public async Task Generate_with_unsupported_provider_returns_bad_request()
    {
        var h = Build(withKey: true);

        var result = await h.Controller.Generate(Request(h.CloudAccountId, provider: "no-such-provider"));

        var (status, error) = AssertError(result);
        Assert.Equal(400, status);
        Assert.Equal("bad_request", error.Error);
        Assert.Contains("Unsupported LLM provider", error.Message);
    }

    // (c) Malformed / invalid output -> graceful error after all retries; nothing invalid persisted.
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{ "name": "no layers here" }""")]
    [InlineData("""{ "layers": {} }""")]
    [InlineData("""{ "layers": { "a": "not an object" } }""")]
    [InlineData("""{ "layers": { "a": { "isEnabled": true, "operationType": "CreateVM", "runPolicy": "sometimes" } } }""")]
    [InlineData("""
        { "layers": {
            "a": { "isEnabled": true, "operationType": "CreateVM", "dependsOn": ["b"] },
            "b": { "isEnabled": true, "operationType": "CreateVM", "dependsOn": ["a"] }
        } }
        """)]
    public async Task Generate_invalid_output_fails_gracefully_and_persists_nothing(string badOutput)
    {
        var h = Build(withKey: true,
            ChatReply(badOutput), ChatReply(badOutput), ChatReply(badOutput));

        var result = await h.Controller.Generate(Request(h.CloudAccountId));

        var (status, error) = AssertError(result);
        Assert.Equal(422, status);
        Assert.Equal("generation_failed", error.Error);
        Assert.StartsWith("Failed to generate valid essence JSON after 3 attempts", error.Message);
        Assert.Equal(3, h.Transport.RequestBodies.Count);
        Assert.Empty(h.Db.Essences);

        var log = Assert.Single(h.Db.AiGenerationLogs);
        Assert.False(log.Success);
        Assert.Equal(3, log.Iterations);
    }

    [Fact]
    public async Task Generate_recovers_when_retry_returns_valid_essence()
    {
        var h = Build(withKey: true, ChatReply("not json"), ChatReply(ValidEssence));

        var result = await h.Controller.Generate(Request(h.CloudAccountId));

        var response = Assert.IsType<GenerateEssenceResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, response.Iterations);
        // The retry prompt must carry the previous failure back to the model.
        Assert.Contains("previous response", h.Transport.RequestBodies[1]);
    }

    // (d) Provider timeout / HTTP error -> graceful error; usage from earlier attempts is kept.
    [Fact]
    public async Task Generate_provider_timeout_returns_graceful_error()
    {
        var h = Build(withKey: true, Timeout());

        var result = await h.Controller.Generate(Request(h.CloudAccountId));

        var (status, error) = AssertError(result);
        Assert.Equal(422, status);
        Assert.Equal("generation_failed", error.Error);
        Assert.Contains("request failed", error.Message);
        Assert.Empty(h.Db.Essences);
        Assert.False(Assert.Single(h.Db.AiGenerationLogs).Success);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Generate_provider_http_error_returns_graceful_error(HttpStatusCode upstream)
    {
        var h = Build(withKey: true, HttpError(upstream));

        var result = await h.Controller.Generate(Request(h.CloudAccountId));

        var (status, error) = AssertError(result);
        Assert.Equal(422, status);
        Assert.Contains($"API error {(int)upstream}", error.Message);
        Assert.Empty(h.Db.Essences);
    }

    [Fact]
    public async Task Generate_failure_after_a_spent_attempt_still_records_that_usage()
    {
        // Attempt 1 burns tokens but is unparseable; attempt 2 hits an upstream error.
        var h = Build(withKey: true, ChatReply("not json", inputTokens: 10, outputTokens: 5), HttpError(HttpStatusCode.ServiceUnavailable));

        var result = await h.Controller.Generate(Request(h.CloudAccountId));

        AssertError(result);
        var log = Assert.Single(h.Db.AiGenerationLogs);
        Assert.False(log.Success);
        Assert.Equal(2, log.Iterations);
        Assert.Equal(10, log.InputTokens);
        Assert.Equal(5, log.OutputTokens);
    }

    // Shape of the framework-level validation failure (e.g. empty prompt) must match the rest of the API.
    [Fact]
    public void Invalid_model_state_returns_standard_error_shape()
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        actionContext.ModelState.AddModelError("Prompt", "The Prompt field is required.");

        var result = InvalidModelStateResponse.Create(actionContext);

        var (status, error) = AssertError(result);
        Assert.Equal(400, status);
        Assert.Equal("bad_request", error.Error);
        Assert.Contains("The Prompt field is required.", error.Message);
    }

    // Unexpected failures never leak a stack trace to the client.
    [Fact]
    public async Task Unhandled_exception_returns_generic_error_without_stack_trace()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionHandlerMiddleware(
            _ => throw new InvalidOperationException("secret internal detail"),
            Mock.Of<ILogger<ExceptionHandlerMiddleware>>());

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("internal_error", root.GetProperty("error").GetString());
        Assert.Equal("An unexpected error occurred.", root.GetProperty("message").GetString());
        Assert.Equal(500, root.GetProperty("statusCode").GetInt32());
        Assert.DoesNotContain("secret internal detail", json);
        Assert.DoesNotContain("   at ", json);
    }
}
