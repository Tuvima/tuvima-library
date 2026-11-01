using System.Globalization;
using System.Text;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Collections.ListImport;

/// <summary>What a list entry says about the title it stands for. Built from a parsed entry or from a stored one.</summary>
public sealed record ListMatchInput(
    string Title,
    int? Year,
    string? Creator,
    IReadOnlyDictionary<string, string> Ids,
    IReadOnlyList<string> MediaTypes);

/// <summary>The library title an entry matched and how: an identifier name (<c>imdb_id</c>) or <c>title</c>.</summary>
public sealed record ListMatch(Guid WorkId, string Basis);

/// <summary>
/// Matches list entries to the titles Library owns (Collections and lists spec D6). Identifiers come first: an IMDb, TMDB, TVDB,
/// ISBN, ASIN, MusicBrainz or ComicVine identifier in the list is compared with the identifiers Library stores, and it
/// matches only when exactly one title holds it. Entries without a usable identifier match by title and year: an exact title
/// (ignoring case, accents, punctuation and a leading "The") with the year agreeing, or a close title that scores at or above
/// the same confidence line the identity pipeline uses to link automatically. Anything below the line, and anything that fits
/// more than one title, is left unmatched: the matcher never guesses.
/// </summary>
public sealed class ListEntryMatcher
{
    private const double CloseTitleScore = 0.90;
    private const double RunnerUpMargin = 0.05;
    private const int MaximumFuzzyCandidates = 40;
    private const int CommonTokenPostings = 400;

    private readonly IFuzzyMatchingService _fuzzy;
    private readonly double _confidenceLine;
    private readonly List<ListImportCandidate> _candidates;
    private readonly Dictionary<string, List<int>> _byTitle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, string Value), List<int>> _byId = [];

    /// <param name="candidates">The owned titles to match against.</param>
    /// <param name="fuzzy">The identity pipeline's fuzzy comparison.</param>
    /// <param name="confidenceLine">The identity pipeline's automatic-link score (<c>auto_link_threshold</c>, 0.85 by default).</param>
    public ListEntryMatcher(IReadOnlyList<ListImportCandidate> candidates, IFuzzyMatchingService fuzzy, double confidenceLine)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _fuzzy = fuzzy ?? throw new ArgumentNullException(nameof(fuzzy));
        _confidenceLine = confidenceLine;
        _candidates = candidates.ToList();

        for (var index = 0; index < _candidates.Count; index++)
        {
            var candidate = _candidates[index];
            var normalised = NormalizeTitle(candidate.Title);
            if (normalised.Length > 0)
            {
                Add(_byTitle, normalised, index);
                foreach (var token in Tokens(normalised))
                {
                    Add(_byToken, token, index);
                }
            }

            foreach (var (key, value) in candidate.Ids)
            {
                if (CanonicalId(key, value) is { } id)
                {
                    if (!_byId.TryGetValue(id, out var list))
                    {
                        list = [];
                        _byId[id] = list;
                    }

                    if (!list.Contains(index))
                    {
                        list.Add(index);
                    }
                }
            }
        }
    }

    /// <summary>The title an entry matches, or null when nothing fits well enough or more than one title fits.</summary>
    public ListMatch? Match(ListMatchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        foreach (var key in ListIdentifierKeys.All)
        {
            if (!input.Ids.TryGetValue(key, out var raw) || CanonicalId(key, raw) is not { } id)
            {
                continue;
            }

            if (!_byId.TryGetValue(id, out var hits))
            {
                continue;
            }

            var fitting = hits.Where(index => AllowsMediaType(input, _candidates[index])).ToList();
            if (fitting.Count == 1)
            {
                return new ListMatch(_candidates[fitting[0]].WorkId, key);
            }
        }

        var normalised = NormalizeTitle(input.Title);
        if (normalised.Length == 0)
        {
            return null;
        }

        if (_byTitle.TryGetValue(normalised, out var exact))
        {
            var fitting = exact.Where(index => AllowsMediaType(input, _candidates[index])).ToList();
            if (fitting.Count > 0)
            {
                return PickExact(input, fitting);
            }
        }

        return MatchClose(input, normalised);
    }

    // ── Exact title ──────────────────────────────────────────────────────────────────────────────────────────────────

    private ListMatch? PickExact(ListMatchInput input, List<int> fitting)
    {
        IEnumerable<int> pool = fitting;
        if (input.Year is { } year)
        {
            var sameYear = fitting.Where(index => _candidates[index].Year == year).ToList();
            var nearYear = fitting.Where(index => _candidates[index].Year is { } other && Math.Abs(other - year) == 1).ToList();
            var unknownYear = fitting.Where(index => _candidates[index].Year is null).ToList();

            // The year must agree: an exact year first, then a year off by one (release years differ by region), then a title
            // whose year Library does not know. A title whose year is known and different is a different title.
            pool = sameYear.Count > 0 ? sameYear : nearYear.Count > 0 ? nearYear : unknownYear;
        }

        var left = pool.ToList();
        if (left.Count == 1)
        {
            return new ListMatch(_candidates[left[0]].WorkId, "title");
        }

        if (left.Count > 1 && input.Creator is { Length: > 0 } creator)
        {
            var byCreator = left
                .Where(index => _candidates[index].Creator is { } other && _fuzzy.ComputeTokenSetRatio(creator, other) >= _confidenceLine)
                .ToList();
            if (byCreator.Count == 1)
            {
                return new ListMatch(_candidates[byCreator[0]].WorkId, "title");
            }
        }

        // Two titles with the same name and nothing to tell them apart: left for the person to pick.
        return null;
    }

    // ── Close title ──────────────────────────────────────────────────────────────────────────────────────────────────

    private ListMatch? MatchClose(ListMatchInput input, string normalised)
    {
        var tokens = Tokens(normalised).ToList();
        if (tokens.Count == 0)
        {
            return null;
        }

        var shared = new Dictionary<int, int>();
        foreach (var token in tokens)
        {
            if (!_byToken.TryGetValue(token, out var postings) || postings.Count > CommonTokenPostings)
            {
                continue;
            }

            foreach (var index in postings)
            {
                shared[index] = shared.GetValueOrDefault(index) + 1;
            }
        }

        var local = new LocalMetadata(input.Title, input.Creator, input.Year?.ToString(CultureInfo.InvariantCulture), null);
        var scored = new List<(int Index, double Composite, double Title)>();
        foreach (var (index, _) in shared.OrderByDescending(pair => pair.Value).Take(MaximumFuzzyCandidates))
        {
            var candidate = _candidates[index];
            if (!AllowsMediaType(input, candidate))
            {
                continue;
            }

            var result = _fuzzy.ScoreCandidate(
                local,
                new CandidateMetadata(candidate.Title, candidate.Creator, candidate.Year?.ToString(CultureInfo.InvariantCulture), null));
            scored.Add((index, result.CompositeScore, result.TitleScore));
        }

        var ranked = scored.OrderByDescending(item => item.Composite).ToList();
        if (ranked.Count == 0)
        {
            return null;
        }

        var best = ranked[0];
        if (best.Composite < _confidenceLine || best.Title < CloseTitleScore)
        {
            return null;
        }

        if (ranked.Count > 1 && best.Composite - ranked[1].Composite < RunnerUpMargin)
        {
            return null;
        }

        return new ListMatch(_candidates[best.Index].WorkId, "title");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool AllowsMediaType(ListMatchInput input, ListImportCandidate candidate) =>
        input.MediaTypes.Count == 0
        || input.MediaTypes.Any(type => string.Equals(type, candidate.MediaType, StringComparison.OrdinalIgnoreCase));

    private static void Add(Dictionary<string, List<int>> map, string key, int index)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(index);
    }

    /// <summary>
    /// A title reduced for comparison: lower case, no accents, "&amp;" as "and", punctuation as spaces, a leading
    /// "the", "a" or "an" dropped.
    /// </summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var decomposed = title.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (c == '&')
            {
                builder.Append(" and ");
            }
            else if (c is '\'' or '’' or '`')
            {
                // "Ocean's" and "Oceans" are the same title.
            }
            else if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(' ');
            }
        }

        var words = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words[0] is "the" or "a" or "an")
        {
            words = words[1..];
        }

        return string.Join(' ', words);
    }

    private static IEnumerable<string> Tokens(string normalisedTitle) =>
        normalisedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length >= 3 || token.All(char.IsDigit));

    /// <summary>The comparable form of an identifier, or null when it is not usable. ISBNs are compared as ISBN-13.</summary>
    internal static (string Key, string Value)? CanonicalId(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (key is "isbn" or "isbn_13" or "isbn_10")
        {
            var isbn = ToIsbn13(trimmed);
            return isbn is null ? null : (ListIdentifierKeys.Isbn, isbn);
        }

        if (key.StartsWith("musicbrainz", StringComparison.Ordinal))
        {
            return (ListIdentifierKeys.MusicBrainz, trimmed.ToLowerInvariant());
        }

        if (key.StartsWith("comic_vine", StringComparison.Ordinal))
        {
            return (ListIdentifierKeys.ComicVine, trimmed.ToLowerInvariant());
        }

        return key is ListIdentifierKeys.Imdb or ListIdentifierKeys.Tmdb or ListIdentifierKeys.Tvdb or ListIdentifierKeys.Asin
            ? (key, trimmed.ToLowerInvariant())
            : null;
    }

    private static string? ToIsbn13(string text)
    {
        var digits = text.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (digits.Length == 13 && digits.All(char.IsAsciiDigit))
        {
            return digits;
        }

        if (digits.Length == 10 && digits[..9].All(char.IsAsciiDigit) && (char.IsAsciiDigit(digits[9]) || digits[9] == 'X'))
        {
            var body = "978" + digits[..9];
            var sum = 0;
            for (var i = 0; i < 12; i++)
            {
                sum += (body[i] - '0') * (i % 2 == 0 ? 1 : 3);
            }

            return body + ((10 - (sum % 10)) % 10).ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }
}
