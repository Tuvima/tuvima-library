using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Files;
using MediaEngine.Api.Services.Plugins.Setup;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugin.ListenBrainz;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The plugin file service and the Plugin file card (Plugins and Integrations screens v5 WP27, D54): the card's state for a shipped
/// source file, a private plugin that has no Tuvima version, and a compiled-in plugin whose shipped description does not exist yet
/// or does; the check reads proposed text and writes nothing.
/// </summary>
public sealed class PluginFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-files", Guid.NewGuid().ToString("N"));
    private readonly string _providers;
    private readonly string _pluginRoot;
    private readonly ConfigurationDirectoryLoader _loader;

    public PluginFileServiceTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(libraryRoot);
        _loader = new ConfigurationDirectoryLoader(config);
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _providers = Path.Combine(config, "providers");
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
    public async Task AShippedSourceFile_ShowsValid_AndTheSameAsTuvimasVersion()
    {
        File.WriteAllText(Path.Combine(_providers, "tmdb.json"), ShippedProviderFiles.ReadText("tmdb.json")!);

        var state = await Service().StateAsync("tmdb", CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal("tmdb.json", state.FileName);
        Assert.Equal("source_file", state.Kind);
        Assert.Equal("valid", state.State);
        Assert.Equal(0, state.ErrorCount);
        Assert.True(state.HasShippedCopy);
        Assert.False(state.DiffersFromShipped);
        Assert.True(state.Editable);
    }

    [Fact]
    public async Task ASourceFileTheLibraryRewrote_WithOnlyTheSwitchChanged_DoesNotCountAsDifferent()
    {
        File.WriteAllText(Path.Combine(_providers, "tmdb.json"), ShippedProviderFiles.ReadText("tmdb.json")!.Replace("\"enabled\": true", "\"enabled\": false", StringComparison.Ordinal));

        var state = await Service().StateAsync("tmdb", CancellationToken.None);

        Assert.False(state!.DiffersFromShipped);
    }

    [Fact]
    public async Task AnEditedSourceFile_DiffersFromTuvimasVersion_AndShowsItsProblemCount()
    {
        File.WriteAllText(Path.Combine(_providers, "tmdb.json"), ShippedProviderFiles.ReadText("tmdb.json")!.Replace("\"weight\": 1,", "\"weight\": 7,", StringComparison.Ordinal));

        var service = Service();
        var state = await service.StateAsync("tmdb", CancellationToken.None);
        var file = await service.ReadAsync("tmdb", CancellationToken.None);

        Assert.True(state!.DiffersFromShipped);
        Assert.Equal("problems", state.State);
        Assert.Equal(1, state.ErrorCount);
        Assert.Contains("\"weight\": 7", file!.Text, StringComparison.Ordinal);
        var problem = Assert.Single(file.Problems);
        Assert.Equal(5, problem.Line);
        Assert.Equal("error", problem.Severity);
    }

    [Fact]
    public async Task APrivatePlugin_HasNoTuvimaVersion_AndItsFileCanBeReadAndChecked_WithoutAnythingWritten()
    {
        var path = WritePrivate("mine", "{\n  \"id\": \"mine\",\n  \"name\": \"Mine\",\n  \"version\": \"1.0.0\",\n  \"kind\": \"declarative\",\n  \"declarative\": {}\n}\n");
        var service = Service();

        var state = await service.StateAsync("mine", CancellationToken.None);
        var file = await service.ReadAsync("mine", CancellationToken.None);
        var broken = await service.CheckAsync("mine", file!.Text.Replace("\"1.0.0\"", "1", StringComparison.Ordinal), CancellationToken.None);
        var locked = await service.CheckAsync("mine", file.Text.Replace("\"id\": \"mine\"", "\"id\": \"yours\"", StringComparison.Ordinal), CancellationToken.None);

        Assert.Equal("plugin.json", state!.FileName);
        Assert.Equal("manifest", state.Kind);
        Assert.False(state.HasShippedCopy);
        Assert.False(state.DiffersFromShipped);
        Assert.Equal("valid", state.State);
        Assert.Equal("problems", broken!.State.State);
        Assert.Equal(4, Assert.Single(broken.Problems).Line);
        var change = Assert.Single(locked!.Problems, problem => problem.Code == "changed_locked");
        Assert.Equal(2, change.Line);
        Assert.Contains("the installed file", change.Message, StringComparison.Ordinal);
        Assert.Equal(file.Text, File.ReadAllText(path));
    }

    [Fact]
    public async Task ACompiledPlugin_HasNoFile_UntilItsShippedDescriptionExists()
    {
        var service = Service();

        Assert.Null(await service.StateAsync(ListenBrainzPlugin.PluginId, CancellationToken.None));
        Assert.Null(await service.ReadAsync(ListenBrainzPlugin.PluginId, CancellationToken.None));
        Assert.Null(await service.CheckAsync(ListenBrainzPlugin.PluginId, "{}", CancellationToken.None));
    }

    [Fact]
    public async Task ACompiledPlugin_WithAShippedDescription_GivesAReadOnlyView()
    {
        var text = "{\n  \"id\": \"tuvima.listenbrainz\",\n  \"name\": \"ListenBrainz\"\n}\n";
        var service = Service(new FixedDescriptions(ListenBrainzPlugin.PluginId, new ShippedPluginDescription("listenbrainz.json", text)));

        var file = await service.ReadAsync(ListenBrainzPlugin.PluginId, CancellationToken.None);

        Assert.Equal(text, file!.Text);
        Assert.Equal("shipped_description", file.State.Kind);
        Assert.Equal("listenbrainz.json", file.State.FileName);
        Assert.False(file.State.Editable);
    }

    [Fact]
    public async Task AnUnknownPlugin_HasNoFile()
    {
        Assert.Null(await Service().ReadAsync("does-not-exist", CancellationToken.None));
    }

    [Fact]
    public async Task ThePageModelEndsWithThePluginFileCard_ForAFileDefinedPlugin()
    {
        File.WriteAllText(Path.Combine(_providers, "tmdb.json"), ShippedProviderFiles.ReadText("tmdb.json")!.Replace("\"weight\": 1,", "\"weight\": 7,", StringComparison.Ordinal));
        var state = await Service().StateAsync("tmdb", CancellationToken.None);

        var page = Page(state);

        var last = page.Cards[^1];
        Assert.Equal("file", last.Key);
        Assert.Equal("Plugin file", last.Title);
        Assert.Same(state, last.File);
        Assert.Equal("tmdb.json", last.Facts.Single(fact => fact.Label == "File").Value);
        Assert.Equal("1 problem", last.Facts.Single(fact => fact.Label == "State").Value);
        Assert.Equal("Differs from Tuvima's version", last.Facts.Single(fact => fact.Label == "Compared with").Value);
        Assert.DoesNotContain(Page(null).Cards, card => card.Key == "file");
    }

    private static PluginSetupPageResponse Page(PluginFileStateDto? file) =>
        PluginPageModelBuilder.Build(new PluginPageModelInput { PluginId = "tmdb", Name = "TMDB", File = file });

    private PluginFileService Service(IShippedPluginDescriptions? descriptions = null)
    {
        var catalog = new PluginCatalog([new ListenBrainzPlugin()], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        return new PluginFileService(catalog, _loader, new ShippedPluginFiles(Path.Combine(_root, "shipped-plugins")), descriptions ?? new NoShippedPluginDescriptions(), NullLogger<PluginFileService>.Instance);
    }

    private string WritePrivate(string id, string text)
    {
        var directory = Path.Combine(_pluginRoot, id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "plugin.json");
        File.WriteAllText(path, text);
        return path;
    }

    private sealed class FixedDescriptions(string pluginId, ShippedPluginDescription description) : IShippedPluginDescriptions
    {
        public ShippedPluginDescription? Find(string id) => string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase) ? description : null;
    }
}
