namespace MediaEngine.Domain.Ingestion;

/// <summary>
/// The one ordered list of steps every file passes through on its way into the Library. The live ingestion bar is the
/// sum of each file's step number divided by the number of steps possible, so it never changes units.
/// A new pipeline step is added here and nowhere else; <see cref="IngestionFileStepMapper"/> decides how the pipeline's
/// own stages map onto these steps.
/// </summary>
public enum IngestionFileStep
{
    /// <summary>The file has been noticed.</summary>
    Found = 1,

    /// <summary>The file has settled (it is no longer being copied or locked) and is being hashed and read.</summary>
    Settled = 2,

    /// <summary>The file has been read and its details are being scored.</summary>
    Read = 3,

    /// <summary>The file's details have been scored.</summary>
    Scored = 4,

    /// <summary>The file is registered in the Library.</summary>
    Registered = 5,

    /// <summary>The retail match is being looked for.</summary>
    Identifying = 6,

    /// <summary>The match is settled and the title is being filled in.</summary>
    Hydrating = 7,

    /// <summary>The wider universe around the title is being filled in.</summary>
    Enriching = 8,

    /// <summary>The file is finished, one way or another.</summary>
    Done = 9,
}
