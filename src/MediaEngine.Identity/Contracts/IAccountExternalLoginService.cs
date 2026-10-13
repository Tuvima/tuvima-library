using MediaEngine.Domain.Entities;

namespace MediaEngine.Identity.Contracts;

public interface IAccountExternalLoginService
{
    Task<IReadOnlyList<AccountExternalLogin>> GetByAccountAsync(Guid accountId, CancellationToken ct = default);

    Task<AccountExternalLogin?> ResolveAsync(string provider, string issuer, string subject, CancellationToken ct = default);

    /// <summary>
    /// Links an outside sign-in to the account. An outside sign-in works away from this computer, so an account with no
    /// email is given one: the provider's email when it supplies a usable one, otherwise <paramref name="accountEmail"/>
    /// (an <see cref="ArgumentException"/> when neither is available, <see cref="InvalidOperationException"/> when the
    /// email already belongs to another account).
    /// </summary>
    Task<AccountExternalLogin> LinkAsync(
        Guid accountId,
        string provider,
        string issuer,
        string subject,
        string? email,
        string? displayName,
        CancellationToken ct = default,
        string? accountEmail = null);

    Task<bool> UnlinkAsync(Guid id, CancellationToken ct = default);

    Task<bool> RecordLoginAsync(Guid id, CancellationToken ct = default);
}
