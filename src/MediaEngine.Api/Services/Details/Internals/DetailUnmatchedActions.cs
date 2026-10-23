using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// The "..." menu's Unmatched entries for one item (Library setup spec D34, D40): Retry now and Keep as is while a file is Unmatched and not
/// kept, Look up again once every Unmatched file is kept. The Dashboard calls the existing Unmatched item routes; no new Engine action exists.
/// </summary>
internal static class DetailUnmatchedActions
{
    public const string RetryKey = "unmatched-retry";
    public const string KeepKey = "unmatched-keep";
    public const string LookUpAgainKey = "unmatched-look-up-again";

    /// <summary>The entries that fit the page's Unmatched files; empty when it has none.</summary>
    public static IEnumerable<DetailAction> For(IReadOnlyList<DetailUnmatchedAssetViewModel> unmatched)
    {
        if (unmatched.Count == 0)
        {
            yield break;
        }

        if (unmatched.Any(asset => !asset.KeptAsIs))
        {
            yield return new DetailAction { Key = RetryKey, Label = "Retry now", Icon = "refresh", IsAdminOnly = true };
            yield return new DetailAction { Key = KeepKey, Label = "Keep as is", Icon = "check", IsAdminOnly = true };
        }
        else
        {
            yield return new DetailAction { Key = LookUpAgainKey, Label = "Look up again", Icon = "search", IsAdminOnly = true };
        }
    }
}
