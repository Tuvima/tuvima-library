using System.Globalization;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>The editor's Bookmarks tab was asked for a title in the Vault, which never takes part in Global bookmarks (universal editor v5 D37).</summary>
public sealed class EditorBookmarkVaultException : Exception;

/// <summary>
/// The Bookmarks tab of the universal editor (v5 D37, WP15): what an administrator reads to make a title's definitive set of Global
/// bookmarks, the check and the one-transaction write of the staged operations, and the title's import and export. Every entry point is
/// administrators only and refuses a Vault title; the Dashboard hides the tab, and these checks are the ones that count. A bookmark is never
/// copied: Global set is the rows of the title flagged <c>is_global</c> once it is definitive, and until then every profile's bookmarks
/// pooled (<see cref="BookmarkPooling"/>).
/// </summary>
public sealed class EditorBookmarksService(
    PlayerCatalogueScope scope,
    EditorBookmarkRepository repository,
    BookmarkClosenessProvider closeness,
    BookmarkSetService sets,
    ReaderBookmarkSetService readerSets,
    PageBookmarkSetService pageSets,
    BookmarkFrameService frames,
    IVaultLibraryRepository vault,
    ISystemActivityRepository activity,
    ILogger<EditorBookmarksService> logger)
{
    public const string NothingToChange = "There are no bookmark changes to save.";
    public const string NotAllowed = "Only an administrator can change a title's Global bookmarks.";
    public const string NoGlobal = "Only movies and TV have Global bookmarks. Bookmarks on books and audiobooks stay personal.";
    public const string NoTitle = "This title is no longer in your library.";
    public const string InTheVault = "Bookmarks of a Vault title are not edited here.";

    /// <summary>The person asking, the files of the title they may see, and every file they may see in all.</summary>
    internal sealed record Title(Guid ProfileId, IReadOnlyList<DisplayWorkRow> Assets, IReadOnlySet<Guid> AllowedAssets);

    /// <summary>The title's administrator, its files and the Vault rule, checked once for every entry point below.</summary>
    internal async Task<Title> RequireTitleAsync(Guid workId, CancellationToken ct)
    {
        var profile = await RequireAdministratorAsync(ct);
        var assets = (await scope.AssetsAsync(ct)).Where(row => row.WorkId == workId).ToList();
        if (assets.Count == 0)
        {
            throw new PlayerResourceDeniedException();
        }

        if (await vault.IsInVaultAsync(workId, ct))
        {
            throw new EditorBookmarkVaultException();
        }

        return new Title(profile, assets, await scope.AssetIdsAsync(ct));
    }

    /// <summary>The signed-in profile, when it is acting as a server administrator; otherwise the refusal the endpoints turn into a 403.</summary>
    internal async Task<Guid> RequireAdministratorAsync(CancellationToken ct)
    {
        var profile = await scope.RequireProfileAsync(null, ct);
        if (!await scope.IsServerAdministratorAsync(ct))
        {
            throw new PlayerBookmarkAdminDeniedException();
        }

        return profile;
    }

    internal double SameSpot(string kind) => kind == BookmarkPooling.TimeKind ? closeness.Current.Time.SameSpot
        : kind == BookmarkPooling.PageKind ? closeness.Current.Page.SameSpot
        : closeness.Current.Text.SameSpot;

    /// <summary>The title's two lists and its state, as the tab shows them.</summary>
    public async Task<EditorBookmarksDto> ReadAsync(Guid workId, CancellationToken ct = default)
    {
        var title = await RequireTitleAsync(workId, ct);
        var rows = await repository.GetByWorksAsync([workId], title.AllowedAssets, ct);
        var definitive = (await repository.GetDefinitiveAsync([workId], ct)).Contains(workId);
        var everyone = EditorBookmarkRepository.Everyone(rows, definitive, SameSpot);
        var hasTimes = !title.Assets.Any(asset => asset.MediaType is "Books" or "Comics");
        var primary = title.Assets.OrderBy(asset => asset.AssetId).First();
        var isPageFile = await pageSets.IsPageFileAsync(primary.AssetId, ct);
        var hasFrames = false;
        foreach (var asset in title.Assets)
        {
            if (await frames.IsVideoAssetAsync(asset.AssetId, ct))
            {
                hasFrames = true;
                break;
            }
        }

        var inSet = everyone.Select(row => row.Id).ToHashSet();
        var candidates = everyone.Select(row => row.ToCandidate()).ToList();
        return new EditorBookmarksDto
        {
            WorkId = workId,
            State = definitive ? EditorBookmarkStates.Definitive : EditorBookmarkStates.Pooled,
            Everyone = [.. everyone.Select(row => ToRow(row, title.ProfileId, inEveryone: true))],
            FromProfiles =
            [
                .. EditorBookmarkRepository.OrderForList(rows).Select(row => ToRow(
                    row, title.ProfileId,
                    inSet.Contains(row.Id) || candidates.Any(set => BookmarkPooling.IsSameSpot(set, row.ToCandidate(), SameSpot(row.PositionKind))))),
            ],
            AssetId = primary.AssetId,
            HasTimes = hasTimes,
            CanExchange = hasTimes || IsBook(title) || isPageFile,
            HasFrames = hasFrames,
            RunningTimeSeconds = double.TryParse(primary.FileDurationSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var length) && length > 0 ? length : null,
        };
    }

    /// <summary>The Engine's reason an operation list cannot be saved, or null when every operation can be carried out.</summary>
    public async Task<string?> CheckAsync(Guid workId, EditorBookmarkChangeDto? change, CancellationToken ct = default)
    {
        try
        {
            var title = await RequireTitleAsync(workId, ct);
            var rows = await repository.GetByWorksAsync([workId], title.AllowedAssets, ct);
            var definitive = (await repository.GetDefinitiveAsync([workId], ct)).Contains(workId);
            if (change?.Ops.Count > 0 && !AllowsGlobal(title))
            {
                return NoGlobal;
            }

            return EditorBookmarkOpRules.Check(change?.Ops ?? [], rows, definitive, SameSpot, hasImportFile: title.Assets.Count > 0);
        }
        catch (PlayerBookmarkAdminDeniedException)
        {
            return NotAllowed;
        }
        catch (PlayerResourceDeniedException)
        {
            return NoTitle;
        }
        catch (EditorBookmarkVaultException)
        {
            return InTheVault;
        }
    }

    /// <summary>
    /// Writes the operations in one transaction and returns how to put everything back exactly (a later change in the same save failed). The
    /// pictures of bookmarks that were merged away are removed, and the pictures of imported and moved bookmarks are queued.
    /// </summary>
    public async Task<Func<CancellationToken, Task>> ApplyAsync(Guid workId, EditorBookmarkChangeDto change, CancellationToken ct = default)
    {
        var title = await RequireTitleAsync(workId, ct);
        if (change.Ops.Count > 0 && !AllowsGlobal(title))
        {
            throw new InvalidOperationException(NoGlobal);
        }

        var ops = change.Ops;
        var importAsset = title.Assets.OrderBy(asset => asset.AssetId).First().AssetId;
        EditorBookmarkCommitOutcome outcome;
        try
        {
            outcome = await repository.CommitAsync(workId, title.ProfileId, importAsset, ops, SameSpot, title.AllowedAssets, ct);
        }
        catch (EditorBookmarkCommitException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        foreach (var (id, assetId) in outcome.DeletedIds)
        {
            frames.RemoveFrame(assetId, id);
        }

        // A picture the bookmark file carried is kept when it is a valid small JPEG; otherwise the Engine makes the picture itself.
        var supplied = outcome.ImportedFrames.ToDictionary(item => item.Id, item => BookmarkFrameImage.FromBase64(item.FrameBase64));
        foreach (var (id, assetId, position) in outcome.AddedIds)
        {
            if (supplied.TryGetValue(id, out var jpeg) && await frames.TryUseSuppliedAsync(assetId, id, jpeg, ct))
            {
                continue;
            }

            await frames.TryEnqueueAsync(new AudiobookBookmarkDto { Id = id, AssetId = assetId, PositionSeconds = position }, ct);
        }

        var before = outcome.Snapshot.Rows.ToDictionary(row => row.Id);
        foreach (var (id, assetId, position) in outcome.TimeChanged)
        {
            // The picture of the old moment goes at once and the one at the new time is made in the background (Unified player v2 D21a).
            await frames.TryEnqueueAsync(new AudiobookBookmarkDto { Id = id, AssetId = assetId, PositionSeconds = position }, ct, timeChanged: true);
        }

        await LogAsync(workId, title.ProfileId, ops, before, ct);
        return async undoCt =>
        {
            await repository.RestoreAsync(outcome.Snapshot, [.. outcome.AddedIds.Select(added => added.Id)], undoCt);
            foreach (var (id, assetId, _) in outcome.AddedIds)
            {
                frames.RemoveFrame(assetId, id);
            }

            // Bookmarks that came back, and ones that went back to their old time, get their pictures again.
            foreach (var (id, assetId) in outcome.DeletedIds)
            {
                if (before.TryGetValue(id, out var restored))
                {
                    await frames.TryEnqueueAsync(new AudiobookBookmarkDto { Id = id, AssetId = assetId, PositionSeconds = restored.PositionSeconds }, undoCt);
                }
            }

            foreach (var (id, assetId, _) in outcome.TimeChanged)
            {
                if (before.TryGetValue(id, out var restored))
                {
                    await frames.TryEnqueueAsync(new AudiobookBookmarkDto { Id = id, AssetId = assetId, PositionSeconds = restored.PositionSeconds }, undoCt, timeChanged: true);
                }
            }
        };
    }

    /// <summary>The editor's Import: a bookmark file beside the title's Global list, row by row. Changes nothing.</summary>
    public async Task<BookmarkSetCompareDto> CompareAsync(Guid workId, EditorBookmarkCompareRequestDto request, CancellationToken ct = default)
    {
        var title = await RequireTitleAsync(workId, ct);
        RequireFileWithinLimit(request.ContentBase64, BookmarkPackLimits.MaximumFileBytes);
        var assetId = title.Assets.OrderBy(asset => asset.AssetId).First().AssetId;
        if (await pageSets.IsPageFileAsync(assetId, ct))
        {
            var pageRows = await repository.GetEveryoneAsync(workId, title.AllowedAssets, SameSpot, ct);
            return await pageSets.CompareWithEveryoneAsync(
                assetId,
                new BookmarkSetRequestDto { AssetId = assetId, ContentBase64 = request.ContentBase64, Mode = request.Mode },
                pageRows,
                ct);
        }

        if (IsBook(title))
        {
            var bookRows = await repository.GetEveryoneAsync(workId, title.AllowedAssets, SameSpot, ct);
            return await readerSets.CompareWithEveryoneAsync(
                assetId,
                new BookmarkSetRequestDto { AssetId = assetId, ContentBase64 = request.ContentBase64, Mode = request.Mode },
                [.. bookRows.Select(ToReaderBookmark)],
                ct);
        }

        var everyone = await EveryoneAsPlayerAsync(workId, title, ct);
        return await sets.CompareWithEveryoneAsync(
            title.ProfileId,
            workId,
            new BookmarkSetRequestDto
            {
                AssetId = assetId,
                ContentBase64 = request.ContentBase64,
                RunningTimeSeconds = request.RunningTimeSeconds,
                Chapters = request.Chapters,
                Mode = request.Mode,
            },
            everyone,
            ct);
    }

    /// <summary>The editor's Export: the title's Global list as one bookmark file. Never anyone's personal bookmarks.</summary>
    public async Task<BookmarkPackFileDto> ExportAsync(Guid workId, CancellationToken ct = default)
    {
        var title = await RequireTitleAsync(workId, ct);
        var primary = title.Assets.OrderBy(asset => asset.AssetId).First();
        if (await pageSets.IsPageFileAsync(primary.AssetId, ct))
        {
            return await pageSets.ExportEveryoneAsync(primary.AssetId, await repository.GetEveryoneAsync(workId, title.AllowedAssets, SameSpot, ct), ct);
        }

        if (IsBook(title))
        {
            var bookRows = await repository.GetEveryoneAsync(workId, title.AllowedAssets, SameSpot, ct);
            return await readerSets.ExportEveryoneAsync(primary.AssetId, [.. bookRows.Select(ToReaderBookmark)], ct);
        }

        var definitive = (await repository.GetDefinitiveAsync([workId], ct)).Contains(workId);
        var everyone = (await EveryoneAsPlayerAsync(workId, title, ct)).Select(bookmark => ForExport(bookmark, definitive)).ToList();
        var length = double.TryParse(primary.FileDurationSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : (double?)null;
        return await sets.ExportEveryoneAsync(primary.AssetId, everyone, length, ct);
    }

    /// <summary>
    /// What a bookmark file may carry of a bookmark. A definitive set is the administrator's own curated list, so its names and notes go out.
    /// A pooled list is every profile's private bookmarks merged, so only the time and the name go out: never a note or a description
    /// somebody wrote for themselves.
    /// </summary>
    internal static AudiobookBookmarkDto ForExport(AudiobookBookmarkDto bookmark, bool definitive) =>
        definitive ? bookmark : bookmark with { Note = null, Description = null };

    /// <summary>True when the title's file is a book, whose bookmarks are places in the text rather than times.</summary>
    /// <summary>Global bookmarks exist for movies and TV only (global bookmarks v5 D3); music has no bookmarks at all (D4).</summary>
    internal static bool AllowsGlobal(Title title) => title.Assets.Count > 0 && title.Assets.All(asset => asset.MediaType is "Movies" or "TV");

    internal static bool IsBook(Title title) => title.Assets.Any(asset => asset.MediaType == "Books");

    /// <summary>Refuses a bookmark file that is too large, from its encoded length and before anything is decoded.</summary>
    internal static void RequireFileWithinLimit(string? contentBase64, long maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(contentBase64) || contentBase64.Length > maximumBytes * 4L / 3 + 8)
        {
            throw new BookmarkPackInvalidException("This is not a bookmark file, or it is too large.");
        }
    }

    /// <summary>A book bookmark in the shape the reader's compare code reads.</summary>
    internal static ReaderBookmark ToReaderBookmark(EditorBookmarkRow row) => new()
    {
        Id = row.Id,
        UserId = row.ProfileId.ToString("D"),
        AssetId = row.AssetId,
        ChapterIndex = row.ChapterIndex ?? 0,
        CfiPosition = row.Locator,
        Label = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name,
        IsGlobal = true,
        OwnerName = row.OwnerName,
        CreatedAt = row.CreatedAt.UtcDateTime,
    };

    /// <summary>A title's Global list in the shape the compare and export code reads, for the administrator already checked by the caller.</summary>
    internal async Task<IReadOnlyList<AudiobookBookmarkDto>> EveryoneAsPlayerAsync(Guid workId, Title title, CancellationToken ct)
    {
        var everyone = await repository.GetEveryoneAsync(workId, title.AllowedAssets, SameSpot, ct);
        return [.. everyone.Select(row => ToPlayerBookmark(row, title.ProfileId))];
    }

    /// <summary>Every title's rows and definitive flags in one read, for the overview of a show, season or album.</summary>
    internal async Task<(IReadOnlyList<EditorBookmarkRow> Rows, IReadOnlySet<Guid> Definitive)> ReadManyAsync(
        IReadOnlyCollection<Guid> workIds, Title title, CancellationToken ct) =>
        (await repository.GetByWorksAsync(workIds, title.AllowedAssets, ct), await repository.GetDefinitiveAsync(workIds, ct));

    /// <summary>Global rows of one title out of rows already read.</summary>
    internal IReadOnlyList<EditorBookmarkRow> EveryoneOf(IReadOnlyList<EditorBookmarkRow> rows, bool definitive) =>
        EditorBookmarkRepository.Everyone(rows, definitive, SameSpot);

    private EditorBookmarkRowDto ToRow(EditorBookmarkRow row, Guid profileId, bool inEveryone)
    {
        var decorated = frames.Decorate(new AudiobookBookmarkDto { Id = row.Id, AssetId = row.AssetId, PositionSeconds = row.PositionSeconds });
        return new EditorBookmarkRowDto
        {
            Id = row.Id,
            AssetId = row.AssetId,
            PositionKind = row.PositionKind,
            PositionSeconds = row.PositionSeconds,
            EndSeconds = row.EndSeconds,
            ChapterIndex = row.ChapterIndex,
            ChapterTitle = row.ChapterTitle,
            Name = row.Name,
            Note = row.Note,
            OwnerName = row.OwnerName,
            IsYours = row.ProfileId == profileId,
            FrameUrl = decorated.FrameUrl,
            InEveryone = inEveryone,
            CreatedAt = row.CreatedAt,
        };
    }

    internal static AudiobookBookmarkDto ToPlayerBookmark(EditorBookmarkRow row, Guid profileId) => new()
    {
        Id = row.Id,
        ProfileId = row.ProfileId,
        WorkId = row.WorkId,
        AssetId = row.AssetId,
        ChapterIndex = row.ChapterIndex,
        ChapterTitle = row.ChapterTitle,
        PositionSeconds = row.PositionSeconds,
        DurationSeconds = row.DurationSeconds,
        EndSeconds = row.EndSeconds,
        Label = row.Label,
        Note = row.Note,
        Description = row.Description,
        IsGlobal = true,
        CreatedAt = row.CreatedAt,
        OwnerName = row.ProfileId == profileId ? null : row.OwnerName,
    };

    // One History line per operation, in plain words and with the bookmark's time and name as they were.
    private async Task LogAsync(
        Guid workId, Guid profileId, IReadOnlyList<EditorBookmarkOp> ops, IReadOnlyDictionary<Guid, EditorBookmarkRow> before, CancellationToken ct)
    {
        foreach (var op in ops)
        {
            try
            {
                await activity.LogAsync(new SystemActivityEntry
                {
                    OccurredAt = DateTimeOffset.UtcNow,
                    ActionType = SystemActionType.MetadataManualOverride,
                    EntityId = workId,
                    EntityType = "Work",
                    ProfileId = profileId,
                    Detail = EditorBookmarkOpRules.Describe(op, before),
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The bookmarks are saved; a History line that could not be written must not undo the save.
                logger.LogWarning(ex, "A History line for a bookmark change on {WorkId} could not be written.", workId);
            }
        }
    }
}

/// <summary>The rules of a staged bookmark change that need no data store: the check of an operation list, and how History words each one.</summary>
internal static class EditorBookmarkOpRules
{
    /// <summary>
    /// Plays the operations over the title's bookmarks without writing, so Review can refuse a change before Save does: every bookmark an
    /// operation names must exist, Rename and Change time need the bookmark to be in Global by then, a time must be inside the file.
    /// </summary>
    public static string? Check(
        IReadOnlyList<EditorBookmarkOp> ops,
        IReadOnlyList<EditorBookmarkRow> rows,
        bool definitive,
        Func<string, double> sameSpotFor,
        bool hasImportFile)
    {
        if (ops.Count == 0)
        {
            return EditorBookmarksService.NothingToChange;
        }

        if (ops.Count > EditorBookmarkLimits.MaximumOps)
        {
            return $"A save takes at most {EditorBookmarkLimits.MaximumOps} bookmark changes.";
        }

        var byId = rows.ToDictionary(row => row.Id);
        HashSet<Guid> flagged = definitive ? [.. rows.Where(row => row.IsGlobal).Select(row => row.Id)] : [];
        var isDefinitive = definitive;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void EnsureDefinitive()
        {
            if (isDefinitive)
            {
                return;
            }

            flagged = [];
            isDefinitive = true;
        }

        foreach (var op in ops)
        {
            if (string.IsNullOrWhiteSpace(op.Id) || !seen.Add(op.Id))
            {
                return "Two bookmark changes share the same id.";
            }

            if (!EditorBookmarkOpKinds.IsKnown(op.Kind))
            {
                return "That bookmark change is not known.";
            }

            if (op.Kind == EditorBookmarkOpKinds.StartOver)
            {
                isDefinitive = false;
                flagged = [];
                continue;
            }

            if (op.Kind == EditorBookmarkOpKinds.Import)
            {
                if (!hasImportFile || op.Items.Count == 0 || op.Items.Count > EditorBookmarkLimits.MaximumImportItems)
                {
                    return "There are no bookmarks from a file to add.";
                }

                foreach (var item in op.Items)
                {
                    var isPage = item.PositionKind == BookmarkPooling.PageKind;
                    var isText = !isPage && !string.IsNullOrWhiteSpace(item.Locator);
                    if (!double.IsFinite(item.PositionSeconds) || item.PositionSeconds < 0
                        || isPage && (item.PositionSeconds < 1 || item.PositionSeconds > PageLocator.MaximumPage || item.PositionSeconds != Math.Floor(item.PositionSeconds))
                        || isText && (item.Locator!.Trim().Length > 4096 || ReaderLocation.TryParse(item.Locator.Trim()) is null)
                        || !isText && !isPage && item.EndSeconds is { } end && (!double.IsFinite(end) || end <= item.PositionSeconds)
                        || !AudiobookBookmarkNotePolicy.TryNormalize(item.Name, out _)
                        || !AudiobookBookmarkNotePolicy.TryNormalizeDescription(item.Description, out _))
                    {
                        return "A bookmark in the file is not valid.";
                    }
                }

                EnsureDefinitive();
                continue;
            }

            if (op.BookmarkId is not { } id || !byId.TryGetValue(id, out var row))
            {
                return "A bookmark you changed is no longer there.";
            }

            EnsureDefinitive();
            switch (op.Kind)
            {
                case EditorBookmarkOpKinds.Add:
                    flagged.Add(id);
                    break;
                case EditorBookmarkOpKinds.Remove:
                    flagged.Remove(id);
                    break;
                case EditorBookmarkOpKinds.Rename:
                    if (!flagged.Contains(id))
                    {
                        return "A bookmark you renamed is no longer in Global.";
                    }

                    if (!AudiobookBookmarkNotePolicy.TryNormalize(op.Name, out _))
                    {
                        return $"A bookmark name cannot exceed {AudiobookBookmarkNotePolicy.MaximumLength} characters.";
                    }

                    break;
                case EditorBookmarkOpKinds.ChangeTime:
                    if (!flagged.Contains(id))
                    {
                        return "A bookmark you moved is no longer in Global.";
                    }

                    if (row.PositionKind != BookmarkPooling.TimeKind)
                    {
                        return "Only a bookmark at a time can be moved.";
                    }

                    if (op.PositionSeconds is not { } seconds || !double.IsFinite(seconds) || seconds < 0
                        || row.DurationSeconds is { } length && seconds > length)
                    {
                        return "That time is not inside the file.";
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>The History line of one operation: what happened, to which bookmark, as it was.</summary>
    public static string Describe(EditorBookmarkOp op, IReadOnlyDictionary<Guid, EditorBookmarkRow> before)
    {
        EditorBookmarkRow? row = op.BookmarkId is { } id && before.TryGetValue(id, out var found) ? found : null;
        var which = row is null ? "a bookmark" : Label(row);
        return op.Kind switch
        {
            EditorBookmarkOpKinds.Add => $"Global bookmarks: added to Global: {which}.",
            EditorBookmarkOpKinds.Remove => $"Global bookmarks: removed from Global: {which}.",
            EditorBookmarkOpKinds.Rename => $"Global bookmarks: renamed {which} to {(string.IsNullOrWhiteSpace(op.Name) ? "no name" : op.Name.Trim())}.",
            EditorBookmarkOpKinds.ChangeTime => $"Global bookmarks: moved {which} to {Time(op.PositionSeconds ?? 0)}.",
            EditorBookmarkOpKinds.Import => $"Global bookmarks: imported {op.Items.Count} bookmark{(op.Items.Count == 1 ? string.Empty : "s")} from a file.",
            EditorBookmarkOpKinds.StartOver => "Global bookmarks: cleared.",
            _ => "Global bookmarks changed.",
        };
    }

    private static string Label(EditorBookmarkRow row)
    {
        var place = row.PositionKind == BookmarkPooling.TimeKind ? Time(row.PositionSeconds)
            : row.PositionKind == BookmarkPooling.PageKind ? $"page {(int)row.PositionSeconds}"
            : row.ChapterIndex is { } chapter ? $"chapter {chapter + 1}" : "a place in the book";
        return string.IsNullOrWhiteSpace(row.Name) ? place : $"{place} {row.Name}";
    }

    /// <summary>A time as the tab shows it: 18:42, or 1:03:30 past the hour.</summary>
    public static string Time(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
