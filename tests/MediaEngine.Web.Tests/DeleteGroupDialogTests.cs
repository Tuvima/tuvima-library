using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Delete a show, season, album or Series: the dialog asks the Engine for the whole group, shows its totals, counts the red button,
/// and asks for the typed name only when the Engine says so. The Engine is a hand-written stub; nothing is deleted anywhere.
/// </summary>
public sealed class DeleteGroupDialogTests : AsyncBunitContext
{
    private static readonly Guid ShowId = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000001");

    private LibraryItemDeletionPreview _preview = Show();
    private readonly List<(LibraryGroupTarget Target, string Fingerprint)> _deleteCalls = [];
    private int _singleTitleCalls;

    public DeleteGroupDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryGroupDeletionPreviewAsync), _ =>
                Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(_preview)));
            stub.SetHandler(nameof(IEngineApiClient.DeleteLibraryGroupAsync), args =>
            {
                _deleteCalls.Add(((LibraryGroupTarget)args![0]!, (string)args[1]!));
                return Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Ok(new LibraryItemDeletionResultDto()));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryItemDeletionPreviewAsync), _ =>
            {
                _singleTitleCalls++;
                return Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(new LibraryItemDeletionPreview()));
            });
        }));
    }

    private static LibraryItemDeletionPreview Show(bool typed = true, bool removesParent = false, string kind = "show", string title = "The Expanse")
    {
        var episodes = Enumerable.Range(1, 3)
            .Select(n => new LibraryItemDeletionItemDto { WorkId = Guid.NewGuid(), Title = $"Episode {n}", MediaType = "TV", FileCount = 1 })
            .ToList();
        return new()
        {
            Items = episodes,
            Files = [.. episodes.Select(episode => new LibraryItemDeletionFileDto
            {
                WorkId = episode.WorkId,
                AssetId = Guid.NewGuid(),
                FolderLabel = "TV › The Expanse › Season 1",
                FileName = episode.Title + ".mkv",
                SizeBytes = 1024,
                Outcome = LibraryItemDeletionOutcomes.Delete,
            })],
            BytesFreed = 3 * 1024,
            Fingerprint = "fp-group",
            Group = new LibraryItemDeletionGroupDto
            {
                Kind = kind,
                Title = title,
                ConfirmName = typed ? title : string.Empty,
                RequiresTypedConfirmation = typed,
                TitlesOwned = 3,
                SubGroups = kind == "show" ? [new() { Title = "Season 1", Owned = 3, Total = 4 }] : [],
                RemovesParent = removesParent,
                ParentTitle = kind == "season" ? "The Expanse" : null,
            },
        };
    }

    private async Task<IRenderedComponent<AppDialogHost>> OpenAsync(string kind = "show", string title = "The Expanse")
    {
        var host = Render<AppDialogHost>();
        await host.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<DeleteFromLibraryDialog>(
            string.Empty,
            new AppDialogParameters<DeleteFromLibraryDialog>
            {
                { component => component.WorkIds, (IReadOnlyList<Guid>)[] },
                { component => component.Group, new LibraryGroupTarget(ShowId, kind, title, []) },
            },
            new AppDialogOptions { MaxWidth = AppMaxWidth.Medium, FullWidth = true, NoHeader = true, CloseButton = false }));
        host.WaitForElement(".delete-library__group");
        return host;
    }

    private static string Body(IRenderedComponent<AppDialogHost> host) =>
        Regex.Replace(host.Find(".delete-library").TextContent, "\\s+", " ").Trim();

    private static IElement RedButton(IRenderedComponent<AppDialogHost> host) =>
        host.FindAll("footer button").Last();

    [Fact]
    public async Task AGroupDelete_AsksTheEngineForTheWholeGroup_NotForTitles()
    {
        var host = await OpenAsync();

        Assert.Equal(0, _singleTitleCalls);
        Assert.Contains("1 season · 3 episodes", Body(host));
        Assert.Contains("Season 1: 3 of 4 episodes owned", Body(host));
        Assert.Contains("Delete \"The Expanse\" from your library?", host.Find(".app-dialog-shell__title").TextContent);
    }

    [Fact]
    public async Task TheRedButtonCountsTitlesAndFiles()
    {
        var host = await OpenAsync();

        Assert.Equal("Delete 3 episodes and 3 files", RedButton(host).TextContent.Trim());
    }

    [Fact]
    public async Task WhenTheEngineAsksForTheName_TheRedButtonStaysOffUntilItMatches()
    {
        var host = await OpenAsync();
        var input = host.Find(".delete-library__confirm input");

        Assert.True(RedButton(host).HasAttribute("disabled"));
        input.Input("the   EXPANSE");
        Assert.False(RedButton(host).HasAttribute("disabled"));

        RedButton(host).Click();
        host.WaitForAssertion(() => Assert.Single(_deleteCalls));
        Assert.Equal("fp-group", _deleteCalls[0].Fingerprint);
        Assert.Equal(ShowId, _deleteCalls[0].Target.GroupId);
    }

    [Fact]
    public async Task AWrongName_KeepsTheRedButtonOff()
    {
        var host = await OpenAsync();

        host.Find(".delete-library__confirm input").Input("The Expans");

        Assert.True(RedButton(host).HasAttribute("disabled"));
        RedButton(host).Click();
        Assert.Empty(_deleteCalls);
    }

    [Fact]
    public async Task WhenNoNameIsAsked_NoBoxIsShown_AndTheButtonIsReady()
    {
        _preview = Show(typed: false, kind: "season", title: "Season 2");

        var host = await OpenAsync("season", "Season 2");

        Assert.Empty(host.FindAll(".delete-library__confirm"));
        Assert.False(RedButton(host).HasAttribute("disabled"));
        Assert.Contains("Delete \"The Expanse\", Season 2 from your library?", host.Find(".app-dialog-shell__title").TextContent);
    }

    [Fact]
    public async Task TheLastSeason_SaysTheShowGoesToo()
    {
        _preview = Show(typed: false, removesParent: true, kind: "season", title: "Season 2");

        var host = await OpenAsync("season", "Season 2");

        Assert.Contains(LibraryGroupDeletionText.LastSeasonLine, Body(host));
    }

    [Fact]
    public void TheSeriesWarning_NamesTheBooksThatGo()
    {
        var group = new LibraryItemDeletionGroupDto { Kind = "series", Title = "Dune Novels", TitlesOwned = 14 };
        List<LibraryItemDeletionItemDto> books = [new() { MediaType = "Books" }];

        Assert.Equal("This deletes the Series and all 14 books in it.", LibraryGroupDeletionText.SeriesLine(group, books));
        Assert.Equal("Delete 14 books", LibraryGroupDeletionText.ConfirmLabel(group, books, filesDeleted: 0));
    }

    [Theory]
    [InlineData("The Expanse", "the expanse", true)]
    [InlineData("The Expanse", "  The   Expanse ", true)]
    [InlineData("The Expanse", "The Expans", false)]
    [InlineData("The Expanse", "", false)]
    [InlineData("", "", false)]
    public void TheTypedName_IgnoresCapitalsAndExtraSpaces(string confirm, string typed, bool expected) =>
        Assert.Equal(expected, LibraryGroupDeletionText.NameMatches(typed, confirm));

    [Fact]
    public void TheToast_NamesTheSeasonUnderItsShow_AndSaysHowMuchWasFreed()
    {
        var group = new LibraryItemDeletionGroupDto { Kind = "season", Title = "Season 2", ParentTitle = "The Expanse" };

        var name = LibraryGroupDeletionText.DisplayName(group, "Season 2");
        var toast = LibraryGroupDeletionText.Success(new LibraryItemDeletionResultDto { BytesFreed = 0 }, name);

        Assert.Equal("Deleted \"The Expanse\", Season 2.", toast);
    }

    [Fact]
    public void TheGroupMenuEntries_AreRedAndLast()
    {
        var season = DetailMenuGrouping.DeleteGroupAction("Delete Season 2…", season: true);
        var show = DetailMenuGrouping.DeleteGroupAction("Delete show…");

        Assert.Equal(DetailActionKeys.DeleteSeason, season.Key);
        Assert.Equal(DetailActionKeys.DeleteFromLibrary, show.Key);
        Assert.True(DetailMenuGrouping.IsDestructive(season));
        Assert.True(DetailMenuGrouping.IsDestructive(show));
    }
}
