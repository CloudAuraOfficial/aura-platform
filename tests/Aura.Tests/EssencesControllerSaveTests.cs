using Aura.Api.Controllers;
using Aura.Api.Services;
using Aura.Core.Interfaces;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Aura.Tests;

// Save, update and clone reject an essence that run creation would reject, with the standard error body.
public class EssencesControllerSaveTests
{
    private const string ValidJson =
        """
        {
          "layers": {
            "create-rg": { "isEnabled": true, "operationType": "CreateResourceGroup", "parameters": {}, "dependsOn": [] }
          }
        }
        """;

    private const string CyclicJson =
        """
        {
          "layers": {
            "a": { "isEnabled": true, "operationType": "CreateResourceGroup", "parameters": {}, "dependsOn": ["b"] },
            "b": { "isEnabled": true, "operationType": "CreateResourceGroup", "parameters": {}, "dependsOn": ["a"] }
          }
        }
        """;

    private const string NoEnabledLayersJson =
        """
        {
          "layers": {
            "off": { "isEnabled": false, "operationType": "CreateResourceGroup", "parameters": {}, "dependsOn": [] }
          }
        }
        """;

    private static (EssencesController Controller, AuraDbContext Db) Build(Guid tenantId, string dbName)
    {
        var db = new AuraDbContext(
            new DbContextOptionsBuilder<AuraDbContext>().UseInMemoryDatabase(dbName).Options,
            new FakeTenant(tenantId));
        var validator = new EssenceValidator();
        var aiBuilder = new AiEssenceBuilderService(
            Mock.Of<ILlmProviderFactory>(), null!, db, validator, NullLogger<AiEssenceBuilderService>.Instance);
        var controller = new EssencesController(
            db, new FakeTenant(tenantId), Mock.Of<IAuditService>(), aiBuilder, validator,
            NullLogger<EssencesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (controller, db);
    }

    private static async Task<Guid> SeedAccountAsync(AuraDbContext db, Guid tenantId)
    {
        var account = new CloudAccount
        {
            TenantId = tenantId, Provider = CloudProvider.Azure, Label = "Test", EncryptedCredentials = "enc"
        };
        db.CloudAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static void AssertStandardBadRequest(IActionResult result, string messageContains)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(bad.Value);
        Assert.Equal("bad_request", error.Error);
        Assert.Equal(400, error.StatusCode);
        Assert.Contains(messageContains, error.Message);
    }

    [Fact]
    public async Task Create_rejects_a_dependency_cycle_with_the_standard_400_and_stores_nothing()
    {
        var tenant = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (controller, db) = Build(tenant, dbName);
        var accountId = await SeedAccountAsync(db, tenant);

        var result = await controller.Create(new CreateEssenceRequest("draft", accountId, CyclicJson));

        AssertStandardBadRequest(result, "Cycle detected in layer dependencies.");
        Assert.Empty(await db.Essences.ToListAsync());
    }

    [Fact]
    public async Task Create_stores_a_paused_essence_with_no_enabled_layers()
    {
        var tenant = Guid.NewGuid();
        var (controller, db) = Build(tenant, Guid.NewGuid().ToString());
        var accountId = await SeedAccountAsync(db, tenant);

        var result = await controller.Create(new CreateEssenceRequest("paused", accountId, NoEnabledLayersJson));

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Single(await db.Essences.ToListAsync());
    }

    [Fact]
    public async Task Create_stores_an_essence_with_an_empty_layers_object()
    {
        var tenant = Guid.NewGuid();
        var (controller, db) = Build(tenant, Guid.NewGuid().ToString());
        var accountId = await SeedAccountAsync(db, tenant);

        var result = await controller.Create(new CreateEssenceRequest("empty", accountId, "{ \"layers\": {} }"));

        Assert.IsType<CreatedAtActionResult>(result);
    }

    [Fact]
    public async Task Create_rejects_duplicate_layer_names_with_a_400_not_a_500()
    {
        var tenant = Guid.NewGuid();
        var (controller, db) = Build(tenant, Guid.NewGuid().ToString());
        var accountId = await SeedAccountAsync(db, tenant);
        const string duplicateJson =
            """
            {
              "layers": {
                "create-rg": { "isEnabled": true, "operationType": "CreateResourceGroup", "parameters": {}, "dependsOn": [] },
                "create-rg": { "isEnabled": true, "operationType": "CreateResourceGroup", "parameters": {}, "dependsOn": [] }
              }
            }
            """;

        var result = await controller.Create(new CreateEssenceRequest("dup", accountId, duplicateJson));

        AssertStandardBadRequest(result, "Duplicate layer name 'create-rg'.");
        Assert.Empty(await db.Essences.ToListAsync());
    }

    [Fact]
    public async Task Update_accepts_pausing_an_essence_by_disabling_every_layer()
    {
        var tenant = Guid.NewGuid();
        var (controller, db) = Build(tenant, Guid.NewGuid().ToString());
        var accountId = await SeedAccountAsync(db, tenant);
        var created = Assert.IsType<CreatedAtActionResult>(
            await controller.Create(new CreateEssenceRequest("draft", accountId, ValidJson)));
        var essenceId = ((EssenceResponse)created.Value!).Id;

        var result = await controller.Update(essenceId, new UpdateEssenceRequest(null, null, NoEnabledLayersJson));

        Assert.IsType<OkObjectResult>(result);
        var stored = await db.Essences.AsNoTracking().SingleAsync(e => e.Id == essenceId);
        Assert.Equal(2, stored.CurrentVersion);
    }

    [Fact]
    public async Task Clone_of_a_paused_essence_is_accepted()
    {
        var tenant = Guid.NewGuid();
        var (controller, db) = Build(tenant, Guid.NewGuid().ToString());
        var accountId = await SeedAccountAsync(db, tenant);
        var source = new Essence
        {
            TenantId = tenant, Name = "paused", CloudAccountId = accountId, EssenceJson = NoEnabledLayersJson, CurrentVersion = 1
        };
        db.Essences.Add(source);
        await db.SaveChangesAsync();

        var result = await controller.Clone(source.Id, new CloneEssenceRequest("copy"));

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(2, await db.Essences.CountAsync());
    }

    [Fact]
    public async Task Clone_of_an_invalid_source_says_the_source_is_at_fault()
    {
        var tenant = Guid.NewGuid();
        var (controller, db) = Build(tenant, Guid.NewGuid().ToString());
        var accountId = await SeedAccountAsync(db, tenant);
        var source = new Essence
        {
            TenantId = tenant, Name = "broken", CloudAccountId = accountId, EssenceJson = CyclicJson, CurrentVersion = 1
        };
        db.Essences.Add(source);
        await db.SaveChangesAsync();

        var result = await controller.Clone(source.Id, new CloneEssenceRequest("copy"));

        AssertStandardBadRequest(result, "Source essence is invalid: Cycle detected in layer dependencies.");
        Assert.Single(await db.Essences.ToListAsync());
    }

    [Fact]
    public async Task Create_stores_a_valid_essence()
    {
        var tenant = Guid.NewGuid();
        var (controller, db) = Build(tenant, Guid.NewGuid().ToString());
        var accountId = await SeedAccountAsync(db, tenant);

        var result = await controller.Create(new CreateEssenceRequest("draft", accountId, ValidJson));

        Assert.IsType<CreatedAtActionResult>(result);
        var stored = Assert.Single(await db.Essences.ToListAsync());
        Assert.Equal(1, stored.CurrentVersion);
    }

    [Fact]
    public async Task Update_rejects_an_invalid_edit_and_leaves_the_stored_essence_and_versions_unchanged()
    {
        var tenant = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (controller, db) = Build(tenant, dbName);
        var accountId = await SeedAccountAsync(db, tenant);
        var created = Assert.IsType<CreatedAtActionResult>(
            await controller.Create(new CreateEssenceRequest("draft", accountId, ValidJson)));
        var essenceId = ((EssenceResponse)created.Value!).Id;

        var result = await controller.Update(essenceId, new UpdateEssenceRequest(null, null, CyclicJson));

        AssertStandardBadRequest(result, "Cycle detected in layer dependencies.");
        var stored = await db.Essences.AsNoTracking().SingleAsync(e => e.Id == essenceId);
        Assert.Equal(ValidJson, stored.EssenceJson);
        Assert.Equal(1, stored.CurrentVersion);
        Assert.Single(await db.EssenceVersions.Where(v => v.EssenceId == essenceId).ToListAsync());
    }

    [Fact]
    public async Task Update_rejects_an_essence_that_is_not_json()
    {
        var tenant = Guid.NewGuid();
        var (controller, db) = Build(tenant, Guid.NewGuid().ToString());
        var accountId = await SeedAccountAsync(db, tenant);
        var created = Assert.IsType<CreatedAtActionResult>(
            await controller.Create(new CreateEssenceRequest("draft", accountId, ValidJson)));
        var essenceId = ((EssenceResponse)created.Value!).Id;

        var result = await controller.Update(essenceId, new UpdateEssenceRequest(null, null, "{ not json"));

        AssertStandardBadRequest(result, "Essence JSON is not valid.");
    }
}
