using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Services;

/// <summary>
/// <see cref="FilenameCleaningConfiguration"/> compiled and ready to use (universal editor v5, D33): the one reader of release noise,
/// separators, years, season-and-episode markers, track and disc numbers and edition markers that the editor's pre-fill, the Videos
/// title cleaner and the scan share. Built by <see cref="Compile"/>, which checks every pattern, so a broken file is refused as a whole
/// instead of failing in the middle of a scan.
/// </summary>
public sealed class FilenameCleaningRules
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex Brackets = new(@"[\[\(][^\]\)]*[\]\)]", RegexOptions.CultureInvariant | RegexOptions.Compiled, PatternTimeout);

    private readonly HashSet<string> _noiseTokens;
    private readonly Regex[] _noiseTokenPatterns;
    private readonly string[] _editionMarkers;
    private readonly Regex _releaseStyleTags;
    private readonly Action<string> _warn;
    private readonly ConcurrentDictionary<string, byte> _timedOut = new(StringComparer.Ordinal);

    private FilenameCleaningRules(
        FilenameCleaningConfiguration configuration,
        HashSet<string> noiseTokens,
        Regex[] noiseTokenPatterns,
        string plainFragments,
        string ambiguousFragments,
        Regex releaseTags,
        Regex releaseStyleTags,
        Regex year,
        Regex[] seasonEpisode,
        Regex seasonFolder,
        Regex[] trackPatterns,
        string[] editionMarkers,
        Action<string> warn)
    {
        _warn = warn;
        Configuration = configuration;
        _noiseTokens = noiseTokens;
        _noiseTokenPatterns = noiseTokenPatterns;
        PlainFragments = plainFragments;
        AmbiguousFragments = ambiguousFragments;
        ReleaseTags = releaseTags;
        _releaseStyleTags = releaseStyleTags;
        Year = year;
        SeasonEpisode = seasonEpisode;
        SeasonFolder = seasonFolder;
        TrackPatterns = trackPatterns;
        _editionMarkers = editionMarkers;
    }

    /// <summary>The configuration these rules were compiled from.</summary>
    public FilenameCleaningConfiguration Configuration { get; }

    /// <summary>The characters a release-style name uses in place of spaces.</summary>
    public IReadOnlyList<string> Separators => Configuration.Separators;

    /// <summary>The noise words and patterns as one regular-expression alternation, for the lookup name cleaner. Ambiguous words are not in it.</summary>
    public string PlainFragments { get; }

    /// <summary>The ambiguous noise words as one regular-expression alternation; empty when there are none.</summary>
    public string AmbiguousFragments { get; }

    /// <summary>The first release noise and everything after it, which a title never contains. Ambiguous words are not noise here.</summary>
    public Regex ReleaseTags { get; }

    /// <summary>A year with its brackets or separators, the year itself in the group <c>y</c>.</summary>
    public Regex Year { get; }

    public IReadOnlyList<Regex> SeasonEpisode { get; }

    public Regex SeasonFolder { get; }

    public IReadOnlyList<Regex> TrackPatterns { get; }

    /// <summary>
    /// Compiles the rules. Null (with the reason) when a pattern does not compile or a rule the readers cannot work without is empty.
    /// <paramref name="warn"/> hears of a pattern that ran out of time on a name (once per pattern); the name is then read as if the pattern
    /// had not matched.
    /// </summary>
    public static FilenameCleaningRules? Compile(FilenameCleaningConfiguration configuration, out string? problem, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        problem = null;
        try
        {
            var plain = new List<string>();
            var noiseTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ambiguousWords = Words(configuration.AmbiguousWords).ToList();
            var ambiguousSet = new HashSet<string>(ambiguousWords, StringComparer.OrdinalIgnoreCase);
            foreach (var word in configuration.NoiseWords.Values.SelectMany(list => list).Concat(configuration.CustomNoiseWords))
            {
                var trimmed = word?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                noiseTokens.Add(trimmed);
                noiseTokens.Add(trimmed.Replace("-", string.Empty, StringComparison.Ordinal));
                if (!ambiguousSet.Contains(trimmed))
                {
                    plain.Add(WordFragment(trimmed));
                }
            }

            foreach (var word in ambiguousWords)
            {
                noiseTokens.Add(word);
            }

            var patterns = configuration.NoisePatterns.Where(pattern => !string.IsNullOrWhiteSpace(pattern)).ToList();
            var tokenPatterns = patterns
                .Select(pattern => new Regex($"^(?:{pattern})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout))
                .ToArray();
            plain.AddRange(patterns);
            if (plain.Count == 0)
            {
                problem = "it lists no noise words";
                return null;
            }

            var plainFragments = string.Join('|', plain.Distinct(StringComparer.OrdinalIgnoreCase));
            var ambiguousFragments = string.Join('|', ambiguousWords.Select(WordFragment));
            var releaseTags = new Regex(
                $@"(?<![A-Za-z0-9])(?:{plainFragments})(?![A-Za-z0-9]).*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);

            var releaseStyleTags = ambiguousWords.Count == 0
                ? releaseTags
                : new Regex(
                    $@"(?<![A-Za-z0-9])(?:{plainFragments}|{ambiguousFragments})(?![A-Za-z0-9]).*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);

            if (string.IsNullOrWhiteSpace(configuration.Year.Pattern))
            {
                problem = "it has no year rule";
                return null;
            }

            var year = new Regex(
                $@"(?:[\(\[]|(?<=[ ._-]))(?<y>{configuration.Year.Pattern})(?:[\)\]]|(?=[ ._-]|$))",
                RegexOptions.CultureInvariant, PatternTimeout);

            var seasonEpisode = configuration.SeasonEpisodePatterns
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout))
                .ToArray();
            if (seasonEpisode.Any(pattern => !pattern.GetGroupNames().Contains("s", StringComparer.Ordinal) || !pattern.GetGroupNames().Contains("e", StringComparer.Ordinal)))
            {
                problem = "a season-and-episode pattern lacks the groups s and e";
                return null;
            }

            if (string.IsNullOrWhiteSpace(configuration.SeasonFolderPattern))
            {
                problem = "it has no season-folder rule";
                return null;
            }

            var seasonFolder = new Regex(configuration.SeasonFolderPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
            var tracks = configuration.TrackPatterns
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout))
                .ToArray();
            if (tracks.Any(pattern => !pattern.GetGroupNames().Contains("track", StringComparer.Ordinal)))
            {
                problem = "a track pattern lacks the group track";
                return null;
            }

            var editions = Words(configuration.EditionMarkers).OrderByDescending(marker => marker.Length).ToArray();
            return new FilenameCleaningRules(
                configuration, noiseTokens, tokenPatterns, plainFragments, ambiguousFragments, releaseTags, releaseStyleTags, year, seasonEpisode, seasonFolder, tracks, editions,
                warn ?? (message => System.Diagnostics.Trace.TraceWarning(message)));
        }
        catch (ArgumentException ex)
        {
            problem = $"a pattern in it is not valid ({ex.Message})";
            return null;
        }
    }

    /// <summary>True when the token (one word of a name, without brackets) is release noise.</summary>
    public bool IsNoiseWord(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        return _noiseTokens.Contains(trimmed)
            || _noiseTokenPatterns.Any(pattern => Guard(pattern.ToString(), () => pattern.IsMatch(trimmed), false));
    }

    /// <summary>
    /// Takes the brackets and the first release noise (and all after it) out of a name, and says which edition marker was among
    /// them. A year is not touched; <see cref="StripYear"/> does that.
    /// </summary>
    public string StripTags(string text, out string? edition)
    {
        edition = FindEdition(text);
        // A release-style name (dots between words, no spaces) has no ordinary words among its tags, so the ambiguous ones count too.
        var tags = text.Contains(' ') ? ReleaseTags : _releaseStyleTags;
        var unbracketed = Guard(Brackets.ToString(), () => Brackets.Replace(text, " "), text);
        var stripped = Guard(tags.ToString(), () => tags.Replace(unbracketed, string.Empty), unbracketed);
        return edition is null ? stripped : RemoveMarkerAfterYear(stripped, edition);
    }

    /// <summary><see cref="StripTags(string, out string?)"/> without the edition.</summary>
    public string StripTags(string text) => StripTags(text, out _);

    public string StripYear(string text) => Guard(Year.ToString(), () => Year.Replace(text, " "), text);

    /// <summary>The year the name carries, or null.</summary>
    public string? FindYear(string text) => MatchYear(text) is { Success: true } match ? match.Groups["y"].Value : null;

    private Match MatchYear(string text) => Guard(Year.ToString(), () => Year.Match(text), Match.Empty);

    /// <summary>
    /// The edition marker a name carries: one in brackets, or one after the year or after the release noise starts
    /// ("Movie.2019.Extended.1080p"). A marker that is part of the title ("Live and Let Die 1973") is not an edition.
    /// </summary>
    public string? FindEdition(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _editionMarkers.Length == 0)
        {
            return null;
        }

        var groups = Guard(Brackets.ToString(), () => Brackets.Matches(text).Select(group => group.Value).ToList(), []);
        foreach (var group in groups)
        {
            if (MarkerIn(group) is { } inBrackets)
            {
                return inBrackets;
            }
        }

        var yearMatch = MatchYear(text);
        if (yearMatch.Success && MarkerIn(text[(yearMatch.Index + yearMatch.Length)..]) is { } afterYear)
        {
            return afterYear;
        }

        var tailStart = Guard(ReleaseTags.ToString(), () => ReleaseTags.Match(text), Match.Empty);
        return tailStart.Success ? MarkerIn(tailStart.Value) : null;
    }

    // A marker written after the year ("Movie.2019.Remastered") is not part of the title, so it leaves with the noise.
    private string RemoveMarkerAfterYear(string text, string marker)
    {
        var yearMatch = MatchYear(text);
        if (!yearMatch.Success)
        {
            return text;
        }

        var cut = yearMatch.Index + yearMatch.Length;
        var pattern = MarkerPattern(marker);
        var tail = text[cut..];
        return text[..cut] + Guard(pattern, () => Regex.Replace(tail, pattern, " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout), tail);
    }

    /// <summary>
    /// Turns the separators of a name into spaces and tidies the result; null when nothing is left. With <paramref name="releaseStyleOnly"/> a name
    /// that already has spaces keeps its full stops ("Dr. Strangelove"); underscores are always spaces.
    /// </summary>
    public string? Tidy(string? text, bool releaseStyleOnly = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var spaced = text;
        if (!releaseStyleOnly || !spaced.Contains(' '))
        {
            foreach (var separator in Separators.Where(separator => separator.Length > 0))
            {
                spaced = spaced.Replace(separator, " ", StringComparison.Ordinal);
            }
        }
        else
        {
            spaced = spaced.Replace('_', ' ');
        }

        spaced = Regex.Replace(spaced, @"\s+", " ").Trim(' ', '-', '–', '—');
        return spaced.Length == 0 ? null : spaced;
    }

    /// <summary>Reads the disc and track number at the start of a music file name; the first pattern that matches wins.</summary>
    public (int? Disc, int? Track, string Remainder) ReadTrack(string name)
    {
        foreach (var pattern in TrackPatterns)
        {
            var match = Guard(pattern.ToString(), () => pattern.Match(name), Match.Empty);
            if (!match.Success)
            {
                continue;
            }

            int? track = int.TryParse(match.Groups["track"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : null;
            int? disc = match.Groups["disc"] is { Success: true } d && int.TryParse(d.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
            var rest = Guard(pattern.ToString(), () => pattern.Replace(name, string.Empty, 1), name).Trim(' ', '-', '_', '.');
            return (disc, track, rest.Length == 0 ? name.Trim() : rest);
        }

        return (null, null, name.Trim());
    }

    private string? MarkerIn(string text)
    {
        foreach (var marker in _editionMarkers)
        {
            var pattern = MarkerPattern(marker);
            if (Guard(pattern, () => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout), false))
            {
                return char.ToUpperInvariant(marker[0]) + marker[1..];
            }
        }

        return null;
    }

    // A pattern that runs out of time on a name is read as "no match" for that name and reported once, so a bad pattern in the file cannot
    // stall or fail a scan or the editor's pre-fill, and the log is not flooded by it either.
    private T Guard<T>(string pattern, Func<T> run, T fallback)
    {
        try
        {
            return run();
        }
        catch (RegexMatchTimeoutException)
        {
            if (_timedOut.TryAdd(pattern, 0))
            {
                _warn($"A file-name cleaning pattern took too long on a name and was skipped: {pattern}");
            }

            return fallback;
        }
    }

    private static string MarkerPattern(string marker) =>
        $@"(?<![A-Za-z0-9]){Regex.Escape(marker).Replace(@"\ ", @"[ ._-]")}(?![A-Za-z0-9])";

    private static IEnumerable<string> Words(IEnumerable<string>? words) =>
        (words ?? []).Select(word => word?.Trim() ?? string.Empty).Where(word => word.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    // A word as a pattern: written out literally, a hyphen inside it optional, a space inside it any separator.
    private static string WordFragment(string word) =>
        Regex.Escape(word).Replace("-", "-?", StringComparison.Ordinal).Replace(@"\ ", "[ ._-]", StringComparison.Ordinal);
}
