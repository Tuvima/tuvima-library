namespace MediaEngine.Contracts.Metadata;

public sealed record MusicTrackMovePreviewRequest(Guid AssetId, string ReleaseId);
public sealed record MusicTrackMoveChoice(string ReleaseTrackId, string? RecordingId,
    string Title, int DiscNumber, int TrackNumber);
public sealed record MusicTrackMovePreview(string ReviewToken, DateTimeOffset ExpiresAt,
    Guid AssetId, string ReleaseId, string Album, string? Artist, IReadOnlyList<MusicTrackMoveChoice> Tracks);
public sealed record MusicTrackMoveSaveRequest(string ReviewToken, string ReleaseTrackId, Guid OperationId);
