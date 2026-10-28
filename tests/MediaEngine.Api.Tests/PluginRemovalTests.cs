using System.Reflection;
using System.Text;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.AiFeatures;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Secrets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Removing a plugin cleanly (Plugin architecture spec C23): the preview says what goes and what stays, removal cleans every part the
/// plugin left in the data store, saved output goes only when the person chose that, and a plugin that is gone for any other reason
/// has its leftovers swept.
/// </summary>
public sealed class PluginRemovalTests : IDisposable
{
    private const string Base = "tuvima.test.removal-base";
    private const string User = "tuvima.test.removal-user";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-removal-tests", Guid.NewGuid().ToString("N"));
    private readonly ApprovalTestKit _kit = new();
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly ConfigFolderSecretsStore _secrets;
    private readonly PluginDataRepository _data;
    private readonly PluginCatalog _catalog;
    private readonly PluginRemovalService _removal;

    public PluginRemovalTests()
    {
        var configRoot = Path.Combine(_root, "config");
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        _secrets = new ConfigFolderSecretsStore(configRoot);
        _loader = new ConfigurationDirectoryLoader(configRoot, _secrets);
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _data = new PluginDataRepository(_kit.Database);
        _catalog = new PluginCatalog([], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        _removal = new PluginRemovalService(
            _catalog,
            new ProviderCapabilityCatalogue(_loader),
            _data,
            _secrets,
            new AiFeatureRetractionService(null!, NullLogger<AiFeatureRetractionService>.Instance),
            new PluginActivityService(new PluginActivityRepository(_kit.Database), new PluginCircuitBreaker(TimeProvider.System), TimeProvider.System, NullLogger<PluginActivityService>.Instance),
            NullLogger<PluginRemovalService>.Instance);
    }

    public void Dispose()
    {
        _kit.Dispose();
        _loader.Dispose();
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
    public async Task Preview_CountsWhatTheRemovalWouldTakeAway_InPlainWords()
    {
        Install(Base, onRemoval: "remove");
        await SeedAsync(Base);

        var preview = await _removal.PreviewAsync(Base, default);

        Assert.NotNull(preview);
        Assert.Equal(2, preview.Records);
        Assert.Equal(1, preview.SecretsSet);
        Assert.Equal("skip markers", preview.SavedOutput);
        Assert.Equal(2, preview.SavedOutputCount);
        Assert.Equal("remove", preview.DefaultOutput);
        Assert.Contains(preview.Lines, line => line.Contains("2 records", StringComparison.Ordinal));
        Assert.Contains(preview.Lines, line => line.Contains("1 saved key or login", StringComparison.Ordinal));
        Assert.Contains(preview.Lines, line => line.Contains("original files are never touched", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Preview_OfAPluginThatIsNotInstalled_IsNothing()
    {
        Assert.Null(await _removal.PreviewAsync("tuvima.test.nope", default));
    }

    [Fact]
    public async Task Remove_CleansRecordsEventPositionRunHistoryLimitsAndKeys_ButKeepsSavedOutputByDefault()
    {
        Install(Base, onRemoval: "keep");
        await SeedAsync(Base);
        await SeedAsync("tuvima.test.other");

        await _removal.RemoveAsync(Base, removeOutput: false, default);

        Assert.Null(_catalog.Get(Base));
        var footprint = await _data.GetFootprintAsync(Base);
        Assert.Equal(0, footprint.Records);
        Assert.Equal(0, footprint.RunRecords);
        Assert.Equal(2, footprint.SkipMarkers);
        Assert.Null(await new PluginEventCursorRepository(_kit.Database).GetAsync(Base));
        Assert.Empty(await new PluginLibraryLimitRepository(_kit.Database).GetAsync(Base));
        Assert.Empty(await _secrets.ListNamesAsync(SecretOwner.Plugin(Base)));
        Assert.Equal(2, (await _data.GetFootprintAsync("tuvima.test.other")).Records);
    }

    [Fact]
    public async Task Remove_WithTheOutputChoice_RemovesOnlyUnreviewedOutput()
    {
        Install(Base, onRemoval: "keep");
        await SeedAsync(Base);
        using (var connection = _kit.Database.CreateConnection())
        {
            connection.Execute("UPDATE playback_segments SET review_status = 'confirmed' WHERE id = (SELECT id FROM playback_segments WHERE plugin_id = @id LIMIT 1);", new { id = Base });
        }

        await _removal.RemoveAsync(Base, removeOutput: true, default);

        Assert.Equal(1, ConfirmedMarkers(Base));
        Assert.Equal(0, (await _data.GetFootprintAsync(Base)).SkipMarkers);
    }

    [Fact]
    public async Task Sweep_CleansLeftoversOfPluginsThatAreGone_ButNotInstalledOnes_AndKeepsSavedOutput()
    {
        Install(Base, onRemoval: "remove");
        await SeedAsync(Base);
        await SeedAsync("tuvima.test.gone");

        var swept = await _removal.SweepOrphansAsync(default);

        Assert.Equal(1, swept);
        Assert.Equal(0, (await _data.GetFootprintAsync("tuvima.test.gone")).Records);
        Assert.Equal(2, (await _data.GetFootprintAsync("tuvima.test.gone")).SkipMarkers);
        Assert.Equal(2, (await _data.GetFootprintAsync(Base)).Records);
    }

    [Fact]
    public async Task Delete_RefusesWhileAnotherPluginNeedsIt_UntilTheCallerAgrees()
    {
        Install(Base, onRemoval: "keep");
        Install(User, onRemoval: "keep", needs: Base);
        _catalog.SetEnabled(Base, true);
        _catalog.SetEnabled(User, true);

        var refused = await Execute(await PluginEndpoints.DeletePluginAsync(Base, null, _catalog, _removal, default));
        Assert.Equal(StatusCodes.Status409Conflict, refused.Status);
        Assert.NotNull(_catalog.Get(Base));

        var agreed = await Execute(await PluginEndpoints.DeletePluginAsync(
            Base, new PluginRemoveRequest { AlsoSwitchOffDependants = true }, _catalog, _removal, default));
        Assert.Equal(StatusCodes.Status200OK, agreed.Status);
        Assert.Null(_catalog.Get(Base));
        Assert.False(_catalog.Get(User)!.Enabled);
    }

    [Fact]
    public async Task Delete_WithAnOutputChoiceThatIsNeitherKeepNorRemove_IsRefused()
    {
        Install(Base, onRemoval: "keep");

        var outcome = await Execute(await PluginEndpoints.DeletePluginAsync(
            Base, new PluginRemoveRequest { Output = "maybe" }, _catalog, _removal, default));

        Assert.Equal(StatusCodes.Status400BadRequest, outcome.Status);
        Assert.NotNull(_catalog.Get(Base));
    }

    [Fact]
    public async Task Delete_OfAPluginThatIsNotInstalled_IsNotFound()
    {
        var outcome = await Execute(await PluginEndpoints.DeletePluginAsync("tuvima.test.nope", null, _catalog, _removal, default));

        Assert.Equal(StatusCodes.Status404NotFound, outcome.Status);
    }

    private int ConfirmedMarkers(string pluginId)
    {
        using var connection = _kit.Database.CreateConnection();
        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM playback_segments WHERE plugin_id = @pluginId AND review_status = 'confirmed';", new { pluginId });
    }

    private void Install(string id, string onRemoval, string? needs = null)
    {
        var directory = Path.Combine(_root, "library", ".data", "plugins", id);
        Directory.CreateDirectory(directory);
        var testAssemblyPath = Assembly.GetExecutingAssembly().Location;
        File.Copy(testAssemblyPath, Path.Combine(directory, Path.GetFileName(testAssemblyPath)), overwrite: true);
        var requires = needs is null ? "" : $", \"requires\": [{{ \"kind\": \"plugin\", \"id\": \"{needs}\" }}]";
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "name": "Removal {{id}}",
              "version": "1.0.0",
              "description": "Loaded from plugin.json.",
              "entry_assembly": "MediaEngine.Api.Tests.dll",
              "entry_type": "MediaEngine.Api.Tests.PluginCatalogTests+DynamicTestPlugin",
              "permissions": ["secrets.plugin"],
              "secrets": [{ "name": "api_token", "label": "Token", "description": "Key." }],
              "declaration": { "stores": { "records": true, "saved_output": "skip markers", "on_removal": "{{onRemoval}}" }{{requires}} }
            }
            """);
        _catalog.Reload();
    }

    private async Task SeedAsync(string pluginId)
    {
        var records = new PluginRecordRepository(_kit.Database);
        var limits = new PluginRecordLimits(65536, 1000, 1_000_000);
        await records.PutAsync(pluginId, "state", "one", "{\"a\":1}", DateTimeOffset.UtcNow, limits);
        await records.PutAsync(pluginId, "state", "two", "{\"b\":2}", DateTimeOffset.UtcNow, limits);
        await new PluginEventCursorRepository(_kit.Database).SetAsync(pluginId, 7, DateTimeOffset.UtcNow);
        await new PluginLibraryLimitRepository(_kit.Database).ReplaceAsync(pluginId, ["lib-1"]);
        await _secrets.SetAsync(SecretOwner.Plugin(pluginId), new Dictionary<string, string> { ["api_token"] = "fake-value-for-tests" });
        using var connection = _kit.Database.CreateConnection();
        connection.Execute("PRAGMA foreign_keys = OFF;");
        connection.Execute(
            "INSERT INTO scheduled_task_runs (id, task_key, plugin_id, started_at, outcome) VALUES (@id, 'job', @pluginId, '2026-10-28T00:00:00Z', 'succeeded');",
            new { id = Guid.NewGuid().ToByteArray(), pluginId });
        for (var i = 0; i < 2; i++)
        {
            connection.Execute(
                """
                INSERT INTO playback_segments (id, asset_id, kind, start_seconds, end_seconds, confidence, source, plugin_id, is_skippable, review_status, created_at, updated_at)
                VALUES (@id, @asset, 'commercial', 0, 30, 0.9, 'detector', @pluginId, 1, 'detected', '2026-10-28T00:00:00Z', '2026-10-28T00:00:00Z');
                """,
                new { id = Guid.NewGuid().ToByteArray(), asset = Guid.NewGuid().ToByteArray(), pluginId });
        }
    }

    private static async Task<(int Status, string Body)> Execute(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }
}
