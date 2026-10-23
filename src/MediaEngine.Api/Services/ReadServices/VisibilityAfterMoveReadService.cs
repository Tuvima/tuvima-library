using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

/// <inheritdoc cref="IVisibilityAfterMoveReadService"/>
public sealed class VisibilityAfterMoveReadService(
    IAccountRepository accounts,
    IProfileRepository profiles,
    IMediaAssetLibraryCountRepository assets,
    IVaultLibraryRepository vaultLibraries,
    IConfigurationLoader configuration,
    LibraryVisibilityRule rule) : IVisibilityAfterMoveReadService
{
    /// <summary>The most files a list may name; a longer list is refused rather than cut short, so the answer is never partial.</summary>
    public const int MaxAssetIds = 5000;

    public async Task<VisibilityAfterMoveResult> GetAsync(VisibilityAfterMoveRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var libraries = configuration.LoadLibraries().Libraries;
        var target = libraries.FirstOrDefault(library =>
            library.Kind == LibraryKinds.Catalogued && Guid.TryParse(library.Id, out var id) && id == request.TargetLibraryId);
        if (target is null)
        {
            return VisibilityAfterMoveResult.Refused("That library is not one of your libraries.");
        }

        var vaultIds = await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false);
        if (IsVault(request.TargetLibraryId, target.Area, vaultIds))
        {
            return VisibilityAfterMoveResult.Refused("Files can't be moved into the Vault from here.");
        }

        var counts = await CountFilesAsync(request, libraries, ct).ConfigureAwait(false);
        if (counts.Refusal is { } refusal)
        {
            return VisibilityAfterMoveResult.Refused(refusal);
        }

        var byLibrary = counts.ByLibrary!;
        // Files already in the target are no different after the move; nothing else to ask when no file would change library.
        if (byLibrary.Keys.All(library => library == request.TargetLibraryId))
        {
            return new VisibilityAfterMoveResult([], null);
        }

        var areas = libraries
            .Where(library => Guid.TryParse(library.Id, out _))
            .ToDictionary(library => Guid.Parse(library.Id), library => library.Area);
        var lost = new List<VisibilityLossDto>();
        foreach (var account in await accounts.GetAllAsync(ct).ConfigureAwait(false))
        {
            // An administrator sees everything; a disabled sign-in cannot sign in, so a line about what it would lose helps no one.
            if (account.IsAdministrator || !account.IsEnabled)
            {
                continue;
            }

            var features = await accounts.GetFeatureGrantsAsync(account.Id, ct).ConfigureAwait(false);
            var hidden = await rule.LoadHiddenAsync(account.Id, ct).ConfigureAwait(false);
            var seesAfter = LibraryVisibilityRule.MaySee(false, false, HasArea(features, target.Area), hidden.Contains(request.TargetLibraryId));

            var count = 0;
            foreach (var (libraryId, files) in byLibrary)
            {
                // A library that is no longer configured has no area, so nobody counts as seeing it: the rule fails closed.
                var seesNow = areas.TryGetValue(libraryId, out var area)
                    && LibraryVisibilityRule.MaySee(false, IsVault(libraryId, area, vaultIds), HasArea(features, area), hidden.Contains(libraryId));
                if (seesNow && !seesAfter)
                {
                    count += files;
                }
            }

            if (count > 0)
            {
                lost.Add(new VisibilityLossDto(account.Id, await DisplayNameAsync(account, ct).ConfigureAwait(false), count));
            }
        }

        return new VisibilityAfterMoveResult(lost, null);
    }

    private async Task<(IReadOnlyDictionary<Guid, int>? ByLibrary, string? Refusal)> CountFilesAsync(
        VisibilityAfterMoveRequest request, IReadOnlyList<LibraryFolderConfig> libraries, CancellationToken ct)
    {
        var hasFolder = !string.IsNullOrWhiteSpace(request.SourceId) || !string.IsNullOrWhiteSpace(request.FolderPath);
        if (request.AssetIds is not null && hasFolder)
        {
            return (null, "Name the files either by a folder or by a list, not both.");
        }

        if (request.AssetIds is { } assetIds)
        {
            if (assetIds.Count == 0)
            {
                return (null, "Say which files to check: the list is empty.");
            }

            return assetIds.Count > MaxAssetIds
                ? (null, $"That is too many files to check at once (the most is {MaxAssetIds:N0}). Check a folder instead, or fewer files.")
                : (await assets.CountByLibraryForAssetsAsync(assetIds, ct).ConfigureAwait(false), null);
        }

        if (!string.IsNullOrWhiteSpace(request.SourceId) && !string.IsNullOrWhiteSpace(request.FolderPath))
        {
            return (null, "Name the folder either by its source or by its path, not both.");
        }

        string? path = request.FolderPath;
        var libraryId = request.LibraryId;
        if (!string.IsNullOrWhiteSpace(request.SourceId))
        {
            var owner = libraries.FirstOrDefault(library =>
                library.Sources.Any(source => string.Equals(source.Id, request.SourceId, StringComparison.Ordinal)));
            if (owner is null)
            {
                return (null, "That folder is not one of your folders.");
            }

            path = owner.Sources.First(source => string.Equals(source.Id, request.SourceId, StringComparison.Ordinal)).Path;
            libraryId = Guid.TryParse(owner.Id, out var ownerId) ? ownerId : null;
        }

        return string.IsNullOrWhiteSpace(path)
            ? (null, "Say which files to check: a folder or a list of files.")
            : (await assets.CountByLibraryInFolderAsync(path, libraryId, ct).ConfigureAwait(false), null);
    }

    private static bool IsVault(Guid libraryId, string area, IReadOnlySet<string> vaultIds) =>
        string.Equals(area, LibraryAreas.Vault, StringComparison.OrdinalIgnoreCase) || vaultIds.Contains(libraryId.ToString("D"));

    // An area that is not one of the four features (Vault, or text nobody recognises) is not an area anyone has.
    private static bool HasArea(IReadOnlySet<AccountFeatureId> features, string area) =>
        features.Any(feature => string.Equals(feature.Value, area, StringComparison.OrdinalIgnoreCase));

    private async Task<string> DisplayNameAsync(MediaEngine.Domain.Entities.Account account, CancellationToken ct)
    {
        if (await accounts.GetDefaultProfileIdAsync(account.Id, ct).ConfigureAwait(false) is { } profileId
            && await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false) is { } profile
            && !string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            return profile.DisplayName;
        }

        return string.IsNullOrWhiteSpace(account.Email) ? "Unnamed sign-in" : account.Email;
    }
}
