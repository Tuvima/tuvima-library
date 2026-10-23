namespace MediaEngine.Domain.Models;

/// <summary>
/// One resumable upload into the Import folder (Library setup spec D44): the file being sent in pieces, where it is being written and
/// where it will land. The piece list is kept apart (<c>IImportUploadRepository.ListPiecesAsync</c>).
/// </summary>
public sealed record ImportUpload(
    Guid Id,
    Guid CreatedBy,
    string KindKey,
    string? LibraryId,
    string TypeLabel,
    string FileName,
    long TotalBytes,
    long PieceBytes,
    int PieceCount,
    string WorkingPath,
    string DestinationFolder,
    string State,
    string? FinalPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastPieceAt,
    DateTimeOffset? ClosedAt)
{
    public const string OpenState = "open";
    public const string CompletedState = "completed";
    public const string CancelledState = "cancelled";

    public bool IsOpen => State == OpenState;

    /// <summary>The size piece <paramref name="index"/> must have: <see cref="PieceBytes"/> for every piece but the last, which holds the rest.</summary>
    public long ExpectedPieceLength(int index) => index == PieceCount - 1
        ? TotalBytes - PieceBytes * (PieceCount - 1)
        : PieceBytes;
}
