using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.MediaEditor.Moves;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Move area of Match and Identity (details overlay spec D12 to D17): who sees it, the picker, the Match step and the one
/// confirmation with its preview. The Engine is a hand-written stub; nothing is moved anywhere.
/// </summary>
public sealed class ItemMoveAreaTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<LibraryMoveCommitRequest> _commits = [];
    private readonly List<LibraryMovePreviewRequest> _previews = [];
    private readonly Queue<LibraryMoveOutcome<LibraryMoveCommitResult>> _commitAnswers = new();
    private LibraryMovePreview _preview = LibraryMoveTestData.AlbumPreview();
    private string? _movedLine;
    private ItemMovedArgs? _moved;
    private LibraryMoveDestination? _currentDestination;

    public ItemMoveAreaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryMoveDestinationsAsync), _ =>
                Task.FromResult(LibraryMoveOutcome<LibraryMoveDestinationsResponse>.Ok(new LibraryMoveDestinationsResponse
                {
                    Destinations = [LibraryMoveTestData.Album()],
                    CurrentDestination = _currentDestination,
                })));
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryMoveAsync), args =>
            {
                _previews.Add((LibraryMovePreviewRequest)args![0]!);
                return Task.FromResult(LibraryMoveOutcome<LibraryMovePreview>.Ok(_preview));
            });
            stub.SetHandler(nameof(IEngineApiClient.MoveLibraryItemsAsync), args =>
            {
                _commits.Add((LibraryMoveCommitRequest)args![0]!);
                return Task.FromResult(_commitAnswers.Count > 0
                    ? _commitAnswers.Dequeue()
                    : LibraryMoveOutcome<LibraryMoveCommitResult>.Ok(new LibraryMoveCommitResult { MovedCount = 1 }));
            });
        }));
    }

    private Task UseDevice(string formFactor) => formFactor switch
    {
        "tablet" => _device.InitialiseAsync(new DeviceContextSnapshot("web", "tablet", true)),
        "phone" => _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true)),
        _ => _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true)),
    };

    private (IRenderedComponent<AppDialogHost> Host, IRenderedComponent<ItemMoveArea> Area) RenderArea(
        string mediaType = "Music", string scopeId = "track", bool unmatched = false)
    {
        var host = Render<AppDialogHost>();
        var area = Render<ItemMoveArea>(parameters => parameters
            .Add(component => component.ItemId, LibraryMoveTestData.SongId)
            .Add(component => component.MediaType, mediaType)
            .Add(component => component.ScopeId, scopeId)
            .Add(component => component.IsUnmatched, unmatched)
            .Add(component => component.OnMoved, EventCallback.Factory.Create<ItemMovedArgs>(this, moved =>
            {
                _moved = moved;
                _movedLine = moved.ResultLine;
            })));
        return (host, area);
    }

    private static IElement Button(IRenderedComponent<ItemMoveArea> area, string label) =>
        area.FindAll("button").Single(button => button.TextContent.Trim() == label);

    private static IElement DialogButton(IRenderedComponent<AppDialogHost> host, string label) =>
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == label);

    private static string DialogText(IRenderedComponent<AppDialogHost> host) =>
        Regex.Replace(host.Find(".app-confirm").TextContent, "\\s+", " ").Trim();

    // Opens the picker, chooses the album, and waits for the Match step.
    private static void ToMatchStep(IRenderedComponent<ItemMoveArea> area, string label = "Move to another album")
    {
        Button(area, label).Click();
        area.WaitForElement($"[data-destination-id='{LibraryMoveTestData.AlbumId}']").Click();
        area.WaitForElement(".tl-match-step");
    }

    // -- who sees it ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Music", "track", "Move to another album")]
    [InlineData("TV", "episode", "Move to another season or show")]
    [InlineData("Books", "book", "Add to a Series without a number")]
    [InlineData("Comics", "issue", "Add to a Series without a number")]
    [InlineData("Audiobooks", "audiobook", "Move to another audiobook")]
    [InlineData("Movies", "movie", "Add to a Series without a number")]
    public async Task OnADesktop_TheMoveEntryNamesTheKindOfPlace(string mediaType, string scopeId, string expected)
    {
        await UseDevice("desktop");
        var (_, area) = RenderArea(mediaType, scopeId);

        Assert.Equal(expected, area.Find(".tl-item-move__copy strong").TextContent.Trim());
        Assert.Contains(area.FindAll("button"), button => button.TextContent.Trim() == expected);
    }

    [Theory]
    [InlineData("tablet")]
    [InlineData("phone")]
    public async Task OnATabletOrPhone_TheMoveEntryIsNotShown(string formFactor)
    {
        await UseDevice(formFactor);
        var (_, area) = RenderArea();

        Assert.Empty(area.FindAll(".tl-item-move"));
        Assert.Empty(area.FindAll("button"));
    }

    [Theory]
    [InlineData("album")]
    [InlineData("season")]
    [InlineData("series")]
    public async Task ForAWholeAlbumSeasonOrSeries_TheMoveEntryIsNotShown(string scopeId)
    {
        await UseDevice("desktop");
        var (_, area) = RenderArea(scopeId: scopeId);

        Assert.Empty(area.FindAll(".tl-item-move"));
    }

    [Fact]
    public async Task ANotMatchedItem_OffersMatchAndCarriesTheQuietTag()
    {
        await UseDevice("desktop");
        var (_, area) = RenderArea(unmatched: true);

        Assert.Equal("Match", Regex.Replace(area.Find(".tl-item-move__copy strong").TextContent, "\\s+", " ").Replace("Not matched", string.Empty).Trim());
        Assert.Equal("Not matched", area.Find(".tl-not-matched-tag").TextContent.Trim());
        Assert.Contains(area.FindAll("button"), button => button.TextContent.Trim() == "Match");
    }

    // -- the move, with its confirmation ----------------------------------------------------------

    [Fact]
    public async Task AMoveConfirmsWithItsPreviewAndThenSendsTheChoiceTheEngineWillCheck()
    {
        await UseDevice("desktop");
        var (host, area) = RenderArea();
        ToMatchStep(area);

        Assert.Equal(LibraryMoveTestData.AlbumId, _previews.Single().DestinationId);
        Assert.Equal([LibraryMoveTestData.SongId], _previews.Single().ItemIds);
        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");

        var text = DialogText(host);
        Assert.Contains("Move Paranoid Android to OK Computer as Track 2?", text, StringComparison.Ordinal);
        Assert.Contains("It leaves The Best Of.", text, StringComparison.Ordinal);
        Assert.Contains("Files stay where they are.", text, StringComparison.Ordinal);
        Assert.Empty(_commits);

        DialogButton(host, "Move").Click();
        area.WaitForAssertion(() => Assert.NotNull(_movedLine));

        var commit = Assert.Single(_commits);
        Assert.Equal("token-1", commit.PreviewToken);
        var row = Assert.Single(commit.Rows);
        Assert.Equal(LibraryMoveModes.Match, row.Mode);
        Assert.Equal("t2", row.EntryKey);
        Assert.Equal("Moved Paranoid Android to OK Computer as Track 2.", _movedLine);
    }

    [Fact]
    public async Task ABookIsAddedToASeriesOnlyAnywayWithoutANumber_AndTheStepSaysWhy()
    {
        await UseDevice("desktop");
        _preview = LibraryMoveTestData.SeriesPreview();
        var (host, area) = RenderArea("Books", "book");

        Assert.Contains("Match and Identity", area.Find(".tl-item-move__copy span").TextContent, StringComparison.Ordinal);
        ToMatchStep(area, "Add to a Series without a number");

        Assert.Equal("none", area.Find(".tl-match-step").GetAttribute("data-list-kind"));
        Assert.Contains("Series are built from each title's match.", area.Find(".tl-match-step__lead").TextContent, StringComparison.Ordinal);
        Assert.Empty(area.FindAll("[data-entry-key]"));
        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");

        Assert.Contains("won't get a number", DialogText(host), StringComparison.Ordinal);
        Assert.Equal(["Cancel", "Add anyway"], host.FindAll(".app-dialog-shell__footer button").Select(button => button.TextContent.Trim()));
        DialogButton(host, "Add anyway").Click();
        area.WaitForAssertion(() => Assert.NotNull(_movedLine));

        var row = Assert.Single(Assert.Single(_commits).Rows);
        Assert.Equal(LibraryMoveModes.AddAnyway, row.Mode);
        Assert.Null(row.EntryKey);
    }

    [Fact]
    public async Task CancellingTheSeriesConfirmationMovesNothing_AndNeverGoesBackToAMatchList()
    {
        await UseDevice("desktop");
        _preview = LibraryMoveTestData.SeriesPreview();
        var (host, area) = RenderArea("Movies", "movie");
        ToMatchStep(area, "Add to a Series without a number");

        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");
        DialogButton(host, "Cancel").Click();

        area.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        Assert.Empty(_commits);
        Assert.True(!Button(area, "Apply").HasAttribute("disabled"));
    }

    [Fact]
    public async Task ABookAddedAnywayEarlier_StillOnlyOffersAddingToASeries_NotMatchOrChangeNumber()
    {
        await UseDevice("desktop");
        var (_, area) = RenderArea("Books", "book", unmatched: true);

        var labels = area.FindAll("button").Select(button => button.TextContent.Trim()).ToList();

        Assert.Equal(["Add to a Series without a number"], labels);
        Assert.DoesNotContain("Match", labels);
        Assert.DoesNotContain("Change number", labels);
    }

    [Fact]
    public async Task BeforeTheDialogTheEngineIsAskedAgainWithTheChoiceMade_AndTheDialogAndCommitUseThatPreview()
    {
        await UseDevice("desktop");
        var (host, area) = RenderArea();
        ToMatchStep(area);

        // The person picks track 3 instead of the Engine's guess (track 2). Only the preview for that choice knows the file's new name.
        area.Find("[data-entry-key='t3']").Click();
        _preview = LibraryMoveTestData.AlbumPreview(
            rows:
            [
                LibraryMoveTestData.Row(fileChange: new LibraryMoveFileChange
                {
                    From = "Music/The Best Of/03 - Paranoid Android.flac",
                    To = "Music/OK Computer/03 - Paranoid Android.flac",
                }),
            ],
            token: "token-2");
        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");

        Assert.Equal(2, _previews.Count);
        var asked = _previews[1];
        Assert.Equal(LibraryMoveTestData.AlbumId, asked.DestinationId);
        var choice = Assert.Single(asked.Choices!);
        Assert.Equal(LibraryMoveTestData.SongId, choice.ItemId);
        Assert.Equal(LibraryMoveModes.Match, choice.Mode);
        Assert.Equal("t3", choice.EntryKey);
        Assert.Contains("The file will be renamed to 03 - Paranoid Android.flac and moved to OK Computer.", DialogText(host), StringComparison.Ordinal);

        DialogButton(host, "Move").Click();
        area.WaitForAssertion(() => Assert.NotNull(_movedLine));

        var commit = Assert.Single(_commits);
        Assert.Equal("token-2", commit.PreviewToken);
        Assert.Equal("t3", Assert.Single(commit.Rows).EntryKey);
    }

    [Fact]
    public async Task IfTheChoiceNoLongerHoldsWhenTheEngineIsAskedAgain_NoDialogOpensAndTheFreshStepIsShown()
    {
        await UseDevice("desktop");
        var (host, area) = RenderArea();
        ToMatchStep(area);

        // Track 2 got a file while the person was deciding: the new preview offers it as taken, so the choice cannot stay.
        _preview = LibraryMoveTestData.AlbumPreview(rows: [LibraryMoveTestData.Row(suggested: null)]);
        _preview.Entries[1] = LibraryMoveTestData.Entry("t2", "2", "Paranoid Android", LibraryMoveTestData.OtherSongId, "Paranoid Android (live)", canAddVersion: false);
        Button(area, "Apply").Click();

        area.WaitForAssertion(() => Assert.Contains("Something changed while this was open. Check again.", area.Markup, StringComparison.Ordinal));
        Assert.Empty(host.FindAll(".app-confirm"));
        Assert.Empty(_commits);
    }

    [Fact]
    public async Task TheConfirmationSaysWhatHappensToTheFile_WhenTheEngineGivesAFileChange()
    {
        await UseDevice("desktop");
        _preview = LibraryMoveTestData.AlbumPreview(rows:
        [
            LibraryMoveTestData.Row(fileChange: new LibraryMoveFileChange
            {
                From = "Music/The Best Of/03 - Paranoid Android.flac",
                To = "Music/OK Computer/02 - Paranoid Android.flac",
            }),
        ]);
        var (host, area) = RenderArea();
        ToMatchStep(area);

        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");

        var text = DialogText(host);
        Assert.Contains("The file will be renamed to 02 - Paranoid Android.flac and moved to OK Computer.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Files stay where they are.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheConfirmationSaysWhyAFileIsNotTouched_WhenTheEngineGivesAFileNote()
    {
        await UseDevice("desktop");
        _preview = LibraryMoveTestData.AlbumPreview(rows:
        [
            LibraryMoveTestData.Row(fileNote: "As-is folder: the file stays where it is"),
        ]);
        var (host, area) = RenderArea();
        ToMatchStep(area);

        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");

        Assert.Contains("As-is folder: the file stays where it is.", DialogText(host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheConfirmationNamesAContainerThatWillBeLeftEmptyAndRemoved()
    {
        await UseDevice("desktop");
        _preview = LibraryMoveTestData.AlbumPreview(warnings: ["The Best Of will be empty and removed"]);
        var (host, area) = RenderArea();
        ToMatchStep(area);

        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");

        Assert.Contains("The Best Of will be empty and removed.", DialogText(host), StringComparison.Ordinal);
    }

    // -- Add anyway (D13) ---------------------------------------------------------------------------

    [Fact]
    public async Task AddAnywayConfirmsThatThereWillBeNoNumber_AndSendsNoEntry()
    {
        await UseDevice("desktop");
        _preview = LibraryMoveTestData.AlbumPreview(rows: [LibraryMoveTestData.Row(suggested: null)]);
        var (host, area) = RenderArea();
        ToMatchStep(area);

        Button(area, "Add anyway").Click();
        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");

        Assert.Contains("This won't get a track number or details from OK Computer. You can match it later.", DialogText(host), StringComparison.Ordinal);
        Assert.Equal(["Match instead", "Add anyway"], host.FindAll(".app-dialog-shell__footer button").Select(button => button.TextContent.Trim()));

        DialogButton(host, "Add anyway").Click();
        area.WaitForAssertion(() => Assert.NotNull(_movedLine));

        var row = Assert.Single(Assert.Single(_commits).Rows);
        Assert.Equal(LibraryMoveModes.AddAnyway, row.Mode);
        Assert.Null(row.EntryKey);
        Assert.Contains("without a number", _movedLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchInsteadGoesBackToTheMatchStepWithNothingMoved()
    {
        await UseDevice("desktop");
        _preview = LibraryMoveTestData.AlbumPreview(rows: [LibraryMoveTestData.Row(suggested: null)]);
        var (host, area) = RenderArea();
        ToMatchStep(area);

        Button(area, "Add anyway").Click();
        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");
        DialogButton(host, "Match instead").Click();

        area.WaitForAssertion(() =>
        {
            Assert.Empty(host.FindAll(".app-confirm"));
            Assert.True(Button(area, "Apply").HasAttribute("disabled"));
        });
        Assert.Empty(_commits);
    }

    // -- the Engine says something changed (D17, Delete D6) -------------------------------------------

    [Fact]
    public async Task WhenSomethingChanged_TheFreshPreviewIsShownWithAShortLineAndAskedAgain()
    {
        await UseDevice("desktop");
        var fresh = LibraryMoveTestData.AlbumPreview(token: "token-2", warnings: ["Leaves The Best Of", "Season 1 will be empty and removed"]);
        _commitAnswers.Enqueue(LibraryMoveOutcome<LibraryMoveCommitResult>.Conflict(fresh, "Something changed since you opened this."));
        var (host, area) = RenderArea();
        ToMatchStep(area);

        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");
        DialogButton(host, "Move").Click();

        host.WaitForAssertion(() => Assert.Contains("Something changed while this was open. Check again.", DialogText(host), StringComparison.Ordinal));
        Assert.Contains("Season 1 will be empty and removed.", DialogText(host), StringComparison.Ordinal);
        Assert.Equal("token-1", Assert.Single(_commits).PreviewToken);
        Assert.Null(_movedLine);

        DialogButton(host, "Move").Click();
        area.WaitForAssertion(() => Assert.NotNull(_movedLine));

        Assert.Equal(["token-1", "token-2"], _commits.Select(commit => commit.PreviewToken));
        Assert.Equal("t2", _commits[1].Rows.Single().EntryKey);
    }

    [Fact]
    public async Task ARefusalForTheWrongPermission_ShowsAPlainLineInsteadOfFailingSilently()
    {
        await UseDevice("desktop");
        _commitAnswers.Enqueue(LibraryMoveOutcome<LibraryMoveCommitResult>.Failed(LibraryMoveOutcomeStatus.Forbidden));
        var (host, area) = RenderArea();
        ToMatchStep(area);

        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");
        DialogButton(host, "Move").Click();

        area.WaitForAssertion(() => Assert.Contains("You can't change this item's identity.", area.Markup, StringComparison.Ordinal));
        Assert.Null(_movedLine);
    }

    [Fact]
    public async Task AnItemStillBeingProcessed_CannotBeApplied_AndTheReasonIsShown()
    {
        await UseDevice("desktop");
        _preview = LibraryMoveTestData.AlbumPreview(rows: [LibraryMoveTestData.Row(blocked: "Still being processed")]);
        var (_, area) = RenderArea();
        ToMatchStep(area);

        Assert.True(Button(area, "Apply").HasAttribute("disabled"));
        Assert.Contains("Still being processed", area.Markup, StringComparison.Ordinal);
    }

    // -- matching and renumbering within the item's own place (D13, section 5) ----------------------------

    [Fact]
    public async Task MatchOnANotMatchedItem_OpensTheMatchStepOnItsOwnPlaceDirectly_AndMovingElsewhereStaysOneClickAway()
    {
        await UseDevice("desktop");
        _currentDestination = LibraryMoveTestData.Album();
        _preview = LibraryMoveTestData.AlbumHoldingTheSongPreview();
        var (host, area) = RenderArea(unmatched: true);

        Assert.Contains(area.FindAll("button"), button => button.TextContent.Trim() == "Move to another album");
        Button(area, "Match").Click();
        area.WaitForElement(".tl-match-step");

        Assert.Empty(area.FindAll("[data-destination-id]"));
        Assert.Equal(LibraryMoveTestData.AlbumId, _previews.Single().DestinationId);
        Assert.DoesNotContain(area.FindAll("button"), button => button.TextContent.Trim() == "Add anyway");

        area.Find("[data-entry-key='t4']").Click();
        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");
        Assert.Contains("Match Paranoid Android to Track 4 in OK Computer?", DialogText(host), StringComparison.Ordinal);
        DialogButton(host, "Match").Click();
        area.WaitForAssertion(() => Assert.NotNull(_movedLine));
        Assert.Equal("Matched Paranoid Android to Track 4 in OK Computer.", _movedLine);
    }

    [Fact]
    public async Task MatchOnANotMatchedItemWithNoPlaceOfItsOwn_FallsBackToThePicker()
    {
        await UseDevice("desktop");
        _currentDestination = null;
        var (_, area) = RenderArea(unmatched: true);

        Button(area, "Match").Click();

        area.WaitForElement($"[data-destination-id='{LibraryMoveTestData.AlbumId}']");
        Assert.Empty(_previews);
    }

    [Fact]
    public async Task ChangeNumberShowsOnlyForAnItemTheEngineSaysSitsInAPlaceWithAList_AndOpensThatPlace()
    {
        await UseDevice("desktop");
        var (_, withoutPlace) = RenderArea();
        Assert.DoesNotContain(withoutPlace.FindAll("button"), button => button.TextContent.Trim() == "Change number");

        _currentDestination = LibraryMoveTestData.Album();
        _preview = LibraryMoveTestData.AlbumHoldingTheSongPreview();
        var (_, area) = RenderArea();
        area.WaitForAssertion(() => Assert.Contains(area.FindAll("button"), button => button.TextContent.Trim() == "Change number"));
        Button(area, "Change number").Click();

        area.WaitForElement(".tl-match-step");
        Assert.Equal(LibraryMoveTestData.AlbumId, _previews.Single().DestinationId);
        Assert.Contains("is-own", area.Find("[data-entry-key='t2']").ClassList);
    }

    // -- a refusal is shown where it happened, and never loops -----------------------------------------

    [Fact]
    public async Task ARefusalWithAReason_IsShownInTheMatchStep_AndNothingAsksAgain()
    {
        await UseDevice("desktop");
        const string reason = "You changed this item's details, so it can't be merged into an existing entry. Add it anyway without a number instead.";
        _commitAnswers.Enqueue(LibraryMoveOutcome<LibraryMoveCommitResult>.Failed(LibraryMoveOutcomeStatus.Invalid, reason));
        var (host, area) = RenderArea();
        ToMatchStep(area);

        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");
        DialogButton(host, "Move").Click();

        area.WaitForAssertion(() => Assert.Contains(reason, area.Markup, StringComparison.Ordinal));
        Assert.Single(_commits);
        Assert.Empty(host.FindAll(".app-confirm"));
        Assert.DoesNotContain("Something changed", area.Markup, StringComparison.Ordinal);
        Assert.Null(_movedLine);
    }

    // -- what the host is told after a move -----------------------------------------------------------

    [Fact]
    public async Task AfterAMove_TheHostIsToldTheWorkTheItemNowLivesInAndWhetherItIsNotMatched()
    {
        await UseDevice("desktop");
        var survivor = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
        _commitAnswers.Enqueue(LibraryMoveOutcome<LibraryMoveCommitResult>.Ok(new LibraryMoveCommitResult
        {
            MovedCount = 1,
            Rows = [new LibraryMoveRowResult { ItemId = LibraryMoveTestData.SongId, Outcome = LibraryMoveOutcomes.Matched, ResultEntityId = survivor }],
        }));
        var (host, area) = RenderArea();
        ToMatchStep(area);

        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");
        DialogButton(host, "Move").Click();
        area.WaitForAssertion(() => Assert.NotNull(_moved));

        Assert.Equal(survivor, _moved!.ResultEntityId);
        Assert.False(_moved.IsUnmatched);
    }

    [Fact]
    public async Task AfterAMove_TheHostIsToldWhichItemMovedAndWhichPlacesTheMoveLeftEmptyAndRemoved()
    {
        await UseDevice("desktop");
        _commitAnswers.Enqueue(LibraryMoveOutcome<LibraryMoveCommitResult>.Ok(new LibraryMoveCommitResult
        {
            MovedCount = 1,
            Rows = [new LibraryMoveRowResult { ItemId = LibraryMoveTestData.SongId, Outcome = LibraryMoveOutcomes.Matched, ResultEntityId = LibraryMoveTestData.AlbumId }],
            RemovedContainers = ["The Best Of"],
        }));
        var (host, area) = RenderArea();
        ToMatchStep(area);

        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");
        DialogButton(host, "Move").Click();
        area.WaitForAssertion(() => Assert.NotNull(_moved));

        Assert.Equal(LibraryMoveTestData.SongId, _moved!.ItemId);
        Assert.Equal(["The Best Of"], _moved.RemovedContainers);
    }

    [Fact]
    public async Task AfterAddAnyway_TheHostIsToldTheItemIsNowNotMatched()
    {
        await UseDevice("desktop");
        _commitAnswers.Enqueue(LibraryMoveOutcome<LibraryMoveCommitResult>.Ok(new LibraryMoveCommitResult
        {
            MovedCount = 1,
            Rows = [new LibraryMoveRowResult { ItemId = LibraryMoveTestData.SongId, Outcome = LibraryMoveOutcomes.AddedUnmatched, ResultEntityId = LibraryMoveTestData.SongId }],
        }));
        _preview = LibraryMoveTestData.AlbumPreview(rows: [LibraryMoveTestData.Row(suggested: null)]);
        var (host, area) = RenderArea();
        ToMatchStep(area);

        Button(area, "Add anyway").Click();
        Button(area, "Apply").Click();
        host.WaitForElement(".app-confirm");
        DialogButton(host, "Add anyway").Click();
        area.WaitForAssertion(() => Assert.NotNull(_moved));

        Assert.True(_moved!.IsUnmatched);
        Assert.Equal(LibraryMoveTestData.SongId, _moved.ResultEntityId);
    }

    [Fact]
    public async Task CancellingTheMatchStepLeavesEverythingAsItWas()
    {
        await UseDevice("desktop");
        var (_, area) = RenderArea();
        ToMatchStep(area);

        Button(area, "Cancel").Click();

        Assert.Empty(area.FindAll(".tl-match-step"));
        Assert.Contains(area.FindAll("button"), button => button.TextContent.Trim() == "Move to another album");
        Assert.Empty(_commits);
    }
}
