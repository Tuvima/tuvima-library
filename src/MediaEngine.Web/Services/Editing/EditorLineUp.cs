using System.Globalization;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>What a file in the line-up is lined up against: the episodes of a show or the tracks of an album.</summary>
public enum LineUpKind
{
    TvEpisodes,
    AlbumTracks,
}

/// <summary>Where a line-up row stands. Ready rows can be staged; a row that Needs choice blocks staging until it is decided.</summary>
public enum LineUpStatus
{
    Ready,
    NeedsChoice,
    Skipped,
}

/// <summary>Which files the line-up covers: only the ticked ones, or every file under the season, show or album.</summary>
public enum LineUpScope
{
    Selected,
    All,
}

/// <summary>
/// One of the provider's episodes or tracks a file can be lined up with, reduced to the deciding facts (universal editor v4, section 3a):
/// number, title, air date and still for an episode; disc, track number and title for a track.
/// </summary>
public sealed record LineUpOption(
    string ChildId,
    string ParentId,
    string Title,
    int? SeasonNumber,
    int? Number,
    int? DiscNumber,
    string? RecordingId,
    string? ImageUrl,
    string? Aired)
{
    /// <summary>"S02 E05" for an episode, "3-07" for a track on a later disc, "07" for a plain track number, or empty when there is no number.</summary>
    public string NumberLabel =>
        SeasonNumber is { } season && Number is { } episode
            ? EditorTvMatchCandidates.SlotLabel(season, episode)
            : Number is { } track
                ? DiscNumber is > 1 ? $"{DiscNumber.Value.ToString(CultureInfo.InvariantCulture)}-{track.ToString("00", CultureInfo.InvariantCulture)}" : track.ToString("00", CultureInfo.InvariantCulture)
                : string.Empty;
}

/// <summary>One of the person's files, as the line-up reads it from the Engine's owned-files list.</summary>
public sealed record LineUpFile(Guid EntityId, Guid AssetId, string Revision, string FileName, string? CurrentNumber, string? CurrentTitle);

/// <summary>
/// One row of the table: a file, the Engine's proposal with how sure it is and why, the other candidates, and what the person chose.
/// <see cref="ChosenId"/> is the provider child the file will be matched to (null until decided); <see cref="LeaveAlone"/> says the person
/// chose to leave this file as it is.
/// </summary>
public sealed class LineUpRow
{
    public required LineUpFile File { get; init; }

    public string? ProposedId { get; init; }

    /// <summary>0 to 100, from the Engine's band for the proposal; 0 when it proposed nothing.</summary>
    public int Confidence { get; init; }

    /// <summary>The Engine's plain reason for the proposal (the small (i)); never invented here.</summary>
    public string? Reason { get; init; }

    /// <summary>True when the Engine's own checks found a conflict with the proposal, so it cannot be taken without a look.</summary>
    public bool Conflicted { get; init; }

    public required IReadOnlyList<LineUpOption> Options { get; init; }

    public string? ChosenId { get; set; }

    public bool LeaveAlone { get; set; }

    public LineUpOption? Chosen => ChosenId is null ? null : Options.FirstOrDefault(option => option.ChildId == ChosenId);

    public LineUpOption? Proposed => ProposedId is null ? null : Options.FirstOrDefault(option => option.ChildId == ProposedId);
}

/// <summary>The outcome of reading the files and the Engine's proposals for them.</summary>
public sealed record LineUpResult(bool Ok, string? Message, string? Warning, IReadOnlyList<LineUpRow> Rows, string TargetParentId)
{
    public static LineUpResult Failed(string message) => new(false, message, null, [], string.Empty);
}

/// <summary>
/// The rules of the several-files Match table (universal editor v4, D5, D7, WP8), kept apart from the screen: how sure a band is, which
/// rows are Ready and which Need choice, and the match each Ready row stages. Nothing here writes; staging only fills the pending list,
/// and Save changes (with its Review) does the saving through the one edit session.
/// </summary>
public static class EditorLineUpRules
{
    /// <summary>The Engine takes this many files in one line-up read.</summary>
    public const int MaxFiles = 1000;

    public const string LeaveAloneValue = "__leave__";

    public const string NeedsChoiceText = "Needs choice";

    public const string ReadyText = "Ready";

    public const string SkippedText = "Left as is";

    public const string LeaveAloneText = "Leave this file as it is";

    public const string NoProposalText = "Nothing fits this file. Choose from the list or leave it as it is.";

    public const string SameChildText = "Another file is lined up with the same one. Choose which one it is.";

    public static bool UsesLineUp(string? mediaType) =>
        string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase) || string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase);

    public static LineUpKind KindOf(string? mediaType) =>
        string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase) ? LineUpKind.AlbumTracks : LineUpKind.TvEpisodes;

    /// <summary>The bar's fill for an Engine band: Exact is full, Strong is high, Review is a coin toss, anything else is empty.</summary>
    public static int ConfidenceOf(string? band) => band switch
    {
        "Exact" => 100,
        "Strong" => 80,
        "Review" => 45,
        _ => 0,
    };

    /// <summary>True for the bands the Engine is sure enough about to line up without a person looking.</summary>
    public static bool IsConfident(string? band) => band is "Exact" or "Strong";

    public static string ConfidenceWord(int confidence) => confidence switch
    {
        >= 90 => "Exact",
        >= 70 => "Strong",
        >= 1 => "Doubtful",
        _ => "No match",
    };

    /// <summary>"Apply to" labels: the ticked files only, or all files under the season, show or album.</summary>
    public static string SelectedLabel => "Selected files only";

    public static string AllLabel(LineUpKind kind, bool wholeShow) => kind == LineUpKind.AlbumTracks
        ? "All files on this album"
        : wholeShow ? "All files in this show" : "All files in this season";

    public static string FilesHeading(LineUpKind kind, int count) => kind == LineUpKind.AlbumTracks
        ? $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "song" : "songs")} lined up with the album's tracks"
        : $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "file" : "files")} lined up with the show's episodes";

    /// <summary>The words for one dropdown entry: number, then title.</summary>
    public static string OptionText(LineUpOption option) =>
        string.IsNullOrWhiteSpace(option.NumberLabel) ? option.Title : $"{option.NumberLabel}  {option.Title}";

    /// <summary>The Engine's reason, or the conflicts it found, or a plain sentence when it proposed nothing.</summary>
    public static string? ReasonOf(MediaEditorPairingCandidateDto? candidate)
    {
        if (candidate is null)
        {
            return NoProposalText;
        }

        if (!string.IsNullOrWhiteSpace(candidate.ReasonText))
        {
            return candidate.ReasonText;
        }

        var parts = candidate.Reasons.Concat(candidate.Conflicts).Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// Turns the Engine's read into rows. A confident proposal that no other file shares is taken as the starting choice (Ready); anything
    /// else starts undecided (Needs choice). Episodes the provider lists beyond the Engine's alternatives are added as options so a file that
    /// matched nothing can still be lined up by hand.
    /// </summary>
    public static IReadOnlyList<LineUpRow> BuildRows(
        IReadOnlyList<LineUpFile> files,
        MediaEditorPairingPreviewDto preview,
        IReadOnlyDictionary<(int Season, int Episode), TvMatchBrowserEpisodeDto>? episodeFacts = null,
        IReadOnlyList<LineUpOption>? catalogue = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(preview);
        var byAsset = preview.Rows.ToDictionary(row => row.AssetId);
        var rows = new List<LineUpRow>(files.Count);
        foreach (var file in files)
        {
            byAsset.TryGetValue(file.AssetId, out var engineRow);
            var candidates = engineRow is null
                ? []
                : new[] { engineRow.Proposed }.Concat(engineRow.Alternatives).Where(candidate => candidate is not null).Select(candidate => candidate!).ToList();
            var options = new List<LineUpOption>();
            foreach (var candidate in candidates)
            {
                var option = OptionOf(candidate.Child, episodeFacts);
                if (options.All(existing => existing.ChildId != option.ChildId))
                {
                    options.Add(option);
                }
            }

            foreach (var extra in catalogue ?? [])
            {
                if (options.All(existing => existing.ChildId != extra.ChildId))
                {
                    options.Add(extra);
                }
            }

            var proposed = engineRow?.Proposed;
            var confident = proposed is not null && IsConfident(proposed.Band) && proposed.Conflicts.Count == 0 && IsConfident(engineRow!.Band);
            rows.Add(new LineUpRow
            {
                File = file,
                ProposedId = proposed?.Child.ChildId,
                Confidence = proposed is null ? 0 : ConfidenceOf(proposed.Band),
                Reason = ReasonOf(proposed),
                Conflicted = proposed is { Conflicts.Count: > 0 },
                Options = options,
                ChosenId = confident ? proposed!.Child.ChildId : null,
            });
        }

        // Two files that both claim the same episode are both doubtful: the person decides which one it is.
        foreach (var group in rows.Where(row => row.ChosenId is not null).GroupBy(row => row.ChosenId).Where(group => group.Count() > 1).ToList())
        {
            foreach (var row in group)
            {
                row.ChosenId = null;
            }
        }

        return rows;
    }

    public static LineUpOption OptionOf(MediaEditorPairingChildDto child, IReadOnlyDictionary<(int Season, int Episode), TvMatchBrowserEpisodeDto>? episodeFacts = null)
    {
        TvMatchBrowserEpisodeDto? facts = null;
        if (child.SeasonNumber is { } season && child.EpisodeNumber is { } episode)
        {
            episodeFacts?.TryGetValue((season, episode), out facts);
        }

        return new LineUpOption(
            child.ChildId,
            child.ParentId,
            child.Title,
            child.SeasonNumber,
            child.EpisodeNumber ?? child.TrackNumber,
            child.DiscNumber,
            child.RecordingId,
            facts?.ImageUrl,
            facts?.Aired);
    }

    public static LineUpOption OptionOf(TvMatchBrowserEpisodeDto episode) => new(
        episode.EpisodeId,
        episode.SeriesId,
        episode.Title,
        episode.SeasonNumber,
        episode.EpisodeNumber,
        null,
        null,
        episode.ImageUrl,
        episode.Aired);

    /// <summary>The status of every row at once, because two rows lined up with one episode are both undecided.</summary>
    public static IReadOnlyList<LineUpStatus> StatusesOf(IReadOnlyList<LineUpRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var counts = rows.Where(row => !row.LeaveAlone && row.ChosenId is not null).GroupBy(row => row.ChosenId!).ToDictionary(group => group.Key, group => group.Count());
        return [.. rows.Select(row =>
            row.LeaveAlone ? LineUpStatus.Skipped
            : row.ChosenId is null || counts.GetValueOrDefault(row.ChosenId) > 1 ? LineUpStatus.NeedsChoice
            : LineUpStatus.Ready)];
    }

    public static int NeedsChoiceCount(IReadOnlyList<LineUpRow> rows) => StatusesOf(rows).Count(status => status == LineUpStatus.NeedsChoice);

    public static int ReadyCount(IReadOnlyList<LineUpRow> rows) => StatusesOf(rows).Count(status => status == LineUpStatus.Ready);

    /// <summary>Staging is allowed only when no row is still undecided and at least one row is Ready.</summary>
    public static bool CanStage(IReadOnlyList<LineUpRow> rows) => NeedsChoiceCount(rows) == 0 && ReadyCount(rows) > 0;

    public static string BlockedText(int needsChoice) =>
        $"{needsChoice.ToString(CultureInfo.InvariantCulture)} {(needsChoice == 1 ? "row still needs" : "rows still need")} a choice before these matches can be staged.";

    /// <summary>The sentence beside the button once the rows are decided: how many matches Save changes will write.</summary>
    public static string ReadySummary(int ready, int skipped) =>
        skipped == 0
            ? $"{ready.ToString(CultureInfo.InvariantCulture)} {(ready == 1 ? "match" : "matches")} ready to stage."
            : $"{ready.ToString(CultureInfo.InvariantCulture)} {(ready == 1 ? "match" : "matches")} ready to stage, {skipped.ToString(CultureInfo.InvariantCulture)} left as {(skipped == 1 ? "it is" : "they are")}.";

    /// <summary>The retail-match request one Ready row stages: the same shape a single episode or song match stages.</summary>
    public static ReplaceRetailMatchRequestDto RequestFor(LineUpKind kind, LineUpRow row, LineUpOption option, string parentName, string? artist, string? albumReleaseId, string? coverUrl)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(option);
        if (kind == LineUpKind.TvEpisodes)
        {
            var candidate = EditorTvMatchCandidates.ForEpisode(new TvMatchBrowserEpisodeDto(
                option.ChildId, option.ParentId, parentName, string.Empty,
                option.SeasonNumber ?? 0, option.Number ?? 0, option.Title, option.Aired, null, option.ImageUrl, string.Empty, false, true, row.File.EntityId));
            return EditorMatchRules.RequestFor(candidate, TvSubject(row), "show_episode");
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = option.Title,
            [MetadataFieldConstants.Album] = parentName,
        };
        if (!string.IsNullOrWhiteSpace(artist))
        {
            fields[MetadataFieldConstants.Artist] = artist!;
        }

        var suggested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (option.Number is { } number)
        {
            suggested[MetadataFieldConstants.TrackNumber] = number.ToString(CultureInfo.InvariantCulture);
        }

        var recording = option.RecordingId ?? option.ChildId;
        var bridges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BridgeIdKeys.MusicBrainzRecordingId] = recording,
        };
        var release = albumReleaseId ?? option.ParentId;
        if (!string.IsNullOrWhiteSpace(release))
        {
            bridges[BridgeIdKeys.MusicBrainzReleaseId] = release;
        }

        return new ReplaceRetailMatchRequestDto
        {
            TargetKind = "item",
            TargetFieldGroup = "track",
            TargetScopeId = "track",
            ProviderId = WellKnownProviders.MusicBrainz.ToString("D"),
            ProviderName = "musicbrainz",
            ProviderItemId = recording,
            CoverUrl = coverUrl,
            RequiredFields = fields,
            SuggestedFields = suggested,
            BridgeIds = bridges,
        };
    }

    private static EditorMatchSubject TvSubject(LineUpRow row) => new(
        row.File.EntityId, "TV", "episode", "show_episode", row.File.CurrentTitle ?? row.File.FileName, null, null, null,
        new Dictionary<string, string>(), []);

    /// <summary>
    /// Stages one identity change per Ready row, replacing what an earlier visit staged for it, and takes the entry back out for a row that
    /// was left as it is. Returns how many matches are now staged, or -1 when a row still Needs choice (nothing is staged then).
    /// Nothing is written, and no file moves: a match never moves files unless its own switch is on, and this table has none.
    /// </summary>
    public static int Stage(PendingEditList pending, LineUpKind kind, IReadOnlyList<LineUpRow> rows, string parentName, string? artist, string? albumReleaseId, string? coverUrl)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(rows);
        if (!CanStage(rows))
        {
            return -1;
        }

        var statuses = StatusesOf(rows);
        var staged = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var existing = pending.Changes.FirstOrDefault(change => change.Kind == EditSessionChangeKinds.Identity && change.EntityId == row.File.EntityId);
            if (statuses[index] != LineUpStatus.Ready || row.Chosen is not { } option)
            {
                if (existing is not null)
                {
                    pending.Remove(existing.Id);
                }

                continue;
            }

            var request = RequestFor(kind, row, option, parentName, artist, albumReleaseId, coverUrl);
            pending.SetTarget(
                EditSessionChangeKinds.Identity,
                row.File.EntityId,
                EditSessionIdentityTarget.Serialize(request),
                label: LabelOf(option, parentName),
                before: BeforeOf(row.File));
            staged++;
        }

        return staged;
    }

    private static string LabelOf(LineUpOption option, string parentName) =>
        string.IsNullOrWhiteSpace(option.NumberLabel) ? $"{option.Title} ({parentName})" : $"{option.NumberLabel} {option.Title} ({parentName})";

    private static string BeforeOf(LineUpFile file)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(file.CurrentNumber))
        {
            parts.Add(file.CurrentNumber!);
        }

        if (!string.IsNullOrWhiteSpace(file.CurrentTitle))
        {
            parts.Add(file.CurrentTitle!);
        }

        return parts.Count == 0 ? file.FileName : string.Join(" ", parts);
    }
}

/// <summary>
/// Reads the person's files and the Engine's line-up for them (the parent-first pairing preview, which never writes), and returns rows ready
/// for the table. The Dashboard keeps no matching rules of its own: bands, reasons and alternatives all come from the Engine.
/// </summary>
public sealed class EditorLineUpLoader
{
    private const int PageSize = 200;
    private const string FilesProblem = "The files could not be read. Try again.";
    private const string TooMany = "Pick up to 1,000 files at a time.";
    private const string NoFiles = "There are no files to line up here.";

    private readonly IEngineApiClient _api;
    private readonly ILogger? _logger;

    public EditorLineUpLoader(IEngineApiClient api, ILogger? logger = null)
    {
        _api = api;
        _logger = logger;
    }

    /// <summary>
    /// The files under <paramref name="parentId"/>, one per Work, in the order the Engine lists them. <paramref name="onlyWorks"/> narrows
    /// them to the ticked rows; null means every file.
    /// </summary>
    public async Task<(IReadOnlyList<LineUpFile>? Files, string? Message)> ReadFilesAsync(Guid parentId, IReadOnlySet<Guid>? onlyWorks, CancellationToken ct = default)
    {
        var files = new List<LineUpFile>();
        var seen = new HashSet<Guid>();
        for (var page = 1; page <= 20; page++)
        {
            var result = await _api.GetMediaEditorOwnedChildrenAsync(parentId, page: page, pageSize: PageSize, directOnly: false, ct: ct);
            if (result is null)
            {
                return (null, FilesProblem);
            }

            foreach (var item in result.Items)
            {
                if (onlyWorks is not null && !onlyWorks.Contains(item.WorkId))
                {
                    continue;
                }

                if (item.AssetId == Guid.Empty || !seen.Add(item.WorkId))
                {
                    continue;
                }

                files.Add(new LineUpFile(
                    item.WorkId,
                    item.AssetId,
                    item.SelectionRevision,
                    string.IsNullOrWhiteSpace(item.SourceFileName) ? item.Title : item.SourceFileName,
                    item.MatchedNumber,
                    string.IsNullOrWhiteSpace(item.MatchedTitle) ? null : item.MatchedTitle));
            }

            if (result.Items.Count == 0 || page * PageSize >= result.TotalCount)
            {
                break;
            }
        }

        if (files.Count == 0)
        {
            return (null, NoFiles);
        }

        return files.Count > EditorLineUpRules.MaxFiles ? (null, TooMany) : (files, null);
    }

    /// <summary>Asks the Engine to line the files up with the chosen show or album and turns its answer into rows.</summary>
    public async Task<LineUpResult> LineUpAsync(Guid parentId, LineUpKind kind, IReadOnlyList<LineUpFile> files, string targetParentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            return LineUpResult.Failed(NoFiles);
        }

        var request = new MediaEditorPairingPreviewRequestDto(
            [.. files.Select(file => file.AssetId)],
            targetParentId,
            files.ToDictionary(file => file.AssetId, file => file.Revision));
        var preview = await _api.PreviewMediaEditorPairingAsync(parentId, request, ct);
        if (preview is null)
        {
            return LineUpResult.Failed(string.IsNullOrWhiteSpace(_api.LastError) ? "The Engine could not line these files up. Try again." : _api.LastError!);
        }

        IReadOnlyDictionary<(int Season, int Episode), TvMatchBrowserEpisodeDto>? facts = null;
        IReadOnlyList<LineUpOption>? catalogue = null;
        if (kind == LineUpKind.TvEpisodes)
        {
            var seasons = preview.Rows
                .SelectMany(row => new[] { row.Proposed }.Concat(row.Alternatives))
                .Where(candidate => candidate?.Child.SeasonNumber is not null)
                .Select(candidate => candidate!.Child.SeasonNumber!.Value)
                .Distinct()
                .Order()
                .Take(30)
                .ToList();
            var map = new Dictionary<(int Season, int Episode), TvMatchBrowserEpisodeDto>();
            var all = new List<LineUpOption>();
            foreach (var season in seasons)
            {
                foreach (var episode in await ReadSeasonAsync(files[0].EntityId, targetParentId, season, ct))
                {
                    map[(episode.SeasonNumber, episode.EpisodeNumber)] = episode;
                    all.Add(EditorLineUpRules.OptionOf(episode));
                }
            }

            facts = map;
            catalogue = all;
        }

        var rows = EditorLineUpRules.BuildRows(files, preview, facts, catalogue);
        return new LineUpResult(true, null, preview.CatalogueWarning, rows, preview.TargetParentId);
    }

    // The still and air date come from the show's season list; a failed read only leaves them out.
    private async Task<IReadOnlyList<TvMatchBrowserEpisodeDto>> ReadSeasonAsync(Guid entityId, string seriesId, int season, CancellationToken ct)
    {
        try
        {
            var response = await _api.GetTvMatchBrowserEpisodesAsync(entityId, seriesId, season, ct);
            return response?.Episodes ?? [];
        }
        catch (HttpRequestException ex)
        {
            // The table still works without pictures and air dates.
            _logger?.LogWarning(ex, "The episode pictures for season {Season} of {SeriesId} could not be read for the line-up", season, seriesId);
            return [];
        }
    }
}
