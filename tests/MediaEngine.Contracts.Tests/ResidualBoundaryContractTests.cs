using System.Text.Json;
using MediaEngine.Application.ReadModels;
using MediaEngine.Contracts.Development;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Progress;
using MediaEngine.Contracts.Timeline;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;

namespace MediaEngine.Contracts.Tests;

public sealed class ResidualBoundaryContractTests
{
    [Fact]
    public void DebugContracts_RoundTripTheCompleteEnrichmentGraph()
    {
        var response = new DebugLookupResponse(
            "Q42",
            [new DebugClaimGroup("title", [new DebugClaimEntry("Dune", 0.99, "provider")])],
            [new DebugPersonResult("Frank Herbert", "Author", "Q100", "/headshot", "Biography", "Writer")],
            [new DebugEntityResult("Arrakis", "Q101", "Location", "Desert planet", "/arrakis")],
            [new DebugRelationshipResult("Q100", "Frank Herbert", "created", "Q101", "Arrakis", null, null)],
            [new DebugBridgeHint("isbn", "978-1", "9781", "isbn_13", ["Apple Books"])]);

        var json = JsonSerializer.Serialize(response, MediaEngineJson.Web);
        var roundTrip = JsonSerializer.Deserialize<DebugLookupResponse>(json, MediaEngineJson.Web);

        Assert.NotNull(roundTrip);
        Assert.Equal("Q42", roundTrip.ResolvedQid);
        Assert.Single(roundTrip.ClaimGroups);
        Assert.Single(roundTrip.Persons);
        Assert.Single(roundTrip.FictionalEntities);
        Assert.Single(roundTrip.Relationships);
        Assert.Single(roundTrip.BridgeHintPreview);
        AssertJsonProperties(
            response,
            "resolvedQid",
            "claimGroups",
            "persons",
            "fictionalEntities",
            "relationships",
            "bridgeHintPreview");

        Assert.Equal(
            ["title", "mediaType", "author"],
            JsonPropertyNames(new DebugLookupRequest("Dune", "Books")));
        Assert.Equal(
            ["qid", "mediaType", "author"],
            JsonPropertyNames(new DebugEnrichRequest("Q42", "Books")));
    }

    [Fact]
    public void DirectProjectionContracts_HaveNoCompletenessDeltaFromInternalSources()
    {
        AssertSamePublicPropertyNames(typeof(CanonDiscrepancy), typeof(CanonDiscrepancyDto));
        AssertSamePublicPropertyNames(typeof(EntityEvent), typeof(EntityTimelineEventDto));
        AssertSamePublicPropertyNames(typeof(EntityFieldChange), typeof(EntityTimelineFieldChangeDto));
        AssertSamePublicPropertyNames(typeof(JourneyItemResponse), typeof(JourneyItemDto));
    }

    [Fact]
    public void DevelopmentResetEnvelope_PreservesNumericScopeAndSnakeCaseResponseShape()
    {
        var reset = new DevHarnessResetResponse(
            DevHarnessWipeScope.Full,
            ["Database reset", "Watcher deferred"]);
        var response = new DevHarnessReingestResponse(
            "Reingest initiated.",
            reset,
            ["C:\\Library"],
            1,
            true);

        var resetJson = JsonSerializer.Serialize(reset, MediaEngineJson.Web);
        Assert.Contains("\"scope\":1", resetJson, StringComparison.Ordinal);
        AssertJsonProperties(
            response,
            "message",
            "reset",
            "scanned_directories",
            "source_count",
            "fsw_paused");
    }

    [Fact]
    public void ApiAndDashboardBoundaries_UseContractsAndExplicitMappings()
    {
        var root = FindRepoRoot();
        var debugEndpoint = Read(root, "src", "MediaEngine.Api", "Endpoints", "DebugEndpoints.cs");
        var canonEndpoint = Read(root, "src", "MediaEngine.Api", "Endpoints", "CanonEndpoints.cs");
        var timelineEndpoint = Read(root, "src", "MediaEngine.Api", "Endpoints", "TimelineEndpoints.cs");
        var progressEndpoint = Read(root, "src", "MediaEngine.Api", "Endpoints", "ProgressEndpoints.cs");
        var client = Read(root, "src", "MediaEngine.Web", "Services", "Integration", "EngineApiClient.Playback.cs");
        var clientPartials = Read(root, "src", "MediaEngine.Web", "Services", "Integration", "EngineApiClient.cs");
        var debugTool = Read(root, "src", "MediaEngine.Web", "Components", "Settings", "EnrichmentTesterToolTab.razor");
        var devSeed = Read(root, "src", "MediaEngine.Api", "DevSupport", "DevSeedEndpoints.cs");

        Assert.Contains("using MediaEngine.Contracts.Development;", debugEndpoint, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "src", "MediaEngine.Api", "Models", "DebugLookupRequest.cs")));
        Assert.False(File.Exists(Path.Combine(root, "src", "MediaEngine.Api", "Models", "DebugLookupResponse.cs")));

        Assert.Contains("Select(MapDiscrepancy)", canonEndpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Produces<IReadOnlyList<CanonDiscrepancy>>", canonEndpoint, StringComparison.Ordinal);
        Assert.Contains("Select(MapEvent)", timelineEndpoint, StringComparison.Ordinal);
        Assert.Contains("Select(MapFieldChange)", timelineEndpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Produces<IReadOnlyList<EntityEvent>>", timelineEndpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Produces<IReadOnlyList<EntityFieldChange>>", timelineEndpoint, StringComparison.Ordinal);
        Assert.Contains("Select(MapJourneyItem)", progressEndpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Produces<IReadOnlyList<JourneyItemResponse>>", progressEndpoint, StringComparison.Ordinal);

        Assert.Contains("GetFromJsonAsync<List<JourneyItemDto>>", client, StringComparison.Ordinal);
        Assert.DoesNotContain("JourneyItemRaw", clientPartials, StringComparison.Ordinal);
        Assert.Contains("@using MediaEngine.Contracts.Development", debugTool, StringComparison.Ordinal);
        Assert.DoesNotContain("private sealed record Debug", debugTool, StringComparison.Ordinal);
        Assert.Contains("MapResetResult(resetResult)", devSeed, StringComparison.Ordinal);
        Assert.Contains("wipeResult is null ? null : MapResetResult(wipeResult)", devSeed, StringComparison.Ordinal);
    }

    private static void AssertJsonProperties<T>(T value, params string[] expected)
    {
        Assert.Equal(expected, JsonPropertyNames(value));
    }

    private static string[] JsonPropertyNames<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, MediaEngineJson.Web));
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
    }

    private static void AssertSamePublicPropertyNames(Type source, Type contract)
    {
        var sourceNames = source.GetProperties().Select(property => property.Name).Order().ToArray();
        var contractNames = contract.GetProperties().Select(property => property.Name).Order().ToArray();
        Assert.Equal(sourceNames, contractNames);
    }

    private static string Read(string root, params string[] segments) =>
        File.ReadAllText(Path.Combine([root, .. segments]));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
