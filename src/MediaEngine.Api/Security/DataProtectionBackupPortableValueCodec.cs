using System.Security.Cryptography;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using Microsoft.AspNetCore.DataProtection;

namespace MediaEngine.Api.Security;

/// <summary>
/// Makes the two machine-protected values a backup keeps readable for the backup, and protects them again with this computer's
/// key on restore, so a backup made on one computer works on another. These are the two-step sign-in secret
/// (<c>account_two_step.secret_protected</c>, protected exactly as the sign-in code does, Data Protection purpose
/// <c>Tuvima.TwoStep.Secret.v1</c>) and a webhook's signing secret (<c>application_webhooks.secret_ciphertext</c>, protected exactly as
/// the webhook code does, for that webhook's id under <c>Tuvima.ApplicationWebhooks.v1</c>). Any other column is refused: a value is
/// never passed through unprotected by accident.
/// </summary>
public sealed class DataProtectionBackupPortableValueCodec(
    ITwoStepSecretProtector twoStepProtector,
    IDataProtectionProvider protection) : IBackupPortableValueCodec
{
    private const string TwoStepTable = "account_two_step";
    private const string TwoStepColumn = "secret_protected";
    private const string WebhookTable = "application_webhooks";
    private const string WebhookColumn = "secret_ciphertext";
    private const string WebhookPurpose = "Tuvima.ApplicationWebhooks.v1";

    /// <inheritdoc/>
    public string? Export(string table, string column, string protectedValue, IReadOnlyDictionary<string, BackupValue> row)
    {
        if (IsWebhookSecret(table, column))
        {
            try
            {
                return WebhookProtector(row).Unprotect(protectedValue);
            }
            catch (CryptographicException)
            {
                // The key ring that sealed this secret is gone; the backup keeps it as it is and it works on this computer only.
                return null;
            }
        }

        EnsureTwoStep(table, column);
        return twoStepProtector.Unprotect(protectedValue);
    }

    /// <inheritdoc/>
    public string Import(string table, string column, string portableValue, IReadOnlyDictionary<string, BackupValue> row)
    {
        if (IsWebhookSecret(table, column))
        {
            return WebhookProtector(row).Protect(portableValue);
        }

        EnsureTwoStep(table, column);
        return twoStepProtector.Protect(portableValue);
    }

    private IDataProtector WebhookProtector(IReadOnlyDictionary<string, BackupValue> row)
    {
        if (!row.TryGetValue("id", out var id))
        {
            throw new InvalidOperationException("A webhook secret cannot be handled without the webhook's id.");
        }

        var webhookId = id switch
        {
            BackupValue.Blob blob => GuidSql.FromDb(blob.Value),
            BackupValue.Text text when Guid.TryParse(text.Value, out var parsed) => parsed,
            _ => throw new InvalidOperationException("A webhook secret cannot be handled without the webhook's id."),
        };
        return protection.CreateProtector(WebhookPurpose, webhookId.ToString("D"));
    }

    private static bool IsWebhookSecret(string table, string column) =>
        string.Equals(table, WebhookTable, StringComparison.Ordinal) && string.Equals(column, WebhookColumn, StringComparison.Ordinal);

    private static void EnsureTwoStep(string table, string column)
    {
        if (!string.Equals(table, TwoStepTable, StringComparison.Ordinal) || !string.Equals(column, TwoStepColumn, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"No portable value is defined for {table}.{column}.");
        }
    }
}
