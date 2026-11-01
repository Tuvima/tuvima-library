namespace MediaEngine.Contracts.Import;

/// <summary>
/// The kinds of file the one shared Import recognises (Collections and lists spec D17). A kind added later (for example a
/// Goodreads library import that also sets reading status) adds a constant here and one handler on the Engine; nothing else
/// about the Import dialog changes.
/// </summary>
public static class ImportKinds
{
    /// <summary>A list of titles from IMDb, Letterboxd, Trakt, Goodreads, StoryGraph, a CSV or a text file, or a list address from TMDb, MDBList, Open Library, MusicBrainz or Trakt.</summary>
    public const string CollectionList = "collection-list";

    /// <summary>A collection pack file (Collections and lists spec D14). Filled in by the packs packet.</summary>
    public const string PackFile = "pack-file";

    /// <summary>A bookmark pack for one title (Videos spec D46).</summary>
    public const string BookmarkPack = "bookmark-pack";

    /// <summary>A plugin's settings file (Plugin architecture spec C15). Keys are never in it.</summary>
    public const string PluginSettings = "plugin-settings";

    /// <summary>The file was not understood as any kind.</summary>
    public const string Unknown = "unknown";
}

/// <summary>Limits shared by the Engine and the Dashboard for a file dropped on the Import dialog.</summary>
public static class ImportLimits
{
    /// <summary>The largest file the Import dialog reads. A big Letterboxd export is well under this.</summary>
    public const int MaximumFileBytes = 8 * 1024 * 1024;

    /// <summary>The most characters accepted when text is pasted instead of a file.</summary>
    public const int MaximumPastedCharacters = 2_000_000;
}

/// <summary>The one privacy label every Import preview carries.</summary>
public static class ImportPrivacy
{
    public const string FilesStayOnThisMachine = "Files stay on this machine.";

    /// <summary>The line for a list address: only the address and the list's identifiers go to the site (Plugin architecture spec D8).</summary>
    public static string AddressGoesTo(string site) => $"Only this address and the list's identifiers go to {site}.";
}

/// <summary>
/// A file dropped (or text pasted) on the Import dialog. The file's bytes are base64 text; pasted text goes in
/// <see cref="Text"/>. Both stay on this machine: nothing here is sent to any other site.
/// </summary>
public sealed record ImportRecogniseRequest
{
    public string FileName { get; init; } = string.Empty;
    public string? ContentBase64 { get; init; }
    public string? Text { get; init; }
    public Guid? ProfileId { get; init; }
}

/// <summary>One labelled fact in a preview, such as "Titles: 250".</summary>
public sealed record ImportFactDto(string Label, string Value);

/// <summary>
/// What the Engine found in a file, in that kind's own words. Every kind fills the same frame (a heading, a source line, a few
/// facts, notes and an action) so the dialog looks the same whatever was dropped; a list also carries its full preview.
/// </summary>
public sealed record ImportPreviewDto
{
    /// <summary>See <see cref="ImportKinds"/>.</summary>
    public string Kind { get; init; } = ImportKinds.Unknown;

    /// <summary>The kind in plain words, for example "Collection list" or "Bookmark pack".</summary>
    public string KindLabel { get; init; } = string.Empty;

    /// <summary>True when the file is a kind Library knows. False shows <see cref="Message"/> instead of a preview.</summary>
    public bool Recognised { get; init; }

    /// <summary>The big line: the list's name, the pack's name, the plugin's name.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>The source line under the heading, for example "Letterboxd export, 250 titles".</summary>
    public string Subheading { get; init; } = string.Empty;

    public IReadOnlyList<ImportFactDto> Facts { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Why a file was not understood, or why it cannot be used here. Plain sentences.</summary>
    public string? Message { get; init; }

    /// <summary>Where the fix for <see cref="Message"/> is, when it is in Settings (for example the Plugins page). Null otherwise.</summary>
    public string? MessageLinkHref { get; init; }

    /// <summary>The words on that link.</summary>
    public string? MessageLinkLabel { get; init; }

    /// <summary>True when the Engine can apply this kind from the Import dialog on its own.</summary>
    public bool CanApply { get; init; }

    /// <summary>The label of the action, for example "Add plugin settings".</summary>
    public string? ApplyLabel { get; init; }

    /// <summary>The privacy line shown under every preview. A list address says which site it asks (see <see cref="ImportPrivacy.AddressGoesTo"/>).</summary>
    public string PrivacyNote { get; init; } = ImportPrivacy.FilesStayOnThisMachine;

    /// <summary>The full list preview when <see cref="Kind"/> is <see cref="ImportKinds.CollectionList"/>.</summary>
    public Collections.ListImportPreviewDto? List { get; init; }

    /// <summary>The bookmark pack facts when <see cref="Kind"/> is <see cref="ImportKinds.BookmarkPack"/>.</summary>
    public ImportBookmarkPackSummaryDto? BookmarkPack { get; init; }
}

/// <summary>What a bookmark pack file holds, read without adding anything.</summary>
public sealed record ImportBookmarkPackSummaryDto
{
    public string Name { get; init; } = string.Empty;
    public string? Author { get; init; }
    public int BookmarkCount { get; init; }

    /// <summary>The library title the pack's identifiers point at, when exactly one is found.</summary>
    public Guid? WorkId { get; init; }
    public string? WorkTitle { get; init; }
}

/// <summary>Asks the Engine to apply a recognised file of a kind that can be applied from the dialog (plugin settings).</summary>
public sealed record ImportApplyRequest
{
    public string Kind { get; init; } = ImportKinds.Unknown;
    public string FileName { get; init; } = string.Empty;
    public string? ContentBase64 { get; init; }
    public string? Text { get; init; }
    public Guid? ProfileId { get; init; }
}

/// <summary>What applying a file did, in a sentence.</summary>
public sealed record ImportApplyResultDto(bool Applied, string Message);
