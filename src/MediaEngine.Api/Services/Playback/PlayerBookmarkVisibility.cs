using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Which of a title's timed bookmarks a profile sees in the player (unified player v2 D21, A11): its own (Mine), and the title's Everyone list,
/// which is the administrator's definitive set once the editor has made one and otherwise every profile's bookmarks pooled, same-spot duplicates
/// merged by the gap in <c>config/bookmarks.json</c>. The rule is "may see the title, may see its bookmarks": a file the profile cannot play
/// (Vault without an unlocked session, a Kid profile's limits) contributes nothing, and a Vault title has no pooled view at all. Lists, counts,
/// markers and frame addresses all come from this one read, so they cannot disagree.
/// </summary>
public sealed class PlayerBookmarkVisibility(
    EditorBookmarkRepository repository,
    BookmarkClosenessProvider closeness,
    IVaultLibraryRepository vault)
{
    /// <summary>One bookmark the profile may see and where it belongs.</summary>
    public sealed record Entry(EditorBookmarkRow Bookmark, bool IsYours, bool InEveryone);

    /// <summary>The profile's own timed bookmarks on the title and its Everyone list, each once, in play order.</summary>
    public async Task<IReadOnlyList<Entry>> ReadAsync(
        Guid profileId, Guid workId, IReadOnlySet<Guid>? authorizedAssetIds, CancellationToken ct = default)
    {
        var rows = (await repository.GetByWorksAsync([workId], authorizedAssetIds, ct).ConfigureAwait(false))
            .Where(row => row.PositionKind == BookmarkPooling.TimeKind)
            .ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var everyone = await EveryoneAsync(workId, rows, ct).ConfigureAwait(false);
        var inEveryone = everyone.Select(row => row.Id).ToHashSet();
        return
        [
            .. EditorBookmarkRepository.OrderForList(rows.Where(row => row.ProfileId == profileId || inEveryone.Contains(row.Id)))
                .Select(row => new Entry(row, row.ProfileId == profileId, inEveryone.Contains(row.Id))),
        ];
    }

    private async Task<IReadOnlyList<EditorBookmarkRow>> EveryoneAsync(Guid workId, IReadOnlyList<EditorBookmarkRow> rows, CancellationToken ct)
    {
        // A Vault file has no pooled view: what other profiles bookmarked there is not shown to anyone else (a work can hold both kinds of file).
        var vaultAssets = new HashSet<Guid>();
        foreach (var assetId in rows.Select(row => row.AssetId).Distinct())
        {
            if (await vault.IsInVaultAsync(assetId, ct).ConfigureAwait(false))
            {
                vaultAssets.Add(assetId);
            }
        }

        var definitive = (await repository.GetDefinitiveAsync([workId], ct).ConfigureAwait(false)).Contains(workId);
        // The same function the editor uses, so the two cannot disagree about what Everyone holds.
        return EditorBookmarkRepository.Everyone(rows, definitive, _ => closeness.Current.Time.SameSpot, vaultAssets.Contains);
    }
}
