using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

/// <summary>
/// The text guard that keeps a Vault file's place out of logs and activity notes (Videos spec D17, D33). The folder list is process
/// wide, so these cases share one class (xunit runs the cases of a class one after another) and use folders no real test writes.
/// </summary>
public sealed class VaultLocationGuardTests : IDisposable
{
    private static readonly string Folder = "/mnt/private-" + Guid.NewGuid().ToString("N") + "/Clips";

    public VaultLocationGuardTests() => VaultLocationGuard.Replace([Folder]);

    public void Dispose() => VaultLocationGuard.Replace([]);

    [Theory]
    [InlineData("{0}")]
    [InlineData("{0}/summer/one.mkv")]
    [InlineData("Settled \"{0}/summer/one.mkv\" after 3 attempts")]
    [InlineData("The folder {0} is not readable")]
    [InlineData("{0}/")]
    public void AnyTextThatSpellsAVaultFolderOrAPathInsideIt_IsRecognised(string template)
    {
        Assert.True(VaultLocationGuard.NamesVaultLocation(string.Format(template, Folder)));
    }

    [Fact]
    public void TheMatchIgnoresCase_SlashStyle_AndTheEscapingOfBackslashesInJson()
    {
        var windowsStyle = Folder.Replace('/', '\\').ToUpperInvariant();
        var jsonEscaped = Folder.Replace("/", "\\\\");

        Assert.True(VaultLocationGuard.NamesVaultLocation($"{windowsStyle}\\Summer\\One.mkv"));
        Assert.True(VaultLocationGuard.NamesVaultLocation($"{{\"path\":\"{jsonEscaped}\\\\summer\\\\one.mkv\"}}"));
        Assert.True(VaultLocationGuard.NamesVaultLocation($"{Folder}//summer///one.mkv"));
    }

    [Fact]
    public void AFolderWhoseNameOnlyStartsLikeTheVaultFolder_IsNotTheVault()
    {
        Assert.False(VaultLocationGuard.NamesVaultLocation(Folder + "-backup/one.mkv"));
        Assert.False(VaultLocationGuard.NamesVaultLocation(Folder + "2/one.mkv"));
        Assert.False(VaultLocationGuard.NamesVaultLocation(Folder + "_old"));
    }

    [Fact]
    public void OrdinaryTextAndEmptyTextAreNeverRecognised()
    {
        Assert.False(VaultLocationGuard.NamesVaultLocation("/mnt/movies/Dune (2021)/Dune.mkv"));
        Assert.False(VaultLocationGuard.NamesVaultLocation(string.Empty));
        Assert.False(VaultLocationGuard.NamesVaultLocation(null));
    }

    [Fact]
    public void WithNoVaultFolders_NothingIsEverRecognised()
    {
        VaultLocationGuard.Replace([]);

        Assert.False(VaultLocationGuard.HasVaultFolders);
        Assert.False(VaultLocationGuard.NamesVaultLocation(Folder + "/one.mkv"));
    }

    [Fact]
    public void AFolderAddedAtOnce_IsKnownBeforeTheNextRefresh_AndTheRefreshReplacesTheList()
    {
        var added = "/mnt/added-" + Guid.NewGuid().ToString("N");

        VaultLocationGuard.Add(added);
        VaultLocationGuard.Add(added);

        Assert.True(VaultLocationGuard.NamesVaultLocation(added + "/one.mkv"));
        Assert.True(VaultLocationGuard.NamesVaultLocation(Folder + "/one.mkv"));

        VaultLocationGuard.Replace([Folder]);

        Assert.False(VaultLocationGuard.NamesVaultLocation(added + "/one.mkv"));
    }

    [Fact]
    public void ABlankFolderIsNeverAdded_SoItCannotMatchEverything()
    {
        VaultLocationGuard.Replace(["", "   ", "/"]);
        VaultLocationGuard.Add("  ");

        Assert.False(VaultLocationGuard.HasVaultFolders);
        Assert.False(VaultLocationGuard.NamesVaultLocation("anything at all"));
    }
}
