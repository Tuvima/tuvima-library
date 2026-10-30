namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>How one group of files ended up when it was looked at (Photo and video filing spec D4, D5, D6).</summary>
public static class IntakeOutcomes
{
    /// <summary>Moved into the library and indexed.</summary>
    public const string Filed = "filed";

    /// <summary>An exact copy of something already there; not filed twice.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>Refused with a reason and moved to the Rejected area; nothing was added.</summary>
    public const string Rejected = "rejected";

    /// <summary>Left where it is for now (still being copied, no room yet, the folder is offline).</summary>
    public const string Deferred = "deferred";
}

/// <summary>
/// The result for one group of files (the main file and its companions). The file name is the person's own; <see cref="ReasonText"/> is a plain
/// sentence that is safe to show to the person who added the file.
/// </summary>
public sealed record IntakeGroupResult(
    string Outcome,
    string FileName,
    string ContentKind,
    IntakeDestination Destination,
    Guid? LibraryId,
    string? ReasonCode,
    string? ReasonText,
    Guid? ItemId,
    bool ItemAdded,
    int FilesAdded,
    int SourcesAdded,
    bool DuplicateInTrash,
    int FileCount,
    long Bytes);

/// <summary>What one pass over the Import area did, as counts and per-group results.</summary>
public sealed record IntakePassResult(IReadOnlyList<IntakeGroupResult> Groups)
{
    public static IntakePassResult Empty { get; } = new([]);

    public int Filed => Groups.Count(group => group.Outcome == IntakeOutcomes.Filed);

    public int Duplicates => Groups.Count(group => group.Outcome == IntakeOutcomes.Duplicate);

    public int Rejected => Groups.Count(group => group.Outcome == IntakeOutcomes.Rejected);

    public int Deferred => Groups.Count(group => group.Outcome == IntakeOutcomes.Deferred);
}

/// <summary>A request that cannot be accepted now (not enough space, the library folder is offline). Nothing was started.</summary>
public sealed class IntakeRefusedException(string code, string message, int statusCode) : InvalidOperationException(message)
{
    public const string NotEnoughSpaceCode = "not_enough_space";
    public const string DestinationOfflineCode = "destination_offline";

    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;
}
