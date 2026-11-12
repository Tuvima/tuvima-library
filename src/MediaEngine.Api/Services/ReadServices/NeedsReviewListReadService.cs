using System.Text.Json;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Review;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// The one Needs Review list (universal editor v3, WP7 and D9): the review queue (items the Engine identified but is not sure about) and the
/// unmatched-files list (files it could not identify) read together, so a person has one place to look and one number to be told. A file is
/// never in both at once (the Engine clears the Unmatched mark when it raises a review item), so the two sources never repeat a row.
/// Each row carries its group, its own picture and, when its Work belongs to a family (a show, an album, a Series), the family; rows come
/// back in group order with a family's rows together (Needs Review redesign WP1). Items a person kept (Keep as it is) are hidden from the
/// normal list and returned on request. Vault libraries are never listed, and neither are Wikidata-only items (matched to a provider, only
/// the Wikidata link is missing; the Engine keeps retrying quietly).
/// </summary>
public sealed class NeedsReviewListReadService(
    IReviewQueueReadService reviews,
    IUnmatchedAssetRepository unmatched,
    IConfigurationLoader configuration,
    IVaultLibraryRepository vault,
    INeedsReviewRowContextReadService contexts) : INeedsReviewListReadService
{
    private static readonly string[] WikidataTriggers = ["MultipleQidMatches", "QidNoMatch"];

    /// <summary>The kind of a review item: only a failed retail match is "not matched"; every other trigger is the Engine being unsure.</summary>
    public static string KindFor(string? trigger) =>
        string.Equals(trigger, ReviewTrigger.RetailMatchFailed, StringComparison.Ordinal) ? NeedsReviewKinds.NotMatched : NeedsReviewKinds.NotSure;

    public async Task<NeedsReviewListDto> ListAsync(
        string? kind, string? libraryId, bool includeFiles, int skip, int take, bool keptOnly = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var names = await ListedLibrariesAsync(ct).ConfigureAwait(false);
        string? library = null;
        if (!string.IsNullOrWhiteSpace(libraryId))
        {
            library = names.Keys.FirstOrDefault(id => string.Equals(id, libraryId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (library is null)
            {
                // A library that is not listed (unknown, or a Vault library) holds nothing here.
                return new NeedsReviewListDto { Skip = skip, Take = take };
            }
        }

        var vaultIds = await vault.GetIdsAsync(ct).ConfigureAwait(false);
        var reviewItems = keptOnly
            ? await reviews.GetKeptAsync(int.MaxValue, library, ct).ConfigureAwait(false)
            : library is null
                ? await reviews.GetPendingAsync(int.MaxValue, ct).ConfigureAwait(false)
                : await reviews.GetPendingForLibraryAsync(int.MaxValue, library, ct).ConfigureAwait(false);
        var shownReviews = reviewItems.Where(item => !IsVaultItem(item, vaultIds) && !NeedsReviewGrouping.IsHidden(item.Trigger)).ToList();

        var files = new List<(UnmatchedAssetListItem File, string LibraryId, string LibraryName)>();
        if (includeFiles)
        {
            foreach (var (id, name) in names)
            {
                if (library is not null && !string.Equals(id, library, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var page = await unmatched.ListByLibraryAsync(id, includeKept: keptOnly, 0, int.MaxValue, ct).ConfigureAwait(false);
                files.AddRange(page.Items.Where(file => file.Asset.KeptAsIs == keptOnly).Select(file => (file, id, name)));
            }
        }

        // One batched read for the whole list: each row's family and the pictures the Engine can serve.
        var assetIds = shownReviews.Where(item => item.EntityType == "MediaAsset").Select(item => item.EntityId)
            .Concat(files.Select(file => file.File.Asset.AssetId)).Distinct().ToList();
        var workIds = shownReviews.Where(item => item.EntityType == "Work").Select(item => item.EntityId).Distinct().ToList();
        var context = await contexts.ReadAsync(assetIds, workIds, ct).ConfigureAwait(false);

        var rows = new List<NeedsReviewRowDto>();
        foreach (var item in shownReviews)
        {
            var itemLibrary = names.Keys.FirstOrDefault(id => string.Equals(id, item.LibraryId, StringComparison.OrdinalIgnoreCase));
            var itemContext = ContextFor(context, item);
            var bestGuess = BestGuessFor(item);
            rows.Add(new NeedsReviewRowDto
            {
                Key = $"review:{item.Id:D}",
                Kind = KindFor(item.Trigger),
                LibraryId = itemLibrary ?? item.LibraryId,
                LibraryName = itemLibrary is null ? null : names[itemLibrary],
                LastTriedAt = item.CreatedAt,
                BestGuess = bestGuess,
                Review = item,
                Group = NeedsReviewGrouping.GroupFor(item.Trigger, bestGuess is not null),
                CoverUrl = string.IsNullOrWhiteSpace(item.CoverUrl) ? itemContext?.CoverUrl : item.CoverUrl,
                FamilyKey = itemContext?.FamilyWorkId?.ToString("D"),
                FamilyTitle = itemContext?.FamilyTitle,
                FamilyWorkId = itemContext?.FamilyWorkId,
                FamilyCoverUrl = itemContext?.FamilyCoverUrl,
                Kept = keptOnly,
            });
        }

        foreach (var (file, id, name) in files)
        {
            context.ByAsset.TryGetValue(file.Asset.AssetId, out var fileContext);
            rows.Add(ToRow(file, id, name, fileContext, keptOnly));
        }

        var keptTotal = keptOnly ? rows.Count : await CountKeptAsync(library, includeFiles, names, ct).ConfigureAwait(false);
        var notMatched = rows.Count(row => row.Kind == NeedsReviewKinds.NotMatched);
        var filtered = Order(rows.Where(row => KindMatches(kind, row.Kind))).ToList();
        var offset = Math.Max(0, skip);
        var size = Math.Max(1, take);
        return new NeedsReviewListDto
        {
            Items = filtered.Skip(offset).Take(size).ToList(),
            Total = filtered.Count,
            NotMatchedTotal = notMatched,
            NotSureTotal = rows.Count - notMatched,
            GroupTotals = TotalsByGroup(filtered),
            KeptTotal = keptTotal,
            Skip = offset,
            Take = size,
        };
    }

    public async Task<ReviewCountResponse> CountAsync(bool includeFiles, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Counted from the very rows the list holds (after the same Vault and Wikidata-only filters), so the notifier can never say more than
        // the list shows.
        var vaultIds = await vault.GetIdsAsync(ct).ConfigureAwait(false);
        var pending = (await reviews.GetPendingAsync(int.MaxValue, ct).ConfigureAwait(false))
            .Where(item => !IsVaultItem(item, vaultIds) && !NeedsReviewGrouping.IsHidden(item.Trigger))
            .ToList();
        var reviewNotMatched = pending.Count(item => KindFor(item.Trigger) == NeedsReviewKinds.NotMatched);
        var reviewTotal = pending.Count;
        var files = 0;
        if (includeFiles)
        {
            var listed = await ListedLibrariesAsync(ct).ConfigureAwait(false);
            var counts = await unmatched.CountByLibraryAsync(ct).ConfigureAwait(false);
            files = counts.Where(count => listed.ContainsKey(count.Key)).Sum(count => count.Value);
        }

        return new ReviewCountResponse
        {
            PendingCount = reviewTotal + files,
            NotMatchedCount = reviewNotMatched + files,
            NotSureCount = reviewTotal - reviewNotMatched,
        };
    }

    // How many kept items the same library filter holds: kept review items, plus (for an administrator) kept files.
    private async Task<int> CountKeptAsync(string? library, bool includeFiles, Dictionary<string, string> listed, CancellationToken ct)
    {
        var total = await reviews.GetKeptCountAsync(library, ct).ConfigureAwait(false);
        if (includeFiles)
        {
            var counts = await unmatched.CountKeptByLibraryAsync(ct).ConfigureAwait(false);
            // The library ids are compared exactly, as ListByLibraryAsync does when it lists the kept files.
            total += listed.Keys
                .Where(id => library is null || string.Equals(id, library, StringComparison.Ordinal))
                .Sum(id => counts.GetValueOrDefault(id));
        }

        return total;
    }

    // Group order, then each family's rows together (families ordered by their most recent row), then newest first, then key.
    private static IEnumerable<NeedsReviewRowDto> Order(IEnumerable<NeedsReviewRowDto> rows)
    {
        var list = rows.ToList();
        var blockNewest = list
            .GroupBy(row => (row.Group, Block: row.FamilyKey ?? row.Key))
            .ToDictionary(group => group.Key, group => group.Max(row => row.LastTriedAt));
        return list
            .OrderBy(row => NeedsReviewGrouping.OrderOf(row.Group))
            .ThenByDescending(row => blockNewest[(row.Group, row.FamilyKey ?? row.Key)])
            .ThenBy(row => row.FamilyKey ?? row.Key, StringComparer.Ordinal)
            .ThenByDescending(row => row.LastTriedAt)
            .ThenBy(row => row.Key, StringComparer.Ordinal);
    }

    private static Dictionary<string, int> TotalsByGroup(IReadOnlyList<NeedsReviewRowDto> rows) => new()
    {
        [NeedsReviewGroups.Ready] = rows.Count(row => row.Group == NeedsReviewGroups.Ready),
        [NeedsReviewGroups.Choose] = rows.Count(row => row.Group == NeedsReviewGroups.Choose),
        [NeedsReviewGroups.NoMatch] = rows.Count(row => row.Group == NeedsReviewGroups.NoMatch),
        [NeedsReviewGroups.FileProblem] = rows.Count(row => row.Group == NeedsReviewGroups.FileProblem),
    };

    private static NeedsReviewRowContext? ContextFor(NeedsReviewRowContexts context, ReviewItemDto item)
    {
        if (item.EntityType == "MediaAsset" && context.ByAsset.TryGetValue(item.EntityId, out var byAsset))
        {
            return byAsset;
        }

        return item.EntityType == "Work" && context.ByWork.TryGetValue(item.EntityId, out var byWork) ? byWork : null;
    }

    // A review item whose file sits in a Vault library is never listed or counted here, whatever the source query returned.
    private static bool IsVaultItem(ReviewItemDto item, IReadOnlySet<string> vaultIds) =>
        !string.IsNullOrWhiteSpace(item.LibraryId) && vaultIds.Any(id => string.Equals(id, item.LibraryId.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool KindMatches(string? filter, string kind) =>
        string.IsNullOrWhiteSpace(filter)
        || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase)
        || string.Equals(filter, kind, StringComparison.OrdinalIgnoreCase);

    private static NeedsReviewRowDto ToRow(
        UnmatchedAssetListItem file, string libraryId, string libraryName, NeedsReviewRowContext? context, bool kept) => new()
        {
            Key = $"file:{file.Asset.AssetId:D}",
            Kind = NeedsReviewKinds.NotMatched,
            LibraryId = libraryId,
            LibraryName = libraryName,
            LastTriedAt = file.Asset.LastTriedAt,
            // Every unmatched file is "No match found": the Engine found nothing to suggest.
            Group = NeedsReviewGrouping.GroupFor(null, hasBestGuess: false),
            CoverUrl = context?.CoverUrl,
            FamilyKey = context?.FamilyWorkId?.ToString("D"),
            FamilyTitle = context?.FamilyTitle,
            FamilyWorkId = context?.FamilyWorkId,
            FamilyCoverUrl = context?.FamilyCoverUrl,
            NextRetryAt = file.Asset.NextRetryAt,
            Kept = kept,
            Unmatched = new UnmatchedItemDto
            {
                AssetId = file.Asset.AssetId,
                LibraryId = libraryId,
                Title = file.Title,
                FileName = Path.GetFileName(file.FilePath),
                Folder = Path.GetDirectoryName(file.FilePath),
                WorkId = file.WorkId,
                Reason = file.Asset.Reason,
                FirstUnmatchedAt = file.Asset.FirstUnmatchedAt,
                LastTriedAt = file.Asset.LastTriedAt,
                NextRetryAt = file.Asset.NextRetryAt,
                KeptAsIs = file.Asset.KeptAsIs,
            },
        };

    // The one-click "Use this": the film's TV series when the Engine found one, the provider match when only Wikidata found several
    // entities, or the Engine's own linked match when it was not sure enough to accept it by itself. Every other row has no single answer
    // to offer, so it has no best guess and Fix starts from the file name instead.
    private static NeedsReviewBestGuessDto? BestGuessFor(ReviewItemDto item)
    {
        if (string.Equals(item.Trigger, ReviewTrigger.MovieMatchedAsTv, StringComparison.Ordinal))
        {
            var suggestion = ReadSuggestion(item.CandidatesJson);
            return suggestion is null
                ? null
                : new NeedsReviewBestGuessDto
                {
                    Action = "move_to_tv",
                    Name = suggestion.Name,
                    Year = suggestion.FirstAirYear?.ToString(),
                    Source = "TMDB TV series",
                    ImageUrl = suggestion.PosterUrl,
                };
        }

        if (WikidataTriggers.Contains(item.Trigger, StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(item.EntityTitle))
        {
            item.DetectedFacts.TryGetValue("year", out var year);
            return new NeedsReviewBestGuessDto
            {
                Action = "keep_provider_match",
                Name = item.EntityTitle!,
                Year = year,
                Source = "Provider match",
                ImageUrl = item.CoverUrl,
            };
        }

        // A low-confidence item keeps the match the Engine found (the pipeline links the best retail match and only withholds acceptance), so
        // "Use this" confirms it. The title shown must be the match's own: the list falls back to the file name or the reason text when the
        // item has no title, so require a real title fact and a provider identifier, plus a confidence above zero.
        if (string.Equals(item.Trigger, ReviewTrigger.LowConfidence, StringComparison.Ordinal)
            && item.ConfidenceScore is > 0
            && !string.IsNullOrWhiteSpace(item.EntityTitle)
            && item.DetectedFacts.TryGetValue("title", out var matchedTitle) && !string.IsNullOrWhiteSpace(matchedTitle)
            && item.BridgeIdentifiers.Count > 0)
        {
            item.DetectedFacts.TryGetValue("year", out var matchedYear);
            return new NeedsReviewBestGuessDto
            {
                Action = "confirm_match",
                Name = item.EntityTitle!,
                Year = matchedYear,
                Source = "Provider match",
                ImageUrl = item.CoverUrl,
            };
        }

        return null;
    }

    private static MovieTvSuggestionDto? ReadSuggestion(string? candidatesJson)
    {
        if (string.IsNullOrWhiteSpace(candidatesJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<MovieTvSuggestionDto>>(candidatesJson)?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Name));
        }
        catch (JsonException)
        {
            // A suggestion that cannot be read is simply no best guess; the row still lists and Fix still works.
            return null;
        }
    }

    // The libraries the Libraries screens list: configured, and not a Vault library (the Vault is never named outside its own area).
    private async Task<Dictionary<string, string>> ListedLibrariesAsync(CancellationToken ct)
    {
        var vaultIds = await vault.GetIdsAsync(ct).ConfigureAwait(false);
        return configuration.LoadLibraries().Libraries
            .Where(library => !string.IsNullOrWhiteSpace(library.Id) && !vaultIds.Contains(library.Id))
            .GroupBy(library => library.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.First().Id, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
    }
}
