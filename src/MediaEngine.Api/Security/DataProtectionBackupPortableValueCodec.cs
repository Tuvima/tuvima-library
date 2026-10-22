using MediaEngine.Identity.Contracts;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.Security;

/// <summary>
/// Makes the one machine-protected value a backup keeps (the two-step sign-in secret) readable for the backup, and
/// protects it again with this computer's key on restore, so a backup made on one computer lets the person sign in on
/// another. It uses the same protector, and so the same Data Protection purpose (<c>Tuvima.TwoStep.Secret.v1</c>), as
/// the sign-in code. Any other column is refused: a value is never passed through unprotected by accident.
/// </summary>
public sealed class DataProtectionBackupPortableValueCodec(ITwoStepSecretProtector protector) : IBackupPortableValueCodec
{
    private const string Table = "account_two_step";
    private const string Column = "secret_protected";

    /// <inheritdoc/>
    public string? Export(string table, string column, string protectedValue)
    {
        EnsureHandled(table, column);
        return protector.Unprotect(protectedValue);
    }

    /// <inheritdoc/>
    public string Import(string table, string column, string portableValue)
    {
        EnsureHandled(table, column);
        return protector.Protect(portableValue);
    }

    private static void EnsureHandled(string table, string column)
    {
        if (!string.Equals(table, Table, StringComparison.Ordinal) || !string.Equals(column, Column, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"No portable value is defined for {table}.{column}.");
        }
    }
}
