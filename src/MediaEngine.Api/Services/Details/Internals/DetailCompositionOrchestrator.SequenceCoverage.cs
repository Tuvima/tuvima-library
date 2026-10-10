using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Entities;
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
    {
        return new SequenceItemViewModel
        {
            Id = item.Id,
            EntityType = item.EntityType,
            Title = item.Title,
            ArtworkUrl = item.ArtworkUrl,
            EpisodeStillUrl = item.EpisodeStillUrl,
            EpisodeStillWidthPx = item.EpisodeStillWidthPx,
            EpisodeStillHeightPx = item.EpisodeStillHeightPx,
            Route = item.Route,
            Description = item.Description,
            Duration = item.Duration,
            PublicationDate = item.PublicationDate,
            PositionNumber = item.PositionNumber,
            PositionSort = item.PositionSort,
            PositionLabel = item.PositionLabel,
            PositionText = item.PositionText,
            GroupKey = item.GroupKey,
            GroupTitle = item.GroupTitle,
            MembershipScope = item.MembershipScope,
            IsCurrent = item.IsCurrent,
            IsOwned = item.IsOwned,
            ProgressState = item.ProgressState,
            EpisodeContext = item.EpisodeContext,
            ProgressPercent = item.ProgressPercent,
            PositionSeconds = item.PositionSeconds,
            DurationSeconds = item.DurationSeconds,
            RemainingSeconds = item.RemainingSeconds,
            ProgressLabel = item.ProgressLabel,
            CoveredWith = coveredWith,
        };
    }
}
