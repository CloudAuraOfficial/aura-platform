using Aura.Core.DTOs;
using Aura.Core.Enums;

namespace Aura.Core.Interfaces;

public interface ICloudCredentialTester
{
    Task<CredentialTestResponse> TestAsync(
        CloudProvider provider,
        string credentialsJson,
        CancellationToken ct = default);
}
