using System.Text.Json;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Search;
using MediaEngine.Domain.Services;

namespace MediaEngine.Contracts.Tests;

public sealed class SearchMatchingContractTests
{
    [Fact]
    public void LocalSearchContract_PreservesTheApiCompleteResult()
    {
        var result = new SearchResultDto
        {
            WorkId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Title = "Example",
            MediaType = "Book",
            CollectionDisplayName = "Examples",
            CoverUrl = "/artwork/example",
            Year = "2026",
            Description = "A complete result.",
            Rating = "PG",
        };

        var json = JsonSerializer.Serialize(result, MediaEngineJson.Web);

        Assert.Contains("\"cover_url\":\"/artwork/example\"", json, StringComparison.Ordinal);
        Assert.Contains("\"year\":\"2026\"", json, StringComparison.Ordinal);
        Assert.Contains("\"description\":\"A complete result.\"", json, StringComparison.Ordinal);
        Assert.Contains("\"rating\":\"PG\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataSearchContract_PreservesProviderAndQueryEnvelope()
    {
        var response = new MetadataSearchResponse
        {
            ProviderName = "open_library",
            Query = "example",
            Results =
            [
                new MetadataSearchResultDto
                {
                    Title = "Example",
                    ProviderItemId = "OL1M",
                    Confidence = 0.9,
                },
            ],
        };

        var json = JsonSerializer.Serialize(response, MediaEngineJson.Web);

        Assert.Contains("\"provider_name\":\"open_library\"", json, StringComparison.Ordinal);
        Assert.Contains("\"query\":\"example\"", json, StringComparison.Ordinal);
        Assert.Contains("\"results\":[", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RetailFieldScores_DoNotDropCoverEvidence()
    {
        var scores = new FieldMatchScoresDto
        {
            CoverScore = 0.75,
            CoverVerdict = 1,
        };

        var json = JsonSerializer.Serialize(scores, MediaEngineJson.Web);

        Assert.Contains("\"cover_score\":0.75", json, StringComparison.Ordinal);
        Assert.Contains("\"cover_verdict\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardSearchModels_KeepNoSearchWireTypes_AndContractsOwnTheUniverseSearchShapes()
    {
        var root = FindRepoRoot();
        var searchModels = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MediaEngine.Web",
            "Models",
            "ViewDTOs",
            "SearchDtos.cs"));

        var universeContracts = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MediaEngine.Contracts",
            "Search",
            "UniverseSearchContracts.cs"));

        Assert.Contains("class SearchUniverseRequestDto", universeContracts, StringComparison.Ordinal);
        Assert.Contains("class UniverseCandidateDto", universeContracts, StringComparison.Ordinal);
        Assert.Contains("class SearchUniverseResponseDto", universeContracts, StringComparison.Ordinal);
        Assert.DoesNotContain("class SearchUniverseRequestDto", searchModels, StringComparison.Ordinal);
        Assert.DoesNotContain("class UniverseCandidateDto", searchModels, StringComparison.Ordinal);
        Assert.DoesNotContain("class SearchUniverseResponseDto", searchModels, StringComparison.Ordinal);
        Assert.DoesNotContain("class UniverseFieldMatchScoresDto", searchModels, StringComparison.Ordinal);
        Assert.DoesNotContain("class SearchRetailRequestDto", searchModels, StringComparison.Ordinal);
        Assert.DoesNotContain("class ItemCanonicalSearchResponseDto", searchModels, StringComparison.Ordinal);
        Assert.DoesNotContain("class FieldMatchScoresDto", searchModels, StringComparison.Ordinal);
    }

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
