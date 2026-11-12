using MediaEngine.Contracts.Review;
using MediaEngine.Web.Components.Settings.NeedsReview;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The pure part of the Needs Review page (spec needs-review-redesign D1, D6, D8): the four groups in their order, families, the counts that
/// drop at once when a row finishes, and the facts a row shows. No page and no services are involved.
/// </summary>
public sealed class NeedsReviewListModelTests
{
    private static Guid Id(int n) => Guid.Parse($"{n:D8}-0000-4000-8000-000000000000");

    private static NeedsReviewListModel Loaded(params NeedsReviewRowDto[] rows)
    {
        var model = new NeedsReviewListModel();
        model.Replace(NeedsReviewTestRows.Page(rows));
        return model;
    }

    [Fact]
    public void Sections_AreReturnedInTheFixedGroupOrder_AndAnEmptyGroupIsLeftOut()
    {
        var model = Loaded(
            NeedsReviewTestRows.FileRow(Id(1), "a.mkv", group: NeedsReviewGroups.FileProblem),
            NeedsReviewTestRows.ReviewRow(Id(2), "Two", group: NeedsReviewGroups.Ready),
            NeedsReviewTestRows.ReviewRow(Id(3), "Three", group: NeedsReviewGroups.NoMatch));

        Assert.Equal(
            [NeedsReviewGroups.Ready, NeedsReviewGroups.NoMatch, NeedsReviewGroups.FileProblem],
            model.Sections().Select(section => section.Group).ToArray());
    }

    [Fact]
    public void AGroupTheEngineDoesNotKnow_IsDrawnWithTheItemsThatFoundNoMatch()
    {
        var model = Loaded(NeedsReviewTestRows.ReviewRow(Id(1), "Odd", group: "something_new"));

        Assert.Equal(NeedsReviewGroups.NoMatch, Assert.Single(model.Sections()).Group);
    }

    [Fact]
    public void TwoOrMoreConsecutiveRowsOfOneFamily_BecomeOneFamilyEntry_AndALoneRowDoesNot()
    {
        var model = Loaded(
            NeedsReviewTestRows.FileRow(Id(1), "S01E01.mkv", familyKey: "show-a", familyTitle: "Show A"),
            NeedsReviewTestRows.FileRow(Id(2), "S01E02.mkv", familyKey: "show-a", familyTitle: "Show A"),
            NeedsReviewTestRows.FileRow(Id(3), "S01E03.mkv", familyKey: "show-a", familyTitle: "Show A"),
            NeedsReviewTestRows.FileRow(Id(4), "Lonely.mkv", familyKey: "show-b", familyTitle: "Show B"),
            NeedsReviewTestRows.FileRow(Id(5), "Plain.mkv"));

        var entries = Assert.Single(model.Sections()).Entries;

        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].IsFamily);
        Assert.Equal(3, entries[0].Rows.Count);
        Assert.False(entries[1].IsFamily);
        Assert.False(entries[2].IsFamily);
    }

    [Fact]
    public void RowsOfOneFamilyThatAreNotNextToEachOther_AreNotMerged()
    {
        var model = Loaded(
            NeedsReviewTestRows.FileRow(Id(1), "a.mkv", familyKey: "show-a"),
            NeedsReviewTestRows.FileRow(Id(2), "b.mkv"),
            NeedsReviewTestRows.FileRow(Id(3), "c.mkv", familyKey: "show-a"));

        Assert.All(Assert.Single(model.Sections()).Entries, entry => Assert.False(entry.IsFamily));
    }

    [Fact]
    public void AFinishedRow_StopsCounting_AndTheSummaryTotalDropsAtOnce()
    {
        var first = NeedsReviewTestRows.ReviewRow(Id(1), "One", group: NeedsReviewGroups.Ready);
        var model = Loaded(first, NeedsReviewTestRows.ReviewRow(Id(2), "Two", group: NeedsReviewGroups.Ready), NeedsReviewTestRows.FileRow(Id(3), "c.mkv"));
        Assert.Equal(3, model.WaitingTotal);

        model.Finish(first, "One matched");

        Assert.Equal(1, model.CountOf(NeedsReviewGroups.Ready));
        Assert.Equal(2, model.WaitingTotal);
        Assert.Equal("One matched", model.StateOf(first.Key).Done);
    }

    [Fact]
    public void ARowTheEngineHoldsButThePageHasNotReadYet_StillCountsInItsGroup()
    {
        var model = new NeedsReviewListModel();
        var page = NeedsReviewTestRows.Page([NeedsReviewTestRows.ReviewRow(Id(1), "One", group: NeedsReviewGroups.Ready)], total: 10);
        page.GroupTotals[NeedsReviewGroups.Ready] = 10;

        model.Replace(page);

        Assert.Equal(10, model.CountOf(NeedsReviewGroups.Ready));
        Assert.True(model.HasMore);
    }

    [Fact]
    public void Failing_KeepsTheRowWaiting_WithItsReason_AndTheNextAttemptClearsTheReason()
    {
        var row = NeedsReviewTestRows.ReviewRow(Id(1), "One");
        var model = Loaded(row);

        model.SetBusy(row, true);
        model.Fail(row, "Could not be matched. Try again in a moment.");

        Assert.True(model.IsWaiting(row));
        Assert.False(model.StateOf(row.Key).Busy);
        Assert.Equal("Could not be matched. Try again in a moment.", model.StateOf(row.Key).Failure);

        model.SetBusy(row, true);

        Assert.Null(model.StateOf(row.Key).Failure);
    }

    [Fact]
    public void ReloadingTheList_KeepsAFinishedRowsLineWhereItWas_UntilThePageIsLeft()
    {
        var first = NeedsReviewTestRows.ReviewRow(Id(1), "One", group: NeedsReviewGroups.Ready);
        var second = NeedsReviewTestRows.ReviewRow(Id(2), "Two", group: NeedsReviewGroups.Ready);
        var third = NeedsReviewTestRows.ReviewRow(Id(3), "Three", group: NeedsReviewGroups.Ready);
        var model = Loaded(first, second, third);
        model.Finish(second, "Two matched");

        // The Engine no longer lists the finished row.
        model.Replace(NeedsReviewTestRows.Page([first, third]), keepFinished: true);

        Assert.Equal([first.Key, second.Key, third.Key], model.Rows.Select(row => row.Key).ToArray());
        Assert.Equal("Two matched", model.StateOf(second.Key).Done);
        Assert.False(model.IsWaiting(second));
        Assert.Equal(2, model.CountOf(NeedsReviewGroups.Ready));
        Assert.Equal(2, model.WaitingTotal);
    }

    [Fact]
    public void ReloadingTheList_ListsARowOnce_AndTheFreshRowWinsWhenTheEngineStillHasItWaiting()
    {
        var row = NeedsReviewTestRows.ReviewRow(Id(1), "One");
        var model = Loaded(row);
        model.Finish(row, "One kept");

        // The person put it back: the Engine lists it as waiting again.
        model.Replace(NeedsReviewTestRows.Page([row]), keepFinished: true);

        Assert.Single(model.Rows);
        Assert.Null(model.StateOf(row.Key).Done);
        Assert.True(model.IsWaiting(row));
        Assert.Equal(1, model.WaitingTotal);
    }

    [Fact]
    public void ReloadingTheList_KeepsARunningActionOnARowThatIsStillListed()
    {
        var row = NeedsReviewTestRows.ReviewRow(Id(1), "One");
        var model = Loaded(row);
        model.SetBusy(row, true);

        model.Replace(NeedsReviewTestRows.Page([row]), keepFinished: true);

        Assert.True(model.IsBusy(row));
    }

    [Fact]
    public void ReloadingTheList_PutsAFinishedRowThatWasFirstInItsGroupBackAtTheTop()
    {
        var first = NeedsReviewTestRows.ReviewRow(Id(1), "One", group: NeedsReviewGroups.Ready);
        var other = NeedsReviewTestRows.ReviewRow(Id(2), "Two", group: NeedsReviewGroups.Ready);
        var model = Loaded(first, other);
        model.Finish(first, "One matched");

        model.Replace(NeedsReviewTestRows.Page([other]), keepFinished: true);

        Assert.Equal([first.Key, other.Key], Assert.Single(model.Sections()).Entries.Select(entry => entry.Key).ToArray());
    }

    [Fact]
    public void AGenuineFreshStart_DropsEveryFinishedLine_SoALibraryChangeStartsClean()
    {
        var row = NeedsReviewTestRows.ReviewRow(Id(1), "One");
        var model = Loaded(row);
        model.Finish(row, "One matched");

        model.Reset(NeedsReviewTestRows.Page([]));

        Assert.Empty(model.Rows);
        Assert.Null(model.StateOf(row.Key).Done);
    }

    [Fact]
    public void ShowMore_SkipsOnlyTheRowsStillWaiting_BecauseTheEngineNoLongerListsAFinishedRow()
    {
        var rows = Enumerable.Range(1, 4).Select(n => NeedsReviewTestRows.ReviewRow(Id(n), $"Row {n}")).ToArray();
        var model = new NeedsReviewListModel();
        model.Replace(NeedsReviewTestRows.Page(rows, total: 10));
        model.Finish(rows[0], "Row 1 matched");
        model.Finish(rows[1], "Row 2 matched");

        Assert.Equal(2, model.WaitingRead);
        Assert.True(model.HasMore);
    }

    [Fact]
    public void ShowMore_IsNotOffered_WhenEveryRowTheEngineHeldWasReadAndSomeHaveFinished()
    {
        var rows = Enumerable.Range(1, 3).Select(n => NeedsReviewTestRows.ReviewRow(Id(n), $"Row {n}")).ToArray();
        var model = Loaded(rows);

        model.Finish(rows[0], "Row 1 matched");

        Assert.False(model.HasMore);
        Assert.Equal(2, model.WaitingTotal);
    }

    [Fact]
    public void TheGroupCounts_StillCountTheRowsNotReadYet_AfterRowsFinish()
    {
        var rows = Enumerable.Range(1, 3).Select(n => NeedsReviewTestRows.ReviewRow(Id(n), $"Row {n}", group: NeedsReviewGroups.Ready)).ToArray();
        var page = NeedsReviewTestRows.Page(rows, total: 8);
        page.GroupTotals[NeedsReviewGroups.Ready] = 8;
        var model = new NeedsReviewListModel();
        model.Replace(page);

        model.Finish(rows[0], "Row 1 matched");

        // Two waiting rows read, five not read yet.
        Assert.Equal(7, model.CountOf(NeedsReviewGroups.Ready));
        Assert.Equal(7, model.WaitingTotal);
    }

    [Fact]
    public void ANextPage_IsCountedAgainstTheEnginesFreshTotals_SoFinishedRowsAreNotSubtractedTwice()
    {
        var rows = Enumerable.Range(1, 3).Select(n => NeedsReviewTestRows.ReviewRow(Id(n), $"Row {n}", group: NeedsReviewGroups.Ready)).ToArray();
        var first = NeedsReviewTestRows.Page(rows, total: 5);
        first.GroupTotals[NeedsReviewGroups.Ready] = 5;
        var model = new NeedsReviewListModel();
        model.Replace(first);
        model.Finish(rows[0], "Row 1 matched");

        // The Engine now holds four (the finished one left); the next page starts after the two waiting rows.
        var next = NeedsReviewTestRows.Page([NeedsReviewTestRows.ReviewRow(Id(4), "Row 4", group: NeedsReviewGroups.Ready), NeedsReviewTestRows.ReviewRow(Id(5), "Row 5", group: NeedsReviewGroups.Ready)], total: 4);
        next.GroupTotals[NeedsReviewGroups.Ready] = 4;
        model.Append(next);

        Assert.False(model.HasMore);
        Assert.Equal(4, model.CountOf(NeedsReviewGroups.Ready));
    }

    [Fact]
    public void ChangingARowsState_RaisesChanged_SoPartsThatShowItAreDrawnAgain()
    {
        var row = NeedsReviewTestRows.ReviewRow(Id(1), "One");
        var model = Loaded(row);
        var raised = 0;
        model.Changed += () => raised++;

        model.SetBusy(row, true);
        model.Fail(row, "No.");
        model.Finish(row, "Done.");

        Assert.Equal(3, raised);
    }

    [Fact]
    public void AppendingAPage_DoesNotListARowTwice()
    {
        var row = NeedsReviewTestRows.ReviewRow(Id(1), "One");
        var model = Loaded(row);

        model.Append(NeedsReviewTestRows.Page([row, NeedsReviewTestRows.ReviewRow(Id(2), "Two")]));

        Assert.Equal(2, model.Rows.Count);
    }

    [Fact]
    public void Search_LooksAtTheTitle_TheFileNameAndTheFamily()
    {
        var model = Loaded(
            NeedsReviewTestRows.ReviewRow(Id(1), "Dune"),
            NeedsReviewTestRows.FileRow(Id(2), "Arrival.2016.mkv"),
            NeedsReviewTestRows.FileRow(Id(3), "S01E01.mkv", familyKey: "k", familyTitle: "Severance"));

        Assert.Equal(1, model.MatchingRows("dune"));
        Assert.Equal(1, model.MatchingRows("ARRIVAL"));
        Assert.Equal(1, model.MatchingRows("severance"));
        Assert.Equal(3, model.MatchingRows(null));
        Assert.Equal(0, model.MatchingRows("nothing like it"));
    }

    [Fact]
    public void NoteKept_RaisesTheHiddenCountWithoutAReload()
    {
        var model = new NeedsReviewListModel();
        model.Replace(NeedsReviewTestRows.Page([], keptTotal: 2));

        model.NoteKept(1);

        Assert.Equal(3, model.KeptTotal);
    }

    [Theory]
    [InlineData("Movies", NeedsReviewKindWord.Movie)]
    [InlineData("TV", NeedsReviewKindWord.Episode)]
    [InlineData("Music", NeedsReviewKindWord.Song)]
    [InlineData("Books", NeedsReviewKindWord.Book)]
    [InlineData("Audiobooks", NeedsReviewKindWord.Audiobook)]
    [InlineData("Comics", NeedsReviewKindWord.Comic)]
    [InlineData(null, NeedsReviewKindWord.Item)]
    public void TheKindWord_IsTakenFromTheMediaType(string? mediaType, NeedsReviewKindWord expected) =>
        Assert.Equal(expected, NeedsReviewRowInfo.KindOf(mediaType));

    [Theory]
    [InlineData("C:/media/Movies/Action/2020", "…/Action/2020")]
    [InlineData("C:\\media\\Movies", "…\\media\\Movies")]
    [InlineData("media/Movies", "media/Movies")]
    public void AFolder_IsShortenedToItsLastTwoNames(string folder, string expected) =>
        Assert.Equal(expected, NeedsReviewRowInfo.ShortFolder(folder));

    [Fact]
    public void TheCommonFolder_IsTheDeepestOneEveryFileSitsIn()
    {
        Assert.Equal(
            "C:/media/tv/Severance",
            NeedsReviewRowInfo.CommonFolder(["C:/media/tv/Severance/Season 1", "C:/media/tv/Severance/Season 2"]));
        Assert.Null(NeedsReviewRowInfo.CommonFolder(["C:/a", "D:/b"]));
    }

    [Fact]
    public void TryAgain_IsOfferedForAFileThatCanBeLookedUpAndForAFailedWriteBack_ButNotForOtherFileProblems()
    {
        var file = NeedsReviewTestRows.FileRow(Id(1), "a.mkv");
        var neverLookedUp = NeedsReviewTestRows.FileRow(Id(2), "b.mp4", reason: "no_youtube_id", group: NeedsReviewGroups.FileProblem);
        var writeBack = NeedsReviewTestRows.ReviewRow(Id(3), "Song", trigger: "WritebackFailed", group: NeedsReviewGroups.FileProblem);
        var unreadable = NeedsReviewTestRows.ReviewRow(Id(4), "Film", trigger: "MediaInspectionFailed", group: NeedsReviewGroups.FileProblem);

        Assert.True(NeedsReviewRowInfo.CanTryAgain(file));
        Assert.False(NeedsReviewRowInfo.CanTryAgain(neverLookedUp));
        Assert.True(NeedsReviewRowInfo.CanTryAgain(writeBack));
        Assert.False(NeedsReviewRowInfo.CanTryAgain(unreadable));
        Assert.True(NeedsReviewRowInfo.RetryIsMainAction(writeBack));
        Assert.False(NeedsReviewRowInfo.RetryIsMainAction(file));
    }

    [Fact]
    public void WhatFixOpens_IsTheReviewItemsEntity_OrTheFilesWork()
    {
        var entity = Id(9);
        var work = Id(8);

        Assert.Equal(entity, NeedsReviewRowInfo.FixTargetOf(NeedsReviewTestRows.ReviewRow(Id(1), "One", entityId: entity)));
        Assert.Equal(work, NeedsReviewRowInfo.FixTargetOf(NeedsReviewTestRows.FileRow(Id(2), "a.mkv", workId: work)));
        Assert.Null(NeedsReviewRowInfo.FixTargetOf(NeedsReviewTestRows.FileRow(Id(3), "b.mkv")));
    }

    // ---- Ticking and the action bar (WP5) -------------------------------------------------------------------------------------------

    private static readonly NeedsReviewBestGuessDto Guess = new() { Action = "confirm_match", Name = "Cibola Burn", Year = "2014", Source = "Provider match" };

    private static string TvCandidates() => System.Text.Json.JsonSerializer.Serialize(new[]
    {
        new MovieTvSuggestionDto { TmdbTvId = "5739", Name = "Dr. Horrible's Sing-Along Blog", FirstAirYear = 2008, Type = "Miniseries", Seasons = 1, Episodes = 3 },
    });

    [Fact]
    public void TheSelectionSaysNoneSomeOrAll_ForTheRowsItIsAskedAbout()
    {
        var selection = new NeedsReviewSelection();
        string[] keys = ["a", "b", "c"];

        Assert.Equal(NeedsReviewTickState.None, selection.StateOf(keys));

        selection.Set(["a"], true);
        Assert.Equal(NeedsReviewTickState.Some, selection.StateOf(keys));

        selection.Set(["b", "c"], true);
        Assert.Equal(NeedsReviewTickState.All, selection.StateOf(keys));

        selection.Set(["b"], false);
        Assert.Equal(NeedsReviewTickState.Some, selection.StateOf(keys));
        Assert.Equal(2, selection.Count);

        selection.Retain(key => key == "a");
        Assert.Equal(1, selection.Count);

        selection.Clear();
        Assert.Equal(0, selection.Count);
        Assert.Equal(NeedsReviewTickState.None, selection.StateOf([]));
    }

    [Fact]
    public void TickedEntries_LeaveOutFinishedRowsAndRowsTheSearchHides_AndMarkAFamilyWhoseEveryWaitingFileIsTicked()
    {
        var model = Loaded(
            NeedsReviewTestRows.FileRow(Id(1), "S01E01.mkv", familyKey: "show-a", familyTitle: "Show A"),
            NeedsReviewTestRows.FileRow(Id(2), "S01E02.mkv", familyKey: "show-a", familyTitle: "Show A"),
            NeedsReviewTestRows.FileRow(Id(3), "Plain.mkv"),
            NeedsReviewTestRows.FileRow(Id(4), "Hidden.mkv"));
        var selection = new NeedsReviewSelection();
        selection.Set(model.Rows.Select(row => row.Key), true);
        model.Finish(model.Rows[2], "done");

        var ticked = model.TickedEntries(selection, search: "S01");

        var family = Assert.Single(ticked);
        Assert.True(family.WholeFamily);
        Assert.Equal(2, family.Rows.Count);
        Assert.Equal(["S01E01.mkv", "S01E02.mkv"], model.TickableKeys("S01").Select(key => model.Rows.Single(row => row.Key == key).Unmatched!.FileName).Order().ToArray());

        model.Finish(model.Rows[1], "done");
        var partly = Assert.Single(model.TickedEntries(selection, search: "S01"));
        Assert.False(partly.WholeFamily);
        Assert.Single(partly.Rows);
    }

    [Fact]
    public void ARowHasOneSuggestion_WhenTheEngineOfferedABestGuess_ButNotWhenItShowsFilmOrTvChoicesInstead()
    {
        var guessed = NeedsReviewTestRows.ReviewRow(Id(1), "Cibola Burn", guess: Guess);
        var noGuess = NeedsReviewTestRows.ReviewRow(Id(2), "No Idea");
        var filmOrTv = NeedsReviewTestRows.ReviewRow(Id(3), "Dr Horribles", trigger: "MovieMatchedAsTv", guess: Guess, candidatesJson: TvCandidates());
        var file = NeedsReviewTestRows.FileRow(Id(4), "a.mkv");

        Assert.True(NeedsReviewRowInfo.HasSingleSuggestion(guessed));
        Assert.False(NeedsReviewRowInfo.HasSingleSuggestion(noGuess));
        Assert.False(NeedsReviewRowInfo.HasSingleSuggestion(filmOrTv));
        Assert.False(NeedsReviewRowInfo.HasSingleSuggestion(file));
    }

    [Fact]
    public void UseSuggestions_NeverTakesAFamilysFiles_EvenWhenTheyCarryAGuess()
    {
        var single = NeedsReviewTestRows.ReviewRow(Id(1), "Cibola Burn", guess: Guess);
        var first = NeedsReviewTestRows.ReviewRow(Id(2), "Ep 1", guess: Guess, familyKey: "show", familyTitle: "Show");
        var second = NeedsReviewTestRows.ReviewRow(Id(3), "Ep 2", guess: Guess, familyKey: "show", familyTitle: "Show");
        var model = Loaded(single, first, second);
        var selection = new NeedsReviewSelection();
        selection.Set(model.Rows.Select(row => row.Key), true);

        var rows = NeedsReviewBulk.SuggestionRows(model.TickedEntries(selection));

        Assert.Equal([single.Key], rows.Select(row => row.Key).ToArray());
    }

    [Fact]
    public void EachBarButton_AppliesOnlyToTheTickedRowsItFits_AndItsCountIsZeroWhenItFitsNone()
    {
        var withGuess = NeedsReviewTestRows.ReviewRow(Id(1), "Cibola Burn", guess: Guess);
        var file = NeedsReviewTestRows.FileRow(Id(2), "a.mkv", workId: Id(92));
        var orphan = NeedsReviewTestRows.FileRow(Id(3), "b.mkv");
        var unreadable = NeedsReviewTestRows.ReviewRow(Id(4), "Film", trigger: "MediaInspectionFailed", group: NeedsReviewGroups.FileProblem);
        var untouched = NeedsReviewTestRows.FileRow(Id(5), "c.mkv", workId: Id(95));
        var model = Loaded(withGuess, file, orphan, unreadable, untouched);
        var selection = new NeedsReviewSelection();
        selection.Set([withGuess.Key, file.Key, orphan.Key, unreadable.Key], true);
        var ticked = model.TickedEntries(selection);

        var plan = NeedsReviewBulk.PlanFor(ticked, deviceMayEdit: true, canDelete: row => row.Unmatched?.WorkId is not null);

        Assert.Equal(new NeedsReviewBarPlan(Selected: 4, Suggestions: 1, TryAgain: 2, Keep: 4, Edit: 3, Delete: 1), plan);
        Assert.DoesNotContain(untouched.Key, NeedsReviewBulk.EditRows(ticked, true).Select(row => row.Key));
        Assert.DoesNotContain(untouched.Key, NeedsReviewBulk.DeleteRows(ticked, row => row.Unmatched?.WorkId is not null).Select(row => row.Key));

        var offDesktop = NeedsReviewBulk.PlanFor(ticked, deviceMayEdit: false, canDelete: _ => false);
        Assert.Equal(0, offDesktop.Edit);
        Assert.Equal(0, offDesktop.Delete);
        Assert.Equal(4, offDesktop.Keep);
    }

    [Fact]
    public void Delete_IsLeftOut_WhenMoreRowsAreTickedThanTheEngineDeletesAtOnce()
    {
        var rows = Enumerable.Range(1, NeedsReviewBulk.MaxDeleteRows + 1)
            .Select(n => NeedsReviewTestRows.FileRow(Guid.NewGuid(), $"f{n}.mkv", workId: Guid.NewGuid()))
            .ToArray();
        var model = Loaded(rows);
        var selection = new NeedsReviewSelection();
        selection.Set(rows.Select(row => row.Key), true);

        Assert.Empty(NeedsReviewBulk.DeleteRows(model.TickedEntries(selection), _ => true));
    }

    // ---- Review follow-ups -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("move_to_tv", null, true)]
    [InlineData("keep_provider_match", null, true)]
    [InlineData("confirm_match", null, true)]
    [InlineData("use_retail_match", "retail:tmdb:438631", true)]
    [InlineData("use_retail_match", null, false)]
    [InlineData("use_retail_match", " ", false)]
    [InlineData("something_new", null, false)]
    [InlineData("", null, false)]
    public void ARowIsOnlySuggested_WhenItsBestGuessIsAnActionThePageCanRun(string action, string? choiceId, bool suggested)
    {
        var guess = new NeedsReviewBestGuessDto { Action = action, Name = "Dune", Year = "2021", Source = "TMDB", ChoiceId = choiceId };
        var row = NeedsReviewTestRows.ReviewRow(Id(1), "Dune", guess: guess);

        Assert.Equal(suggested, NeedsReviewRowInfo.HasSingleSuggestion(row));
        var ticked = Loaded(row);
        var selection = new NeedsReviewSelection();
        selection.Set([row.Key], true);
        Assert.Equal(suggested ? 1 : 0, NeedsReviewBulk.SuggestionRows(ticked.TickedEntries(selection)).Count);
    }

    [Fact]
    public void ReplacingAndKeepingFinishedLines_AlsoCarriesARowWhoseActionIsStillRunning_SoItsLaterFinishHasARowToLandOn()
    {
        var running = NeedsReviewTestRows.FileRow(Id(1), "Running.mkv", group: NeedsReviewGroups.NoMatch);
        var other = NeedsReviewTestRows.FileRow(Id(2), "Other.mkv", group: NeedsReviewGroups.NoMatch);
        var model = Loaded(running, other);
        model.SetBusy(running, true);

        // The Engine no longer lists the running row (for example the items-removed event reloaded the list mid-action).
        model.Replace(NeedsReviewTestRows.Page([other], total: 1), keepFinished: true);

        Assert.Equal([running.Key, other.Key], model.Rows.Select(row => row.Key));
        Assert.True(model.IsBusy(running));
        Assert.Equal(1, model.WaitingRead);
        model.Finish(running, "Running.mkv deleted");
        Assert.Equal("Running.mkv deleted", model.StateOf(running.Key).Done);
        Assert.False(model.IsBusy(running));
        Assert.Equal(1, model.WaitingTotal);
    }

    [Fact]
    public void ARowThatFailsAfterBeingCarried_KeepsItsReasonOnTheRow()
    {
        var running = NeedsReviewTestRows.FileRow(Id(1), "Running.mkv");
        var model = Loaded(running);
        model.SetBusy(running, true);
        model.Replace(NeedsReviewTestRows.Page([]), keepFinished: true);

        model.Fail(running, "Could not be deleted.");

        Assert.Single(model.Rows);
        Assert.Equal("Could not be deleted.", model.StateOf(running.Key).Failure);
    }

    [Fact]
    public void AFreshStart_DropsARunningRowTheEngineNoLongerLists()
    {
        var running = NeedsReviewTestRows.FileRow(Id(1), "Running.mkv");
        var model = Loaded(running);
        model.SetBusy(running, true);

        model.Replace(NeedsReviewTestRows.Page([]), keepFinished: false);

        Assert.Empty(model.Rows);
    }

    [Fact]
    public void EditNItems_CountsEachWorkOnce_WhenTwoTickedRowsOpenTheSameWork()
    {
        var work = Guid.Parse("77777777-7777-4777-8777-777777777777");
        var first = NeedsReviewTestRows.ReviewRow(Id(1), "Cibola Burn", entityId: work);
        var second = NeedsReviewTestRows.ReviewRow(Id(2), "Cibola Burn (second file)", entityId: work);
        var third = NeedsReviewTestRows.ReviewRow(Id(3), "Other Film");
        var model = Loaded(first, second, third);
        var selection = new NeedsReviewSelection();
        selection.Set([first.Key, second.Key, third.Key], true);
        var ticked = model.TickedEntries(selection);

        var rows = NeedsReviewBulk.EditRows(ticked, deviceMayEdit: true);

        Assert.Equal([first.Key, third.Key], rows.Select(row => row.Key));
        Assert.Equal(2, NeedsReviewBulk.PlanFor(ticked, deviceMayEdit: true, canDelete: _ => false).Edit);
        Assert.Equal(3, NeedsReviewBulk.PlanFor(ticked, deviceMayEdit: true, canDelete: _ => false).Selected);
    }

    [Fact]
    public void EditNItems_OffersNothing_OnADeviceThatMayNotEdit()
    {
        var row = NeedsReviewTestRows.ReviewRow(Id(1), "Cibola Burn");
        var model = Loaded(row);
        var selection = new NeedsReviewSelection();
        selection.Set([row.Key], true);

        Assert.Empty(NeedsReviewBulk.EditRows(model.TickedEntries(selection), deviceMayEdit: false));
    }
}
