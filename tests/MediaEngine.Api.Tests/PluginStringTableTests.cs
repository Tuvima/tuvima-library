using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

public sealed class PluginStringTableTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"tuvima_plugin_strings_{Guid.NewGuid():N}");

    public PluginStringTableTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "strings"));
        File.WriteAllText(Path.Combine(_folder, "strings", "en.json"), """{"hello":"Hello","only_en":"Only English","count":"{0} items"}""");
        File.WriteAllText(Path.Combine(_folder, "strings", "fr.json"), """{"hello":"Bonjour"}""");
        File.WriteAllText(Path.Combine(_folder, "strings", "de.json"), "{ not json");
    }

    private PluginStringTable Load() =>
        PluginStringTable.Load(new PluginManifest { Id = "p", SupportedLanguages = ["fr", "de", "../x"] }, _folder);

    [Theory]
    [InlineData("fr-CA", "Bonjour")]
    [InlineData("fr", "Bonjour")]
    [InlineData("en-GB", "Hello")]
    [InlineData("es", "Hello")]
    public void Words_FollowTheServerLanguageAndFallBackToEnglish(string tag, string expected) =>
        Assert.Equal(expected, Load().Get("hello", tag));

    [Fact]
    public void MissingTranslation_ShowsEnglish_AndMissingKey_ShowsTheKey()
    {
        var table = Load();
        Assert.Equal("Only English", table.Get("only_en", "fr"));
        Assert.Equal("nope", table.Get("nope", "fr"));
        Assert.Equal("Hello", table.Get("hello", "de"));
    }

    [Fact]
    public void Placeholders_AreFilled() => Assert.Equal("3 items", Load().Get("count", "en", 3));

    public void Dispose() => Directory.Delete(_folder, true);
}
