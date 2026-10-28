using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Puts the files of the items an edit session changed on the batched write list (universal editor spec D15). The library's own
/// "Save changes into the files" setting and the write-back switches still decide whether a file is really written: this only asks, and
/// the write list ignores the ask when write-back is off. A session that says it should not update the files asks for nothing.
/// </summary>
public interface IEditSessionFileWrites
{
    /// <summary>Queues the files for every item with a saved data change. Never throws: the save is already in the library.</summary>
    Task QueueAsync(IReadOnlyCollection<Guid> entityIds, CancellationToken ct = default);
}

/// <summary><see cref="IEditSessionFileWrites"/> over the shared file write list, using the same trigger a manual override uses.</summary>
public sealed class EditSessionFileWrites(
    IFileWriteQueue fileWrites,
    IMediaAssetRepository assets,
    ILogger<EditSessionFileWrites> logger) : IEditSessionFileWrites
{
    /// <summary>The write list's trigger for a change a person made by hand.</summary>
    public const string Trigger = "manual_override";

    public async Task QueueAsync(IReadOnlyCollection<Guid> entityIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        foreach (var entityId in entityIds.Distinct())
        {
            try
            {
                // A work's file is its first media asset; an id that is already a media file (or not one at all) is passed as it is,
                // and the write list ignores what is not a media file.
                var asset = await assets.FindFirstByWorkIdAsync(entityId, ct).ConfigureAwait(false);
                await fileWrites.EnqueueAssetAsync(asset?.Id ?? entityId, Trigger, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The file of item {EntityId} could not be put on the write list after an edit session.", entityId);
            }
        }
    }
}

/// <summary>Which saved changes of a session leave a file worth writing.</summary>
internal static class EditSessionFileWriteRules
{
    /// <summary>Details, identity and artwork changes change what a file would say; a move or a relationship change is not a tag change.</summary>
    public static bool ChangesFileContent(EditSessionChange change) =>
        change.Kind is EditSessionChangeKinds.Details or EditSessionChangeKinds.Identity or EditSessionChangeKinds.Artwork;
}
