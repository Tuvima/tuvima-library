using System.Net;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Files;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using MediaEngine.Storage.Secrets;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Restore official plugins (Plugins and Integrations screens v5 WP29, D57): the preview lists each Tuvima plugin file that is missing,
/// damaged or edited and counts the rest as matching; the restore puts Tuvima's version back, keeps a source file's own switch, and
/// leaves keys and Private plugins alone. The administrator-only rule is asserted by <see cref="PluginEndpointAuthorizationTests"/>.
/// </summary>
public sealed class PluginRestoreTests : IDisposable
{
    private const string PrivateText = "{\n  \"id\": \"mine\",\n  \"name\": \"Mine\",\n  \"version\": \"1.0.0\",\n  \"kind\": \"declarative\",\n  \"permissions\": [\"ai.infer\"],\n  \"ai_permissions\": [{ \"role\": \"text_fast\", \"max_tokens\": 200, \"schedule\": \"on-demand\", \"resource_class\": \"ai\" }],\n  \"declarative\": { \"ai_steps\": [{ \"key\": \"summarise\", \"name\": \"Summarise\", \"role\": \"text_fast\", \"prompt\": \"Summarise: {text}\" }] }\n}\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-restore", Guid.NewGuid().ToString("N"));
    private readonly string _config;
    private readonly string _providers;
    private readonly string _pluginRoot;
    private readonly ConfigurationDirectoryLoader _loader;

    public PluginRestoreTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        _config = Path.Combine(_root, "config");
        Directory.CreateDirectory(libraryRoot);
        _loader = new ConfigurationDirectoryLoader(_config);
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _providers = Path.Combine(_config, "providers");
        Directory.CreateDirectory(_providers);
        _pluginRoot = Path.Combine(libraryRoot, ".data", "plugins");
        Directory.CreateDirectory(_pluginRoot);
    }

    public void Dispose()
    {
        _loader.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The temp folder is cleaned up by the operating system later.
        }
    }

    [Fact]
    public async Task Preview_ListsTheDeletedAndTheEditedFile_AndCountsTheRestAsMatching()
    {
        WriteAllShipped();
        File.Delete(Path.Combine(_providers, "lrclib.json"));
        EditWeight("tmdb.json");

        var preview = await Build().PreviewAsync(default);

        Assert.Equal(2, preview.Items.Count);
        var missing = Assert.Single(preview.Items, item => item.FileName == "lrclib.json");
        Assert.Equal(("missing", "restore"), (missing.Reason, missing.Action));
        var edited = Assert.Single(preview.Items, item => item.FileName == "tmdb.json");
        Assert.Equal(("edited", "reset"), (edited.Reason, edited.Action));
        Assert.Equal(ShippedProviderFiles.FileNames().Count - 2, preview.MatchingCount);
    }

    [Fact]
    public async Task Preview_CallsAnUnreadableFileDamaged_AndIgnoresTheSwitchAndLayout()
    {
        WriteAllShipped();
        File.WriteAllText(Path.Combine(_providers, "tvdb.json"), "{ not json");
        var tmdb = Path.Combine(_providers, "tmdb.json");
        File.WriteAllText(tmdb, File.ReadAllText(tmdb).Replace("\"enabled\": true", "\"enabled\": false", StringComparison.Ordinal));

        var preview = await Build().PreviewAsync(default);

        var damaged = Assert.Single(preview.Items);
        Assert.Equal(("tvdb.json", "damaged", "restore"), (damaged.FileName, damaged.Reason, damaged.Action));
    }

    [Fact]
    public async Task Preview_WhenEverythingMatches_ListsNothing()
    {
        WriteAllShipped();

        var preview = await Build().PreviewAsync(default);

        Assert.Empty(preview.Items);
        Assert.Equal(ShippedProviderFiles.FileNames().Count, preview.MatchingCount);
    }

    [Fact]
    public async Task Restore_PutsBothFilesBack_AndLeavesKeysSwitchesAndAPrivatePluginUntouched()
    {
        WriteAllShipped();
        File.Delete(Path.Combine(_providers, "lrclib.json"));
        var tmdb = Path.Combine(_providers, "tmdb.json");
        File.WriteAllText(tmdb, File.ReadAllText(tmdb).Replace("\"weight\": 1,", "\"weight\": 0.5,", StringComparison.Ordinal).Replace("\"enabled\": true", "\"enabled\": false", StringComparison.Ordinal));
        var privatePath = Path.Combine(_pluginRoot, "mine", "plugin.json");
        Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
        File.WriteAllText(privatePath, PrivateText);
        var secrets = new ConfigFolderSecretsStore(_config);
        await secrets.SetAsync(SecretOwner.Plugin("tmdb"), new Dictionary<string, string> { ["api_key"] = "the-key-value" });
        var service = Build();

        var result = await service.RestoreAsync(default);

        Assert.Equal((1, 1, 0), (result.RestoredCount, result.ResetCount, result.FailedCount));
        Assert.Equal("restored", Assert.Single(result.Items, item => item.FileName == "lrclib.json").Outcome);
        Assert.Equal("reset", Assert.Single(result.Items, item => item.FileName == "tmdb.json").Outcome);
        Assert.True(PluginFileComparer.Same(File.ReadAllText(Path.Combine(_providers, "lrclib.json")), ShippedProviderFiles.ReadText("lrclib.json")!, PluginFileComparer.SourceFileChoices));
        Assert.Equal(1, _loader.LoadProvider("tmdb")!.Weight);
        Assert.False(_loader.LoadProvider("tmdb")!.Enabled);
        Assert.Equal("the-key-value", await secrets.GetAsync(SecretOwner.Plugin("tmdb"), "api_key"));
        Assert.Equal(PrivateText, File.ReadAllText(privatePath));
        var again = await service.PreviewAsync(default);
        Assert.Empty(again.Items);
    }

    // ------------------------------------------------------------------------------------------------ Helpers

    private void WriteAllShipped()
    {
        foreach (var name in ShippedProviderFiles.FileNames())
        {
            File.WriteAllText(Path.Combine(_providers, name), ShippedProviderFiles.ReadText(name)!);
        }
    }

    private void EditWeight(string fileName)
    {
        var path = Path.Combine(_providers, fileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"weight\": 1,", "\"weight\": 0.5,", StringComparison.Ordinal));
    }

    private PluginRestoreService Build()
    {
        var catalog = new PluginCatalog([], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        var files = new PluginFileService(catalog, _loader, new ShippedPluginFiles(Path.Combine(_root, "shipped-plugins")), new NoShippedPluginDescriptions(), NullLogger<PluginFileService>.Instance);
        var check = new PluginCheckNowService(catalog, new ProviderCapabilityCatalogue(_loader), _loader, new NoContextFactory(), new OkFactory(), TimeProvider.System, NullLogger<PluginCheckNowService>.Instance);
        var editor = new PluginFileEditor(files, catalog, _loader, check, NullLogger<PluginFileEditor>.Instance);
        return new PluginRestoreService(files, editor, NullLogger<PluginRestoreService>.Instance);
    }

    private sealed class NoContextFactory : IPluginExecutionContextFactory
    {
        public IPluginExecutionContext Create(string pluginId, string purpose) => throw new NotSupportedException();

        public IPluginExecutionContext CreateForMedia(string pluginId, string purpose, PluginMediaAssetContext asset) => throw new NotSupportedException();
    }

    private sealed class OkFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
