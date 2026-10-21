using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Metadata sources supplied by JSON plugins (Developer tools spec WP20): a switched-on plugin's sources are written into the
/// providers folder with Library-set values, removed when the plugin goes off, and described by capability sheets.
/// </summary>
public sealed class PluginMetadataSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-sources", Guid.NewGuid().ToString("N"));
    private readonly string _pluginRoot;
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PluginSettingsService _settings;
    private readonly PluginCatalog _catalog;

    public PluginMetadataSourceTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        _pluginRoot = Path.Combine(libraryRoot, ".data", "plugins");
        Directory.CreateDirectory(_pluginRoot);
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _settings = new PluginSettingsService(_loader);
        var files = new PluginMetadataSourceFiles(_loader, NullLogger<PluginMetadataSourceFiles>.Instance);
        _catalog = new PluginCatalog([], _settings, _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance, sourceFiles: files);
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
    public void ASwitchedOffPlugin_WritesNoProviderFile()
    {
        Add("json.source", Source("my_source", "api.example.com"), Network("api.example.com"));

        Assert.Empty(PluginProviders());
    }

    [Fact]
    public void SwitchingAPluginOn_WritesItsSourceWithLibrarySetValues_AndSwitchingOffRemovesIt()
    {
        Add("json.source", Source("my_source", "api.example.com", extra: "\"weight\": 9.9, \"registration_order\": 1, \"requires_api_key\": true, \"provider_id\": \"00000000-0000-0000-0000-000000000001\""), Network("api.example.com"));

        Enable("json.source", true);

        var provider = Assert.Single(PluginProviders());
        Assert.Equal(NameOf("json.source", "my_source"), provider.Name);
        Assert.Equal("json.source", provider.PluginId);
        Assert.Equal(PluginMetadataSourceFiles.LibraryWeight, provider.Weight);
        Assert.True(provider.RegistrationOrder >= PluginMetadataSourceFiles.FirstRegistrationOrder);
        Assert.False(provider.RequiresApiKey);
        Assert.NotEqual("00000000-0000-0000-0000-000000000001", provider.ProviderId);

        Enable("json.source", false);

        Assert.Empty(PluginProviders());
    }

    [Fact]
    public void ASourceThatContactsAnUndeclaredAddress_IsNotTakenIn()
    {
        Add("json.sneaky", Source("sneaky", "elsewhere.example.org"), Network("api.example.com"));

        Enable("json.sneaky", true);

        Assert.Empty(PluginProviders());
    }

    [Fact]
    public void ASourceWhoseTemplateNamesAnUndeclaredHost_IsNotTakenIn()
    {
        Add("json.template", Source("templ", "api.example.com", extra: "\"search_strategies\": [{ \"name\": \"s\", \"priority\": 1, \"url_template\": \"https://evil.example.org/?q={title}\" }]"), Network("api.example.com"));

        Enable("json.template", true);

        Assert.Empty(PluginProviders());
    }

    [Fact]
    public void AClaimConfidenceAboveTheLibraryLimit_IsLowered()
    {
        Add("json.bold", Source("bold", "api.example.com", extra: "\"field_mappings\": [{ \"claim_key\": \"title\", \"json_path\": \"$.t\", \"confidence\": 1.0 }]"), Network("api.example.com"));

        Enable("json.bold", true);

        var provider = Assert.Single(PluginProviders());
        var mapping = Assert.Single(provider.FieldMappings!);
        Assert.Equal(PluginMetadataSourceFiles.MaxClaimConfidence, mapping.Confidence);
    }

    [Fact]
    public void TwoPluginsWithSimilarIds_NeverShareAFileName()
    {
        Add("json.a", Source("one", "api.example.com"), Network("api.example.com"));
        Add("json_a", Source("one", "api.example.com"), Network("api.example.com"));

        Enable("json.a", true);
        Enable("json_a", true);

        Assert.Equal(2, PluginProviders().Count);
        Enable("json_a", false);
        Assert.Equal("json.a", Assert.Single(PluginProviders()).PluginId);
    }

    [Fact]
    public void ThePersonsOffSwitch_SurvivesTheNextReload()
    {
        Add("json.source", Source("my_source", "api.example.com"), Network("api.example.com"));
        Enable("json.source", true);
        var provider = Assert.Single(PluginProviders());
        provider.Enabled = false;
        _loader.SaveProvider(provider);

        _catalog.Reload();

        Assert.False(Assert.Single(PluginProviders()).Enabled);
    }

    [Fact]
    public void ASourceFromAPluginWithoutTheNetworkPermission_IsNotTakenIn()
    {
        Add("json.quiet", Source("quiet", "api.example.com"), "\"permissions\": [], \"network\": [{ \"host\": \"api.example.com\", \"sends\": \"Titles\" }],");

        Enable("json.quiet", true);

        Assert.Empty(PluginProviders());
    }

    [Fact]
    public void TheCatalogue_SaysWhatWouldHaveNoSourceIfOneWereOff_AndWhatStaysCovered()
    {
        Save("a_source", "Source A", ["artwork", "metadata"], ["movie"]);
        Save("b_source", "Source B", ["artwork"], ["movie"]);

        var impact = Assert.IsType<MediaEngine.Contracts.Plugins.PluginSourceImpactDto>(new ProviderCapabilityCatalogue(_loader).SwitchOffImpact("a_source"));

        Assert.Equal(["movie details"], impact.WouldLose);
        var covered = Assert.Single(impact.StillCovered);
        Assert.Equal("movie artwork", covered.Supplies);
        Assert.Equal(["Source B"], covered.By);
        Assert.Contains("kept", impact.Keeps, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCatalogue_ComparesTwoSources_AndReturnsNothingForAnUnknownOne()
    {
        Save("a_source", "Source A", ["artwork", "metadata"], ["movie"]);
        Save("b_source", "Source B", ["artwork", "ratings"], ["movie"]);
        var catalogue = new ProviderCapabilityCatalogue(_loader);

        var comparison = Assert.IsType<MediaEngine.Contracts.Plugins.PluginSourceComparisonDto>(catalogue.Compare("a_source", "b_source"));

        Assert.Equal(["movie artwork"], comparison.Both);
        Assert.Equal(["movie details"], comparison.OnlyFrom);
        Assert.Equal(["movie ratings"], comparison.OnlyTo);
        Assert.Null(catalogue.Compare("a_source", "missing"));
        Assert.Null(catalogue.SwitchOffImpact("missing"));
    }

    [Fact]
    public void TheOverview_SaysARestartIsNeeded_OnlyWhenThePluginSourcesDifferFromStartup()
    {
        Add("json.source", Source("my_source", "api.example.com"), Network("api.example.com"));
        var atStartup = new ProviderStartupSnapshot([]);

        Assert.False(new ProviderCapabilityCatalogue(_loader, atStartup).Overview().RestartNeeded);

        Enable("json.source", true);

        Assert.True(new ProviderCapabilityCatalogue(_loader, atStartup).Overview().RestartNeeded);
        Assert.False(new ProviderCapabilityCatalogue(_loader, new ProviderStartupSnapshot([NameOf("json.source", "my_source")])).Overview().RestartNeeded);
    }

    private static string NameOf(string pluginId, string source) =>
        $"plugin_{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pluginId)))[..8].ToLowerInvariant()}_{source}";

    private List<ProviderConfiguration> PluginProviders() => _loader.LoadAllProviders().Where(p => p.PluginId is not null).ToList();

    private void Enable(string id, bool on)
    {
        _settings.SetEnabled(Assert.IsType<PluginRegistration>(_catalog.Get(id)).Manifest, on);
        _catalog.Reload();
    }

    private void Save(string name, string display, string[] capabilities, string[] media) => _loader.SaveProvider(new ProviderConfiguration
    {
        Name = name,
        DisplayName = display,
        ProviderId = Guid.NewGuid().ToString(),
        Enabled = true,
        ProviderCapabilities = [.. capabilities],
        CanHandle = new CanHandleConfig { MediaTypes = [.. media] },
    });

    private static string Network(string host) =>
        $"\"permissions\": [\"network.http\"], \"network\": [{{ \"host\": \"{host}\", \"sends\": \"Titles\" }}],";

    private static string Source(string name, string host, string extra = "") =>
        $"\"declarative\": {{ \"metadata_sources\": [{{ \"name\": \"{name}\", \"adapter_type\": \"config_driven\", \"version\": \"1.0\", {(extra.Length > 0 ? extra + ", " : "")}\"endpoints\": {{ \"api\": \"https://{host}\" }} }}] }}";

    private void Add(string id, string declarative, string head)
    {
        var directory = Path.Combine(_pluginRoot, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $"{{ \"id\": \"{id}\", \"name\": \"{id}\", \"version\": \"1.0.0\", \"kind\": \"declarative\", {head} {declarative} }}");
        _catalog.Reload();
    }
}
