namespace MediaEngine.Domain.Models;

// What the Engine reads and writes to move items into another album, season, series, audiobook or hand-picked collection
// (Details overlay spec, WP5). The Contracts types are the wire shapes; these are the Engine's own.

/// <summary>The kinds of place an item can be moved into. The wire strings are the same.</summary>
public static class LibraryMoveKinds
{
    public const string Album = "album";
    public const string Season = "season";
    public const string Series = "series";
    public const string Audiobook = "audiobook";
    public const string Collection = "collection";
}

/// <summary>Plain-words reasons the Engine gives for a refusal that the Engine's two layers (the preview and the commit transaction) both reach.</summary>
public static class LibraryMoveReasons
{
    /// <summary>An item whose details the person set cannot be merged into an existing entry: removing its emptied title would lose them.</summary>
    public const string UserManagedMerge =
        "You changed this item's details, so it can't be merged into an existing entry. Add it anyway without a number instead.";
}

/// <summary>How one item is placed in the destination.</summary>
public enum LibraryMoveMode
{
    /// <summary>Takes the number and place of one entry in the destination's list (or, with no list, goes at the end).</summary>
    Match,

    /// <summary>Joins the destination with no number, flagged as not matched.</summary>
    AddAnyway,

    /// <summary>The entry already has a file: this one is added to it as another version (another Edition).</summary>
    AddAsVersion,
}

/// <summary>
/// One thing a person asked to move: a title (a Work), or an audiobook part (one Edition of a recording Work). Carries what the
/// preview shows and what the commit checks again.
/// </summary>
public sealed record LibraryMoveItem(
    Guid ItemId,
    bool IsEdition,
    Guid WorkId,
    Guid? EditionId,
    string MediaType,
    string WorkKind,
    Guid? ParentWorkId,
    string Title,
    string WorkTitle,
    int? Ordinal,
    double? OrdinalSort,
    int? DiscNumber,
    int? PartNumber,
    bool IsUnmatched,
    string? ParentTitle,
    int? ParentOrdinal,
    string? GrandparentTitle,
    string IdentityRevision,
    double? DurationSeconds,
    bool ProcessingRunning,
    bool HasUserManagedDetails,
    int WorkEditionCount,
    bool IsContainer,
    IReadOnlyList<Guid> AssetIds);

/// <summary>A place an item could go, as found by the destination search.</summary>
public sealed record LibraryMoveDestinationRow(
    Guid Id,
    string Kind,
    string MediaType,
    string Label,
    string? ParentLabel);

/// <summary>What a list provider needs to know about a destination.</summary>
public sealed record LibraryMoveDestinationInfo(
    Guid Id,
    string Kind,
    string MediaType,
    string Label,
    string? ParentLabel,
    int? SeasonNumber,
    Guid? ParentWorkId,
    Guid? CollectionId,
    string? ManifestJson);

/// <summary>An existing Work under an album, season or series (a track, an episode, an issue, a volume).</summary>
public sealed record LibraryMoveChild(
    Guid WorkId,
    string Title,
    int? Ordinal,
    double? OrdinalSort,
    int? DiscNumber,
    bool HasFile,
    bool IsCatalogOnly,
    bool IsUnmatched,
    double? DurationSeconds,
    bool HasUserManagedDetails);

/// <summary>One file (Edition) of an audiobook, with the part number its file carries.</summary>
public sealed record LibraryMovePart(
    Guid EditionId,
    string Title,
    int? PartNumber,
    int? PartCount,
    bool IsUnmatched,
    double? DurationSeconds);

/// <summary>Where one item is to be placed. At most one of the target fields is set.</summary>
/// <param name="ExistingWorkId">An entry that already has a Work row under the destination (a track, episode or position).</param>
/// <param name="ManifestItemId">The known series entry (a row of the series list) the item takes, so the list shows it as owned.</param>
/// <param name="Ordinal">The whole number of a new slot.</param>
/// <param name="OrdinalSort">The sort position of a new slot (carries decimals such as 1.5, and disc-aware track numbers).</param>
/// <param name="DiscNumber">The disc of a new music slot.</param>
/// <param name="PartNumber">The part number an audiobook part takes.</param>
/// <param name="TakenPartEditionId">An audiobook part that already holds the number, when adding as another version of it.</param>
public sealed record LibraryMoveTarget(
    Guid? ExistingWorkId = null,
    Guid? ManifestItemId = null,
    int? Ordinal = null,
    double? OrdinalSort = null,
    int? DiscNumber = null,
    int? PartNumber = null,
    Guid? TakenPartEditionId = null);

/// <summary>The commit's instruction for one item, with what the preview showed so the transaction can check it again.</summary>
public sealed record LibraryMovePlanRow(
    Guid ItemId,
    LibraryMoveMode Mode,
    LibraryMoveTarget Target,
    Guid? ExpectedParentWorkId,
    string ExpectedIdentityRevision);

public sealed record LibraryMovePlan(
    Guid DestinationId,
    string DestinationKind,
    IReadOnlyList<LibraryMovePlanRow> Rows,
    string ActorLabel);

/// <summary>What a committed row became. The wire strings are in the Contracts outcomes.</summary>
/// <param name="ResultEntityId">The Work the item now lives in or as: its own Work when it stayed a title of its own, the entry's Work when it was merged into one, the audiobook for a part.</param>
public sealed record LibraryMoveRowOutcome(Guid ItemId, string Outcome, Guid ResultEntityId);

/// <summary>The result of the commit transaction: either everything moved, or nothing did and why.</summary>
/// <param name="IsRefusal">When nothing moved: true for a request the Engine does not allow whatever the state (the reason is plain words for the person); false for something that changed since the preview.</param>
public sealed record LibraryMoveCommitOutcome(
    bool Committed,
    string? ConflictReason,
    IReadOnlyList<LibraryMoveRowOutcome> Rows,
    IReadOnlyList<string> RemovedContainers,
    IReadOnlyList<string> OwnedFilesToRemove,
    bool IsRefusal = false)
{
    /// <summary>Nothing moved because something changed since the preview.</summary>
    public static LibraryMoveCommitOutcome Conflict(string reason) => new(false, reason, [], [], []);

    /// <summary>Nothing moved because the request is not one the Engine allows, however fresh the preview.</summary>
    public static LibraryMoveCommitOutcome Refusal(string reason) => new(false, reason, [], [], [], IsRefusal: true);

    /// <param name="ownedFiles">Files Tuvima wrote for titles the move merged away (pictures, text tracks), for the caller to remove once the commit has succeeded.</param>
    public static LibraryMoveCommitOutcome Done(
        IReadOnlyList<LibraryMoveRowOutcome> rows, IReadOnlyList<string> removed, IReadOnlyList<string> ownedFiles) =>
        new(true, null, rows, removed, ownedFiles);
}

/// <summary>What leaving would empty: a source place that has nothing else in it, found for the preview's warnings.</summary>
public sealed record LibraryMoveSourceFacts(
    Guid ContainerId,
    string Label,
    bool BecomesEmpty);
