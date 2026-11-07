using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The header's background (universal editor v5 D9): a gradient made from the cover's two main colours, mixed into the window surface so the
/// title stays readable. How much of each colour is kept comes from <c>config/ui/palette.json</c> (D34); a cover with no stored colours
/// gets no gradient at all, so the header is the plain window surface.
/// </summary>
public static partial class EditorHeaderGradient
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColour();

    /// <summary>The header's <c>style</c> value, or null when the cover has no usable colour (the plain surface).</summary>
    public static string? Style(EditorCoverColorsDto? colors, EditorHeaderPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var primary = Valid(colors?.PrimaryHex) ?? Valid(colors?.SecondaryHex) ?? Valid(colors?.AccentHex);
        if (primary is null)
        {
            return null;
        }

        var secondary = Valid(colors?.SecondaryHex) ?? primary;
        var first = Math.Clamp(palette.PrimaryPercent, 0, 100).ToString(CultureInfo.InvariantCulture);
        var second = Math.Clamp(palette.SecondaryPercent, 0, 100).ToString(CultureInfo.InvariantCulture);
        return $"background:linear-gradient(100deg,var(--tl-bg-surface) 0%,var(--tl-bg-surface) 30%,"
            + $"color-mix(in srgb,{primary} {first}%,var(--tl-bg-surface)) 68%,color-mix(in srgb,{secondary} {second}%,var(--tl-bg-surface)) 100%)";
    }

    // Only a plain #rrggbb value reaches a style attribute: the Engine's colours are data, not markup.
    private static string? Valid(string? value) => value is not null && HexColour().IsMatch(value) ? value : null;
}

/// <summary>One tab of the editor window: the tool it opens and its words.</summary>
public sealed record EditorTab(EditTool Tool, string Label);

/// <summary>
/// Which tabs the editor offers (universal editor v5, 3a): the item's kind says which, in the fixed order Details, Artwork, Match, History,
/// and the item's own abilities decide whether a listed tab can really open (no Artwork where there is no picture to choose, no Match where
/// nothing can be matched). The Contents and Files tabs are gone: their jobs moved to the header.
/// </summary>
public static class EditorTabRules
{
    /// <summary>The standard four, used while the Engine has not said which tabs a kind has.</summary>
    public static readonly IReadOnlyList<string> Standard = ["details", "artwork", "match", "history"];

    public static IReadOnlyList<EditorTab> For(EditorHeaderConfigDto? config, ItemFieldEditSession fields, bool showHistory)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var wanted = config is { Tabs.Count: > 0 } ? config.Tabs : Standard;
        var tabs = new List<EditorTab>(wanted.Count);
        foreach (var id in wanted)
        {
            switch (id.ToLowerInvariant())
            {
                case "details":
                    tabs.Add(new EditorTab(EditTool.Details, "Details"));
                    break;
                case "artwork" or "pictures" when fields.CanEditArtwork:
                    tabs.Add(new EditorTab(EditTool.Artwork, id.Equals("pictures", StringComparison.OrdinalIgnoreCase) ? "Pictures" : EditorUniverseRules.ArtworkTabLabel(fields.SubjectKind)));
                    break;
                case "match" when fields.CanMatch || EditorUniverseRules.IsUniverseSubject(fields.SubjectKind):
                    tabs.Add(new EditorTab(EditTool.Match, EditorUniverseRules.MatchTabLabel(fields.SubjectKind)));
                    break;
                case "history" when showHistory:
                    tabs.Add(new EditorTab(EditTool.History, "History"));
                    break;
            }
        }

        return tabs;
    }
}

/// <summary>
/// The one line of facts under the header's title (universal editor v5, 3a): the pieces <c>config/editor.json</c> lists for the item's kind,
/// in order, as far as the item's fields and totals know them. A piece nothing knows is left out, never guessed. The match state is not text:
/// it is the chip beside the line, so it is reported on its own.
/// </summary>
public static class EditorHeaderFacts
{
    public const string MatchState = "match_state";

    public static IReadOnlyList<string> Parts(
        EditorHeaderConfigDto? config, Func<string[], string?> field, ItemQuickFactsDto? totals = null, EditorHeaderDto? header = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        var parts = new List<string>();
        foreach (var key in config?.FactsLine ?? [])
        {
            var text = Piece(key, field, totals, header);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text.Trim());
            }
        }

        return parts;
    }

    /// <summary>True when the kind's facts line carries the match state (the chip).</summary>
    public static bool ShowsMatchState(EditorHeaderConfigDto? config) =>
        config?.FactsLine.Contains(MatchState, StringComparer.Ordinal) == true;

    private static string? Piece(string key, Func<string[], string?> field, ItemQuickFactsDto? totals, EditorHeaderDto? header)
    {
        string? Field(params string[] keys) => Clean(field(keys));
        string? Plural(int count, string one, string many) => count > 0 ? $"{DisplayFormat.FormatCount(count)} {(count == 1 ? one : many)}" : null;
        var current = header?.Level.Items.FirstOrDefault(item => item.IsCurrent);
        return key switch
        {
            "year" => Field("year") ?? YearOf(Field("release_date", "air_date", "premiere_date")),
            "release_date" => Field("release_date"),
            "air_date" => Field("air_date", "premiere_date"),
            "runtime" => Field("runtime", "duration"),
            "rating" => Field("rating", "content_rating", "certification"),
            "season_number" => Field("season_number") is { } season ? $"Season {season}" : null,
            "episode_number" => Field("episode_number") is { } episode ? $"Episode {episode}" : null,
            "track_number" => Field("track_number") is { } track ? $"Track {track}" : null,
            "issue_number" => Field("issue_number") is { } issue ? $"Issue {issue}" : null,
            "number" => Field("number") ?? (current?.Number is { } number ? number.ToString(CultureInfo.CurrentCulture) : null),
            "artist" => Field("artist", "album_artist"),
            "author" => Field("author"),
            "narrator" => Field("narrator"),
            "series" => Field("series", "series_name"),
            "page_count" => Field("page_count") is { } pages ? $"{pages} pages" : null,
            "season_count" => totals is { DirectTotal: > 0 } ? Plural(totals.DirectTotal, "season", "seasons") : null,
            "episode_count" => totals is { LeafTotal: > 0 } ? Plural(totals.LeafTotal, "episode", "episodes") : null,
            "track_count" => totals is { LeafTotal: > 0 } ? Plural(totals.LeafTotal, "track", "tracks") : totals is { DirectTotal: > 0 } ? Plural(totals.DirectTotal, "track", "tracks") : null,
            "section_count" => totals is { DirectTotal: > 0 } ? Plural(totals.DirectTotal, "section", "sections") : null,
            "video_count" => totals is { LeafTotal: > 0 } ? Plural(totals.LeafTotal, "video", "videos") : totals is { DirectTotal: > 0 } ? Plural(totals.DirectTotal, "video", "videos") : null,
            "member_count" => totals?.ItemCount is > 0 ? $"{DisplayFormat.FormatCount(totals.ItemCount.Value)} in your library" : null,
            "item_count" => totals?.ItemCount is > 0 ? Plural(totals.ItemCount.Value, "item", "items") : null,
            "total_runtime" => totals is { RuntimeSeconds: > 0 } ? DisplayFormat.FormatDurationHoursMinutesRounded(totals.RuntimeSeconds.Value) : null,
            "total_size" or "size" => FileSize(header, totals),
            "watched" => current is { State: EditorNavItemStates.Watched } ? "Watched" : null,
            "roles" => totals is { Roles.Count: > 0 } ? string.Join(", ", totals.Roles) : null,
            "counts_by_kind" => totals is { CountsByKind.Count: > 0 }
                ? string.Join(" · ", totals.CountsByKind.Take(3).Select(count => $"{DisplayFormat.FormatCount(count.Count)} {count.Label.ToLowerInvariant()}"))
                : null,
            _ => null,
        };
    }

    private static string? FileSize(EditorHeaderDto? header, ItemQuickFactsDto? totals)
    {
        var bytes = header?.FileFacts?.SizeBytes ?? totals?.SizeBytes;
        return bytes is > 0 ? DisplayFormat.FormatFileSize(bytes.Value) : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // The year inside a date however it is written ("2021-10-22", "22 Oct 2021").
    private static string? YearOf(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return null;
        }

        var match = Regex.Match(date, @"(?<!\d)(1[89]\d{2}|20\d{2})(?!\d)");
        return match.Success ? match.Value : null;
    }
}

/// <summary>The words of the header's item dropdown (universal editor v5, screens A, B, D): a row's code, its lines and the "All supporting files" count.</summary>
public static class EditorNavText
{
    /// <summary>
    /// The small line over an item's title: <c>S02E03</c> for an episode, a lesson's number and length for a course ("01 · 12 min", never an
    /// S/E code), a track's number. An item with no code says what it is ("This movie").
    /// </summary>
    public static string Code(EditorNavItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var length = Length(item.DurationSeconds);
        var code = item.Code ?? (item.Number is { } number ? number.ToString("00", CultureInfo.InvariantCulture) : null);
        if (item.Kind == "video")
        {
            return string.Join(" · ", new[] { code, length }.Where(piece => !string.IsNullOrWhiteSpace(piece)));
        }

        return code ?? ThisItem(item.Kind);
    }

    /// <summary>"This movie", "This book": what the dropdown says over the item itself when it has no code.</summary>
    public static string ThisItem(string kind) => kind switch
    {
        "movie" => "This movie",
        "book" => "This book",
        "audiobook" => "This audiobook",
        "comic" => "This comic",
        "track" => "This song",
        "video" => "This video",
        "episode" => "This episode",
        _ => "This item",
    };

    /// <summary>The word over the item dropdown's choice when nothing is chosen ("Episode", "Track", "Video").</summary>
    public static string ChooseWord(EditorHeaderDto header) => header.Kind switch
    {
        EditorKinds.TvShow or EditorKinds.TvSeason or EditorKinds.TvEpisode => "episode",
        EditorKinds.Album or EditorKinds.Song => "song",
        EditorKinds.Course or EditorKinds.CourseSection or EditorKinds.Video => "video",
        EditorKinds.Series => "book",
        _ => "item",
    };

    /// <summary>A length in the dropdown's plain words: "12 min", "1 h 5 min". Null when the length is unknown.</summary>
    public static string? Length(double? seconds)
    {
        if (seconds is not > 0)
        {
            return null;
        }

        var minutes = Math.Max(1, (int)Math.Round(seconds.Value / 60d, MidpointRounding.AwayFromZero));
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }

    /// <summary>The second line of a supporting file's row: its group (else its role), then where it is shown or what size it is: "Homework · with lesson 02", "Character guides · 18 pages".</summary>
    public static string FileLine(EditorHeaderSupportingFileDto file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var lead = !string.IsNullOrWhiteSpace(file.Group) ? file.Group : file.RoleLabel;
        var tail = !string.IsNullOrWhiteSpace(file.ShownAlongside?.Label)
            ? file.ShownAlongside!.Label
            : file.PageCount is > 0 ? $"{file.PageCount} {(file.PageCount == 1 ? "page" : "pages")}" : Length(file.DurationSeconds);
        return string.Join(" · ", new[] { lead, tail }.Where(piece => !string.IsNullOrWhiteSpace(piece)));
    }

    /// <summary>
    /// The "All supporting files" row's second line: how many are at this level and in the whole title ("12 in this season · 238 in the show").
    /// Without a second level, or when the level holds them all, only the total is said.
    /// </summary>
    public static string AllFilesLine(EditorHeaderDto header, EditorHeaderSupportingFilesDto files)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(files);
        if (header.SecondLevel is not { } second || files.Count == files.TotalInRoot)
        {
            return $"{DisplayFormat.FormatCount(files.TotalInRoot)} in all";
        }

        var title = header.Kind is EditorKinds.Course or EditorKinds.CourseSection or EditorKinds.Video ? "course" : "show";
        return $"{DisplayFormat.FormatCount(files.Count)} in this {second.Label.ToLowerInvariant()} · {DisplayFormat.FormatCount(files.TotalInRoot)} in the {title}";
    }

    /// <summary>The second dropdown's row detail: "10 episodes · 12 supporting files".</summary>
    public static string EntryLine(EditorHeaderLevelEntryDto entry, string itemWord)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var pieces = new List<string>();
        if (entry.ItemCount > 0)
        {
            pieces.Add($"{DisplayFormat.FormatCount(entry.ItemCount)} {itemWord}{(entry.ItemCount == 1 ? string.Empty : "s")}");
        }

        if (entry.SupportingFileCount > 0)
        {
            pieces.Add($"{DisplayFormat.FormatCount(entry.SupportingFileCount)} supporting {(entry.SupportingFileCount == 1 ? "file" : "files")}");
        }

        return string.Join(" · ", pieces);
    }
}
