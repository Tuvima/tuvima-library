using System.Text.Json;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Review;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the one Needs Review list and its notifier count (universal editor v3, WP7 and D9).</summary>
public sealed class NeedsReviewListContractTests
{
    [Fact]
    public void AnUnmatchedFileRow_UsesSnakeCaseNames_AndCarriesNoReview()
    {
        var asset = Guid.NewGuid();
        var row = new NeedsReviewRowDto
        {
            Key = $"file:{asset:D}",
            LibraryId = "movies",
            LibraryName = "Movies",
            LastTriedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero),
            Unmatched = new UnmatchedItemDto { AssetId = asset, LibraryId = "movies", FileName = "a.mkv", Reason = "no_match" },
        };

        var json = JsonSerializer.Serialize(row);
        var back = JsonSerializer.Deserialize<NeedsReviewRowDto>(json)!;

        Assert.StartsWith("{\"key\":\"file:" + asset.ToString("D") + "\",\"library_id\":\"movies\",\"library_name\":\"Movies\",\"last_tried_at\":\"2026-11-03T09:00:00+00:00\",\"best_guess\":null,\"choices\":[],\"review\":null,\"unmatched\":{", json, StringComparison.Ordinal);
        Assert.Null(back.Review);
        Assert.Equal("a.mkv", back.Unmatched!.FileName);
    }

    [Fact]
    public void ABestGuess_NamesTheOneActionUseThisRuns()
    {
        var json = JsonSerializer.Serialize(new NeedsReviewBestGuessDto { Action = "move_to_tv", Name = "Chernobyl", Year = "2019", Source = "TMDB TV series" });

        Assert.Equal("{\"action\":\"move_to_tv\",\"name\":\"Chernobyl\",\"year\":\"2019\",\"source\":\"TMDB TV series\",\"image_url\":null,\"choice_id\":null}", json);
    }

    [Fact]
    public void ARetailBestGuess_NamesTheChoiceUseThisSends()
    {
        var json = JsonSerializer.Serialize(new NeedsReviewBestGuessDto
        {
            Action = "use_retail_match",
            Name = "Dune",
            Year = "2021",
            Source = "TMDB",
            ChoiceId = "retail:tmdb:438631",
        });

        Assert.Equal("{\"action\":\"use_retail_match\",\"name\":\"Dune\",\"year\":\"2021\",\"source\":\"TMDB\",\"image_url\":null,\"choice_id\":\"retail:tmdb:438631\"}", json);
    }

    [Fact]
    public void AChoice_UsesSnakeCaseNames_AndTheThreeKindsAreFixedWords()
    {
        var json = JsonSerializer.Serialize(new NeedsReviewChoiceDto
        {
            Id = "retail:tmdb:438631",
            Kind = NeedsReviewChoiceKinds.Retail,
            Title = "Dune (2021)",
            Subtitle = "Denis Villeneuve",
        });

        Assert.Equal("{\"id\":\"retail:tmdb:438631\",\"kind\":\"retail\",\"title\":\"Dune (2021)\",\"subtitle\":\"Denis Villeneuve\",\"image_url\":null}", json);
        Assert.Equal("retail", NeedsReviewChoiceKinds.Retail);
        Assert.Equal("series", NeedsReviewChoiceKinds.Series);
        Assert.Equal("media_type", NeedsReviewChoiceKinds.MediaType);
    }

    [Fact]
    public void ARowWithChoices_CarriesThemUnderChoices()
    {
        var row = new NeedsReviewRowDto
        {
            Key = "review:x",
            Choices = [new NeedsReviewChoiceDto { Id = "media_type:Music", Kind = NeedsReviewChoiceKinds.MediaType, Title = "Music" }],
        };

        var back = JsonSerializer.Deserialize<NeedsReviewRowDto>(JsonSerializer.Serialize(row))!;

        Assert.Equal("media_type:Music", Assert.Single(back.Choices).Id);
        Assert.Contains("\"choices\":[{\"id\":\"media_type:Music\"", JsonSerializer.Serialize(row), StringComparison.Ordinal);
    }

    [Fact]
    public void TheChooseRequestAndResponse_UseSnakeCaseNames()
    {
        var review = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal("{\"choice_id\":\"series:abc\"}", JsonSerializer.Serialize(new ReviewChooseRequestDto { ChoiceId = "series:abc" }));
        Assert.Equal(
            "{\"chosen\":true,\"review_item_id\":\"11111111-2222-3333-4444-555555555555\",\"choice_id\":\"series:abc\",\"kind\":\"series\"}",
            JsonSerializer.Serialize(new ReviewChooseResponse(true, review, "series:abc", NeedsReviewChoiceKinds.Series)));
    }

    [Fact]
    public void ThePage_CarriesTheTotalsForItsGroupsAndItsKeptItems()
    {
        var json = JsonSerializer.Serialize(new NeedsReviewListDto { Total = 3, Skip = 0, Take = 100 });

        Assert.Equal(
            "{\"items\":[],\"total\":3,\"group_totals\":{\"ready\":0,\"choose\":0,\"no_match\":0,\"file_problem\":0},\"kept_total\":0,\"skip\":0,\"take\":100}",
            json);
    }

    [Fact]
    public void TheGroups_AreTheFourWordsThePageSectionsUse()
    {
        Assert.Equal("ready", NeedsReviewGroups.Ready);
        Assert.Equal("choose", NeedsReviewGroups.Choose);
        Assert.Equal("no_match", NeedsReviewGroups.NoMatch);
        Assert.Equal("file_problem", NeedsReviewGroups.FileProblem);
    }

    [Fact]
    public void ARow_CarriesItsGroupPictureFamilyRetryTimeAndKeptFlag_UnderSnakeCaseNames()
    {
        var show = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var row = new NeedsReviewRowDto
        {
            Key = "file:x",
            Group = NeedsReviewGroups.NoMatch,
            CoverUrl = "/stream/a/cover",
            FamilyKey = show.ToString("D"),
            FamilyTitle = "Chernobyl",
            FamilyWorkId = show,
            FamilyCoverUrl = $"/stream/entity/work/{show:D}/cover",
            NextRetryAt = new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero),
            Kept = true,
        };

        var json = JsonSerializer.Serialize(row);

        Assert.Contains("\"group\":\"no_match\",\"cover_url\":\"/stream/a/cover\",\"family_key\":\"11111111-2222-3333-4444-555555555555\",\"family_title\":\"Chernobyl\",\"family_work_id\":\"11111111-2222-3333-4444-555555555555\",\"family_cover_url\":\"/stream/entity/work/11111111-2222-3333-4444-555555555555/cover\",\"next_retry_at\":\"2026-11-10T08:00:00+00:00\",\"kept\":true}", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<NeedsReviewRowDto>(json)!;
        Assert.Equal(show, back.FamilyWorkId);
        Assert.True(back.Kept);
    }

    [Fact]
    public void ARowWithNoFamilyOrPicture_WritesNullsAndAGroup()
    {
        var json = JsonSerializer.Serialize(new NeedsReviewRowDto { Key = "review:x" });

        Assert.Contains("\"group\":\"no_match\",\"cover_url\":null,\"family_key\":null,\"family_title\":null,\"family_work_id\":null,\"family_cover_url\":null,\"next_retry_at\":null,\"kept\":false}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PuttingAnItemBack_AnswersWithTheItemsId()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal(
            "{\"reopened\":true,\"review_item_id\":\"11111111-2222-3333-4444-555555555555\"}",
            JsonSerializer.Serialize(new ReviewReopenResponse(reopened: true, review_item_id: id)));
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
