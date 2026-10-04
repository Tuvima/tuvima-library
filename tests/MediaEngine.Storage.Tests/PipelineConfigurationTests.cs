using System.Text.Json;
using MediaEngine.Storage;

namespace MediaEngine.Storage.Tests;

public sealed class PipelineConfigurationTests
{
    [Fact]
    public void RepositoryConfiguration_LoadsAllConfiguredPipelinesAndProviders()
    {
        var configDirectory = Path.GetDirectoryName(FindRepoFile("config", "pipelines.json"))!;
        using var loader = new ConfigurationDirectoryLoader(configDirectory);

        var pipelines = loader.LoadPipelines();
        var providers = loader.LoadAllProviders();

        Assert.NotEmpty(pipelines.Pipelines);
        Assert.Contains(providers, provider => provider.Name == "musicbrainz");
        Assert.Contains(providers, provider => provider.Name == "apple_api");
    }

    [Fact]
    public void MediaPipelines_PreferWikidataDescriptionsBeforeRetailFallbacks()
    {
        var configPath = FindRepoFile("config", "pipelines.json");
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));

        var expectedRetailFallbacks = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Books"] = ["apple_api"],
            ["Audiobooks"] = ["apple_api"],
            ["Music"] = ["apple_api"],
            ["Movies"] = ["tmdb"],
            ["TV"] = ["tvdb"],
            ["Comics"] = ["comicvine"],
        };

        foreach (var (mediaType, fallbacks) in expectedRetailFallbacks)
        {
            var priorities = ReadPriority(document, mediaType, "description");

            Assert.NotEmpty(priorities);
            Assert.Equal("wikidata_reconciliation", priorities[0]);
            Assert.Equal(fallbacks, priorities.Skip(1).ToArray());
        }

        Assert.Equal(
            ["wikidata_reconciliation", "tvdb"],
            ReadPriority(document, "TV", "episode_description"));
    }

    [Fact]
    public void BooksPipeline_UsesAppleAsItsOnlyRetailIdentityProvider()
    {
        using var pipelines = JsonDocument.Parse(File.ReadAllText(FindRepoFile("config", "pipelines.json")));
        var providers = pipelines.RootElement
            .GetProperty("Books")
            .GetProperty("providers")
            .EnumerateArray()
            .OrderBy(element => element.GetProperty("rank").GetInt32())
            .Select(element => element.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Equal(["apple_api"], providers);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(FindRepoFile("config", "pipelines.json"))!, "providers", "open_library.json")));
    }

    [Fact]
    public void AudiobookPipeline_PreservesCanonicalBookIdentityWithRetailFallback()
    {
        var configPath = FindRepoFile("config", "pipelines.json");
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));

        Assert.Equal(["wikidata_reconciliation", "apple_api"], ReadPriority(document, "Audiobooks", "title"));
        Assert.Equal(["wikidata_reconciliation", "apple_api"], ReadPriority(document, "Audiobooks", "author"));
        Assert.Equal(["apple_api"], ReadPriority(document, "Audiobooks", "series"));
        Assert.Equal(["apple_api"], ReadPriority(document, "Audiobooks", "narrator"));
        Assert.Equal(["apple_api"], ReadPriority(document, "Audiobooks", "cover"));
    }

    [Fact]
    public void MediaPipelines_AssignSequenceFieldsToMediaSpecificProviders()
    {
        var configPath = FindRepoFile("config", "pipelines.json");
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));

        Assert.Equal(["tmdb", "wikidata_reconciliation"], ReadPriority(document, "Movies", "series"));
        Assert.Equal(["tmdb", "wikidata_reconciliation"], ReadPriority(document, "Movies", "series_position"));
        Assert.Equal(["tmdb", "wikidata_reconciliation"], ReadPriority(document, "Movies", "sequence_total"));

        Assert.Equal(["tvdb", "wikidata_reconciliation"], ReadPriority(document, "TV", "episode_number"));
        Assert.Equal(["wikidata_reconciliation"], ReadPriority(document, "TV", "episode_count"));
        Assert.Equal(["wikidata_reconciliation"], ReadPriority(document, "TV", "sequence_total"));

        Assert.Equal(["comicvine", "wikidata_reconciliation"], ReadPriority(document, "Comics", "series"));
        Assert.Equal(["comicvine", "wikidata_reconciliation"], ReadPriority(document, "Comics", "issue_number"));
        Assert.Equal(["comicvine", "local_processor"], ReadPriority(document, "Comics", "issue_title"));
        Assert.Equal(["comicvine", "local_processor"], ReadPriority(document, "Comics", "issue_description"));
        Assert.Equal(["comicvine"], ReadPriority(document, "Comics", "issue_source_url"));
        Assert.Equal(["comicvine", "wikidata_reconciliation"], ReadPriority(document, "Comics", "sequence_total"));

        Assert.Equal(["musicbrainz", "apple_api"], ReadPriority(document, "Music", "title"));
        Assert.Equal(["musicbrainz", "apple_api"], ReadPriority(document, "Music", "author"));
        Assert.Equal(["musicbrainz", "apple_api"], ReadPriority(document, "Music", "artist"));
        Assert.Equal(["apple_api"], ReadPriority(document, "Music", "track_number"));
        Assert.Equal(["apple_api"], ReadPriority(document, "Music", "disc_number"));
        Assert.Equal(["apple_api", "musicbrainz"], ReadPriority(document, "Music", "cover"));
        Assert.Equal(["musicbrainz", "apple_api"], ReadPriority(document, "Music", "album"));
        Assert.Equal(["musicbrainz", "apple_api"], ReadPriority(document, "Music", "year"));
        Assert.Equal(["musicbrainz", "apple_api"], ReadPriority(document, "Music", "track_count"));
        Assert.Equal(["musicbrainz", "apple_api"], ReadPriority(document, "Music", "sequence_total"));
    }

    [Fact]
    public void MusicBrainz_IsConfiguredBeforeAppleForStageOneMusicIdentity()
    {
        using var pipelines = JsonDocument.Parse(File.ReadAllText(FindRepoFile("config", "pipelines.json")));
        using var provider = JsonDocument.Parse(File.ReadAllText(FindRepoFile("config", "providers", "musicbrainz.json")));

        var musicPipeline = pipelines.RootElement
            .GetProperty("Music")
            .GetProperty("providers");
        var musicProviders = musicPipeline
            .EnumerateArray()
            .Select(element => element.GetProperty("name").GetString() ?? "")
            .ToArray();
        var musicPurposes = musicPipeline
            .EnumerateArray()
            .Select(element => element.GetProperty("purpose").GetString() ?? "")
            .ToArray();
        var appleEntry = musicPipeline
            .EnumerateArray()
            .Single(element => string.Equals(element.GetProperty("name").GetString(), "apple_api", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Sequential", pipelines.RootElement.GetProperty("Music").GetProperty("strategy").GetString());
        Assert.Equal(["musicbrainz", "apple_api"], musicProviders);
        Assert.Equal(["identity", "enrichment"], musicPurposes);
        Assert.True(appleEntry.GetProperty("requires_identity").GetBoolean());
        Assert.True(appleEntry.GetProperty("use_as_identity_fallback").GetBoolean());
        Assert.Equal(
            "musicbrainz",
            appleEntry.GetProperty("accepted_transition").GetProperty("provider").GetString());
        var strategies = provider.RootElement
            .GetProperty("search_strategies")
            .EnumerateArray()
            .Select(element => element.GetProperty("name").GetString() ?? "")
            .ToArray();
        Assert.Equal("recording_id_lookup", strategies[0]);
        Assert.Equal("isrc_lookup", strategies[1]);
        Assert.Contains("recording_album_only_search", strategies);
        Assert.Equal("recording_identity_search", strategies[^1]);
        Assert.True(provider.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal([1, 3], provider.RootElement.GetProperty("hydration_stages").EnumerateArray().Select(element => element.GetInt32()).ToArray());
        Assert.Contains("musicbrainz_release_group_id", provider.RootElement.GetProperty("preferred_bridge_ids").GetProperty("Music").EnumerateArray().Select(element => element.GetString()));
    }

    [Fact]
    public void PublicProviderLinks_AreDeclaredInProviderConfiguration()
    {
        using var musicBrainz = JsonDocument.Parse(File.ReadAllText(FindRepoFile("config", "providers", "musicbrainz.json")));
        using var wikidata = JsonDocument.Parse(File.ReadAllText(FindRepoFile("config", "providers", "wikidata_reconciliation.json")));

        var musicLinks = musicBrainz.RootElement.GetProperty("ui_metadata").GetProperty("external_links");
        Assert.Equal(
            "https://musicbrainz.org/release/{value}",
            musicLinks.GetProperty("musicbrainz_release_id").GetProperty("url_template").GetString());
        Assert.Equal(
            "https://musicbrainz.org/release-group/{value}",
            musicLinks.GetProperty("musicbrainz_release_group_id").GetProperty("url_template").GetString());

        var authorityLinks = wikidata.RootElement.GetProperty("ui_metadata").GetProperty("external_links");
        Assert.Equal(
            "https://www.wikidata.org/wiki/{value}",
            authorityLinks.GetProperty("wikidata_qid").GetProperty("url_template").GetString());
        Assert.Equal(
            "{value}",
            authorityLinks.GetProperty("wikipedia_url").GetProperty("url_template").GetString());
    }

    [Fact]
    public void VisibleProviders_DeclareStableManagementCapabilities()
    {
        var providerDirectory = Path.GetDirectoryName(FindRepoFile("config", "providers", "tmdb.json"))!;
        var visibleProviders = Directory.GetFiles(providerDirectory, "*.json")
            .Where(path => !path.EndsWith("local_filesystem.json", StringComparison.OrdinalIgnoreCase));

        foreach (var path in visibleProviders)
        {
            using var provider = JsonDocument.Parse(File.ReadAllText(path));
            var capabilities = provider.RootElement.GetProperty("provider_capabilities")
                .EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.NotEmpty(capabilities);
            Assert.DoesNotContain(capabilities, string.IsNullOrWhiteSpace);
        }

        using var wikidata = JsonDocument.Parse(File.ReadAllText(FindRepoFile("config", "providers", "wikidata_reconciliation.json")));
        var ui = wikidata.RootElement.GetProperty("ui_metadata");
        Assert.Equal("canonical_source", ui.GetProperty("system_role").GetString());
        Assert.True(ui.GetProperty("required_system_provider").GetBoolean());
    }

    [Fact]
    public void SourcePriorityDefaults_IncludeEveryConfigurableFieldChain()
    {
        using var active = JsonDocument.Parse(File.ReadAllText(FindRepoFile("config", "pipelines.json")));
        using var defaults = JsonDocument.Parse(File.ReadAllText(FindRepoFile("config", "pipeline-priority-defaults.json")));

        foreach (var media in active.RootElement.EnumerateObject())
        {
            var defaultFields = defaults.RootElement.GetProperty(media.Name).GetProperty("field_priorities");
            foreach (var field in media.Value.GetProperty("field_priorities").EnumerateObject())
            {
                Assert.True(defaultFields.TryGetProperty(field.Name, out _), $"Default priority is missing {media.Name}.{field.Name}.");
            }
        }
    }

    [Fact]
    public void MusicBridgePriority_PrefersMusicBrainzIdsBeforeAppleIds()
    {
        using var provider = JsonDocument.Parse(File.ReadAllText(FindRepoFile("config", "providers", "musicbrainz.json")));
        using var wikidata = JsonDocument.Parse(File.ReadAllText(FindRepoFile("config", "providers", "wikidata_reconciliation.json")));

        var preferred = provider.RootElement
            .GetProperty("preferred_bridge_ids")
            .GetProperty("Music")
            .EnumerateArray()
            .Select(element => element.GetString() ?? "")
            .ToArray();

        Assert.Equal("musicbrainz_recording_id", preferred[0]);
        Assert.Contains("musicbrainz_recording_id", preferred);
        Assert.DoesNotContain("apple_music_id", preferred);

        var musicTrackScope = wikidata.RootElement
            .GetProperty("bridge_resolution")
            .GetProperty("scopes")
            .GetProperty("MusicTrack");
        var targetIds = musicTrackScope
            .GetProperty("target_ids")
            .EnumerateArray()
            .Select(element => element.GetString() ?? "")
            .ToArray();
        var contextIds = musicTrackScope
            .GetProperty("context_ids")
            .EnumerateArray()
            .Select(element => element.GetString() ?? "")
            .ToArray();

        Assert.Equal(["musicbrainz_recording_id", "musicbrainz_work_id", "isrc", "apple_music_id"], targetIds);
        Assert.Contains("musicbrainz_release_group_id", contextIds);
        Assert.Contains("apple_music_collection_id", contextIds);
        Assert.False(musicTrackScope.GetProperty("allow_constrained_text_fallback").GetBoolean());

        var labels = wikidata.RootElement
            .GetProperty("data_extension")
            .GetProperty("property_labels");
        Assert.Equal("open_library_id", labels.GetProperty("P648").GetString());
        Assert.Equal("musicbrainz_release_group_id", labels.GetProperty("P436").GetString());
        Assert.Equal("musicbrainz_recording_id", labels.GetProperty("P4404").GetString());
        Assert.Equal("apple_music_id", labels.GetProperty("P10110").GetString());
    }

    [Fact]
    public void HydrationConfig_DeclaresCollectionRollupRelationshipTypes()
    {
        using var hydration = JsonDocument.Parse(File.ReadAllText(FindRepoFile("config", "hydration.json")));
        var types = hydration.RootElement
            .GetProperty("collection_rollup_relationship_types")
            .EnumerateArray()
            .Select(element => element.GetString() ?? "")
            .ToArray();

        Assert.Equal(["series", "franchise", "fictional_universe", "based_on"], types);
    }

    [Fact]
    public void Documentation_DescribesMusicBrainzFirstMusicIdentity()
    {
        var providerGuide = File.ReadAllText(FindRepoFile("docs", "guides", "configuring-providers.md"));
        var architecture = File.ReadAllText(FindRepoFile("docs", "architecture", "ingestion-identity-enrichment-pipeline.md"));
        var mediaTypes = File.ReadAllText(FindRepoFile("docs", "reference", "media-types.md"));

        Assert.Contains("MusicBrainz tries identifiers and staged text searches first", providerGuide, StringComparison.Ordinal);
        Assert.Contains("For music, Stage 1 is a bounded configured flow", architecture, StringComparison.Ordinal);
        Assert.Contains("MusicBrainz - Stage 1 configured identifier lookup", mediaTypes, StringComparison.Ordinal);
        Assert.Contains("Apple API - Stage 1 enrichment or fallback identity", mediaTypes, StringComparison.Ordinal);
    }

    private static string[] ReadPriority(JsonDocument document, string mediaType, string field)
    {
        var root = document.RootElement;
        Assert.True(root.TryGetProperty(mediaType, out var mediaConfig), $"{mediaType} pipeline is missing");
        Assert.True(mediaConfig.TryGetProperty("field_priorities", out var priorities), $"{mediaType} priorities are missing");
        Assert.True(priorities.TryGetProperty(field, out var fieldPriorities), $"{mediaType}.{field} priority is missing");

        return fieldPriorities
            .EnumerateArray()
            .Select(element => element.GetString() ?? "")
            .ToArray();
    }

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(parts)} from {AppContext.BaseDirectory}");
    }
}
