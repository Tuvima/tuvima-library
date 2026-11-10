using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

/// <summary>One saved bookmark of any kind and any profile, as the editor's Bookmarks tab reads and rewrites it.</summary>
public sealed record EditorBookmarkRow
{
    public Guid Id { get; init; }
    public Guid ProfileId { get; init; }
    public Guid WorkId { get; init; }
    public Guid AssetId { get; init; }
    public string PositionKind { get; init; } = BookmarkPooling.TimeKind;
    public double PositionSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public int? ChapterIndex { get; init; }
    public string? ChapterTitle { get; init; }
    public string? Label { get; init; }
    public string? Note { get; init; }
    public string? Description { get; init; }
    public string? Locator { get; init; }
    public bool IsGlobal { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? OwnerName { get; init; }

    /// <summary>The bookmark's name: its label, else its note.</summary>
    public string Name => !string.IsNullOrWhiteSpace(Label) ? Label!.Trim() : Note?.Trim() ?? string.Empty;

    public BookmarkPoolCandidate ToCandidate() => new(
        Id, AssetId, PositionKind, PositionSeconds, ChapterIndex, Locator, IsGlobal, !string.IsNullOrWhiteSpace(Name), CreatedAt);
}

/// <summary>What a committed set of bookmark operations did, and how to put everything back.</summary>
/// <param name="Snapshot">The title's bookmarks and definitive flag before the commit, for <see cref="EditorBookmarkRepository.RestoreAsync"/>.</param>
/// <param name="DeletedIds">Bookmarks that no longer exist (merged into a bookmark that joined Everyone's), so their pictures can go.</param>
/// <param name="AddedIds">New bookmarks an import made, with their time, so their pictures can be made.</param>
/// <param name="TimeChanged">Bookmarks that moved, so their pictures are made again.</param>
public sealed record EditorBookmarkCommitOutcome(
    EditorBookmarkSnapshot Snapshot,
    IReadOnlyList<(Guid Id, Guid AssetId)> DeletedIds,
    IReadOnlyList<(Guid Id, Guid AssetId, double PositionSeconds)> AddedIds,
    IReadOnlyList<(Guid Id, Guid AssetId, double PositionSeconds)> TimeChanged)
{
    /// <summary>The pictures a bookmark file carried for the bookmarks it added (base64 JPEG text, unchecked: the caller validates it).</summary>
    public IReadOnlyList<(Guid Id, string FrameBase64)> ImportedFrames { get; init; } = [];
}

/// <summary>A title's bookmarks as they were before a commit.</summary>
public sealed record EditorBookmarkSnapshot(Guid WorkId, bool WasDefinitive, DateTimeOffset? MadeAt, Guid? MadeBy, IReadOnlyList<EditorBookmarkRow> Rows);

/// <summary>A bookmark operation the commit could not carry out, with a plain reason for the person.</summary>
public sealed class EditorBookmarkCommitException(string message) : InvalidOperationException(message);

/// <summary>
/// The editor's Bookmarks tab on the data store (universal editor v5 D37): every profile's bookmarks on a title, the title's definitive-set
/// flag, and the one transaction that writes an administrator's staged operations. A bookmark is never copied: Everyone's set is the rows of
/// a title flagged <c>is_global</c> once the title is definitive, and a title that is not definitive pools every profile's rows (see
/// <see cref="BookmarkPooling"/>). Who may do any of it is checked by the caller.
/// </summary>
public sealed class EditorBookmarkRepository
{
    private readonly IDatabaseConnection _db;

    public EditorBookmarkRepository(IDatabaseConnection db) => _db = db;

    private const string RowColumns = """
        b.id AS Id, b.profile_id AS ProfileId, b.work_id AS WorkId, b.asset_id AS AssetId, b.position_kind AS PositionKind,
        b.position_seconds AS PositionSeconds, b.end_seconds AS EndSeconds, b.duration_seconds AS DurationSeconds,
        b.chapter_index AS ChapterIndex, b.chapter_title AS ChapterTitle, b.label AS Label, b.note AS Note,
        b.description AS Description, b.locator AS Locator, b.is_global AS IsGlobal, b.created_at AS CreatedAt,
        p.display_name AS OwnerName
        """;

    /// <summary>
    /// Every bookmark of every profile on these titles, limited to the files the caller may see (null means no limit). A file the caller cannot
    /// see contributes nothing.
    /// </summary>
    public Task<IReadOnlyList<EditorBookmarkRow>> GetByWorksAsync(
        IReadOnlyCollection<Guid> workIds, IReadOnlySet<Guid>? authorizedAssetIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var rows = new List<EditorBookmarkRow>();
        if (workIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<EditorBookmarkRow>>(rows);
        }

        using var conn = _db.CreateConnection();
        var allowed = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray();
        foreach (var chunk in workIds.Distinct().Chunk(400))
        {
            ct.ThrowIfCancellationRequested();
            rows.AddRange(conn.Query<EditorBookmarkRow>(
                $"""
                SELECT {RowColumns}
                FROM audiobook_bookmarks b
                LEFT JOIN profiles p ON p.id = b.profile_id
                WHERE b.work_id IN @works AND (@unrestricted = 1 OR b.asset_id IN @allowed)
                ORDER BY b.position_seconds ASC, b.created_at ASC;
                """,
                new { works = chunk.Select(GuidSql.ToBlob).ToArray(), unrestricted = authorizedAssetIds is null ? 1 : 0, allowed }));
        }

        return Task.FromResult<IReadOnlyList<EditorBookmarkRow>>(rows);
    }

    /// <summary>Which of these titles have a definitive set.</summary>
    public Task<IReadOnlySet<Guid>> GetDefinitiveAsync(IReadOnlyCollection<Guid> workIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var found = new HashSet<Guid>();
        if (workIds.Count == 0)
        {
            return Task.FromResult<IReadOnlySet<Guid>>(found);
        }

        using var conn = _db.CreateConnection();
        foreach (var chunk in workIds.Distinct().Chunk(400))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var id in conn.Query<Guid>(
                "SELECT work_id FROM bookmark_definitive_sets WHERE work_id IN @works;",
                new { works = chunk.Select(GuidSql.ToBlob).ToArray() }))
            {
                found.Add(id);
            }
        }

        return Task.FromResult<IReadOnlySet<Guid>>(found);
    }

    /// <summary>
    /// Everyone's bookmarks on a title: the rows flagged <c>is_global</c> when the title has a definitive set, else every profile's
    /// bookmarks pooled. The editor and the player both take Everyone from <see cref="Everyone"/>, so they cannot disagree.
    /// <paramref name="excludeAsset"/> leaves a file out of the set before it is built (the player drops Vault files, which have no
    /// pooled view); the editor passes nothing because it refuses a Vault title as a whole.
    /// </summary>
    public async Task<IReadOnlyList<EditorBookmarkRow>> GetEveryoneAsync(
        Guid workId, IReadOnlySet<Guid>? authorizedAssetIds, Func<string, double> sameSpotFor, CancellationToken ct = default,
        Func<Guid, bool>? excludeAsset = null)
    {
        var rows = await GetByWorksAsync([workId], authorizedAssetIds, ct).ConfigureAwait(false);
        var definitive = (await GetDefinitiveAsync([workId], ct).ConfigureAwait(false)).Contains(workId);
        return Everyone(rows, definitive, sameSpotFor, excludeAsset);
    }

    /// <summary>Everyone's rows out of a title's rows: the flagged ones when definitive, else one standing bookmark per spot.</summary>
    public static IReadOnlyList<EditorBookmarkRow> Everyone(
        IReadOnlyList<EditorBookmarkRow> rows, bool definitive, Func<string, double> sameSpotFor, Func<Guid, bool>? excludeAsset = null)
    {
        if (excludeAsset is not null)
        {
            rows = [.. rows.Where(row => !excludeAsset(row.AssetId))];
        }

        if (definitive)
        {
            return OrderForList(rows.Where(row => row.IsGlobal));
        }

        var byId = rows.ToDictionary(row => row.Id);
        return OrderForList(BookmarkPooling.Merge(rows.Select(row => row.ToCandidate()), sameSpotFor).Select(group => byId[group.Representative.Id]));
    }

    /// <summary>Shows a list in play order: by file, then by place.</summary>
    public static IReadOnlyList<EditorBookmarkRow> OrderForList(IEnumerable<EditorBookmarkRow> rows) =>
        [.. rows.OrderBy(row => row.AssetId).ThenBy(row => row.ChapterIndex ?? -1).ThenBy(row => row.PositionSeconds).ThenBy(row => row.CreatedAt)];

    /// <summary>
    /// Writes an administrator's staged operations on one title in one transaction, in order. The first operation other than "start over" makes
    /// the title's set definitive (the pooled list becomes the set). Adding a bookmark to Everyone's merges the bookmarks at the same spot: another
    /// profile's personal one is replaced and deleted (Global bookmarks spec D3), one already in Everyone's leaves it. Nothing is written when an
    /// operation cannot be carried out; <see cref="EditorBookmarkCommitException"/> says why.
    /// </summary>
    public Task<EditorBookmarkCommitOutcome> CommitAsync(
        Guid workId,
        Guid actingProfileId,
        Guid? importAssetId,
        IReadOnlyList<EditorBookmarkOp> ops,
        Func<string, double> sameSpotFor,
        IReadOnlySet<Guid>? authorizedAssetIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var allowed = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray();
            var scope = new { workId, unrestricted = authorizedAssetIds is null ? 1 : 0, allowed };
            var loaded = conn.Query<EditorBookmarkRow>(
                $"""
                SELECT {RowColumns}
                FROM audiobook_bookmarks b
                LEFT JOIN profiles p ON p.id = b.profile_id
                WHERE b.work_id = @workId AND (@unrestricted = 1 OR b.asset_id IN @allowed);
                """,
                scope,
                tx).ToList();
            var marker = conn.QuerySingleOrDefault<DefinitiveMarker>(
                "SELECT made_at AS MadeAt, made_by AS MadeBy FROM bookmark_definitive_sets WHERE work_id = @workId;", new { workId }, tx);
            var snapshot = new EditorBookmarkSnapshot(workId, marker is not null, marker?.MadeAt, marker?.MadeBy, [.. loaded]);

            var rows = loaded.ToDictionary(row => row.Id);
            var definitive = marker is not null;
            var deleted = new List<(Guid Id, Guid AssetId)>();
            var added = new List<(Guid Id, Guid AssetId, double PositionSeconds)>();
            var timeChanged = new List<(Guid Id, Guid AssetId, double PositionSeconds)>();
            var importedFrames = new List<(Guid Id, string FrameBase64)>();

            void SetFlag(Guid id, bool flag)
            {
                conn.Execute("UPDATE audiobook_bookmarks SET is_global = @flag WHERE id = @id;", new { id, flag = flag ? 1 : 0 }, tx);
                rows[id] = rows[id] with { IsGlobal = flag };
            }

            void Delete(EditorBookmarkRow row)
            {
                conn.Execute("DELETE FROM audiobook_bookmarks WHERE id = @id;", new { id = row.Id }, tx);
                rows.Remove(row.Id);
                deleted.Add((row.Id, row.AssetId));
            }

            // The first change turns the pooled list into the set: one bookmark per spot is flagged, nothing is deleted or copied.
            void EnsureDefinitive()
            {
                if (definitive)
                {
                    return;
                }

                var standing = BookmarkPooling.Merge(rows.Values.Select(row => row.ToCandidate()), sameSpotFor).Select(group => group.Representative.Id).ToHashSet();
                foreach (var row in rows.Values.ToList())
                {
                    var shouldBeIn = standing.Contains(row.Id);
                    if (row.IsGlobal != shouldBeIn)
                    {
                        SetFlag(row.Id, shouldBeIn);
                    }
                }

                conn.Execute(
                    "INSERT OR REPLACE INTO bookmark_definitive_sets (work_id, made_at, made_by) VALUES (@workId, @madeAt, @madeBy);",
                    new { workId, madeAt = DateTimeOffset.UtcNow, madeBy = actingProfileId },
                    tx);
                definitive = true;
            }

            EditorBookmarkRow Find(Guid? id, string what)
            {
                if (id is not { } key || !rows.TryGetValue(key, out var found))
                {
                    throw new EditorBookmarkCommitException($"The bookmark to {what} is no longer there.");
                }

                return found;
            }

            EditorBookmarkRow InSet(Guid? id, string what)
            {
                var found = Find(id, what);
                if (!found.IsGlobal)
                {
                    throw new EditorBookmarkCommitException($"The bookmark to {what} is not in Everyone's.");
                }

                return found;
            }

            // Everything at the same spot as a bookmark that just joined Everyone's: one already in Everyone's steps out (it stays with its
            // owner), another profile's personal one is replaced and deleted.
            void MergeSameSpot(EditorBookmarkRow joined)
            {
                var gap = sameSpotFor(joined.PositionKind);
                foreach (var near in rows.Values.Where(other => other.Id != joined.Id
                    && BookmarkPooling.IsSameSpot(other.ToCandidate(), joined.ToCandidate(), gap)).ToList())
                {
                    if (near.IsGlobal)
                    {
                        SetFlag(near.Id, false);
                    }
                    else
                    {
                        Delete(near);
                    }
                }
            }

            foreach (var op in ops)
            {
                switch (op.Kind)
                {
                    case EditorBookmarkOpKinds.Add:
                        {
                            var target = Find(op.BookmarkId, "add");
                            EnsureDefinitive();
                            target = rows[target.Id];
                            if (!target.IsGlobal)
                            {
                                SetFlag(target.Id, true);
                                MergeSameSpot(rows[target.Id]);
                            }

                            break;
                        }

                    case EditorBookmarkOpKinds.Remove:
                        {
                            var target = Find(op.BookmarkId, "remove");
                            EnsureDefinitive();
                            if (rows[target.Id].IsGlobal)
                            {
                                SetFlag(target.Id, false);
                            }

                            break;
                        }

                    case EditorBookmarkOpKinds.Rename:
                        {
                            var target = Find(op.BookmarkId, "rename");
                            EnsureDefinitive();
                            target = InSet(target.Id, "rename");
                            if (!AudiobookBookmarkNotePolicy.TryNormalize(op.Name, out var name))
                            {
                                throw new EditorBookmarkCommitException($"A bookmark name cannot exceed {AudiobookBookmarkNotePolicy.MaximumLength} characters.");
                            }

                            conn.Execute("UPDATE audiobook_bookmarks SET label = @name WHERE id = @id;", new { id = target.Id, name }, tx);
                            rows[target.Id] = target with { Label = name };
                            break;
                        }

                    case EditorBookmarkOpKinds.ChangeTime:
                        {
                            var target = Find(op.BookmarkId, "move");
                            EnsureDefinitive();
                            target = InSet(target.Id, "move");
                            if (target.PositionKind != BookmarkPooling.TimeKind)
                            {
                                throw new EditorBookmarkCommitException("Only a bookmark at a time can be moved.");
                            }

                            if (op.PositionSeconds is not { } seconds || !double.IsFinite(seconds) || seconds < 0
                                || target.DurationSeconds is { } length && seconds > length)
                            {
                                throw new EditorBookmarkCommitException("That time is not inside the file.");
                            }

                            // An end that would now sit before the start goes (a bookmark's end is optional).
                            var end = target.EndSeconds is { } oldEnd && oldEnd > seconds ? oldEnd : (double?)null;
                            conn.Execute(
                                "UPDATE audiobook_bookmarks SET position_seconds = @seconds, end_seconds = @end WHERE id = @id;",
                                new { id = target.Id, seconds, end },
                                tx);
                            rows[target.Id] = target with { PositionSeconds = seconds, EndSeconds = end };
                            timeChanged.Add((target.Id, target.AssetId, seconds));
                            break;
                        }

                    case EditorBookmarkOpKinds.Import:
                        {
                            if (importAssetId is not { } assetId)
                            {
                                throw new EditorBookmarkCommitException("This title has no file to import bookmarks onto.");
                            }

                            EnsureDefinitive();
                            foreach (var item in op.Items)
                            {
                                // A comic or PDF bookmark is a page; a book bookmark carries a reader location; anything else is a time.
                                var isPage = item.PositionKind == BookmarkPooling.PageKind;
                                var isText = !isPage && !string.IsNullOrWhiteSpace(item.Locator);
                                var locator = isPage ? PageLocator.Format((int)item.PositionSeconds) : isText ? item.Locator!.Trim() : null;
                                var spot = isText ? ReaderLocation.TryParse(locator!) : null;
                                if (!double.IsFinite(item.PositionSeconds) || item.PositionSeconds < 0
                                    || isPage && (item.PositionSeconds < 1 || item.PositionSeconds > PageLocator.MaximumPage || item.PositionSeconds != Math.Floor(item.PositionSeconds))
                                    || isText && (spot is null || locator!.Length > 4096)
                                    || !isText && !isPage && item.EndSeconds is { } itemEnd && (!double.IsFinite(itemEnd) || itemEnd <= item.PositionSeconds)
                                    || !AudiobookBookmarkNotePolicy.TryNormalize(item.Name, out var itemName)
                                    || !AudiobookBookmarkNotePolicy.TryNormalizeDescription(item.Description, out var itemDescription))
                                {
                                    throw new EditorBookmarkCommitException("A bookmark in the file is not valid.");
                                }

                                if (item.ReplacesBookmarkId is { } replaces && rows.TryGetValue(replaces, out var replaced) && replaced.IsGlobal)
                                {
                                    SetFlag(replaced.Id, false);
                                }

                                var created = new EditorBookmarkRow
                                {
                                    Id = Guid.NewGuid(),
                                    ProfileId = actingProfileId,
                                    WorkId = workId,
                                    AssetId = assetId,
                                    PositionKind = isPage ? BookmarkPooling.PageKind : isText ? BookmarkPooling.TextKind : BookmarkPooling.TimeKind,
                                    PositionSeconds = isText ? 0 : item.PositionSeconds,
                                    EndSeconds = isText || isPage ? null : item.EndSeconds,
                                    ChapterIndex = isText ? item.ChapterIndex ?? spot!.Chapter : null,
                                    ChapterTitle = string.IsNullOrWhiteSpace(item.ChapterTitle) ? null : item.ChapterTitle.Trim(),
                                    Label = itemName,
                                    Description = itemDescription,
                                    Locator = locator,
                                    IsGlobal = true,
                                    CreatedAt = DateTimeOffset.UtcNow,
                                };
                                conn.Execute(
                                    """
                                    INSERT INTO audiobook_bookmarks
                                        (id, profile_id, work_id, asset_id, chapter_index, chapter_title, position_seconds, duration_seconds, end_seconds,
                                         label, note, description, is_global, created_at, position_kind, locator)
                                    VALUES
                                        (@Id, @ProfileId, @WorkId, @AssetId, @ChapterIndex, @ChapterTitle, @PositionSeconds, NULL, @EndSeconds,
                                         @Label, NULL, @Description, 1, @CreatedAt, @PositionKind, @Locator);
                                    """,
                                    created,
                                    tx);
                                rows[created.Id] = created;
                                if (!isText && !isPage)
                                {
                                    added.Add((created.Id, created.AssetId, created.PositionSeconds));
                                    if (!string.IsNullOrEmpty(item.FrameBase64))
                                    {
                                        importedFrames.Add((created.Id, item.FrameBase64));
                                    }
                                }

                                MergeSameSpot(created);
                            }

                            break;
                        }

                    case EditorBookmarkOpKinds.StartOver:
                        conn.Execute("DELETE FROM bookmark_definitive_sets WHERE work_id = @workId;", new { workId }, tx);
                        foreach (var row in rows.Values.Where(row => row.IsGlobal).ToList())
                        {
                            SetFlag(row.Id, false);
                        }

                        definitive = false;
                        break;

                    default:
                        throw new EditorBookmarkCommitException("That bookmark change is not known.");
                }
            }

            // A bookmark the same commit added and then merged away has no picture to make or remove.
            var addedIds = added.Where(item => rows.ContainsKey(item.Id)).ToList();
            var deletedIds = deleted.Where(item => loaded.Any(row => row.Id == item.Id)).ToList();
            return new EditorBookmarkCommitOutcome(snapshot, deletedIds, addedIds, timeChanged.Where(item => rows.ContainsKey(item.Id)).ToList())
            {
                ImportedFrames = importedFrames.Where(item => rows.ContainsKey(item.Id)).ToList(),
            };
        }, ct);
    }

    /// <summary>
    /// Puts a title's bookmarks back exactly as a snapshot says (a later change in the same save failed): bookmarks the commit added go, the ones
    /// it deleted return, every flag, name and time is restored, and the definitive flag is as it was.
    /// </summary>
    public Task RestoreAsync(EditorBookmarkSnapshot snapshot, IReadOnlyCollection<Guid> addedIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            foreach (var id in addedIds)
            {
                conn.Execute("DELETE FROM audiobook_bookmarks WHERE id = @id;", new { id }, tx);
            }

            foreach (var row in snapshot.Rows)
            {
                conn.Execute(
                    """
                    INSERT OR REPLACE INTO audiobook_bookmarks
                        (id, profile_id, work_id, asset_id, chapter_index, chapter_title, position_seconds, duration_seconds, end_seconds,
                         label, note, description, is_global, created_at, position_kind, locator)
                    VALUES
                        (@Id, @ProfileId, @WorkId, @AssetId, @ChapterIndex, @ChapterTitle, @PositionSeconds, @DurationSeconds, @EndSeconds,
                         @Label, @Note, @Description, @IsGlobal, @CreatedAt, @PositionKind, @Locator);
                    """,
                    new
                    {
                        row.Id,
                        row.ProfileId,
                        row.WorkId,
                        row.AssetId,
                        row.ChapterIndex,
                        row.ChapterTitle,
                        row.PositionSeconds,
                        row.DurationSeconds,
                        row.EndSeconds,
                        row.Label,
                        row.Note,
                        row.Description,
                        IsGlobal = row.IsGlobal ? 1 : 0,
                        row.CreatedAt,
                        row.PositionKind,
                        row.Locator,
                    },
                    tx);
            }

            conn.Execute("DELETE FROM bookmark_definitive_sets WHERE work_id = @workId;", new { workId = snapshot.WorkId }, tx);
            if (snapshot.WasDefinitive)
            {
                conn.Execute(
                    "INSERT INTO bookmark_definitive_sets (work_id, made_at, made_by) VALUES (@workId, @madeAt, @madeBy);",
                    new { workId = snapshot.WorkId, madeAt = snapshot.MadeAt ?? DateTimeOffset.UtcNow, madeBy = snapshot.MadeBy },
                    tx);
            }
        }, ct);
    }

    private sealed class DefinitiveMarker
    {
        public DateTimeOffset MadeAt { get; init; }
        public Guid? MadeBy { get; init; }
    }
}
