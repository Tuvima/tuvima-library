using System.Globalization;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services.Matching;

/// <summary>
/// "Re-match seasons and episodes" (universal editor v3, D8 item 3): once a show is matched to a different TheTVDB show, every episode the
/// library holds under it is matched again against the new show by its own season and episode number, through the very write the
/// retail-match action uses, never moving a file. An episode the new show has no slot for is not left quietly on the old show: it gets a
/// Needs Review item that says which slot was missing. Seasons need no match of their own, they follow the show and their episodes' numbers.
/// </summary>
public interface IShowChildRematcher
{
    /// <summary>How many episodes the re-match covers and how many the chosen show has a slot for. Null when the item is not a TV show.</summary>
    Task<ShowChildRematchPreviewDto?> PreviewAsync(Guid showEntityId, string seriesId, LibraryMoveAccess canEdit, CancellationToken ct);

    /// <summary>The plain-English reason the re-match cannot be done, or null when it can.</summary>
    Task<string?> CheckAsync(Guid showEntityId, ShowChildRematchTarget target, LibraryMoveAccess canEdit, CancellationToken ct);

    /// <summary>Matches each episode that has a slot and sends the rest to Needs Review. Throws, naming how many were matched, when a match fails.</summary>
    Task ApplyAsync(Guid showEntityId, ShowChildRematchTarget target, LibraryMoveAccess canEdit, CancellationToken ct);
}

/// <summary>The rules of the show re-match that need no provider, kept apart so they can be tested directly.</summary>
public static class ShowChildRematchPlan
{
    /// <summary>The most episodes one show is re-matched in a single save (a longer show is matched in its first part and the rest left).</summary>
    public const int MaxEpisodes = 500;

    /// <summary>One episode the library holds under the show, as the owned-children read says it.</summary>
    public sealed record Child(Guid WorkId, Guid AssetId, string SourceFileName, int? Season, string? Number);

    /// <summary>One episode and the slot of the chosen show it takes (<see cref="Target"/> null when the show has none).</summary>
    public sealed record Row(Child Child, int Season, int Episode, TvMatchBrowserEpisodeDto? Target)
    {
        public bool Found => Target is not null;
    }

    /// <summary>
    /// Pairs each episode that has a season and an episode number of 1 or more with the chosen show's episode of the same numbers. An
    /// episode without both numbers is left out (nothing to match it by). Season 0 is an ordinary, explicit season.
    /// </summary>
    public static IReadOnlyList<Row> Pair(IEnumerable<Child> children, IReadOnlyDictionary<(int Season, int Episode), TvMatchBrowserEpisodeDto> slots)
    {
        var rows = new List<Row>();
        foreach (var child in children)
        {
            if (child.Season is not { } season || season < 0
                || !int.TryParse(child.Number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode)
                || episode < 1)
            {
                continue;
            }

            rows.Add(new Row(child, season, episode, slots.GetValueOrDefault((season, episode))));
        }

        return rows.OrderBy(row => row.Season).ThenBy(row => row.Episode).ThenBy(row => row.Child.SourceFileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The plain sentence on the Needs Review item of an episode the new show has no slot for.</summary>
    public static string MissingSlotDetail(string showName, int season, int episode) =>
        $"{showName} has no season {season.ToString(CultureInfo.InvariantCulture)} episode {episode.ToString(CultureInfo.InvariantCulture)}, so this episode could not be matched to it by number.";
}

/// <inheritdoc cref="IShowChildRematcher"/>
public sealed class ShowChildRematcher(
    IMediaEditorOwnedChildReadService ownedChildren,
    ProviderApiClient tvdb,
    IRetailMatchWriter writer,
    IReviewQueueRepository reviews,
    ILogger<ShowChildRematcher> logger) : IShowChildRematcher
{
    public async Task<ShowChildRematchPreviewDto?> PreviewAsync(Guid showEntityId, string seriesId, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var plan = await PlanAsync(showEntityId, seriesId, canEdit, ct).ConfigureAwait(false);
        if (plan is null)
        {
            return null;
        }

        return new ShowChildRematchPreviewDto
        {
            EntityId = showEntityId,
            SeriesId = seriesId,
            ShowName = plan.ShowName,
            EpisodeCount = plan.Rows.Count,
            MatchableCount = plan.Rows.Count(row => row.Found),
            NotFoundCount = plan.Rows.Count(row => !row.Found),
        };
    }

    public async Task<string?> CheckAsync(Guid showEntityId, ShowChildRematchTarget target, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        if (!tvdb.IsConfigured("tvdb"))
        {
            return "Connect TheTVDB in Settings before re-matching the seasons and episodes.";
        }

        return await PlanAsync(showEntityId, target.SeriesId, canEdit, ct).ConfigureAwait(false) is null ? "This item is not a TV show." : null;
    }

    public async Task ApplyAsync(Guid showEntityId, ShowChildRematchTarget target, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var plan = await PlanAsync(showEntityId, target.SeriesId, canEdit, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This item is not a TV show.");

        var matched = 0;
        var sentToReview = 0;
        var failures = new List<string>();
        foreach (var row in plan.Rows)
        {
            if (row.Target is { } slot)
            {
                var (ok, reason) = Api.Services.EditSession.IdentityEditSessionChangeHandler.ReadOutcome(
                    await writer.ReplaceWithoutMovingAsync(row.Child.WorkId, EpisodeSiblingMatchPlan.RequestFor(slot), ct).ConfigureAwait(false));
                if (ok)
                {
                    matched++;
                }
                else
                {
                    logger.LogWarning("Episode {EntityId} could not be re-matched to season {Season} episode {Episode}: {Reason}", row.Child.WorkId, row.Season, row.Episode, reason);
                    failures.Add($"{row.Child.SourceFileName}: {reason}");
                }
            }
            else if (await SendToReviewAsync(row, plan.ShowName, ct).ConfigureAwait(false))
            {
                sentToReview++;
            }
        }

        logger.LogInformation("Show {EntityId} re-matched against TheTVDB {SeriesId}: {Matched} episodes matched, {Review} sent to Needs Review.",
            showEntityId, target.SeriesId, matched, sentToReview);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Re-matched {matched} of {matched + failures.Count} episodes. {failures[0]}");
        }
    }

    private async Task<bool> SendToReviewAsync(ShowChildRematchPlan.Row row, string showName, CancellationToken ct)
    {
        try
        {
            var existing = await reviews.GetByEntityAsync(row.Child.AssetId, ct).ConfigureAwait(false);
            if (existing.Any(item => item.Status == ReviewStatus.Pending && item.Trigger == ReviewTrigger.RetailMatchFailed))
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            await reviews.InsertAsync(new ReviewQueueEntry
            {
                Id = Guid.NewGuid(),
                EntityId = row.Child.AssetId,
                EntityType = "MediaAsset",
                Trigger = ReviewTrigger.RetailMatchFailed,
                ConfidenceScore = 0.0,
                Detail = ShowChildRematchPlan.MissingSlotDetail(showName, row.Season, row.Episode),
                CreatedAt = now,
                ReviewReadyAt = now,
                AutomationCompletedAt = now,
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The episode keeps its old match either way; the save is not failed over the note that it needs a look.
            logger.LogWarning(ex, "Episode {EntityId} could not be sent to Needs Review after the show was re-matched.", row.Child.AssetId);
            return false;
        }
    }

    private sealed record Plan(string ShowName, IReadOnlyList<ShowChildRematchPlan.Row> Rows);

    private async Task<Plan?> PlanAsync(Guid showEntityId, string seriesId, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var children = new List<ShowChildRematchPlan.Child>();
        for (var page = 1; children.Count < ShowChildRematchPlan.MaxEpisodes; page++)
        {
            var found = await ownedChildren.SearchAsync(showEntityId, null, page, 100, null, null, null, null, null, ct).ConfigureAwait(false);
            if (found is null)
            {
                return null;
            }

            children.AddRange(found.Items.Select(item => new ShowChildRematchPlan.Child(item.WorkId, item.AssetId, item.SourceFileName, item.SeasonNumber, item.MatchedNumber)));
            if (found.Items.Count == 0 || page * 100 >= found.TotalCount)
            {
                break;
            }
        }

        var show = await tvdb.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", seriesId)), ct).ConfigureAwait(false);
        var english = await tvdb.GetAsync("tvdb", "series_translation", ProviderApiClient.Args(("id", seriesId)), ct).ConfigureAwait(false);
        var showName = Text(english, "name") ?? Text(show, "name") ?? $"TheTVDB series {seriesId}";
        var ordered = await tvdb.GetAllPagesAsync("tvdb", "series_episodes",
            ProviderApiClient.Args(("id", seriesId), ("season_type", "default"), ("lang", "eng")), ct).ConfigureAwait(false);

        var slots = new Dictionary<(int Season, int Episode), TvMatchBrowserEpisodeDto>();
        foreach (var season in TvMatchBrowserMapper.MapSeasons(show, seriesId, null))
        {
            foreach (var episode in TvMatchBrowserMapper.MapEpisodes(
                         TvMatchBrowserMapper.SelectSeasonEpisodes(ordered, season), seriesId, showName, season, null, new Dictionary<string, Guid>()))
            {
                slots.TryAdd((episode.SeasonNumber, episode.EpisodeNumber), episode);
            }
        }

        var rows = new List<ShowChildRematchPlan.Row>();
        foreach (var row in ShowChildRematchPlan.Pair(children.Take(ShowChildRematchPlan.MaxEpisodes), slots))
        {
            // A file the person may not change is left out, as the save would leave it out.
            if (!await canEdit(row.Child.WorkId, LibraryMoveRight.Match, ct).ConfigureAwait(false))
            {
                continue;
            }

            rows.Add(row);
        }

        return new Plan(showName, rows);
    }

    private static string? Text(System.Text.Json.Nodes.JsonNode? node, string key) =>
        string.IsNullOrWhiteSpace(node?[key]?.ToString()) ? null : node![key]!.ToString().Trim();
}
