namespace MediaEngine.Domain.Ingestion;

/// <summary>
/// What a file's intake operation records in its result summary when the file turned out to be one the Library already
/// has (written by the pipeline, read by <see cref="IngestionFileStepMapper"/>).
/// </summary>
public static class IngestionResultSummaries
{
    /// <summary>An exact copy of a file the Library already has.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>Two copies of the same file raced to register; the loser is a duplicate.</summary>
    public const string DuplicateRace = "duplicate_race";

    /// <summary>The Library's own, unchanged file seen again where it already is (for example after organising).</summary>
    public const string SamePathRedetected = "same_path_redetected";

    /// <summary>A tracked file replaced in place with new content; its metadata was re-read and it was identified again.</summary>
    public const string ContentChanged = "content_changed";

    /// <summary>A registered file whose identity job was missing (for example after a restart) and was put back.</summary>
    public const string IdentityRecovered = "identity_recovered";
}
