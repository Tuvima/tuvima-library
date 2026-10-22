namespace MediaEngine.Domain.PersonalMedia;

/// <summary>
/// The three kinds a View folder (and every View item) can have. Each kind has its own folder list and its own
/// per-profile permission (Library setup D36, D49).
/// </summary>
public static class ViewContentKinds
{
    public const string Photos = "photos";
    public const string PersonalVideos = "personal_videos";
    public const string Documents = "documents";

    public static IReadOnlyList<string> All { get; } = [Photos, PersonalVideos, Documents];

    public static bool IsValid(string? value) =>
        value is Photos or PersonalVideos or Documents;
}

/// <summary>
/// What each View kind accepts, as one pure rule (Library setup D49). The file types are the item media kinds the
/// View index already uses: <c>image</c>, <c>video</c>, <c>document</c>, <c>audio</c> and <c>other</c>.
/// </summary>
public static class ViewKindAcceptance
{
    private const string Image = "image";
    private const string Video = "video";
    private const string Document = "document";

    /// <summary>The source key of the upload/backup source every personal space starts with.</summary>
    public const string BrowserUploadSourceKey = "builtin:browser-uploads";

    /// <summary>
    /// True when a folder of <paramref name="contentKind"/> takes a file of <paramref name="mediaKind"/>.
    /// Photos take pictures; Personal videos take videos; Documents take documents and pictures of paper.
    /// Audio and other files are never accepted in any View kind.
    /// </summary>
    public static bool Accepts(string? contentKind, string? mediaKind) => contentKind switch
    {
        ViewContentKinds.Photos => mediaKind == Image,
        ViewContentKinds.PersonalVideos => mediaKind == Video,
        ViewContentKinds.Documents => mediaKind is Document or Image,
        _ => false,
    };

    /// <summary>
    /// True for sources that take what a person sends: the upload/backup source of a personal space and the Shared
    /// Library's direct-add and contribution intake. A camera roll is pictures and videos together.
    /// </summary>
    public static bool IsIntakeSource(string? sourceKey) =>
        !string.IsNullOrWhiteSpace(sourceKey)
        && (sourceKey.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceKey, "shared:timeline", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceKey, "shared:folders", StringComparison.OrdinalIgnoreCase)
            || sourceKey.StartsWith("shared:folder:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An intake source takes pictures and videos (a camera roll is both) and the documents people already send it
    /// (uploads and backups of pictures and documents, Library setup D47). Audio and other files are never taken.
    /// </summary>
    public static bool IntakeAccepts(string? mediaKind) => mediaKind is Image or Video or Document;

    /// <summary>Whether a file of <paramref name="mediaKind"/> belongs in the source with this kind and key.</summary>
    public static bool SourceAccepts(string? contentKind, string? sourceKey, string? mediaKind) =>
        IsIntakeSource(sourceKey) ? IntakeAccepts(mediaKind) : Accepts(contentKind, mediaKind);

    /// <summary>
    /// The kind an item takes when it arrives through an intake source: its own type (a picture is a photo, a video is
    /// a personal video). Null for anything no View kind accepts.
    /// </summary>
    public static string? OwnKindOf(string? mediaKind) => mediaKind switch
    {
        Image => ViewContentKinds.Photos,
        Video => ViewContentKinds.PersonalVideos,
        Document => ViewContentKinds.Documents,
        _ => null,
    };

    /// <summary>The kind a scanned item gets: the folder's kind, or its own type when the folder is an intake source.</summary>
    public static string? KindFor(string? contentKind, string? sourceKey, string? mediaKind) =>
        IsIntakeSource(sourceKey) ? OwnKindOf(mediaKind) : contentKind;
}
