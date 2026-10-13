namespace MediaEngine.Ingestion.Models;

/// <summary>
/// Immutable, request-scoped view of the filesystem policy for one configured
/// library source. Configuration adapters should construct this value from the
/// authoritative library/source definition before asking any service to mutate
/// a file.
/// </summary>
public sealed record FileSourceMutationPolicy
{
    public required string LibraryId { get; init; }
    public required string SourceId { get; init; }
    public required string RootPath { get; init; }
    public required FileSourceManagementMode ManagementMode { get; init; }
    public bool IsWritable { get; init; }
    public bool AllowMove { get; init; }
    public bool AllowRename { get; init; }
    public bool AllowMetadataWriteback { get; init; }
    public bool AllowDelete { get; init; }
    public bool AllowAsDestination { get; init; }
}

/// <summary>
/// Files already managed elsewhere are always index-only. A managed source is
/// eligible for mutation only after the remaining policy checks pass.
/// </summary>
public enum FileSourceManagementMode
{
    ExistingLibrary,
    ManagedByTuvima,
}

public enum SourceMutationKind
{
    Move,
    Rename,
    MetadataWriteback,
    Delete,
    UseAsDestination,
}

public sealed record SourceMutationRequest
{
    public required FileSourceMutationPolicy Source { get; init; }
    public required SourceMutationKind Mutation { get; init; }
    public required string Path { get; init; }
}

public sealed record SourceMutationDecision(bool Allowed, string? Reason)
{
    public static SourceMutationDecision Permit() => new(true, null);

    public static SourceMutationDecision Deny(string reason) => new(false, reason);
}
