using System.Globalization;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Services.Editing;

/// <summary>The kinds of item the Quick Facts panel has its own facts for (universal editor v4, section 3a).</summary>
public enum QuickFactsKind
{
    /// <summary>Nothing to show: the item has no Quick Facts (for example a character or place of a Universe).</summary>
    None,
    Film,
    FilmSeries,
    Show,
    Season,
    Episode,
    Book,
    Audiobook,
    ComicIssue,
    Series,
    Album,
    Song,
    PersonalVideo,
    VideoSeries,
    Person,
    CuratedCollection,
    Universe,
}

/// <summary>One read-only fact: a short value with the label that says what it is.</summary>
public sealed record QuickFact(string Key, string Icon, string Value, string Label);

/// <summary>The facts the panel shows, and the plain notes under them ("Read-only library", "File write-back off").</summary>
public sealed record QuickFactsModel(QuickFactsKind Kind, IReadOnlyList<QuickFact> Facts, IReadOnlyList<string> Notes)
{
    public static QuickFactsModel Empty { get; } = new(QuickFactsKind.None, [], []);

    public bool IsEmpty => Facts.Count == 0 && Notes.Count == 0;
}

/// <summary>
/// Works out the Quick Facts of an item (universal editor v4, D4 and the table in section 3a): three to five read-only facts that suit its
/// kind, plus "Read-only library" or "File write-back off" when true. Pure: the facts a single item already has come from the editor's
/// field list, the totals of a container come from the Engine's Quick Facts read, and a fact neither knows is left out rather than guessed.
/// </summary>
public static class EditorQuickFactsBuilder
{
    public const string ReadOnlyLibraryNote = "Read-only library";
    public const string WriteBackOffNote = "File write-back off";

    // Icon names are the app's own Material icon keys (see EditorQuickFacts.razor).
    private const string Clock = "Schedule";
    private const string Calendar = "CalendarToday";
    private const string Quality = "HighQuality";
    private const string Size = "Storage";
    private const string Pages = "MenuBook";
    private const string Number = "Tag";
    private const string Folder = "FolderOpen";
    private const string Person = "Person";
    private const string People = "People";
    private const string Seasons = "ViewAgenda";
    private const string Episodes = "LiveTv";
    private const string Tracks = "QueueMusic";
    private const string Library = "LibraryBooks";
    private const string Hub = "Hub";
    private const string Narrator = "RecordVoiceOver";

    /// <summary>Which kind of item the editor is on, from the Engine's media type and scope (or the subject kind).</summary>
    public static QuickFactsKind KindOf(string? mediaType, string? scopeId, string? subjectKind, ItemQuickFactsDto? totals = null)
    {
        if (!string.IsNullOrWhiteSpace(subjectKind))
        {
            return subjectKind switch
            {
                EditorSubjectKinds.Person => QuickFactsKind.Person,
                EditorSubjectKinds.Collection => QuickFactsKind.CuratedCollection,
                EditorSubjectKinds.Universe => QuickFactsKind.Universe,
                EditorSubjectKinds.Series => totals is { CountsByKind.Count: > 0 } && totals.CountsByKind.All(count => count.Label == "Films")
                    ? QuickFactsKind.FilmSeries
                    : QuickFactsKind.Series,
                _ => QuickFactsKind.None,
            };
        }

        var scope = (scopeId ?? string.Empty).Trim().ToLowerInvariant();
        var isSeries = scope is "series" or "tv_show" or "show";
        return (mediaType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "tv" or "television" or "show" => scope switch
            {
                "season" => QuickFactsKind.Season,
                "episode" => QuickFactsKind.Episode,
                _ => QuickFactsKind.Show,
            },
            "movie" or "movies" => isSeries ? QuickFactsKind.FilmSeries : QuickFactsKind.Film,
            "music" or "album" => scope is "track" ? QuickFactsKind.Song : QuickFactsKind.Album,
            "book" or "books" => isSeries ? QuickFactsKind.Series : QuickFactsKind.Book,
            "audiobook" or "audiobooks" => isSeries ? QuickFactsKind.Series : QuickFactsKind.Audiobook,
            "comic" or "comics" => isSeries ? QuickFactsKind.Series : QuickFactsKind.ComicIssue,
            "video" or "videos" => isSeries || totals is { DirectTotal: > 0 } ? QuickFactsKind.VideoSeries : QuickFactsKind.PersonalVideo,
            _ => QuickFactsKind.None,
        };
    }

    /// <param name="field">Looks up the first present field among the keys and returns its shown value, or null.</param>
    /// <param name="totals">The Engine's totals, or null when that read failed (the facts then say only what the fields say).</param>
    /// <param name="mediaType">The Engine's media type, which words a Series' "owned" line (books, issues).</param>
    /// <param name="fileWriteOff">True only when the library's own write-back switch is known to be off.</param>
    public static QuickFactsModel Build(
        QuickFactsKind kind, Func<string[], string?> field, ItemQuickFactsDto? totals, bool fileWriteOff, string? mediaType = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (kind == QuickFactsKind.None)
        {
            return QuickFactsModel.Empty;
        }

        var facts = new List<QuickFact>();
        void Add(string key, string icon, string? value, string label)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                facts.Add(new QuickFact(key, icon, value.Trim(), label));
            }
        }

        string? FieldValue(params string[] keys) => Clean(field(keys));
        var runtime = TotalRuntime(totals);
        var size = TotalSize(totals);
        var filesLabel = FilesLabel(totals);

        switch (kind)
        {
            case QuickFactsKind.Film:
                Add("runtime", Clock, FieldValue("runtime", "duration") ?? runtime, "Runtime");
                Add("year", Calendar, FieldValue("year") ?? YearOf(FieldValue("release_date")), "Year");
                Add("size", Size, size, filesLabel);
                Add("quality", Quality, totals?.BestQuality, "Best quality you own");
                break;

            case QuickFactsKind.FilmSeries:
                Add("films", Folder, OwnedCount(totals), "Films owned");
                Add("runtime", Clock, runtime, "Total runtime");
                Add("size", Size, size, filesLabel);
                break;

            case QuickFactsKind.Show:
                if (totals is { DirectTotal: > 0 })
                {
                    Add("seasons", Seasons, OwnedOfTotal(totals.DirectOwned, totals.DirectTotal), "Seasons");
                }

                if (totals is { LeafTotal: > 0 })
                {
                    Add("episodes", Episodes, OwnedOfTotal(totals.LeafOwned, totals.LeafTotal), "Episodes owned");
                }

                Add("size", Size, size, filesLabel);
                break;

            case QuickFactsKind.Season:
                if (totals is { DirectTotal: > 0 })
                {
                    Add("episodes", Episodes, OwnedOfTotal(totals.DirectOwned, totals.DirectTotal), "Episodes owned");
                }

                Add("runtime", Clock, runtime, "Total runtime");
                Add("size", Size, size, filesLabel);
                break;

            case QuickFactsKind.Episode:
                Add("air_date", Calendar, FieldValue("air_date"), "Air date");
                Add("runtime", Clock, FieldValue("runtime", "duration") ?? runtime, "Runtime");
                Add("size", Size, size, filesLabel);
                Add("quality", Quality, totals?.BestQuality, "Quality");
                break;

            case QuickFactsKind.Book:
                Add("pages", Pages, FieldValue("page_count"), "Pages");
                Add("edition", Library, FieldValue("edition_format"), "Edition");
                Add("isbn", Number, FieldValue("isbn"), "ISBN");
                Add("size", Size, size, filesLabel);
                break;

            case QuickFactsKind.Audiobook:
                Add("narrator", Narrator, FieldValue("narrator"), "Narrator");
                Add("length", Clock, FieldValue("duration", "runtime") ?? runtime, "Length");
                Add("files", Folder, totals is { FileCount: > 0 } ? Count(totals.FileCount) : null, totals is { FileCount: 1 } ? "File" : "Files");
                Add("size", Size, size, "Size");
                break;

            case QuickFactsKind.ComicIssue:
                Add("issue", Number, FieldValue("issue_number"), "Issue");
                Add("pages", Pages, FieldValue("page_count"), "Pages");
                Add("size", Size, size, filesLabel);
                break;

            case QuickFactsKind.Series:
                Add("owned", Library, OwnedCount(totals) ?? OwnedOfDirect(totals), OwnedLabel(mediaType, totals));
                Add("size", Size, size, filesLabel);
                break;

            case QuickFactsKind.Album:
                if (totals is { LeafTotal: > 0 } or { DirectTotal: > 0 })
                {
                    var (owned, total) = totals!.LeafTotal > 0 ? (totals.LeafOwned, totals.LeafTotal) : (totals.DirectOwned, totals.DirectTotal);
                    Add("tracks", Tracks, OwnedOfTotal(owned, total), "Tracks owned");
                }

                Add("length", Clock, runtime ?? FieldValue("duration", "runtime"), "Length");
                Add("size", Size, size, filesLabel);
                break;

            case QuickFactsKind.Song:
                Add("length", Clock, FieldValue("duration", "runtime") ?? runtime, "Length");
                Add("track", Number, TrackAndDisc(FieldValue("track_number"), FieldValue("disc_number")), "Track and disc");
                Add("quality", Quality, totals?.BestQuality, "Quality");
                break;

            case QuickFactsKind.PersonalVideo:
                Add("length", Clock, FieldValue("runtime", "duration") ?? runtime, "Length");
                Add("size", Size, size, filesLabel);
                break;

            case QuickFactsKind.VideoSeries:
                Add("videos", Folder, totals is { DirectTotal: > 0 } ? Count(totals.DirectOwned) : null, "Videos");
                Add("length", Clock, runtime, "Total length");
                Add("size", Size, size, filesLabel);
                break;

            case QuickFactsKind.Person:
                if (totals is { Roles.Count: > 0 })
                {
                    Add("roles", Person, string.Join(", ", totals.Roles), totals.Roles.Count == 1 ? "Role" : "Roles");
                }

                Add("works", Library, totals?.ItemCount is { } works ? Count(works) : null, "Works in your library");
                break;

            case QuickFactsKind.CuratedCollection:
                Add("items", Folder, totals?.ItemCount is { } items ? Count(items) : null, "Items");
                Add("owner", People, totals?.Owner, "Owner");
                break;

            case QuickFactsKind.Universe:
                Add("works", Library, totals?.ItemCount is { } inUniverse ? Count(inUniverse) : null, "Works in your library");
                foreach (var count in totals?.CountsByKind.Take(3) ?? [])
                {
                    Add("kind_" + count.Label.ToLowerInvariant(), Hub, Count(count.Count), count.Label);
                }

                break;
        }

        var notes = new List<string>();
        if (totals is { ReadOnlyLibrary: true })
        {
            notes.Add(ReadOnlyLibraryNote);
        }

        if (fileWriteOff)
        {
            notes.Add(WriteBackOffNote);
        }

        // Three to five facts: never more than five, however many the kind could offer.
        return new QuickFactsModel(kind, facts.Take(5).ToList(), notes);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? TotalRuntime(ItemQuickFactsDto? totals) =>
        totals is { RuntimeSeconds: > 0 } ? DisplayFormat.FormatDurationHoursMinutesRounded(totals.RuntimeSeconds.Value) : null;

    private static string? TotalSize(ItemQuickFactsDto? totals) =>
        totals is { SizeBytes: > 0 } ? DisplayFormat.FormatFileSize(totals.SizeBytes.Value) : null;

    private static string FilesLabel(ItemQuickFactsDto? totals) => (totals?.FileCount ?? 0) switch
    {
        0 => "Size",
        1 => "1 file",
        var n => $"{n.ToString("N0", CultureInfo.CurrentCulture)} files",
    };

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string OwnedOfTotal(int owned, int total) => owned == total
        ? Count(total)
        : $"{Count(owned)} of {Count(total)}";

    private static string? OwnedOfDirect(ItemQuickFactsDto? totals) =>
        totals is { DirectTotal: > 0 } ? OwnedOfTotal(totals.DirectOwned, totals.DirectTotal) : null;

    // A Series' owned count: the Engine's count of its titles, or, when it has none, the count of its children.
    private static string OwnedLabel(string? mediaType, ItemQuickFactsDto? totals)
    {
        if (totals is { CountsByKind.Count: 1 })
        {
            return $"{totals.CountsByKind[0].Label} owned";
        }

        return (mediaType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "comic" or "comics" => "Issues owned",
            "book" or "books" => "Books owned",
            "audiobook" or "audiobooks" => "Audiobooks owned",
            _ => "Owned",
        };
    }

    private static string? OwnedCount(ItemQuickFactsDto? totals) =>
        totals?.ItemCount is { } count ? Count(count) : null;

    // The year inside a release date however it is written ("2021-10-22", "22 Oct 2021", "October 22, 2021").
    private static string? YearOf(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(date, @"(?<!\d)(1[89]\d{2}|20\d{2})(?!\d)");
        return match.Success ? match.Value : null;
    }

    private static string? TrackAndDisc(string? track, string? disc)
    {
        if (track is null && disc is null)
        {
            return null;
        }

        return track is not null && disc is not null
            ? $"Track {track} · Disc {disc}"
            : track is not null ? $"Track {track}" : $"Disc {disc}";
    }
}
