namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Tells the scans and the folder watcher which files on disk to leave alone: the ones that stayed in a source folder when their
/// title was deleted from the library (<c>excluded_source_files</c>). It is asked once for every file a scan finds, so a file that
/// has not changed is answered from memory without reading it.
/// </summary>
public interface IExcludedSourceFileFilter
{
    /// <summary>
    /// True when <paramref name="fullPath"/> is an excluded file that has not been replaced, so it must not be ingested. A different
    /// file now sitting at an excluded place lifts the exclusion and gets false, so it is ingested like any new file. A path outside
    /// every library source, one that is not excluded, and one that no longer exists all get false.
    /// </summary>
    Task<bool> ShouldSkipAsync(string fullPath, CancellationToken ct = default);

    /// <summary>Forgets the remembered exclusions so the next question reads them again. Call it after exclusions are added or removed.</summary>
    void Invalidate();
}
