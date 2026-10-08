using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>Evaluates configured media identity evidence with terminal eligibility gates.</summary>
public sealed class RetailMatchScoringService : IRetailMatchScoringService
{
    private readonly IFuzzyMatchingService _fuzzy;
    private readonly IConfigurationLoader _configLoader;
    private readonly ICoverArtHashService? _coverArtHash;
    private readonly ILogger<RetailMatchScoringService>? _logger;
    public RetailMatchScoringService(IFuzzyMatchingService fuzzy, IConfigurationLoader configLoader,
        ICoverArtHashService? coverArtHash = null, ILogger<RetailMatchScoringService>? logger = null)
        => (_fuzzy, _configLoader, _coverArtHash, _logger) = (fuzzy, configLoader, coverArtHash, logger);

    public FieldMatchScores ScoreCandidate(IReadOnlyDictionary<string, string> fileHints,
        string? candidateTitle, string? candidateAuthor, string? candidateYear, MediaType mediaType,
        MatchTierConfig? matchTiers = null, CandidateExtendedMetadata? extendedMetadata = null, double structuralBonus = 0)
    {
        _ = matchTiers;
        var metadata = extendedMetadata ?? new();
        var scope = metadata.Scope ?? mediaType switch
        {
            MediaType.TV => fileHints.ContainsKey("episode_number") || fileHints.ContainsKey("episode_title") ? "episode" : "series",
            MediaType.Music => fileHints.GetValueOrDefault("match_scope") == "album" ? "album" : "track",
            MediaType.Comics => "issue",
            _ => "default",
        };
        // Provider bridge validation supplies only verified exact-ID evidence here.
        return Evaluate(fileHints, new(mediaType, scope, candidateTitle, candidateAuthor, candidateYear, metadata), structuralBonus >= .35);
    }

    public FieldMatchScores ScoreCandidate(IReadOnlyDictionary<string, string> hints, CandidateSignals candidate)
        => Evaluate(hints, candidate, false);

    private FieldMatchScores Evaluate(IReadOnlyDictionary<string, string> hints, CandidateSignals candidate, bool verifiedId)
    {
        var policy = _configLoader.LoadPipelines().GetPipelineForMediaType(candidate.MediaType).Scoring;
        if (!policy.Scopes.TryGetValue(candidate.Scope, out var matrix))
        {
            throw new InvalidOperationException($"Missing retail scoring matrix for {candidate.MediaType}/{candidate.Scope}.");
        }
        var ext = candidate.Metadata;
        string? Local(params string[] keys) => First(hints, keys);
        string? Remote(params string[] keys) => First(ext.Signals, keys);
        var fileTitle = candidate.Scope switch
        {
            "episode" => Local("episode_title"),
            "series" when candidate.MediaType == MediaType.TV => Local("show_name", "title"),
            "album" => Local("album", "title"),
            "issue" => Local("issue_title") ?? (IsGeneratedComicIssueLabel(Local("title"), hints) ? null : Local("title")),
            _ => Local("title"),
        };
        var fileCreator = candidate.Scope == "album" ? Local("album_artist", "artist", "author") : RetailHints.GetCreatorHint(hints, candidate.MediaType);
        var fileYear = RetailHints.GetYearHint(hints);
        var candidateYear = RetailHints.NormalizeYear(candidate.Year ?? (candidate.MediaType == MediaType.Comics ? Remote("series_start_year") : null));
        var values = new Dictionary<string, (string? Local, string? Remote, double? Score)>();
        double? Text(string? a, string? b) => Present(a) && Present(b) ? Similarity(a!, b!) : null;
        var placeholderTitle = Present(fileTitle) && PlaceholderTitleDetector.IsPlaceholder(fileTitle);
        var titleScore = placeholderTitle ? 0 : Text(fileTitle, candidate.Title);
        if (candidate.MediaType == MediaType.Books && Present(fileTitle) && Present(candidate.Title))
        {
            titleScore = Text(fileTitle!.Split(':')[0], candidate.Title!.Split(':')[0]);
        }
        if (candidate.MediaType == MediaType.Movies)
        {
            titleScore = Best(titleScore, Text(fileTitle, Remote("original_title")));
        }
        if (candidate.MediaType == MediaType.TV && candidate.Scope == "series" && titleScore is not null
            && !AreEquivalentComparableText(fileTitle!, candidate.Title!))
        {
            titleScore = Math.Min(titleScore.Value, RetailTextSimilarity.ComputeWordOverlap(fileTitle!, candidate.Title!));
        }
        if (placeholderTitle)
        {
            titleScore = 0;
        }
        values["title"] = (fileTitle, candidate.Title, titleScore);
        var remoteCreator = candidate.Scope == "album" ? Remote("album_artist", "artist", "author") ?? candidate.Creator : candidate.Creator;
        values["author"] = (fileCreator, remoteCreator, Present(fileCreator) && Present(remoteCreator)
            ? ComputeCreatorScore(fileCreator!, remoteCreator!, policy.CreatorListMode) : null);
        var yearDifference = Difference(fileYear, candidateYear);
        values["year"] = (fileYear, candidateYear, yearDifference is null ? null : yearDifference == 0 ? 1
            : yearDifference <= 1 ? .8 : candidate.MediaType == MediaType.Movies ? .2 : .3);
        var narrator = Local("narrator"); var remoteNarrator = Remote("narrator");
        var narratorScore = Text(narrator, remoteNarrator);
        if (Present(narrator) && !Present(remoteNarrator) && Present(ext.Description))
        {
            narratorScore = ContainsNames(ext.Description!, narrator!) ? 1 : 0;
        }
        values["narrator"] = (narrator, remoteNarrator ?? ext.Description, narratorScore);
        values["album"] = (Local("album"), Remote("album"), Text(Local("album"), Remote("album")));
        values["series"] = (Local("series", "show_name"), ext.Series ?? Remote("series", "show_name"),
            Text(Local("series", "show_name"), ext.Series ?? Remote("series", "show_name")));
        var localIssue = Local("issue_number", "series_position", "issue"); var remoteIssue = ext.IssueNumber ?? Remote("issue_number", "series_position", "issue");
        values["issue"] = (localIssue, remoteIssue, Ordinal(localIssue, remoteIssue));
        var localSeason = Local("season_number"); var remoteSeason = Remote("season_number");
        var localEpisode = Local("episode_number"); var remoteEpisode = Remote("episode_number");
        var seasonScore = Ordinal(localSeason, remoteSeason); var episodeScore = Ordinal(localEpisode, remoteEpisode);
        var hasFileNumbers = Present(localSeason) || Present(localEpisode);
        double? structureScore = !hasFileNumbers ? null : seasonScore == 1 && episodeScore == 1 ? 1 : 0;
        values["season_episode"] = (hasFileNumbers ? $"S{localSeason} E{localEpisode}" : null,
            Present(remoteSeason) || Present(remoteEpisode) ? $"S{remoteSeason} E{remoteEpisode}" : null, structureScore);
        var fileDuration = Number(Local("duration_sec", "duration_seconds")) ?? DurationMinutes(Local("duration"));
        var remoteDuration = ext.DurationSeconds ?? Number(Remote("duration_sec", "duration_seconds"));
        var durationDifference = fileDuration is > 0 && remoteDuration is > 0 ? Math.Abs(fileDuration.Value - remoteDuration.Value) : (double?)null;
        values["duration"] = (AsText(fileDuration), AsText(remoteDuration), durationDifference is null ? null : durationDifference <= 3 ? 1 : durationDifference <= 10 ? .5 : 0);
        values["track_count"] = (Local("track_count"), Remote("track_count"), Ordinal(Local("track_count"), Remote("track_count")));

        var rows = new List<RetailFieldScore>();
        var active = matrix.Fields.Where(field => values[field.Key].Score is not null || field.Value.IfMissing == "zero"
            || field.Value.IfMissing == "zero-if-file-has" && Present(values[field.Key].Local)).ToList();
        var activeWeight = active.Sum(field => field.Value.Weight);
        var composite = 0d;
        var blocks = new List<string>();
        foreach (var (key, rule) in matrix.Fields)
        {
            var value = values[key]; var missing = value.Score is null;
            var requiredMissing = missing && (rule.IfMissing == "zero" || rule.IfMissing == "zero-if-file-has" && Present(value.Local));
            var effectiveWeight = activeWeight > 0 && active.Any(field => field.Key == key) ? rule.Weight / activeWeight : 0;
            var score = value.Score ?? (requiredMissing ? 0 : (double?)null);
            var contribution = (score ?? 0) * effectiveWeight; composite += contribution;
            rows.Add(new(key, Label(key, candidate.Scope), score, effectiveWeight, missing, "weighted", contribution,
                rule.IfMissing, requiredMissing ? "required_missing" : missing ? "not_provided" : score >= .95 ? "exact" : score >= .7 ? "close" : "mismatch", value.Local, value.Remote));
            if (requiredMissing)
            {
                blocks.Add($"required_{key}_missing");
            }
        }
        var identityKeys = candidate.Scope == "issue" ? new[] { "series", "issue" } : new[] { "title", "author", "year", "season_episode", "album" };
        var identityEvidenceCount = rows.Count(row => identityKeys.Contains(row.Key) && !row.Missing && row.Score is >= .7);
        if (structureScore == 1 && Text(Local("show_name", "series"), Remote("show_name", "series") ?? ext.Series) is >= .85)
        {
            identityEvidenceCount++;
        }
        var idKeys = candidate.MediaType switch
        {
            MediaType.Books or MediaType.Audiobooks => new[] { "isbn", "isbn_13", "isbn_10", "asin" },
            MediaType.Movies => new[] { "tmdb_id", "imdb_id" },
            MediaType.TV => new[] { "tvdb_id", "tmdb_id", "imdb_id" },
            MediaType.Music when candidate.Scope == "album" => new[] { "musicbrainz_release_id", "mbid", "barcode" },
            MediaType.Music => new[] { "musicbrainz_recording_id", "mbid", "isrc" },
            MediaType.Comics => new[] { "comicvine_id" },
            _ => Array.Empty<string>(),
        };
        var exactId = verifiedId || idKeys.Any(key => Present(Local(key)) && Present(Remote(key)) && Id(Local(key)!) == Id(Remote(key)!));
        if (!exactId && identityEvidenceCount < 2)
        {
            blocks.Add("insufficient_identity_evidence");
        }

        // Explicit structural contradictions stay terminal even when a provider ID or cover agrees.
        var structuralContradiction = candidate.MediaType == MediaType.TV && candidate.Scope == "episode" && structureScore == 0
            || candidate.MediaType == MediaType.Comics && (values["issue"].Score == 0 || values["series"].Score is < .55);
        if (structuralContradiction)
        {
            blocks.Add("structural_identity_contradiction");
        }
        if (matrix.Fields.ContainsKey("author") && Present(fileCreator) && Present(remoteCreator) && values["author"].Score is < .55)
        {
            blocks.Add("required_author_contradiction");
        }
        if (placeholderTitle)
        {
            blocks.Add("placeholder_title");
        }
        var rawKind = Remote("kind", "media_type", "media_kind", "media_format");
        var signalKind = CandidateKind(rawKind);
        var knownKind = ext.Kind ?? signalKind.Type;
        var failedGate = structuralContradiction;
        foreach (var gate in matrix.Gates)
        {
            var passed = gate switch
            {
                "format" => (knownKind is null || knownKind == candidate.MediaType) && (signalKind.Scope is null || signalKind.Scope == candidate.Scope),
                "not_derivative" => RetailCandidateQualityGuard.GetRejectionReasons(candidate.MediaType, hints, candidate.Title, ext).Count == 0,
                "show_title" => Text(Local("show_name", "series"), Remote("show_name", "series") ?? ext.Series) is >= .85,
                _ => false,
            };
            var unknownFormat = gate == "format" && knownKind is null;
            rows.Add(new(gate, Label(gate, candidate.Scope), unknownFormat ? null : passed ? 1 : 0, 0, unknownFormat, "gate", 0, "zero", unknownFormat ? "not_provided" : passed ? "pass" : "fail", gate == "format" ? candidate.MediaType.ToString() : null, gate == "format" ? rawKind ?? knownKind?.ToString() : null));
            if (!passed) { failedGate = true; blocks.Add($"gate_{gate}_failed"); }
        }

        void Add(string key, string role, double amount)
        {
            if (amount == 0)
            {
                return;
            }
            composite += amount;
            rows.Add(new(key, Label(key, candidate.Scope), amount, 0, false, role, amount, "redistribute", role, null, null));
        }
        foreach (var (key, amount) in matrix.Bonuses)
        {
            var factor = key switch
            {
                "exact_id" => exactId ? 1d : 0,
                "cover" => ext.CoverArtSimilarity is > .8 ? 1 : ext.CoverArtSimilarity is > .6 ? .5 : 0,
                "publisher" => Text(Local("publisher"), ext.Publisher) is >= .85 ? 1 : 0,
                "page_count" => Within(Number(Local("page_count")), ext.PageCount, .1) ? 1 : 0,
                "series_description" => Present(Local("series")) && Present(ext.Description) && ContainsNames(ext.Description!, Local("series")!) ? 1 : 0,
                "duration" => Within(fileDuration, remoteDuration, .15) ? 1 : 0,
                "director" => Text(Local("director", "author"), Remote("director") ?? candidate.Creator) is >= .85 ? 1 : 0,
                "writer" => Text(Local("writer", "author"), Remote("writer") ?? candidate.Creator) is >= .85 ? 1 : 0,
                "track_disc" => Ordinal(Local("track_number"), Remote("track_number")) == 1 && Ordinal(Local("disc_number"), Remote("disc_number")) == 1 ? 1 : 0,
                _ => 0,
            };
            Add(key, "bonus", amount * factor);
        }
        foreach (var (key, amount) in matrix.Penalties)
        {
            var applies = key switch
            {
                "language" => KnownLanguage(Local("language")) && KnownLanguage(ext.Language) && Language(Local("language")!) != Language(ext.Language!),
                "year" => yearDifference > 3,
                "runtime" => fileDuration is > 0 && remoteDuration is > 0 && !Within(fileDuration, remoteDuration, .25),
                "duration" when candidate.MediaType == MediaType.Music => durationDifference > 30,
                "duration" => fileDuration is > 0 && remoteDuration is > 0 && !Within(fileDuration, remoteDuration, .50),
                "episode" => episodeScore == 0,
                "season" => episodeScore == 1 && seasonScore == 0,
                _ => false,
            };
            if (applies)
            {
                Add(key, "penalty", -amount);
            }
        }
        foreach (var key in new[] { "director", "writer", "publisher", "language", "isbn" })
        {
            var remote = key == "publisher" ? ext.Publisher : key == "language" ? ext.Language : Remote(key);
            var local = Local(key);
            if (Present(local) || Present(remote))
            {
                rows.Add(new(key, Label(key, candidate.Scope), null, 0, !Present(remote), "info", 0, "redistribute", "info", local, remote));
            }
        }
        composite = placeholderTitle ? 0 : Math.Clamp(composite, 0, 1);
        if (failedGate)
        {
            composite = Math.Min(composite, .50);
        }
        else if (blocks.Count > 0)
        {
            composite = Math.Min(composite, _configLoader.LoadHydration().RetailAmbiguousThreshold);
        }
        _logger?.LogDebug("Retail matrix {Type}/{Scope}: score={Score}, blocks={Blocks}", candidate.MediaType, candidate.Scope, composite, string.Join(",", blocks));
        double Field(string key) => values.GetValueOrDefault(key).Score ?? 0;
        return new()
        {
            TitleScore = Field("title"), AuthorScore = Field("author"), YearScore = Field("year"),
            FormatScore = rows.FirstOrDefault(row => row.Key == "format" && row.Role == "gate")?.Score ?? 0,
            CrossFieldBoost = rows.Where(row => row.Role is "bonus" or "penalty" && row.Key != "cover").Sum(row => row.Contribution),
            CoverArtScore = rows.Where(row => row.Key == "cover").Sum(row => row.Contribution),
            CompositeScore = Math.Round(composite, 4), FieldScores = rows, AutoAcceptBlockReasons = blocks,
        };
    }
    private static (MediaType? Type,string? Scope) CandidateKind(string? value)
    {
        var key = value?.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant();
        return key switch
        {
            "ebook" or "book" or "books" => (MediaType.Books,null),
            "audiobook" or "audiobooks" => (MediaType.Audiobooks,null),
            "movie" or "movies" or "featuremovie" => (MediaType.Movies,null),
            "tv" or "television" => (MediaType.TV,null),
            "tvepisode" => (MediaType.TV,"episode"),
            "tvseries" or "tvshow" => (MediaType.TV,"series"),
            "music" => (MediaType.Music,null),
            "song" or "musictrack" => (MediaType.Music,"track"),
            "album" or "musicalbum" => (MediaType.Music,"album"),
            "comic" or "comics" or "comicissue" => (MediaType.Comics,null),
            _ => (null,null),
        };
    }

    private double Similarity(string a, string b) => AreEquivalentComparableText(a, b) ? 1 : Math.Clamp(_fuzzy.ComputeTokenSetRatio(a,b), 0, 1);
    private static double? Best(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
    private static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
    private static string? First(IReadOnlyDictionary<string,string> values, params string[] keys) => keys.Select(key => values.GetValueOrDefault(key)).FirstOrDefault(Present);
    private static double? Number(string? value) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    private static double? DurationMinutes(string? value)
    {
        if (value?.Contains(':') == true)
        {
            var parts = value.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length is 2 or 3 && parts.All(part => Number(part) is >= 0))
            {
                return parts.Aggregate(0d, (seconds, part) => seconds * 60 + Number(part)!.Value);
            }
            return null;
        }
        return Number(value) * 60;
    }
    private static string? AsText(double? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static double? Difference(string? a, string? b) => int.TryParse(a, out var x) && int.TryParse(b, out var y) ? Math.Abs(x - y) : null;
    private static bool Within(double? a, double? b, double fraction) => a is > 0 && b is > 0 && Math.Abs(a.Value-b.Value)/a.Value <= fraction;
    private static double? Ordinal(string? a, string? b)
    {
        if (!Present(a) || !Present(b))
        {
            return null;
        }
        var x = Number(Regex.Match(a!, @"\d+(?:\.\d+)?").Value); var y = Number(Regex.Match(b!, @"\d+(?:\.\d+)?").Value);
        return x is not null && y is not null ? x == y ? 1 : 0 : string.Equals(a!.Trim(), b!.Trim(), StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }
    private static string Id(string value) => value.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
    private static string Language(string value) => value.Split('-', '_')[0].Trim().ToLowerInvariant();
    private static bool KnownLanguage(string? value) => Present(value) && Language(value!) is not ("und" or "unknown");
    private static bool ContainsNames(string text, string name) => Regex.IsMatch(RetailTextSimilarity.NormalizeComparableText(text),
        @"\b" + Regex.Escape(RetailTextSimilarity.NormalizeComparableText(name)) + @"\b", RegexOptions.CultureInvariant);
    private static string Label(string key, string scope) => key switch
    {
        "title" => scope == "episode" ? "Episode title" : scope == "album" ? "Album title" : "Title",
        "author" => scope is "track" or "album" ? "Artist" : "Author",
        "year" => "Year", "season_episode" => "Season and episode", "track_count" => "Track count",
        "show_title" => "Show identity", "not_derivative" => "Original work", "format" => "Media kind",
        "exact_id" => "Exact identifier", "series_description" => "Series in description", "track_disc" => "Track and disc",
        "page_count" => "Page count", "issue" => "Issue number", "cover" => "Cover similarity",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' ')),
    };

    private static bool IsExactComicIssueIdentity(
        IReadOnlyDictionary<string, string> fileHints,
        CandidateExtendedMetadata? extendedMetadata)
    {
        if (extendedMetadata is null)
        {
            return false;
        }

        var fileSeries = fileHints.GetValueOrDefault(MetadataFieldConstants.Series);
        var candidateSeries = extendedMetadata.Series;
        if (string.IsNullOrWhiteSpace(fileSeries) || string.IsNullOrWhiteSpace(candidateSeries))
        {
            return false;
        }

        var fileIssue = GetComicIssueHint(fileHints);
        var candidateIssue = extendedMetadata.IssueNumber;
        if (string.IsNullOrWhiteSpace(fileIssue) || string.IsNullOrWhiteSpace(candidateIssue))
        {
            return false;
        }

        return AreEquivalentComparableText(fileSeries, candidateSeries)
            && RetailHints.AreEquivalentOrdinals(fileIssue, candidateIssue);
    }

    private static string? GetComicIssueHint(IReadOnlyDictionary<string, string> fileHints)
        => fileHints.GetValueOrDefault("issue_number")
            ?? fileHints.GetValueOrDefault(MetadataFieldConstants.SeriesPosition)
            ?? fileHints.GetValueOrDefault("issue");

    private static bool IsGeneratedComicIssueLabel(
        string? title,
        IReadOnlyDictionary<string, string> fileHints)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var series = fileHints.GetValueOrDefault(MetadataFieldConstants.Series);
        var issue = GetComicIssueHint(fileHints);
        if (string.IsNullOrWhiteSpace(series) || string.IsNullOrWhiteSpace(issue))
        {
            return false;
        }

        var normalizedTitle = RetailTextSimilarity.NormalizeComparableText(
            Regex.Replace(title, @"\(\d{4}\)\s*$", string.Empty));
        var normalizedSeries = RetailTextSimilarity.NormalizeComparableText(series);
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedSeries))
        {
            return false;
        }

        if (!int.TryParse(RetailHints.ExtractLeadingDigits(issue), out var issueNumber))
        {
            return false;
        }

        var pattern = $"^{Regex.Escape(normalizedSeries)}\\s+(?:issue\\s+|no\\s+|number\\s+)?0*{issueNumber}$";
        return Regex.IsMatch(normalizedTitle, pattern, RegexOptions.IgnoreCase);
    }

    private static bool AreEquivalentComparableText(string left, string right)
    {
        return string.Equals(
            RetailTextSimilarity.NormalizeComparableText(left),
            RetailTextSimilarity.NormalizeComparableText(right),
            StringComparison.Ordinal);
    }

    private double ComputeCreatorScore(
        string fileCreator,
        string candidateCreator,
        string creatorListMode)
    {
        if (HaveSameCreatorTokens(fileCreator, candidateCreator))
        {
            return 1.0;
        }

        var fileCreators = RetailHints.SplitAuthors(fileCreator);
        var candidateCreators = RetailHints.SplitAuthors(candidateCreator);
        if (fileCreators.Count == 1 && candidateCreators.Count == 1)
        {
            return _fuzzy.ComputeTokenSetRatio(fileCreator, candidateCreator);
        }

        // Creator lists must be compared member-by-member. Token-set similarity alone
        // treats a shorter list as a perfect subset of a longer one, so a candidate such
        // as "J. R. R. Tolkien & Rafat Allam" would otherwise score 1.0 against Tolkien.
        int matched = 0;
        var usedCandidates = new HashSet<int>();
        foreach (var fileCreatorPart in fileCreators)
        {
            double bestMatch = 0.0;
            int bestIndex = -1;
            for (int i = 0; i < candidateCreators.Count; i++)
            {
                if (usedCandidates.Contains(i))
                {
                    continue;
                }

                var similarity = AreEquivalentComparableText(fileCreatorPart, candidateCreators[i])
                    ? 1.0
                    : _fuzzy.ComputeTokenSetRatio(fileCreatorPart, candidateCreators[i]);
                if (similarity > bestMatch)
                {
                    bestMatch = similarity;
                    bestIndex = i;
                }
            }

            if (bestMatch >= 0.70 && bestIndex >= 0)
            {
                matched++;
                usedCandidates.Add(bestIndex);
            }
        }

        var denominator = string.Equals(
            creatorListMode,
            "local-primary-containment",
            StringComparison.OrdinalIgnoreCase)
            ? fileCreators.Count
            : Math.Max(fileCreators.Count, candidateCreators.Count);
        return (double)matched / denominator;
    }

    private static bool HaveSameCreatorTokens(string left, string right)
    {
        var leftTokens = GetCreatorTokens(left);
        var rightTokens = GetCreatorTokens(right);

        return leftTokens.SetEquals(rightTokens);
    }

    private static HashSet<string> GetCreatorTokens(string value)
    {
        var words = RetailTextSimilarity.NormalizeComparableText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !string.Equals(token, "and", StringComparison.Ordinal))
            .ToList();
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].Length == 1)
            {
                var initials = new StringBuilder();
                while (i < words.Count && words[i].Length == 1)
                {
                    initials.Append(words[i]);
                    i++;
                }

                tokens.Add(initials.ToString());
                i--;
                continue;
            }

            tokens.Add(words[i]);
        }

        return tokens;
    }
}
