using Bunit;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Contents and Files tabs of the universal editor window (v4 D3, WP5, acceptance A3): Contents lists the children of a season with a
/// picture, number, title, matched mark and size, pages in as it scrolls, and ticking rows turns the editor into "Editing 5 episodes". Files
/// lists the item's files with folder, size, quality and the Read-only mark, numbered when there are several. The Engine is a hand-written
/// stub; nothing is written anywhere.
/// </summary>
public sealed class EditorContentsAndFilesTests : AsyncBunitContext
{
    private static readonly Guid ShowId = EditTrailTestData.ShowId;
    private static readonly Guid SeasonOne = EditTrailTestData.SeasonOneId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<string> _asked = [];

    public EditorContentsAndFilesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    // Season 1 holds `count` episodes: the first four matched, the rest not; each has a size, and pages hold `pageSize` of them.
    private static void RegisterLongSeason(EngineApiClientStub stub, List<string> asked, int count, int pageSize)
    {
        EditTrailTestData.RegisterFamily(stub, asked);
        stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorOwnedChildrenAsync), args =>
        {
            var parent = (Guid)args![0]!;
            var page = (int)args[2]!;
            asked.Add($"children:{parent:N}:page{page}");
            var all = parent == SeasonOne
                ? Enumerable.Range(1, count).Select(number => Episode(number)).ToList()
                : [];
            var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new MediaEditorOwnedChildSearchDto { ParentEntityId = parent, Page = page, PageSize = pageSize, TotalCount = all.Count, Items = items });
        });
    }

    private static MediaEditorOwnedChildDto Episode(int number) => new()
    {
        WorkId = Guid.Parse($"00000000-0000-0000-0000-0000000001{number:00}"),
        AssetId = Guid.NewGuid(),
        Title = $"Episode {number}",
        MatchedNumber = number.ToString(),
        SourceFileName = $"S01E{number:00}.mkv",
        SourceFilePath = $"/library/S01E{number:00}.mkv",
        MatchState = number <= 4 ? "matched" : "unmatched",
        MatchReason = number <= 4 ? null : "No show, season or episode on a provider matches this file yet.",
        FileSizeBytes = 1_500_000_000L + number,
    };

    private async Task<EditorHost> OpenSeasonAsync(IEngineApiClient api)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var host = new EditorHost(api, _device, new ServiceCollection().BuildServiceProvider(), NullLogger<EditorHost>.Instance);
        var binding = new EditorBinding(_device)
        {
            EntityId = SeasonOne,
            HasPermission = true,
            MediaTypes = () => ["TV"],
            Trail = () => [new EditTrailItem(ShowId, "Le Continental", EditTrailKinds.Show), new EditTrailItem(SeasonOne, "Season 1", EditTrailKinds.Season)],
        };
        Assert.True(await host.OpenAsync(binding, SeasonOne));
        await host.Family.EnsureAsync(SeasonOne, [ShowId]);
        return host;
    }

    private IRenderedComponent<EditorContentsTab> RenderContents(EditorHost host) =>
        Render<EditorContentsTab>(parameters => parameters
            .Add(tab => tab.Flow, host.Flow)
            .Add(tab => tab.Family, host.Family)
            .Add(tab => tab.Batch, host.Batch)
            .Add(tab => tab.ParentId, SeasonOne)
            .Add(tab => tab.CanEdit, true));

    [Fact]
    public async Task Contents_ListsTheChildren_WithNumberTitleMatchedMarkAndSize()
    {
        var host = await OpenSeasonAsync(EngineApiClientStub.Create(stub => RegisterLongSeason(stub, _asked, 6, 50)));

        var tab = RenderContents(host);

        tab.WaitForAssertion(() => Assert.Equal(6, tab.FindAll("tr.tl-contents__row").Count));
        var first = tab.FindAll("tr.tl-contents__row")[0];
        Assert.Equal("1", first.QuerySelector(".tl-contents__num")!.TextContent.Trim());
        Assert.Contains("Episode 1", first.QuerySelector(".tl-contents__title")!.TextContent);
        Assert.Contains("Matched", first.TextContent);
        Assert.NotNull(first.QuerySelector("img"));
        Assert.Contains("GB", first.QuerySelector(".tl-contents__size")!.TextContent);
        var fifth = tab.FindAll("tr.tl-contents__row")[4];
        Assert.Contains("Not matched", fifth.TextContent);
    }

    [Fact]
    public async Task TickingFiveEpisodes_ShowsEditingFiveEpisodes()
    {
        var host = await OpenSeasonAsync(EngineApiClientStub.Create(stub => RegisterLongSeason(stub, _asked, 6, 50)));
        var tab = RenderContents(host);
        tab.WaitForAssertion(() => Assert.Equal(6, tab.FindAll("tr.tl-contents__row").Count));

        for (var index = 0; index < 5; index++)
        {
            tab.FindAll("tr.tl-contents__row")[index].QuerySelector("input[type=checkbox]")!.Change(true);
        }

        tab.WaitForAssertion(() => Assert.Equal("Editing 5 episodes", tab.Find(".tl-contents__count").TextContent.Trim()));
        Assert.Equal(5, host.Batch.Count);
        Assert.All(host.Batch.Rows, row => Assert.Equal(MediaEngine.Contracts.Details.DetailEntityType.TvEpisode, row.Kind));
    }

    [Fact]
    public async Task ChoosingWhatToChange_OpensTheEditPanel_AndRemembersTheTabToComeBackTo()
    {
        var host = await OpenSeasonAsync(EngineApiClientStub.Create(stub => RegisterLongSeason(stub, _asked, 6, 50)));
        var tab = RenderContents(host);
        tab.WaitForAssertion(() => Assert.Equal(6, tab.FindAll("tr.tl-contents__row").Count));
        tab.FindAll("tr.tl-contents__row")[0].QuerySelector("input[type=checkbox]")!.Change(true);
        tab.FindAll("tr.tl-contents__row")[1].QuerySelector("input[type=checkbox]")!.Change(true);

        tab.Find(".tl-contents__edit").Click();

        Assert.Equal(BulkPanelKind.Edit, host.Batch.Panel);
        Assert.Equal(EditTool.Contents, host.Batch.ReturnTool);
    }

    [Fact]
    public async Task Contents_PagesInTheNextRows_WhenShowMoreIsPressed()
    {
        var host = await OpenSeasonAsync(EngineApiClientStub.Create(stub => RegisterLongSeason(stub, _asked, 7, 3)));
        var tab = RenderContents(host);
        tab.WaitForAssertion(() => Assert.Equal(3, tab.FindAll("tr.tl-contents__row").Count));
        Assert.Contains("Show more (4 left)", tab.Find(".tl-contents__more").TextContent);

        tab.Find(".tl-contents__more button").Click();

        tab.WaitForAssertion(() => Assert.Equal(6, tab.FindAll("tr.tl-contents__row").Count));
        Assert.Contains($"children:{SeasonOne:N}:page2", _asked);
    }

    [Fact]
    public async Task WithoutEditPermission_TheRowsAreListedWithNoTickBoxes()
    {
        var host = await OpenSeasonAsync(EngineApiClientStub.Create(stub => RegisterLongSeason(stub, _asked, 3, 50)));

        var tab = Render<EditorContentsTab>(parameters => parameters
            .Add(t => t.Flow, host.Flow)
            .Add(t => t.Family, host.Family)
            .Add(t => t.Batch, host.Batch)
            .Add(t => t.ParentId, SeasonOne)
            .Add(t => t.CanEdit, false));

        tab.WaitForAssertion(() => Assert.Equal(3, tab.FindAll("tr.tl-contents__row").Count));
        Assert.Empty(tab.FindAll("input[type=checkbox]"));
    }

    [Fact]
    public void Files_ListsNameFolderSizeQuality_AndMarksAReadOnlyFile()
    {
        var item = Guid.NewGuid();
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorWorkVersionsAsync), _ =>
            Task.FromResult<MediaEditorWorkVersionSelectorDto?>(new MediaEditorWorkVersionSelectorDto
            {
                WorkId = item,
                Editions =
                [
                    new MediaEditorEditionSelectorDto
                    {
                        AssetCount = 2,
                        Assets =
                        [
                            new MediaEditorAssetSelectorDto { AssetId = Guid.NewGuid(), FileName = "Part 1.m4b", Folder = "/media/audiobooks/Dune", FileSizeBytes = 400_000_000, TechnicalLabel = "M4B", ReadOnly = true },
                            new MediaEditorAssetSelectorDto { AssetId = Guid.NewGuid(), FileName = "Part 2.m4b", Folder = "/media/audiobooks/Dune", FileSizeBytes = 350_000_000, TechnicalLabel = "M4B" },
                        ],
                    },
                ],
            })));
        Services.AddSingleton(api);

        var tab = Render<EditorFilesTab>(parameters => parameters.Add(t => t.EntityId, item));

        tab.WaitForAssertion(() => Assert.Equal(2, tab.FindAll("tr.tl-files__row").Count));
        var rows = tab.FindAll("tr.tl-files__row");
        Assert.Equal("1", rows[0].QuerySelector(".tl-files__num")!.TextContent.Trim());
        Assert.Equal("Part 1.m4b", rows[0].QuerySelector(".tl-files__name")!.TextContent.Trim());
        Assert.Equal("/media/audiobooks/Dune", rows[0].QuerySelector(".tl-files__folder")!.TextContent.Trim());
        Assert.Contains("MB", rows[0].QuerySelector(".tl-files__size")!.TextContent);
        Assert.Equal("M4B", rows[0].QuerySelector(".tl-files__quality")!.TextContent.Trim());
        Assert.Contains("Read-only", rows[0].TextContent);
        Assert.DoesNotContain("Read-only", rows[1].TextContent);
        Assert.Empty(tab.FindAll("button"));
    }

    [Fact]
    public void Files_OnASeason_SaysTheFilesLiveOnTheEpisodes()
    {
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorWorkVersionsAsync), _ =>
            Task.FromResult<MediaEditorWorkVersionSelectorDto?>(null)));
        Services.AddSingleton(api);

        var tab = Render<EditorFilesTab>(parameters => parameters.Add(t => t.EntityId, SeasonOne).Add(t => t.IsGrouping, true));

        tab.WaitForAssertion(() => Assert.Contains("episodes, songs and books inside", tab.Find("[data-testid=files-empty]").TextContent));
    }
}
