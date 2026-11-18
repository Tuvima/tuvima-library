using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The bottom of a details page (details-page-bottom spec D4 to D7, section 4): the Engine writes the "Your copy" row, the Dashboard only draws
/// it. Chips and a File details control sit in one full-width row; the control opens one panel of groups with the file's name at the bottom, and
/// the file's place stays out of the page until it is asked for. History, the Type and Library rows and the old About this panel are gone.
/// </summary>
public sealed class DetailsTabFileFactsTests : AsyncBunitContext
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly Guid ItemId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly DeviceContextService _device;
    private EngineApiClientStub? _stub;

    public DetailsTabFileFactsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = EngineApiClientStub.Create(stub => _stub = stub);
        _device = new DeviceContextService(api);
        Services.AddSingleton(api);
        Services.AddSingleton(_device);
    }

    private Task UseAsync(string formFactor) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));

    private static DetailYourCopyBlockViewModel MovieBlock(
        string? label = null,
        string? location = "/media/Movies/Test (2011)/Test (2011).mkv",
        string[]? chips = null) => new()
        {
            Label = label,
            Kind = DetailYourCopyKinds.Video,
            Chips = chips ?? ["1080p", "H.264", "AAC 5.1", "MKV"],
            Groups =
        [
            new DetailYourCopyGroupViewModel { Key = DetailYourCopyGroupKeys.Video, Lines = ["1920 × 1080 · 23.976 fps", "H.264 · 8.4 Mb/s", "SDR"] },
            new DetailYourCopyGroupViewModel { Key = DetailYourCopyGroupKeys.AudioSubtitles, Lines = ["English · AAC 5.1", "48 kHz · 384 kb/s", "Subtitles · English, Spanish"] },
            new DetailYourCopyGroupViewModel { Key = DetailYourCopyGroupKeys.File, Lines = ["MKV · 7.1 GB", "Duration · 1h 49m", "Added · Nov 2, 2026"] },
        ],
            FileName = "Test (2011).mkv",
            FileLocation = location,
        };

    private static DetailPageViewModel Model(
        DetailEntityType type = DetailEntityType.Movie,
        DetailYourCopyViewModel? copy = null,
        bool canEdit = false) => new()
        {
            Id = ItemId.ToString("D"),
            EntityType = type,
            Title = "Test",
            YourCopy = copy,
            OverflowActions = canEdit ? [new DetailAction { Key = "edit", Label = "Edit details" }] : [],
        };

    private static DetailYourCopyViewModel One(DetailYourCopyBlockViewModel block) => new() { Blocks = [block] };

    private IRenderedComponent<DetailsTab> RenderTab(DetailPageViewModel model) =>
        Render<DetailsTab>(parameters => parameters.Add(tab => tab.Model, model));

    [Fact]
    public async Task AMovie_ShowsOneYourCopyRowWithChipsAndFileDetailsClosed()
    {
        await UseAsync("desktop");

        var cut = RenderTab(Model(copy: One(MovieBlock())));

        Assert.Contains("Your copy", cut.Find(".tl-your-copy__title").TextContent);
        Assert.Equal(["1080p", "H.264", "AAC 5.1", "MKV"], cut.FindAll(".tl-your-copy__chip").Select(chip => chip.TextContent.Trim()));
        var toggle = cut.Find(".tl-your-copy__toggle");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.Contains("File details", toggle.TextContent);
        Assert.Empty(cut.FindAll(".tl-your-copy__panel"));
        Assert.DoesNotContain("Test (2011).mkv", cut.Markup);
    }

    [Fact]
    public async Task FileDetails_OpensOnePanelInThreeGroupsWithAnIconEachAndTheFilenameAtTheBottom()
    {
        await UseAsync("desktop");
        var cut = RenderTab(Model(copy: One(MovieBlock())));

        cut.Find(".tl-your-copy__toggle").Click();

        Assert.Equal("true", cut.Find(".tl-your-copy__toggle").GetAttribute("aria-expanded"));
        var groups = cut.FindAll(".tl-your-copy__group");
        Assert.Equal(["Video", "Audio and subtitles", "File"], groups.Select(group => group.QuerySelector("h3")!.TextContent.Trim()));
        Assert.All(groups, group => Assert.NotNull(group.QuerySelector("h3 svg")));
        Assert.Contains("1920 × 1080 · 23.976 fps", groups[0].TextContent);
        Assert.Contains("Subtitles · English, Spanish", groups[1].TextContent);
        Assert.Contains("MKV · 7.1 GB", groups[2].TextContent);
        var panel = cut.Find(".tl-your-copy__panel");
        Assert.Equal("Filename", panel.QuerySelector(".tl-your-copy__file-label")!.TextContent);
        Assert.Equal("Test (2011).mkv", panel.QuerySelector(".tl-your-copy__file-name")!.TextContent);
        // The filename follows the groups: it is the last thing in the panel (before the file location, which is closed).
        Assert.Same(panel.Children.Last(), panel.QuerySelector(".tl-your-copy__file"));
    }

    [Fact]
    public async Task TheFileLocation_StaysOutOfThePageUntilShowFileLocationIsPressed()
    {
        await UseAsync("desktop");
        var cut = RenderTab(Model(copy: One(MovieBlock()), canEdit: true));
        cut.Find(".tl-your-copy__toggle").Click();

        Assert.DoesNotContain("/media/Movies", cut.Markup);
        Assert.Equal("Show file location", cut.Find(".tl-your-copy__location-toggle").TextContent.Trim());

        cut.Find(".tl-your-copy__location-toggle").Click();

        Assert.Equal("/media/Movies/Test (2011)/Test (2011).mkv", cut.Find(".tl-your-copy__path").TextContent);
        Assert.Equal("Hide file location", cut.Find(".tl-your-copy__location-toggle").TextContent.Trim());

        // Closing File details puts the place away again: opening it afresh shows no path.
        cut.Find(".tl-your-copy__toggle").Click();
        cut.Find(".tl-your-copy__toggle").Click();
        Assert.Empty(cut.FindAll(".tl-your-copy__path"));
        Assert.DoesNotContain("/media/Movies", cut.Markup);
    }

    [Fact]
    public async Task WhenTheEngineSentNoFileLocation_ThereIsNoShowFileLocationControl()
    {
        await UseAsync("desktop");
        var cut = RenderTab(Model(copy: One(MovieBlock(location: null))));

        cut.Find(".tl-your-copy__toggle").Click();

        Assert.Equal("Test (2011).mkv", cut.Find(".tl-your-copy__file-name").TextContent);
        Assert.Empty(cut.FindAll(".tl-your-copy__location-toggle"));
    }

    [Fact]
    public async Task OnATelevision_FileDetailsIsAFocusableButtonAndNoFilenameOrLocationIsDrawn()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("television", "tv", false));
        var cut = RenderTab(Model(copy: One(MovieBlock())));

        Assert.NotNull(cut.Find("section.tl-your-copy.tl-your-copy--tv"));
        var toggle = cut.Find("button.tl-your-copy__toggle");
        Assert.Equal("button", toggle.GetAttribute("type"));
        toggle.Click();

        Assert.Equal(3, cut.FindAll(".tl-your-copy__group").Count);
        Assert.Empty(cut.FindAll(".tl-your-copy__file"));
        Assert.DoesNotContain("Test (2011).mkv", cut.Markup);
        Assert.DoesNotContain("/media/Movies", cut.Markup);
    }

    [Fact]
    public async Task WhileTheFileIsNotScannedYet_TheRowReadsReadingFileFactsWithNoChipsAndNoFileDetails()
    {
        await UseAsync("desktop");
        var block = new DetailYourCopyBlockViewModel { Kind = DetailYourCopyKinds.Video, IsReadingFacts = true };

        var cut = RenderTab(Model(copy: One(block)));

        Assert.Contains("Reading file facts", cut.Find(".tl-your-copy__reading").TextContent);
        Assert.Empty(cut.FindAll(".tl-your-copy__chip"));
        Assert.Empty(cut.FindAll(".tl-your-copy__toggle"));
    }

    [Fact]
    public async Task SeveralEditions_GetOneRowEachLabelledByEditionAndEachOpensItsOwnPanel()
    {
        await UseAsync("desktop");
        var copy = new DetailYourCopyViewModel
        {
            Blocks =
            [
                MovieBlock("Theatrical cut"),
                MovieBlock("Director's cut", chips: ["4K", "HEVC", "MKV"]),
            ],
        };
        var cut = RenderTab(Model(copy: copy));

        Assert.Equal(2, cut.FindAll(".tl-your-copy__block").Count);
        Assert.Equal(["Theatrical cut", "Director's cut"], cut.FindAll(".tl-your-copy__edition").Select(label => label.TextContent.Trim()));

        cut.FindAll(".tl-your-copy__toggle")[1].Click();

        var blocks = cut.FindAll(".tl-your-copy__block");
        Assert.Empty(blocks[0].QuerySelectorAll(".tl-your-copy__panel"));
        Assert.Single(blocks[1].QuerySelectorAll(".tl-your-copy__panel"));
    }

    [Fact]
    public async Task ForAProfileThatMayNotSeeFileFacts_TheEngineSendsNoYourCopyAndNothingIsDrawn()
    {
        await UseAsync("desktop");

        var cut = RenderTab(Model(copy: null, canEdit: true));

        Assert.Empty(cut.FindAll(".tl-your-copy"));
        Assert.DoesNotContain("File details", cut.Markup);
        Assert.DoesNotContain("Your copy", cut.Markup);
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("tablet")]
    [InlineData("phone")]
    public async Task ThePageBottom_HasNoHistoryNoTypeOrLibraryRowsAndAsksTheEngineForNothing(string formFactor)
    {
        await UseAsync(formFactor);

        var cut = RenderTab(Model(copy: One(MovieBlock()), canEdit: true));

        Assert.Empty(cut.FindAll(".tl-item-history"));
        Assert.Empty(cut.FindAll(".tl-detail-dl"));
        Assert.DoesNotContain("Library Status", cut.Markup);
        Assert.DoesNotContain("About this", cut.Markup);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), _stub!.Calls);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetMediaEditorWorkVersionsAsync), _stub.Calls);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetLibraryItemDetailAsync), _stub.Calls);
    }

    [Fact]
    public async Task AShow_DrawsTheEnginesSummaryAsItIs_ChipsForTheCommonQualityAndTheExceptionsInThePanel()
    {
        await UseAsync("desktop");
        var block = new DetailYourCopyBlockViewModel
        {
            Kind = DetailYourCopyKinds.Show,
            Chips = ["62 episodes", "4K", "HDR10", "MKV"],
            Groups =
            [
                new DetailYourCopyGroupViewModel { Key = DetailYourCopyGroupKeys.Video, Lines = ["Mostly 3840 × 2160 · HEVC", "12 episodes in 1080p"] },
                new DetailYourCopyGroupViewModel { Key = DetailYourCopyGroupKeys.AudioSubtitles, Lines = ["English · AAC 5.1"] },
                new DetailYourCopyGroupViewModel { Key = DetailYourCopyGroupKeys.Files, Lines = ["62 files · MKV · 214 GB", "Total · 44h 20m"] },
            ],
        };
        var cut = RenderTab(Model(DetailEntityType.TvShow, One(block)));

        Assert.Contains("62 episodes", cut.Find(".tl-your-copy__chips").TextContent);
        cut.Find(".tl-your-copy__toggle").Click();

        Assert.Equal(["Video", "Audio and subtitles", "Files"], cut.FindAll(".tl-your-copy__group h3").Select(title => title.TextContent.Trim()));
        Assert.Contains("12 episodes in 1080p", cut.Markup);
        Assert.Contains("Total · 44h 20m", cut.Markup);
        // A show has no single file, so the panel names none.
        Assert.Empty(cut.FindAll(".tl-your-copy__file"));
    }

    [Theory]
    [InlineData(DetailYourCopyKinds.Album, new[] { DetailYourCopyGroupKeys.Audio, DetailYourCopyGroupKeys.Tracks, DetailYourCopyGroupKeys.Files }, new[] { "Audio", "Tracks", "Files" })]
    [InlineData(DetailYourCopyKinds.Book, new[] { DetailYourCopyGroupKeys.Book, DetailYourCopyGroupKeys.Reading, DetailYourCopyGroupKeys.File }, new[] { "Book", "Reading", "File" })]
    public async Task AnAlbumAndABook_ShowTheirOwnThreeGroups(string kind, string[] keys, string[] titles)
    {
        await UseAsync("desktop");
        var block = new DetailYourCopyBlockViewModel
        {
            Kind = kind,
            Chips = ["FLAC"],
            Groups = keys.Select(key => new DetailYourCopyGroupViewModel { Key = key, Lines = ["A line"] }).ToList(),
        };
        var cut = RenderTab(Model(kind == DetailYourCopyKinds.Album ? DetailEntityType.MusicAlbum : DetailEntityType.Book, One(block)));

        cut.Find(".tl-your-copy__toggle").Click();

        Assert.Equal(titles, cut.FindAll(".tl-your-copy__group h3").Select(title => title.TextContent.Trim()));
    }

    [Fact]
    public async Task ASeries_ShowsSeriesFactsAndNoTypeOrStatusRows()
    {
        await UseAsync("desktop");
        var model = new DetailPageViewModel
        {
            EntityType = DetailEntityType.BookSeries,
            SequencePlacement = new SequencePlacementViewModel
            {
                ItemPluralLabel = "Books",
                TotalKnownItems = 9,
                OrderedItems =
                [
                    new SequenceItemViewModel { Id = Guid.NewGuid().ToString("D"), Title = "One", PublicationDate = "2011", IsOwned = true },
                    new SequenceItemViewModel { Id = Guid.NewGuid().ToString("D"), Title = "Two", PublicationDate = "2021", IsOwned = false },
                ],
            },
        };

        var cut = RenderTab(model);

        var text = cut.Find(".tl-file-facts__list").TextContent;
        Assert.Contains("1 of 9 books", text);
        Assert.Contains("2011", text);
        Assert.Contains("2021", text);
        Assert.Empty(cut.FindAll(".tl-detail-dl"));
        Assert.Empty(cut.FindAll(".tl-your-copy"));
        Assert.DoesNotContain("BookSeries", cut.Markup);
    }

    [Fact]
    public async Task APerson_HasNoDetailsFactsBlockAndSourcesStayInTheLeftColumn()
    {
        await UseAsync("desktop");
        var model = new DetailPageViewModel
        {
            EntityType = DetailEntityType.Person,
            PersonDetails = new PersonDetailFacts { Occupation = "Novelist", Nationality = "Canadian" },
        };

        var cut = RenderTab(model);

        Assert.Empty(cut.FindAll(".tl-detail-dl"));
        Assert.DoesNotContain("Canonical identity", cut.Markup);
        var css = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Details/DetailsTab.razor.css"));
        Assert.Contains("grid-column: 1 / -1", css);
        Assert.Contains("justify-self: start", css);
    }

    [Fact]
    public void ThePhoneRowIsOneTapTargetAndTheGroupsStackOnNarrowWindows()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Details/YourCopyRow.razor.css"));

        // The File details control's box is stretched over the whole row on a phone (600 px and narrower).
        var phone = css[css.IndexOf("@media (max-width: 600px)", StringComparison.Ordinal)..];
        Assert.Contains(".tl-your-copy__action::after", phone);
        Assert.Contains("inset: 0", phone);
        Assert.Contains("@media (max-width: 840px)", css);
        Assert.Contains("grid-template-columns: minmax(0, 1fr)", css);
        Assert.Contains("overflow-wrap: anywhere", css);
        Assert.DoesNotContain("overflow-x: auto", css);
    }
}
