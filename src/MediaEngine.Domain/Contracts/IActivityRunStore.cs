namespace MediaEngine.Domain.Contracts;

/// <summary>The sorts of run Library records (spec library-activity D21).</summary>
public static class ActivityRunKinds
{
    public const string Added = "added";
    public const string Updated = "updated";
    public const string Fixed = "fixed";
    public const string Removed = "removed";

    public static bool IsKnown(string? kind) => kind is Added or Updated or Fixed or Removed;
}

/// <summary>The states of a run: Active while items still join it, then Completed.</summary>
public static class ActivityRunStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>One run: a scan, refresh, fix or removal in one library, with the titles it touched.</summary>
public sealed record ActivityRunRecord(
    Guid Id,
    string LibraryId,
    string Kind,
    string Status,
    bool IsFirstRun,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? EndedAt,
    int ItemCount);

/// <summary>One title a run touched, with the key the stacking rule groups it under (one tile per show, album, folder or title).</summary>
public sealed record ActivityRunItemRecord(
    Guid Id,
    Guid RunId,
    Guid? WorkId,
    Guid? AssetId,
    string MediaType,
    string Title,
    string GroupKey,
    string GroupTitle,
    Guid? GroupWorkId,
    DateTimeOffset CreatedAt);

/// <summary>A run with the number of items it touched per media type, for the History entry's plain count.</summary>
public sealed record ActivityRunSummary(ActivityRunRecord Run, IReadOnlyDictionary<string, int> CountsByMediaType);

/// <summary>What the History list asks for: a month, kinds, a date range, a title search and the library chips.</summary>
public sealed record ActivityRunQuery(
    IReadOnlyCollection<string>? LibraryIds = null,
    IReadOnlyCollection<string>? Kinds = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Search = null,
    bool NewestFirst = true,
    int Offset = 0,
    int Limit = 200);

/// <summary>One stacked tile inside an opened run: all the episodes of one show, the tracks of one album, or a single title.</summary>
public sealed record ActivityRunGroup(
    string GroupKey,
    string GroupTitle,
    string MediaType,
    Guid? GroupWorkId,
    Guid? FirstWorkId,
    Guid? FirstAssetId,
    int ItemCount);

/// <summary>The library's own question: what asset belongs to which library, and where does its file live (for the Videos folder rule).</summary>
public sealed record ActivityAssetContext(string? LibraryId, string? FilePath);

/// <summary>
/// Storage for activity runs (spec library-activity WP1). The recorder decides when a run opens and closes; this only reads and writes.
/// </summary>
public interface IActivityRunStore
{
    /// <summary>The newest active run of this kind in this library whose last item joined at or after <paramref name="notBefore"/>, or null.</summary>
    Task<ActivityRunRecord?> FindOpenRunAsync(string libraryId, string kind, DateTimeOffset notBefore, CancellationToken ct = default);

    /// <summary>True when this library has ever had a run (so the next one is not its first).</summary>
    Task<bool> LibraryHasRunsAsync(string libraryId, CancellationToken ct = default);

    Task InsertRunAsync(ActivityRunRecord run, CancellationToken ct = default);

    /// <summary>Adds one item to a run and moves the run's last activity and count forward.</summary>
    Task AddItemAsync(ActivityRunItemRecord item, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Completes every active run whose last item joined before <paramref name="quietBefore"/>; returns how many closed.</summary>
    Task<int> CompleteQuietRunsAsync(DateTimeOffset quietBefore, CancellationToken ct = default);

    Task<ActivityAssetContext?> GetAssetContextAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>The runs that match, with counts per media type, newest or oldest first.</summary>
    Task<IReadOnlyList<ActivityRunSummary>> ListRunsAsync(ActivityRunQuery query, CancellationToken ct = default);

    Task<ActivityRunRecord?> GetRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>One run's items as stacked groups (D12), optionally narrowed to one media type, a page at a time.</summary>
    Task<IReadOnlyList<ActivityRunGroup>> ListGroupsAsync(Guid runId, string? mediaType, int offset, int limit, CancellationToken ct = default);

    /// <summary>Every title that matches a search, with each run it appears in and how many items that run added (D18).</summary>
    Task<IReadOnlyList<ActivityTitleHit>> SearchTitlesAsync(ActivityRunQuery query, CancellationToken ct = default);

    /// <summary>The newest Completed or Active run end per library; used for "Last completed scan …".</summary>
    Task<DateTimeOffset?> GetLastCompletedAsync(IReadOnlyCollection<string>? libraryIds, CancellationToken ct = default);

    /// <summary>Deletes runs that started before <paramref name="olderThan"/> (D20); returns how many.</summary>
    Task<int> PurgeAsync(DateTimeOffset olderThan, CancellationToken ct = default);

    /// <summary>
    /// The Works added by a later (not first) run that ended, or is still open, within the New window (D14).
    /// </summary>
    Task<IReadOnlySet<Guid>> GetNewWorkIdsAsync(IReadOnlyCollection<Guid> workIds, DateTimeOffset newSince, CancellationToken ct = default);

    /// <summary>The Works added by a later run within the New window, newest first, up to <paramref name="limit"/>.</summary>
    Task<IReadOnlySet<Guid>> ListNewWorkIdsAsync(DateTimeOffset newSince, int limit, CancellationToken ct = default);
}

/// <summary>One run in which a searched title (a show, album or single title) was added, with how much.</summary>
public sealed record ActivityTitleHit(
    string GroupKey,
    string GroupTitle,
    string MediaType,
    Guid? GroupWorkId,
    Guid? FirstWorkId,
    Guid EntryId,
    string LibraryId,
    string Kind,
    DateTimeOffset StartedAt,
    int ItemCount);
