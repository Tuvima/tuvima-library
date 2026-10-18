namespace MediaEngine.Storage;

/// <summary>Host settings for starting a <see cref="DatabaseConnection"/>.</summary>
public sealed class DataStoreStartupOptions
{
    /// <summary>
    /// The folder holding data store backups. Named in upgrade messages so a person knows where an older
    /// backup can be restored from; later also where safety copies are kept. Null means "your backups folder".
    /// </summary>
    public string? BackupDirectory { get; init; }
}
