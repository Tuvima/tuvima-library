using System.Globalization;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Moves the item's physical files (kind <c>file_move</c>): renames and moves each one to fit where the library now places it,
/// through the same re-file the library move and the organiser use. It runs after every data change, one item at a time, and cannot
/// be undone, so a failure leaves the data saved and names the files that did not move (spec D13).
/// <para>
/// The check refuses, with the reason a person reads, an item whose files sit in an As-is folder, a read-only folder, are protected
/// originals, or whose new name is taken. Nothing is ever overwritten. Because files are planned against where the item sits
/// <i>when this runs</i> (after a staged relationship change was saved), the change needs no destination of its own;
/// <see cref="EditSessionChange.Token"/>, when the Dashboard sends one from its move preview, is not required and is ignored.
/// </para>
/// </summary>
public sealed class FileMoveEditSessionChangeHandler(
    ILibraryMoveRepository moveItems,
    IAutoOrganizeService organizer) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.FileMove;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        var assets = await AssetsOfAsync(change.EntityId, ct).ConfigureAwait(false);
        if (assets is null)
        {
            return EditSessionReasons.Gone;
        }

        if (assets.Count == 0)
        {
            return EditSessionReasons.NoFiles;
        }

        var plans = new List<AssetRefilePlan>(assets.Count);
        foreach (var assetId in assets)
        {
            plans.Add(await organizer.PlanRefileAsync(assetId, null, ct).ConfigureAwait(false));
        }

        // Some files can move: the ones that cannot are reported when the move runs. None can: say why, once.
        if (plans.Any(plan => plan.Disposition == AssetRefileDisposition.Moves))
        {
            return null;
        }

        if (plans.All(plan => plan.Disposition == AssetRefileDisposition.AlreadyFits))
        {
            return null;
        }

        return ReasonFor(plans.First(plan => plan.Disposition != AssetRefileDisposition.AlreadyFits), plans.Count > 1);
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        var assets = await AssetsOfAsync(change.EntityId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(EditSessionReasons.Gone);

        var didNotMove = new List<string>();
        foreach (var assetId in assets)
        {
            var plan = await organizer.PlanRefileAsync(assetId, null, ct).ConfigureAwait(false);
            if (plan.Disposition == AssetRefileDisposition.AlreadyFits)
            {
                continue;
            }

            if (plan.Disposition != AssetRefileDisposition.Moves)
            {
                didNotMove.Add(DescribeKept(plan));
                continue;
            }

            var outcome = await organizer.RefileAsync(assetId, ct).ConfigureAwait(false);
            if (outcome.Kind is AssetRefileOutcomeKind.Failed or AssetRefileOutcomeKind.Kept)
            {
                didNotMove.Add($"{FileName(plan.CurrentPath)}: {outcome.Reason ?? "it could not be moved"}");
            }
        }

        if (didNotMove.Count > 0)
        {
            var many = didNotMove.Count.ToString(CultureInfo.InvariantCulture);
            throw new InvalidOperationException(
                $"{many} of {assets.Count.ToString(CultureInfo.InvariantCulture)} file(s) did not move. Your other changes are saved. {string.Join(" ", didNotMove)}");
        }

        // A move on disk cannot be put back like a data change.
        return null;
    }

    private async Task<IReadOnlyList<Guid>?> AssetsOfAsync(Guid entityId, CancellationToken ct)
    {
        var item = (await moveItems.GetItemsAsync([entityId], ct).ConfigureAwait(false)).FirstOrDefault();
        return item?.AssetIds;
    }

    private static string ReasonFor(AssetRefilePlan plan, bool many) => plan.Disposition switch
    {
        AssetRefileDisposition.AsIs => many ? MoveFilePlanner.AsIsNoteMany : MoveFilePlanner.AsIsNote,
        AssetRefileDisposition.ReadOnly => many ? MoveFilePlanner.ReadOnlyNoteMany : MoveFilePlanner.ReadOnlyNote,
        AssetRefileDisposition.Protected => many ? MoveFilePlanner.ProtectedNoteMany : MoveFilePlanner.ProtectedNote,
        AssetRefileDisposition.Collision => many ? MoveFilePlanner.TakenNoteMany : MoveFilePlanner.TakenNote,
        _ => many ? MoveFilePlanner.UnavailableNoteMany : MoveFilePlanner.UnavailableNote,
    };

    private static string DescribeKept(AssetRefilePlan plan) => $"{FileName(plan.CurrentPath)}: {ReasonFor(plan, many: false)}";

    private static string FileName(string path) => string.IsNullOrWhiteSpace(path) ? "A file" : Path.GetFileName(path);
}
