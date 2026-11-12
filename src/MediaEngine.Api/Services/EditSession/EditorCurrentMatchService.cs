using System.Globalization;
using System.Text.Json.Nodes;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// The Match tab's first view for an item that already has a match (universal editor v5 D12, 3c "Existing match"). The matched record is
/// found by the provider id the Engine stored when it matched, never by a text search, so the tab can never say "Nothing called ..." about
/// the item's own title. A show, season or episode matched on TheTVDB is read from TheTVDB by its id; when the provider is not connected,
/// does not answer, or is one the Engine has no lookup by id for, the record the Engine stored is shown and a plain note says so.
/// TheTVDB pictures reach the browser through the Engine's own preview connection point (the same one the TheTVDB match lists use), so the
/// browser never contacts TheTVDB itself. A read only: nothing is written.
/// </summary>
public sealed class EditorCurrentMatchService(
    IMetadataEditorRepository launches,
    ICanonicalValueRepository canonicals,
    IMetadataClaimRepository claims,
    ProviderApiClient tvdb,
    IMemoryCache previews,
    EditorMatchVerdictCalculator calculator,
    ItemQuickFactsService quickFacts,
    ILogger<EditorCurrentMatchService> logger)
{
    private const string TvdbProvider = EditorMatchRule.TvdbProvider;

    /// <summary>The current match of the item with this id; null when no such item is in the Library.</summary>
    public async Task<EditorCurrentMatchDto?> ReadAsync(Guid entityId, CancellationToken ct = default)
    {
        var launch = await launches.ResolveEditorLaunchAsync(entityId, ct).ConfigureAwait(false);
        if (launch is null)
        {
            return null;
        }

        var own = await ReadOwnAsync(launch, ct).ConfigureAwait(false);
        var root = launch.RootWorkId != Guid.Empty && launch.RootWorkId != launch.WorkId
            ? Latest(await canonicals.GetByEntityAsync(launch.RootWorkId, ct).ConfigureAwait(false))
            : own;
        var isTv = string.Equals(launch.MediaType, "TV", StringComparison.OrdinalIgnoreCase);
        var fileName = string.IsNullOrWhiteSpace(launch.RepresentativeMediaFilePath)
            ? null
            : Path.GetFileName(launch.RepresentativeMediaFilePath.Replace('\\', '/'));

        var qid = Value(own, BridgeIdKeys.WikidataQid) ?? Value(root, BridgeIdKeys.WikidataQid);
        var isEpisode = string.Equals(launch.WorkKind, "child", StringComparison.OrdinalIgnoreCase);
        var source = EditorMatchRule.FindProviderSource(launch.MediaType, isTv, isEpisode, own, root);
        if (source is null)
        {
            return new EditorCurrentMatchDto
            {
                EntityId = entityId,
                State = EditorCurrentMatchStates.NotMatched,
                WikidataQid = qid,
                WikidataUrl = OutsideLinks.Wikidata(qid),
                FileName = fileName,
            };
        }

        // What the Engine stored for this item when it matched (the fallback, and the "matched record" side of the Why rows).
        var stored = new RecordFacts(
            Value(own, MetadataFieldConstants.Title),
            Value(own, MetadataFieldConstants.Year),
            Value(own, MetadataFieldConstants.Author) ?? Value(own, MetadataFieldConstants.Artist) ?? Value(own, MetadataFieldConstants.Director),
            Value(own, MetadataFieldConstants.SeasonNumber),
            Value(own, MetadataFieldConstants.EpisodeNumber),
            Value(own, "cover_url") ?? Value(own, "poster_url"),
            Value(own, "description"));

        RecordFacts facts = stored;
        var origin = EditorCurrentMatchSources.Stored;
        string? note = null;
        if (source.CanLookUp)
        {
            if (!tvdb.IsConfigured(TvdbProvider))
            {
                note = "TheTVDB is not connected, so this is the record Library saved when it matched.";
            }
            else
            {
                try
                {
                    var live = await ReadTvdbAsync(source, ct).ConfigureAwait(false);
                    if (live is null)
                    {
                        note = "TheTVDB no longer has this record, so this is the record Library saved when it matched.";
                    }
                    else
                    {
                        facts = live with
                        {
                            Season = live.Season ?? stored.Season,
                            Episode = live.Episode ?? stored.Episode,
                        };
                        origin = EditorCurrentMatchSources.Provider;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "TheTVDB did not answer for the current match of item {EntityId}", entityId);
                    note = "TheTVDB did not answer, so this is the record Library saved when it matched.";
                }
            }
        }
        else
        {
            note = $"{source.ProviderName} is shown from what Library saved when it matched.";
        }

        var parsed = EditorFileNameParser.Parse(launch.RepresentativeMediaFilePath, isTv);

        // A season or an episode is checked against its show: the file name carries the show's name, and the stored confidence for that name
        // belongs to the show.
        var showLevel = isTv && source.Kind != "Show";
        var titleSide = showLevel ? Value(root, MetadataFieldConstants.Title) ?? Value(root, MetadataFieldConstants.ShowName) : facts.Title ?? stored.Title;
        var scores = await ReadScoresAsync(showLevel && launch.RootWorkId != Guid.Empty ? launch.RootWorkId : launch.WorkId, ct).ConfigureAwait(false);

        // The year row compares like with like: a season or episode file carries the show's year, so it is set beside the show's year.
        var yearSide = showLevel ? Value(root, MetadataFieldConstants.Year) : facts.Year;

        // A film, an episode, a song or an audiobook is also checked against its measured length (when the Engine has measured the file).
        var hasOwnLength = source.Kind is "Film" or "Episode" or "Song" || string.Equals(launch.MediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase);
        var measured = hasOwnLength ? (await quickFacts.GetAsync(entityId, ct).ConfigureAwait(false))?.RuntimeSeconds : null;
        var recordMinutes = hasOwnLength && double.TryParse(Value(own, MetadataFieldConstants.Runtime), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) ? minutes : (double?)null;

        // A TheTVDB picture is shown through the Engine's preview, never as TheTVDB's own address.
        var imageUrl = MetadataEndpoints.CreateTvdbPreviewUrl(previews, entityId, launch.WorkId, facts.ImageUrl)
            ?? (IsTvdbAddress(facts.ImageUrl) ? null : facts.ImageUrl);
        var why = BuildWhy(isTv, parsed, titleSide, yearSide, facts, scores, measured, recordMinutes);
        var checks = why.Select(Check).ToList();
        return new EditorCurrentMatchDto
        {
            EntityId = entityId,
            State = EditorCurrentMatchStates.Matched,
            Record = new EditorMatchedRecordDto
            {
                ProviderId = source.ProviderId,
                ProviderName = source.ProviderName,
                ProviderItemId = source.ItemId,
                Kind = source.Kind,
                Title = facts.Title ?? stored.Title ?? string.Empty,
                Year = facts.Year,
                Creator = facts.Creator,
                ImageUrl = imageUrl,
                Description = facts.Description,
                SourceUrl = source.Url,
                Source = origin,
            },
            WikidataQid = qid,
            WikidataUrl = OutsideLinks.Wikidata(qid),
            Why = why,
            Verdict = calculator.Verdict(checks),
            Pinned = IsPinned(own) || IsPinned(root),
            FileName = fileName,
            Note = note,
        };
    }

    // What the Engine stored for the item itself. Ingestion keeps a fact about the file (a film's TMDB id, a book's ISBN or store id, an
    // episode's or a song's own id) on the file's asset and a fact about the container on the Work (ClaimScopeCatalog), so the item's
    // own values are the Work's with the asset's filling what the Work does not have. A container (a show, a season, an album) has no
    // file of its own: the asset the launch carries there is some child's, and its ids are never the container's.
    private async Task<IReadOnlyDictionary<string, string>> ReadOwnAsync(MetadataEditorLaunchContext launch, CancellationToken ct)
    {
        var work = Latest(await canonicals.GetByEntityAsync(launch.WorkId, ct).ConfigureAwait(false));
        if (launch.RepresentativeAssetId is not { } assetId || assetId == Guid.Empty || EditorMatchRule.IsContainer(launch.WorkKind))
        {
            return work;
        }

        return EditorMatchRule.OwnValues(work, Latest(await canonicals.GetByEntityAsync(assetId, ct).ConfigureAwait(false)), launch.WorkKind);
    }

    private static bool IsTvdbAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var address)
        && (address.Host.Equals("thetvdb.com", StringComparison.OrdinalIgnoreCase)
            || address.Host.EndsWith(".thetvdb.com", StringComparison.OrdinalIgnoreCase));

    private sealed record RecordFacts(
        string? Title, string? Year, string? Creator, string? Season, string? Episode, string? ImageUrl, string? Description);

    // One call by id: series_extended / season_extended / episode_extended. Never a search.
    private async Task<RecordFacts?> ReadTvdbAsync(EditorMatchRule.ProviderSource source, CancellationToken ct)
    {
        var endpoint = source.Kind switch
        {
            "Episode" => "episode_extended",
            "Season" => "season_extended",
            _ => "series_extended",
        };
        var node = await tvdb.GetAsync(TvdbProvider, endpoint, ProviderApiClient.Args(("id", source.ItemId)), ct).ConfigureAwait(false);
        if (node is null)
        {
            return null;
        }

        var title = Text(node, "name");
        if (source.Kind == "Show")
        {
            // The English name, when TheTVDB keeps one, is what the rest of Library shows.
            var english = await tvdb.GetAsync(TvdbProvider, "series_translation", ProviderApiClient.Args(("id", source.ItemId)), ct).ConfigureAwait(false);
            title = Text(english, "name") ?? title;
        }

        var year = Text(node, "year") ?? YearOf(Text(node, "aired"));
        return new RecordFacts(
            title ?? $"TheTVDB {source.Kind.ToLowerInvariant()} {source.ItemId}",
            year,
            Creator: null,
            Season: Text(node, "seasonNumber") ?? (source.Kind == "Season" ? Text(node, "number") : null),
            Episode: source.Kind == "Episode" ? Text(node, "number") : null,
            ImageUrl: Text(node, "image"),
            Description: Text(node, "overview"));
    }

    private static string? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year.ToString(CultureInfo.InvariantCulture)
            : null;

    // The Engine's stored confidence per field: the strongest claim that agrees with the value that won.
    private async Task<Dictionary<string, double>> ReadScoresAsync(Guid workId, CancellationToken ct)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var winners = Latest(await canonicals.GetByEntityAsync(workId, ct).ConfigureAwait(false));
        foreach (var claim in await claims.GetByEntityAsync(workId, ct).ConfigureAwait(false))
        {
            if (winners.TryGetValue(claim.ClaimKey, out var won)
                && string.Equals(won, claim.ClaimValue, StringComparison.OrdinalIgnoreCase)
                && (!scores.TryGetValue(claim.ClaimKey, out var best) || claim.Confidence > best))
            {
                scores[claim.ClaimKey] = Math.Clamp(claim.Confidence, 0, 1);
            }
        }

        return scores;
    }

    // Each fact the file's name says, set beside what the matched record says. A fact the file does not say is "not checked" and never
    // counts against the match.
    private IReadOnlyList<EditorMatchEvidenceRowDto> BuildWhy(
        bool isTv, ParsedFileName parsed, string? titleSide, string? yearSide, RecordFacts record, IReadOnlyDictionary<string, double> scores,
        double? measuredSeconds, double? recordMinutes)
    {
        var rows = new List<EditorMatchEvidenceRowDto>();
        var fileTitle = isTv ? parsed.Show : parsed.Title;
        rows.Add(Row("title", fileTitle, titleSide, scores.GetValueOrDefault(MetadataFieldConstants.Title) is var t and > 0 ? t : null, same: SameText));
        rows.Add(Row("year", parsed.Year, yearSide, scores.GetValueOrDefault(MetadataFieldConstants.Year) is var y and > 0 ? y : null, same: SameText));
        if (isTv && (parsed.Season is not null || record.Season is not null))
        {
            rows.Add(Row("season_number", parsed.Season?.ToString(CultureInfo.InvariantCulture), record.Season, null, same: SameNumber));
        }

        if (isTv && (parsed.Episode is not null || record.Episode is not null))
        {
            rows.Add(Row("episode_number", parsed.Episode?.ToString(CultureInfo.InvariantCulture), record.Episode, null, same: SameNumber));
        }

        if (measuredSeconds is > 0 && recordMinutes is > 0)
        {
            var runtime = calculator.Runtime(measuredSeconds, recordMinutes);
            rows.Add(new EditorMatchEvidenceRowDto { Field = "runtime", YourFile = runtime.Yours, MatchedRecord = runtime.Theirs, State = runtime.State, Note = runtime.Note });
        }

        return [.. rows.Select(row => new EditorMatchEvidenceRowDto
        {
            Field = row.Field,
            YourFile = row.YourFile,
            MatchedRecord = row.MatchedRecord,
            Score = row.Score,
            State = row.State,
            Verdict = calculator.RowVerdict(row.State, row.Score),
            Note = row.Note,
        })];
    }

    // A row of the Why table as a compared fact, so the overall verdict is judged the same way a search result is.
    private static MatchFactCheck Check(EditorMatchEvidenceRowDto row) => new(
        row.Field switch
        {
            "title" => MatchFactNames.Title,
            "year" => MatchFactNames.Year,
            "runtime" => MatchFactNames.Runtime,
            _ => MatchFactNames.Number,
        },
        row.State, row.YourFile, row.MatchedRecord, row.Field);

    private static bool IsPinned(IReadOnlyDictionary<string, string> map) =>
        string.Equals(Value(map, MetadataFieldConstants.MatchPinned), "true", StringComparison.OrdinalIgnoreCase);

    private static EditorMatchEvidenceRowDto Row(
        string field, string? file, string? matched, double? score, Func<string, string, bool> same) => new()
        {
            Field = field,
            YourFile = file,
            MatchedRecord = matched,
            Score = score,
            State = string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(matched)
            ? EditorMatchEvidenceStates.NotChecked
            : same(file, matched) ? EditorMatchEvidenceStates.Agrees : EditorMatchEvidenceStates.Differs,
        };

    private static bool SameText(string a, string b) => Normalize(a) == Normalize(b);

    private static bool SameNumber(string a, string b) =>
        int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
        && int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) && x == y;

    private static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IReadOnlyDictionary<string, string> Latest(IEnumerable<MediaEngine.Domain.Entities.CanonicalValue> values) =>
        values.Where(field => !string.IsNullOrWhiteSpace(field.Key))
            .GroupBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.LastScoredAt).First().Value, StringComparer.OrdinalIgnoreCase);

    private static string? Value(IReadOnlyDictionary<string, string> map, string key) => EditorMatchRule.Value(map, key);

    private static string? Text(JsonNode? node, string key) =>
        string.IsNullOrWhiteSpace(node?[key]?.ToString()) ? null : node![key]!.ToString().Trim();
}
