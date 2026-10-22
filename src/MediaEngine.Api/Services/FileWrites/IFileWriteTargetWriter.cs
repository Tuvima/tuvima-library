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

    /// <summary>Writes every row and answers with one result per row. A row that cannot be written is reported, never thrown.</summary>
    Task<IReadOnlyList<FileWriteResult>> WriteBatchAsync(IReadOnlyList<MediaFileWriteIntent> intents, CancellationToken ct);
}

/// <summary>How one claimed row ended.</summary>
public sealed record FileWriteResult(Guid IntentId, WriteBackOutcome Outcome);
