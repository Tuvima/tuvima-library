using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Services;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A plain kind a plugin supplies (lookup "none") is plain everywhere, even though the libraries file says "enriched": ingestion, the
/// lookup guard and the identity-bypass list all read the merged list of kinds, and a change to the plugin's kind takes effect at once
/// (Library setup D38).
/// </summary>
public sealed class PluginPlainKindLookupTests : IDisposable
{
    private const string LibraryId = "f1f1f1f1-0000-4000-8000-000000000001";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-plain-kind", Guid.NewGuid().ToString("N"));
    private readonly string _pluginRoot;
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PluginCatalog _catalog;
    private readonly LibraryKindListService _kinds;
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;

    public PluginPlainKindLookupTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        _pluginRoot = Path.Combine(libraryRoot, ".data", "plugins");
        Directory.CreateDirectory(_pluginRoot);
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _loader.SaveCore(new CoreConfiguration
        {
            LibraryRoot = libraryRoot,
            OrganizationTemplates = new(StringComparer.OrdinalIgnoreCase) { ["default"] = "{Category}/{Title} ({Year})/{Title}{Ext}" },
        });
        _catalog = new PluginCatalog([], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        _kinds = new LibraryKindListService(_loader, _catalog);

        // The file says "enriched" with the YouTube lookup on: only the kind can stop the lookup.
        var baseline = new LibrariesConfiguration
        {
            StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Library data", Path = libraryRoot, AllowWrite = true }],
            ViewStorage = new ViewStorageConfig { StorageLocationId = "media", RelativeRoot = "View" },
            Libraries =
        [
            new LibraryFolderConfig
            {
                Id = LibraryId,
                Name = "Shows",
                Category = "Music",
                Kind = LibraryKinds.Catalogued,
                KindKey = "podcasts",
                Area = LibraryAreas.Listen,
                MediaTypes = ["Music"],
                MetadataPolicy = LibraryMetadataPolicies.Enriched,
                YoutubeLookup = true,
            },
        ],
        };
        _loader.SaveLibraries(baseline);

        _databasePath = Path.Combine(_root, "library.db");
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
    }

    public void Dispose()
    {
        _database.Dispose();
        DatabaseConnection.ReleasePooledConnections(_databasePath);
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
    public void APluginsPlainKind_IsBlockedByTheGuardAndListedAsBypassing_WhetherThePluginIsOnOrOff()
    {
        // With the plugin off its kind is unknown, and an unknown kind fails closed: nothing is looked up for it.
        var guard = Guard(_kinds);
        var bypass = new ConfiguredIdentityBypassLibraries(_loader, _kinds);
        Assert.True(guard.BlocksLibrary(LibraryId));
        Assert.Equal([LibraryId], bypass.GetLibraryIds().ToArray());

        AddPlugin(Lookup("\"none\""));
        _catalog.SetEnabled("json.podcasts", true);

        Assert.True(guard.BlocksLibrary(LibraryId));
        Assert.Equal([LibraryId], guard.GetBlockedLibraryIds().ToArray());
        Assert.Equal([LibraryId], bypass.GetLibraryIds().ToArray());
    }

    [Fact]
    public void WithoutTheMergedList_APluginKindIsUnknown_SoItFailsClosedAndLooksNothingUp()
    {
        AddPlugin(Lookup("{ \"providers\": [\"apple_api\"] }"));
        _catalog.SetEnabled("json.podcasts", true);

        Assert.True(Guard(kindSource: null).BlocksLibrary(LibraryId));
        Assert.False(Guard(_kinds).BlocksLibrary(LibraryId));
    }

    [Fact]
    public void APluginKindChangedToLookupNone_TakesEffectAtOnce_AndRefreshesTheIngestionFolderList()
    {
        AddPlugin(Lookup("{ \"providers\": [\"apple_api\"] }"));
        _catalog.SetEnabled("json.podcasts", true);
        var guard = Guard(_kinds);
        using var changes = new LibrariesOptionsChangeTokenSource(_loader, _kinds);
        var token = changes.GetChangeToken();
        Assert.False(guard.BlocksLibrary(LibraryId));

        AddPlugin(Lookup("\"none\""));

        Assert.True(guard.BlocksLibrary(LibraryId));
        Assert.True(token.HasChanged);
    }

    [Fact]
    public void IngestionBuildsThePluginsPlainKindAsLocalOnly_AndLogsOneWarningNamingTheLibrary()
    {
        AddPlugin(Lookup("\"none\""));
        _catalog.SetEnabled("json.podcasts", true);
        var log = new CapturingLogger();

        var entry = Assert.Single(LibraryFolderEntryFactory.CreateAll(_loader.LoadLibraries().Libraries, _kinds.GetKinds(), log));

        Assert.True(entry.BypassesExternalIdentity);
        Assert.False(entry.AsksYoutube);
        Assert.Equal(LibraryMetadataPolicies.LocalOnly, entry.MetadataPolicy);
        var warning = Assert.Single(log.Warnings);
        Assert.Contains("The library \"Shows\"", warning, StringComparison.Ordinal);
        Assert.Contains("metadata_policy is \"enriched\"", warning, StringComparison.Ordinal);
        Assert.Contains("youtube_lookup is true", warning, StringComparison.Ordinal);
    }

    private ConfiguredExternalLookupGuard Guard(MediaEngine.Domain.Contracts.ILibraryKindSource? kindSource) =>
        new(_loader, new MediaAssetRepository(_database), NullLogger<ConfiguredExternalLookupGuard>.Instance, kindSource);

    private static string Lookup(string lookup) => $$"""
        "declarative": { "library_kinds": [{
          "key": "podcasts", "area": "listen", "label": "Podcasts", "icon": "Podcasts", "format": "Music", "lookup": {{lookup}},
          "grouping": "folder_and_tags", "naming_template": "default", "misfit": "keep", "tooltip": "Episodes of shows you follow."
        }] }
        """;

    private void AddPlugin(string fields)
    {
        var directory = Path.Combine(_pluginRoot, "json.podcasts");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            {
              "id": "json.podcasts",
              "name": "Podcasts",
              "version": "1.0.0",
              "kind": "declarative",
              {{fields}}
            }
            """);
        _catalog.Reload();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
