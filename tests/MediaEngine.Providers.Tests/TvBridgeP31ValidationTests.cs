using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Adapters;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Regression for the Solo Leveling first-run failure: the show-scope TheTVDB id (389597)
/// resolved Wikidata Q112898063 (P31 = Q63952888 "anime television series"), but the Stage 2
/// P31 media-type validation rejected it because Q63952888 was not in the TV allow-list, and the
/// singleton adapter kept the startup config so a later allow-list edit never took effect.
/// </summary>
public sealed class TvBridgeP31ValidationTests : IDisposable
{
    private const string SoloLevelingSeriesQid = "Q112898063";
    private const string AnimeTelevisionSeriesClass = "Q63952888";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "tuvima-p31-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void ShippedConfig_AcceptsAnimeTelevisionSeriesForTvShowScope()
    {
        var adapter = CreateAdapter(LoadShippedConfig(), loader: null);

        // Solo Leveling: bridge tvdb_id=389597 -> Q112898063, P31 = Q63952888.
        Assert.True(ValidateP31(adapter, [AnimeTelevisionSeriesClass], SoloLevelingSeriesQid, MediaType.TV, "TV"));
    }

    [Fact]
    public void ShippedConfig_StillRejectsNonTelevisionClassesForTvShowScope()
    {
        var adapter = CreateAdapter(LoadShippedConfig(), loader: null);

        Assert.False(ValidateP31(adapter, ["Q5"], "Q1", MediaType.TV, "TV"));
        Assert.False(ValidateP31(adapter, ["Q7725634"], "Q2", MediaType.TV, "TV"));
    }

    [Fact]
    public void StartupConfigWithoutAnimeClass_RejectsSoloLevelingSeries()
    {
        var startup = LoadShippedConfig();
        startup.InstanceOfClasses["TV"].Remove(AnimeTelevisionSeriesClass);
        var adapter = CreateAdapter(startup, loader: null);

        Assert.False(ValidateP31(adapter, [AnimeTelevisionSeriesClass], SoloLevelingSeriesQid, MediaType.TV, "TV"));
    }

    [Fact]
    public void HotReloadedAllowList_IsHonouredBySingletonAdapter()
    {
        // The Engine builds the adapter once from the config present at startup; the allow-list
        // edit happens afterwards on disk (config hot reload).
        var startup = LoadShippedConfig();
        startup.InstanceOfClasses["TV"].Remove(AnimeTelevisionSeriesClass);

        var providersDir = Path.Combine(_tempDir, "providers");
        Directory.CreateDirectory(providersDir);
        var configPath = Path.Combine(providersDir, "wikidata_reconciliation.json");
        File.WriteAllText(configPath, SerializeWithoutTvClass(AnimeTelevisionSeriesClass));

        var adapter = CreateAdapter(startup, new ConfigurationDirectoryLoader(_tempDir));
        Assert.False(ValidateP31(adapter, [AnimeTelevisionSeriesClass], SoloLevelingSeriesQid, MediaType.TV, "TV"));

        // Config edited on disk: Q63952888 added to the TV list.
        File.WriteAllText(configPath, File.ReadAllText(ShippedConfigPath));

        Assert.True(ValidateP31(adapter, [AnimeTelevisionSeriesClass], SoloLevelingSeriesQid, MediaType.TV, "TV"));
    }

    private static string SerializeWithoutTvClass(string qid)
    {
        var root = JsonNode.Parse(File.ReadAllText(ShippedConfigPath), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!;
        var tv = root["instance_of_classes"]!["TV"]!.AsArray();
        var toRemove = tv.Where(n => n?.GetValue<string>() == qid).ToList();
        foreach (var node in toRemove)
        {
            tv.Remove(node);
        }

        return root.ToJsonString();
    }

    private static string ShippedConfigPath =>
        Path.Combine(FindRepoRoot(), "config", "providers", "wikidata_reconciliation.json");

    private static ReconciliationProviderConfig LoadShippedConfig() =>
        JsonSerializer.Deserialize<ReconciliationProviderConfig>(File.ReadAllText(ShippedConfigPath), s_jsonOptions)
        ?? throw new InvalidOperationException("Failed to deserialize wikidata_reconciliation.json");

    private static bool ValidateP31(
        ReconciliationAdapter adapter,
        string[] instanceOf,
        string qid,
        MediaType mediaType,
        string? resolutionScope)
    {
        var method = typeof(ReconciliationAdapter).GetMethod(
            "ValidateP31ForMediaType",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method!.Invoke(adapter, [instanceOf, qid, mediaType, resolutionScope])!;
    }

    private static ReconciliationAdapter CreateAdapter(ReconciliationProviderConfig config, IConfigurationLoader? loader) =>
        new(
            config,
            new StubHttpClientFactory(),
            NullLogger<ReconciliationAdapter>.Instance,
            new StubFuzzyMatchingService(),
            responseCache: null,
            configLoader: loader);

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(TvBridgeP31ValidationTests).Assembly.Location);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not find repository root (.git directory)");
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubFuzzyMatchingService : IFuzzyMatchingService
    {
        public double ComputeTokenSetRatio(string a, string b) => 0.0;

        public double ComputePartialRatio(string a, string b) => 0.0;

        public FieldMatchResult ScoreCandidate(LocalMetadata local, CandidateMetadata candidate) => new();
    }
}
