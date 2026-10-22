using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The one list of library kinds the Dashboard draws every screen from: the kinds that ship with the Library, then the kinds of
/// switched-on JSON plugins. A plugin adds a kind as settings, not code, and a bad or colliding kind is refused with a sentence.
/// </summary>
public sealed class LibraryKindsApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-library-kinds", Guid.NewGuid().ToString("N"));
    private readonly string _pluginRoot;
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PluginCatalog _catalog;
    private readonly LibraryKindListService _kinds;

    public LibraryKindsApiTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        _pluginRoot = Path.Combine(libraryRoot, ".data", "plugins");
        Directory.CreateDirectory(_pluginRoot);
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _loader.SaveCore(new CoreConfiguration
        {
            LibraryRoot = libraryRoot,
            OrganizationTemplates = new(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = "{Category}/{Title} ({Year})/{Title}{Ext}",
                ["Music"] = "Music/{AlbumArtist}/{Album} ({Year})/{TrackNumber?} - {Title}{Ext}",
                ["TV"] = "TV/{Series} ({Year})/Season {Season}/{Series} - {EpisodeCode?} - {EpisodeTitle}{Ext}",
            },
        });
        _catalog = new PluginCatalog([], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        _kinds = new LibraryKindListService(_loader, _catalog);
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
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    [Fact]
    public void TheKindsListHoldsTheKindsThatShipInOrderWithTheNewKindsHidden()
    {
        var list = _kinds.GetDtos();

        Assert.Equal(
            ["movies", "tv", "youtube", "other_videos", "music", "audiobooks", "other_audio", "books", "comics", "other_reading", "photos", "personal_videos", "documents"],
            list.Select(kind => kind.Key).ToArray());
        Assert.Equal(["watch", "watch", "watch", "watch", "listen", "listen", "listen", "read", "read", "read", "view", "view", "view"], list.Select(kind => kind.Area).ToArray());
        Assert.Equal(["youtube", "other_audio", "other_reading"], list.Where(kind => !kind.Available).Select(kind => kind.Key).ToArray());
        Assert.All(list, kind =>
        {
            Assert.True(kind.BuiltIn);
            Assert.Null(kind.PluginId);
        });
        Assert.Equal(["Photos", "Personal videos", "Documents"], list.Where(kind => kind.Area == "view").Select(kind => kind.Label).ToArray());
        Assert.Null(list.Single(kind => kind.Key == "photos").Format);
        Assert.Equal(["tmdb", "subdl"], list.Single(kind => kind.Key == "movies").LookupProviders);
        Assert.Empty(list.Single(kind => kind.Key == "other_videos").LookupProviders);
    }

    [Fact]
    public void OnlyThePlainKindsAllowSeveralLibrariesAndAPluginKindCanAskForTheSame()
    {
        Assert.Equal(
            ["other_videos", "other_audio", "other_reading"],
            _kinds.GetDtos().Where(kind => kind.AllowsMultiple).Select(kind => kind.Key).ToArray());
        Assert.All(_kinds.GetDtos().Where(kind => kind.AllowsMultiple), kind => Assert.Equal(5, kind.MaxLibraries));
        Assert.Equal(1, _kinds.GetDtos().Single(kind => kind.Key == "movies").MaxLibraries);

        Add("json.podcasts", $$"""
            "declarative": { "library_kinds": [{{ReplaceField(PodcastsKind(), "misfit", "\"misfit\": \"keep\", \"allows_multiple\": true, \"max_libraries\": 3")}}] }
            """);
        _catalog.SetEnabled("json.podcasts", true);

        var podcasts = Assert.Single(_kinds.GetDtos(), kind => kind.Key == "podcasts");
        Assert.True(podcasts.AllowsMultiple);
        Assert.Equal(3, podcasts.MaxLibraries);
    }

    [Fact]
    public void AValidPodcastsKindAppearsOnceThePluginIsSwitchedOnAndGoesWhenItIsSwitchedOff()
    {
        Add("json.podcasts", PodcastsPlugin());
        Assert.DoesNotContain(_kinds.GetDtos(), kind => kind.Key == "podcasts");

        _catalog.SetEnabled("json.podcasts", true);
        var list = _kinds.GetDtos();

        var podcasts = Assert.Single(list, kind => kind.Key == "podcasts");
        Assert.Equal("listen", podcasts.Area);
        Assert.Equal("Podcasts", podcasts.Label);
        Assert.Equal("Music", podcasts.Format);
        Assert.False(podcasts.BuiltIn);
        Assert.Equal("json.podcasts", podcasts.PluginId);
        Assert.True(podcasts.Available);
        Assert.Empty(podcasts.LookupProviders);
        // It sits in its area after the kinds that ship, which keep their places.
        Assert.Equal(["music", "audiobooks", "other_audio", "podcasts"], list.Where(kind => kind.Area == "listen").Select(kind => kind.Key).ToArray());

        _catalog.SetEnabled("json.podcasts", false);

        Assert.DoesNotContain(_kinds.GetDtos(), kind => kind.Key == "podcasts");
        Assert.Equal(13, _kinds.GetDtos().Count);
    }

    [Fact]
    public void AKindThatLooksThingsUpFromThePluginsOwnSourceNamesTheSourceTheLibraryKnowsItBy()
    {
        Add("json.recorded", """
            "permissions": ["network.http"],
            "network": [{ "host": "api.example.com", "sends": "Search words" }],
            "declarative": {
              "metadata_sources": [{ "name": "my_source", "adapter_type": "config_driven", "version": "1.0", "endpoints": { "api": "https://api.example.com" } }],
              "library_kinds": [{ "key": "recorded_tv", "area": "watch", "label": "Recorded TV", "icon": "Tv", "format": "TV",
                "lookup": { "providers": ["my_source", "apple_api"] }, "grouping": "tv_show_season", "naming_template": "TV", "misfit": "unmatched" }]
            }
            """);
        _catalog.SetEnabled("json.recorded", true);

        var recorded = Assert.Single(_kinds.GetDtos(), kind => kind.Key == "recorded_tv");

        Assert.Equal([PluginMetadataSourceFiles.ProviderNameFor("json.recorded", "my_source"), "apple_api"], recorded.LookupProviders);
    }

    [Theory]
    [InlineData("\"area\": \"radio\"", "area", "\"area\" that does not exist")]
    [InlineData("\"grouping\": \"by_mood\"", "grouping", "\"grouping\" of \"by_mood\"")]
    [InlineData("\"naming_template\": \"nowhere\"", "naming_template", "\"naming_template\" of \"nowhere\"")]
    [InlineData("\"format\": \"Podcasts\"", "format", "\"format\" of \"Podcasts\"")]
    [InlineData("\"lookup\": { \"providers\": [\"nowhere\"] }", "lookup", "looks details up from \"nowhere\"")]
    [InlineData("\"misfit\": \"delete\"", "misfit", "\"misfit\" of \"delete\"")]
    [InlineData("\"key\": \"Not A Key\"", "key", "needs a \"key\"")]
    [InlineData("\"built_in\": true", "built_in", "unknown field \"built_in\"")]
    public void AnInvalidKindIsRefusedWithASentenceAndNeverListed(string replacement, string field, string expected)
    {
        var kind = ReplaceField(PodcastsKind(), field, replacement);
        Add("json.bad", $$"""
            "declarative": { "library_kinds": [{{kind}}] }
            """);

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("json.bad"));

        Assert.NotNull(plugin.LoadError);
        Assert.Contains(expected, plugin.LoadError, StringComparison.Ordinal);
        Assert.False(plugin.Enabled);
        Assert.Throws<PluginCannotBeEnabledException>(() => _catalog.SetEnabled("json.bad", true));
        Assert.DoesNotContain(_kinds.GetDtos(), item => item.Key == "podcasts");
    }

    [Fact]
    public void AKindMissingARequiredFieldIsRefusedNamingIt()
    {
        Add("json.bad", """
            "declarative": { "library_kinds": [{ "key": "podcasts", "area": "listen", "label": "Podcasts" }] }
            """);

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("json.bad"));

        Assert.Contains("needs \"icon\"", plugin.LoadError, StringComparison.Ordinal);
    }

    [Fact]
    public void AKindWhoseKeyIsOneThatShipsIsRefusedWithASentenceOnThePluginsPage()
    {
        Add("json.music", $$"""
            "declarative": { "library_kinds": [{{ReplaceField(PodcastsKind(), "key", "\"key\": \"music\"")}}] }
            """);

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("json.music"));

        Assert.Contains("\"music\"", plugin.LoadError, StringComparison.Ordinal);
        Assert.Contains("ships with the Library", plugin.LoadError, StringComparison.Ordinal);
        Assert.False(plugin.Enabled);
        Assert.Throws<PluginCannotBeEnabledException>(() => _catalog.SetEnabled("json.music", true));
        Assert.Equal(13, _kinds.GetDtos().Count);
    }

    [Fact]
    public void AKindWhoseKeyAnotherSwitchedOnPluginUsesIsRefusedAndTheRunningPluginKeepsIt()
    {
        Add("json.podcasts-a", PodcastsPlugin());
        _catalog.SetEnabled("json.podcasts-a", true);

        Add("json.podcasts-b", PodcastsPlugin());

        var second = Assert.IsType<PluginRegistration>(_catalog.Get("json.podcasts-b"));
        Assert.Contains("already uses that key", second.LoadError, StringComparison.Ordinal);
        Assert.Contains("json.podcasts-a", second.LoadError ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("json.podcasts-a", Assert.Single(_kinds.GetDtos(), kind => kind.Key == "podcasts").PluginId);
        Assert.Null(_catalog.Get("json.podcasts-a")!.LoadError);
    }

    [Fact]
    public void TheApprovalStatementListsTheKindsAPluginAddsInPlainWords()
    {
        Add("json.podcasts", PodcastsPlugin());
        var manifest = _catalog.Get("json.podcasts")!.Manifest;

        var statement = ApprovalStatementBuilder.ForPlugin(manifest, PluginTrustLevel.Private);

        Assert.Contains("Adds the library type 'Podcasts' under Listen, with no lookup (files are shown by folder and file name)", statement.CanDo);
    }

    [Fact]
    public void TheApprovalStatementNamesWhereAKindLooksThingsUp()
    {
        Add("json.recorded", """
            "declarative": { "library_kinds": [{ "key": "recorded_tv", "area": "watch", "label": "Recorded TV", "icon": "Tv", "format": "TV",
                "lookup": { "providers": ["apple_api"] }, "grouping": "tv_show_season", "naming_template": "TV", "misfit": "unmatched" }] }
            """);

        var statement = ApprovalStatementBuilder.ForPlugin(_catalog.Get("json.recorded")!.Manifest, PluginTrustLevel.Private);

        Assert.Contains("Adds the library type 'Recorded TV' under Watch, looking details up from apple_api", statement.CanDo);
    }

    [Fact]
    public void ALibraryMustNameAKindInTheFullListSoAPluginKindCountsOnlyWhileItsPluginIsOn()
    {
        var configuration = new LibrariesConfiguration
        {
            Libraries =
            [
                new LibraryFolderConfig { Id = Guid.NewGuid().ToString("D"), Name = "Podcasts", Kind = LibraryKinds.Catalogued, KindKey = "podcasts", MediaTypes = ["Music"] },
            ],
        };
        Add("json.podcasts", PodcastsPlugin());

        Assert.Contains("\"podcasts\"", _kinds.ValidateLibraries(configuration), StringComparison.Ordinal);

        _catalog.SetEnabled("json.podcasts", true);
        Assert.Null(_kinds.ValidateLibraries(configuration));

        configuration.Libraries[0].MediaTypes = ["Books"];
        Assert.Contains("must include Music", _kinds.ValidateLibraries(configuration), StringComparison.Ordinal);
    }

    private static string PodcastsKind() => """
        {
          "key": "podcasts", "area": "listen", "label": "Podcasts", "icon": "Podcasts", "format": "Music", "lookup": "none",
          "grouping": "folder_and_tags", "naming_template": "default", "misfit": "keep", "tooltip": "Episodes of shows you follow."
        }
        """;

    private static string PodcastsPlugin() => $$"""
        "declarative": { "library_kinds": [{{PodcastsKind()}}] }
        """;

    // Swaps one field of the Podcasts kind for another text, or adds it when the kind has no such field.
    private static string ReplaceField(string kind, string field, string replacement)
    {
        var lines = kind.Split('\n').ToList();
        var index = lines.FindIndex(line => line.Contains($"\"{field}\":", StringComparison.Ordinal));
        if (index < 0)
        {
            return kind.Replace("{", "{ " + replacement + ",", StringComparison.Ordinal);
        }

        // A line can hold several fields; take out just this one (up to the next comma at the same level).
        var text = lines[index];
        var start = text.IndexOf($"\"{field}\":", StringComparison.Ordinal);
        var end = FieldEnd(text, start);
        lines[index] = text[..start] + replacement + text[end..];
        return string.Join('\n', lines);
    }

    private static int FieldEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (!inString)
            {
                if (c is '{' or '[')
                {
                    depth++;
                }
                else if (c is '}' or ']')
                {
                    if (depth == 0)
                    {
                        return i;
                    }

                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    return i;
                }
            }
        }

        return text.Length;
    }

    private void Add(string id, string fields)
    {
        var directory = Path.Combine(_pluginRoot, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "name": "{{id}}",
              "version": "1.0.0",
              "kind": "declarative",
              {{fields}}
            }
            """);
        _catalog.Reload();
    }
}
