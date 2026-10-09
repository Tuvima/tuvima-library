namespace MediaEngine.Domain.PersonalMedia;

/// <summary>A household's Shared View library. Each household has exactly one.</summary>
public sealed record ViewSharedLibrary(
    Guid LibraryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid HouseholdId = default);

/// <summary>A source owned by the Shared library itself, without a profile or Personal Space.</summary>
public sealed record ViewSharedSource(
    Guid Id,
    Guid LibraryId,
    ViewSourceType SourceType,
    string Name,
    string? SourceKey,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ViewSourceStorageMode StorageMode = ViewSourceStorageMode.Managed,
    string? RelativePath = null,
    string? ExternalPath = null,
    bool IncludeSubdirectories = true,
    bool Enabled = true,
    bool IncludeInTimeline = false);
