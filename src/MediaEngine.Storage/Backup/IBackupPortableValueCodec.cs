namespace MediaEngine.Storage.Backup;

/// <summary>
/// Makes a value that is protected with this computer's key readable for a backup, and protects it again on restore, so
/// a backup made on one computer works on another. Only the columns a rule lists as portable
/// (<see cref="BackupTableRule.PortableColumns"/>) are ever passed in. The Engine supplies the implementation.
/// </summary>
public interface IBackupPortableValueCodec
{
    /// <summary>
    /// The readable value, or null when it cannot be read with this computer's key (for example the key ring was
    /// replaced). Throws <see cref="InvalidOperationException"/> for a table and column it does not handle.
    /// <paramref name="row"/> holds the row's values by column name, because some values are protected for the row they
    /// belong to (a webhook secret is protected for that webhook's id).
    /// </summary>
    string? Export(string table, string column, string protectedValue, IReadOnlyDictionary<string, BackupValue> row);

    /// <summary>
    /// The value protected with this computer's key. Throws <see cref="InvalidOperationException"/> for a table and
    /// column it does not handle. <paramref name="row"/> holds the row's values by column name, as for <see cref="Export"/>.
    /// </summary>
    string Import(string table, string column, string portableValue, IReadOnlyDictionary<string, BackupValue> row);
}
