using MediaEngine.Api.Services.Plugins.Sources;
using Serilog;

namespace MediaEngine.Api.Tests;

public sealed class ShippedProviderFilesTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "tuvima-shipped-" + Guid.NewGuid().ToString("N"));

    public ShippedProviderFilesTests() => Directory.CreateDirectory(Path.Combine(_config, "providers"));

    public void Dispose() => Directory.Delete(_config, recursive: true);

    [Fact]
    public void AMissingShippedFile_IsPutBack_AndTheSecretsFolderIsLeftAlone()
    {
        var secrets = Path.Combine(_config, "secrets");
        Directory.CreateDirectory(secrets);
        File.WriteAllText(Path.Combine(secrets, "tmdb.json"), "{\"key\":\"abc\"}");

        var restored = ShippedProviderFiles.Repair(_config, Log.Logger);

        Assert.Contains("tmdb.json", restored);
        Assert.True(File.Exists(Path.Combine(_config, "providers", "tmdb.json")));
        Assert.Equal("{\"key\":\"abc\"}", File.ReadAllText(Path.Combine(secrets, "tmdb.json")));
    }

    [Fact]
    public void AnUnreadableShippedFile_IsReplaced()
    {
        File.WriteAllText(Path.Combine(_config, "providers", "tmdb.json"), "{ not json");

        ShippedProviderFiles.Repair(_config, Log.Logger);

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_config, "providers", "tmdb.json")));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void AFileThatReadsFine_IsNeverTouched_AndAPrivateFileStaysToo()
    {
        var edited = Path.Combine(_config, "providers", "tmdb.json");
        File.WriteAllText(edited, "{\"name\":\"tmdb\",\"enabled\":false}");
        var mine = Path.Combine(_config, "providers", "my_upload.json");
        File.WriteAllText(mine, "{}");

        var restored = ShippedProviderFiles.Repair(_config, Log.Logger);

        Assert.DoesNotContain("tmdb.json", restored);
        Assert.Equal("{\"name\":\"tmdb\",\"enabled\":false}", File.ReadAllText(edited));
        Assert.Equal("{}", File.ReadAllText(mine));
    }

    [Fact]
    public void AFolderThatDoesNotExistYet_IsLeftToFirstRunSetup()
    {
        var missing = Path.Combine(_config, "elsewhere");

        Assert.Empty(ShippedProviderFiles.Repair(missing, Log.Logger));
        Assert.False(Directory.Exists(missing));
    }
}
