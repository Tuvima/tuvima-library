using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;

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
/// </summary>
public sealed class ExtraMoveEditSessionChangeHandler(
    IItemExtraRepository extras,
    IItemExtrasScanner scanner,
    ILibraryItemDeletionRepository titles,
    IIngestionEngine ingestion,
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

    public string Kind => EditSessionChangeKinds.ExtraMove;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
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
}
