namespace MediaEngine.Identity.Contracts;

/// <summary>Keeps authenticator keys encrypted at rest (the Engine uses its data protection key ring).</summary>
public interface ITwoStepSecretProtector
{
    string Protect(string base32Secret);

    /// <summary>The key, or <c>null</c> when it cannot be read (for example the key ring was replaced).</summary>
    string? Unprotect(string protectedSecret);
}
