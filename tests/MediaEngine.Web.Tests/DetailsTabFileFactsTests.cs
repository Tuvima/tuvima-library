using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Details tab: file facts first (every device, one block per file), then a read-only
/// History that only people who may already see it get, on desktop-class screens only.
/// </summary>
public sealed class DetailsTabFileFactsTests : AsyncBunitContext
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly Guid ItemId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly DeviceContextService _device;
    private int _historyCalls;
    private int _detailCalls;
    private bool _throwOnDetail;
    private MediaEditorWorkVersionSelectorDto? _versions;
    private LibraryItemDetailViewModel? _detail;

    public DetailsTabFileFactsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorWorkVersionsAsync),
                _ => Task.FromResult(_versions));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryItemDetailAsync), _ =>
            {
                _detailCalls++;
                if (_throwOnDetail)
                {
                    throw new InvalidOperationException("engine unavailable");
                }

                return Task.FromResult(_detail);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), _ =>
            {
                _historyCalls++;
                return Task.FromResult<(List<LibraryItemHistoryDto> Items, string? Error)>((
                [
                    new LibraryItemHistoryDto
                    {
                        Id = "h1",
                        EntityId = ItemId,
                        OccurredAt = new DateTimeOffset(2026, 5, 2, 10, 30, 0, TimeSpan.Zero),
                        EventType = "ingested",
                        Label = "Added to the library",
                        ActorLabel = "System",
                    },
                ], null));
            });
        });
        _device = new DeviceContextService(api);
        Services.AddSingleton(api);
        Services.AddSingleton(_device);
    }

    private Task UseAsync(string formFactor) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));

    private static MediaEditorWorkVersionSelectorDto OneFile(string fileName, string label) => new()
    {
        WorkId = ItemId,
        Editions =
        [
            new MediaEditorEditionSelectorDto
            {
                EditionId = Guid.NewGuid(),
                AssetCount = 1,
                Assets = [new MediaEditorAssetSelectorDto { AssetId = Guid.NewGuid(), FileName = fileName, TechnicalLabel = label }],
            },
        ],
    };

    private static LibraryItemDetailViewModel Detail(string fileName) => new()
    {
        EntityId = ItemId,
        FileName = fileName,
        FilePath = $"/library/media/{fileName}",
        FileSizeBytes = 3_221_225_472,
        PlaybackSummary = new PlaybackTechnicalSummaryViewModel
        {
            VideoResolutionLabel = "1080p",
            VideoCodec = "HEVC",
            AudioLanguage = "English",
            AudioCodec = "AC3",
            AudioChannels = "5.1",
            SubtitleSummary = "English, French",
        },
    };

    private static DetailPageViewModel Model(DetailEntityType type, bool canEdit = false) => new()
    {
        Id = ItemId.ToString("D"),
        EntityType = type,
        Title = "Test",
        Facts = new DetailFactsViewModel { Runtime = "2h 11m", Language = "English" },
        OverflowActions = canEdit ? [new DetailAction { Key = "edit", Label = "Edit details" }] : [],
    };

    private IRenderedComponent<DetailsTab> RenderTab(DetailPageViewModel model)
    {
        var cut = Render<DetailsTab>(parameters => parameters.Add(tab => tab.Model, model));
        cut.WaitForState(() => cut.FindAll(".tl-file-facts__note[role='status']").Count == 0);
        return cut;
    }

    [Theory]
    [InlineData(DetailEntityType.Movie, "movie.mkv", "MKV")]
    [InlineData(DetailEntityType.TvEpisode, "s01e01.mp4", "MP4")]
    [InlineData(DetailEntityType.Book, "book.epub", "EPUB")]
    [InlineData(DetailEntityType.Audiobook, "book.m4b", "M4B")]
    [InlineData(DetailEntityType.ComicIssue, "issue.cbz", "CBZ")]
    [InlineData(DetailEntityType.Work, "track.flac", "FLAC")]
    public async Task EveryFileBearingTypeShowsItsFileFactsOnEveryDevice(DetailEntityType type, string fileName, string format)
    {
        foreach (var formFactor in new[] { "desktop", "tablet", "phone" })
        {
            _versions = OneFile(fileName, format);
            _detail = Detail(fileName);
            await UseAsync(formFactor);

            var cut = RenderTab(Model(type));

            var text = cut.Find(".tl-file-facts__list").TextContent;
            Assert.Contains(fileName, text);
            Assert.Contains(format, text);
            Assert.Contains("2h 11m", text);
            Assert.Contains("1080p (HEVC)", text);
            Assert.Contains("English (AC3 5.1)", text);
            Assert.Contains("English, French", text);
            Assert.Contains("3 GB", text);
            Assert.Single(cut.FindAll(".tl-file-facts__block"));
        }
    }

    [Fact]
    public async Task AFailedLoadShowsADegradedNoteInTheSamePanelAndKeepsTheRestOfTheTab()
    {
        _versions = OneFile("movie.mkv", "MKV");
        _throwOnDetail = true;
        await UseAsync("desktop");

        var cut = RenderTab(Model(DetailEntityType.Movie, canEdit: true));

        Assert.Contains("File details could not be loaded.", cut.Find(".tl-file-facts [role='alert']").TextContent);
        Assert.Empty(cut.FindAll(".tl-file-facts__list"));
        Assert.Contains("Library Status", cut.Markup);
    }

    [Fact]
    public async Task SeveralFilesGetOneBlockEach()
    {
        _versions = new MediaEditorWorkVersionSelectorDto
        {
            WorkId = ItemId,
            Editions =
            [
                new MediaEditorEditionSelectorDto
                {
                    EditionId = Guid.NewGuid(),
                    Label = "Theatrical",
                    AssetCount = 1,
                    Assets = [new MediaEditorAssetSelectorDto { AssetId = Guid.NewGuid(), FileName = "movie.mkv", TechnicalLabel = "MKV" }],
                },
                new MediaEditorEditionSelectorDto
                {
                    EditionId = Guid.NewGuid(),
                    Label = "Extended",
                    AssetCount = 1,
                    Assets = [new MediaEditorAssetSelectorDto { AssetId = Guid.NewGuid(), FileName = "movie-extended.mp4", TechnicalLabel = "MP4" }],
                },
            ],
        };
        _detail = Detail("movie.mkv");
        await UseAsync("phone");

        var cut = RenderTab(Model(DetailEntityType.Movie));

        var blocks = cut.FindAll(".tl-file-facts__block");
        Assert.Equal(2, blocks.Count);
        Assert.Equal(["movie.mkv", "movie-extended.mp4"], cut.FindAll(".tl-file-facts__title").Select(title => title.TextContent));
        // The stored technical facts belong to the file the item detail names; the other file shows what is known of it.
        Assert.Contains("1080p (HEVC)", blocks[0].TextContent);
        Assert.DoesNotContain("1080p (HEVC)", blocks[1].TextContent);
        Assert.Contains("Extended", blocks[1].TextContent);
    }

    [Fact]
    public async Task FilePathAndHistoryAreHiddenFromPeopleWhoCannotSeeThemToday()
    {
        _versions = OneFile("movie.mkv", "MKV");
        _detail = Detail("movie.mkv");
        await UseAsync("desktop");

        var cut = RenderTab(Model(DetailEntityType.Movie, canEdit: false));

        Assert.DoesNotContain("/library/media/movie.mkv", cut.Markup);
        Assert.DoesNotContain("Path", cut.Find(".tl-file-facts__list").TextContent);
        Assert.Empty(cut.FindAll(".tl-item-history"));
        Assert.Equal(0, _historyCalls);
    }

    [Fact]
    public async Task FilePathAndReadOnlyHistoryShowForPeopleWhoMaySeeThemOnDesktop()
    {
        _versions = OneFile("movie.mkv", "MKV");
        _detail = Detail("movie.mkv");
        await UseAsync("desktop");

        var cut = RenderTab(Model(DetailEntityType.Movie, canEdit: true));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-item-history__entry")));

        Assert.Contains("/library/media/movie.mkv", cut.Find(".tl-file-facts__list").TextContent);
        var history = cut.Find(".tl-item-history");
        Assert.Contains("Added to the library", history.TextContent);
        Assert.Empty(history.QuerySelectorAll("button, a, input"));
        Assert.Equal(1, _historyCalls);

        // File facts come first, then History.
        var order = cut.FindAll(".tl-file-facts, .tl-item-history").Select(section => section.ClassName ?? string.Empty).ToList();
        Assert.Contains("tl-file-facts", order[0]);
        Assert.Contains("tl-item-history", order[1]);
    }

    [Theory]
    [InlineData("tablet", true)]
    [InlineData("phone", false)]
    public async Task HistoryNeverShowsOutsideDesktopAndPhonesNeverShowThePath(string formFactor, bool pathShown)
    {
        _versions = OneFile("movie.mkv", "MKV");
        _detail = Detail("movie.mkv");
        await UseAsync(formFactor);

        var cut = RenderTab(Model(DetailEntityType.Movie, canEdit: true));

        Assert.Empty(cut.FindAll(".tl-item-history"));
        Assert.Equal(0, _historyCalls);
        var facts = cut.Find(".tl-file-facts__list").TextContent;
        Assert.Contains("movie.mkv", facts);
        Assert.Equal(pathShown, facts.Contains("/library/media/movie.mkv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStoredContainerShowsOnlyWhenItAddsToTheFormat()
    {
        _versions = OneFile("movie.mkv", "MKV");
        _detail = Detail("movie.mkv");
        _detail.CanonicalValues = [new LibraryItemCanonicalValueDto { Key = "container", Value = "Matroska" }];
        await UseAsync("desktop");
        var different = RenderTab(Model(DetailEntityType.Movie)).Find(".tl-file-facts__list").TextContent;
        Assert.Contains("Matroska", different);

        _detail.CanonicalValues = [new LibraryItemCanonicalValueDto { Key = "container", Value = "mkv" }];
        var same = RenderTab(Model(DetailEntityType.Movie)).Find(".tl-file-facts__list").TextContent;
        Assert.DoesNotContain("Container", same);
    }

    [Fact]
    public async Task ThePathLandsOnTheFileItBelongsToNotOnItsSibling()
    {
        _versions = new MediaEditorWorkVersionSelectorDto
        {
            WorkId = ItemId,
            Editions =
            [
                new MediaEditorEditionSelectorDto
                {
                    EditionId = Guid.NewGuid(),
                    AssetCount = 2,
                    Assets =
                    [
                        new MediaEditorAssetSelectorDto { AssetId = Guid.NewGuid(), FileName = "a.mp4", TechnicalLabel = "MP4" },
                        new MediaEditorAssetSelectorDto { AssetId = Guid.NewGuid(), FileName = "b.mkv", TechnicalLabel = "MKV" },
                    ],
                },
            ],
        };
        _detail = Detail("b.mkv");
        await UseAsync("desktop");

        var cut = RenderTab(Model(DetailEntityType.Movie, canEdit: true));

        var blocks = cut.FindAll(".tl-file-facts__block");
        Assert.DoesNotContain("/library/media/b.mkv", blocks[0].TextContent);
        Assert.DoesNotContain("1080p (HEVC)", blocks[0].TextContent);
        Assert.Contains("/library/media/b.mkv", blocks[1].TextContent);
        Assert.Contains("1080p (HEVC)", blocks[1].TextContent);
    }

    [Theory]
    [InlineData(DetailEntityType.TvShow)]
    [InlineData(DetailEntityType.TvSeason)]
    [InlineData(DetailEntityType.MusicAlbum)]
    [InlineData(DetailEntityType.BookSeries)]
    [InlineData(DetailEntityType.Collection)]
    public async Task GroupingsOwnNoFileSoTheyPointToTheItemsInside(DetailEntityType type)
    {
        _detail = Detail("episode.mkv");
        await UseAsync("desktop");

        var cut = RenderTab(Model(type, canEdit: true));

        Assert.Contains("Open an item inside it", cut.Find(".tl-file-facts").TextContent);
        Assert.Empty(cut.FindAll(".tl-file-facts__list"));
        Assert.Empty(cut.FindAll(".tl-item-history"));
        // A grouping never borrows one of its episodes' files.
        Assert.Equal(0, _detailCalls);
        Assert.Equal(0, _historyCalls);
    }

    [Fact]
    public async Task OwnedFormatsFillInWhenNoFileListIsStored()
    {
        _versions = null;
        _detail = null;
        await UseAsync("phone");
        var model = new DetailPageViewModel
        {
            Id = ItemId.ToString("D"),
            EntityType = DetailEntityType.Book,
            Title = "Test",
            OwnedFormats =
            [
                new OwnedFormatViewModel { DisplayName = "Ebook", FileFormat = "EPUB", PageCount = 320 },
                new OwnedFormatViewModel { DisplayName = "Audiobook", FileFormat = "M4B", Runtime = "11h 4m", ChapterCount = 30 },
            ],
        };

        var cut = RenderTab(model);

        var blocks = cut.FindAll(".tl-file-facts__block");
        Assert.Equal(2, blocks.Count);
        Assert.Contains("320", blocks[0].TextContent);
        Assert.Contains("11h 4m", blocks[1].TextContent);
    }

    [Fact]
    public void TheFactsListWrapsOnPhonesInsteadOfScrollingSideways()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Details/DetailsTab.razor.css"));

        Assert.Contains("grid-template-columns: minmax(5rem, 9rem) minmax(0, 1fr)", css);
        Assert.Contains("overflow-wrap: anywhere", css);
        Assert.Contains("@media (max-width: 600px)", css);
        Assert.DoesNotContain("overflow-x: auto", css);
        Assert.DoesNotContain("overflow-x: scroll", css);
        Assert.DoesNotContain("white-space: nowrap", css);
        Assert.DoesNotContain("white-space: pre", css);
    }
}
