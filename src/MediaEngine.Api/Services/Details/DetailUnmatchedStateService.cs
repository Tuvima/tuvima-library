using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Details;

/// <summary>
/// Adds the Unmatched state of a single-item page's own files (Library setup spec D34, D40): the list of files and the "..." menu entries
/// that act on them. Only a viewer who passes the administrator check the page's other actions use gets any of it, and a Vault file never
/// does (the Vault is never named outside its own area). Everyone else gets the page exactly as composed.
/// </summary>
public sealed class DetailUnmatchedStateService(IUnmatchedAssetRepository unmatched, IVaultLibraryRepository vault)
{
    internal async Task ApplyAsync(
        DetailPageViewModel detail,
        IReadOnlyList<Guid>? authorizedAssetIds,
        DetailActionAuthorizationContext authorization,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(detail);

        // The check comes first: a viewer who may not act reads nothing from the Unmatched list.
        if (authorizedAssetIds is not { Count: > 0 } || !authorization.Allows(DetailUnmatchedActions.RetryKey))
        {
            return;
        }

        var rows = await unmatched.ListByAssetsAsync(authorizedAssetIds, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return;
        }

        var vaultIds = await vault.GetIdsAsync(ct).ConfigureAwait(false);
        var own = rows
            .Where(row => !vaultIds.Contains(row.LibraryId))
            .OrderBy(row => row.AssetId)
            .Select(row => new DetailUnmatchedAssetViewModel { AssetId = row.AssetId, KeptAsIs = row.KeptAsIs })
            .ToList();
        if (own.Count == 0)
        {
            return;
        }

        detail.UnmatchedAssets = own;
        detail.OverflowActions =
        [
            .. detail.OverflowActions,
            .. DetailUnmatchedActions.For(own).Where(action => authorization.Allows(action.Key)),
        ];
    }
}
