using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Activity;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The plain words of the Activity log, one test per kind of event, and the one map from kind to category.
/// </summary>
public sealed class ActivityLogPresenterTests
{
    private static readonly Guid WorkId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    [Fact]
    public void MediaAdded_ReadsAsTheTitleAddedToTheMediaType_AndLinksToTheItem()
    {
        var dto = Present(SystemActionType.MediaAdded, name: "Dune", detail: "Added", entityId: WorkId, changes: """{"media_type":"Movies"}""");

        Assert.Equal(ActivityLogCategoryValues.Added, dto.Category);
        Assert.Equal(SystemActionType.MediaAdded, dto.Kind);
        Assert.Equal("Dune", dto.Title);
        Assert.Equal("Added to Movies", dto.Detail);
        Assert.Equal("Movies", dto.MediaType);
        Assert.Equal($"/details/work/{WorkId:D}?context=watch", dto.Route);
    }

    [Fact]
    public void MediaAdded_WithoutATitle_ReadsAsAnUntitledItem()
    {
        var dto = Present(SystemActionType.MediaAdded, name: null, changes: """{"media_type":"Books"}""");

        Assert.Equal("Untitled item", dto.Title);
        Assert.Equal("Added to Books", dto.Detail);
    }

    [Fact]
    public void MediaAdded_ForAnEpisode_HasNoLinkBecauseItsPageIsTheShows()
    {
        var dto = Present(SystemActionType.MediaAdded, name: "Pilot", entityId: WorkId, changes: """{"media_type":"TV"}""");

        Assert.Equal("Added to TV", dto.Detail);
        Assert.Null(dto.Route);
    }

    [Fact]
    public void PersonalMediaAdded_ShowsTheStoredCountAndLibraryWords_AsPhotos()
    {
        var dto = Present(SystemActionType.PersonalMediaAdded, name: "3,000 photos", detail: "Added to Family photos", changes: """{"photos":3000,"videos":0,"count":3000}""");

        Assert.Equal(ActivityLogCategoryValues.Added, dto.Category);
        Assert.Equal("3,000 photos", dto.Title);
        Assert.Equal("Added to Family photos", dto.Detail);
        Assert.Equal("Photos", dto.MediaType);
        Assert.Equal(3000, dto.Count);
    }

    [Fact]
    public void ReviewItemCreated_NamesTheItem_AndLinksToNeedsReview()
    {
        var dto = Present(SystemActionType.ReviewItemCreated, name: "Arrival", detail: "Review item created: LowConfidence");

        Assert.Equal(ActivityLogCategoryValues.Review, dto.Category);
        Assert.Equal("Arrival", dto.Title);
        Assert.Equal("Needs Review", dto.Detail);
        Assert.Equal("/settings/review", dto.Route);
    }

    [Fact]
    public void ReviewItemCreated_WithoutATitle_ReadsAsAnUntitledItem_AndNeverShowsTheStoredTriggerCode()
    {
        var dto = Present(SystemActionType.ReviewItemCreated, name: null, detail: "Review item created: LowConfidence");

        Assert.Equal("Untitled item", dto.Title);
        Assert.DoesNotContain("LowConfidence", dto.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewItemResolved_SaysTheReviewWasResolved()
    {
        var dto = Present(SystemActionType.ReviewItemResolved, name: "Arrival");

        Assert.Equal("Arrival", dto.Title);
        Assert.Equal("Review resolved", dto.Detail);
    }

    [Fact]
    public void MediaFailed_UsesTheReasonFromTheChanges_AndNamesTheFileWhenThereIsNoTitle()
    {
        var dto = Present(
            SystemActionType.MediaFailed,
            detail: "Failed \u2014 movie.mkv: the file is damaged",
            changes: """{"source_file":"movie.mkv","reason":"the file is damaged","error_type":"corrupt_file"}""");

        Assert.Equal(ActivityLogCategoryValues.Failed, dto.Category);
        Assert.Equal("movie.mkv", dto.Title);
        Assert.Equal("the file is damaged", dto.Detail);
    }

    [Fact]
    public void MediaFailed_WithoutAReason_StripsTheOldFailedPrefixFromTheStoredDetail()
    {
        var dto = Present(SystemActionType.MediaFailed, detail: "Failed \u2014 movie.mkv: could not be read");

        Assert.Equal("A file", dto.Title);
        Assert.Equal("could not be read", dto.Detail);
    }

    [Fact]
    public void MediaFailed_FromAnUnexpectedError_ShowsThePlainLine_NotTheErrorKeptInTheChanges()
    {
        // As written by IngestionEngine.RecordUnhandledCandidateFailureAsync.
        var dto = Present(
            SystemActionType.MediaFailed,
            name: "a.epub",
            detail: "Something went wrong while reading this file. The technical log has the details.",
            changes: """{"source_path":"C:\\secret\\a.epub","source_file":"a.epub","error_type":"IOException","message":"Sharing violation at C:\\secret\\a.epub"}""");

        Assert.Equal("a.epub", dto.Title);
        Assert.Equal("Something went wrong while reading this file. The technical log has the details.", dto.Detail);
        Assert.DoesNotContain("secret", dto.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkWritebackFailed_UsesTheStoredDetail()
    {
        var dto = Present(SystemActionType.ArtworkWritebackFailed, name: "Dune", detail: "The cover could not be saved into the file.");

        Assert.Equal(ActivityLogCategoryValues.Failed, dto.Category);
        Assert.Equal("Dune", dto.Title);
        Assert.Equal("The cover could not be saved into the file.", dto.Detail);
    }

    [Fact]
    public void FileQuarantined_IsAFailure_NamedByItsFile()
    {
        var dto = Present(SystemActionType.FileQuarantined, detail: "It was moved aside.", changes: """{"source_file":"bad.cbz"}""");

        Assert.Equal(ActivityLogCategoryValues.Failed, dto.Category);
        Assert.Equal("bad.cbz", dto.Title);
        Assert.Equal("It was moved aside.", dto.Detail);
    }

    [Fact]
    public void ToolMissing_ReadsAsCouldntFindTheTool_AndLinksToToolsAndRequirements()
    {
        var dto = Present(
            SystemActionType.ToolMissing,
            name: "FFmpeg",
            detail: "Videos, music and audiobooks wait until FFmpeg is available.",
            changes: """{"tool_id":"ffmpeg","tool":"FFmpeg","state":"missing"}""");

        Assert.Equal(ActivityLogCategoryValues.System, dto.Category);
        Assert.Equal("Couldn't find FFmpeg", dto.Title);
        Assert.Equal("Videos, music and audiobooks wait until FFmpeg is available.", dto.Detail);
        Assert.Equal("/settings/tools", dto.Route);
    }

    [Fact]
    public void ToolMissing_WhenTheToolIsFoundButBroken_ReadsAsNotWorking()
    {
        var dto = Present(SystemActionType.ToolMissing, name: "FFmpeg", changes: """{"tool":"FFmpeg","state":"problem"}""");

        Assert.Equal("FFmpeg isn't working", dto.Title);
    }

    [Fact]
    public void ToolFound_ReadsAsFoundTheTool_WithItsStoredDetail()
    {
        var dto = Present(SystemActionType.ToolFound, name: "FFmpeg", detail: "Version 7.1 is ready.", changes: """{"tool":"FFmpeg","state":"ready"}""");

        Assert.Equal("Found FFmpeg", dto.Title);
        Assert.Equal("Version 7.1 is ready.", dto.Detail);
        Assert.Equal("/settings/tools", dto.Route);
    }

    [Fact]
    public void ToolDownloadChecksumRefused_SaysTheDownloadWasThrownAway()
    {
        var dto = Present(SystemActionType.ToolDownloadChecksumRefused, changes: """{"tool":"FFmpeg"}""");

        Assert.Equal("Refused a download of FFmpeg", dto.Title);
        Assert.Equal("The download didn't match its recorded checksum, so it was thrown away.", dto.Detail);
    }

    [Fact]
    public void ToolDownloadFailed_ShowsTheReasonFromTheChanges()
    {
        var dto = Present(
            SystemActionType.ToolDownloadFailed,
            detail: "Could not download FFmpeg. The Library could not reach the download site.",
            changes: """{"tool":"FFmpeg","reason":"The Library could not reach the download site."}""");

        Assert.Equal("Couldn't download FFmpeg", dto.Title);
        Assert.Equal("The Library could not reach the download site.", dto.Detail);
    }

    [Theory]
    [InlineData(SystemActionType.ToolDownloadStarted, "Downloading FFmpeg")]
    [InlineData(SystemActionType.ToolDownloadFinished, "Downloaded FFmpeg")]
    [InlineData(SystemActionType.ToolDownloadCancelled, "Cancelled the download of FFmpeg")]
    public void ToolDownloadProgress_ReadsInPlainWords(string kind, string title)
    {
        var dto = Present(kind, changes: """{"tool":"FFmpeg"}""");

        Assert.Equal(ActivityLogCategoryValues.System, dto.Category);
        Assert.Equal(title, dto.Title);
    }

    [Fact]
    public void ProviderDown_SaysMatchingWaitsUntilItIsBack()
    {
        var dto = Present(SystemActionType.ProviderDown, name: "TMDB", changes: """{"provider_id":"tmdb","reason":"HTTP 503"}""");

        Assert.Equal("TMDB isn't responding", dto.Title);
        Assert.Equal("Matching and details from TMDB wait until it's back. The Library keeps checking.", dto.Detail);
    }

    [Fact]
    public void ProviderRecovered_SaysItemsCarryOn()
    {
        var dto = Present(SystemActionType.ProviderRecovered, name: "TMDB");

        Assert.Equal("TMDB is back", dto.Title);
        Assert.Equal("Items that were waiting for it carry on.", dto.Detail);
    }

    [Theory]
    [InlineData(SystemActionType.ServerStarted, "The Library started")]
    [InlineData(SystemActionType.ServerStopped, "The Library stopped")]
    public void ServerLifecycle_ReadsInPlainWords(string kind, string title)
    {
        var dto = Present(kind, detail: "Watching /data/incoming");

        Assert.Equal(ActivityLogCategoryValues.System, dto.Category);
        Assert.Equal(title, dto.Title);
    }

    [Fact]
    public void ActivityPruned_ReadsAsClearedOldLogEntries_WithTheStoredDetail()
    {
        var dto = Present(SystemActionType.ActivityPruned, detail: "Removed 1,200 entries older than 90 days.");

        Assert.Equal("Cleared old log entries", dto.Title);
        Assert.Equal("Removed 1,200 entries older than 90 days.", dto.Detail);
    }

    [Fact]
    public void ReconciliationCompleted_ReadsAsCheckedTheLibraryFolders()
    {
        var dto = Present(SystemActionType.ReconciliationCompleted, detail: "Scanned 40 files.");

        Assert.Equal("Checked the library folders", dto.Title);
        Assert.Equal("Scanned 40 files.", dto.Detail);
    }

    [Fact]
    public void AKindThatIsNotShown_HasNoEntry()
    {
        var entry = new SystemActivityEntry { ActionType = SystemActionType.FileHashed, OccurredAt = DateTimeOffset.UtcNow };

        Assert.Null(ActivityLogPresenter.Present(entry));
    }

    [Theory]
    [InlineData(SystemActionType.MediaAdded, ActivityLogCategoryValues.Added)]
    [InlineData(SystemActionType.PersonalMediaAdded, ActivityLogCategoryValues.Added)]
    [InlineData(SystemActionType.ReviewItemCreated, ActivityLogCategoryValues.Review)]
    [InlineData(SystemActionType.ReviewItemResolved, ActivityLogCategoryValues.Review)]
    [InlineData(SystemActionType.MediaFailed, ActivityLogCategoryValues.Failed)]
    [InlineData(SystemActionType.FileQuarantined, ActivityLogCategoryValues.Failed)]
    [InlineData(SystemActionType.ArtworkWritebackFailed, ActivityLogCategoryValues.Failed)]
    [InlineData(SystemActionType.ToolMissing, ActivityLogCategoryValues.System)]
    [InlineData(SystemActionType.ProviderRecovered, ActivityLogCategoryValues.System)]
    [InlineData(SystemActionType.ReconciliationCompleted, ActivityLogCategoryValues.System)]
    public void EveryShownKind_BelongsToOneCategory(string kind, string category)
    {
        Assert.Equal(category, ActivityLogCategories.CategoryOf(kind));
        Assert.Contains(kind, ActivityLogCategories.ActionTypesFor(category));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("nonsense")]
    public void AnEmptyUnknownOrAllCategory_ShowsTheUnionOfTheFour_NotEveryRow(string? category)
    {
        var all = ActivityLogCategories.ActionTypesFor(category);
        var union = new[]
            {
                ActivityLogCategoryValues.Added,
                ActivityLogCategoryValues.Review,
                ActivityLogCategoryValues.Failed,
                ActivityLogCategoryValues.System,
            }
            .SelectMany(ActivityLogCategories.ActionTypesFor)
            .Distinct()
            .ToList();

        Assert.Equal(union.Count, all.Count);
        Assert.All(union, kind => Assert.Contains(kind, all));
        Assert.DoesNotContain(SystemActionType.FileHashed, all);
    }

    private static ActivityLogEntryDto Present(
        string kind,
        string? name = null,
        string? detail = null,
        string? changes = null,
        Guid? entityId = null) =>
        ActivityLogPresenter.Present(new SystemActivityEntry
        {
            Id = 7,
            OccurredAt = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero),
            ActionType = kind,
            CollectionName = name,
            Detail = detail,
            ChangesJson = changes,
            EntityId = entityId,
        }) ?? throw new InvalidOperationException($"{kind} is not shown in the Activity log.");
}
