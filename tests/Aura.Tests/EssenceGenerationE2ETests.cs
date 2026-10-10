using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Aura.Api.Controllers;
using Aura.Api.Middleware;
using Aura.Api.Services;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

// End-to-end coverage of AI essence generation with a FAKE provider: the real
// OpenAiCompatibleLlmProvider runs over a scripted HttpMessageHandler, so no network
// traffic and no real keys are involved. Covers the service, the controller's HTTP
// mapping, and the project's own essence validation (ParseAndSortLayers).
public class EssenceGenerationE2ETests
{
    // Never contacted: the scripted transport intercepts every request.
    private const string FakeApiUrl = "http://localhost/fake-llm/chat/completions";
    private const string FakeKey = "fake-key-for-tests";

    private const string ValidEssence = """
        {
          "baseEssence": { "cloudProvider": "Azure", "defaultRegion": "eastus", "baseLoad": "EmissionLoadVM", "uniqueId": "demo-vm" },
          "layers": {
            "create-rg": { "isEnabled": true, "operationType": "CreateResourceGroup", "parameters": { "resourceGroup": "rg-demo", "location": "eastus" }, "dependsOn": [] },
            "deploy-vm": { "isEnabled": true, "operationType": "CreateVM", "parameters": { "resourceGroup": "rg-demo", "vmName": "vm-demo", "location": "eastus", "vmSize": "Standard_B2s" }, "dependsOn": ["create-rg"] },
            "health-check": { "isEnabled": true, "operationType": "HttpHealthCheck", "parameters": { "path": "/health" }, "dependsOn": ["deploy-vm"] }
          }
        }
        """;

    // Scripted transport: each call pops the next step; a step may return a response or throw.
    private sealed class ScriptedTransport : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _steps;
        public int CallCount { get; private set; }

        public ScriptedTransport(IEnumerable<Func<HttpResponseMessage>> steps) => _steps = new(steps);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            if (_steps.Count == 0)
                return Task.FromException<HttpResponseMessage>(
                    new InvalidOperationException("Unexpected extra provider call in test."));
            try
            {
                return Task.FromResult(_steps.Dequeue()());
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }

    // OpenAI-shaped success reply carrying the given completion text.
    private static Func<HttpResponseMessage> Reply(string content, int inputTokens = 10, int outputTokens = 5) => () =>
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

    private static Func<HttpResponseMessage> HttpError(HttpStatusCode status, string body) => () =>
        new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Harness
    {
        public required AuraDbContext Db { get; init; }
        public required AiEssenceBuilderService Service { get; init; }
        public required ScriptedTransport Transport { get; init; }
        public required Guid UserId { get; init; }
        public required Guid CloudAccountId { get; init; }
    }

    private static Harness CreateHarness(bool hasKey, params Func<HttpResponseMessage>[] steps)
    {
        var db = new AuraDbContext(new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var userId = Guid.NewGuid();
        var cloudAccountId = Guid.NewGuid();
        var tenantId = Guid.Empty; // matches the parameterless DbContext's tenant query filter

        if (hasKey)
        {
            db.UserAiProviders.Add(new UserAiProvider
            {
                TenantId = tenantId, UserId = userId, ProviderName = "openai", EncryptedApiKey = FakeKey
            });
        }
        db.CloudAccounts.Add(new CloudAccount
        {
            Id = cloudAccountId, TenantId = tenantId, Provider = CloudProvider.Azure, Label = "acct"
        });
        db.SaveChanges();

        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>())).Returns<string>(s => s);

        var transport = new ScriptedTransport(steps);
        var provider = new OpenAiCompatibleLlmProvider(new HttpClient(transport), "openai", FakeApiUrl, "fake-model");
        var service = new AiEssenceBuilderService(
            new LlmProviderFactory(new ILlmProvider[] { provider }),
            new UserAiKeyService(db, crypto.Object),
            db,
            Mock.Of<ILogger<AiEssenceBuilderService>>());

        return new Harness
        {
            Db = db, Service = service, Transport = transport, UserId = userId, CloudAccountId = cloudAccountId
        };
    }

    private static GenerateEssenceRequest Request(Guid cloudAccountId, string prompt = "Deploy a small Ubuntu VM in East US") =>
        new(prompt, cloudAccountId, "openai");

    private static Task<GenerateEssenceResponse> Generate(Harness h, GenerateEssenceRequest? request = null) =>
        h.Service.GenerateAsync(h.UserId, request ?? Request(h.CloudAccountId), Guid.Empty, CancellationToken.None);

    // ---- (a) valid prompt -> valid, runnable essence ----

    [Fact]
    public async Task Valid_prompt_yields_essence_that_passes_project_validation()
    {
        var h = CreateHarness(hasKey: true, Reply(ValidEssence, 120, 80));

        var result = await Generate(h);

        Assert.Null(AiEssenceBuilderService.ValidateEssenceJson(result.EssenceJson));
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(result.EssenceJson, Guid.NewGuid());
        Assert.Equal(new[] { "create-rg", "deploy-vm", "health-check" }, layers.Select(l => l.LayerName));
        Assert.Equal(new[] { 0, 1, 2 }, layers.Select(l => l.SortOrder));

        Assert.Equal(1, result.Iterations);
        Assert.Equal(120, result.InputTokens);
        Assert.Equal(80, result.OutputTokens);
        Assert.Equal("fake-model", result.Model);
        Assert.Equal(1, h.Transport.CallCount);

        var log = await h.Db.Set<AiGenerationLog>().SingleAsync();
        Assert.True(log.Success);
        Assert.Equal("openai", log.ProviderName);
    }

    [Fact]
    public async Task Fenced_markdown_output_is_unwrapped_and_accepted()
    {
        var h = CreateHarness(hasKey: true, Reply($"```json\n{ValidEssence}\n```"));

        var result = await Generate(h);

        Assert.Null(AiEssenceBuilderService.ValidateEssenceJson(result.EssenceJson));
        Assert.StartsWith("{", result.EssenceJson);
    }

    [Fact]
    public async Task Generation_does_not_persist_an_essence_itself()
    {
        // The UI saves the generated JSON through the normal create path; generate must not.
        var h = CreateHarness(hasKey: true, Reply(ValidEssence));

        await Generate(h);

        Assert.Empty(h.Db.Essences);
    }

    [Fact]
    public async Task Bad_first_output_is_retried_and_second_valid_output_wins()
    {
        var h = CreateHarness(hasKey: true, Reply("Sure! Here is your essence."), Reply(ValidEssence));

        var result = await Generate(h);

        Assert.Equal(2, result.Iterations);
        Assert.Equal(20, result.InputTokens);  // 10 × 2 attempts
        Assert.Equal(10, result.OutputTokens); // 5 × 2 attempts
        var log = await h.Db.Set<AiGenerationLog>().SingleAsync();
        Assert.True(log.Success);
        Assert.Equal(2, log.Iterations);
    }

    // ---- (b) no provider key ----

    [Fact]
    public async Task Missing_key_fails_clearly_without_calling_the_provider()
    {
        var h = CreateHarness(hasKey: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(h));

        Assert.Contains("No API key configured for provider 'openai'", ex.Message);
        Assert.Equal(0, h.Transport.CallCount);
        Assert.Empty(h.Db.Set<AiGenerationLog>());
    }

    [Fact]
    public async Task Blank_prompt_is_rejected_before_any_provider_call()
    {
        var h = CreateHarness(hasKey: true);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Generate(h, Request(h.CloudAccountId, prompt: "   ")));

        Assert.Equal("Prompt must not be blank.", ex.Message);
        Assert.Equal(0, h.Transport.CallCount);
    }

    // ---- (c) malformed / invalid model output ----

    private const string CyclicEssence = """
        {"layers":{
          "a":{"isEnabled":true,"operationType":"CreateResourceGroup","dependsOn":["b"]},
          "b":{"isEnabled":true,"operationType":"CreateVM","dependsOn":["a"]}}}
        """;

    public static IEnumerable<object[]> InvalidOutputs() => new[]
    {
        new object[] { "not json at all", "" },
        new object[] { "[1,2,3]", "" },                           // valid JSON, not an object (#21 regression)
        new object[] { "\"just a string\"", "" },
        new object[] { "{\"name\":\"no layers here\"}", "missing 'layers'" },
        new object[] { "{\"layers\":[]}", "" },                  // layers must be an object
        new object[] { "{\"layers\":{}}", "no enabled layers" },
        new object[] { "{\"layers\":{\"a\":{\"isEnabled\":false,\"operationType\":\"CreateResourceGroup\"}}}", "no enabled layers" },
        new object[] { CyclicEssence, "Cycle detected" },
        new object[] { "{\"layers\":{\"a\":{\"isEnabled\":true,\"operationType\":\"CreateResourceGroup\",\"runPolicy\":\"sometimes\"}}}", "unknown runPolicy" },
    };

    [Theory]
    [MemberData(nameof(InvalidOutputs))]
    public async Task Invalid_model_output_is_retried_then_rejected_and_nothing_is_persisted(string output, string expectedReason)
    {
        var h = CreateHarness(hasKey: true, Reply(output), Reply(output), Reply(output));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(h));

        Assert.Contains("Failed to generate valid essence JSON after 3 attempts", ex.Message);
        if (expectedReason.Length > 0)
            Assert.Contains(expectedReason, ex.Message);

        // Usage from every paid attempt is recorded, marked as failed (#21).
        var log = await h.Db.Set<AiGenerationLog>().SingleAsync();
        Assert.False(log.Success);
        Assert.Equal(3, log.Iterations);
        Assert.Equal(30, log.InputTokens);
        Assert.Equal(15, log.OutputTokens);

        Assert.Empty(h.Db.Essences);
    }

    [Fact]
    public async Task Usage_from_earlier_attempts_is_kept_when_a_later_provider_call_fails()
    {
        var h = CreateHarness(hasKey: true, Reply("not json"), HttpError(HttpStatusCode.InternalServerError, "{\"error\":\"upstream\"}"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(h));

        Assert.Contains("LLM provider error", ex.Message);
        var log = await h.Db.Set<AiGenerationLog>().SingleAsync();
        Assert.False(log.Success);
        Assert.Equal(2, log.Iterations);
        Assert.Equal(10, log.InputTokens);   // the failed call reported no usage
        Assert.Equal(5, log.OutputTokens);
    }

    // ---- (d) provider timeout / HTTP error ----

    [Fact]
    public async Task Provider_timeout_fails_gracefully_without_retry()
    {
        // HttpClient's timeout surfaces as TaskCanceledException; the provider maps it to a failed result.
        var h = CreateHarness(hasKey: true, () => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(h));

        Assert.StartsWith("LLM provider error: openai request failed", ex.Message);
        Assert.Equal(1, h.Transport.CallCount);
        var log = await h.Db.Set<AiGenerationLog>().SingleAsync();
        Assert.False(log.Success);
        Assert.Equal(1, log.Iterations);
    }

    [Fact]
    public async Task Provider_http_error_fails_gracefully_with_status_in_message()
    {
        var h = CreateHarness(hasKey: true, HttpError(HttpStatusCode.Unauthorized, "{\"error\":\"invalid key\"}"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(h));

        Assert.Contains("API error 401", ex.Message);
        Assert.Equal(1, h.Transport.CallCount);
        Assert.Empty(h.Db.Essences);
    }

    // ---- controller: HTTP status and standard error shape ----

    private static EssencesController CreateController(Harness h)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, h.UserId.ToString()),
            new Claim(ClaimTypes.Role, "Member")
        }, "test"));

        return new EssencesController(
            h.Db,
            Mock.Of<ITenantContext>(t => t.TenantId == Guid.Empty),
            Mock.Of<IAuditService>(),
            h.Service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
    }

    private static (int status, ErrorResponse error) AssertErrorResult(IActionResult result)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(obj.Value);
        Assert.Equal(error.StatusCode, obj.StatusCode);

        // The client-facing body must never carry a stack trace or exception type name.
        var json = JsonSerializer.Serialize(error);
        Assert.DoesNotContain("   at ", json);
        Assert.DoesNotContain("Exception", json);
        return (obj.StatusCode!.Value, error);
    }

    [Fact]
    public async Task Controller_valid_prompt_returns_200_with_essence()
    {
        var h = CreateHarness(hasKey: true, Reply(ValidEssence));

        var result = await CreateController(h).Generate(new GenerateEssenceRequest(
            "Deploy a small Ubuntu VM", h.CloudAccountId, "openai"));

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<GenerateEssenceResponse>(ok.Value);
        Assert.Null(AiEssenceBuilderService.ValidateEssenceJson(body.EssenceJson));
    }

    [Fact]
    public async Task Controller_missing_key_returns_422_standard_shape()
    {
        var h = CreateHarness(hasKey: false);

        var (status, error) = AssertErrorResult(await CreateController(h).Generate(
            new GenerateEssenceRequest("Deploy a VM", h.CloudAccountId, "openai")));

        Assert.Equal(422, status);
        Assert.Equal("generation_failed", error.Error);
        Assert.Equal(422, error.StatusCode);
        Assert.Contains("No API key configured", error.Message);
    }

    [Fact]
    public async Task Controller_malformed_output_returns_422_and_persists_no_essence()
    {
        var h = CreateHarness(hasKey: true, Reply("nope"), Reply("nope"), Reply("nope"));

        var (status, error) = AssertErrorResult(await CreateController(h).Generate(
            new GenerateEssenceRequest("Deploy a VM", h.CloudAccountId, "openai")));

        Assert.Equal(422, status);
        Assert.Equal("generation_failed", error.Error);
        Assert.Empty(h.Db.Essences);
    }

    [Fact]
    public async Task Controller_provider_timeout_returns_422_standard_shape()
    {
        var h = CreateHarness(hasKey: true, () => throw new TaskCanceledException("timed out", new TimeoutException()));

        var (status, error) = AssertErrorResult(await CreateController(h).Generate(
            new GenerateEssenceRequest("Deploy a VM", h.CloudAccountId, "openai")));

        Assert.Equal(422, status);
        Assert.Contains("request failed", error.Message);
    }

    [Fact]
    public async Task Controller_unknown_provider_returns_400()
    {
        var h = CreateHarness(hasKey: true);

        var (status, error) = AssertErrorResult(await CreateController(h).Generate(
            new GenerateEssenceRequest("Deploy a VM", h.CloudAccountId, "no-such-provider")));

        Assert.Equal(400, status);
        Assert.Equal("bad_request", error.Error);
        Assert.Contains("Unsupported LLM provider", error.Message);
    }

    [Fact]
    public async Task Controller_unknown_cloud_account_returns_400_before_any_provider_call()
    {
        var h = CreateHarness(hasKey: true, Reply(ValidEssence));

        var (status, error) = AssertErrorResult(await CreateController(h).Generate(
            new GenerateEssenceRequest("Deploy a VM", Guid.NewGuid(), "openai")));

        Assert.Equal(400, status);
        Assert.Equal("Cloud account not found.", error.Message);
        Assert.Equal(0, h.Transport.CallCount);
    }

    // ---- request-validation failures use the same error shape ----

    [Fact]
    public void Model_validation_failure_is_reported_in_standard_error_shape()
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        actionContext.ModelState.AddModelError("Prompt", "The Prompt field is required.");

        var result = Assert.IsType<BadRequestObjectResult>(InvalidModelStateResponse.Create(actionContext));

        var error = Assert.IsType<ErrorResponse>(result.Value);
        Assert.Equal("bad_request", error.Error);
        Assert.Equal("The Prompt field is required.", error.Message);
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void Model_validation_failure_without_message_falls_back_to_generic_text()
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        actionContext.ModelState.AddModelError("body", "   ");

        var result = Assert.IsType<BadRequestObjectResult>(InvalidModelStateResponse.Create(actionContext));

        Assert.Equal("Request is invalid.", Assert.IsType<ErrorResponse>(result.Value).Message);
    }
}
