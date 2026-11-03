using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Setup;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// What the generated plugin page needs from the Engine (Plugins and Integrations screens v5 WP24): where each setting is kept, a row
/// that has no key, the per-plugin logging cap (D59) in the page model and in the Activity store.
/// </summary>
public sealed class PluginSetupChangesTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), $"tuvima_plugin_limits_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_configDirectory))
            {
                Directory.Delete(_configDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temporary folder cleanup is best effort.
        }
    }

    // ------------------------------------------------------------------------------------------------ Page model

    [Fact]
    public void ThePageModel_SaysWhereEachSettingIsKept()
    {
        var tmdb = SourcePage("tmdb", external: null);
        var stash = SourcePage("stashbox", external: new Dictionary<string, JsonElement>
        {
            ["fingerprint_permission"] = JsonSerializer.SerializeToElement(true),
        });

        var tunables = Controls(tmdb).Where(control => control.Control != "group_list").ToList();
        Assert.All(tunables, control => Assert.Equal("source_config", control.StoredIn));
        var servers = Controls(stash).Single(control => control.Key == "servers");
        Assert.Equal("value_source", servers.StoredIn);
        Assert.Equal("value_source", Controls(stash).Single(control => control.Key == "fingerprint_permission").StoredIn);
    }

    [Fact]
    public void ARowTheValueSourceSaysHasNoKey_IsMarkedKeyMissing()
    {
        var page = SourcePage("stashbox", external: new Dictionary<string, JsonElement>
        {
            ["servers"] = JsonSerializer.SerializeToElement(new object[]
            {
                new { id = "mine", title = "Mine", subtitle = "https://stash.example", state = "not_connected", locked = false, key_missing = true },
                new { id = "keyed", title = "Keyed", subtitle = "https://other.example", state = "connected", locked = false, key_missing = false },
            }),
        });

        var items = Controls(page).Single(control => control.Key == "servers").GroupList!.Items;

        Assert.True(items.Single(item => item.Id == "mine").KeyMissing);
        Assert.False(items.Single(item => item.Id == "keyed").KeyMissing);
    }

    [Fact]
    public void TheLoggingCap_IsAnAdvancedSettingOnEveryPluginsPage_WithItsDefaults()
    {
        var page = SourcePage("tmdb", external: null, cap: PluginActivityLimits.Default);

        var cap = Controls(page).Where(control => control.StoredIn == "activity_cap").ToDictionary(control => control.Key);

        Assert.Equal(["max_days", "max_entries"], cap.Keys.Order(StringComparer.Ordinal));
        Assert.All(cap.Values, control => Assert.True(control.Advanced));
        Assert.Equal(200, cap["max_entries"].Value!.Value.GetInt32());
        Assert.Equal(14, cap["max_days"].Value!.Value.GetInt32());
        Assert.Equal(PluginActivityLimits.MinimumEntries, (int)cap["max_entries"].Minimum!.Value);
        Assert.Equal(PluginActivityLimits.MaximumDays, (int)cap["max_days"].Maximum!.Value);
    }

    // ------------------------------------------------------------------------------------------------ The cap

    [Fact]
    public void TheCap_DefaultsTo200EntriesOr14Days_AndASavedChoiceSurvivesAReload()
    {
        var limits = new PluginActivityLimits(Loader());
        Assert.Equal(new PluginActivityCap(200, 14), limits.Get("any.plugin"));

        limits.Set("Any.Plugin", 50, 3);

        Assert.Equal(new PluginActivityCap(50, 3), limits.Get("any.plugin"));
        Assert.Equal(new PluginActivityCap(50, 3), new PluginActivityLimits(Loader()).Get("ANY.PLUGIN"));
        Assert.Equal(new PluginActivityCap(200, 14), limits.Get("another.plugin"));
    }

    [Theory]
    [InlineData(5, 14)]
    [InlineData(5000, 14)]
    [InlineData(200, 0)]
    [InlineData(200, 400)]
    public void ACapOutsideWhatThePageAllows_IsRefused(int entries, int days)
    {
        var limits = new PluginActivityLimits(Loader());

        Assert.Throws<ArgumentOutOfRangeException>(() => limits.Set("a", entries, days));
        Assert.Equal(PluginActivityLimits.Default, limits.Get("a"));
    }

    [Fact]
    public async Task TheActivityStore_KeepsOnlyWhatThePluginsCapAllows()
    {
        var repository = new RecordingRepository();
        var limits = new PluginActivityLimits(Loader());
        limits.Set("capped", 25, 2);
        var clock = new FixedClock(Start);
        var service = new PluginActivityService(repository, new PluginCircuitBreaker(clock), clock, NullLogger<PluginActivityService>.Instance, limits);

        await service.RecordRunAsync(new PluginActivityContext("capped", "1.0.0"), 1, "Ran");
        await service.RecordRunAsync(new PluginActivityContext("uncapped", "1.0.0"), 1, "Ran");

        Assert.Equal([("capped", 25), ("uncapped", 200)], repository.Kept);
        Assert.Equal([("capped", Start.AddDays(-2)), ("uncapped", Start.AddDays(-14))], repository.Trimmed);
    }

    private ConfigurationDirectoryLoader Loader() => new(_configDirectory);

    private static IEnumerable<PluginSetupControlDto> Controls(PluginSetupPageResponse page) =>
        page.Cards.Single(card => card.Key == "settings").Controls;

    private static PluginSetupPageResponse SourcePage(string name, IReadOnlyDictionary<string, JsonElement>? external, PluginActivityCap? cap = null)
    {
        var config = Provider(name);
        var sheet = new PluginSourceDto
        {
            Name = name,
            MediaTypes = [.. config.CanHandle?.MediaTypes ?? []],
            Supplies = [.. config.ProviderCapabilities],
            SendsDataTo = [.. config.Endpoints.Values.Select(address => new Uri(address).Host).Distinct()],
            Access = PluginAccessMapper.Map(
                config.Declaration,
                [.. config.Endpoints.Values.Select(address => (new Uri(address).Host, (string?)null)).Distinct()],
                config.RequiresApiKey,
                config.CanHandle?.MediaTypes ?? [],
                config.CanHandle?.MediaTypes,
                "metadata"),
        };
        var input = PluginPageModelInputs.FromSourceFile(
            config,
            sheet,
            alwaysOn: false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            external ?? new Dictionary<string, JsonElement>());
        return PluginPageModelBuilder.Build(input with { ActivityCap = cap });
    }

    private static MediaEngine.Domain.Configuration.ProviderConfiguration Provider(string name) =>
        new ConfigurationDirectoryLoader(Path.Combine(FindRepoRoot(), "config")).LoadProvider(name)
        ?? throw new InvalidOperationException($"No provider file for {name}.");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingRepository : IPluginActivityRepository
    {
        public List<(string Plugin, int Keep)> Kept { get; } = [];

        public List<(string Plugin, DateTimeOffset Before)> Trimmed { get; } = [];

        public Task AddAsync(StoredPluginActivity entry, int keepPerPlugin, CancellationToken ct = default)
        {
            Kept.Add((entry.PluginId, keepPerPlugin));
            return Task.CompletedTask;
        }

        public Task<int> TrimOlderThanAsync(string pluginId, DateTimeOffset olderThan, CancellationToken ct = default)
        {
            Trimmed.Add((pluginId, olderThan));
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<StoredPluginActivity>> GetRecentAsync(string pluginId, int limit, bool failuresOnly, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredPluginActivity>>([]);

        public Task<int> CountFailuresSinceAsync(string pluginId, DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> DeleteForPluginAsync(string pluginId, CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult(0);
    }
}
