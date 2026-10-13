using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Contracts;

/// <summary>Describes what is left untouched in the Import folder because a type has nowhere to file it yet.</summary>
public interface IImportFolderWaitingReader
{
    /// <summary>
    /// One entry per media type that has files waiting in the Import folder and no ready managed folder to file them
    /// into. Empty when nothing is waiting or no Import folder is set.
    /// </summary>
    Task<IReadOnlyList<ImportFolderWaiting>> GetWaitingAsync(CancellationToken ct = default);
}
