using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Components.Details;

/// <summary>One group of the details page's Extras row: its name (null for files in no group), the cards shown, and how many it holds in all.</summary>
public sealed record ExtrasGroupView(string? Name, IReadOnlyList<DetailExtraCardViewModel> Shown, int Total);

/// <summary>
/// What the Extras row and the "Shown alongside" chips show (universal editor v5 item 16, D31): the row is grouped by the groups a person named,
/// at most <c>cards_per_group</c> cards each with a "See all" for the rest; a file shown alongside an episode or lesson also appears as a chip
/// under that row. The numbers come from the Engine (<c>config/editor.json</c>), so nothing here holds one.
/// </summary>
public static class ExtrasRowRules
{
    /// <summary>
    /// The row's groups in the order each first appears, files in no group last. A title with no group at all is one plain group with no name
    /// (so no heading). <paramref name="perGroup"/> of 0 or less means the Engine named no limit: every card shows.
    /// </summary>
    public static IReadOnlyList<ExtrasGroupView> Groups(IReadOnlyList<DetailExtraCardViewModel>? cards, int perGroup)
    {
        if (cards is null || cards.Count == 0)
        {
            return [];
        }

        IReadOnlyList<DetailExtraCardViewModel> Cap(List<DetailExtraCardViewModel> list) => perGroup > 0 ? [.. list.Take(perGroup)] : list;

        var named = new List<(string Name, List<DetailExtraCardViewModel> Cards)>();
        var ungrouped = new List<DetailExtraCardViewModel>();
        foreach (var card in cards)
        {
            if (string.IsNullOrWhiteSpace(card.Group))
            {
                ungrouped.Add(card);
                continue;
            }

            var at = named.FindIndex(group => string.Equals(group.Name, card.Group.Trim(), StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                named.Add((card.Group.Trim(), [card]));
            }
            else
            {
                named[at].Cards.Add(card);
            }
        }

        var groups = named.Select(group => new ExtrasGroupView(group.Name, Cap(group.Cards), group.Cards.Count)).ToList();
        if (ungrouped.Count > 0)
        {
            // With no named group the row is one plain group (no heading); beside named groups the rest sit under "More" (the row supplies the word).
            groups.Add(new ExtrasGroupView(named.Count == 0 ? null : string.Empty, Cap(ungrouped), ungrouped.Count));
        }

        return groups;
    }

    /// <summary>True when some group holds more cards than the row shows, so the row offers "See all".</summary>
    public static bool HasHidden(IReadOnlyList<ExtrasGroupView> groups) => groups.Any(group => group.Total > group.Shown.Count);

    /// <summary>
    /// The files shown alongside this row's episode or lesson: an episode is found by its season and episode number, a lesson (a video of a
    /// folder of videos) by its media asset, which the Engine put on the card. A missing file shows no chip.
    /// </summary>
    public static IReadOnlyList<DetailExtraCardViewModel> ShownAlongside(IReadOnlyList<DetailExtraCardViewModel>? cards, SequenceItemViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (cards is null || cards.Count == 0 || row.EpisodeContext is not { } context)
        {
            return [];
        }

        var episodeKey = context.SeasonNumber is { } season && context.EpisodeNumber is { } episode ? $"episode:{season}:{episode}" : null;
        var assetId = context.EpisodeAssetId == Guid.Empty ? null : context.EpisodeAssetId.ToString("D");
        return
        [
            .. cards.Where(card => !card.IsMissing
                && ((episodeKey is not null && string.Equals(card.AlongsideKey, episodeKey, StringComparison.OrdinalIgnoreCase))
                    || (assetId is not null && string.Equals(card.AlongsideAssetId, assetId, StringComparison.OrdinalIgnoreCase)))),
        ];
    }

    /// <summary>The chip's words: the role and the type badge ("Worksheet · PDF").</summary>
    public static string ChipText(DetailExtraCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return string.Join(" · ", new[] { card.RoleLabel, card.FileBadge }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>Where a card opens: the Extra viewer.</summary>
    public static string ViewerHref(string extraId) => $"/extras/{Uri.EscapeDataString(extraId)}";
}
