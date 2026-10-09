using MediaEngine.Identity.Contracts;
using Microsoft.AspNetCore.DataProtection;

namespace MediaEngine.Api.Security;

/// <summary>
/// Keeps authenticator keys encrypted in the data store with the Engine's own data protection key ring, the same way
/// the Dashboard service credential and the Intercom tokens are protected. Nothing is sent anywhere.
/// </summary>
public sealed class DataProtectionTwoStepSecretProtector(IDataProtectionProvider provider) : ITwoStepSecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Tuvima.TwoStep.Secret.v1");

    public string Protect(string base32Secret) => _protector.Protect(base32Secret);

    public string? Unprotect(string protectedSecret)
    {
        try
        {
            return _protector.Unprotect(protectedSecret);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The key ring that sealed this key is gone; the person signs in with a recovery code or an administrator resets it.
            return null;
        }
    }
}
