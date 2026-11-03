using System.Text.Json;
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
/// Kept-as-is files and Vault libraries are never listed.
/// </summary>
public sealed class NeedsReviewListReadService(
    IReviewQueueReadService reviews,
    IUnmatchedAssetRepository unmatched,
    IConfigurationLoader configuration,
    IVaultLibraryRepository vault) : INeedsReviewListReadService
{
    private static readonly string[] WikidataTriggers = ["WikidataBridgeFailed", "MissingQid", "MultipleQidMatches", "QidNoMatch"];

    /// <summary>The kind of a review item: only a failed retail match is "not matched"; every other trigger is the Engine being unsure.</summary>
    public static string KindFor(string? trigger) =>
        string.Equals(trigger, ReviewTrigger.RetailMatchFailed, StringComparison.Ordinal) ? NeedsReviewKinds.NotMatched : NeedsReviewKinds.NotSure;

    public async Task<NeedsReviewListDto> ListAsync(string? kind, string? libraryId, bool includeFiles, int skip, int take, CancellationToken ct = default)
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

        var rows = new List<NeedsReviewRowDto>();
        var pending = library is null
            ? await reviews.GetPendingAsync(int.MaxValue, ct).ConfigureAwait(false)
            : await reviews.GetPendingForLibraryAsync(int.MaxValue, library, ct).ConfigureAwait(false);
        foreach (var item in pending)
        {
            var itemLibrary = names.Keys.FirstOrDefault(id => string.Equals(id, item.LibraryId, StringComparison.OrdinalIgnoreCase));
            rows.Add(new NeedsReviewRowDto
            {
                Key = $"review:{item.Id:D}",
                Kind = KindFor(item.Trigger),
                LibraryId = itemLibrary ?? item.LibraryId,
                LibraryName = itemLibrary is null ? null : names[itemLibrary],
                LastTriedAt = item.CreatedAt,
                BestGuess = BestGuessFor(item),
                Review = item,
            });
        }

        if (includeFiles)
        {
            foreach (var (id, name) in names)
            {
                if (library is not null && !string.Equals(id, library, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var page = await unmatched.ListByLibraryAsync(id, includeKept: false, 0, int.MaxValue, ct).ConfigureAwait(false);
                rows.AddRange(page.Items.Select(file => ToRow(file, id, name)));
            }
        }

        var notMatched = rows.Count(row => row.Kind == NeedsReviewKinds.NotMatched);
        var filtered = rows.Where(row => KindMatches(kind, row.Kind))
            .OrderByDescending(row => row.LastTriedAt)
            .ThenBy(row => row.Key, StringComparer.Ordinal)
            .ToList();
        var offset = Math.Max(0, skip);
        var size = Math.Max(1, take);
        return new NeedsReviewListDto
        {
            Items = filtered.Skip(offset).Take(size).ToList(),
            Total = filtered.Count,
            NotMatchedTotal = notMatched,
            NotSureTotal = rows.Count - notMatched,
            Skip = offset,
            Take = size,
        };
    }

    public async Task<ReviewCountResponse> CountAsync(bool includeFiles, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var reasons = await reviews.GetPendingReasonCountsAsync(ct).ConfigureAwait(false);
        var reviewNotMatched = reasons.Where(reason => KindFor(reason.Trigger) == NeedsReviewKinds.NotMatched).Sum(reason => reason.Count);
        var reviewTotal = reasons.Sum(reason => reason.Count);
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

    private static bool KindMatches(string? filter, string kind) =>
        string.IsNullOrWhiteSpace(filter)
        || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase)
        || string.Equals(filter, kind, StringComparison.OrdinalIgnoreCase);

    private static NeedsReviewRowDto ToRow(UnmatchedAssetListItem file, string libraryId, string libraryName) => new()
    {
        Key = $"file:{file.Asset.AssetId:D}",
        Kind = NeedsReviewKinds.NotMatched,
        LibraryId = libraryId,
        LibraryName = libraryName,
        LastTriedAt = file.Asset.LastTriedAt,
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

    // The one-click "Use this": the film's TV series when the Engine found one, or the provider match when only Wikidata could not confirm it.
    // Every other row has no single answer to offer, so it has no best guess and Fix starts from the file name instead.
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
