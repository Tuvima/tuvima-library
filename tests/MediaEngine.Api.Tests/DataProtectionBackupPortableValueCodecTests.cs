using MediaEngine.Api.Security;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using Microsoft.AspNetCore.DataProtection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The codec that makes the two machine-protected values a backup keeps (the two-step sign-in secret and a webhook's signing secret)
/// readable for a backup, checked against the real protectors that seal them in the data store.
/// </summary>
public sealed class DataProtectionBackupPortableValueCodecTests
{
    private const string Secret = "JBSWY3DPEHPK3PXP";
    private static readonly IReadOnlyDictionary<string, BackupValue> NoRow = new Dictionary<string, BackupValue>();

    [Fact]
    public void Export_GivesBackTheSecretTheRealProtectorSealed_AndImportSealsItAgain()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new DataProtectionTwoStepSecretProtector(provider);
        var codec = new DataProtectionBackupPortableValueCodec(protector, provider);
        var sealedSecret = protector.Protect(Secret);

        var readable = codec.Export("account_two_step", "secret_protected", sealedSecret, NoRow);
        var sealedAgain = codec.Import("account_two_step", "secret_protected", readable!, NoRow);

        Assert.Equal(Secret, readable);
        Assert.NotEqual(Secret, sealedAgain);
        Assert.Equal(Secret, protector.Unprotect(sealedAgain));
    }

    [Fact]
    public void Import_OnAnotherComputer_SealsTheSecretWithThatComputersKey()
    {
        var oldProvider = new EphemeralDataProtectionProvider();
        var newProvider = new EphemeralDataProtectionProvider();
        var oldComputer = new DataProtectionTwoStepSecretProtector(oldProvider);
        var newComputer = new DataProtectionTwoStepSecretProtector(newProvider);
        var readable = new DataProtectionBackupPortableValueCodec(oldComputer, oldProvider)
            .Export("account_two_step", "secret_protected", oldComputer.Protect(Secret), NoRow);

        var sealedOnNew = new DataProtectionBackupPortableValueCodec(newComputer, newProvider).Import("account_two_step", "secret_protected", readable!, NoRow);

        Assert.Equal(Secret, newComputer.Unprotect(sealedOnNew));
        Assert.Null(oldComputer.Unprotect(sealedOnNew));
    }

    [Fact]
    public void Export_ReturnsNull_WhenTheKeyThatSealedTheSecretIsGone()
    {
        var sealedSecret = new DataProtectionTwoStepSecretProtector(new EphemeralDataProtectionProvider()).Protect(Secret);
        var otherProvider = new EphemeralDataProtectionProvider();
        var codec = new DataProtectionBackupPortableValueCodec(new DataProtectionTwoStepSecretProtector(otherProvider), otherProvider);

        Assert.Null(codec.Export("account_two_step", "secret_protected", sealedSecret, NoRow));
    }

    [Fact]
    public void WebhookSecret_RoundTripsThroughTheProtectorTheWebhookCodeUses_ForThatWebhooksId()
    {
        var provider = new EphemeralDataProtectionProvider();
        var codec = new DataProtectionBackupPortableValueCodec(new DataProtectionTwoStepSecretProtector(provider), provider);
        var webhook = Guid.NewGuid();
        var row = RowOf(webhook);
        // Exactly how ApplicationWebhookService seals a webhook's secret.
        var sealedByTheWebhookCode = provider.CreateProtector("Tuvima.ApplicationWebhooks.v1", webhook.ToString("D")).Protect("whsec_abc123");

        var readable = codec.Export("application_webhooks", "secret_ciphertext", sealedByTheWebhookCode, row);
        var sealedAgain = codec.Import("application_webhooks", "secret_ciphertext", readable!, row);

        Assert.Equal("whsec_abc123", readable);
        Assert.Equal("whsec_abc123", provider.CreateProtector("Tuvima.ApplicationWebhooks.v1", webhook.ToString("D")).Unprotect(sealedAgain));
    }

    [Fact]
    public void WebhookSecret_SealedAgainOnAnotherComputer_ReadsWithThatComputersKey()
    {
        var oldProvider = new EphemeralDataProtectionProvider();
        var newProvider = new EphemeralDataProtectionProvider();
        var webhook = Guid.NewGuid();
        var row = RowOf(webhook);
        var readable = new DataProtectionBackupPortableValueCodec(new DataProtectionTwoStepSecretProtector(oldProvider), oldProvider).Export(
            "application_webhooks",
            "secret_ciphertext",
            oldProvider.CreateProtector("Tuvima.ApplicationWebhooks.v1", webhook.ToString("D")).Protect("whsec_abc123"),
            row);

        var sealedOnNew = new DataProtectionBackupPortableValueCodec(new DataProtectionTwoStepSecretProtector(newProvider), newProvider)
            .Import("application_webhooks", "secret_ciphertext", readable!, row);

        Assert.Equal("whsec_abc123", newProvider.CreateProtector("Tuvima.ApplicationWebhooks.v1", webhook.ToString("D")).Unprotect(sealedOnNew));
    }

    [Fact]
    public void WebhookSecret_IsNotReadableForAnotherWebhooksId_AndNeedsTheRowsId()
    {
        var provider = new EphemeralDataProtectionProvider();
        var codec = new DataProtectionBackupPortableValueCodec(new DataProtectionTwoStepSecretProtector(provider), provider);
        var sealedSecret = provider.CreateProtector("Tuvima.ApplicationWebhooks.v1", Guid.NewGuid().ToString("D")).Protect("whsec_abc123");

        Assert.Null(codec.Export("application_webhooks", "secret_ciphertext", sealedSecret, RowOf(Guid.NewGuid())));
        Assert.Throws<InvalidOperationException>(() => codec.Export("application_webhooks", "secret_ciphertext", sealedSecret, NoRow));
        Assert.Throws<InvalidOperationException>(() => codec.Import("application_webhooks", "secret_ciphertext", "whsec_abc123", NoRow));
    }

    [Theory]
    [InlineData("account_two_step", "created_at")]
    [InlineData("account_credentials", "secret_protected")]
    [InlineData("accounts", "email")]
    [InlineData("application_webhooks", "url")]
    [InlineData("application_webhooks", "secret_protected")]
    [InlineData("account_two_step", "secret_ciphertext")]
    public void ExportAndImport_RefuseAnyOtherColumn_RatherThanPassTheValueThrough(string table, string column)
    {
        var provider = new EphemeralDataProtectionProvider();
        var codec = new DataProtectionBackupPortableValueCodec(new DataProtectionTwoStepSecretProtector(provider), provider);

        Assert.Throws<InvalidOperationException>(() => codec.Export(table, column, "value", RowOf(Guid.NewGuid())));
        Assert.Throws<InvalidOperationException>(() => codec.Import(table, column, "value", RowOf(Guid.NewGuid())));
    }

    [Fact]
    public void TheRegistry_ListsTheWebhookSecretAsPortable_NotExcluded()
    {
        var rule = BackupTableClassification.Find("application_webhooks")!;

        Assert.Equal(["secret_ciphertext"], rule.PortableColumns);
        Assert.DoesNotContain("secret_ciphertext", rule.ExcludedColumns);
    }

    private static IReadOnlyDictionary<string, BackupValue> RowOf(Guid id) =>
        new Dictionary<string, BackupValue> { ["id"] = BackupValue.FromBlob(GuidSql.ToBlob(id)) };
}
