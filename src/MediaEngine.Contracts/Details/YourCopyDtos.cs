namespace MediaEngine.Contracts.Details;

/// <summary>
/// The "Your copy" row at the bottom of a details page (details-page-bottom spec D4, D5, section 4): what the person's own file or files are
/// like, as short chips and one panel of facts. The Engine reads the stored file facts and writes every value, including the show-, album- and
/// book-level summaries (the common quality, the exceptions and the totals), so the Dashboard only draws them. The wording of the values is
/// English, like the page's other fact strings; the labels around them (group titles, "File details") belong to the Dashboard.
/// </summary>
public sealed class DetailYourCopyViewModel
{
    /// <summary>One block per edition when a title has several editions (each labelled by edition), otherwise one block.</summary>
    public IReadOnlyList<DetailYourCopyBlockViewModel> Blocks { get; init; } = [];
}

/// <summary>One edition's (or the one title's) Your copy row: chips, the panel's groups, and the file's own name and place.</summary>
public sealed class DetailYourCopyBlockViewModel
{
    /// <summary>The edition's name, set only when the title has more than one edition (for example "Director's cut"). Null otherwise.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Which icon and which panel groups the row has: <see cref="DetailYourCopyKinds"/>. A movie, episode or show reads as video; the Dashboard
    /// picks the icon from this and the group titles from each group's <see cref="DetailYourCopyGroupViewModel.Key"/>.
    /// </summary>
    public string Kind { get; init; } = DetailYourCopyKinds.Video;

    /// <summary>True while the facts are not known yet (the file has not been scanned): the row shows "Reading file facts" with no chips and no File details control.</summary>
    public bool IsReadingFacts { get; init; }

    /// <summary>The key facts, most important first ("1080p", "H.264", "AAC 5.1", "MKV", "HDR10"). A fact that is not known is not in the list.</summary>
    public IReadOnlyList<string> Chips { get; init; } = [];

    /// <summary>The File details panel's groups, in order. Empty while <see cref="IsReadingFacts"/> is true.</summary>
    public IReadOnlyList<DetailYourCopyGroupViewModel> Groups { get; init; } = [];

    /// <summary>The file's name. Null when the block covers several files (a show, an album), which have no single name.</summary>
    public string? FileName { get; init; }

    /// <summary>
    /// The file's place on disk, sent only to a viewer who may already edit the item (the same people who see Edit), and shown only when asked
    /// ("Show file location"). Null for everyone else and when the block covers several files.
    /// </summary>
    public string? FileLocation { get; init; }
}

/// <summary>One group of the File details panel: a key the Dashboard titles and gives an icon, and its lines.</summary>
public sealed class DetailYourCopyGroupViewModel
{
    /// <summary>One of <see cref="DetailYourCopyGroupKeys"/>.</summary>
    public string Key { get; init; } = string.Empty;

    public IReadOnlyList<string> Lines { get; init; } = [];
}

/// <summary>The kinds of Your copy row (<see cref="DetailYourCopyBlockViewModel.Kind"/>).</summary>
public static class DetailYourCopyKinds
{
    public const string Video = "video";
    public const string Show = "show";
    public const string Album = "album";
    public const string Audio = "audio";
    public const string Book = "book";
    public const string Audiobook = "audiobook";
    public const string Comic = "comic";
}

/// <summary>The panel groups (<see cref="DetailYourCopyGroupViewModel.Key"/>): video items use video, audio_subtitles and file; albums audio, tracks and files; books book, reading and file.</summary>
public static class DetailYourCopyGroupKeys
{
    public const string Video = "video";
    public const string AudioSubtitles = "audio_subtitles";
    public const string File = "file";
    public const string Files = "files";
    public const string Audio = "audio";
    public const string Tracks = "tracks";
    public const string Book = "book";
    public const string Reading = "reading";
}
