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
/// where applying it would put the item and its file. An item that does not line up, or whose verdict is Weak match, comes back with
/// <see cref="EditorGroupMatchLineDto.Fits"/> false so the Dashboard leaves it unticked. A read: nothing is written until Save changes.
/// </summary>
public sealed class EditorGroupMatchService(
    EditorMatchSearchService search,
    EditorMatchLocalEvidenceReader reader,
    IEditorMatchKindResolver kinds,
    IEditorMatchPlacement placement,
    MusicBrainzReleaseClient musicBrainz,
    ProviderApiClient tvdb,
    ILogger<EditorGroupMatchService> logger)
{
    private const string TvdbProvider = "tvdb";
    private const string EpisodeScope = "episode";
    private const string TrackScope = "track";

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
                => "Choose a valid show.",
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
            return new EditorGroupMatchDto { Kind = request.Kind, MessageCode = EditorMatchMessageCodes.InvalidRequest };
        }

        return request.Kind == EditorGroupMatchKinds.Episodes
            ? await EpisodesAsync(request, actor, canEdit, ct).ConfigureAwait(false)
            : await SongsAsync(request, actor, canEdit, ct).ConfigureAwait(false);
    }

    // What the lined-up items are matched as; the Dashboard stages each match with it instead of inventing the strings.
    private static EditorGroupMatchDto Answer(string kind, string? label = null, IReadOnlyList<EditorGroupMatchLineDto>? lines = null, string? code = null)
    {
        var episodes = kind == EditorGroupMatchKinds.Episodes;
        var match = EditorMatchKinds.For(episodes ? EditorKinds.TvEpisode : EditorKinds.Song);
        return new EditorGroupMatchDto
        {
            Kind = kind,
            TargetLabel = label ?? string.Empty,
            Lines = lines ?? [],
            MessageCode = code,
            SubjectMediaType = match?.MediaType,
            SubjectScopeId = episodes ? EpisodeScope : TrackScope,
            SubjectTargetGroup = match?.TargetFieldGroup,
        };
    }

    // Two ticked items that would take the same episode or track cannot both be right: neither is ticked, and each says so, so the person
    // keeps the one that belongs. An item already left out for another reason does not count as taking it.
    private static IReadOnlyList<EditorGroupMatchLineDto> FlagDuplicates(List<EditorGroupMatchLineDto> lines)
    {
        var duplicated = lines
            .Where(line => line.Fits && line.Candidate is not null)
            .GroupBy(line => TargetKey(line.Candidate!))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(line => line.EntityId)
            .ToHashSet();
        return [.. lines.Select(line => duplicated.Contains(line.EntityId) ? Flagged(line, EditorMatchMessageCodes.Duplicate, line.NumberText) : line)];
    }

    private static string TargetKey(MediaEngine.Contracts.Matching.ItemCanonicalRetailCandidateDto candidate) =>
        string.Join("|", candidate.ProviderItemId, candidate.SuggestedFields.GetValueOrDefault("disc_number"), candidate.SuggestedFields.GetValueOrDefault("track_number"));

    private static EditorGroupMatchLineDto Flagged(EditorGroupMatchLineDto line, string code, string? value) => new()
    {
        EntityId = line.EntityId,
        Title = line.Title,
        NumberText = line.NumberText,
        TargetTitle = line.TargetTitle,
        Verdict = line.Verdict,
        Reasons = line.Reasons,
        Fits = false,
        NoteCode = code,
        NoteValue = value,
        Candidate = line.Candidate,
        Placement = line.Placement,
    };

    // ── Episodes ─────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<EditorGroupMatchDto> EpisodesAsync(EditorGroupMatchRequestDto request, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var seriesId = request.SeriesId!;
        var number = request.SeasonNumber!.Value;
        if (!tvdb.IsConfigured(TvdbProvider))
        {
            return Answer(request.Kind, code: EditorMatchMessageCodes.ProviderNotConnected);
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
                return Answer(request.Kind, code: EditorMatchMessageCodes.NotOnProvider);
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
            logger.LogWarning(ex, "The group match could not read show {SeriesId} from the provider", seriesId);
            return Answer(request.Kind, code: EditorMatchMessageCodes.ProviderSilent);
        }

        var lines = new List<EditorGroupMatchLineDto>();
        foreach (var id in request.EntityIds.Distinct())
        {
            lines.Add(await EpisodeLineAsync(id, season, episodes, showName, actor, canEdit, ct).ConfigureAwait(false));
        }

        return Answer(request.Kind, $"{showName} · {season.Title}", FlagDuplicates(lines));
    }

    private async Task<EditorGroupMatchLineDto> EpisodeLineAsync(
        Guid id, TvMatchBrowserSeasonDto season, IReadOnlyList<TvMatchBrowserEpisodeDto> episodes, string showName,
        LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var kind = await kinds.ResolveAsync(id, ct).ConfigureAwait(false);
        if (kind != EditorKinds.TvEpisode)
        {
            return Unplaced(id, null, null, EditorMatchMessageCodes.WrongKind);
        }

        if (!await canEdit(id, LibraryMoveRight.Match, ct).ConfigureAwait(false))
        {
            return Unplaced(id, null, null, EditorMatchMessageCodes.CannotChange);
        }

        var read = await reader.ReadAsync(id, kind, ct).ConfigureAwait(false);
        if (read is null)
        {
            return Unplaced(id, null, null, EditorMatchMessageCodes.CouldNotRead);
        }

        var title = TitleOf(read);
        if (read.Evidence.Number is not { } number)
        {
            return Unplaced(id, title, null, EditorMatchMessageCodes.NoNumber);
        }

        var numberText = number.ToString(CultureInfo.InvariantCulture);
        var target = episodes.FirstOrDefault(episode => episode.EpisodeNumber == number);
        if (target is null)
        {
            return Unplaced(id, title, numberText, EditorMatchMessageCodes.NoSuchNumber, numberText);
        }

        var candidate = EditorMatchCandidates.ForTvEpisode(
            target.SeriesId, showName, target.EpisodeId, target.SeasonNumber, target.EpisodeNumber, target.Title,
            target.Aired, target.Overview, target.ImageUrl, target.RuntimeMinutes);
        var line = await LineAsync(id, kind, read, title, numberText, candidate, actor, canEdit, ct).ConfigureAwait(false);

        // A file that names another season (S01E03 in a group matched to Season 2) lines up with the wrong episode: it is left out unless ticked.
        return read.Evidence.Season is { } fileSeason && fileSeason != season.SeasonNumber
            ? Flagged(line, EditorMatchMessageCodes.OtherSeason, fileSeason.ToString(CultureInfo.InvariantCulture))
            : line;
    }

    // ── Songs ────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<EditorGroupMatchDto> SongsAsync(EditorGroupMatchRequestDto request, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var detail = await musicBrainz.FetchReleaseDetailAsync(request.ReleaseId!, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return Answer(request.Kind, code: EditorMatchMessageCodes.ProviderSilent);
        }

        var tracks = detail.Discs.SelectMany(disc => disc.Tracks).ToList();
        var lines = new List<EditorGroupMatchLineDto>();
        foreach (var id in request.EntityIds.Distinct())
        {
            lines.Add(await SongLineAsync(id, detail, tracks, actor, canEdit, ct).ConfigureAwait(false));
        }

        var label = string.IsNullOrWhiteSpace(detail.Summary.Artist) ? detail.Summary.Title : $"{detail.Summary.Title} · {detail.Summary.Artist}";
        return Answer(request.Kind, label, FlagDuplicates(lines));
    }

    private async Task<EditorGroupMatchLineDto> SongLineAsync(
        Guid id, MusicBrainzReleaseDetail detail, IReadOnlyList<MusicBrainzTrackDetail> tracks,
        LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var kind = await kinds.ResolveAsync(id, ct).ConfigureAwait(false);
        if (kind != EditorKinds.Song)
        {
            return Unplaced(id, null, null, EditorMatchMessageCodes.WrongKind);
        }

        if (!await canEdit(id, LibraryMoveRight.Match, ct).ConfigureAwait(false))
        {
            return Unplaced(id, null, null, EditorMatchMessageCodes.CannotChange);
        }

        var read = await reader.ReadAsync(id, kind, ct).ConfigureAwait(false);
        if (read is null)
        {
            return Unplaced(id, null, null, EditorMatchMessageCodes.CouldNotRead);
        }

        var title = TitleOf(read);
        if (read.Evidence.Number is not { } number)
        {
            return Unplaced(id, title, null, EditorMatchMessageCodes.NoNumber);
        }

        // The number as the person reads it: "2·4" for disc 2 track 4, "4" when the file names no disc.
        var numberText = read.Evidence.Disc is { } disc
            ? $"{disc.ToString(CultureInfo.InvariantCulture)}·{number.ToString(CultureInfo.InvariantCulture)}"
            : number.ToString(CultureInfo.InvariantCulture);
        var target = EditorMusicSuggestion.TrackFor(tracks, number, read.Evidence.Disc, read.Evidence.FileTitle);
        if (target is null)
        {
            return Unplaced(id, title, numberText, EditorMatchMessageCodes.NoSuchNumber, numberText);
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
            Fits = judged.Verdict != EditorMatchVerdicts.Weak,
            Candidate = candidate,
            Placement = where,
        };
    }

    private static EditorGroupMatchLineDto Unplaced(Guid id, string? title, string? numberText, string code, string? value = null) => new()
    {
        EntityId = id,
        Title = title ?? string.Empty,
        NumberText = numberText,
        Fits = false,
        NoteCode = code,
        NoteValue = value,
    };

    private static string TitleOf(EditorMatchLocalRead read) =>
        !string.IsNullOrWhiteSpace(read.FileName) ? Path.GetFileNameWithoutExtension(read.FileName) : read.Evidence.FileTitle ?? string.Empty;

    private static string? FirstText(System.Text.Json.Nodes.JsonNode? node, string key) =>
        string.IsNullOrWhiteSpace(node?[key]?.ToString()) ? null : node![key]!.ToString().Trim();
}
