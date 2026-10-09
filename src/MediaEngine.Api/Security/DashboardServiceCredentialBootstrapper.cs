using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;

namespace MediaEngine.Api.Security;

public sealed record DashboardServiceCredentialOptions(string ConfigDirectory)
{
    public const string Purpose = "dashboard-engine";
    public const string ProtectorPurpose = "Tuvima.DashboardEngineCredential.v1";
    public string CredentialPath => Path.Combine(ConfigDirectory, ".secrets", "dashboard-engine.credential.json");
}

public sealed class DashboardServiceCredentialBootstrapper(
    IIdentityRepository identities,
    IDataProtectionProvider protectionProvider,
    DashboardServiceCredentialOptions options,
    DashboardServiceCredentialRecognizer recognizer)
{
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        var protector = protectionProvider.CreateProtector(DashboardServiceCredentialOptions.ProtectorPurpose);
        var path = Path.GetFullPath(options.CredentialPath);
        var active = await identities.GetActiveServiceCredentialAsync(DashboardServiceCredentialOptions.Purpose, ct).ConfigureAwait(false);

        if (active is not null && File.Exists(path))
        {
            var bundle = JsonSerializer.Deserialize<DashboardServiceCredentialBundle>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))
                ?? throw new InvalidOperationException("The Dashboard service credential bundle is invalid.");
            var token = protector.Unprotect(bundle.ProtectedToken);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(active.TokenHash),
                    Encoding.UTF8.GetBytes(HashToken(token))))
            {
                throw new InvalidOperationException("The Dashboard service credential bundle does not match the Engine database.");
            }

            recognizer.Remember(token);
            return;
        }

        if (active is not null)
        {
            await identities.RevokeServiceCredentialsAsync(DashboardServiceCredentialOptions.Purpose, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }

        var plaintext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var credential = new ServiceCredential
        {
            Id = Guid.NewGuid(),
            Purpose = DashboardServiceCredentialOptions.Purpose,
            KeyId = Guid.NewGuid().ToString("N"),
            TokenHash = HashToken(plaintext),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await identities.InsertServiceCredentialAsync(credential, ct).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bundleToWrite = new DashboardServiceCredentialBundle(credential.KeyId, protector.Protect(plaintext), credential.CreatedAt);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(bundleToWrite), ct).ConfigureAwait(false);
        File.Move(temporaryPath, path, overwrite: true);
        recognizer.Remember(plaintext);
    }

    private static string HashToken(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
