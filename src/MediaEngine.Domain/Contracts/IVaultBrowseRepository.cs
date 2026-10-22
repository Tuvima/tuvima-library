namespace MediaEngine.Domain.Contracts;

/// <summary>Which scenes a Vault list holds, by whether a stash-box record is attached (Videos spec D39).</summary>
public static class VaultSceneMatchFilter
{
    public const string All = "all";
    public const string Matched = "matched";
    public const string Unmatched = "unmatched";

    public static bool IsValid(string? value) => value is All or Matched or Unmatched;
}

/// <summary>How a list of scenes is ordered (Videos spec D39). "Last played" is deliberately absent: watching the Vault leaves no trace (D33).</summary>
public static class VaultSceneSorts
{
    public const string DateAdded = "date_added";
    public const string Title = "title";
    public const string ReleaseDate = "release_date";
    public const string Duration = "duration";

    public static bool IsValid(string? value) => value is DateAdded or Title or ReleaseDate or Duration;
}

/// <summary>How the performer and studio lists are ordered (Videos spec D39).</summary>
public static class VaultNameSorts
{
    public const string Name = "name";
    public const string SceneCount = "scene_count";

    public static bool IsValid(string? value) => value is Name or SceneCount;
}

/// <summary>
/// One list of Vault scenes. Only the filters that are set apply. <see cref="Text"/> matches what is actually stored for a scene: its
/// title, description, file name and the names linked to it.
/// </summary>
public sealed record VaultSceneQuery(
    string Match = VaultSceneMatchFilter.All,
    string Sort = VaultSceneSorts.DateAdded,
    bool Descending = true,
    Guid? PerformerId = null,
    Guid? StudioId = null,
    Guid? TagId = null,
    string? FolderId = null,
    string? Text = null,
    int Skip = 0,
    int Take = 60);

/// <summary>
/// One Vault scene as a card or row needs it. <see cref="ImageArtworkId"/> is the stash-box scene image for a matched scene, else the
/// still frame saved at ingestion (chosen once, never re-picked, D41); <see cref="StillArtworkIds"/> are up to four saved stills for the
/// optional hover cycling.
/// </summary>
public sealed record VaultSceneRow(
    Guid AssetId,
    Guid WorkId,
    string Title,
    bool Matched,
    string FileName,
    string FolderId,
    string FolderName,
    double? DurationSeconds,
    long? SizeBytes,
    DateTimeOffset? AddedAt,
    string? ReleaseDate,
    string? Studio,
    IReadOnlyList<string> Tags,
    Guid? ImageArtworkId,
    IReadOnlyList<Guid> StillArtworkIds);

/// <param name="Total">How many scenes the filters match, before paging.</param>
/// <param name="Heading">The name of the performer, studio or tag the list was filtered by, when it was (so a tile can title its page).</param>
public sealed record VaultSceneList(IReadOnlyList<VaultSceneRow> Items, int Total, string? Heading);

/// <summary>A name linked to a scene, for the scene's own page.</summary>
public sealed record VaultLinkedName(Guid Id, string Name);

/// <summary>One scene's own page: the card facts, the description and the linked names, and how it was matched.</summary>
public sealed record VaultSceneDetailRow(
    VaultSceneRow Scene,
    string? Description,
    string? MatchedBy,
    IReadOnlyList<VaultLinkedName> Performers,
    IReadOnlyList<VaultLinkedName> Studios,
    IReadOnlyList<VaultLinkedName> Tags);

/// <summary>A folder (a series or section of the folder layout) and how many scenes it holds. <see cref="Id"/> is stable and carries no path.</summary>
public sealed record VaultFolderRow(string Id, string Name, int SceneCount, Guid? ImageArtworkId);

/// <summary>A performer, studio or tag with the number of owned scenes that refer to it. Always at least one: a name with no scene is never listed.</summary>
public sealed record VaultNameTileRow(Guid Id, string Name, int SceneCount, Guid? ImageArtworkId);

public sealed record VaultNameSearchResult(IReadOnlyList<VaultNameTileRow> Performers, IReadOnlyList<VaultNameTileRow> Studios);

/// <summary>
/// Reads for the Vault's browsing views (Videos spec D39, D41). Every method reads Vault libraries only and never touches the shared
/// People, Tag, Series or artwork records. The caller (the Engine's Vault gate) decides whether the person may see the Vault at all.
/// </summary>
public interface IVaultBrowseRepository
{
    Task<VaultSceneList> ListScenesAsync(VaultSceneQuery query, CancellationToken ct = default);

    Task<VaultSceneDetailRow?> GetSceneAsync(Guid assetId, CancellationToken ct = default);

    Task<IReadOnlyList<VaultFolderRow>> ListFoldersAsync(CancellationToken ct = default);

    /// <summary>Performers with at least one owned scene.</summary>
    Task<IReadOnlyList<VaultNameTileRow>> ListPerformersAsync(string sort, bool descending, CancellationToken ct = default);

    /// <summary>Studios with at least one owned scene.</summary>
    Task<IReadOnlyList<VaultNameTileRow>> ListStudiosAsync(string sort, bool descending, CancellationToken ct = default);

    /// <summary>Tags with at least one owned scene.</summary>
    Task<IReadOnlyList<VaultNameTileRow>> ListTagsAsync(string sort, bool descending, CancellationToken ct = default);

    /// <summary>Performers and studios whose name contains the text and that have at least one owned scene.</summary>
    Task<VaultNameSearchResult> SearchNamesAsync(string text, int take, CancellationToken ct = default);
}
