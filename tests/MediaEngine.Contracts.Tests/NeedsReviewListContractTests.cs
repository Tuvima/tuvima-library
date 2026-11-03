using System.Text.Json;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Review;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the one Needs Review list and its notifier count (universal editor v3, WP7 and D9).</summary>
public sealed class NeedsReviewListContractTests
{
    [Fact]
    public void Kinds_AreTheTwoWordsTheFiltersUse()
    {
        Assert.Equal("not_matched", NeedsReviewKinds.NotMatched);
        Assert.Equal("not_sure", NeedsReviewKinds.NotSure);
    }

    [Fact]
    public void AnUnmatchedFileRow_UsesSnakeCaseNames_AndCarriesNoReview()
    {
        var asset = Guid.NewGuid();
        var row = new NeedsReviewRowDto
        {
            Key = $"file:{asset:D}",
            Kind = NeedsReviewKinds.NotMatched,
            LibraryId = "movies",
            LibraryName = "Movies",
            LastTriedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero),
            Unmatched = new UnmatchedItemDto { AssetId = asset, LibraryId = "movies", FileName = "a.mkv", Reason = "no_match" },
        };

        var json = JsonSerializer.Serialize(row);
        var back = JsonSerializer.Deserialize<NeedsReviewRowDto>(json)!;

        Assert.StartsWith("{\"key\":\"file:" + asset.ToString("D") + "\",\"kind\":\"not_matched\",\"library_id\":\"movies\",\"library_name\":\"Movies\",\"last_tried_at\":\"2026-11-03T09:00:00+00:00\",\"best_guess\":null,\"review\":null,\"unmatched\":{", json, StringComparison.Ordinal);
        Assert.Null(back.Review);
        Assert.Equal("a.mkv", back.Unmatched!.FileName);
    }

    [Fact]
    public void ABestGuess_NamesTheOneActionUseThisRuns()
    {
        var json = JsonSerializer.Serialize(new NeedsReviewBestGuessDto { Action = "move_to_tv", Name = "Chernobyl", Year = "2019", Source = "TMDB TV series" });

        Assert.Equal("{\"action\":\"move_to_tv\",\"name\":\"Chernobyl\",\"year\":\"2019\",\"source\":\"TMDB TV series\",\"image_url\":null}", json);
    }

    [Fact]
    public void ThePage_CarriesTheTotalsTheFilterChipsShow()
    {
        var json = JsonSerializer.Serialize(new NeedsReviewListDto { Total = 3, NotMatchedTotal = 2, NotSureTotal = 1, Skip = 0, Take = 100 });

        Assert.Equal("{\"items\":[],\"total\":3,\"not_matched_total\":2,\"not_sure_total\":1,\"skip\":0,\"take\":100}", json);
    }

    [Fact]
    public void TheCount_KeepsPendingCountAsTheNotifierTotal_AndSplitsItByKind()
    {
        var json = JsonSerializer.Serialize(new ReviewCountResponse { PendingCount = 5, NotMatchedCount = 3, NotSureCount = 2 });

        Assert.Equal("{\"pending_count\":5,\"not_matched_count\":3,\"not_sure_count\":2}", json);
    }

    [Fact]
    public void AReviewItem_NowNamesItsLibrary()
    {
        var json = JsonSerializer.Serialize(new ReviewItemDto { LibraryId = "tv" });

        Assert.Contains("\"library_id\":\"tv\"", json, StringComparison.Ordinal);
    }
}
