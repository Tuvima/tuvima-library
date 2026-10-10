namespace MediaEngine.Contracts.Metadata;

/// <summary>One episode of the file's season that the "This file covers" picker can show.</summary>
public sealed record MediaEditorFileCoverageEpisodeDto(
    Guid WorkId,
    int? EpisodeNumber,
    string Title,
    bool IsHost,
    bool IsCovered,
    bool OwnedByOtherFile);

/// <summary>The episodes a file covers, plus the season's other episodes it could cover.</summary>
public sealed record MediaEditorFileCoverageDto(
    Guid AssetId,
    Guid HostWorkId,
    int MaxEpisodes,
    IReadOnlyList<MediaEditorFileCoverageEpisodeDto> Episodes);

/// <summary>Replaces the full list of episodes one file covers (the host episode must be included).</summary>
public sealed record MediaEditorFileCoverageSaveRequestDto(
    Guid OperationId,
    Guid AssetId,
    IReadOnlyList<Guid> WorkIds);
