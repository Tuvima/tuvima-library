using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;

namespace MediaEngine.Identity;

public sealed class AccountExternalLoginService : IAccountExternalLoginService
{
    private readonly IAccountExternalLoginRepository _loginRepository;
    private readonly IAccountRepository _accountRepository;

    public AccountExternalLoginService(
        IAccountExternalLoginRepository loginRepository,
        IAccountRepository accountRepository)
    {
        ArgumentNullException.ThrowIfNull(loginRepository);
        ArgumentNullException.ThrowIfNull(accountRepository);

        _loginRepository = loginRepository;
        _accountRepository = accountRepository;
    }

    public Task<IReadOnlyList<AccountExternalLogin>> GetByAccountAsync(Guid accountId, CancellationToken ct = default) =>
        _loginRepository.GetByAccountAsync(accountId, ct);

    public Task<AccountExternalLogin?> ResolveAsync(string provider, string issuer, string subject, CancellationToken ct = default)
    {
        ValidateIdentity(provider, issuer, subject);
        return _loginRepository.GetByProviderSubjectAsync(provider.Trim(), NormalizeIssuer(issuer), subject.Trim(), ct);
    }

    public async Task<AccountExternalLogin> LinkAsync(
        Guid accountId,
        string provider,
        string issuer,
        string subject,
        string? email,
        string? displayName,
        CancellationToken ct = default,
        string? accountEmail = null)
    {
        ValidateIdentity(provider, issuer, subject);

        var account = await _accountRepository.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            throw new InvalidOperationException($"Account '{accountId}' was not found.");
        }

        var normalizedProvider = provider.Trim();
        var normalizedIssuer = NormalizeIssuer(issuer);
        var normalizedSubject = subject.Trim();
        var existing = await _loginRepository
            .GetByProviderSubjectAsync(normalizedProvider, normalizedIssuer, normalizedSubject, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException("That external sign-in account is already linked.");
        }

        var needsEmail = !account.HasEmail;
        (string Display, string Normalized)? addedEmail = null;
        if (needsEmail)
        {
            addedEmail = await ChooseAccountEmailAsync(account, email, accountEmail, ct).ConfigureAwait(false);
        }

        var login = new AccountExternalLogin
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Provider = normalizedProvider,
            Issuer = normalizedIssuer,
            Subject = normalizedSubject,
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            LinkedAt = DateTimeOffset.UtcNow,
        };

        if (addedEmail is { } chosen)
        {
            account.Email = chosen.Display;
            account.NormalizedEmail = chosen.Normalized;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            if (!await _accountRepository.UpdateAsync(account, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The account could not be updated.");
            }
        }

        await _loginRepository.InsertAsync(login, ct).ConfigureAwait(false);
        return login;
    }

    /// <summary>The provider's email when it is usable and free, otherwise the one the person typed; one of them is required.</summary>
    private async Task<(string Display, string Normalized)> ChooseAccountEmailAsync(
        Account account, string? providerEmail, string? typedEmail, CancellationToken ct)
    {
        if (TryNormalize(providerEmail, out var providerNormalized)
            && await IsFreeAsync(account, providerNormalized, ct).ConfigureAwait(false))
        {
            return (CanonicalAddress(providerEmail!), providerNormalized);
        }

        if (string.IsNullOrWhiteSpace(typedEmail))
        {
            throw new ArgumentException("Add an email to link this sign-in.");
        }

        if (!TryNormalize(typedEmail, out var typedNormalized))
        {
            throw new ArgumentException("Enter a valid email address.");
        }

        if (!await IsFreeAsync(account, typedNormalized, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("That email is already used.");
        }

        return (CanonicalAddress(typedEmail), typedNormalized);
    }

    private async Task<bool> IsFreeAsync(Account account, string normalized, CancellationToken ct)
    {
        var owner = await _accountRepository.GetByNormalizedEmailAsync(normalized, ct).ConfigureAwait(false);
        return owner is null || owner.Id == account.Id;
    }

    // The address as stored for display: the bare address, so Identity's user-name validation accepts it.
    private static string CanonicalAddress(string value) => new System.Net.Mail.MailAddress(value.Trim()).Address;

    private static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            normalized = new System.Net.Mail.MailAddress(value.Trim()).Address.ToUpperInvariant();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public Task<bool> UnlinkAsync(Guid id, CancellationToken ct = default) =>
        _loginRepository.DeleteAsync(id, ct);

    public Task<bool> RecordLoginAsync(Guid id, CancellationToken ct = default) =>
        _loginRepository.TouchLastLoginAsync(id, DateTimeOffset.UtcNow, ct);

    private static void ValidateIdentity(string provider, string issuer, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        if (provider.Length > 100)
        {
            throw new ArgumentException("Provider must be 100 characters or fewer.", nameof(provider));
        }

        if (issuer.Length > 300 || !Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Issuer must be an absolute HTTPS URL of 300 characters or fewer.", nameof(issuer));
        }

        if (subject.Length > 300)
        {
            throw new ArgumentException("Subject must be 300 characters or fewer.", nameof(subject));
        }
    }

    // OIDC issuer identifiers are exact values. Do not remove a trailing slash or
    // otherwise rewrite the validated issuer; doing so can merge distinct issuers.
    private static string NormalizeIssuer(string issuer) => issuer.Trim();
}
