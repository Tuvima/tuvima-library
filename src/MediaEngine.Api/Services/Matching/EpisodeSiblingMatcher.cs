using System.Globalization;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services.Matching;

/// <summary>
/// "Match the other episodes in this season by number" (universal editor v3, WP4 and D4): once one episode is matched into a show, the
/// other episodes in its folder season take the same season of that show by their own episode numbers. A read says how many that is
/// (<see cref="PreviewAsync"/>); the edit session's <c>sibling_match</c> change does it (<see cref="ApplyAsync"/>) through the very write the
/// retail-match action uses, one episode at a time, never moving an episode out of its folder.
/// </summary>
public interface IEpisodeSiblingMatcher
{
    /// <summary>The other episodes of the item's folder season and the episode of the chosen season each would take. Null when the item is not a TV episode.</summary>
    Task<EpisodeSiblingMatchPreviewDto?> PreviewAsync(Guid entityId, string seriesId, int seasonNumber, LibraryMoveAccess canEdit, CancellationToken ct);

    /// <summary>The plain-English reason the match cannot be done, or null when it can.</summary>
    Task<string?> CheckAsync(Guid entityId, EpisodeSiblingMatchTarget target, LibraryMoveAccess canEdit, CancellationToken ct);

    /// <summary>Matches every other episode that has its number in the chosen season. Throws, naming how many were matched, when any fails.</summary>
    Task ApplyAsync(Guid entityId, EpisodeSiblingMatchTarget target, LibraryMoveAccess canEdit, CancellationToken ct);
}

/// <summary>The rules of the by-number match that need no provider, kept apart so they can be tested directly.</summary>
public static class EpisodeSiblingMatchPlan
{
    /// <summary>The most episodes one folder season is read for (a season longer than this is matched in the first part and the rest left).</summary>
    public const int MaxSiblings = 100;

    /// <summary>One other episode of the folder season, as the owned-children read says it.</summary>
    public sealed record Sibling(Guid EntityId, string SourceFileName, string? Number);

    /// <summary>
    /// Pairs each other episode that has an episode number of 1 or more with the episode of the chosen season that has the same number.
    /// An episode without a number is left out altogether (nothing to match it by); one whose number the season does not have is kept as
    /// a row that was not found, so the person sees why it is not counted. Season 0 is an ordinary, explicit season.
    /// </summary>
    public static IReadOnlyList<EpisodeSiblingMatchRowDto> Pair(
        Guid chosenEntityId,
        IEnumerable<Sibling> siblings,
        IReadOnlyDictionary<int, TvMatchBrowserEpisodeDto> seasonEpisodesByNumber)
    {
        var rows = new List<EpisodeSiblingMatchRowDto>();
        foreach (var sibling in siblings)
        {
            if (sibling.EntityId == chosenEntityId
                || !int.TryParse(sibling.Number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                || number < 1)
            {
                continue;
            }

            var found = seasonEpisodesByNumber.TryGetValue(number, out var target);
            rows.Add(new EpisodeSiblingMatchRowDto
            {
                EntityId = sibling.EntityId,
                SourceFileName = sibling.SourceFileName,
                EpisodeNumber = number,
                TargetEpisodeId = found ? target!.EpisodeId : null,
                TargetTitle = found ? target!.Title : null,
                Found = found,
            });
        }

        return rows.OrderBy(row => row.EpisodeNumber).ThenBy(row => row.SourceFileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The retail-match request for an episode of the chosen season: the same one the Match tool stages for a chosen episode.</summary>
    public static ReplaceRetailMatchRequestDto RequestFor(TvMatchBrowserEpisodeDto episode) => new()
    {
        TargetKind = "item",
        TargetFieldGroup = "show_episode",
        TargetScopeId = "episode",
        ProviderId = WellKnownProviders.Tvdb.ToString("D"),
        ProviderName = "tvdb",
        ProviderItemId = episode.EpisodeId,
        CoverUrl = episode.ImageUrl,
        RequiredFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.ShowName] = episode.ShowName,
            [MetadataFieldConstants.SeasonNumber] = episode.SeasonNumber.ToString(CultureInfo.InvariantCulture),
            [MetadataFieldConstants.EpisodeNumber] = episode.EpisodeNumber.ToString(CultureInfo.InvariantCulture),
            [MetadataFieldConstants.EpisodeTitle] = episode.Title,
        },
        BridgeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BridgeIdKeys.TvdbId] = episode.SeriesId,
            [BridgeIdKeys.TvdbEpisodeId] = episode.EpisodeId,
        },
    };
}

/// <inheritdoc cref="IEpisodeSiblingMatcher"/>
public sealed class EpisodeSiblingMatcher(
    IItemCanonicalRepository items,
    IWorkRepository works,
    IMediaEditorOwnedChildReadService ownedChildren,
    ProviderApiClient tvdb,
    IRetailMatchWriter writer,
    ILogger<EpisodeSiblingMatcher> logger) : IEpisodeSiblingMatcher
{
    public async Task<EpisodeSiblingMatchPreviewDto?> PreviewAsync(
        Guid entityId, string seriesId, int seasonNumber, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var context = await items.ResolveWorkAssetContextAsync(entityId, ct).ConfigureAwait(false);
        if (context is null || !string.Equals(context.MediaType, "TV", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var lineage = await works.GetLineageByAssetAsync(context.AssetId, ct).ConfigureAwait(false);
        if (lineage is null)
        {
            return null;
        }

        var preview = new EpisodeSiblingMatchPreviewDto { EntityId = entityId, SeriesId = seriesId, SeasonNumber = seasonNumber };
        var show = await tvdb.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", seriesId)), ct).ConfigureAwait(false);
        var season = TvMatchBrowserMapper.MapSeasons(show, seriesId, null).FirstOrDefault(item => item.SeasonNumber == seasonNumber);
        if (show is null || season is null)
        {
            return preview;
        }

        var english = await tvdb.GetAsync("tvdb", "series_translation", ProviderApiClient.Args(("id", seriesId)), ct).ConfigureAwait(false);
        var showName = Text(english, "name") ?? Text(show, "name") ?? $"TheTVDB series {seriesId}";
        preview.ShowName = showName;

        var ordered = await tvdb.GetAllPagesAsync("tvdb", "series_episodes",
            ProviderApiClient.Args(("id", seriesId), ("season_type", "default"), ("lang", "eng")), ct).ConfigureAwait(false);
        var episodes = TvMatchBrowserMapper.MapEpisodes(
            TvMatchBrowserMapper.SelectSeasonEpisodes(ordered, season), seriesId, showName, season, null, new Dictionary<string, Guid>());
        var byNumber = episodes.GroupBy(episode => episode.EpisodeNumber).ToDictionary(group => group.Key, group => group.First());

        var siblings = lineage.ParentWorkId is { } parent
            ? await ownedChildren.SearchAsync(parent, null, 1, EpisodeSiblingMatchPlan.MaxSiblings, null, null, null, null, null, ct, null, true)
                .ConfigureAwait(false)
            : null;
        var candidates = (siblings?.Items ?? [])
            .Where(item => item.WorkId != lineage.WorkId && item.WorkId != entityId)
            .Select(item => new EpisodeSiblingMatchPlan.Sibling(item.WorkId, item.SourceFileName, item.MatchedNumber))
            .ToList();

        foreach (var row in EpisodeSiblingMatchPlan.Pair(entityId, candidates, byNumber))
        {
            // A file the person may not change is left out, as the save would leave it out.
            if (!await canEdit(row.EntityId, LibraryMoveRight.Match, ct).ConfigureAwait(false))
            {
                continue;
            }

            preview.Rows.Add(row);
        }

        preview.MatchableCount = preview.Rows.Count(row => row.Found);
        return preview;
    }

    public async Task<string?> CheckAsync(Guid entityId, EpisodeSiblingMatchTarget target, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        if (!tvdb.IsConfigured("tvdb"))
        {
            return "Connect TheTVDB in Settings before matching the other episodes.";
        }

        var preview = await PreviewAsync(entityId, target.SeriesId, target.SeasonNumber, canEdit, ct).ConfigureAwait(false);
        return preview is null ? "This item is not a TV episode." : null;
    }

    public async Task ApplyAsync(Guid entityId, EpisodeSiblingMatchTarget target, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var preview = await PreviewAsync(entityId, target.SeriesId, target.SeasonNumber, canEdit, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This item is not a TV episode.");
        var episodes = await ReadSeasonEpisodesAsync(preview, ct).ConfigureAwait(false);

        var matched = 0;
        var failures = new List<string>();
        foreach (var row in preview.Rows.Where(row => row.Found && row.TargetEpisodeId is not null))
        {
            if (!episodes.TryGetValue(row.EpisodeNumber, out var episode))
            {
                continue;
            }

            var outcome = IdentityEditSessionChangeHandlerOutcome(
                await writer.ReplaceWithoutMovingAsync(row.EntityId, EpisodeSiblingMatchPlan.RequestFor(episode), ct).ConfigureAwait(false));
            if (outcome is null)
            {
                matched++;
            }
            else
            {
                logger.LogWarning("Episode {EntityId} could not be matched by number to episode {Number}: {Reason}", row.EntityId, row.EpisodeNumber, outcome);
                failures.Add($"{row.SourceFileName}: {outcome}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Matched {matched} of {matched + failures.Count} other episodes. {failures[0]}");
        }
    }

    // The same episode records the preview paired, read again for the save so each request carries the full identity.
    private async Task<IReadOnlyDictionary<int, TvMatchBrowserEpisodeDto>> ReadSeasonEpisodesAsync(EpisodeSiblingMatchPreviewDto preview, CancellationToken ct)
    {
        var show = await tvdb.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", preview.SeriesId)), ct).ConfigureAwait(false);
        var season = TvMatchBrowserMapper.MapSeasons(show, preview.SeriesId, null).FirstOrDefault(item => item.SeasonNumber == preview.SeasonNumber);
        if (season is null)
        {
            return new Dictionary<int, TvMatchBrowserEpisodeDto>();
        }

        var ordered = await tvdb.GetAllPagesAsync("tvdb", "series_episodes",
            ProviderApiClient.Args(("id", preview.SeriesId), ("season_type", "default"), ("lang", "eng")), ct).ConfigureAwait(false);
        return TvMatchBrowserMapper.MapEpisodes(
                TvMatchBrowserMapper.SelectSeasonEpisodes(ordered, season), preview.SeriesId, preview.ShowName, season, null, new Dictionary<string, Guid>())
            .GroupBy(episode => episode.EpisodeNumber)
            .ToDictionary(group => group.Key, group => group.First());
    }

    private static string? IdentityEditSessionChangeHandlerOutcome(IResult result)
    {
        var (ok, reason) = EditSession.IdentityEditSessionChangeHandler.ReadOutcome(result);
        return ok ? null : reason;
    }

    private static string? Text(System.Text.Json.Nodes.JsonNode? node, string key) =>
        string.IsNullOrWhiteSpace(node?[key]?.ToString()) ? null : node![key]!.ToString().Trim();
}
