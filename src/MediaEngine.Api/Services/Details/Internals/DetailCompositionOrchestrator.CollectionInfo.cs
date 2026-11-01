using System.Globalization;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    /// <summary>The kind of metadata pill that carries "You own 83 of 250" in the hero of an imported list's page.</summary>
    internal const string ListOwnedPillKind = "list_owned";

    /// <summary>
    /// What a set or an imported list adds to its collection page (Collections and lists spec 4g): the owned count and the
    /// "Not in your library" titles. A Kids profile gets neither, because what a list holds that the library does not is not theirs to see.
    /// Any other collection returns null.
    /// </summary>
    private async Task<DetailCollectionInfoViewModel?> LoadCollectionInfoAsync(
        Guid collectionId,
        string? collectionType,
        Guid? profileId,
        CancellationToken ct)
    {
        if (string.Equals(collectionType, "Set", StringComparison.OrdinalIgnoreCase))
        {
            return new DetailCollectionInfoViewModel { FillKind = CollectionFillKinds.Set, IsSet = true };
        }

        if (_collectionLists is null)
        {
            return null;
        }

        var source = await _collectionLists.GetSourceAsync(collectionId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var profile = profileId.HasValue && _profiles is not null
            ? await _profiles.GetByIdAsync(profileId.Value, ct).ConfigureAwait(false)
            : null;
        if (profile?.IsKidsProfile == true)
        {
            return new DetailCollectionInfoViewModel { FillKind = CollectionFillKinds.List };
        }

        var entries = await _collectionLists.GetEntriesAsync(collectionId, ct).ConfigureAwait(false);
        var missing = entries
            .Where(entry => entry.OnList && entry.Status == ListImportChoices.StatusUnmatched)
            .OrderBy(entry => entry.Position)
            .Select(entry => new DetailMissingTitleViewModel
            {
                Position = entry.Position,
                Title = entry.Title,
                Year = entry.Year,
                Creator = entry.Creator,
            })
            .ToList();
        return new DetailCollectionInfoViewModel
        {
            FillKind = CollectionFillKinds.List,
            List = new DetailCollectionListViewModel
            {
                SourceKind = source.SourceKind,
                SourceLabel = source.SourceLabel,
                LastRefreshedAt = source.LastRefreshedAt,
                EntryCount = source.EntryCount,
                OwnedCount = source.MatchedCount,
                UnmatchedCount = source.UnmatchedCount,
                Missing = missing,
            },
        };
    }

    /// <summary>"You own 83 of 250": the hero fact of an imported list's page. Nothing for any other page.</summary>
    private static IReadOnlyList<MetadataPill> WithListOwnedPill(
        IReadOnlyList<MetadataPill> metadata,
        DetailCollectionInfoViewModel? info)
    {
        if (info?.List is not { EntryCount: > 0 } list)
        {
            return metadata;
        }

        return
        [
            .. metadata,
            new MetadataPill
            {
                Label = $"You own {list.OwnedCount.ToString(CultureInfo.InvariantCulture)} of {list.EntryCount.ToString(CultureInfo.InvariantCulture)}",
                Kind = ListOwnedPillKind,
                Tooltip = list.SourceLabel,
            },
        ];
    }
}
