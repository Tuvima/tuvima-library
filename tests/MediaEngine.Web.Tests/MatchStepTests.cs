using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.MediaEditor.Moves;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Match step (details overlay spec D12 to D16): the Engine's best guess is already chosen with its reason, entries that
/// already have a file are marked, Apply stays off until every item is matched or added anyway, a taken entry asks before it is
/// used, and a destination with no list needs no choices at all. The Engine's answers are plain data; nothing is moved.
/// </summary>
public sealed class MatchStepTests : AsyncBunitContext
{
    private LibraryMoveSelection? _applied;

    public MatchStepTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<AppDialogHost> RenderHost() => Render<AppDialogHost>();

    private IRenderedComponent<MatchStep> RenderStep(LibraryMoveSelection selection) =>
        Render<MatchStep>(parameters => parameters
            .Add(component => component.Selection, selection)
            .Add(component => component.OnApply, EventCallback.Factory.Create<LibraryMoveSelection>(this, applied => _applied = applied)));

    private static IElement Button(IRenderedComponent<MatchStep> step, string label) =>
        step.FindAll("button").Single(button => button.TextContent.Trim() == label);

    private static bool IsOff(IElement button) => button.HasAttribute("disabled");

    private static string Text(IRenderedComponent<MatchStep> step) =>
        Regex.Replace(step.Find(".tl-match-step").TextContent, "\\s+", " ").Trim();

    private static LibraryMovePreview TwoSongs() => LibraryMoveTestData.AlbumPreview(
        rows:
        [
            LibraryMoveTestData.Row(),
            LibraryMoveTestData.Row(LibraryMoveTestData.OtherSongId, "Unknown Song", suggested: null),
        ]);

    // -- the Engine's best guess ------------------------------------------------------------------

    [Fact]
    public void TheEnginesBestGuessIsAlreadyChosenAndItsReasonIsShown()
    {
        var step = RenderStep(LibraryMoveSelection.From(LibraryMoveTestData.AlbumPreview()));

        var guess = step.Find("[data-entry-key='t2']");
        Assert.Contains("is-chosen", guess.ClassList);
        Assert.Contains("Best guess: Same title", guess.TextContent, StringComparison.Ordinal);
        Assert.Contains("Best guess: Track 2. Same title.", Text(step), StringComparison.Ordinal);
        Assert.False(IsOff(Button(step, "Apply")));
    }

    [Fact]
    public void EntriesThatAlreadyHaveAFileAreMarked()
    {
        var step = RenderStep(LibraryMoveSelection.From(LibraryMoveTestData.AlbumPreview()));

        Assert.Contains("Has a file", step.Find("[data-entry-key='t1']").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Has a file", step.Find("[data-entry-key='t3']").TextContent, StringComparison.Ordinal);
    }

    // -- Apply stays off until every item is decided ------------------------------------------------

    [Fact]
    public void ApplyStaysOffUntilEveryItemIsMatched_AndTheUnmatchedRowIsHighlighted()
    {
        var step = RenderStep(LibraryMoveSelection.From(TwoSongs()));

        Assert.True(IsOff(Button(step, "Apply")));
        var unmatched = step.FindAll("tr.is-unmatched");
        Assert.Equal(LibraryMoveTestData.OtherSongId.ToString(), Assert.Single(unmatched).GetAttribute("data-item-id"));

        step.FindAll("select")[1].Change("t3");

        Assert.False(IsOff(Button(step, "Apply")));
        Assert.Empty(step.FindAll("tr.is-unmatched"));
    }

    [Fact]
    public void AddUnmatchedWithoutNumbersEnablesApplyAndSendsAddAnywayRows()
    {
        var step = RenderStep(LibraryMoveSelection.From(TwoSongs()));
        Assert.True(IsOff(Button(step, "Apply")));

        Button(step, "Add unmatched without numbers").Click();
        Assert.False(IsOff(Button(step, "Apply")));
        Button(step, "Apply").Click();

        var request = Assert.IsType<LibraryMoveSelection>(_applied).ToCommitRequest();
        Assert.Equal("token-1", request.PreviewToken);
        var matched = request.Rows.Single(row => row.ItemId == LibraryMoveTestData.SongId);
        Assert.Equal(LibraryMoveModes.Match, matched.Mode);
        Assert.Equal("t2", matched.EntryKey);
        var anyway = request.Rows.Single(row => row.ItemId == LibraryMoveTestData.OtherSongId);
        Assert.Equal(LibraryMoveModes.AddAnyway, anyway.Mode);
        Assert.Null(anyway.EntryKey);
    }

    [Fact]
    public void ASongCannotBeAppliedToAnAlbumThatLacksItWithoutMatchOrAddAnyway()
    {
        var step = RenderStep(LibraryMoveSelection.From(LibraryMoveTestData.AlbumWithoutTracksPreview()));

        Assert.True(IsOff(Button(step, "Apply")));
        Assert.Contains("nothing to match against yet", Text(step), StringComparison.Ordinal);

        Button(step, "Add anyway").Click();
        Assert.False(IsOff(Button(step, "Apply")));
        Button(step, "Apply").Click();

        var row = Assert.Single(Assert.IsType<LibraryMoveSelection>(_applied).ToCommitRequest().Rows);
        Assert.Equal(LibraryMoveModes.AddAnyway, row.Mode);
        Assert.Null(row.EntryKey);
    }

    // -- a taken entry (D16) -----------------------------------------------------------------------

    [Fact]
    public void ChoosingATakenEntryAsksAndAddAsAnotherVersionSendsAddAsVersion()
    {
        var host = RenderHost();
        var step = RenderStep(LibraryMoveSelection.From(LibraryMoveTestData.AlbumPreview(rows: [LibraryMoveTestData.Row(suggested: null)])));

        step.Find("[data-entry-key='t1']").Click();
        host.WaitForElement(".app-confirm");
        Assert.Contains("Track 1 already has a file. Add as another version, or pick another track.", host.Find(".app-confirm__message").TextContent, StringComparison.Ordinal);
        Assert.True(IsOff(Button(step, "Apply")));

        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Add as another version").Click();
        step.WaitForAssertion(() => Assert.False(IsOff(Button(step, "Apply"))));
        Button(step, "Apply").Click();
        step.WaitForAssertion(() => Assert.NotNull(_applied));

        var row = Assert.Single(Assert.IsType<LibraryMoveSelection>(_applied).ToCommitRequest().Rows);
        Assert.Equal(LibraryMoveModes.AddAsVersion, row.Mode);
        Assert.Equal("t1", row.EntryKey);
    }

    [Fact]
    public void PickingAnotherEntryAfterAskingChangesNothing()
    {
        var host = RenderHost();
        var step = RenderStep(LibraryMoveSelection.From(LibraryMoveTestData.AlbumPreview(rows: [LibraryMoveTestData.Row(suggested: null)])));

        step.Find("[data-entry-key='t1']").Click();
        host.WaitForElement(".app-confirm");
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Pick another entry").Click();

        step.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        Assert.True(IsOff(Button(step, "Apply")));
        Assert.DoesNotContain("is-chosen", step.Find("[data-entry-key='t1']").ClassList);
    }

    [Fact]
    public void ATakenEntryTheEngineCannotAddAVersionTo_OffersOnlyAnotherEntry()
    {
        var host = RenderHost();
        var preview = LibraryMoveTestData.AlbumPreview(rows: [LibraryMoveTestData.Row(suggested: null)], canAddVersion: false);
        var step = RenderStep(LibraryMoveSelection.From(preview));

        step.Find("[data-entry-key='t1']").Click();

        Assert.Empty(host.FindAll(".app-confirm"));
        Assert.Contains("Track 1 already has a file. Pick another track.", Text(step), StringComparison.Ordinal);
        Assert.DoesNotContain("another version", Text(step), StringComparison.Ordinal);
        Assert.True(IsOff(Button(step, "Apply")));
    }

    // -- no list (D14), blocked items --------------------------------------------------------------

    [Fact]
    public void ADestinationWithNoListSkipsTheMatchTable()
    {
        var step = RenderStep(LibraryMoveSelection.From(LibraryMoveTestData.NoListPreview()));

        Assert.Empty(step.FindAll("table"));
        Assert.Empty(step.FindAll("[data-entry-key]"));
        Assert.Contains("has no list to match against", Text(step), StringComparison.Ordinal);
        Assert.False(IsOff(Button(step, "Apply")));
        Button(step, "Apply").Click();

        var row = Assert.Single(Assert.IsType<LibraryMoveSelection>(_applied).ToCommitRequest().Rows);
        Assert.Equal(LibraryMoveModes.Match, row.Mode);
        Assert.Null(row.EntryKey);
    }

    [Fact]
    public void AnItemStillBeingProcessedKeepsApplyOffAndSaysWhy()
    {
        var preview = LibraryMoveTestData.AlbumPreview(rows: [LibraryMoveTestData.Row(blocked: "Still being processed")]);
        var step = RenderStep(LibraryMoveSelection.From(preview));

        Assert.True(IsOff(Button(step, "Apply")));
        Assert.Contains("Paranoid Android: Still being processed. Try again when it has finished.", Text(step), StringComparison.Ordinal);
    }

    [Fact]
    public void AReasonThatDoesNotPassByItself_NeverSaysToTryAgainLater()
    {
        var preview = LibraryMoveTestData.AlbumPreview(rows:
            [LibraryMoveTestData.Row(blocked: "You changed details on this audiobook, so its last file can't be moved out of it.")]);
        var step = RenderStep(LibraryMoveSelection.From(preview));

        Assert.True(IsOff(Button(step, "Apply")));
        var note = step.Find(".tl-match-step__blocked-note").TextContent;
        Assert.Contains("so its last file can't be moved out of it.", note, StringComparison.Ordinal);
        Assert.DoesNotContain("Try again", note, StringComparison.Ordinal);
    }

    // -- the item's own entry, permission and details the person set ----------------------------------

    [Fact]
    public void TheItemsOwnEntryIsMarkedAndCannotBeChosen()
    {
        var step = RenderStep(LibraryMoveSelection.From(LibraryMoveTestData.AlbumHoldingTheSongPreview(), withinCurrentPlace: true));

        var own = step.Find("[data-entry-key='t2']");
        Assert.Contains("is-own", own.ClassList);
        Assert.Contains("This is it now", own.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Has a file", own.TextContent, StringComparison.Ordinal);
        Assert.Contains("Has a file", step.Find("[data-entry-key='t1']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WithinTheItemsCurrentPlace_AddAnywayIsNotOffered_AndTheWordsSayMatch()
    {
        var step = RenderStep(LibraryMoveSelection.From(LibraryMoveTestData.AlbumHoldingTheSongPreview(), withinCurrentPlace: true));

        Assert.DoesNotContain(step.FindAll("button"), button => button.TextContent.Trim() == "Add anyway");
        Assert.Contains("Match Paranoid Android in OK Computer", Text(step), StringComparison.Ordinal);
        Assert.DoesNotContain("or add it anyway", Text(step), StringComparison.Ordinal);
    }

    [Fact]
    public void ACallerWhoMayNotMatch_IsToldWhy_AndOnlyAddAnywayIsLeft()
    {
        var preview = LibraryMoveTestData.AlbumPreview(rows: [LibraryMoveTestData.Row(suggested: null, canMatch: false)]);
        var step = RenderStep(LibraryMoveSelection.From(preview));

        Assert.Contains(LibraryMoveText.MatchNotAllowedNote, step.Find("[data-pick-note]").TextContent, StringComparison.Ordinal);
        Assert.True(IsOff(Button(step, "Apply")));
        Button(step, "Add anyway").Click();
        Assert.False(IsOff(Button(step, "Apply")));
    }

    [Fact]
    public void AnItemWhoseDetailsThePersonSet_IsToldItCannotMergeIntoAnExistingTitle_AndStillMayTakeAnEmptySlot()
    {
        const string reason = "You changed this item's details, so it can't be merged into an existing entry.";
        var preview = LibraryMoveTestData.AlbumHoldingTheSongPreview(rows: [LibraryMoveTestData.Row(LibraryMoveTestData.OtherSongId, "Unknown Song", suggested: null, mergeBlocked: reason)]);
        var step = RenderStep(LibraryMoveSelection.From(preview));

        Assert.Contains(reason, step.Find("[data-pick-note]").TextContent, StringComparison.Ordinal);
        step.Find("[data-entry-key='t3']").Click();
        Assert.True(IsOff(Button(step, "Apply")));
        step.Find("[data-entry-key='t4']").Click();
        Assert.False(IsOff(Button(step, "Apply")));
    }
}
