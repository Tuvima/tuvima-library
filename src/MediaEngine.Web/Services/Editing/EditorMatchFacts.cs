using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;

namespace MediaEngine.Web.Services.Editing;

/// <summary>One deciding fact, as the item has it now and as the candidate has it, and whether the two agree.</summary>
public sealed record EditorFactPair(string Label, string? Before, string? After, bool Agrees);

/// <summary>One way to search in Match (universal editor v4, D18).</summary>
public sealed record EditorSearchOption(string Key, string Label, string Placeholder);

/// <summary>A provider record named by an ID or a pasted link.</summary>
public sealed record EditorProviderReference(string Provider, string Id);

/// <summary>
/// The Match tab's rules that need no screen (universal editor v4, D5, D9, D18 and section 3a): the deciding facts compared per kind of
/// item, the Engine's confidence as a percentage, the search options each kind offers and how a pasted provider ID or link is read.
/// </summary>
public static partial class EditorMatchFacts
{
    public const string SearchTitle = "title";
    public const string SearchProviderId = "id";
    public const string SearchYear = "year";
    public const string SearchAnotherTitle = "another";
    public const string SearchEpisodeWords = "episode_words";
    public const string SearchEpisodeNumber = "episode_number";

    /// <summary>The Engine's confidence for a candidate as a whole percentage (0 to 100); 0 when it gave none.</summary>
    public static int ConfidencePercent(ItemCanonicalRetailCandidateDto candidate)
    {
        var score = candidate.CompositeScore > 0 ? candidate.CompositeScore : candidate.Confidence;
        return (int)Math.Round(Math.Clamp(score, 0, 1) * 100, MidpointRounding.AwayFromZero);
    }

    /// <summary>Whether the Engine said anything about how sure it is.</summary>
    public static bool HasConfidence(ItemCanonicalRetailCandidateDto candidate) => candidate.CompositeScore > 0 || candidate.Confidence > 0;

    /// <summary>
    /// The search options a kind of item offers. Each type shows only the ones that apply: an ID or link for every type with a provider that
    /// has them, a year where a year narrows the answer, another title always, and for an episode the words of its title and its number.
    /// </summary>
    public static IReadOnlyList<EditorSearchOption> SearchOptionsFor(string mediaType, string scopeId)
    {
        var options = new List<EditorSearchOption>
        {
            new(SearchTitle, "Title from your folder and file names", "Title"),
        };

        var tv = string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase);
        var episode = tv && string.Equals(scopeId, "episode", StringComparison.OrdinalIgnoreCase);
        var music = string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase);

        options.Add(new(SearchAnotherTitle, tv ? "Search another show" : "Search another title", tv ? "Another show" : "Another title"));
        options.Add(new(SearchProviderId, "Provider ID or link", ProviderIdPlaceholder(mediaType)));
        if (!episode && !music)
        {
            options.Add(new(SearchYear, "Year", "Year, such as 1997"));
        }

        if (episode)
        {
            options.Add(new(SearchEpisodeWords, "Episode title words", "Words from the episode title"));
            options.Add(new(SearchEpisodeNumber, "Episode number or air date", "3, S02E03, or 2017-02-08"));
        }

        return options;
    }

    private static string ProviderIdPlaceholder(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "tv" => "TheTVDB ID or link",
        "movies" => "IMDb or TMDB ID or link",
        "books" or "audiobooks" => "ISBN",
        "music" => "MusicBrainz ID or link",
        _ => "Provider ID or link",
    };

    /// <summary>Reads a pasted provider ID or link: a TheTVDB series, an IMDb title, a MusicBrainz id, an ISBN. Null when it is none of them.</summary>
    public static EditorProviderReference? ParseProviderReference(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (TvdbLink().Match(value) is { Success: true } tvdb)
        {
            return new EditorProviderReference("tvdb", tvdb.Groups["id"].Value);
        }

        if (ImdbId().Match(value) is { Success: true } imdb)
        {
            return new EditorProviderReference("imdb", imdb.Groups["id"].Value);
        }

        if (MusicBrainzId().Match(value) is { Success: true } mb)
        {
            return new EditorProviderReference("musicbrainz", mb.Groups["id"].Value.ToLowerInvariant());
        }

        var digits = value.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (IsbnLike().IsMatch(digits))
        {
            return new EditorProviderReference("isbn", digits);
        }

        return value.Length is >= 1 and <= 12 && value.All(char.IsAsciiDigit) ? new EditorProviderReference("tvdb", value) : null;
    }

    [GeneratedRegex(@"thetvdb\.com/(?:series|dataset/series|\?tab=series[^#]*?id=)/?(?<id>\d{1,12})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TvdbLink();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<id>tt\d{6,10})(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImdbId();

    [GeneratedRegex(@"(?<![0-9a-fA-F-])(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?![0-9a-fA-F-])", RegexOptions.CultureInvariant)]
    private static partial Regex MusicBrainzId();

    [GeneratedRegex(@"^(?:\d{9}[\dXx]|97[89]\d{10})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsbnLike();

    /// <summary>
    /// A filter over an episode list: matches title words, a number ("3", "S02E03", "E3", "absolute" numbering is not offered by the provider's
    /// season lists) or an air date ("2017-02-08", "2017"). An empty filter keeps every row.
    /// </summary>
    public static bool EpisodeMatchesFilter(int seasonNumber, int episodeNumber, string title, string? aired, string? filter)
    {
        var text = filter?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var slot = SlotFilter().Match(text);
        if (slot.Success)
        {
            var season = slot.Groups["s"].Success ? int.Parse(slot.Groups["s"].Value, CultureInfo.InvariantCulture) : seasonNumber;
            return season == seasonNumber && int.Parse(slot.Groups["e"].Value, CultureInfo.InvariantCulture) == episodeNumber;
        }

        if (text.All(char.IsAsciiDigit) && text.Length <= 3)
        {
            return int.Parse(text, CultureInfo.InvariantCulture) == episodeNumber;
        }

        if (!string.IsNullOrEmpty(aired) && aired.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => title.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"^(?:s(?<s>\d{1,3}))?\s*e(?<e>\d{1,4})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlotFilter();

    /// <summary>
    /// The deciding facts for a kind of item (spec 3a), the item's own and the candidate's side by side. Only facts at least one side has are
    /// listed; nothing else is compared on screen.
    /// </summary>
    public static IReadOnlyList<EditorFactPair> DecidingFacts(EditorMatchSubject subject, ItemCanonicalRetailCandidateDto candidate)
    {
        var tv = string.Equals(subject.MediaType, "TV", StringComparison.OrdinalIgnoreCase);
        var music = string.Equals(subject.MediaType, "Music", StringComparison.OrdinalIgnoreCase);
        var scope = subject.ScopeId.ToLowerInvariant();
        var rows = new List<EditorFactPair>();

        void Add(string label, string? before, string? after, bool numeric = false, bool minutes = false)
        {
            before = Clean(before);
            after = Clean(after);
            if (before is null && after is null)
            {
                return;
            }

            rows.Add(new EditorFactPair(label, before, after, before is not null && after is not null && Same(before, after, numeric, minutes)));
        }

        string? Mine(params string[] keys) => keys
            .Select(key => subject.Draft.TryGetValue(key, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        string? Theirs(params string[] keys) => keys.Select(key => CandidateValue(candidate, key)).FirstOrDefault(value => value is not null);

        if (tv && scope == "episode")
        {
            Add("Episode", Slot(Mine(MetadataFieldConstants.SeasonNumber), Mine(MetadataFieldConstants.EpisodeNumber)),
                Slot(Theirs(MetadataFieldConstants.SeasonNumber), Theirs(MetadataFieldConstants.EpisodeNumber)));
            Add("Title", Mine(MetadataFieldConstants.EpisodeTitle, MetadataFieldConstants.Title), candidate.Title);
            Add("Aired", Mine(MetadataFieldConstants.AirDate), Theirs(MetadataFieldConstants.AirDate));
            Add("Runtime", Mine(MetadataFieldConstants.Runtime), Theirs(MetadataFieldConstants.Runtime), minutes: true);
        }
        else if (tv)
        {
            Add("Title", subject.Title, candidate.Title);
            Add("Year", subject.Year, Theirs(MetadataFieldConstants.Year) ?? candidate.Year, numeric: true);
            Add("Network", Mine(MetadataFieldConstants.Network), Theirs(MetadataFieldConstants.Network));
        }
        else if (music && scope == "track")
        {
            Add("Track", Mine(MetadataFieldConstants.TrackNumber), Theirs(MetadataFieldConstants.TrackNumber), numeric: true);
            Add("Title", subject.Title, candidate.Title);
            Add("Length", Mine(MetadataFieldConstants.Runtime, MetadataFieldConstants.DurationField), Theirs(MetadataFieldConstants.Runtime, MetadataFieldConstants.DurationField), minutes: true);
            Add("Album", Mine(MetadataFieldConstants.Album), Theirs(MetadataFieldConstants.Album));
        }
        else if (music)
        {
            Add("Album", subject.Title, candidate.Title);
            Add("Artist", subject.Creator, Theirs(MetadataFieldConstants.Artist) ?? candidate.Author);
            Add("Year", subject.Year, Theirs(MetadataFieldConstants.Year) ?? candidate.Year, numeric: true);
            Add("Tracks", Mine(MetadataFieldConstants.TrackCount), Theirs(MetadataFieldConstants.TrackCount), numeric: true);
        }
        else if (string.Equals(subject.MediaType, "Comics", StringComparison.OrdinalIgnoreCase))
        {
            Add("Series", Mine(MetadataFieldConstants.Series), Theirs(MetadataFieldConstants.Series) ?? candidate.Title);
            Add("Issue", Mine(MetadataFieldConstants.IssueNumber), Theirs(MetadataFieldConstants.IssueNumber), numeric: true);
            Add("Cover date", subject.Year, Theirs(MetadataFieldConstants.Year) ?? candidate.Year, numeric: true);
        }
        else if (string.Equals(subject.MediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase))
        {
            Add("Title", subject.Title, candidate.Title);
            Add("Author", subject.Creator, candidate.Author ?? Theirs(MetadataFieldConstants.Author));
            Add("Narrator", Mine(MetadataFieldConstants.Narrator), Theirs(MetadataFieldConstants.Narrator));
            Add("Length", Mine(MetadataFieldConstants.Runtime, MetadataFieldConstants.DurationField), Theirs(MetadataFieldConstants.Runtime, MetadataFieldConstants.DurationField), minutes: true);
        }
        else if (string.Equals(subject.MediaType, "Books", StringComparison.OrdinalIgnoreCase))
        {
            Add("Title", subject.Title, candidate.Title);
            Add("Author", subject.Creator, candidate.Author ?? Theirs(MetadataFieldConstants.Author));
            Add("Year", subject.Year, Theirs(MetadataFieldConstants.Year) ?? candidate.Year, numeric: true);
            Add("ISBN", Mine("isbn"), Theirs("isbn"), numeric: true);
        }
        else
        {
            Add("Title", subject.Title, candidate.Title);
            Add("Year", subject.Year, Theirs(MetadataFieldConstants.Year) ?? candidate.Year, numeric: true);
            Add("Runtime", Mine(MetadataFieldConstants.Runtime), Theirs(MetadataFieldConstants.Runtime), minutes: true);
        }

        return rows;
    }

    private static string? Slot(string? season, string? episode)
    {
        if (!int.TryParse(season, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            || !int.TryParse(episode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e))
        {
            return null;
        }

        return $"S{s.ToString("00", CultureInfo.InvariantCulture)}E{e.ToString("00", CultureInfo.InvariantCulture)}";
    }

    private static string? CandidateValue(ItemCanonicalRetailCandidateDto candidate, string key)
    {
        foreach (var bag in new[] { candidate.RequiredFields, candidate.SuggestedFields, candidate.ExtraFields })
        {
            if (bag.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Same(string before, string after, bool numeric, bool minutes)
    {
        if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (minutes && TryMinutes(before, out var a) && TryMinutes(after, out var b))
        {
            return Math.Abs(a - b) <= 2;
        }

        if (numeric && int.TryParse(Leading(before), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            && int.TryParse(Leading(after), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            return x == y;
        }

        return false;
    }

    private static string Leading(string value) => new(value.TakeWhile(char.IsAsciiDigit).ToArray());

    // Runtimes arrive as minutes ("44") or as "44 min"; a value that is not a plain number of minutes is not compared.
    private static bool TryMinutes(string value, out double minutes) =>
        double.TryParse(value.Replace("min", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out minutes);

    /// <summary>How a fact is shown: minutes read "44 min"; everything else as it is.</summary>
    public static string Show(string label, string value) =>
        label is "Runtime" or "Length" && TryMinutes(value, out var minutes) ? $"{minutes.ToString("0.#", CultureInfo.InvariantCulture)} min" : value;

    /// <summary>
    /// The sentence for a provider that did not answer, naming the provider. The Engine's errors start with the provider's name
    /// ("musicbrainz search could not complete"); the display name replaces it.
    /// </summary>
    public static string ProviderDownText(IReadOnlyList<string> errors, IReadOnlyList<ItemCanonicalSearchProviderDto> providers, string? chosenProvider)
    {
        var names = new List<string>();
        foreach (var error in errors)
        {
            var match = providers.FirstOrDefault(p => error.StartsWith(p.Name, StringComparison.OrdinalIgnoreCase));
            var name = match is null ? FirstWord(error) : (string.IsNullOrWhiteSpace(match.DisplayName) ? match.Name : match.DisplayName);
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        if (names.Count == 0 && !string.IsNullOrWhiteSpace(chosenProvider))
        {
            var chosen = providers.FirstOrDefault(p => p.Name == chosenProvider);
            names.Add(chosen is null || string.IsNullOrWhiteSpace(chosen.DisplayName) ? chosenProvider : chosen.DisplayName);
        }

        return names.Count == 0
            ? "A provider did not answer. Try again, or search in another provider."
            : $"{string.Join(" and ", names)} did not answer. Try again, or search in another provider.";
    }

    private static string FirstWord(string text) => text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
}
