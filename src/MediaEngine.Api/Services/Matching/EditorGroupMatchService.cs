using System.Globalization;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services.Matching;

/// <summary>
/// Match on several ticked episodes or songs (universal editor v5, D36): one show and season, or one release, is picked once, and every
/// ticked item is lined up by its own number. Each line carries the record the item would take, the same verdict a single pick gets, and
/// where applying it would put the item and its file. An item that does not line up, or whose verdict is Check match, comes back with
/// <see cref="EditorGroupMatchLineDto.Fits"/> false so the Dashboard leaves it unticked. A read: nothing is written until Save changes.
/// </summary>
public sealed class EditorGroupMatchService(
    EditorMatchSearchService search,
    EditorMatchLocalEvidenceReader reader,
    IEditorMatchKindResolver kinds,
    EditorMatchPlacementService placement,
    EditorMusicMatchService music,
    ProviderApiClient tvdb)
{
    private const string TvdbProvider = "tvdb";

    /// <summary>The refusal sentence for a request that cannot be lined up, or null. Checked before any provider is asked.</summary>
    public static string? Refusal(EditorGroupMatchRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EntityIds.Count == 0)
        {
            return "Tick the episodes or songs to match first.";
        }

        if (request.EntityIds.Count > LibraryBatchEditRequest.MaxEntityIds)
        {
            return $"You can match up to {LibraryBatchEditRequest.MaxEntityIds} items at once.";
        }

        return request.Kind switch
        {
            EditorGroupMatchKinds.Episodes when request.SeriesId is not { Length: >= 1 and <= 12 } || !request.SeriesId.All(char.IsAsciiDigit)
                => "Choose a valid TheTVDB show.",
            EditorGroupMatchKinds.Episodes when request.SeasonNumber is not (>= 0 and <= 999)
                => "Choose the season to line the episodes up with.",
            EditorGroupMatchKinds.Songs when !Guid.TryParse(request.ReleaseId, out _)
                => "Choose a release to line the songs up with.",
            EditorGroupMatchKinds.Episodes or EditorGroupMatchKinds.Songs => null,
            _ => "That kind of group cannot be matched together.",
        };
    }

    public async Task<EditorGroupMatchDto> LineUpAsync(
        EditorGroupMatchRequestDto request, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct = default)
    {
        var refusal = Refusal(request);
        if (refusal is not null)
        {
            return new EditorGroupMatchDto { Kind = request.Kind, Message = refusal };
        }

        return request.Kind == EditorGroupMatchKinds.Episodes
            ? await EpisodesAsync(request, actor, canEdit, ct).ConfigureAwait(false)
            : await SongsAsync(request, actor, canEdit, ct).ConfigureAwait(false);
    }

    // ── Episodes ─────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<EditorGroupMatchDto> EpisodesAsync(EditorGroupMatchRequestDto request, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var seriesId = request.SeriesId!;
        var number = request.SeasonNumber!.Value;
        if (!tvdb.IsConfigured(TvdbProvider))
        {
            return new EditorGroupMatchDto { Kind = request.Kind, Message = "Connect TheTVDB in Settings before matching episodes." };
        }

        IReadOnlyList<TvMatchBrowserEpisodeDto> episodes;
        TvMatchBrowserSeasonDto season;
        string showName;
        try
        {
            var show = await tvdb.GetAsync(TvdbProvider, "series_extended", ProviderApiClient.Args(("id", seriesId)), ct).ConfigureAwait(false);
            var picked = show is null ? null : TvMatchBrowserMapper.MapSeasons(show, seriesId, null).FirstOrDefault(item => item.SeasonNumber == number);
            if (show is null || picked is null)
            {
                return new EditorGroupMatchDto { Kind = request.Kind, Message = "That show or season is not on TheTVDB." };
            }

            var english = await tvdb.GetAsync(TvdbProvider, "series_translation", ProviderApiClient.Args(("id", seriesId)), ct).ConfigureAwait(false);
            showName = FirstText(english, "name") ?? FirstText(show, "name") ?? $"TheTVDB series {seriesId}";
            var ordered = await tvdb.GetAllPagesAsync(TvdbProvider, "series_episodes",
                ProviderApiClient.Args(("id", seriesId), ("season_type", "default"), ("lang", "eng")), ct).ConfigureAwait(false);
            season = picked;
            episodes = TvMatchBrowserMapper.MapEpisodes(
                TvMatchBrowserMapper.SelectSeasonEpisodes(ordered, picked), seriesId, showName, picked, null, new Dictionary<string, Guid>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EditorGroupMatchDto { Kind = request.Kind, Message = $"TheTVDB did not answer: {ex.Message}" };
        }

        var lines = new List<EditorGroupMatchLineDto>();
        foreach (var id in request.EntityIds.Distinct())
        {
            lines.Add(await EpisodeLineAsync(id, season, episodes, showName, actor, canEdit, ct).ConfigureAwait(false));
        }

        return new EditorGroupMatchDto { Kind = request.Kind, TargetLabel = $"{showName} · {season.Title}", Lines = lines };
    }

    private async Task<EditorGroupMatchLineDto> EpisodeLineAsync(
        Guid id, TvMatchBrowserSeasonDto season, IReadOnlyList<TvMatchBrowserEpisodeDto> episodes, string showName,
        LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var kind = await kinds.ResolveAsync(id, ct).ConfigureAwait(false);
        if (kind != EditorKinds.TvEpisode)
        {
            return Unplaced(id, null, null, "This is not an episode, so it is left as it is.");
        }

        if (!await canEdit(id, LibraryMoveRight.Match, ct).ConfigureAwait(false))
        {
            return Unplaced(id, null, null, "You cannot change this one.");
        }

        var read = await reader.ReadAsync(id, kind, ct).ConfigureAwait(false);
        if (read is null)
        {
            return Unplaced(id, null, null, "This episode could not be read.");
        }

        var title = TitleOf(read);
        if (read.Evidence.Number is not { } number)
        {
            return Unplaced(id, title, null, "Its name carries no episode number to line up by.");
        }

        var numberText = $"Episode {number.ToString(CultureInfo.InvariantCulture)}";
        var target = episodes.FirstOrDefault(episode => episode.EpisodeNumber == number);
        if (target is null)
        {
            return Unplaced(id, title, numberText, $"{season.Title} has no episode {number.ToString(CultureInfo.InvariantCulture)}.");
        }

        var candidate = EditorMatchCandidates.ForTvEpisode(
            target.SeriesId, showName, target.EpisodeId, target.SeasonNumber, target.EpisodeNumber, target.Title,
            target.Aired, target.Overview, target.ImageUrl, target.RuntimeMinutes);
        return await LineAsync(id, kind, read, title, numberText, candidate, actor, canEdit, ct).ConfigureAwait(false);
    }

    // ── Songs ────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<EditorGroupMatchDto> SongsAsync(EditorGroupMatchRequestDto request, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var detail = await music.ReadReleaseAsync(request.ReleaseId!, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return new EditorGroupMatchDto { Kind = request.Kind, Message = "MusicBrainz did not answer for that release. Try again in a moment." };
        }

        var tracks = detail.Discs.SelectMany(disc => disc.Tracks).ToList();
        var lines = new List<EditorGroupMatchLineDto>();
        foreach (var id in request.EntityIds.Distinct())
        {
            lines.Add(await SongLineAsync(id, detail, tracks, actor, canEdit, ct).ConfigureAwait(false));
        }

        var label = string.IsNullOrWhiteSpace(detail.Summary.Artist) ? detail.Summary.Title : $"{detail.Summary.Title} · {detail.Summary.Artist}";
        return new EditorGroupMatchDto { Kind = request.Kind, TargetLabel = label, Lines = lines };
    }

    private async Task<EditorGroupMatchLineDto> SongLineAsync(
        Guid id, MusicBrainzReleaseDetail detail, IReadOnlyList<MusicBrainzTrackDetail> tracks,
        LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var kind = await kinds.ResolveAsync(id, ct).ConfigureAwait(false);
        if (kind != EditorKinds.Song)
        {
            return Unplaced(id, null, null, "This is not a song, so it is left as it is.");
        }

        if (!await canEdit(id, LibraryMoveRight.Match, ct).ConfigureAwait(false))
        {
            return Unplaced(id, null, null, "You cannot change this one.");
        }

        var read = await reader.ReadAsync(id, kind, ct).ConfigureAwait(false);
        if (read is null)
        {
            return Unplaced(id, null, null, "This song could not be read.");
        }

        var title = TitleOf(read);
        if (read.Evidence.Number is not { } number)
        {
            return Unplaced(id, title, null, "Its name and tags carry no track number to line up by.");
        }

        var numberText = read.Evidence.Disc is { } disc
            ? $"Disc {disc.ToString(CultureInfo.InvariantCulture)}, track {number.ToString(CultureInfo.InvariantCulture)}"
            : $"Track {number.ToString(CultureInfo.InvariantCulture)}";
        var target = EditorMusicSuggestion.TrackFor(tracks, number, read.Evidence.Disc, read.Evidence.FileTitle);
        if (target is null)
        {
            return Unplaced(id, title, numberText, $"{detail.Summary.Title} has no {numberText.ToLowerInvariant()}.");
        }

        var candidate = EditorMatchCandidates.ForMusicTrack(
            detail.Summary.ReleaseId, detail.Summary.ReleaseGroupId, detail.Summary.Title, detail.Summary.Artist, detail.Summary.Year,
            detail.Summary.HasFrontCover, target.RecordingId, target.Title, target.Artist, target.Disc, target.Track,
            target.LengthSeconds, target.Disambiguation);
        return await LineAsync(id, kind, read, title, numberText, candidate, actor, canEdit, ct).ConfigureAwait(false);
    }

    // ── Shared ───────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<EditorGroupMatchLineDto> LineAsync(
        Guid id, string kind, EditorMatchLocalRead read, string title, string numberText,
        MediaEngine.Contracts.Matching.ItemCanonicalRetailCandidateDto candidate, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var judged = search.JudgeRead(kind, candidate, read);
        var where = await placement.PlanAsync(id, candidate, actor, canEdit, ct).ConfigureAwait(false);
        return new EditorGroupMatchLineDto
        {
            EntityId = id,
            Title = title,
            NumberText = numberText,
            TargetTitle = candidate.Title,
            Verdict = judged.Verdict,
            Reasons = judged.Reasons,
            Fits = judged.Verdict != EditorMatchVerdicts.Check,
            Candidate = candidate,
            Placement = where,
        };
    }

    private static EditorGroupMatchLineDto Unplaced(Guid id, string? title, string? numberText, string note) => new()
    {
        EntityId = id,
        Title = title ?? string.Empty,
        NumberText = numberText,
        Fits = false,
        Note = note,
    };

    private static string TitleOf(EditorMatchLocalRead read) =>
        !string.IsNullOrWhiteSpace(read.FileName) ? Path.GetFileNameWithoutExtension(read.FileName) : read.Evidence.FileTitle ?? string.Empty;

    private static string? FirstText(System.Text.Json.Nodes.JsonNode? node, string key) =>
        string.IsNullOrWhiteSpace(node?[key]?.ToString()) ? null : node![key]!.ToString().Trim();
}
