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

        // Request bodies in call order, so tests can check what the model was told on a retry.
        public List<string> RequestBodies { get; } = new();

        public ScriptedTransport(IEnumerable<Func<HttpResponseMessage>> steps) => _steps = new(steps);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            RequestBodies.Add(request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult() ?? "");
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

    // Records formatted log lines so tests can check that details reach the log, not the client.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class Harness
    {
        public required AuraDbContext Db { get; init; }
        public required AiEssenceBuilderService Service { get; init; }
        public required ScriptedTransport Transport { get; init; }
        public required CapturingLogger<AiEssenceBuilderService> Logger { get; init; }
        public required Guid UserId { get; init; }
        public required Guid CloudAccountId { get; init; }
    }

    private static Harness CreateHarness(bool hasKey, params Func<HttpResponseMessage>[] steps)
    {
        var transport = new ScriptedTransport(steps);
        var provider = new OpenAiCompatibleLlmProvider(new HttpClient(transport), "openai", FakeApiUrl, "fake-model");
        return CreateHarnessWithProvider(hasKey, provider, transport);
    }

    private static Harness CreateHarnessWithProvider(bool hasKey, ILlmProvider provider, ScriptedTransport transport)
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

        var logger = new CapturingLogger<AiEssenceBuilderService>();
        var service = new AiEssenceBuilderService(
            new LlmProviderFactory(new ILlmProvider[] { provider }),
            new UserAiKeyService(db, crypto.Object),
            db,
            new EssenceValidator(),
            logger);

        return new Harness
        {
            Db = db, Service = service, Transport = transport, Logger = logger,
            UserId = userId, CloudAccountId = cloudAccountId
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

        Assert.Null(new EssenceValidator().Validate(result.EssenceJson));
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

        Assert.Null(new EssenceValidator().Validate(result.EssenceJson));
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
        new object[] { "{\"layers\":{\"a\":{\"isEnabled\":true,\"operationType\":\"CreateResourceGroup\",\"runPolicy\":\"7\"}}}", "unknown runPolicy" },
    };

    [Theory]
    [MemberData(nameof(InvalidOutputs))]
    public async Task Invalid_model_output_is_retried_then_rejected_and_nothing_is_persisted(string output, string expectedReason)
    {
        var h = CreateHarness(hasKey: true, Reply(output), Reply(output), Reply(output));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(h));

        Assert.Equal("The model returned an invalid essence after 3 attempts. Try rephrasing the prompt.", ex.Message);
        if (expectedReason.Length > 0)
            Assert.Contains(expectedReason, string.Join("\n", h.Logger.Messages));

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

        Assert.Equal("The AI provider returned HTTP 500. Try again later.", ex.Message);
        var log = await h.Db.Set<AiGenerationLog>().SingleAsync();
        Assert.False(log.Success);
        Assert.Equal(2, log.Iterations);
        Assert.Equal(10, log.InputTokens);   // the failed call reported no usage
        Assert.Equal(5, log.OutputTokens);
    }

    [Fact]
    public async Task Usage_is_logged_when_the_client_disconnects_mid_retry()
    {
        // The second provider call is aborted by the request's own token, as on a client disconnect.
        using var cts = new CancellationTokenSource();
        var h = CreateHarness(hasKey: true, Reply("not json"), () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Service.GenerateAsync(h.UserId, Request(h.CloudAccountId), Guid.Empty, cts.Token));

        var log = await h.Db.Set<AiGenerationLog>().SingleAsync();
        Assert.False(log.Success);
        Assert.Equal(2, log.Iterations);
        Assert.Equal(10, log.InputTokens);   // only the first call reported usage
        Assert.Equal(5, log.OutputTokens);
    }

    [Fact]
    public async Task Usage_is_logged_when_an_unexpected_exception_escapes_a_provider_call()
    {
        // Not an HTTP or cancellation failure, so the provider does not handle it; it escapes the loop.
        var provider = new Mock<ILlmProvider>();
        provider.Setup(p => p.ProviderName).Returns("openai");
        provider.SetupSequence(p => p.GenerateAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmCompletionResult("not json", 10, 5, "fake-model", true))
            .ThrowsAsync(new InvalidCastException("simulated provider bug"));
        var h = CreateHarnessWithProvider(hasKey: true, provider.Object, new ScriptedTransport(Array.Empty<Func<HttpResponseMessage>>()));

        await Assert.ThrowsAsync<InvalidCastException>(() => Generate(h));

        var log = await h.Db.Set<AiGenerationLog>().SingleAsync();
        Assert.False(log.Success);
        Assert.Equal(2, log.Iterations);
        Assert.Equal(10, log.InputTokens);
        Assert.Equal(5, log.OutputTokens);
    }

    [Fact]
    public void Unexpected_parser_exception_is_reported_as_invalid_output_not_thrown()
    {
        // ParseAndSortLayers only throws the types it documents today. Whatever a parser throws,
        // the retry loop must see a rejection, so nothing escapes ValidateEssenceJson.
        var reason = new EssenceValidator().Validate(
            ValidEssence, _ => throw new NullReferenceException("simulated parser bug"));

        Assert.NotNull(reason);
        Assert.Contains("simulated parser bug", reason);
    }

    // ---- (d) provider timeout / HTTP error ----

    [Fact]
    public async Task Provider_timeout_fails_gracefully_without_retry()
    {
        // HttpClient's timeout surfaces as TaskCanceledException; the provider maps it to a failed result.
        var h = CreateHarness(hasKey: true, () => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(h));

        Assert.Equal("The AI provider request failed. Try again.", ex.Message);
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

        Assert.Equal("The AI provider rejected the API key. Check the key in Account Settings.", ex.Message);
        Assert.DoesNotContain("invalid key", ex.Message);  // the upstream body stays in the log
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
            h.Service,
            new EssenceValidator(),
            Mock.Of<ILogger<EssencesController>>())
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
        Assert.Null(new EssenceValidator().Validate(body.EssenceJson));
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
        Assert.Equal("The AI provider request failed. Try again.", error.Message);
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
    public void Model_validation_failure_without_message_names_the_field()
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        actionContext.ModelState.AddModelError("Provider", "   ");

        var result = Assert.IsType<BadRequestObjectResult>(InvalidModelStateResponse.Create(actionContext));

        Assert.Equal("'Provider' has an invalid value.", Assert.IsType<ErrorResponse>(result.Value).Message);
    }

    // The JSON input formatter records a conversion failure as text, as it does here.
    [Fact]
    public void Deserializer_text_failure_names_the_field_and_drops_framework_internals()
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        actionContext.ModelState.TryAddModelError("CloudAccountId",
            "The JSON value could not be converted to System.Guid. Path: $.cloudAccountId | LineNumber: 0 | BytePositionInLine: 45.");

        var result = Assert.IsType<BadRequestObjectResult>(InvalidModelStateResponse.Create(actionContext));

        var message = Assert.IsType<ErrorResponse>(result.Value).Message;
        Assert.Equal("'CloudAccountId' has an invalid value.", message);
        Assert.DoesNotContain("System.", message);
        Assert.DoesNotContain("BytePosition", message);
    }

    [Fact]
    public void Deserializer_exception_failure_names_the_field_and_drops_framework_internals()
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        actionContext.ModelState.TryAddModelException("CloudAccountId", new JsonException("'x' is an invalid start of a value."));

        var result = Assert.IsType<BadRequestObjectResult>(InvalidModelStateResponse.Create(actionContext));

        Assert.Equal("'CloudAccountId' has an invalid value.", Assert.IsType<ErrorResponse>(result.Value).Message);
    }

    [Fact]
    public void Model_validation_choice_is_deterministic_and_prefers_field_errors()
    {
        // ModelState is a dictionary: the same errors must always yield the same message, and a
        // body-level error must not hide a field-specific one.
        ActionContext Build()
        {
            var context = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
            context.ModelState.AddModelError("request", "The request field is required.");
            context.ModelState.AddModelError("Provider", "The Provider field is required.");
            context.ModelState.AddModelError("Prompt", "The Prompt field is required.");
            return context;
        }

        for (var i = 0; i < 5; i++)
        {
            var message = Assert.IsType<ErrorResponse>(
                Assert.IsType<BadRequestObjectResult>(InvalidModelStateResponse.Create(Build())).Value).Message;
            Assert.Equal("The Prompt field is required.", message);
        }
    }

    // ---- operation types: an unknown type is rejected at generation and fed back to the model ----

    // The health-check layer names a type with no handler, as the GCP prompt once did.
    private static readonly string UnknownTypeEssence =
        ValidEssence.Replace("\"HttpHealthCheck\"", "\"DeployDeploymentManager\"");

    [Fact]
    public async Task Unknown_operation_type_is_rejected_and_the_retry_is_told_why()
    {
        var h = CreateHarness(hasKey: true, Reply(UnknownTypeEssence), Reply(ValidEssence));

        var result = await Generate(h);

        Assert.Equal(2, result.Iterations);
        Assert.Equal(2, h.Transport.CallCount);
        Assert.Contains("Unknown operationType", h.Transport.RequestBodies[1]);
        Assert.Contains("DeployDeploymentManager", h.Transport.RequestBodies[1]);
        Assert.Null(new EssenceValidator().Validate(result.EssenceJson));
        Assert.True((await h.Db.Set<AiGenerationLog>().SingleAsync()).Success);
    }

    [Fact]
    public async Task Unknown_operation_type_on_every_attempt_fails_and_keeps_the_type_out_of_the_client_message()
    {
        var h = CreateHarness(hasKey: true, Reply(UnknownTypeEssence), Reply(UnknownTypeEssence), Reply(UnknownTypeEssence));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(h));

        Assert.Equal("The model returned an invalid essence after 3 attempts. Try rephrasing the prompt.", ex.Message);
        Assert.DoesNotContain("DeployDeploymentManager", ex.Message);
        Assert.Equal(3, h.Transport.CallCount);
        Assert.Contains(h.Logger.Messages, m => m.Contains("DeployDeploymentManager"));
        Assert.False((await h.Db.Set<AiGenerationLog>().SingleAsync()).Success);
    }

    // ---- operation types: an unknown type is rejected when an essence is saved ----

    private static (EssencesController controller, Essence essence) WithStoredEssence(Harness h, string json)
    {
        var essence = new Essence
        {
            Id = Guid.NewGuid(), TenantId = Guid.Empty, Name = "demo",
            CloudAccountId = h.CloudAccountId, EssenceJson = json, CurrentVersion = 1
        };
        h.Db.Essences.Add(essence);
        h.Db.SaveChanges();
        return (CreateController(h), essence);
    }

    [Fact]
    public async Task Controller_create_rejects_unknown_operation_type_with_standard_error_shape()
    {
        var h = CreateHarness(hasKey: true);

        var result = await CreateController(h).Create(
            new CreateEssenceRequest("demo", h.CloudAccountId, UnknownTypeEssence));

        var (status, error) = AssertErrorResult(result);
        Assert.Equal(400, status);
        Assert.Equal("bad_request", error.Error);
        Assert.Contains("layer 'health-check' uses 'DeployDeploymentManager'", error.Message);
        Assert.Empty(h.Db.Essences);
    }

    [Fact]
    public async Task Controller_create_accepts_known_operation_types()
    {
        var h = CreateHarness(hasKey: true);

        var result = await CreateController(h).Create(
            new CreateEssenceRequest("demo", h.CloudAccountId, ValidEssence));

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Single(h.Db.Essences);
    }

    [Fact]
    public async Task Controller_update_rejects_unknown_operation_type_and_keeps_the_stored_json()
    {
        var h = CreateHarness(hasKey: true);
        var (controller, essence) = WithStoredEssence(h, ValidEssence);

        var result = await controller.Update(essence.Id, new UpdateEssenceRequest(null, null, UnknownTypeEssence));

        var (status, _) = AssertErrorResult(result);
        Assert.Equal(400, status);
        var stored = await h.Db.Essences.SingleAsync();
        Assert.Equal(ValidEssence, stored.EssenceJson);
        Assert.Equal(1, stored.CurrentVersion);
    }

    [Fact]
    public async Task Controller_clone_rejects_a_source_that_names_an_unknown_operation_type()
    {
        var h = CreateHarness(hasKey: true);
        var (controller, essence) = WithStoredEssence(h, UnknownTypeEssence);

        var result = await controller.Clone(essence.Id, new CloneEssenceRequest("copy"));

        var (status, _) = AssertErrorResult(result);
        Assert.Equal(400, status);
        Assert.Single(h.Db.Essences);
    }
}
