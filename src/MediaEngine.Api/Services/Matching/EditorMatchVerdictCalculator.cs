using System.Globalization;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Services.Matching;

/// <summary>The facts the verdict compares, named as they are in <c>config/scoring.json</c> (<c>match_verdict.evidence_facts</c>).</summary>
public static class MatchFactNames
{
    public const string Title = "title";
    public const string Year = "year";
    public const string Number = "number";
    public const string Creator = "creator";
    public const string Runtime = "runtime";
    public const string TrackLength = "track_length";
    public const string Identifier = "identifier";
}

/// <summary>One fact set beside the file: a tick, a cross, or "not checked", with the reason in plain words.</summary>
/// <param name="Fact">One of <see cref="MatchFactNames"/>.</param>
/// <param name="State">One of <see cref="EditorMatchEvidenceStates"/>.</param>
/// <param name="Yours">What the file says, as shown.</param>
/// <param name="Theirs">What the record says, as shown.</param>
/// <param name="Text">The reason in words ("Title matches").</param>
/// <param name="Note">A note under an approximate fact ("1:47:52 measured · 108 min (rounded)").</param>
public sealed record MatchFactCheck(string Fact, string State, string? Yours, string? Theirs, string Text, string? Note = null)
{
    public bool IsChecked => State != EditorMatchEvidenceStates.NotChecked;
}

/// <summary>
/// The Match tab's verdict (universal editor v5, 3c "Verdict"): each fact the file can speak to is set beside the record; a fact the file
/// says nothing about is "not checked" and never counts against the match. Strong means nothing differs and enough agrees; Weak means a
/// fact that says which record this is differs; anything between is Possible. The bands and tolerances come from
/// <c>match_verdict</c> in <c>config/scoring.json</c>. The verdict is advice: Apply match is always available.
/// </summary>
public sealed class EditorMatchVerdictCalculator(MatchVerdictSettings settings)
{
    private static readonly string[] LeadingArticles = ["the", "a", "an"];

    // The identifiers a record can carry, the one a person recognizes first listed first.
    private static readonly string[] IdentifierKeys = ["isbn", "imdb_id", "tmdb_id", "isrc", "musicbrainz_id", "youtube_id"];

    /// <summary>The verdict for a set of compared facts.</summary>
    public string Verdict(IReadOnlyCollection<MatchFactCheck> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var identity = new HashSet<string>(settings.IdentityFacts, StringComparer.OrdinalIgnoreCase);

        // An identifier that agrees (IMDb, TMDB, ISBN, ISRC, YouTube ID) says which record this is more surely than a title does: files carry
        // alternate and translated titles. So a title that differs on its own then reads as Possible, never Weak. Any other identity fact
        // that differs (a number, a second identifier) still reads as Weak.
        var identifierAgrees = facts.Any(fact => fact.Fact == MatchFactNames.Identifier && fact.State == EditorMatchEvidenceStates.Agrees);
        if (facts.Any(fact => fact.State == EditorMatchEvidenceStates.Differs
                              && identity.Contains(fact.Fact)
                              && !(identifierAgrees && fact.Fact == MatchFactNames.Title)))
        {
            return EditorMatchVerdicts.Weak;
        }

        if (identifierAgrees && facts.Any(fact => fact.Fact == MatchFactNames.Title && fact.State == EditorMatchEvidenceStates.Differs))
        {
            return EditorMatchVerdicts.Possible;
        }

        var differing = facts.Count(fact => fact.State == EditorMatchEvidenceStates.Differs);
        var agreeing = facts.Count(fact => fact.State == EditorMatchEvidenceStates.Agrees);
        var identityAgrees = facts.Any(fact => fact.State == EditorMatchEvidenceStates.Agrees && identity.Contains(fact.Fact));
        return differing == 0 && identityAgrees && agreeing >= settings.StrongMinAgreeingFacts
            ? EditorMatchVerdicts.Strong
            : EditorMatchVerdicts.Possible;
    }

    /// <summary>
    /// A "Why this match" row's verdict label. A fact that differs reads as Weak; one that agrees reads as Strong, or as Possible when the
    /// Engine's stored confidence in it is below the Strong band. A fact not checked has no label.
    /// </summary>
    public string? RowVerdict(string state, double? storedScore)
    {
        if (state == EditorMatchEvidenceStates.NotChecked)
        {
            return null;
        }

        if (state == EditorMatchEvidenceStates.Differs)
        {
            return storedScore is { } low && low >= settings.PossibleMinScore ? EditorMatchVerdicts.Possible : EditorMatchVerdicts.Weak;
        }

        return storedScore is { } score && score < settings.StrongMinScore
            ? score >= settings.PossibleMinScore ? EditorMatchVerdicts.Possible : EditorMatchVerdicts.Weak
            : EditorMatchVerdicts.Strong;
    }

    /// <summary>
    /// True when a result is a collection, box set or omnibus rather than one title (universal editor v6, D6): the provider says so itself
    /// (<c>is_collection</c>), or the title carries one of <c>collection_title_markers</c> as whole words. A record that has a place in a series
    /// (<paramref name="seriesNumber"/>) is one numbered title, so "The Mistborn Trilogy, Book 1" is not a collection; the provider's own flag still wins.
    /// </summary>
    public bool IsCollection(string? title, IReadOnlyDictionary<string, string>? providerFields, string? seriesNumber = null)
    {
        if (providerFields is not null
            && providerFields.TryGetValue("is_collection", out var flag)
            && (flag.Equals("true", StringComparison.OrdinalIgnoreCase) || flag == "1"))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(seriesNumber))
        {
            return false;
        }

        var words = Words(title ?? string.Empty);
        return settings.CollectionTitleMarkers
            .Select(marker => Words(marker))
            .Any(marker => marker.Count > 0 && ContainsRun(words, marker));
    }

    private static bool ContainsRun(List<string> words, List<string> run)
    {
        for (var start = 0; start + run.Count <= words.Count; start++)
        {
            if (words.Skip(start).Take(run.Count).SequenceEqual(run))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The facts the kind compares, from the configuration.</summary>
    public IReadOnlyList<string> FactsFor(string? kind) => settings.FactsFor(kind);

    public MatchFactCheck Title(string? yours, string? theirs)
    {
        if (IsBlank(yours) || IsBlank(theirs))
        {
            return NotChecked(MatchFactNames.Title, "Title", yours, theirs);
        }

        return SameTitle(yours!, theirs!)
            ? new MatchFactCheck(MatchFactNames.Title, EditorMatchEvidenceStates.Agrees, yours, theirs, "Title matches")
            : new MatchFactCheck(MatchFactNames.Title, EditorMatchEvidenceStates.Differs, yours, theirs, "Title differs");
    }

    public MatchFactCheck Year(string? yours, string? theirs)
    {
        if (!TryYear(yours, out var mine) || !TryYear(theirs, out var other))
        {
            return NotChecked(MatchFactNames.Year, "Year", yours, theirs);
        }

        return mine == other
            ? new MatchFactCheck(MatchFactNames.Year, EditorMatchEvidenceStates.Agrees, yours, theirs, "Year matches")
            : new MatchFactCheck(MatchFactNames.Year, EditorMatchEvidenceStates.Differs, yours, theirs, $"Year differs ({mine} and {other})");
    }

    /// <summary>An episode, issue, season or track number.</summary>
    public MatchFactCheck Number(int? yours, int? theirs, string word = "Number")
    {
        if (yours is null || theirs is null)
        {
            return new MatchFactCheck(
                MatchFactNames.Number, EditorMatchEvidenceStates.NotChecked, Show(yours), Show(theirs), $"{word} not checked");
        }

        return yours == theirs
            ? new MatchFactCheck(MatchFactNames.Number, EditorMatchEvidenceStates.Agrees, Show(yours), Show(theirs), $"{word} matches")
            : new MatchFactCheck(MatchFactNames.Number, EditorMatchEvidenceStates.Differs, Show(yours), Show(theirs), $"{word} differs ({yours} and {theirs})");
    }

    public MatchFactCheck Creator(string? yours, string? theirs)
    {
        if (IsBlank(yours) || IsBlank(theirs))
        {
            return NotChecked(MatchFactNames.Creator, "Creator", yours, theirs);
        }

        return SameName(yours!, theirs!)
            ? new MatchFactCheck(MatchFactNames.Creator, EditorMatchEvidenceStates.Agrees, yours, theirs, "Creator matches")
            : new MatchFactCheck(MatchFactNames.Creator, EditorMatchEvidenceStates.Differs, yours, theirs, "Creator differs");
    }

    /// <summary>
    /// The measured length of the file against a provider runtime in whole minutes. Providers round, so the two are consistent within
    /// <c>runtime_tolerance_minutes</c>.
    /// </summary>
    public MatchFactCheck Runtime(double? measuredSeconds, double? providerMinutes)
    {
        if (measuredSeconds is not > 0 || providerMinutes is not > 0)
        {
            return new MatchFactCheck(
                MatchFactNames.Runtime, EditorMatchEvidenceStates.NotChecked, ClockText(measuredSeconds), MinutesText(providerMinutes), "Runtime not checked");
        }

        var measuredMinutes = measuredSeconds.Value / 60d;
        var note = $"{ClockText(measuredSeconds)} measured · {MinutesText(providerMinutes)} (rounded)";
        return Math.Abs(measuredMinutes - providerMinutes.Value) <= settings.RuntimeToleranceMinutes
            ? new MatchFactCheck(MatchFactNames.Runtime, EditorMatchEvidenceStates.Agrees, ClockText(measuredSeconds), MinutesText(providerMinutes), "Runtime consistent", note)
            : new MatchFactCheck(
                MatchFactNames.Runtime, EditorMatchEvidenceStates.Differs, ClockText(measuredSeconds), MinutesText(providerMinutes),
                $"Runtime differs by {Math.Round(Math.Abs(measuredMinutes - providerMinutes.Value)).ToString("0", CultureInfo.InvariantCulture)} min", note);
    }

    /// <summary>A song's measured length against the record's, within <c>track_length_tolerance_seconds</c>.</summary>
    public MatchFactCheck TrackLength(double? measuredSeconds, double? providerSeconds)
    {
        if (measuredSeconds is not > 0 || providerSeconds is not > 0)
        {
            return new MatchFactCheck(
                MatchFactNames.TrackLength, EditorMatchEvidenceStates.NotChecked, ClockText(measuredSeconds), ClockText(providerSeconds), "Length not checked");
        }

        var difference = Math.Abs(measuredSeconds.Value - providerSeconds.Value);
        var note = $"{ClockText(measuredSeconds)} measured · {ClockText(providerSeconds)}";
        return difference <= settings.TrackLengthToleranceSeconds
            ? new MatchFactCheck(MatchFactNames.TrackLength, EditorMatchEvidenceStates.Agrees, ClockText(measuredSeconds), ClockText(providerSeconds), "Length consistent", note)
            : new MatchFactCheck(
                MatchFactNames.TrackLength, EditorMatchEvidenceStates.Differs, ClockText(measuredSeconds), ClockText(providerSeconds),
                $"Length differs by {Math.Round(difference).ToString("0", CultureInfo.InvariantCulture)} s", note);
    }

    /// <summary>
    /// The identifiers found with the file (an ISBN, a YouTube id, an ISRC) against the ones the record carries. Only an identifier of a
    /// kind the record also carries can be compared; when there is none, the fact is not checked.
    /// </summary>
    public MatchFactCheck Identifier(IReadOnlyCollection<EditorMatchIdentifierDto> found, IReadOnlyDictionary<string, string> recordIds)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(recordIds);
        var recordValues = recordIds.Values.Select(Compact).Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var comparable = found.Where(item => item.Kind != EditorMatchIdentifierKinds.EmbeddedTitle && recordIds.ContainsKey(KeyFor(item.Kind))).ToList();
        var anyCarried = found.Where(item => item.Kind != EditorMatchIdentifierKinds.EmbeddedTitle)
            .Any(item => recordValues.Contains(Compact(item.Value)));
        if (anyCarried)
        {
            var hit = found.First(item => recordValues.Contains(Compact(item.Value)));
            return new MatchFactCheck(
                MatchFactNames.Identifier, EditorMatchEvidenceStates.Agrees, hit.Value, hit.Value, $"{KindWord(hit.Kind)} matches");
        }

        if (comparable.Count > 0)
        {
            var miss = comparable[0];
            return new MatchFactCheck(
                MatchFactNames.Identifier, EditorMatchEvidenceStates.Differs, miss.Value, recordIds[KeyFor(miss.Kind)], $"{KindWord(miss.Kind)} differs");
        }

        // Nothing to compare: still show what each side does carry so the compare table can say which side is missing. The two are never of
        // different kinds side by side (a file's ISRC against a record's MusicBrainz id): a record identifier of the file's own kind would have been
        // comparable above, so when the file names one the record's side stays empty.
        var fileValue = found.FirstOrDefault(item => item.Kind != EditorMatchIdentifierKinds.EmbeddedTitle)?.Value;
        var recordValue = fileValue is not null
            ? null
            : IdentifierKeys.Select(key => recordIds.GetValueOrDefault(key)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return new MatchFactCheck(MatchFactNames.Identifier, EditorMatchEvidenceStates.NotChecked, fileValue, recordValue, "Identifier not checked");
    }

    /// <summary>The bridge-id key a kind of found identifier is stored under on a record.</summary>
    private static string KeyFor(string kind) => kind switch
    {
        EditorMatchIdentifierKinds.Isbn => "isbn",
        EditorMatchIdentifierKinds.YouTube => "youtube_id",
        EditorMatchIdentifierKinds.Isrc => "isrc",
        EditorMatchIdentifierKinds.Imdb => "imdb_id",
        EditorMatchIdentifierKinds.Tmdb => "tmdb_id",
        EditorMatchIdentifierKinds.MusicBrainz => "musicbrainz_id",
        _ => kind,
    };

    private static string KindWord(string kind) => kind switch
    {
        EditorMatchIdentifierKinds.Isbn => "ISBN",
        EditorMatchIdentifierKinds.YouTube => "YouTube ID",
        EditorMatchIdentifierKinds.Isrc => "ISRC",
        EditorMatchIdentifierKinds.Imdb => "IMDb ID",
        EditorMatchIdentifierKinds.Tmdb => "TMDB ID",
        EditorMatchIdentifierKinds.MusicBrainz => "MusicBrainz ID",
        _ => "Identifier",
    };

    private static MatchFactCheck NotChecked(string fact, string word, string? yours, string? theirs) =>
        new(fact, EditorMatchEvidenceStates.NotChecked, yours, theirs, $"{word} not checked");

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string? Show(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static bool TryYear(string? value, out int year)
    {
        year = 0;
        return !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length >= 4
            && int.TryParse(value.Trim().AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year)
            && year is > 0;
    }

    /// <summary>Two titles are the same when their words agree, ignoring case, punctuation, a leading article and "&amp;" against "and".</summary>
    internal static bool SameTitle(string a, string b)
    {
        var x = Words(a);
        var y = Words(b);
        if (x.Count == 0 || y.Count == 0)
        {
            return false;
        }

        if (x.SequenceEqual(y))
        {
            return true;
        }

        // A subtitle one side leaves out is fine ("Dune" against "Dune: Part One" is not: the shorter must be most of the longer).
        var (shorter, longer) = x.Count <= y.Count ? (x, y) : (y, x);
        return shorter.Count >= 2 && shorter.All(longer.Contains) && shorter.Count / (double)longer.Count >= 0.6;
    }

    /// <summary>Two names are the same when their words agree in any order ("Frank Herbert" and "Herbert, Frank") or one holds the other's.</summary>
    internal static bool SameName(string a, string b)
    {
        var x = Words(a).ToHashSet(StringComparer.Ordinal);
        var y = Words(b).ToHashSet(StringComparer.Ordinal);
        return x.Count > 0 && y.Count > 0 && (x.IsSubsetOf(y) || y.IsSubsetOf(x));
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text.Replace("&", " and ", StringComparison.Ordinal))
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        if (words.Count > 1 && LeadingArticles.Contains(words[0], StringComparer.Ordinal))
        {
            words.RemoveAt(0);
        }

        return words;
    }

    private static string Compact(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>A length as a clock: <c>1:47:52</c> over an hour, <c>4:14</c> under it.</summary>
    internal static string? ClockText(double? seconds)
    {
        if (seconds is not > 0)
        {
            return null;
        }

        var total = (int)Math.Round(seconds.Value, MidpointRounding.AwayFromZero);
        var time = TimeSpan.FromSeconds(total);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private static string? MinutesText(double? minutes) =>
        minutes is > 0 ? $"{Math.Round(minutes.Value).ToString("0", CultureInfo.InvariantCulture)} min" : null;
}
