using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A book that is only half added (no title yet, no cover, no type, no progress gates) must still show as a row, in the
/// Activity list and in its details drawer, and must never replace the whole screen with "Something went wrong" (ingestion WP8, bug 13).
/// </summary>
public sealed class HalfAddedRowTests : LiveLibraryTestContext
{
    private readonly RecordingLoggerProvider _logs = new();

    public HalfAddedRowTests()
    {
        Services.AddNativeUiServices();
        Services.GetRequiredService<ILoggerFactory>().AddProvider(_logs);
    }

    public static TheoryData<string> HalfAddedShapes() => new()
    {
        "no-title",
        "no-type",
        "unknown-type",
        "no-cover-no-gates",
        "in-review",
        "failed",
        "default-dates-and-empty-child-unit",
        "every-field-blank",
    };

    [Theory]
    [MemberData(nameof(HalfAddedShapes))]
    public void ARowMissingItsTitleCoverOrType_StillShowsItsFiveCellsAndFacets(string shape)
    {
        var cut = RenderList(HalfAdded(shape));

        var row = Assert.Single(cut.FindAll(".history-row"));
        Assert.Equal("adding", row.GetAttribute("data-activity-state"));
        Assert.Equal(5, row.QuerySelectorAll(".ingestion-facets__slot").Length);
        Assert.NotEmpty(cut.Find("#recent-history-title").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='row-error']"));
    }

    [Fact]
    public void ARowWithNoTitle_SaysItIsIdentifyingMediaInsteadOfShowingABlankName()
    {
        var cut = RenderList(HalfAdded("no-title"));

        Assert.Equal("Identifying media", cut.Find(".history-row__open strong").TextContent);
        Assert.Contains("Identifying media", cut.Find(".history-row__open").GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    [Fact]
    public void ARowWithNoType_ShowsAGenericTypeAndTheFallbackArtworkIcon()
    {
        var cut = RenderList(HalfAdded("no-type"));

        Assert.StartsWith("Unknown", cut.Find(".history-row__identity small").TextContent.Trim(), StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".history-row__art .app-media-artwork__fallback"));
        Assert.Empty(cut.FindAll(".history-row__art img"));
    }

    [Theory]
    [MemberData(nameof(HalfAddedShapes))]
    public void TheDrawerForAHalfAddedRow_StillOpens(string shape)
    {
        var cut = Render<IngestionMediaDrawer>(parameters => parameters.Add(drawer => drawer.Item, HalfAdded(shape)));

        Assert.NotEmpty(cut.Find("#ingestion-drawer-title").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='row-error']"));
    }

    [Theory]
    [InlineData("duration_sec", "1e300")]
    [InlineData("duration_sec", "Infinity")]
    [InlineData("duration_sec", "NaN")]
    [InlineData("duration", "99999999999999999999")]
    public void NeedsReviewFacts_WithAnImpossibleDuration_AreShownAsTheyWereDetectedInsteadOfThrowing(string key, string value)
    {
        var item = new ReviewItemViewModel
        {
            EntityTitle = "Cibola Burn",
            MediaType = "Audiobooks",
            Trigger = "RetailMatchFailed",
            DetectedFacts = new Dictionary<string, string> { [key] = value },
        };

        var presentation = ReviewIssuePresentationBuilder.Build(item);

        Assert.Contains(presentation.KnownFacts, fact => fact.Value == value);
    }

    [Fact]
    public void OneRowThatCannotBeDrawn_ShowsACouldntBeShownRowAndTheOtherRowsAndHeadingStay()
    {
        var broken = HalfAdded("no-title");
        broken.Artwork = null!;
        var cut = RenderList(Named("First book"), broken, Named("Third book"));

        Assert.Equal("Library activity", cut.Find("#recent-history-title").TextContent);
        var rows = cut.FindAll(".history-row");
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.TextContent.Contains("First book", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.TextContent.Contains("Third book", StringComparison.Ordinal));

        var fallback = Assert.Single(cut.FindAll("[data-testid='row-error']"));
        Assert.Contains(AppRowErrorBoundary.Headline, fallback.TextContent, StringComparison.Ordinal);
        Assert.Contains("Identifying media", fallback.TextContent, StringComparison.Ordinal);
        Assert.Contains(_logs.Errors, error => error is NullReferenceException);
    }

    [Fact]
    public void AFailedRow_ShowsNormallyAgainWhenItsDataChanges()
    {
        var broken = Named("Cibola Burn");
        broken.Artwork = null!;
        var cut = RenderList(broken);
        Assert.Single(cut.FindAll("[data-testid='row-error']"));

        var repaired = Named("Cibola Burn");
        repaired.GroupId = broken.GroupId;
        repaired.UpdatedAt = broken.UpdatedAt.AddSeconds(5);
        Presentation = () => Task.FromResult<IngestionPresentationSnapshotDto?>(new IngestionPresentationSnapshotDto { IsRunning = true, CurrentMedia = [repaired], CurrentMediaTotal = 1 });
        _ = Services.GetRequiredService<IngestionLiveDashboardState>().LoadAsync();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid='row-error']"));
            Assert.Single(cut.FindAll(".history-row"));
        });
    }

    [Fact]
    public void TheRowBoundary_LogsTheExceptionAndShowsTheItemTitleInItsFallback()
    {
        var cut = Render<AppRowErrorBoundary>(parameters => parameters
            .Add(boundary => boundary.ItemTitle, "Cibola Burn")
            .Add(boundary => boundary.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<ThrowingRow>(0);
                builder.CloseComponent();
            })));

        var fallback = cut.Find("[data-testid='row-error']");
        Assert.Contains(AppRowErrorBoundary.Headline, fallback.TextContent, StringComparison.Ordinal);
        Assert.Contains("Cibola Burn", fallback.TextContent, StringComparison.Ordinal);
        var logged = Assert.Single(_logs.Errors);
        Assert.IsType<InvalidOperationException>(logged);
        Assert.Equal("this row cannot be drawn", logged.Message);
    }

    [Fact]
    public void TheRowBoundary_ShowsItsChildAgainWhenItsResetKeyChanges()
    {
        RenderFragment throwing = builder =>
        {
            builder.OpenComponent<ThrowingRow>(0);
            builder.CloseComponent();
        };
        RenderFragment fine = builder => builder.AddMarkupContent(0, "<p class=\"fine-row\">fine</p>");
        var cut = Render<AppRowErrorBoundary>(parameters => parameters
            .Add(boundary => boundary.ResetKey, 1)
            .Add(boundary => boundary.ChildContent, throwing));
        Assert.Single(cut.FindAll("[data-testid='row-error']"));

        cut.Render(parameters => parameters
            .Add(boundary => boundary.ResetKey, 1)
            .Add(boundary => boundary.ChildContent, fine));
        Assert.Single(cut.FindAll("[data-testid='row-error']"));

        cut.Render(parameters => parameters
            .Add(boundary => boundary.ResetKey, 2)
            .Add(boundary => boundary.ChildContent, fine));
        Assert.Empty(cut.FindAll("[data-testid='row-error']"));
        Assert.Single(cut.FindAll(".fine-row"));
    }

    [Theory]
    [InlineData("src/MediaEngine.Web/Components/Settings/IngestionMediaDrawer.razor")]
    [InlineData("src/MediaEngine.Web/Components/Settings/NeedsReviewPageContent.razor")]
    [InlineData("src/MediaEngine.Web/Components/Pages/RecentlyAddedPageContent.razor")]
    [InlineData("src/MediaEngine.Web/Components/Settings/ActivityLogPanel.razor")]
    public void EveryRowLoopOnTheIngestionPages_IsWrappedInTheRowErrorBoundary(string relativePath)
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));

        Assert.Contains("<AppRowErrorBoundary", source, StringComparison.Ordinal);
        Assert.Contains("</AppRowErrorBoundary>", source, StringComparison.Ordinal);
    }

    // The Activity list with these items being added right now, as the Engine's live snapshot reports them.
    private IRenderedComponent<MediaEngine.Web.Components.Pages.RecentlyAddedPageContent> RenderList(params IngestionMediaGroupDto[] adding)
    {
        Presentation = () => Task.FromResult<IngestionPresentationSnapshotDto?>(new IngestionPresentationSnapshotDto
        {
            IsRunning = true,
            CurrentMedia = [.. adding],
            CurrentMediaTotal = adding.Length,
        });
        var cut = Render<MediaEngine.Web.Components.Pages.RecentlyAddedPageContent>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".history-row, [data-testid='row-error']")));
        return cut;
    }

    private static IngestionMediaGroupDto Named(string title) => new()
    {
        GroupId = Guid.NewGuid(),
        MediaType = "Books",
        Title = title,
        Availability = "adding",
        StatusLabel = "Adding",
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class ThrowingRow : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            throw new InvalidOperationException("this row cannot be drawn");
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<Exception> Errors { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Errors);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<Exception> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error && exception is not null)
                {
                    errors.Add(exception);
                }
            }
        }
    }

    private static IngestionMediaGroupDto HalfAdded(string shape)
    {
        var row = new IngestionMediaGroupDto
        {
            GroupId = Guid.NewGuid(),
            MediaType = "Books",
            Title = "Cibola Burn",
            Availability = "adding",
            StatusLabel = "Adding",
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        switch (shape)
        {
            case "no-title":
                row.Title = string.Empty;
                break;
            case "no-type":
                row.MediaType = string.Empty;
                break;
            case "unknown-type":
                row.MediaType = "Unknown";
                break;
            case "no-cover-no-gates":
                row.CoverUrl = null;
                row.ProgressGates = [];
                row.CurrentGateLabel = null;
                row.WaitingFor = [];
                break;
            case "in-review":
                row.Availability = "review";
                row.StatusLabel = "Needs review";
                row.Subtitle = null;
                break;
            case "failed":
                row.Availability = "failed";
                row.StatusLabel = "Failed";
                break;
            case "default-dates-and-empty-child-unit":
                row.AddedAt = default;
                row.UpdatedAt = default;
                row.ChildUnit = string.Empty;
                row.ChildCompleted = 1;
                break;
            case "every-field-blank":
                row.Title = string.Empty;
                row.MediaType = string.Empty;
                row.Subtitle = string.Empty;
                row.StatusLabel = string.Empty;
                row.CurrentGateLabel = string.Empty;
                row.ChildUnit = string.Empty;
                row.AddedAt = default;
                row.UpdatedAt = default;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }

        return row;
    }
}
