using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Extras;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Moves a file between a show's Extras and its episodes (kind <c>extra_move</c>, Extras spec WP4). Nothing is renamed or moved on disk.
/// <list type="bullet">
/// <item><b>Extra to episode:</b> records the person's decision with the season and episode they chose, then asks ingestion to look at
/// the file again. Ingestion reads the decision and identifies the file as that episode, so the episode count goes up and the card goes.</item>
/// <item><b>Episode to Extras:</b> records the decision first (so the file is never ingested again), then removes the episode's
/// library records. The file stays where it is.</item>
/// </list>
/// Neither can be put back by an undo (the second removes records), so this change is written after every change that can, and after
/// the others it never needs undoing itself. See <see cref="EditSessionExtraMove"/> for how a change names what moves.
/// <para>
/// It also writes "What is this file?" (universal editor v4 D20): a file the Engine could not place, or any single file, becomes an Extra
/// of its own title with a kind and place, becomes an Extra of another title, or is ignored. An ignored file is added to the excluded-files
/// list with the reason "Ignored by you" (so scans skip it and Settings can Add it back); the file itself is never touched.
/// </para>
/// </summary>
public sealed class ExtraMoveEditSessionChangeHandler(
    IItemExtraRepository extras,
    IItemExtrasScanner scanner,
    ILibraryItemDeletionRepository titles,
    IIngestionEngine ingestion,
    ILibraryFolderResolver folders,
    IExcludedSourceFileRepository excludedFiles,
    IExcludedSourceFileFilter excludedFileFilter,
    EditSessionCallerAccessor callers,
    ILogger<ExtraMoveEditSessionChangeHandler> logger) : IEditSessionChangeHandler
{
    public const string ExtraGone = "That extra is no longer there.";
    public const string OnlyClips = "Only a video clip can become an episode.";
    public const string FileMissing = "The file is no longer where Library found it.";
    public const string BadNumbers = "Pick a season and an episode number. Episodes start at 1.";
    public const string NotThisShow = "That extra does not belong to this show.";
    public const string NotAShowFile = "This file is not in a show's folder, so it cannot be an Extra.";
    public const string OneFileOnly = "Only an episode with a single video file can move to Extras.";
    public const string NoWhere = "Choose to move it to an episode or to Extras.";
    public const string BadChoice = "Choose what this file is again.";
    public const string OneFileToSort = "Only an item with a single file can be sorted this way.";
    public const string NotAnExtraFile = "Only a document, pictures or a video or audio clip can be an Extra.";
    public const string KindDoesNotFit = "That kind does not fit this file.";
    public const string NotInATitleFolder = "This file is not inside a title's folder, so it cannot be an Extra of its own title. Attach it to another title instead.";
    public const string TitleGone = "That title is no longer in your library, or has no folder to keep an Extra in. Pick it again.";
    public const string NotInALibraryFolder = "This file is not in a library folder, so it cannot be ignored.";

    public string Kind => EditSessionChangeKinds.ExtraMove;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (EditSessionExtraMove.IsFileChoice(change.Token))
        {
            return (await PlanFileAsync(change, ct).ConfigureAwait(false)).Reason;
        }

        if (EditSessionExtraMove.IsToExtras(change.Token))
        {
            return (await PlanToExtrasAsync(change.EntityId, ct).ConfigureAwait(false)).Reason;
        }

        if (EditSessionExtraMove.TryParseEpisode(change.Token, out _, out _))
        {
            return (await PlanToEpisodeAsync(change, ct).ConfigureAwait(false)).Reason;
        }

        return change.Token is { Length: > 0 } && change.Token.StartsWith("episode:", StringComparison.Ordinal) ? BadNumbers : NoWhere;
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (EditSessionExtraMove.IsFileChoice(change.Token))
        {
            await ApplyFileAsync(change, ct).ConfigureAwait(false);
            return null;
        }

        if (EditSessionExtraMove.IsToExtras(change.Token))
        {
            await ApplyToExtrasAsync(change.EntityId, ct).ConfigureAwait(false);
        }
        else
        {
            await ApplyToEpisodeAsync(change, ct).ConfigureAwait(false);
        }

        // A moved file cannot be put back by an undo: the decision stands, and the episode records are gone or rebuilt by ingestion.
        return null;
    }

    // ----- "What is this file?" (D20) ----------------------------------------------------------------------------------------

    private async Task ApplyFileAsync(EditSessionChange change, CancellationToken ct)
    {
        var plan = await PlanFileAsync(change, ct).ConfigureAwait(false);
        if (plan.Reason is not null || plan.Choice is not { } choice || plan.Work is not { } work)
        {
            throw new InvalidOperationException(plan.Reason ?? EditSessionReasons.Gone);
        }

        if (choice.Action == EditSessionExtraMove.ActionIgnore)
        {
            var place = plan.Excluded ?? throw new InvalidOperationException(NotInALibraryFolder);
            // The exclusion first, so no scan takes the file back in while its records are being removed. The file stays on disk.
            await excludedFiles.AddOrReplaceAsync(place with { ExcludedByName = callers.Current?.Actor.DisplayName, ExcludedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
            excludedFileFilter.Invalidate();
            await extras.RemoveByPathAsync(work.Assets[0].FilePath, ct).ConfigureAwait(false);
        }
        else
        {
            var described = plan.Extra ?? throw new InvalidOperationException(NotInATitleFolder);
            // The decision first: from now on ingestion leaves the file alone. Then the item's records go; the file stays on disk.
            await extras.SetDecisionAsync(described, ct).ConfigureAwait(false);
        }

        await titles.CancelProcessingAsync([work.WorkId], ct).ConfigureAwait(false);
        await titles.DeleteWorksAsync([work.WorkId], keepPersonalState: false, ct).ConfigureAwait(false);
        if (plan.Extra is { } filed)
        {
            await scanner.SyncItemFolderAsync(filed.ItemFolder, ct).ConfigureAwait(false);
        }
    }

    private async Task<FilePlan> PlanFileAsync(EditSessionChange change, CancellationToken ct)
    {
        if (!EditSessionExtraMove.TryParseFile(change.Token, out var choice) || choice is null)
        {
            return FilePlan.Refuse(BadChoice);
        }

        var work = (await titles.GetWorksAsync([change.EntityId], ct).ConfigureAwait(false)).FirstOrDefault();
        if (work is null)
        {
            return FilePlan.Refuse(EditSessionReasons.Gone);
        }

        if (work.Assets.Count != 1)
        {
            return FilePlan.Refuse(OneFileToSort);
        }

        var asset = work.Assets[0];
        if (!File.Exists(asset.FilePath))
        {
            return FilePlan.Refuse(FileMissing);
        }

        if (choice.Action == EditSessionExtraMove.ActionIgnore)
        {
            var resolved = folders.ResolveSourceForPath(asset.FilePath);
            var relative = resolved is null
                ? null
                : ExcludedSourceFilePaths.RelativePathOf(Path.GetFullPath(resolved.Source.Path), Path.GetFullPath(asset.FilePath));
            if (resolved is null || relative is null)
            {
                return FilePlan.Refuse(NotInALibraryFolder);
            }

            var info = new FileInfo(asset.FilePath);
            var place = new ExcludedSourceFile(
                Guid.NewGuid(), resolved.Library.Id, resolved.Source.Id, relative,
                string.IsNullOrWhiteSpace(asset.ContentHash) ? null : asset.ContentHash,
                info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                work.Title, DateTimeOffset.UtcNow, null, null, ExcludedSourceFileReasons.IgnoredByYou);
            return new FilePlan(null, choice, work, null, place);
        }

        var extension = Path.GetExtension(asset.FilePath);
        if (!ExtrasRule.IsExtraKindExtension(extension))
        {
            return FilePlan.Refuse(NotAnExtraFile);
        }

        if (!ExtraKinds.Fits(ExtrasRule.KindOf(extension), choice.ExtraKind))
        {
            return FilePlan.Refuse(KindDoesNotFit);
        }

        ExtraTitleFolder? target = null;
        if (choice.Action == EditSessionExtraMove.ActionAttach)
        {
            target = await FindTitleFolderAsync(choice.TitleId ?? Guid.Empty, ct).ConfigureAwait(false);
            if (target is null)
            {
                return FilePlan.Refuse(TitleGone);
            }
        }

        var decision = new ExtraDecision(choice.ExtraKind, choice.Season, choice.Episode, choice.Track, target);
        var described = await scanner.DescribeAsync(asset.FilePath, ItemExtraOrigins.UserExtra, decision, ct).ConfigureAwait(false);
        return described is null
            ? FilePlan.Refuse(target is null ? NotInATitleFolder : TitleGone)
            : new FilePlan(null, choice, work, described, null);
    }

    // The folder an Extra of another title is filed under: found from one of that title's own files, whether the id names a single item
    // or a show, season, album or series.
    private async Task<ExtraTitleFolder?> FindTitleFolderAsync(Guid titleId, CancellationToken ct)
    {
        if (titleId == Guid.Empty)
        {
            return null;
        }

        var group = await titles.ResolveGroupAsync(titleId, ct).ConfigureAwait(false);
        IReadOnlyList<Guid> ids = group is { OwnedWorkIds.Count: > 0 } ? [.. group.OwnedWorkIds.Take(5)] : [titleId];
        foreach (var work in await titles.GetWorksAsync(ids, ct).ConfigureAwait(false))
        {
            foreach (var asset in work.Assets.Where(asset => File.Exists(asset.FilePath)))
            {
                if (await scanner.ResolveTitleFolderAsync(asset.FilePath, ct).ConfigureAwait(false) is { } folder)
                {
                    return folder;
                }
            }
        }

        return null;
    }

    private async Task ApplyToEpisodeAsync(EditSessionChange change, CancellationToken ct)
    {
        var plan = await PlanToEpisodeAsync(change, ct).ConfigureAwait(false);
        var described = plan.Reason is null && plan.Extra is { } extra
            ? extra
            : throw new InvalidOperationException(plan.Reason ?? ExtraGone);
        EditSessionExtraMove.TryParseEpisode(change.Token, out var season, out var episode);

        await extras.SetDecisionAsync(
            described with { Origin = ItemExtraOrigins.UserEpisode, TargetSeason = season, TargetEpisode = episode },
            ct).ConfigureAwait(false);

        try
        {
            // Ingestion reads the decision and identifies the file as that episode. If this scan cannot start the file is still picked
            // up by the next folder sweep, so the move is saved either way.
            await ingestion.ScanDirectory(Path.GetDirectoryName(described.FilePath)!, includeSubdirectories: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The folder of an Extra moved to an episode could not be queued for ingestion; the next sweep will pick it up.");
        }
    }

    private async Task ApplyToExtrasAsync(Guid episodeId, CancellationToken ct)
    {
        var plan = await PlanToExtrasAsync(episodeId, ct).ConfigureAwait(false);
        var described = plan.Reason is null && plan.Extra is { } extra
            ? extra
            : throw new InvalidOperationException(plan.Reason ?? EditSessionReasons.Gone);

        // The decision first: from now on ingestion leaves the file alone. Then the episode's records go; the file stays on disk.
        await extras.SetDecisionAsync(described, ct).ConfigureAwait(false);
        await titles.CancelProcessingAsync([episodeId], ct).ConfigureAwait(false);
        await titles.DeleteWorksAsync([episodeId], keepPersonalState: false, ct).ConfigureAwait(false);
        await scanner.SyncItemFolderAsync(described.ItemFolder, ct).ConfigureAwait(false);
    }

    private async Task<Plan> PlanToEpisodeAsync(EditSessionChange change, CancellationToken ct)
    {
        if (!EditSessionExtraMove.TryParseEpisode(change.Token, out _, out _))
        {
            return new Plan(null, BadNumbers);
        }

        if (!Guid.TryParse(change.TargetId, out var extraId) || await extras.GetAsync(extraId, ct).ConfigureAwait(false) is not { } extra)
        {
            return new Plan(null, ExtraGone);
        }

        if (extra.Kind != ItemExtraKinds.Clip)
        {
            return new Plan(null, OnlyClips);
        }

        if (!File.Exists(extra.FilePath))
        {
            return new Plan(null, FileMissing);
        }

        if (!await BelongsToTitleAsync(change.EntityId, extra.ItemFolder, ct).ConfigureAwait(false))
        {
            return new Plan(null, NotThisShow);
        }

        return new Plan(extra, null);
    }

    private async Task<Plan> PlanToExtrasAsync(Guid episodeId, CancellationToken ct)
    {
        var works = await titles.GetWorksAsync([episodeId], ct).ConfigureAwait(false);
        if (works.FirstOrDefault() is not { } work)
        {
            return new Plan(null, EditSessionReasons.Gone);
        }

        if (work.Assets.Count != 1)
        {
            return new Plan(null, OneFileOnly);
        }

        var path = work.Assets[0].FilePath;
        if (!File.Exists(path))
        {
            return new Plan(null, FileMissing);
        }

        var described = await scanner.DescribeAsync(path, ItemExtraOrigins.UserExtra, null, null, ct).ConfigureAwait(false);
        return described is { Kind: ItemExtraKinds.Clip } ? new Plan(described, null) : new Plan(null, NotAShowFile);
    }

    // The show being edited must be the one whose folder holds the Extra, so an administrator who may edit one show cannot move another's.
    private async Task<bool> BelongsToTitleAsync(Guid titleId, string itemFolder, CancellationToken ct)
    {
        var group = await titles.ResolveGroupAsync(titleId, ct).ConfigureAwait(false);
        IReadOnlyList<Guid> ids = group is { OwnedWorkIds.Count: > 0 } ? [.. group.OwnedWorkIds.Take(5)] : [titleId];
        var folder = Path.TrimEndingDirectorySeparator(itemFolder);
        foreach (var work in await titles.GetWorksAsync(ids, ct).ConfigureAwait(false))
        {
            if (work.Assets.Any(asset => IsUnder(folder, asset.FilePath)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnder(string folder, string path) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed record Plan(ItemExtra? Extra, string? Reason);

    private sealed record FilePlan(string? Reason, ExtraFileChoice? Choice, LibraryItemDeletionWork? Work, ItemExtra? Extra, ExcludedSourceFile? Excluded)
    {
        public static FilePlan Refuse(string reason) => new(reason, null, null, null, null);
    }
}
