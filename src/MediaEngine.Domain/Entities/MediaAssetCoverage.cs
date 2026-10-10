namespace MediaEngine.Domain.Entities;

/// <summary>
/// One episode (Work) covered by a physical file (Media Asset). A file that holds
/// several episodes stays attached to its first episode; this row lists every
/// episode it covers, in file order. Single-episode files have no rows.
/// </summary>
public sealed record MediaAssetCoverage(
    Guid AssetId,
    Guid WorkId,
    int Position,
    double? StartSeconds,
    double? EndSeconds,
    string Source)
{
    public const string SourceFilename = "filename";
    public const string SourceChapters = "chapters";
    public const string SourceManual = "manual";
    public const string SourceProviderRuntime = "provider_runtime";

    public static bool IsKnownSource(string? source) =>
        source is SourceFilename or SourceChapters or SourceManual or SourceProviderRuntime;
}
