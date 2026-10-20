using MediaEngine.Ingestion.Contracts;

namespace MediaEngine.Api.Services.Settings;

/// <summary>
/// Puts the optional import folder to work after the folder settings are saved: creates its seven per-type folders and asks the
/// engine to start watching it and file what is already inside. Removing the folder deletes nothing; everything stays on disk.
/// </summary>
public sealed class ImportFolderApplier
{
    private readonly IImportFolderService _importFolders;
    private readonly IIngestionEngine _engine;

    public ImportFolderApplier(IImportFolderService importFolders, IIngestionEngine engine)
    {
        ArgumentNullException.ThrowIfNull(importFolders);
        ArgumentNullException.ThrowIfNull(engine);
        _importFolders = importFolders;
        _engine = engine;
    }

    /// <summary>Creates the per-type folders inside the import folder (and the folder itself) when it is set.</summary>
    public void CreateFolders() => _importFolders.EnsureSubfolders(createImportFolder: true);

    /// <summary>Creates the per-type folders, then has the engine watch the import folder and look at what is in it.</summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        CreateFolders();
        await _engine.RefreshImportFolderAsync(ct).ConfigureAwait(false);
    }
}
