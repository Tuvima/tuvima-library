namespace MediaEngine.Domain.Ingestion;

/// <summary>How a finished file ended.</summary>
public enum IngestionFileOutcome
{
    /// <summary>The file is not finished yet.</summary>
    None = 0,

    /// <summary>The file was added to the Library.</summary>
    Added = 1,

    /// <summary>The file needs a person to look at it.</summary>
    NeedsReview = 2,

    /// <summary>The file failed.</summary>
    Failed = 3,

    /// <summary>The file was skipped (a duplicate, not applicable, or no longer there).</summary>
    Skipped = 4,

    /// <summary>The Engine could not match the file. It stays in the Library and is looked up again later; it is not a Needs Review item.</summary>
    Unmatched = 5,
}
