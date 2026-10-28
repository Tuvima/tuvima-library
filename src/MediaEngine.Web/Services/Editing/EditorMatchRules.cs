using System.Globalization;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;

namespace MediaEngine.Web.Services.Editing;

/// <summary>One tab above the Match results: what kind of record is being searched for.</summary>
public sealed record EditorMatchTab(string Group, string Label);

/// <summary>What a result says about itself, in the words a result row and the review use.</summary>
public sealed record EditorMatchCandidateFacts(
    string Title,
    string? Creator,
    string? Parent,
    string? Year,
    string? Edition,
    string Provider);

/// <summary>The place a result names for the item, as the library labels places ("OK Computer", "Season 2").</summary>
public sealed record ProposedParent(string Label, string? ParentLabel);

/// <summary>
/// The Match tool's rules that need no screen (universal editor spec D5, D6, D11): which tabs a kind of item gets, what a result says
/// about itself, where a result would place the item, and the request staged for the Engine. Kept apart so they can be tested directly.
/// </summary>
public static class EditorMatchRules
{
    private static readonly string[] EditionKeys = ["edition", "version", "format", "release_type"];

    /// <summary>
    /// The tabs for an item. A song can be matched to a track or to the album it is on; the Engine has no artist search for music yet, so
    /// there is no Artists tab. Every other kind of item is matched to its own kind of record, so it needs no tabs.
    /// </summary>
    public static IReadOnlyList<EditorMatchTab> TabsFor(string mediaType, string targetGroup) =>
        string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase)
        && string.Equals(targetGroup, "track", StringComparison.OrdinalIgnoreCase)
            ? [new EditorMatchTab("track", "Tracks"), new EditorMatchTab("album", "Albums")]
            : [new EditorMatchTab(targetGroup, LabelFor(targetGroup))];

    public static string LabelFor(string group) => group.ToLowerInvariant() switch
    {
        "track" => "Tracks",
        "album" => "Albums",
        "artist" => "Artists",
        "episode" => "Episodes",
        "season" => "Seasons",
        "series" or "show" => "Series",
        "movie" => "Movies",
        "issue" => "Issues",
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(group.Replace('_', ' ')),
    };

    /// <summary>The kind of record a group is, as the search asks for it.</summary>
    public static string TargetKindOf(string group) => group.ToLowerInvariant() switch
    {
        "artist" or "narrator" => "person",
        "album" or "series" or "show" => "container",
        _ => "item",
    };

    /// <summary>The words a result row shows.</summary>
    public static EditorMatchCandidateFacts FactsOf(ItemCanonicalRetailCandidateDto candidate)
    {
        var creator = FirstNonEmpty(candidate.Author, candidate.Director, Field(candidate, MetadataFieldConstants.Artist));
        return new EditorMatchCandidateFacts(
            candidate.Title,
            creator,
            ParentOf(candidate),
            FirstNonEmpty(candidate.Year, Field(candidate, MetadataFieldConstants.Year)),
            EditionOf(candidate),
            candidate.ProviderName);
    }

    /// <summary>The album, show or series a result sits in.</summary>
    public static string? ParentOf(ItemCanonicalRetailCandidateDto candidate) => FirstNonEmpty(
        Field(candidate, MetadataFieldConstants.Album),
        Field(candidate, MetadataFieldConstants.ShowName),
        Field(candidate, MetadataFieldConstants.Series));

    private static string? EditionOf(ItemCanonicalRetailCandidateDto candidate)
    {
        foreach (var key in EditionKeys)
        {
            if (candidate.ExtraFields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// The place a result would put the item, in the words the library's own places use: the album a song is on, "Season 2" of the show
    /// an episode is in, the series a title is in. Null when the result names none.
    /// </summary>
    public static ProposedParent? ProposedParentOf(ItemCanonicalRetailCandidateDto candidate, string mediaType)
    {
        if (string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase))
        {
            var album = Field(candidate, MetadataFieldConstants.Album);
            return album is null ? null : new ProposedParent(album, Field(candidate, MetadataFieldConstants.Artist));
        }

        if (string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase))
        {
            var show = Field(candidate, MetadataFieldConstants.ShowName);
            if (show is null)
            {
                return null;
            }

            var season = Field(candidate, MetadataFieldConstants.SeasonNumber);
            return season is not null && int.TryParse(season, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? new ProposedParent(number == 0 ? "Specials" : $"Season {number.ToString(CultureInfo.InvariantCulture)}", show)
                : new ProposedParent(show, null);
        }

        var series = Field(candidate, MetadataFieldConstants.Series);
        return series is null ? null : new ProposedParent(series, null);
    }

    /// <summary>Whether a place the library offers is the one a result names.</summary>
    public static bool IsSamePlace(ProposedParent proposed, string label, string? parentLabel)
    {
        var sameLabel = string.Equals(proposed.Label.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(proposed.Label, "Specials", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(label.Trim(), "Season 0", StringComparison.OrdinalIgnoreCase));
        if (!sameLabel)
        {
            return false;
        }

        return proposed.ParentLabel is null
               || parentLabel is null
               || string.Equals(proposed.ParentLabel.Trim(), parentLabel.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the Engine is asked to save for a chosen result (never moves anything, spec D6).</summary>
    public static ReplaceRetailMatchRequestDto RequestFor(ItemCanonicalRetailCandidateDto candidate, EditorMatchSubject subject, string group) => new()
    {
        TargetKind = TargetKindOf(group),
        TargetFieldGroup = group,
        TargetScopeId = subject.ScopeId,
        ProviderId = candidate.ProviderId,
        ProviderName = candidate.ProviderName,
        ProviderItemId = candidate.ProviderItemId ?? string.Empty,
        CoverUrl = candidate.CoverUrl,
        RequiredFields = new Dictionary<string, string>(candidate.RequiredFields, StringComparer.OrdinalIgnoreCase),
        SuggestedFields = new Dictionary<string, string>(candidate.SuggestedFields, StringComparer.OrdinalIgnoreCase),
        BridgeIds = new Dictionary<string, string>(candidate.BridgeIds, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>
    /// Whether a move preview says the files cannot move (an As-is, protected or read-only folder, a taken name), as opposed to a line
    /// that only says what will happen to them: a row with a reason it is blocked, or with a note and no file change.
    /// </summary>
    public static bool FilesCannotMove(LibraryMovePreviewRow? row) =>
        row is not null
        && (!string.IsNullOrWhiteSpace(row.BlockedReason)
            || (row.FileChange is null && !string.IsNullOrWhiteSpace(row.FileNote)));

    private static string? Field(ItemCanonicalRetailCandidateDto candidate, string key)
    {
        if (candidate.RequiredFields.TryGetValue(key, out var required) && !string.IsNullOrWhiteSpace(required))
        {
            return required.Trim();
        }

        return candidate.SuggestedFields.TryGetValue(key, out var suggested) && !string.IsNullOrWhiteSpace(suggested)
            ? suggested.Trim()
            : null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
