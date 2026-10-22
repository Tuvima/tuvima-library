using MediaEngine.Api.Security;
using Microsoft.AspNetCore.DataProtection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The codec that makes the two-step sign-in secret readable for a backup, checked against the real protector that
/// seals it in the data store.
/// </summary>
public sealed class DataProtectionBackupPortableValueCodecTests
{
    private const string Secret = "JBSWY3DPEHPK3PXP";

    [Fact]
    public void Export_GivesBackTheSecretTheRealProtectorSealed_AndImportSealsItAgain()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new DataProtectionTwoStepSecretProtector(provider);
        var codec = new DataProtectionBackupPortableValueCodec(protector);
        var sealedSecret = protector.Protect(Secret);

        var readable = codec.Export("account_two_step", "secret_protected", sealedSecret);
        var sealedAgain = codec.Import("account_two_step", "secret_protected", readable!);

        Assert.Equal(Secret, readable);
        Assert.NotEqual(Secret, sealedAgain);
        Assert.Equal(Secret, protector.Unprotect(sealedAgain));
    }

    [Fact]
    public void Import_OnAnotherComputer_SealsTheSecretWithThatComputersKey()
    {
        var oldComputer = new DataProtectionTwoStepSecretProtector(new EphemeralDataProtectionProvider());
        var newComputer = new DataProtectionTwoStepSecretProtector(new EphemeralDataProtectionProvider());
        var readable = new DataProtectionBackupPortableValueCodec(oldComputer)
            .Export("account_two_step", "secret_protected", oldComputer.Protect(Secret));

        var sealedOnNew = new DataProtectionBackupPortableValueCodec(newComputer).Import("account_two_step", "secret_protected", readable!);

        Assert.Equal(Secret, newComputer.Unprotect(sealedOnNew));
        Assert.Null(oldComputer.Unprotect(sealedOnNew));
    }

    [Fact]
    public void Export_ReturnsNull_WhenTheKeyThatSealedTheSecretIsGone()
    {
        var sealedSecret = new DataProtectionTwoStepSecretProtector(new EphemeralDataProtectionProvider()).Protect(Secret);
        var codec = new DataProtectionBackupPortableValueCodec(new DataProtectionTwoStepSecretProtector(new EphemeralDataProtectionProvider()));

        Assert.Null(codec.Export("account_two_step", "secret_protected", sealedSecret));
    }

    [Theory]
    [InlineData("account_two_step", "created_at")]
    [InlineData("account_credentials", "secret_protected")]
    [InlineData("accounts", "email")]
    public void ExportAndImport_RefuseAnyOtherColumn_RatherThanPassTheValueThrough(string table, string column)
    {
        var codec = new DataProtectionBackupPortableValueCodec(new DataProtectionTwoStepSecretProtector(new EphemeralDataProtectionProvider()));

        Assert.Throws<InvalidOperationException>(() => codec.Export(table, column, "value"));
        Assert.Throws<InvalidOperationException>(() => codec.Import(table, column, "value"));
    }
}
