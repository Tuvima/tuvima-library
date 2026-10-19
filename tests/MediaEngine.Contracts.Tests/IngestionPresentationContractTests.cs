using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Contracts.Ingestion;

namespace MediaEngine.Contracts.Tests;

public sealed class IngestionPresentationContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PresentationSnapshot_CarriesPhotosAndVideosInProgressAsCounts()
    {
        var node = Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(new IngestionPresentationSnapshotDto
        {
            PersonalMediaInProgress = new IngestionPersonalMediaProgressDto { Photos = 3000, Videos = 12 },
        }, JsonOptions));

        var counts = Assert.IsType<JsonObject>(node["personal_media_in_progress"]);
        Assert.Equal(
            new[] { "photos", "videos" }.Order(StringComparer.Ordinal),
            counts.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(3000, counts["photos"]!.GetValue<int>());
        Assert.Equal(12, counts["videos"]!.GetValue<int>());
    }

    [Fact]
    public void RecentDay_CarriesItsCountsByTypeWithSnakeCaseNames()
    {
        var day = new IngestionRecentDayDto
        {
            Date = new DateOnly(2026, 10, 19),
            TotalCount = 3,
            TypeCounts = [new IngestionTypeCountDto { MediaType = "Movies", Count = 212 }],
        };

        var node = Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(day, JsonOptions));

        Assert.Equal(
            new[] { "date", "total_count", "type_counts", "items" }.Order(StringComparer.Ordinal),
            node.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        var typeCount = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(node["type_counts"])[0]);
        Assert.Equal("Movies", typeCount["media_type"]!.GetValue<string>());
        Assert.Equal(212, typeCount["count"]!.GetValue<int>());

        var restored = JsonSerializer.Deserialize<IngestionRecentDayDto>(node.ToJsonString(), JsonOptions);
        var restoredCount = Assert.Single(restored!.TypeCounts);
        Assert.Equal(("Movies", 212), (restoredCount.MediaType, restoredCount.Count));
    }
}
