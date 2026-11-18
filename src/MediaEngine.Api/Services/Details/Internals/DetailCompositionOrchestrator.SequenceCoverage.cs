using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    private readonly record struct SameFileCandidate(Guid WorkId, string? PositionLabel, string? Title);

    /// <summary>
    /// For owned episodes that live in the same physical file as another listed episode,
    /// returns the other episodes of that file. One batched lookup per page (never one per
    /// card); a library with no combined files returns an empty map.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<SequenceCoverageLinkViewModel>>> LoadSameFileLinksAsync(
        IReadOnlyList<SameFileCandidate> candidates,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<SequenceCoverageLinkViewModel>>();
        var byWork = new Dictionary<Guid, SameFileCandidate>();
        foreach (var candidate in candidates)
        {
            byWork.TryAdd(candidate.WorkId, candidate);
        }

        if (byWork.Count < 2)
        {
            return result;
        }

        IReadOnlyList<MediaAssetCoverage> rows;
        try
        {
            rows = await new MediaAssetCoverageRepository(_db).ListByWorksAsync(byWork.Keys.ToList(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The chips are decoration; the episode list still renders without them.
            _logger?.LogWarning(ex, "Same-file episode lookup failed; episodes render without Same file chips.");
            return result;
        }

        foreach (var file in rows.GroupBy(row => row.AssetId).Where(group => group.Count() > 1))
        {
            var ordered = file.OrderBy(row => row.Position).ToList();
            foreach (var row in ordered)
            {
                var links = ordered
                    .Where(other => other.WorkId != row.WorkId && byWork.ContainsKey(other.WorkId))
                    .Select(other => new SequenceCoverageLinkViewModel
                    {
                        Id = other.WorkId.ToString("D"),
                        PositionLabel = byWork[other.WorkId].PositionLabel,
                        Title = byWork[other.WorkId].Title,
                    })
                    .ToList();
                if (links.Count > 0)
                {
                    result[row.WorkId] = links;
                }
            }
        }

        return result;
    }

    private async Task<List<SequenceItemViewModel>> ApplySameFileCoverageAsync(
        List<SequenceItemViewModel> items,
        CancellationToken ct)
    {
        var candidates = new List<SameFileCandidate>();
        foreach (var item in items)
        {
            if (item.IsOwned && Guid.TryParse(item.Id, out var workId))
            {
                candidates.Add(new SameFileCandidate(
                    workId,
                    item.PositionNumber?.ToString(CultureInfo.InvariantCulture) ?? item.PositionLabel,
                    item.Title));
            }
        }

        var links = await LoadSameFileLinksAsync(candidates, ct);
        if (links.Count == 0)
        {
            return items;
        }

        return items.Select(item =>
            item.IsOwned && Guid.TryParse(item.Id, out var workId) && links.TryGetValue(workId, out var siblings)
                ? CloneWithCoverage(item, siblings)
                : item).ToList();
    }

    private static SequenceItemViewModel CloneWithCoverage(
        SequenceItemViewModel item,
        IReadOnlyList<SequenceCoverageLinkViewModel> coveredWith)
        => CloneEntry(item, coveredWith);

    /// <summary>
    /// A file that covers several listed episodes becomes ONE entry, "E19-20: Title A + Title B" (review mock-ups 2026-11-18 D9).
    /// The entry keeps the first episode's id, route and file; each covered episode stays its own Work, so counts, ratings and
    /// next-episode logic are unchanged, and <see cref="SequenceItemViewModel.EpisodeCount"/> says how many episodes it stands for.
    /// Siblings that are not listed in the same season (or have no number) are left as they are, with the "Same file" chip.
    /// </summary>
    private static List<SequenceItemViewModel> MergeCombinedEpisodeEntries(List<SequenceItemViewModel> items)
    {
        if (!items.Any(item => item.IsOwned && item.CoveredWith.Count > 0))
        {
            return items;
        }

        var byId = new Dictionary<string, SequenceItemViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            byId.TryAdd(item.Id, item);
        }

        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<SequenceItemViewModel>(items.Count);
        foreach (var item in items)
        {
            if (consumed.Contains(item.Id))
            {
                continue;
            }

            if (!item.IsOwned || item.CoveredWith.Count == 0 || EpisodeNumberOf(item) is null)
            {
                merged.Add(item);
                continue;
            }

            var members = new List<SequenceItemViewModel> { item };
            foreach (var link in item.CoveredWith)
            {
                if (byId.TryGetValue(link.Id, out var sibling)
                    && !consumed.Contains(sibling.Id)
                    && !ReferenceEquals(sibling, item)
                    && sibling.IsOwned
                    && EpisodeNumberOf(sibling) is not null
                    && string.Equals(sibling.GroupKey, item.GroupKey, StringComparison.OrdinalIgnoreCase))
                {
                    members.Add(sibling);
                }
            }

            if (members.Count == 1)
            {
                merged.Add(item);
                continue;
            }

            foreach (var member in members.Skip(1))
            {
                consumed.Add(member.Id);
            }

            merged.Add(CombineEpisodes(members));
        }

        return merged;
    }

    private static int? EpisodeNumberOf(SequenceItemViewModel item)
        => item.PositionNumber ?? TryParseInt(item.PositionLabel);

    private static SequenceItemViewModel CombineEpisodes(IReadOnlyList<SequenceItemViewModel> members)
    {
        var ordered = members
            .OrderBy(member => EpisodeNumberOf(member) ?? int.MaxValue)
            .ToList();
        var first = ordered[0];
        var numbers = ordered.Select(member => EpisodeNumberOf(member)!.Value).ToList();
        var numberText = CombinedEpisodeFiles.NumberText(numbers);
        var others = ordered.Skip(1)
            .Select(member => new SequenceCoverageLinkViewModel
            {
                Id = member.Id,
                PositionLabel = member.PositionNumber?.ToString(CultureInfo.InvariantCulture) ?? member.PositionLabel,
                Title = member.Title,
                Route = member.Route,
            })
            .ToList();
        return CloneEntry(
            first,
            others,
            episodeCount: ordered.Count,
            title: CombinedEpisodeFiles.TitleText(ordered.Select(member => member.Title)),
            positionLabel: numberText.Replace("E", string.Empty, StringComparison.Ordinal),
            positionText: numberText,
            isCurrent: ordered.Any(member => member.IsCurrent));
    }

    /// <summary>How many owned episodes the entries stand for (a combined entry counts every episode its file covers).</summary>
    private static int OwnedEpisodeTotal(IEnumerable<SequenceItemViewModel> items)
        => items.Where(item => item.IsOwned).DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Sum(item => Math.Max(1, item.EpisodeCount));

    private static int CompletedEpisodeTotal(IEnumerable<SequenceItemViewModel> items)
        => items.Where(item => item.IsOwned && item.ProgressState == LibraryProgressState.Completed)
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Sum(item => Math.Max(1, item.EpisodeCount));

    /// <summary>How many physical files hold the owned entries; 0 when it equals the episode count (every episode is its own file).</summary>
    private static int FileTotalWhenCombined(IReadOnlyCollection<SequenceItemViewModel> items)
    {
        var files = items.Where(item => item.IsOwned).DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Count();
        return files < OwnedEpisodeTotal(items) ? files : 0;
    }

    private static SequenceItemViewModel CloneEntry(
        SequenceItemViewModel item,
        IReadOnlyList<SequenceCoverageLinkViewModel> coveredWith,
        int? episodeCount = null,
        string? title = null,
        string? positionLabel = null,
        string? positionText = null,
        bool? isCurrent = null)
    {
        return new SequenceItemViewModel
        {
            Id = item.Id,
            EntityType = item.EntityType,
            Title = title ?? item.Title,
            ArtworkUrl = item.ArtworkUrl,
            EpisodeStillUrl = item.EpisodeStillUrl,
            EpisodeStillWidthPx = item.EpisodeStillWidthPx,
            EpisodeStillHeightPx = item.EpisodeStillHeightPx,
            PictureIsStandIn = item.PictureIsStandIn,
            StandInRetryAfter = item.StandInRetryAfter,
            Route = item.Route,
            Description = item.Description,
            Duration = item.Duration,
            PublicationDate = item.PublicationDate,
            PositionNumber = item.PositionNumber,
            PositionSort = item.PositionSort,
            PositionLabel = positionLabel ?? item.PositionLabel,
            PositionText = positionText ?? item.PositionText,
            GroupKey = item.GroupKey,
            GroupTitle = item.GroupTitle,
            MembershipScope = item.MembershipScope,
            IsCurrent = isCurrent ?? item.IsCurrent,
            IsOwned = item.IsOwned,
            IsUnmatched = item.IsUnmatched,
            ProgressState = item.ProgressState,
            EpisodeContext = item.EpisodeContext,
            ProgressPercent = item.ProgressPercent,
            PositionSeconds = item.PositionSeconds,
            DurationSeconds = item.DurationSeconds,
            RemainingSeconds = item.RemainingSeconds,
            ProgressLabel = item.ProgressLabel,
            CoveredWith = coveredWith,
            EpisodeCount = episodeCount ?? item.EpisodeCount,
        };
    }
}
