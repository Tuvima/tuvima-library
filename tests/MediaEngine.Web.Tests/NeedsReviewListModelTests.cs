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
    public void ReplacingTheList_StartsAgain_SoAFinishedRowsLineLeavesWithTheReload()
    {
        var row = NeedsReviewTestRows.ReviewRow(Id(1), "One");
        var model = Loaded(row);
        model.Finish(row, "One matched");

        model.Replace(NeedsReviewTestRows.Page([row]));

        Assert.Null(model.StateOf(row.Key).Done);
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
}
