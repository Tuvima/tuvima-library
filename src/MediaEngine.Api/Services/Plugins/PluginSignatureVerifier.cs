using System.Security.Cryptography;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Checks the signature Tuvima gives a Verified or Official plugin package (Developer tools spec WP19). A signature is an
/// ECDSA P-256 / SHA-256 signature over the exact bytes of the package, written as base64 in the plugin directory list.
/// The public keys that count are the ones built in here; a package nobody signed with one of them is not Verified.
/// With no key built in, nothing verifies, so a Verified or Official package is refused rather than trusted by default.
/// </summary>
public sealed class PluginSignatureVerifier
{
    /// <summary>
    /// The Tuvima release public keys (base64 SubjectPublicKeyInfo). Empty until Tuvima publishes its first signed
    /// plugin; every signature check fails closed until a key is added here.
    /// </summary>
    public static readonly IReadOnlyList<string> TuvimaReleaseKeys = [];

    private const int MaxSignatureTextLength = 512;

    private readonly IReadOnlyList<string> _publicKeys;

    public PluginSignatureVerifier()
        : this(TuvimaReleaseKeys)
    {
    }

    public PluginSignatureVerifier(IReadOnlyList<string> publicKeys)
    {
        _publicKeys = publicKeys;
    }

    /// <summary>True when at least one trusted key is available, so a signature can be checked at all.</summary>
    public bool HasKeys => _publicKeys.Count > 0;

    /// <summary>True when the package bytes carry a valid signature from a trusted key. Anything unreadable counts as not valid.</summary>
    public bool Verify(ReadOnlySpan<byte> package, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature) || signature.Length > MaxSignatureTextLength)
        {
            return false;
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        foreach (var key in _publicKeys)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.VerifyData(package, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                // A key that cannot be read, or a signature of the wrong shape for it, simply does not verify.
            }
        }

        return false;
    }
}
