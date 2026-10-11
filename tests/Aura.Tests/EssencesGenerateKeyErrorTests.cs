using System.Security.Claims;
using System.Security.Cryptography;
using Aura.Api.Controllers;
using Aura.Api.Services;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

public class EssencesGenerateKeyErrorTests
{
    private static AuraDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    private static EssencesController Controller(AuraDbContext db, Guid userId, Mock<ICryptoService> crypto)
    {
        var keySvc = new UserAiKeyService(db, crypto.Object, Mock.Of<ILogger<UserAiKeyService>>());
        var providers = new Mock<ILlmProviderFactory>();
        providers.Setup(f => f.SupportedProviders).Returns(new[] { "openrouter" });
        var aiBuilder = new AiEssenceBuilderService(
            providers.Object, keySvc, db, new EssenceValidator(), Mock.Of<ILogger<AiEssenceBuilderService>>());

        var tenant = new Mock<ITenantContext>();
        tenant.Setup(t => t.TenantId).Returns(Guid.Empty);

        var controller = new EssencesController(db, tenant.Object, Mock.Of<IAuditService>(), aiBuilder, new EssenceValidator(),
            Mock.Of<ILogger<EssencesController>>());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    // Reproduces gap G4 from #35: a stored key that can't be decrypted used to fall through to the
    // middleware and come back as a generic 500 internal_error. It must now be a 422 in the standard shape.
    [Fact]
    public async Task Generate_returns_422_stored_key_unreadable_when_saved_key_cannot_be_decrypted()
    {
        var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var cloudId = Guid.NewGuid();
        db.UserAiProviders.Add(new UserAiProvider
        {
            TenantId = Guid.Empty, UserId = userId, ProviderName = "openrouter", EncryptedApiKey = "from-another-secret"
        });
        db.CloudAccounts.Add(new CloudAccount
        {
            Id = cloudId, TenantId = Guid.Empty, Provider = CloudProvider.Azure, Label = "acct"
        });
        await db.SaveChangesAsync();

        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>())).Throws(new CryptographicException());
        var controller = Controller(db, userId, crypto);

        var result = await controller.Generate(
            new GenerateEssenceRequest("build me a vnet", cloudId, "openrouter"));

        var obj = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(obj.Value);
        Assert.Equal(422, error.StatusCode);
        Assert.Equal("stored_key_unreadable", error.Error);
        Assert.Equal("stored key for 'openrouter' can't be read, re-enter it in Account Settings.", error.Message);
    }
}
