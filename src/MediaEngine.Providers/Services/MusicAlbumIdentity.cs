using System.Text.RegularExpressions;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Compares album names for track-list identity. Retail catalogues commonly append
/// mastering and mix labels that do not change the underlying sequence, while
/// deluxe/expanded/collector editions usually add tracks and must remain distinct.
/// </summary>
internal static partial class MusicAlbumIdentity
{
    private static readonly string[] TrackSetQualifiers =
    [
        "anniversary",
        "bonus",
        "collector",
        "deluxe",
        "expanded",
        "extended",
        "super deluxe",
    ];

    public static bool IsSameTrackList(string? requestedAlbum, string? candidateAlbum)
    {
        if (string.IsNullOrWhiteSpace(requestedAlbum) || string.IsNullOrWhiteSpace(candidateAlbum))
        {
            return false;
        }

        var requested = Normalize(requestedAlbum);
        var candidate = Normalize(candidateAlbum);
        if (string.IsNullOrWhiteSpace(requested.BaseName)
            || string.IsNullOrWhiteSpace(candidate.BaseName))
        {
            return false;
        }

        if (!string.Equals(
                requested.TrackSetQualifier,
                candidate.TrackSetQualifier,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(requested.BaseName, candidate.BaseName, StringComparison.OrdinalIgnoreCase)
            || RetailTextSimilarity.ComputeWordOverlap(requested.BaseName, candidate.BaseName) >= 0.92)
        {
            return true;
        }

        var requestedSuffix = Normalize(AlbumNameAfterIdentityPrefix(requestedAlbum)).BaseName;
        var candidateSuffix = Normalize(AlbumNameAfterIdentityPrefix(candidateAlbum)).BaseName;
        return string.Equals(requestedSuffix, candidateSuffix, StringComparison.OrdinalIgnoreCase)
            || RetailTextSimilarity.ComputeWordOverlap(requestedSuffix, candidateSuffix) >= 0.92;
    }

    public static double ComputeBaseNameOverlap(string? requestedAlbum, string? candidateAlbum)
    {
        if (string.IsNullOrWhiteSpace(requestedAlbum) || string.IsNullOrWhiteSpace(candidateAlbum))
        {
            return 0;
        }

        var requested = Normalize(requestedAlbum);
        var candidate = Normalize(candidateAlbum);
        if (string.IsNullOrWhiteSpace(requested.BaseName)
            || string.IsNullOrWhiteSpace(candidate.BaseName))
        {
            return 0;
        }

        if (!string.Equals(
                requested.TrackSetQualifier,
                candidate.TrackSetQualifier,
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return RetailTextSimilarity.ComputeWordOverlap(requested.BaseName, candidate.BaseName);
    }

    // ── Edition labels ────────────────────────────────────────────────────────
    //
    // IsSameTrackList treats deluxe/collector/expanded editions as different albums because
    // retail catalogues give them their own track lists. MusicBrainz is different: it keeps
    // those labels in a release's disambiguation, not its title, so a file tagged
    // "Jagged Little Pill (Collector's Edition)" legitimately sits on the release titled
    // "Jagged Little Pill". The helpers below compare albums by base name, ignoring edition labels.

    private static readonly HashSet<string> EditionLabelWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "deluxe", "collector", "collectors", "expanded", "extended", "remaster", "remastered",
        "remastering", "anniversary", "special", "limited", "bonus", "track", "tracks", "explicit",
        "clean", "edition", "version", "reissue", "super", "digital", "standard", "international",
        "mono", "stereo",
    };

    private static readonly HashSet<string> EditionFillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "the", "of", "with", "plus", "a",
    };

    /// <summary>Words that carry no identity on their own when matching a label to a disambiguation.</summary>
    private static readonly HashSet<string> EditionGenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "edition", "version", "track", "tracks", "digital", "standard", "international",
    };

    /// <summary>
    /// True when the two albums have the same base name once edition labels (deluxe, collector's
    /// edition, expanded, remastered, anniversary, special edition, bonus track version, explicit,
    /// clean, "edition"/"version" parentheticals) are removed. Totally different names stay different.
    /// </summary>
    public static bool IsSameBaseAlbum(string? requestedAlbum, string? candidateAlbum)
    {
        if (string.IsNullOrWhiteSpace(requestedAlbum) || string.IsNullOrWhiteSpace(candidateAlbum))
        {
            return false;
        }

        var requested = SplitEditionLabels(requestedAlbum).BaseName;
        var candidate = SplitEditionLabels(candidateAlbum).BaseName;
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return string.Equals(requested, candidate, StringComparison.OrdinalIgnoreCase)
            || RetailTextSimilarity.ComputeWordOverlap(requested, candidate) >= 0.92;
    }

    /// <summary>
    /// The normalised edition label removed from an album name ("collectors edition" for
    /// "Jagged Little Pill (Collector's Edition)"), or empty when the album carries none.
    /// </summary>
    public static string EditionLabel(string? album)
        => string.IsNullOrWhiteSpace(album) ? string.Empty : SplitEditionLabels(album).Label;

    /// <summary>
    /// True when every identifying word of <paramref name="label"/> appears in
    /// <paramref name="other"/> (another edition label, or a release disambiguation such as
    /// "collector's edition"). A label with no identifying word never matches.
    /// </summary>
    public static bool LabelIsCoveredBy(string? label, string? other)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(other))
        {
            return false;
        }

        var wanted = ComparableWords(label)
            .Where(word => !EditionGenericWords.Contains(word) && !EditionFillerWords.Contains(word))
            .ToList();
        if (wanted.Count == 0)
        {
            return false;
        }

        var available = ComparableWords(other).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wanted.All(available.Contains);
    }

    private static IEnumerable<string> ComparableWords(string text)
        => RetailTextSimilarity
            .NormalizeComparableText(text.Replace("'", string.Empty).Replace("’", string.Empty))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static (string BaseName, string Label) SplitEditionLabels(string album)
    {
        var labels = new List<string>();
        var text = album.Replace('’', '\'');

        // "(Collector's Edition)", "[Deluxe]"
        text = BracketedGroupRegex().Replace(text, match =>
        {
            if (!IsEditionLabelPhrase(match.Groups[1].Value))
            {
                return match.Value;
            }

            labels.Add(match.Groups[1].Value);
            return " ";
        });

        // "Album - Deluxe Edition", "Album: Remastered"
        var trailing = TrailingSeparatedRegex().Match(text);
        if (trailing.Success && IsEditionLabelPhrase(trailing.Groups[1].Value)
            && trailing.Index > 0)
        {
            labels.Add(trailing.Groups[1].Value);
            text = text[..trailing.Index];
        }

        // "Album Deluxe Edition", "Album Remastered"
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var take = Math.Min(5, words.Length - 1); take >= 1; take--)
        {
            var suffix = string.Join(' ', words[^take..]);
            var last = words[^1];
            var endsInEditionNoun = last.Equals("edition", StringComparison.OrdinalIgnoreCase)
                || last.Equals("version", StringComparison.OrdinalIgnoreCase);
            var singleLabel = take == 1
                && (last.Equals("deluxe", StringComparison.OrdinalIgnoreCase)
                    || last.Equals("remastered", StringComparison.OrdinalIgnoreCase)
                    || last.Equals("expanded", StringComparison.OrdinalIgnoreCase));
            if ((endsInEditionNoun || singleLabel) && IsEditionLabelPhrase(suffix))
            {
                labels.Add(suffix);
                text = string.Join(' ', words[..^take]);
                break;
            }
        }

        var baseName = string.Join(' ', ComparableWords(text));
        var label = string.Join(' ', labels.SelectMany(ComparableWords));
        return (baseName, label);
    }

    /// <summary>
    /// A phrase made only of edition-label words, fillers, ordinals ("20th") and years, with at
    /// least one real label word, e.g. "Collector's Edition" or "Remastered 2015".
    /// </summary>
    private static bool IsEditionLabelPhrase(string phrase)
    {
        var words = ComparableWords(phrase).ToList();
        if (words.Count == 0)
        {
            return false;
        }

        var hasLabelWord = false;
        foreach (var word in words)
        {
            if (EditionLabelWords.Contains(word))
            {
                hasLabelWord = true;
            }
            else if (!EditionFillerWords.Contains(word) && !OrdinalOrYearRegex().IsMatch(word))
            {
                return false;
            }
        }

        return hasLabelWord;
    }

    [GeneratedRegex(@"[\(\[]([^\)\]]*)[\)\]]")]
    private static partial Regex BracketedGroupRegex();

    [GeneratedRegex(@"\s+[-–—:]\s+([^-–—:]+)$")]
    private static partial Regex TrailingSeparatedRegex();

    [GeneratedRegex(@"^(\d+(st|nd|rd|th)|(19|20)\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalOrYearRegex();

    private static NormalizedAlbum Normalize(string value)
    {
        var comparable = RetailTextSimilarity.NormalizeComparableText(value);
        var trackSetQualifier = string.Join(
            '|',
            TrackSetQualifiers.Where(qualifier =>
                comparable.Contains(qualifier, StringComparison.OrdinalIgnoreCase)));
        var hasHarmlessEditionQualifier = HarmlessEditionQualifierRegex().IsMatch(comparable);

        // These labels describe mastering, mix, or media presentation. They are
        // intentionally removable because the song sequence remains the album.
        comparable = HarmlessEditionQualifierRegex().Replace(comparable, " ");
        comparable = SoundtrackDescriptorRegex().Replace(comparable, " ");
        comparable = TrackSetQualifierRegex().Replace(comparable, " ");
        if (hasHarmlessEditionQualifier)
        {
            comparable = YearRegex().Replace(comparable, " ");
        }

        comparable = string.Join(' ', comparable.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return new NormalizedAlbum(comparable, trackSetQualifier);
    }

    private static string AlbumNameAfterIdentityPrefix(string value)
    {
        var separator = value.LastIndexOf(':');
        return separator >= 0 && separator < value.Length - 1
            ? value[(separator + 1)..].Trim()
            : value;
    }

    [GeneratedRegex(@"\b(remaster(ed)?|remix(ed)?|mix|mono|stereo|digital master)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HarmlessEditionQualifierRegex();

    [GeneratedRegex(@"\b(original motion picture soundtrack|music from the original motion picture|original soundtrack)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SoundtrackDescriptorRegex();

    [GeneratedRegex(@"\b(anniversary|bonus|collector'?s?|deluxe|expanded|extended|super deluxe)( edition)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TrackSetQualifierRegex();

    [GeneratedRegex(@"\b(19|20)\d{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex YearRegex();

    private sealed record NormalizedAlbum(string BaseName, string TrackSetQualifier);
}
