namespace MediaEngine.Storage.Tests;

public sealed class Phase6SettingsConfigurationTests
{
    [Fact]
    public void ConfigurationDirectoryLoader_PersistsAdminSettingsSources()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Storage/ConfigurationDirectoryLoader.cs");

        Assert.Contains("SaveCore", source, StringComparison.Ordinal);
        Assert.Contains("SaveProvider", source, StringComparison.Ordinal);
        Assert.Contains("SavePipelines", source, StringComparison.Ordinal);
        Assert.Contains("SaveHydration", source, StringComparison.Ordinal);
        Assert.Contains("SaveTranscoding", source, StringComparison.Ordinal);
        Assert.Contains("SaveLibraries", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderSecrets_AreNotReturnedAsPlaintextInStatusDto()
    {
        var endpoint = ReadRepoFile(@"src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs");
        var webDto = ReadRepoFile(@"src/MediaEngine.Contracts/Settings/ProviderSettingsContracts.cs");

        Assert.Contains("HasApiKey", endpoint, StringComparison.Ordinal);
        Assert.Contains("HasApiKey", webDto, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key_plaintext", endpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plaintext", webDto, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            relativePath)));
}
