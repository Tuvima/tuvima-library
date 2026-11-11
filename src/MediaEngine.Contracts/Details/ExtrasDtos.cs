namespace MediaEngine.Contracts.Details;

/// <summary>What an Extras card opens: a document in the reader, a folder of pictures in the photo viewer, a clip in the video player.</summary>
public static class DetailExtraCardKinds
{
    public const string Document = "document";
    public const string Pictures = "pictures";
    public const string Clip = "clip";
}

/// <summary>One card in a title's Extras row. A picture folder is one card however many pictures it holds.</summary>
public sealed class DetailExtraCardViewModel
{
    /// <summary>The Extra the card opens (for a picture folder, its first picture). Open it with the Extras viewer.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>One of <see cref="DetailExtraCardKinds"/>.</summary>
    public string Kind { get; init; } = DetailExtraCardKinds.Document;

    public string Title { get; init; } = string.Empty;

    /// <summary>The line under the title: "PDF, 42 pages", "24 pictures", "12:03".</summary>
    public string? Subtitle { get; init; }

    /// <summary>What the Extra is (universal editor v4 D20): an <c>ExtraKinds</c> value such as <c>trailer</c>, <c>document</c> or <c>other_clip</c>.</summary>
    public string ExtraKind { get; init; } = string.Empty;

    /// <summary>Where in its title the Extra belongs, for example "Season 1", "Season 1, episode 3" or "Track 4"; empty for the whole title.</summary>
    public string? Place { get; init; }

    /// <summary>The kind and place as one line for the card's badge: "Trailer", "Trailer · Season 1".</summary>
    public string? Badge { get; init; }

    /// <summary>How many files the card holds (more than one only for a picture folder).</summary>
    public int ItemCount { get; init; } = 1;

    /// <summary>Up to four pictures for the card's mosaic; empty for a document or a clip.</summary>
    public IReadOnlyList<string> PreviewIds { get; init; } = [];

    /// <summary>True when the file is no longer where Library found it; the card stays with a warning and "File not found".</summary>
    public bool IsMissing { get; init; }

    /// <summary>What a person wrote about the file (supporting files, universal editor v5); null when nothing is written.</summary>
    public string? Description { get; init; }

    /// <summary>What the file is: a role id such as <c>worksheet</c>, <c>guide</c>, <c>trailer</c> or <c>pictures</c> (the lists are in <c>config/extras.json</c>).</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>The role as shown ("Behind the scenes").</summary>
    public string RoleLabel { get; init; } = string.Empty;

    /// <summary>The group the file is in ("Character guides"); null when it is not in one.</summary>
    public string? Group { get; init; }

    /// <summary>What the file belongs to, as a scope key: <c>root</c>, <c>season:2</c>, <c>episode:2:3</c>, <c>folder:Section 1</c>, <c>file:Section 1/02 Intro.mkv</c> or <c>track:4</c>.</summary>
    public string ScopeKey { get; init; } = "root";

    /// <summary>Where <see cref="ScopeKey"/> came from: <c>folder</c> ("From its folder location") or <c>you</c> ("Set by you").</summary>
    public string ScopeSource { get; init; } = "folder";

    /// <summary>The episode or lesson the file is shown alongside, as a scope key; null when there is none.</summary>
    public string? AlongsideKey { get; init; }

    /// <summary>The short type badge on the card and the chips: "PDF", "EPUB", "TXT", "IMG", "VIDEO", "AUDIO".</summary>
    public string FileBadge { get; init; } = string.Empty;

    /// <summary>The file's size in bytes (the sum for a folder of pictures); 0 when unknown.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Pages of a document; null when unknown or not a document.</summary>
    public int? PageCount { get; init; }

    /// <summary>The words for <see cref="AlongsideKey"/> ("Season 2, episode 3", "Passing parameters"); null when there is none.</summary>
    public string? AlongsideLabel { get; init; }

    /// <summary>
    /// The media asset of the lesson (a video of a folder of videos) the file is shown alongside, so a row of the page can find its own files
    /// (universal editor v5 D31). An episode is found by the season and episode in <see cref="AlongsideKey"/> instead. Null when none.
    /// </summary>
    public string? AlongsideAssetId { get; init; }

    /// <summary>
    /// The media asset of the lesson the file belongs to, when its <see cref="ScopeKey"/> is that one file of a folder of videos, so the lesson's row and
    /// end card list it as the lesson's own file (universal editor v5 D31). An episode is found by the season and episode in <see cref="ScopeKey"/>. Null when none.
    /// </summary>
    public string? ScopeAssetId { get; init; }

    /// <summary>The tags on the file.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// One supporting file as the editor and the browser read it (universal editor v5 D11, D13, D14): the file, what a person set, and what
/// the Engine read from its folder and file names, side by side so "Use folder location" can revert. A picture folder is one item.
/// </summary>
public sealed class SupportingFileDto
{
    public string Id { get; init; } = string.Empty;

    /// <summary>One of <see cref="DetailExtraCardKinds"/>.</summary>
    public string Kind { get; init; } = DetailExtraCardKinds.Document;

    /// <summary>The file name with its extension.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>The title shown: the one a person gave, else the cleaned file name.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The title a person gave; null while the cleaned file name is used.</summary>
    public string? OwnTitle { get; init; }

    public string? Description { get; init; }

    /// <summary>The group's id; null when ungrouped.</summary>
    public string? GroupId { get; init; }

    /// <summary>The group's name; null when ungrouped.</summary>
    public string? Group { get; init; }

    /// <summary>Where the group came from: <c>folder</c>, <c>you</c>, or null when ungrouped.</summary>
    public string? GroupSource { get; init; }

    /// <summary>What the file is: a role id (see <see cref="DetailExtraCardViewModel.Role"/>).</summary>
    public string Role { get; init; } = string.Empty;

    public string RoleLabel { get; init; } = string.Empty;

    /// <summary>True when a person chose the role; false when it is only guessed from the names.</summary>
    public bool RoleIsSetByYou { get; init; }

    /// <summary>What the file belongs to (see <see cref="DetailExtraCardViewModel.ScopeKey"/>).</summary>
    public string ScopeKey { get; init; } = "root";

    /// <summary>The scope read from the file's folder, which "Use folder location" returns to.</summary>
    public string InferredScopeKey { get; init; } = "root";

    /// <summary><c>folder</c> or <c>you</c>.</summary>
    public string ScopeSource { get; init; } = "folder";

    /// <summary>The episode or lesson the file is shown alongside; null when none.</summary>
    public string? AlongsideKey { get; init; }

    /// <summary>The alongside value read from the file's name, which clearing the choice returns to; null when none.</summary>
    public string? InferredAlongsideKey { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public long SizeBytes { get; init; }

    public int? PageCount { get; init; }

    public double? DurationSeconds { get; init; }

    /// <summary>How many files the item holds (more than one only for a picture folder).</summary>
    public int ItemCount { get; init; } = 1;

    /// <summary>True when the file is no longer on disk and the record is kept for a while as "File missing".</summary>
    public bool IsMissing { get; init; }
}

/// <summary>A group of supporting files under one root title: a name a person gave, an order, and how many files are in it.</summary>
public sealed class SupportingFileGroupDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    public int FileCount { get; init; }
}

/// <summary>What the Extras viewer needs to open one card: the files behind it, in order.</summary>
public sealed class ExtraViewDto
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = DetailExtraCardKinds.Document;
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public IReadOnlyList<ExtraViewItemDto> Items { get; init; } = [];
}

/// <summary>One file in the Extras viewer.</summary>
public sealed class ExtraViewItemDto
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>The file name with its extension, used for downloads.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>The file's media type, for example <c>application/pdf</c> or <c>video/mp4</c>.</summary>
    public string MimeType { get; init; } = "application/octet-stream";

    public bool IsMissing { get; init; }
}
