using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// The central check for plugin and source actions (Plugin architecture spec PA2). In order: who is asking against the item's audience,
/// the Vault rule, then the administrator's library limit. Every refusal is the same quiet "not for this" to the caller; the reason is
/// only for the Activity list. An item with no declaration is for administrators only (D1).
/// </summary>
public sealed class PluginAccessGate(
    PluginCatalog catalog,
    IConfigurationLoader configuration,
    IVaultLibraryRepository vault,
    IVaultSectionOnlineLookup onlineLookup,
    PluginLibraryLimitService limits) : IPluginAccessGate
{
    public async Task<PluginAccessOutcome> CheckAsync(PluginAccessQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Describe(query.ItemId) is not { } rules)
        {
            return PluginAccessOutcome.Unknown;
        }

        // A larger value is a wider audience: an item for Adults serves an administrator and an adult, never a child.
        if (query.Requester is { } requester && rules.Audience < requester)
        {
            return PluginAccessOutcome.NotInAudience;
        }

        var inVault = await IsInVaultAsync(query, ct).ConfigureAwait(false);
        if (rules.AdultOnly && !inVault)
        {
            // An adult-only source is the Vault's own and is never asked about an ordinary item.
            return PluginAccessOutcome.VaultBlocked;
        }

        if (inVault && !await VaultAllowsAsync(rules.VaultUse, query.LibraryId, ct).ConfigureAwait(false))
        {
            return PluginAccessOutcome.VaultBlocked;
        }

        return await limits.IsAllowedAsync(query.ItemId, query.LibraryId, ct).ConfigureAwait(false)
            ? PluginAccessOutcome.Allowed
            : PluginAccessOutcome.LibraryLimited;
    }

    /// <summary>The widest audience a person belongs to, worked out from their request. Anything that is not clearly an adult is treated as a child.</summary>
    public static PluginAudience RequesterOf(RequestAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.IsEffectiveAdministrator || authority.IsAdministratorApplication)
        {
            return PluginAudience.Administrators;
        }

        // A person counts as an adult only while their profile really belongs to the account's household and, on a paired app, the app is
        // still enabled. A profile that cannot be resolved is not "not restricted"; it is simply not trusted.
        var validPerson = authority.IsAuthenticated && authority.HasHumanContext && authority.AccountEnabled && authority.ActiveProfileInUserHousehold
            && (authority.PrincipalKind == PrincipalKind.Human
                || (authority.PrincipalKind == PrincipalKind.DelegatedUserClient && authority.ApplicationEnabled));
        return validPerson && !authority.ActiveProfileIsRestricted ? PluginAudience.Adults : PluginAudience.Everyone;
    }

    private async Task<bool> IsInVaultAsync(PluginAccessQuery query, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(query.LibraryId))
        {
            return (await vault.GetIdsAsync(ct).ConfigureAwait(false)).Contains(query.LibraryId);
        }

        return query.EntityId is { } id && id != Guid.Empty && await vault.IsInVaultAsync(id, ct).ConfigureAwait(false);
    }

    private async Task<bool> VaultAllowsAsync(PluginVaultUse use, string? libraryId, CancellationToken ct) => use switch
    {
        PluginVaultUse.LocalOnly or PluginVaultUse.OwnSource => true,
        // Without the library the section's setting cannot be read, so the answer is no.
        PluginVaultUse.FollowsSectionLookup => !string.IsNullOrWhiteSpace(libraryId) && await onlineLookup.IsOnAsync(libraryId, ct).ConfigureAwait(false),
        _ => false,
    };

    private (PluginAudience Audience, PluginVaultUse VaultUse, bool AdultOnly)? Describe(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        if (catalog.Get(itemId) is { } registration)
        {
            var resolved = registration.Manifest.ResolvedDeclaration;
            return (resolved.Audience, resolved.VaultUse, false);
        }

        if (configuration.LoadProvider(itemId) is { } provider)
        {
            var resolved = PluginDeclarationRules.Resolve(provider.Declaration, provider.Endpoints.Count > 0, null, null);
            // A source that serves only the Vault is its own source there, unless it says "never".
            var vaultUse = provider.AdultOnly && resolved.VaultUse != PluginVaultUse.Never ? PluginVaultUse.OwnSource : resolved.VaultUse;
            return (resolved.Audience, vaultUse, provider.AdultOnly);
        }

        return null;
    }
}
