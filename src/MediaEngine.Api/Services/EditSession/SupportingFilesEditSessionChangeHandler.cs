using MediaEngine.Api.Services.Extras;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Saves the group, role, Belongs to and tags set on several supporting files (universal editor v5 D16, D30) with the rest of the session, so one
/// Save changes title fields and supporting files together or not at all. It writes through <see cref="SupportingFileBulkService"/>, the one
/// place that knows how: all the data changes in one transaction, then the file moves (only in a Managed folder). The check is the preview
/// the amber footer shows, so Review and Save can never disagree. If a later change in the session fails, the undo puts every row back and
/// moves the files back. A file that cannot be moved (in use, name taken) stays where it is with its data saved, as everywhere else.
/// </summary>
public sealed class SupportingFilesEditSessionChangeHandler(SupportingFileBulkService bulk) : IEditSessionChangeHandler
{
    public const string NoFiles = "There are no supporting files to change.";

    public string Kind => EditSessionChangeKinds.SupportingFiles;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (change.SupportingFiles is not { } request)
        {
            return NoFiles;
        }

        var outcome = await bulk.PreviewAsync(change.EntityId, request, ct).ConfigureAwait(false);
        if (outcome.Value is not { } preview)
        {
            return outcome.Message;
        }

        // A staged save is all or nothing: a file that is gone or not this title's makes the whole change invalid, named in Review.
        var unusable = preview.Results.FirstOrDefault(item =>
            item.Status is SupportingFileBulkStatuses.Refused or SupportingFileBulkStatuses.NotFound);
        return unusable?.Reason;
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (change.SupportingFiles is not { } request)
        {
            throw new InvalidOperationException(NoFiles);
        }

        var (outcome, undo) = await bulk.ApplyAsync(change.EntityId, request, ct).ConfigureAwait(false);
        if (outcome.Value is null)
        {
            throw new InvalidOperationException(outcome.Message ?? EditSessionReasons.SaveFailed);
        }

        return undo;
    }
}
