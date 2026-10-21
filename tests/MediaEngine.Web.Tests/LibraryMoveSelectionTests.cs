using MediaEngine.Contracts.Library;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>The rules of the Match step's choices (details overlay spec D12 to D16), without any screen.</summary>
public sealed class LibraryMoveSelectionTests
{
    private static LibraryMovePreview TwoSongs(bool canAddVersion = true, string token = "token-1") => LibraryMoveTestData.AlbumPreview(
        rows:
        [
            LibraryMoveTestData.Row(suggested: null),
            LibraryMoveTestData.Row(LibraryMoveTestData.OtherSongId, "Unknown Song", suggested: null),
        ],
        canAddVersion: canAddVersion,
        token: token);

    [Fact]
    public void AFreeBestGuessIsChosenButATakenOneIsLeftForThePersonToDecide()
    {
        var preview = LibraryMoveTestData.AlbumPreview(rows:
        [
            LibraryMoveTestData.Row(suggested: "t2"),
            LibraryMoveTestData.Row(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), "Airbag", suggested: "t1"),
        ]);

        var selection = LibraryMoveSelection.From(preview);

        Assert.Equal(LibraryMoveRowChoice.Match, selection.Find(LibraryMoveTestData.SongId)!.Choice);
        Assert.Equal("t2", selection.Find(LibraryMoveTestData.SongId)!.EntryKey);
        Assert.Equal(LibraryMoveRowChoice.Undecided, selection.Rows[1].Choice);
        Assert.False(selection.CanApply);
    }

    [Fact]
    public void TwoItemsCannotTakeTheSameEntry()
    {
        var selection = LibraryMoveSelection.From(TwoSongs());

        Assert.Equal(LibraryMovePickResult.Matched, selection.Pick(LibraryMoveTestData.SongId, "t3"));
        Assert.Equal(LibraryMovePickResult.PickedByAnotherItem, selection.Pick(LibraryMoveTestData.OtherSongId, "t3"));
        Assert.Equal(LibraryMoveRowChoice.Undecided, selection.Find(LibraryMoveTestData.OtherSongId)!.Choice);
    }

    [Fact]
    public void ATakenEntryIsNeverReplacedSilently_AndOnlyBecomesAVersionWhenTheEngineCanHonourIt()
    {
        var withVersions = LibraryMoveSelection.From(TwoSongs(canAddVersion: true));
        Assert.Equal(LibraryMovePickResult.EntryTaken, withVersions.Pick(LibraryMoveTestData.SongId, "t1"));
        Assert.Equal(LibraryMoveRowChoice.Undecided, withVersions.Find(LibraryMoveTestData.SongId)!.Choice);
        Assert.True(withVersions.AddAsVersion(LibraryMoveTestData.SongId, "t1"));
        Assert.Equal(LibraryMoveModes.AddAsVersion, withVersions.ToCommitRequest().Rows[0].Mode);

        var withoutVersions = LibraryMoveSelection.From(TwoSongs(canAddVersion: false));
        Assert.False(withoutVersions.AddAsVersion(LibraryMoveTestData.SongId, "t1"));
        Assert.Equal(LibraryMoveRowChoice.Undecided, withoutVersions.Find(LibraryMoveTestData.SongId)!.Choice);
    }

    [Fact]
    public void AnItemAddedAnywayTakesNoEntryAndAnUnmatchedClearGoesBack()
    {
        var selection = LibraryMoveSelection.From(TwoSongs());
        selection.Pick(LibraryMoveTestData.SongId, "t2");

        selection.AddAnyway(LibraryMoveTestData.SongId);
        var row = selection.ToCommitRequest().Rows[0];
        Assert.Equal(LibraryMoveModes.AddAnyway, row.Mode);
        Assert.Null(row.EntryKey);

        selection.Clear(LibraryMoveTestData.SongId);
        Assert.Equal(LibraryMoveRowChoice.Undecided, selection.Find(LibraryMoveTestData.SongId)!.Choice);
    }

    [Fact]
    public void AFreshPreviewKeepsEveryChoiceThatStillHolds()
    {
        var before = LibraryMoveSelection.From(TwoSongs(token: "token-1"));
        before.Pick(LibraryMoveTestData.SongId, "t3");
        before.AddAnyway(LibraryMoveTestData.OtherSongId);

        // Between the preview and the confirm, track 3 got a file from someone else.
        var changed = TwoSongs(token: "token-2");
        changed.Entries[2] = LibraryMoveTestData.Entry("t3", "3", "Subterranean Homesick Alien", Guid.NewGuid(), "Another file");
        var after = LibraryMoveSelection.From(changed, before);

        Assert.Equal(LibraryMoveRowChoice.Undecided, after.Find(LibraryMoveTestData.SongId)!.Choice);
        Assert.Equal(LibraryMoveRowChoice.AddAnyway, after.Find(LibraryMoveTestData.OtherSongId)!.Choice);
        Assert.False(after.CanApply);
        Assert.Equal("token-2", after.ToCommitRequest().PreviewToken);
    }

    [Fact]
    public void ThePreviewRequestForTheConfirmationCarriesTheChoicesMade_AndLeavesOutRowsStillUndecided()
    {
        var selection = LibraryMoveSelection.From(TwoSongs());
        selection.Pick(LibraryMoveTestData.SongId, "t3");

        var request = selection.ToPreviewRequest(LibraryMoveTestData.AlbumId);

        Assert.Equal(LibraryMoveTestData.AlbumId, request.DestinationId);
        Assert.Equal([LibraryMoveTestData.SongId, LibraryMoveTestData.OtherSongId], request.ItemIds);
        var choice = Assert.Single(request.Choices!);
        Assert.Equal(LibraryMoveTestData.SongId, choice.ItemId);
        Assert.Equal(LibraryMoveModes.Match, choice.Mode);
        Assert.Equal("t3", choice.EntryKey);
    }

    [Fact]
    public void AnItemAddedAnywayIsSentAsAChoiceWithNoEntry_SoItsFileIsNamedWithoutANumber()
    {
        var selection = LibraryMoveSelection.From(TwoSongs());
        selection.AddAnyway(LibraryMoveTestData.SongId);

        var choice = Assert.Single(selection.ToPreviewRequest(LibraryMoveTestData.AlbumId).Choices!);

        Assert.Equal(LibraryMoveModes.AddAnyway, choice.Mode);
        Assert.Null(choice.EntryKey);
    }

    [Fact]
    public void WithNothingChosenYetOrNoListToChooseFrom_NoChoicesAreSent()
    {
        Assert.Null(LibraryMoveSelection.From(TwoSongs()).ToPreviewRequest(LibraryMoveTestData.AlbumId).Choices);
        Assert.Null(LibraryMoveSelection.From(LibraryMoveTestData.NoListPreview()).ToPreviewRequest(LibraryMoveTestData.AlbumId).Choices);
    }

    [Fact]
    public void ASeriesTakesOnlyAddAnyway_SoEveryRowIsAddedAnywayAndNothingIsLeftToChoose()
    {
        var selection = LibraryMoveSelection.From(LibraryMoveTestData.SeriesPreview());

        Assert.True(selection.AddAnywayOnly);
        Assert.False(selection.HasList);
        Assert.True(selection.CanApply);
        var row = Assert.Single(selection.ToCommitRequest().Rows);
        Assert.Equal(LibraryMoveModes.AddAnyway, row.Mode);
        Assert.Null(row.EntryKey);
        Assert.Null(selection.ToPreviewRequest(LibraryMoveTestData.AlbumId).Choices);
    }

    [Fact]
    public void TheConfirmationForASeriesSaysItGetsNoNumber_AndOffersCancelNotMatchInstead()
    {
        var confirmation = LibraryMoveText.Confirm(LibraryMoveSelection.From(LibraryMoveTestData.SeriesPreview()));

        Assert.Equal("Add A Dune Novella to The Dune Chronicles anyway?", confirmation.Title);
        Assert.Contains("won't get a number", confirmation.Message, StringComparison.Ordinal);
        Assert.Contains("re-match it in Match and Identity", confirmation.Message, StringComparison.Ordinal);
        Assert.Equal("Add anyway", confirmation.PrimaryLabel);
        Assert.Equal("Cancel", confirmation.SecondaryLabel);
    }

    [Fact]
    public void ADestinationWithNoListNeedsNoChoices_AndSendsNoEntry()
    {
        var selection = LibraryMoveSelection.From(LibraryMoveTestData.NoListPreview());

        Assert.False(selection.HasList);
        Assert.True(selection.CanApply);
        var row = Assert.Single(selection.ToCommitRequest().Rows);
        Assert.Equal(LibraryMoveModes.Match, row.Mode);
        Assert.Null(row.EntryKey);
    }

    [Fact]
    public void AnAlbumWithNoTracksStillNeedsAChoice_SoASongCannotJustBeDroppedIn()
    {
        var selection = LibraryMoveSelection.From(LibraryMoveTestData.AlbumWithoutTracksPreview());

        Assert.True(selection.HasList);
        Assert.False(selection.CanApply);
        selection.AddAnyway(LibraryMoveTestData.SongId);
        Assert.True(selection.CanApply);
    }

    [Fact]
    public void AnItemsOwnEntryIsNeverSelectable_NotAsAMatchAndNotAsAnotherVersion()
    {
        var selection = LibraryMoveSelection.From(LibraryMoveTestData.AlbumHoldingTheSongPreview());

        Assert.Equal(LibraryMovePickResult.OwnEntry, selection.Pick(LibraryMoveTestData.SongId, "t2"));
        Assert.False(selection.AddAsVersion(LibraryMoveTestData.SongId, "t2"));
        Assert.Equal(LibraryMoveRowChoice.Undecided, selection.Find(LibraryMoveTestData.SongId)!.Choice);
        Assert.True(selection.IsOwn("t2", LibraryMoveTestData.SongId));
        Assert.False(selection.IsTaken("t2", LibraryMoveTestData.SongId));
        Assert.True(selection.IsTaken("t1", LibraryMoveTestData.SongId));
    }

    [Fact]
    public void AnOwnEntryTheEngineSuggestsIsNotChosen()
    {
        var preview = LibraryMoveTestData.AlbumHoldingTheSongPreview([LibraryMoveTestData.Row(suggested: "t2")]);

        var selection = LibraryMoveSelection.From(preview);

        Assert.Equal(LibraryMoveRowChoice.Undecided, selection.Find(LibraryMoveTestData.SongId)!.Choice);
    }

    [Fact]
    public void ACallerWhoMayNotMatch_CanOnlyAddAnyway_AndNoEntryIsPickedForThem()
    {
        var preview = LibraryMoveTestData.AlbumPreview(rows: [LibraryMoveTestData.Row(canMatch: false)]);

        var selection = LibraryMoveSelection.From(preview);

        Assert.Equal(LibraryMoveRowChoice.Undecided, selection.Rows[0].Choice);
        Assert.Equal(LibraryMovePickResult.MatchNotAllowed, selection.Pick(LibraryMoveTestData.SongId, "t2"));
        Assert.False(selection.AddAsVersion(LibraryMoveTestData.SongId, "t1"));
        Assert.Equal(LibraryMoveText.MatchNotAllowedNote, selection.Rows[0].PickNote);
        selection.AddAnyway(LibraryMoveTestData.SongId);
        Assert.True(selection.CanApply);
    }

    [Fact]
    public void AnItemWhoseDetailsThePersonSet_CannotPickAnExistingTitle_ButCanTakeAnEmptySlot()
    {
        var preview = LibraryMoveTestData.AlbumHoldingTheSongPreview(
            [LibraryMoveTestData.Row(LibraryMoveTestData.OtherSongId, "Unknown Song", suggested: "t3", mergeBlocked: "You changed this item's details.")]);

        var selection = LibraryMoveSelection.From(preview);
        var row = selection.Rows[0];

        Assert.Equal(LibraryMoveRowChoice.Undecided, row.Choice);
        Assert.Equal(LibraryMovePickResult.CannotMerge, selection.Pick(row.ItemId, "t3"));
        Assert.Equal(LibraryMovePickResult.Matched, selection.Pick(row.ItemId, "t4"));
        Assert.Equal("t4", row.EntryKey);
    }

    [Fact]
    public void WithinTheItemsCurrentPlace_AddAnywayIsNotAChoice()
    {
        var selection = LibraryMoveSelection.From(LibraryMoveTestData.AlbumHoldingTheSongPreview(), withinCurrentPlace: true);

        selection.AddAnyway(LibraryMoveTestData.SongId);
        selection.AddUnmatchedWithoutNumbers();
        var kept = LibraryMoveSelection.From(LibraryMoveTestData.AlbumHoldingTheSongPreview(token: "token-2"), selection);

        Assert.Equal(LibraryMoveRowChoice.Undecided, selection.Rows[0].Choice);
        Assert.True(kept.IsWithinCurrentPlace);
    }
}
