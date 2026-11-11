using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The working order of a series' videos in the editor (universal editor v3.1): arrows, drag and drop, sections, the sort button and how an
/// arrangement becomes staged changes. Nothing here reaches the Engine; Save changes saves what was staged.
/// </summary>
public sealed class VideoOrderEditorTests
{
    private static readonly Guid Series = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid PartOne = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000011");
    private static readonly Guid PartTwo = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000012");

    private static readonly Guid A = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000d");

    private static VideoOrderEntryDto Entry(Guid id, string title, string file = "", string? added = null) =>
        new() { Id = id, Title = title, FileName = file, AddedAt = added };

    private static VideoOrderEditor Plain()
    {
        var editor = new VideoOrderEditor();
        editor.Load(new VideoOrderResponse
        {
            Groups = [new VideoOrderGroupDto { ParentId = Series, Title = "Cooking with Sam", Entries = [Entry(A, "First dinner"), Entry(B, "Kitchen tour"), Entry(C, "Interview")] }],
        });
        return editor;
    }

    private static VideoOrderEditor Sections()
    {
        var editor = new VideoOrderEditor();
        editor.Load(new VideoOrderResponse
        {
            Groups =
            [
                new VideoOrderGroupDto { ParentId = PartOne, Title = "Part one", Entries = [Entry(A, "Intro"), Entry(B, "Basics")] },
                new VideoOrderGroupDto { ParentId = PartTwo, Title = "Part two", Entries = [Entry(C, "Advanced"), Entry(D, "Wrap up")] },
            ],
        }, "Course");
        return editor;
    }

    private static IEnumerable<Guid> Ids(VideoOrderEditor editor, int list) => editor.Lists[list].Current.Select(row => row.Id);

    [Fact]
    public void AnArrow_MovesAVideoOnePlace_AndStopsAtTheEnds()
    {
        var editor = Plain();

        Assert.True(editor.Nudge(0, 1, -1));
        Assert.Equal([B, A, C], Ids(editor, 0));
        Assert.False(editor.CanMoveUp(0, 0));
        Assert.False(editor.CanMoveDown(0, 2));
        Assert.False(editor.Nudge(0, 0, -1));
    }

    [Theory]
    [InlineData(0, 3, new[] { 'b', 'c', 'a' })]
    [InlineData(2, 0, new[] { 'c', 'a', 'b' })]
    [InlineData(0, 1, new[] { 'a', 'b', 'c' })]
    public void ADrop_PutsTheVideoOnTheChosenGap(int from, int gap, char[] expected)
    {
        var editor = Plain();
        var byLetter = new Dictionary<char, Guid> { ['a'] = A, ['b'] = B, ['c'] = C };

        editor.Drop(0, from, 0, gap);

        Assert.Equal(expected.Select(letter => byLetter[letter]), Ids(editor, 0));
    }

    [Fact]
    public void ADropIntoAnotherSection_MovesTheVideoThere_ButNeverEmptiesASection()
    {
        var editor = Sections();

        Assert.True(editor.Drop(0, 0, 1, 1));
        Assert.Equal([B], Ids(editor, 0));
        Assert.Equal([C, A, D], Ids(editor, 1));
        Assert.False(editor.Drop(0, 0, 1, 0));
        Assert.Equal([B], Ids(editor, 0));
    }

    [Fact]
    public void TheSortButton_OrdersByFileNameWithNumbersReadAsNumbers()
    {
        var editor = new VideoOrderEditor();
        editor.Load(new VideoOrderResponse
        {
            Groups = [new VideoOrderGroupDto { ParentId = Series, Title = "S", Entries = [Entry(A, "x", "Ep 10.mp4"), Entry(B, "y", "ep 2.mp4"), Entry(C, "z", "Ep 1.mp4")] }],
        });

        editor.Sort(0, VideoSortBy.FileName);

        Assert.Equal([C, B, A], Ids(editor, 0));
    }

    [Fact]
    public void TheSortButton_OrdersByDateAdded_WithUnknownDatesLast()
    {
        var editor = new VideoOrderEditor();
        editor.Load(new VideoOrderResponse
        {
            Groups = [new VideoOrderGroupDto { ParentId = Series, Title = "S", Entries = [Entry(A, "a", "", null), Entry(B, "b", "", "2026-02-01 10:00:00"), Entry(C, "c", "", "2026-01-01 10:00:00")] }],
        });

        editor.Sort(0, VideoSortBy.DateAdded);

        Assert.Equal([C, B, A], Ids(editor, 0));
    }

    [Fact]
    public void AReorder_BecomesOneStagedOrder_NamedForTheSeries()
    {
        var editor = Plain();
        var pending = new PendingEditList();

        editor.Nudge(0, 1, -1);
        editor.Stage(pending);

        var staged = Assert.Single(pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoOrder, staged.Kind);
        Assert.Equal(Series, staged.EntityId);
        Assert.Equal([B, A, C], staged.OrderedIds);
        Assert.Equal("Cooking with Sam", pending.Label(staged.Id));
    }

    [Fact]
    public void PuttingTheListBackAsItWas_StagesNothing()
    {
        var editor = Plain();
        var pending = new PendingEditList();
        editor.Nudge(0, 1, -1);
        editor.Stage(pending);

        editor.Nudge(0, 0, 1);
        editor.Stage(pending);

        Assert.Empty(pending.Changes);
        Assert.Equal(string.Empty, editor.WhatWillHappen());
    }

    [Fact]
    public void AMoveToAnotherSection_BecomesASeriesChangeForThatVideo()
    {
        var editor = Sections();
        var pending = new PendingEditList();

        editor.Drop(0, 0, 1, 2);
        editor.Stage(pending);

        var staged = Assert.Single(pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoSeries, staged.Kind);
        Assert.Equal(A, staged.EntityId);
        Assert.Equal(PartTwo.ToString(), staged.TargetId);
    }

    [Fact]
    public void AMoveToTheMiddleOfAnotherSection_AlsoStagesTheOrderThereAndReplacesEarlierStaging()
    {
        var editor = Sections();
        var pending = new PendingEditList();

        editor.Drop(0, 0, 1, 2);
        editor.Stage(pending);
        editor.Drop(1, 2, 1, 0);
        editor.Stage(pending);

        Assert.Equal(2, pending.Changes.Count);
        Assert.Equal([A, C, D], pending.FindVideoOrder(PartTwo)!.OrderedIds);
        Assert.NotNull(pending.FindVideoSeries(A));
    }

    [Fact]
    public void WhatWillHappen_SaysItIsLibraryOnly()
    {
        var editor = Plain();

        editor.Nudge(0, 2, -1);

        Assert.Contains("new order", editor.WhatWillHappen());
        Assert.Contains("not moved or renamed", editor.WhatWillHappen());
    }

    [Fact]
    public void ASeriesOfOneVideo_HasNothingToOrder()
    {
        var editor = new VideoOrderEditor();
        editor.Load(new VideoOrderResponse { Groups = [new VideoOrderGroupDto { ParentId = Series, Title = "S", Entries = [Entry(A, "Only")] }] });

        Assert.False(editor.HasSomethingToOrder);
    }

    [Fact]
    public void Review_MarksSeriesAndOrderLibraryOnly_AndTheRenameAsLastAndPermanent()
    {
        var pending = new PendingEditList();
        pending.SetTarget(EditSessionChangeKinds.VideoSeries, A, Series.ToString(), null, "Cooking with Sam", null, EditReviewRows.NotInASeries);
        pending.SetVideoOrder(Series, [B, A], "Cooking with Sam");
        pending.SetTarget(EditSessionChangeKinds.VideoRename, Series, null, null, "Cooking with Sam");

        var rows = EditReviewRows.Build(pending, _ => "Interview", null);

        Assert.Equal(["Series", "Order", "File names"], rows.Select(row => row.What));
        Assert.Contains("Library only", rows[0].Warning);
        Assert.Contains("Library only", rows[1].Warning);
        Assert.Equal(EditReviewGroup.PhysicalFiles, rows[2].Group);
        Assert.Contains("cannot be undone", rows[2].Warning);
        Assert.Equal("2 videos in a new order", rows[1].After);
    }
}
