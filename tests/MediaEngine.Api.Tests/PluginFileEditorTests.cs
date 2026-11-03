using System.Net;
using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Files;
using MediaEngine.Api.Services.Plugins.Setup;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugin.CommercialSkip;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugin.ListenBrainz;
using MediaEngine.Plugin.MediaSegments;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using MediaEngine.Storage.Secrets;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Plugin file editor's two writing actions (Plugins and Integrations screens v5 WP28, D55, D56): save refuses an invalid file and
/// writes nothing; a valid file is written, the plugin is loaded again and its check runs, and its keys and saved values are exactly as
/// they were; Reset puts Tuvima's pristine copy back; a compiled-in plugin, a private plugin's Reset and a file outside the plugin's own
/// place are all refused. A broken source file does not take the page down. The shipped JSON of a compiled-in plugin is viewable.
/// </summary>
public sealed class PluginFileEditorTests : IDisposable
{
    private const string PrivateText = "{\n  \"id\": \"mine\",\n  \"name\": \"Mine\",\n  \"version\": \"1.0.0\",\n  \"kind\": \"declarative\",\n  \"permissions\": [\"ai.infer\"],\n  \"ai_permissions\": [{ \"role\": \"text_fast\", \"max_tokens\": 200, \"schedule\": \"on-demand\", \"resource_class\": \"ai\" }],\n  \"declarative\": { \"ai_steps\": [{ \"key\": \"summarise\", \"name\": \"Summarise\", \"role\": \"text_fast\", \"prompt\": \"Summarise: {text}\" }] }\n}\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-editor", Guid.NewGuid().ToString("N"));
    private readonly string _config;
    private readonly string _providers;
    private readonly string _pluginRoot;
    private readonly string _shippedManifests;
    private readonly ConfigurationDirectoryLoader _loader;

    public PluginFileEditorTests()
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
        _shippedManifests = Path.Combine(_root, "shipped-plugins");
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

    // ------------------------------------------------------------------------------------------------ Save

    [Fact]
    public async Task Save_RefusesAnInvalidFile_AndWritesNothing()
    {
        var path = WriteShippedSource("tmdb.json");
        var before = File.ReadAllText(path);
        var (editor, _) = Build();

        var result = await editor.SaveAsync("tmdb", before.Replace("\"weight\": 1,", "\"weight\": 7,", StringComparison.Ordinal), default);

        Assert.NotNull(result);
        Assert.False(result.Saved);
        Assert.Equal(PluginFileEditor.Invalid, result.Outcome);
        Assert.Equal(1, result.State.ErrorCount);
        Assert.Equal(5, Assert.Single(result.Problems, problem => problem.Severity == "error").Line);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task Save_RefusesABrokenFile_AndATooLargeFile()
    {
        var path = WriteShippedSource("tmdb.json");
        var before = File.ReadAllText(path);
        var (editor, _) = Build();

        var broken = await editor.SaveAsync("tmdb", before.Replace("{", "[", StringComparison.Ordinal), default);
        var huge = await editor.SaveAsync("tmdb", new string(' ', PluginFileChecker.MaxCharacters + 1), default);

        Assert.False(broken!.Saved);
        Assert.Equal(PluginFileEditor.Invalid, broken.Outcome);
        Assert.False(huge!.Saved);
        Assert.Equal(PluginFileEditor.Invalid, huge.Outcome);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public async Task Save_RefusesAChangeToALockedField()
    {
        WritePrivate("mine", PrivateText);
        var (editor, _) = Build();

        var result = await editor.SaveAsync("mine", PrivateText.Replace("\"id\": \"mine\"", "\"id\": \"yours\"", StringComparison.Ordinal), default);

        Assert.False(result!.Saved);
        Assert.Contains(result.Problems, problem => problem.Code == "changed_locked");
        Assert.Equal(PrivateText, File.ReadAllText(Path.Combine(_pluginRoot, "mine", "plugin.json")));
    }

    [Fact]
    public async Task Save_OfAValidSourceFile_WritesIt_ReloadsIt_AndRunsItsCheck()
    {
        var path = WriteShippedSource("tmdb.json");
        var text = File.ReadAllText(path).Replace("\"weight\": 1,", "\"weight\": 0.5,", StringComparison.Ordinal);
        var original = File.ReadAllText(path);
        var (editor, _) = Build();

        var result = await editor.SaveAsync("tmdb", text, default);

        Assert.NotNull(result);
        Assert.True(result.Saved, result.Message);
        Assert.Equal(PluginFileEditor.Saved, result.Outcome);
        Assert.True(result.Reloaded);
        Assert.Equal("working", result.Health);
        Assert.Equal(text, File.ReadAllText(path));
        Assert.Equal(text, result.Text);
        Assert.Equal(original, File.ReadAllText(path + ".bak"));
        Assert.Equal(0.5, _loader.LoadProvider("tmdb")!.Weight);
        Assert.Empty(Directory.GetFiles(_providers, "*.tmp"));
        Assert.True(result.State.DiffersFromShipped);
    }

    [Fact]
    public async Task Save_OfAValidManifest_ReloadsThePlugin_AndKeepsItsKeysAndSavedValues()
    {
        WritePrivate("mine", PrivateText);
        var secrets = new ConfigFolderSecretsStore(_config);
        await secrets.SetAsync(SecretOwner.Plugin("mine"), new Dictionary<string, string> { ["api_key"] = "the-key-value" });
        var (editor, catalog) = Build();
        var settingsService = new PluginSettingsService(_loader);
        var saved = settingsService.Load(catalog.Get("mine")!.Manifest);
        saved.Settings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["region"] = JsonSerializer.SerializeToElement("nz"),
            ["a_setting_the_new_file_forgets"] = JsonSerializer.SerializeToElement(42),
        };
        settingsService.Save(saved);
        var edited = PrivateText.Replace("\"name\": \"Mine\"", "\"name\": \"Mine, renamed\"", StringComparison.Ordinal);

        var result = await editor.SaveAsync("mine", edited, default);

        Assert.True(result!.Saved, result.Message + " " + string.Join("; ", result.Problems.Select(problem => $"{problem.Line}: {problem.Message}")));
        Assert.True(result.Reloaded, result.Message);
        Assert.Equal("Mine, renamed", catalog.Get("mine")!.Manifest.Name);
        Assert.Null(catalog.Get("mine")!.LoadError);
        Assert.Equal(edited, File.ReadAllText(Path.Combine(_pluginRoot, "mine", "plugin.json")));
        var kept = settingsService.Load(catalog.Get("mine")!.Manifest);
        Assert.Equal("nz", kept.Settings["region"].GetString());
        Assert.Equal(42, kept.Settings["a_setting_the_new_file_forgets"].GetInt32());
        Assert.Equal("the-key-value", await secrets.GetAsync(SecretOwner.Plugin("mine"), "api_key"));
        Assert.Equal("not_checked", result.Health);
    }

    [Fact]
    public async Task Save_OfAFileThePluginCannotLoad_SaysSo_AndOffersTheWayBack()
    {
        // Passes the published schema and Library's rules (an empty "declarative" section is valid JSON of the right shape), but the
        // plugin loader refuses a plugin that does nothing, so the page must say the saved file did not load.
        var (editor, catalog) = Build();
        var empty = "{\n  \"id\": \"mine\",\n  \"name\": \"Mine\",\n  \"version\": \"1.0.0\",\n  \"kind\": \"declarative\",\n  \"declarative\": {}\n}\n";

        WritePrivate("mine", empty.Replace("\"name\": \"Mine\"", "\"name\": \"Before\"", StringComparison.Ordinal));
        catalog.Reload();

        var result = await editor.SaveAsync("mine", empty, default);

        Assert.NotNull(result);
        Assert.True(result.Saved);
        Assert.False(result.Reloaded);
        Assert.Equal(PluginFileEditor.ReloadFailed, result.Outcome);
        Assert.Contains("could not load the plugin", result.Message, StringComparison.Ordinal);
        Assert.Equal("needs_attention", result.Health);
        Assert.NotNull(catalog.Get("mine")!.LoadError);
    }

    [Fact]
    public async Task Save_NeverWritesOutsideThePluginsOwnFile()
    {
        WritePrivate("mine", PrivateText);
        var (editor, _) = Build();
        var outside = Path.Combine(_root, "outside.json");

        var result = await editor.SaveAsync("mine", PrivateText.Replace("\"name\": \"Mine\"", "\"name\": \"Other\"", StringComparison.Ordinal), default);
        var traversal = await editor.SaveAsync("../../outside", PrivateText, default);
        var odd = await editor.SaveAsync("..\\outside", PrivateText, default);

        Assert.True(result!.Saved);
        Assert.Null(traversal);
        Assert.Null(odd);
        Assert.False(File.Exists(outside));
        Assert.Equal(["plugin.json", "plugin.json.bak"], Directory.GetFiles(Path.Combine(_pluginRoot, "mine")).Select(file => Path.GetFileName(file)).Order().ToArray());
        Assert.Equal(["mine"], Directory.GetDirectories(_pluginRoot).Select(directory => Path.GetFileName(directory)).ToArray());
    }

    [Fact]
    public async Task Save_AndReset_AreRefusedForACompiledInPlugin()
    {
        var (editor, _) = Build(plugins: [new FandomLorePlugin()]);

        var saved = await editor.SaveAsync(new FandomLorePlugin().Manifest.Id, "{}", default);
        var reset = await editor.ResetAsync(new FandomLorePlugin().Manifest.Id, default);

        Assert.Equal(PluginFileEditor.ReadOnly, saved!.Outcome);
        Assert.False(saved.Saved);
        Assert.Equal(PluginFileEditor.ReadOnly, reset!.Outcome);
        Assert.False(reset.Saved);
        Assert.NotEmpty(saved.Text);
    }

    [Fact]
    public async Task Save_And_Reset_OfAPluginWithNoFile_AreNotFound()
    {
        var (editor, _) = Build();

        Assert.Null(await editor.SaveAsync("does-not-exist", "{}", default));
        Assert.Null(await editor.ResetAsync("does-not-exist", default));
    }

    // ------------------------------------------------------------------------------------------------ Reset

    [Fact]
    public async Task Reset_PutsTuvimasPristineSourceFileBack_AndKeepsThePersonsSwitch()
    {
        var path = Path.Combine(_providers, "tmdb.json");
        var shipped = ShippedProviderFiles.ReadText("tmdb.json")!;
        File.WriteAllText(path, shipped.Replace("\"weight\": 1,", "\"weight\": 0.5,", StringComparison.Ordinal).Replace("\"enabled\": true", "\"enabled\": false", StringComparison.Ordinal));
        var (editor, _) = Build();

        var result = await editor.ResetAsync("tmdb", default);

        Assert.True(result!.Saved, result.Message);
        Assert.Equal(PluginFileEditor.Reset, result.Outcome);
        Assert.True(result.Reloaded);
        Assert.False(result.State.DiffersFromShipped);
        Assert.Equal(1, _loader.LoadProvider("tmdb")!.Weight);
        Assert.False(_loader.LoadProvider("tmdb")!.Enabled);
        Assert.True(PluginFileComparer.Same(File.ReadAllText(path), shipped, PluginFileComparer.SourceFileChoices));
    }

    [Fact]
    public async Task Reset_PutsTuvimasShippedManifestBack()
    {
        var shipped = PrivateText;
        Directory.CreateDirectory(Path.Combine(_shippedManifests, "mine"));
        File.WriteAllText(Path.Combine(_shippedManifests, "mine", "plugin.json"), shipped);
        var path = WritePrivate("mine", PrivateText.Replace("\"name\": \"Mine\"", "\"name\": \"Edited\"", StringComparison.Ordinal));
        var (editor, catalog) = Build();

        var result = await editor.ResetAsync("mine", default);

        Assert.True(result!.Saved, result.Message);
        Assert.Equal(shipped, File.ReadAllText(path));
        Assert.Equal("Mine", catalog.Get("mine")!.Manifest.Name);
    }

    [Fact]
    public async Task Reset_IsRefusedForAPrivatePlugin_BecauseItHasNoTuvimaVersion()
    {
        var path = WritePrivate("mine", PrivateText);
        var (editor, _) = Build();

        var result = await editor.ResetAsync("mine", default);

        Assert.False(result!.Saved);
        Assert.Equal(PluginFileEditor.NoShippedCopy, result.Outcome);
        Assert.False(result.State.HasShippedCopy);
        Assert.Equal(PrivateText, File.ReadAllText(path));
    }

    // ------------------------------------------------------------------------------------------------ Compiled-in plugins

    [Fact]
    public void EveryCompiledInPluginWithSettings_HasAShippedDescription_ThatPassesTheSettingsSchema()
    {
        ITuvimaPlugin[] plugins = [new CommercialSkipPlugin(), new FandomLorePlugin(), new SkipMarkersPlugin(), new ListenBrainzPlugin()];
        var descriptions = new CompiledPluginDescriptions(plugins);
        var withSettings = plugins.Where(plugin => plugin.Manifest.SettingsSchema is { ValueKind: JsonValueKind.Object }).ToList();

        Assert.NotEmpty(withSettings);
        foreach (var plugin in withSettings)
        {
            var description = descriptions.Find(plugin.Manifest.Id);
            Assert.NotNull(description);
            Assert.EndsWith(".settings.json", description.FileName, StringComparison.Ordinal);
            var result = PluginFileChecker.Check(description.Text, PluginFileFormat.SettingsDescription, description.FileName, baselineText: null);
            Assert.True(result.IsValid, $"{description.FileName}: " + string.Join("; ", result.Problems.Where(problem => problem.Severity == PluginFileSeverity.Error).Select(problem => $"line {problem.Line} {problem.Message}")));
        }
    }

    [Fact]
    public async Task ACompiledInPlugin_ShowsItsShippedJson_AsAValidReadOnlyFile()
    {
        var plugin = new FandomLorePlugin();
        var (_, catalog) = Build(descriptions: new CompiledPluginDescriptions([plugin]), plugins: [plugin]);
        var service = new PluginFileService(catalog, _loader, new ShippedPluginFiles(_shippedManifests), new CompiledPluginDescriptions([plugin]), NullLogger<PluginFileService>.Instance);

        var file = await service.ReadAsync(plugin.Manifest.Id, default);

        Assert.NotNull(file);
        Assert.Equal("shipped_description", file.State.Kind);
        Assert.Equal("fandom-lore.settings.json", file.State.FileName);
        Assert.False(file.State.Editable);
        Assert.Equal("valid", file.State.State);
        Assert.Contains("auto_discovery_enabled", file.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ A broken file does not break the page

    [Fact]
    public async Task APageStillOpens_WhenASourceFileFailsTheLoadersChecks_AndTheFileCardShowsTheProblem()
    {
        WriteShippedSource("tmdb.json");
        File.WriteAllText(Path.Combine(_providers, "broken.json"), "{ \"name\": \"broken\", \"provider_id\": \"" + Guid.NewGuid() + "\", \"weight\": \"heavy\" }");
        var (_, catalog) = Build();
        var files = FileService(catalog);
        var pages = new PluginPageModelService(
            catalog, _loader, new ProviderCapabilityCatalogue(_loader), new ConfigFolderSecretsStore(_config), [], new PluginActivityLimits(_loader), files, NullLogger<PluginPageModelService>.Instance);

        var page = await pages.BuildAsync("broken", default);
        var other = await pages.BuildAsync("tmdb", default);

        Assert.NotNull(page);
        var card = page.Cards[^1];
        Assert.Equal("file", card.Key);
        Assert.NotNull(card.File);
        Assert.Equal("broken.json", card.File.FileName);
        Assert.NotNull(other);
        Assert.Null(await pages.BuildAsync("no-such-plugin", default));
        Assert.False(pages.Exists("no-such-plugin"));
    }

    // ------------------------------------------------------------------------------------------------ Helpers

    private string WriteShippedSource(string fileName)
    {
        var path = Path.Combine(_providers, fileName);
        File.WriteAllText(path, ShippedProviderFiles.ReadText(fileName)!);
        return path;
    }

    private string WritePrivate(string id, string text)
    {
        var directory = Path.Combine(_pluginRoot, id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "plugin.json");
        File.WriteAllText(path, text);
        return path;
    }

    private PluginFileService FileService(PluginCatalog catalog, IShippedPluginDescriptions? descriptions = null) =>
        new(catalog, _loader, new ShippedPluginFiles(_shippedManifests), descriptions ?? new NoShippedPluginDescriptions(), NullLogger<PluginFileService>.Instance);

    private (PluginFileEditor Editor, PluginCatalog Catalog) Build(IShippedPluginDescriptions? descriptions = null, IReadOnlyList<ITuvimaPlugin>? plugins = null)
    {
        var catalog = new PluginCatalog(plugins ?? [], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        var check = new PluginCheckNowService(
            catalog,
            new ProviderCapabilityCatalogue(_loader),
            _loader,
            new NoContextFactory(),
            new OkFactory(),
            TimeProvider.System,
            NullLogger<PluginCheckNowService>.Instance);
        var editor = new PluginFileEditor(FileService(catalog, descriptions ?? new CompiledPluginDescriptions(plugins ?? [])), catalog, _loader, check, NullLogger<PluginFileEditor>.Instance);
        return (editor, catalog);
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
