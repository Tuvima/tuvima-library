using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.FileWrites;

/// <summary>
/// Writes one kind of target (a media asset, later a View item) when the scheduler hands it a batch of claimed rows. A writer always
/// builds what it writes from Library's values at the moment of writing, never from a copy taken when the change was queued.
/// </summary>
public interface IFileWriteTargetWriter
{
    /// <summary>The <c>target_kind</c> of the rows this writer handles (<c>media_asset</c> or <c>view_item</c>).</summary>
    string TargetKind { get; }

    /// <summary>
    /// Called once at the start of a run, before the first batch of this writer's rows, so a writer can start a helper program once for
    /// the whole run (batched write-back D6). Not called for a run in which this writer has nothing to write.
    /// </summary>
    Task BeginRunAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called once when the run has finished with this writer's rows, also when the run was stopped or failed part-way.</summary>
    Task EndRunAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Writes every row and answers with one result per row. A row that cannot be written is reported, never thrown.</summary>
    Task<IReadOnlyList<FileWriteResult>> WriteBatchAsync(IReadOnlyList<MediaFileWriteIntent> intents, CancellationToken ct);
}

/// <summary>
/// How one claimed row ended. <paramref name="ReadAgainFirst"/> means the file was changed by something else after the change was
/// queued: nothing was written, the file's own tags were read back into Library, and the row waits for the next run (the attempt does
/// not count against it).
/// </summary>
public sealed record FileWriteResult(Guid IntentId, WriteBackOutcome Outcome, bool ReadAgainFirst = false);
