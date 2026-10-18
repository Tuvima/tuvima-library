namespace MediaEngine.Storage;

/// <summary>Host settings for starting a <see cref="DatabaseConnection"/>.</summary>
public sealed class DataStoreStartupOptions
{
    /// <summary>
    /// The folder holding data store backups. Safety copies made before an upgrade go in its
    /// <c>safety-copies</c> subfolder, and upgrade messages name both. Null means no safety copy is made.
    /// </summary>
    public string? BackupDirectory { get; init; }
}
