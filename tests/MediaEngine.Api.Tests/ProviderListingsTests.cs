using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>Metadata providers as tiles in the Plugins list (Plugins and Integrations screens v3 D15).</summary>
public sealed class ProviderListingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-provider-listings", Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _loader;

    public ProviderListingsTests()
    {
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    [Fact]
    public void AProvider_IsListedAsAnOfficialMetadataTile_WithItsKeyState()
    {
        Save("tmdb", "TMDB", requiresKey: true, key: null, enabled: false);

        var tile = Assert.Single(Listings().List());

        Assert.Equal("provider", tile.ListingKind);
        Assert.Equal("official", tile.TrustLevel);
        Assert.Equal("intake", tile.Category);
        Assert.Equal("TMDB", tile.Name);
        Assert.True(tile.Provider!.RequiresKey);
        Assert.False(tile.Provider.KeyPresent);
        Assert.False(tile.Provider.AlwaysOn);
        Assert.Equal(["movies", "tv"], tile.MediaTypes);
    }

    [Fact]
    public void AProviderWithAKey_SaysSo()
    {
        Save("tmdb", "TMDB", requiresKey: true, key: "secret", enabled: true);

        Assert.True(Assert.Single(Listings().List()).Provider!.KeyPresent);
    }

    [Fact]
    public void TheCoreSources_AreAlwaysOnAndAlwaysEnabled()
    {
        Save("wikidata_reconciliation", "Wikidata", requiresKey: false, key: null, enabled: false);

        var tile = Assert.Single(Listings().List());

        Assert.True(tile.Provider!.AlwaysOn);
        Assert.True(tile.Enabled);
    }

    [Fact]
    public void AProviderASuppliedByAPlugin_IsLeftOut_BecauseThePluginsOwnTileStandsForIt()
    {
        _loader.SaveProvider(new ProviderConfiguration
        {
            Name = "from_plugin",
            DisplayName = "From plugin",
            ProviderId = Guid.NewGuid().ToString(),
            PluginId = "json.source",
            Enabled = true,
        });

        Assert.Empty(Listings().List());
    }

    private ProviderListings Listings() => new(_loader, new ProviderCapabilityCatalogue(_loader));

    private void Save(string name, string display, bool requiresKey, string? key, bool enabled) => _loader.SaveProvider(new ProviderConfiguration
    {
        Name = name,
        DisplayName = display,
        ProviderId = Guid.NewGuid().ToString(),
        Enabled = enabled,
        RequiresApiKey = requiresKey,
        HttpClient = key is null ? null : new HttpClientConfig { ApiKey = key },
        CanHandle = new CanHandleConfig { MediaTypes = ["Movies", "TV"] },
    });
}
