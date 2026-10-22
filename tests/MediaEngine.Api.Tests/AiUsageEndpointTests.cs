using MediaEngine.AI.Configuration;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.LocalAi;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.AiFeatures;
using MediaEngine.Contracts.Ai;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>The Usage page of Local AI: who used which ability, and where each AI plugin stands against its hourly allowance.</summary>
public sealed class AiUsageEndpointTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-ai-usage", Guid.NewGuid().ToString("N"));
    private readonly PluginCatalog _catalog;

    public AiUsageEndpointTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        var loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        var empty = new ServiceCollection().BuildServiceProvider();
        _catalog = new PluginCatalog(
            [new VibeTagsPlugin(empty), new SmartLabellingPlugin()],
            new PluginSettingsService(loader),
            loader,
            new ApprovesEverything(),
            NullLogger<PluginCatalog>.Instance);
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
    public async Task Usage_NamesEachPluginAndAbility_AndKeepsPeopleAndIngestionAsTheirOwnRows()
    {
        var usage = new SummaryRepository(
            new AiUsageSummaryRow(AiCallerKind.Plugin, "tuvima.ai-vibe-tags", AiAbility.Classify, 12, 10, 4_500, Now),
            new AiUsageSummaryRow(AiCallerKind.Person, "person", AiAbility.WriteTextQuick, 3, 3, 900, Now),
            new AiUsageSummaryRow(AiCallerKind.Ingestion, "ingestion", AiAbility.ReadTextFromImage, 40, 40, 12_000, Now));

        var response = await Run(usage);

        var plugin = Assert.Single(response.Rows, row => row.CallerKind == "plugin");
        Assert.Equal("Vibe tags", plugin.CallerName);
        Assert.Equal("Classify", plugin.Ability);
        Assert.Equal(4.5, plugin.BusySeconds);
        Assert.Equal(10, plugin.Succeeded);
        Assert.Contains(response.Rows, row => row.CallerKind == "person");
        Assert.Contains(response.Rows, row => row.CallerKind == "ingestion");
    }

    [Fact]
    public async Task Usage_ListsOnlyPluginsThatAskForLocalAi_WithTheirHourlyAllowance()
    {
        var response = await Run(new SummaryRepository());

        Assert.All(response.Plugins, standing =>
        {
            Assert.Equal(120, standing.RequestsPerHour);
            Assert.False(standing.OverBudget);
        });
        Assert.Contains(response.Plugins, standing => standing.PluginName == "Vibe tags");
    }

    [Fact]
    public async Task Usage_ClampsTheNumberOfDaysToTheRetentionWindow()
    {
        var response = await Run(new SummaryRepository(), days: 5_000);

        Assert.Equal(IAiUsageRecordRepository.RetentionDays, response.Days);
    }

    private async Task<AiUsageResponse> Run(SummaryRepository usage, int days = 7)
    {
        var clock = new FixedClock(Now);
        var budget = new PluginAiBudget(new AiSettings(), usage, clock);
        var result = await AiEndpoints.GetAiUsageAsync(usage, budget, _catalog, clock, CancellationToken.None, days);
        return Assert.IsType<Ok<AiUsageResponse>>(result).Value!;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ApprovesEverything : IPluginApprovalCheck
    {
        public bool IsCovered(PluginManifest manifest, PluginTrustLevel trust) => true;
    }

    private sealed class SummaryRepository(params AiUsageSummaryRow[] rows) : IAiUsageRecordRepository
    {
        public Task AddAsync(AiUsageRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AiUsageTotals> GetTotalsSinceAsync(AiCallerKind kind, string callerId, DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult(new AiUsageTotals(0, 0));

        public Task<IReadOnlyList<AiUsageRecord>> ListRecentAsync(int limit, int skip = 0, AiCallerKind? kind = null, string? callerId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AiUsageRecord>>([]);

        public Task<IReadOnlyList<AiUsageSummaryRow>> SummariseSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AiUsageSummaryRow>>(rows);

        public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(0);
    }
}
