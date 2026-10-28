using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Every shipped plugin and metadata source says who may use it, how it behaves when it fails and whether to tell the administrator
/// (Plugin architecture spec PA3). A new one that leaves these out fails here, so the Plugins page never shows a blank where an
/// administrator expects an answer.
/// </summary>
public sealed class PluginDeclarationGuardrailTests : IDisposable
{
    private readonly ConfigurationDirectoryLoader _loader = new(Path.Combine(FindRepoRoot(), "config"));

    public void Dispose() => _loader.Dispose();

    [Fact]
    public void EverySource_StatesItsAudienceFailureAndNoticeBehaviour_AndHasACategory()
    {
        var sources = _loader.LoadAllProviders();
        Assert.NotEmpty(sources);

        foreach (var source in sources)
        {
            var declaration = source.Declaration;
            Assert.True(declaration is not null, $"Source '{source.Name}' has no declaration block.");
            Assert.True(PluginDeclarationRules.TryParseAudience(declaration!.Audience, out _), $"Source '{source.Name}' does not state who may use it.");
            Assert.False(string.IsNullOrWhiteSpace(declaration.OnFailure), $"Source '{source.Name}' does not state what happens when it fails.");
            Assert.False(string.IsNullOrWhiteSpace(declaration.Notify), $"Source '{source.Name}' does not say whether to tell the administrator.");
            Assert.False(string.IsNullOrWhiteSpace(declaration.WeightClass), $"Source '{source.Name}' does not state how heavy it is.");
            Assert.False(string.IsNullOrWhiteSpace(declaration.Changelog), $"Source '{source.Name}' has no changelog line.");
            Assert.True(source.UiMetadata is not null && !string.IsNullOrWhiteSpace(source.UiMetadata.Category), $"Source '{source.Name}' has no category.");
            Assert.Empty(PluginDeclarationRules.Validate(declaration));
        }
    }

    [Fact]
    public void TheOnDemandSources_AreForEveryone_AndEachOffersItsLookupAction()
    {
        var lyrics = Assert.IsType<ProviderConfiguration>(_loader.LoadProvider("lrclib"));
        var subtitles = Assert.IsType<ProviderConfiguration>(_loader.LoadProvider("subdl"));

        Assert.Equal("everyone", lyrics.Declaration!.Audience);
        Assert.Equal("find_lyrics", Assert.Single(lyrics.Declaration.Actions).Kind);
        Assert.Equal("lyrics", lyrics.Declaration.TextTrack);
        Assert.Equal("everyone", subtitles.Declaration!.Audience);
        Assert.Equal("find_subtitles", Assert.Single(subtitles.Declaration.Actions).Kind);
        Assert.Equal("subtitles", subtitles.Declaration.TextTrack);
    }

    [Fact]
    public void EveryOtherSource_IsForAdministratorsOnly()
    {
        foreach (var source in _loader.LoadAllProviders().Where(source => source.Name is not ("lrclib" or "subdl")))
        {
            Assert.Equal("administrators", source.Declaration!.Audience);
        }
    }

    [Fact]
    public void StashBox_IsTheVaultsOwnSource()
    {
        var stash = Assert.IsType<ProviderConfiguration>(_loader.LoadProvider("stashbox"));

        Assert.True(stash.AdultOnly);
        Assert.Equal("own_source", stash.Declaration!.Vault);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
