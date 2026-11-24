using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;
using Tuvima.Wikidata;
// PersonSearchResult exists in both MediaEngine.Domain.Models and Tuvima.Wikidata
// (the v2.1+ Persons sub-service). The Domain DTO is the public contract this
// service exposes; the library type is only used internally to call the wrapper.
using PersonSearchResult = MediaEngine.Domain.Models.PersonSearchResult;
using TwPersonRole = Tuvima.Wikidata.PersonRole;
using TwPersonSearchRequest = Tuvima.Wikidata.PersonSearchRequest;
using TwPersonSearchResult = Tuvima.Wikidata.PersonSearchResult;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Standalone person reconciliation service that resolves unlinked person names
/// to Wikidata QIDs by delegating to the Tuvima.Wikidata v2.1+
/// <see cref="Services.PersonsService"/> sub-service. Phase 4 of the adapter
/// slimdown remediation collapses the previous ~440-line hand-rolled
/// implementation into this thin wrapper.
///
/// <para>
/// Three-tier confidence model (unchanged from the legacy version):
///   - Tier 1 (0.90): Structured Wikidata properties (P50, P57, P161, P175)
///   - Tier 2 (0.80): This service — standalone name search with occupation match
///   - Tier 3 (0.75): AI description extraction fallback
/// </para>
///
/// <para>
/// Auto-accept threshold: score &gt;= 0.80. Below that, the person is skipped
/// and retried at the next 30-day refresh cycle. The library's
/// <see cref="TwPersonSearchRequest.AcceptThreshold"/> defaults to 0.80, so we
/// pass the same value through.
/// </para>
///
/// <para>
/// What the library handles natively (no longer in this file):
/// role → P106 occupation mapping (replaces the OccupationsByRole dictionary),
/// musical group inclusion for Performer / Artist roles (replaces the manual
/// Q5 → Q215380 → Q5741069 fallback chain), notable-work P800 boost for the
/// title hint, and the v2.3 fix for the P106-on-musical-groups penalty that
/// previously forced consumers to lower the threshold to ~0.5 for Daft Punk /
/// Radiohead. Daft Punk now passes at the documented default 0.80.
/// </para>
/// </summary>
public sealed class PersonReconciliationService : IPersonReconciliationService
{
    private const double AutoAcceptThreshold = 0.80;

    // Run-long memory of lookups that already failed (speed: #1360). A name no Wikidata person matches is searched once, not once per
    // title that credits them; the namesakes of a name are fetched once. Entries expire so a later refresh can try again.
    private static readonly TimeSpan MemoLifetime = TimeSpan.FromHours(6);
    private readonly LookupMemo<bool> _notFound = new(MemoLifetime);
    private readonly LookupMemo<IReadOnlyList<ReconciliationResult>> _namesakes = new(MemoLifetime);

    private readonly WikidataReconciler? _reconciler;
    private readonly IConfigurationLoader _configLoader;
    private readonly ILogger<PersonReconciliationService> _logger;

    public PersonReconciliationService(
        IConfigurationLoader configLoader,
        ILogger<PersonReconciliationService> logger,
        WikidataReconciler? reconciler = null)
    {
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(logger);
        _configLoader = configLoader;
        _logger = logger;
        _reconciler = reconciler;
    }

    public async Task<PersonSearchResult?> SearchPersonAsync(
        string name,
        string expectedRole,
        string? workTitle = null,
        CancellationToken ct = default,
        string? contextText = null)
    {
        if (_reconciler is null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var language = _configLoader.LoadCore().Language?.Metadata ?? "en";

        var memoKey = LookupMemo<bool>.Key(name, expectedRole, language);
        if (_notFound.TryGet(memoKey, out _))
        {
            _logger.LogDebug("Person reconciliation skipped: '{Name}' ({Role}) already searched without a match in this run", name, expectedRole);
            return null;
        }

        TwPersonSearchResult libResult;
        try
        {
            libResult = await RetryTransientAsync(() => _reconciler.Persons.SearchAsync(
                new TwPersonSearchRequest
                {
                    Name = name,
                    Role = MapRole(expectedRole),
                    TitleHint = workTitle,
                    Language = language,
                    AcceptThreshold = AutoAcceptThreshold,
                    // IncludeMusicalGroups left null — the library defaults
                    // Performer / Artist to true and every other role to false,
                    // which matches the legacy MusicRoles set.
                }, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Person reconciliation search failed for '{Name}' ({Role})", name, expectedRole);
            return null;
        }

        if (!libResult.Found || string.IsNullOrWhiteSpace(libResult.Qid))
        {
            _logger.LogDebug(
                "Person reconciliation auto-skipped: '{Name}' ({Role}) - best score {Score:F2} below threshold {Threshold:F2}",
                name, expectedRole, libResult.Score, AutoAcceptThreshold);
            _notFound.Set(memoKey, true);
            return null;
        }

        var evidenceLabels = await ResolveEvidenceLabelsAsync([libResult], ct).ConfigureAwait(false);
        if (!HasCorroboratingIdentityEvidence(libResult, expectedRole, workTitle, evidenceLabels))
        {
            _logger.LogWarning(
                "Person reconciliation rejected an exact-name result without compatible role/work evidence: '{Name}' ({Role}) -> {Qid}",
                name, expectedRole, libResult.Qid);
            return null;
        }

        // Several people can share one name (a comedian and a basketball player both called Stephen Curry). Unless one of the
        // person's known works is this title, the work's own description decides: it picks the one namesake it fits, and when it
        // fits none of them (or several) the person is left unmatched and retried later rather than guessed from name and job alone.
        var chosenQid = libResult.Qid!;
        var chosenName = libResult.CanonicalName ?? name;
        var chosenScore = libResult.Score;
        if (!NotableWorkMatches(ResolveLabels(libResult.NotableWorks, evidenceLabels), workTitle))
        {
            var namesakes = await FindNamesakesAsync(name, language, ct).ConfigureAwait(false);
            var fit = ChooseNamesakeFromContext(namesakes, $"{workTitle} {contextText}", chosenQid);
            if (fit.Outcome == NamesakeOutcome.Unmatched)
            {
                _logger.LogWarning(
                    "Person reconciliation left unmatched: '{Name}' ({Role}) shares its name with other people and the work's text does not tell which one",
                    name, expectedRole);
                return null;
            }

            if (fit.Outcome == NamesakeOutcome.Switch && fit.Person is { } other)
            {
                _logger.LogInformation(
                    "Person reconciliation chose the namesake the work's description fits: '{Name}' ({Role}) -> {Qid}",
                    name, expectedRole, other.Id);
                chosenQid = other.Id;
                chosenName = other.Name;
            }
        }

        _logger.LogInformation(
            "Person reconciliation auto-accepted: '{Name}' ({Role}) -> {QID} '{WikiName}' (score={Score:F2})",
            name, expectedRole, chosenQid, chosenName, chosenScore);

        return new PersonSearchResult(chosenQid, chosenName, chosenScore);
    }

    /// <summary>People on Wikidata whose name is exactly this name (best five, no job filter, so a namesake is never hidden).</summary>
    private async Task<IReadOnlyList<ReconciliationResult>> FindNamesakesAsync(
        string name, string language, CancellationToken ct)
    {
        var key = LookupMemo<bool>.Key(name, "namesakes", language);
        if (_namesakes.TryGet(key, out var remembered))
        {
            return remembered;
        }

        try
        {
            var matches = await RetryTransientAsync(() => _reconciler!.Reconcile.ReconcileAsync(
                new ReconciliationRequest { Query = name, Types = ["Q5"], Language = language, Limit = 5 }, ct), ct)
                .ConfigureAwait(false);
            var wanted = NormalizeEvidence(name);
            IReadOnlyList<ReconciliationResult> found = matches.Where(match => NormalizeEvidence(match.Name) == wanted).ToList();
            _namesakes.Set(key, found);
            return found;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Best effort: if the namesake check cannot run, keep the match the search already made.
            _logger.LogDebug(ex, "Namesake check failed for '{Name}'; keeping the search result", name);
            return [];
        }
    }

    /// <summary>
    /// True when exactly one namesake's Wikidata description shares a meaningful word with the work's text (for example "basketball")
    /// and that namesake is not the person the search chose. No fit, a fit with the chosen person, or a fit with several, is false.
    /// </summary>
    internal static bool ContextFavoursAnotherNamesake(
        IReadOnlyList<ReconciliationResult> namesakes, string chosenQid, string? contextText)
    {
        if (namesakes.Count < 2)
        {
            return false;
        }

        var contextWords = SignificantWords(contextText);
        if (contextWords.Count == 0)
        {
            return false;
        }

        var fits = namesakes
            .Where(candidate => SignificantWords(candidate.Description).Any(word =>
                contextWords.Any(other => word.StartsWith(other, StringComparison.OrdinalIgnoreCase)
                    || other.StartsWith(word, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        return fits.Count == 1 && !string.Equals(fits[0].Id, chosenQid, StringComparison.OrdinalIgnoreCase);
    }

    internal enum NamesakeOutcome { Keep, Switch, Unmatched }

    internal readonly record struct NamesakeChoice(NamesakeOutcome Outcome, ReconciliationResult? Person);

    /// <summary>
    /// Decides between people sharing one exact name. One person (or none, when nobody else shares the name) means keep the
    /// search's pick. Several people: the one whose Wikidata description the work's text fits wins (Switch when that is not the
    /// search's pick, Keep when it is); a text that fits none or several of them leaves the person Unmatched.
    /// </summary>
    internal static NamesakeChoice ChooseNamesakeFromContext(
        IReadOnlyList<ReconciliationResult> namesakes, string? contextText, string? chosenQid = null)
    {
        if (namesakes.Count < 2)
        {
            return new NamesakeChoice(NamesakeOutcome.Keep, null);
        }

        var contextWords = SignificantWords(contextText);
        var fits = namesakes
            .Where(candidate => SignificantWords(candidate.Description).Any(word =>
                contextWords.Any(other => word.StartsWith(other, StringComparison.OrdinalIgnoreCase)
                    || other.StartsWith(word, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (fits.Count != 1)
        {
            return new NamesakeChoice(NamesakeOutcome.Unmatched, null);
        }

        return new NamesakeChoice(
            chosenQid is not null && string.Equals(fits[0].Id, chosenQid, StringComparison.OrdinalIgnoreCase)
                ? NamesakeOutcome.Keep
                : NamesakeOutcome.Switch,
            fits[0]);
    }

    // A league or sport named in the work's text points at the same people as the sport's own word on Wikidata
    // ("NBA champion" and "American basketball player"), so the short name is read as the longer word.
    private static readonly Dictionary<string, string> HintAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nba"] = "basketball",
        ["wnba"] = "basketball",
        ["nfl"] = "football",
        ["mlb"] = "baseball",
        ["nhl"] = "hockey",
        ["fifa"] = "footballer",
        ["pga"] = "golfer",
        ["atp"] = "tennis",
        ["wta"] = "tennis",
    };

    // Words that describe nearly everyone with a job, so they never tell two people apart.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "american", "british", "english", "canadian", "australian", "which", "about", "their", "player", "actor", "actress",
        "writer", "singer", "musician", "comedian", "series", "season", "episode", "episodes", "person", "people", "born",
        "known", "professional", "former", "current",
    };

    private static HashSet<string> SignificantWords(string? text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return words;
        }

        foreach (var raw in text.Split(
                     [' ', ',', '.', ';', ':', '(', ')', '-', '/', '"', '\'', '!', '?', '\n', '\r', '\t'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (HintAliases.TryGetValue(raw, out var alias))
            {
                words.Add(alias);
            }
            else if (raw.Length >= 5 && !StopWords.Contains(raw) && !raw.All(char.IsDigit))
            {
                words.Add(raw);
            }
        }

        return words;
    }

    public async Task<IReadOnlyDictionary<string, PersonSearchResult?>> SearchPersonsBatchAsync(
        IReadOnlyList<(string Name, string Role, string? WorkTitle)> requests,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, PersonSearchResult?>(StringComparer.OrdinalIgnoreCase);
        if (_reconciler is null || requests.Count == 0)
        {
            return results;
        }

        // Deduplicate by name (case-insensitive), keeping the first occurrence's
        // Role and WorkTitle. Mirrors the legacy implementation's contract.
        var seen = new Dictionary<string, (string Name, string Role, string? WorkTitle)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var req in requests)
        {
            if (!string.IsNullOrWhiteSpace(req.Name) && !seen.ContainsKey(req.Name))
            {
                seen[req.Name] = req;
            }
        }

        if (seen.Count == 0)
        {
            return results;
        }

        // The Wikidata client deduplicates and bounds provider concurrency internally.
        // We still deduplicate here so the result map preserves this service's
        // name-keyed contract for downstream callers.
        var language = _configLoader.LoadCore().Language?.Metadata ?? "en";
        var uniqueRequests = seen.Values.ToList();
        IReadOnlyList<TwPersonSearchResult> batchResults;
        try
        {
            batchResults = await RetryTransientAsync(() => _reconciler.Persons.SearchBatchAsync(
                uniqueRequests.Select(req => new TwPersonSearchRequest
                {
                    Name = req.Name,
                    Role = MapRole(req.Role),
                    TitleHint = req.WorkTitle,
                    Language = language,
                    AcceptThreshold = AutoAcceptThreshold,
                }).ToList(),
                ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Person reconciliation batch search failed for {Count} person(s)", seen.Count);
            return results;
        }

        var evidenceLabels = await ResolveEvidenceLabelsAsync(batchResults, ct).ConfigureAwait(false);
        for (var i = 0; i < uniqueRequests.Count; i++)
        {
            var (personName, role, _) = uniqueRequests[i];
            var libResult = batchResults[i];
            if (!libResult.Found || string.IsNullOrWhiteSpace(libResult.Qid))
            {
                _logger.LogDebug(
                    "Person reconciliation auto-skipped: '{Name}' ({Role}) - best score {Score:F2} below threshold {Threshold:F2}",
                    personName, role, libResult.Score, AutoAcceptThreshold);
                results[personName.ToLowerInvariant()] = null;
                continue;
            }

            if (!HasCorroboratingIdentityEvidence(libResult, role, uniqueRequests[i].WorkTitle, evidenceLabels))
            {
                _logger.LogWarning(
                    "Person reconciliation rejected an exact-name result without compatible role/work evidence: '{Name}' ({Role}) -> {Qid}",
                    personName, role, libResult.Qid);
                results[personName.ToLowerInvariant()] = null;
                continue;
            }

            var canonicalName = libResult.CanonicalName ?? personName;
            _logger.LogInformation(
                "Person reconciliation auto-accepted: '{Name}' ({Role}) -> {QID} '{WikiName}' (score={Score:F2})",
                personName, role, libResult.Qid, canonicalName, libResult.Score);

            results[personName.ToLowerInvariant()] = new PersonSearchResult(
                libResult.Qid!,
                canonicalName,
                libResult.Score);
        }

        return results;
    }

    private const int SearchAttempts = 3;

    /// <summary>
    /// A search that failed on a passing problem (a timeout, a busy provider) is tried again before giving up. Without
    /// this a person got a Wikidata id on one run and none on the next, depending on when the provider hiccuped.
    /// </summary>
    private async Task<T> RetryTransientAsync<T>(Func<Task<T>> search, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await search().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < SearchAttempts)
            {
                _logger.LogDebug(ex, "Person reconciliation search failed on attempt {Attempt}; trying again", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Translates the consumer's string-typed role into the library's
    /// <see cref="TwPersonRole"/> enum. Roles outside the known set fall back
    /// to <see cref="TwPersonRole.Unknown"/>, which causes the library to
    /// skip the occupation filter entirely.
    /// </summary>
    private static TwPersonRole MapRole(string expectedRole) => expectedRole switch
    {
        // Match-case insensitive comparison via ToLowerInvariant first.
        _ when string.Equals(expectedRole, "Author", StringComparison.OrdinalIgnoreCase) => TwPersonRole.Author,
        _ when string.Equals(expectedRole, "Narrator", StringComparison.OrdinalIgnoreCase) => TwPersonRole.Narrator,
        _ when string.Equals(expectedRole, "Director", StringComparison.OrdinalIgnoreCase) => TwPersonRole.Director,
        _ when string.Equals(expectedRole, "Actor", StringComparison.OrdinalIgnoreCase) => TwPersonRole.Actor,
        _ when string.Equals(expectedRole, "VoiceActor", StringComparison.OrdinalIgnoreCase) => TwPersonRole.VoiceActor,
        _ when string.Equals(expectedRole, "Composer", StringComparison.OrdinalIgnoreCase) => TwPersonRole.Composer,
        _ when string.Equals(expectedRole, "Performer", StringComparison.OrdinalIgnoreCase) => TwPersonRole.Performer,
        _ when string.Equals(expectedRole, "Artist", StringComparison.OrdinalIgnoreCase) => TwPersonRole.Artist,
        _ when string.Equals(expectedRole, "Screenwriter", StringComparison.OrdinalIgnoreCase) => TwPersonRole.Screenwriter,
        _ => TwPersonRole.Unknown,
    };

    private async Task<IReadOnlyDictionary<string, string?>> ResolveEvidenceLabelsAsync(
        IReadOnlyList<TwPersonSearchResult> results, CancellationToken ct)
    {
        // The client returns P106/P800 entity IDs, not display labels. Resolve in
        // English because the role vocabulary below is English, independently of
        // the user's metadata language. One batch also covers batch reconciliation.
        var qids = results.Where(result => result.Found && !result.IsGroup)
            .SelectMany(result => result.Occupations.Concat(result.NotableWorks))
            .Where(IsQid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return qids.Count == 0 || _reconciler is null
            ? new Dictionary<string, string?>()
            : await _reconciler.Labels.GetBatchAsync(qids, language: "en", cancellationToken: ct)
                .ConfigureAwait(false);
    }

    private static bool IsQid(string value) => value.Length > 1
        && (value[0] is 'Q' or 'q') && value.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    internal static bool HasCorroboratingIdentityEvidence(
        TwPersonSearchResult result,
        string expectedRole,
        string? workTitle,
        IReadOnlyDictionary<string, string?>? evidenceLabels = null)
    {
        if (result.IsGroup)
        {
            return expectedRole.Equals("Performer", StringComparison.OrdinalIgnoreCase)
                || expectedRole.Equals("Artist", StringComparison.OrdinalIgnoreCase);
        }

        if (NotableWorkMatches(ResolveLabels(result.NotableWorks, evidenceLabels), workTitle))
        {
            return true;
        }

        var acceptedTerms = expectedRole.Trim().ToLowerInvariant() switch
        {
            "author" => new[] { "author", "writer", "novelist", "poet", "journalist", "playwright" },
            "narrator" => new[] { "narrator", "voice actor", "actor", "performer" },
            "director" => new[] { "director", "filmmaker" },
            "actor" or "voiceactor" => new[] { "actor", "actress", "voice actor", "performer" },
            "composer" => new[] { "composer", "musician", "songwriter" },
            "performer" or "artist" => new[] { "musician", "singer", "rapper", "performer", "actor", "instrumentalist", "recording artist" },
            "screenwriter" => new[] { "screenwriter", "writer", "playwright" },
            "producer" => new[] { "producer" },
            _ => Array.Empty<string>(),
        };

        return acceptedTerms.Length > 0 && ResolveLabels(result.Occupations, evidenceLabels).Any(occupation =>
            acceptedTerms.Any(term => occupation.Equals(term, StringComparison.OrdinalIgnoreCase)
                || occupation.EndsWith(" " + term, StringComparison.OrdinalIgnoreCase)
                || occupation.StartsWith(term + "-", StringComparison.OrdinalIgnoreCase)
                || occupation.EndsWith("-" + term, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> ResolveLabels(
        IEnumerable<string> values, IReadOnlyDictionary<string, string?>? labels) =>
        values.Select(value => IsQid(value) ? labels?.GetValueOrDefault(value) : value)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!);

    private static bool NotableWorkMatches(IEnumerable<string> notableWorks, string? workTitle)
    {
        if (string.IsNullOrWhiteSpace(workTitle))
        {
            return false;
        }

        var normalizedTitle = NormalizeEvidence(workTitle);
        return normalizedTitle.Length >= 4 && notableWorks.Any(work =>
        {
            var normalizedWork = NormalizeEvidence(work);
            return normalizedWork.Length >= 4 && (normalizedWork.Equals(normalizedTitle, StringComparison.Ordinal)
                || normalizedWork.StartsWith(normalizedTitle, StringComparison.Ordinal)
                || normalizedTitle.StartsWith(normalizedWork, StringComparison.Ordinal));
        });
    }

    private static string NormalizeEvidence(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
