namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Where the moves Library makes on its own wait for the next scheduled run (Library setup D62; batched write-back D10 as amended).
/// Filing a newly identified file into its folder's pattern, or out of the Import folder, is not done the moment the file is
/// identified: it is recorded here and done in the next run with the tags, moves first (Write now runs it at once). Until then the file
/// stays exactly where it is and stays playable. A person's own move is not filed through here; it happens straight away.
/// </summary>
public interface IFilingQueue
{
    /// <summary>
    /// Records that the file of <paramref name="assetId"/> is to be filed on the next run. With <paramref name="held"/> the request is
    /// recorded as held (its newly added Managed folder has not passed its first check) and is never moved. Returns false when nothing
    /// was recorded: the asset is unknown, a person's own move is already asked for or running, or the file is already waiting.
    /// </summary>
    Task<bool> RequestFilingAsync(Guid assetId, bool held, CancellationToken ct = default);
}

/// <summary>
/// Says whether automatic filing into a Managed folder is held back (Library setup D61): a newly added Managed folder changes nothing
/// on disk until its first check passes. The Engine answers from the folder's first check (checking or paused means held); the check
/// releases the held requests when it passes or an administrator chooses Keep going. A held request is recorded as held and is never
/// moved by anything else. <see cref="NoFilingHold"/> is the answer where no first check exists.
/// </summary>
public interface IFilingHold
{
    /// <summary>True when files filed into the source <paramref name="sourceId"/> of library <paramref name="libraryId"/> must wait.</summary>
    bool IsHeld(string libraryId, string sourceId, string path);
}

/// <summary>The hold used where no first check exists: nothing is ever held.</summary>
public sealed class NoFilingHold : IFilingHold
{
    public bool IsHeld(string libraryId, string sourceId, string path) => false;
}
