using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The rules behind the editor's Bookmarks tab (universal editor v5 D37, WP15): how a staged bookmark operation joins the ones already staged,
/// what the tab shows while changes are staged, what Review changes says, and how a time is read and written. Pure; no Engine.
/// </summary>
public sealed class EditorBookmarkStagingTests
{
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly Guid Opening = Guid.NewGuid();
    private static readonly Guid Twist = Guid.NewGuid();
    private static readonly Guid Asset = Guid.NewGuid();

    private static EditorBookmarkOp Op(string kind, Guid? id = null, string? name = null, double? seconds = null, string opId = "") =>
        new() { Id = opId, Kind = kind, BookmarkId = id, Name = name, PositionSeconds = seconds };

    private static EditorBookmarkRowDto Row(Guid id, string name, double seconds, bool inEveryone = false, string owner = "Mira") => new()
    {
        Id = id,
        AssetId = Asset,
        PositionSeconds = seconds,
        Name = name,
        OwnerName = owner,
        InEveryone = inEveryone,
        CreatedAt = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds),
    };

    private static EditorBookmarksDto Loaded(string state = EditorBookmarkStates.Pooled) => new()
    {
        WorkId = WorkId,
        State = state,
        AssetId = Asset,
        HasTimes = true,
        Everyone = [Row(Opening, "Opening", 724, inEveryone: true)],
        FromProfiles = [Row(Opening, "Opening", 724, inEveryone: true), Row(Twist, "The twist", 3100)],
    };

    // ---------------------------------------------------------------- staging

    [Fact]
    public void RemovingABookmarkJustAdded_LeavesNothingStaged()
    {
        var ops = EditorBookmarkStaging.Stage([], Op(EditorBookmarkOpKinds.Add, Twist));
        ops = EditorBookmarkStaging.Stage(ops, Op(EditorBookmarkOpKinds.Remove, Twist));

        Assert.Empty(ops);
    }

    [Fact]
    public void AddingABookmarkStagedForRemoval_CancelsTheRemoval()
    {
        var ops = EditorBookmarkStaging.Stage([], Op(EditorBookmarkOpKinds.Remove, Opening));
        ops = EditorBookmarkStaging.Stage(ops, Op(EditorBookmarkOpKinds.Add, Opening));

        Assert.Empty(ops);
    }

    [Fact]
    public void AddingTheSameBookmarkTwice_StagesItOnce()
    {
        var ops = EditorBookmarkStaging.Stage([], Op(EditorBookmarkOpKinds.Add, Twist));
        ops = EditorBookmarkStaging.Stage(ops, Op(EditorBookmarkOpKinds.Add, Twist));

        Assert.Single(ops);
    }

    [Fact]
    public void RenamingTheSameBookmarkTwice_KeepsOnlyTheLastName()
    {
        var ops = EditorBookmarkStaging.Stage([], Op(EditorBookmarkOpKinds.Rename, Opening, name: "Cold open", opId: "a"));
        ops = EditorBookmarkStaging.Stage(ops, Op(EditorBookmarkOpKinds.Rename, Opening, name: "Prologue", opId: "b"));

        var rename = Assert.Single(ops);
        Assert.Equal("Prologue", rename.Name);
    }

    [Fact]
    public void ChangingTheTimeOfTheSameBookmarkTwice_KeepsOnlyTheLastTime()
    {
        var ops = EditorBookmarkStaging.Stage([], Op(EditorBookmarkOpKinds.ChangeTime, Opening, seconds: 700, opId: "a"));
        ops = EditorBookmarkStaging.Stage(ops, Op(EditorBookmarkOpKinds.ChangeTime, Opening, seconds: 735, opId: "b"));

        Assert.Equal(735, Assert.Single(ops).PositionSeconds);
    }

    [Fact]
    public void RemovingABookmarkRenamedEarlier_DropsTheRename_AndKeepsTheRemoval()
    {
        var ops = EditorBookmarkStaging.Stage([], Op(EditorBookmarkOpKinds.Rename, Opening, name: "Cold open"));
        ops = EditorBookmarkStaging.Stage(ops, Op(EditorBookmarkOpKinds.Remove, Opening));

        Assert.Equal(EditorBookmarkOpKinds.Remove, Assert.Single(ops).Kind);
    }

    [Fact]
    public void StartOver_ThrowsEveryEarlierOperationAway()
    {
        var ops = EditorBookmarkStaging.Stage([], Op(EditorBookmarkOpKinds.Add, Twist));
        ops = EditorBookmarkStaging.Stage(ops, Op(EditorBookmarkOpKinds.Rename, Opening, name: "Prologue"));
        ops = EditorBookmarkStaging.Stage(ops, Op(EditorBookmarkOpKinds.StartOver));

        Assert.Equal(EditorBookmarkOpKinds.StartOver, Assert.Single(ops).Kind);
    }

    [Fact]
    public void AnImportedBookmark_IsRenamedMovedAndRemovedInsideTheImport()
    {
        var import = new EditorBookmarkOp
        {
            Id = "imp",
            Kind = EditorBookmarkOpKinds.Import,
            Items = [new EditorBookmarkImportItem { PositionSeconds = 60, Name = "A" }, new EditorBookmarkImportItem { PositionSeconds = 120, Name = "B" }],
        };
        var first = EditorBookmarkProjection.ImportedId("imp", 0);
        var second = EditorBookmarkProjection.ImportedId("imp", 1);

        var ops = EditorBookmarkStaging.Stage([import], Op(EditorBookmarkOpKinds.Rename, first, name: "Cold open"));
        ops = EditorBookmarkStaging.Stage(ops, Op(EditorBookmarkOpKinds.ChangeTime, second, seconds: 130));
        var items = Assert.Single(ops).Items;
        Assert.Equal("Cold open", items[0].Name);
        Assert.Equal(130, items[1].PositionSeconds);

        ops = EditorBookmarkStaging.Stage(ops, Op(EditorBookmarkOpKinds.Remove, first));
        Assert.Equal(130, Assert.Single(Assert.Single(ops).Items).PositionSeconds);

        ops = EditorBookmarkStaging.Stage(ops, Op(EditorBookmarkOpKinds.Remove, EditorBookmarkProjection.ImportedId("imp", 0)));
        Assert.Empty(ops);
    }

    // ---------------------------------------------------------------- what the tab shows while changes are staged

    [Fact]
    public void WithNothingStaged_TheListsAreWhatTheEngineSent()
    {
        var view = EditorBookmarkProjection.Apply(Loaded(), []);

        Assert.Equal(EditorBookmarkStates.Pooled, view.State);
        Assert.False(view.StateStaged);
        Assert.Equal(0, view.StagedCount);
        Assert.All(view.Everyone, row => Assert.Equal(EditorBookmarkRowMark.None, row.Mark));
        Assert.Equal(2, view.FromProfiles.Count);
    }

    [Fact]
    public void AStagedAdd_PutsTheBookmarkInEveryones_MarkedAdded_AndMakesTheSetDefinitive()
    {
        var view = EditorBookmarkProjection.Apply(Loaded(), [Op(EditorBookmarkOpKinds.Add, Twist, opId: "a")]);

        Assert.Equal(EditorBookmarkStates.Definitive, view.State);
        Assert.True(view.StateStaged);
        Assert.Equal(1, view.StagedCount);
        var added = Assert.Single(view.Everyone, row => row.Id == Twist);
        Assert.Equal(EditorBookmarkRowMark.Added, added.Mark);
        Assert.True(Assert.Single(view.FromProfiles, row => row.Id == Twist).Row.InEveryone);
        Assert.Equal(2, view.Everyone.Count);
    }

    [Fact]
    public void AStagedRemove_KeepsTheRowListedAsRemoved_AndFreesItInTheProfilesList()
    {
        var view = EditorBookmarkProjection.Apply(Loaded(), [Op(EditorBookmarkOpKinds.Remove, Opening, opId: "r")]);

        Assert.Equal(EditorBookmarkRowMark.Removed, Assert.Single(view.Everyone).Mark);
        Assert.False(Assert.Single(view.FromProfiles, row => row.Id == Opening).Row.InEveryone);
    }

    [Fact]
    public void AStagedRenameAndTimeChange_ShowTheNewNameAndPlace_InOrder()
    {
        var loaded = Loaded() with
        {
            Everyone = [Row(Opening, "Opening", 724, inEveryone: true), Row(Twist, "The twist", 3100, inEveryone: true)],
        };
        var view = EditorBookmarkProjection.Apply(loaded,
        [
            Op(EditorBookmarkOpKinds.Rename, Opening, name: "Cold open", opId: "n"),
            Op(EditorBookmarkOpKinds.ChangeTime, Opening, seconds: 4000, opId: "t"),
        ]);

        Assert.Equal([Twist, Opening], view.Everyone.Select(row => row.Id).ToArray());
        var moved = view.Everyone[1];
        Assert.Equal("Cold open", moved.Row.Name);
        Assert.Equal(4000, moved.Row.PositionSeconds);
        Assert.Equal(EditorBookmarkRowMark.Moved, moved.Mark);
    }

    [Fact]
    public void AStagedImport_ListsEachBookmarkAsImported_AndRemovesTheOneItReplaces()
    {
        var import = new EditorBookmarkOp
        {
            Id = "imp",
            Kind = EditorBookmarkOpKinds.Import,
            Items = [new EditorBookmarkImportItem { PositionSeconds = 730, Name = "Opening scene", ReplacesBookmarkId = Opening }, new EditorBookmarkImportItem { PositionSeconds = 90, Name = "Titles" }],
        };

        var view = EditorBookmarkProjection.Apply(Loaded(), [import]);

        Assert.Equal(EditorBookmarkRowMark.Removed, Assert.Single(view.Everyone, row => row.Id == Opening).Mark);
        Assert.Equal(2, view.Everyone.Count(row => row.Mark == EditorBookmarkRowMark.Imported));
        Assert.Equal(EditorBookmarkStates.Definitive, view.State);
    }

    [Fact]
    public void AStagedClearGlobal_EmptiesGlobal_AndLeavesEveryProfilesBookmarks()
    {
        var view = EditorBookmarkProjection.Apply(Loaded(EditorBookmarkStates.Definitive), [Op(EditorBookmarkOpKinds.StartOver, opId: "s")]);

        Assert.Equal(EditorBookmarkStates.Pooled, view.State);
        Assert.True(view.StateStaged);
        Assert.Empty(view.Everyone);
        Assert.Equal(2, view.FromProfiles.Count);
    }

    // ---------------------------------------------------------------- the pending list

    [Fact]
    public void TheBookmarksChange_CountsOneThingPerOperation_AndRidesInTheSaveRequest()
    {
        var pending = new PendingEditList();
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Add, Twist), "Added to Global: 51:40 The twist");
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Rename, Opening, name: "Cold open"), "Renamed 12:04 to Cold open");

        Assert.Equal(2, pending.ChangeCount);
        var change = Assert.Single(pending.ToRequest(previewOnly: false).Changes);
        Assert.Equal(EditSessionChangeKinds.Bookmarks, change.Kind);
        Assert.Equal(WorkId, change.EntityId);
        Assert.Equal(2, change.Bookmarks!.Ops.Count);
        Assert.All(change.Bookmarks.Ops, op => Assert.False(string.IsNullOrWhiteSpace(op.Id)));
    }

    [Fact]
    public void ClearingTheLastOperation_RemovesTheTitlesChange()
    {
        var pending = new PendingEditList();
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Add, Twist), "Added");
        var only = pending.BookmarkOps(WorkId).Single();

        Assert.True(pending.ClearBookmarkOp(WorkId, only.Id));

        Assert.True(pending.IsEmpty);
        Assert.False(pending.ClearBookmarkOp(WorkId, only.Id));
    }

    [Fact]
    public void ACancelledOperation_TakesItsReviewLineWithIt()
    {
        var pending = new PendingEditList();
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Add, Twist), "Added to Global: 51:40 The twist");
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Rename, Opening, name: "Cold open"), "Renamed 12:04 to Cold open");

        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Remove, Twist), "Removed from Global: 51:40 The twist");

        var rows = EditReviewRows.Build(pending, _ => "Dune", null);
        var row = Assert.Single(rows);
        Assert.Equal("Renamed 12:04 to Cold open", row.After);
    }

    [Fact]
    public void Discarding_ClearsTheBookmarkOperations()
    {
        var pending = new PendingEditList();
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Add, Twist), "Added");

        pending.Discard();

        Assert.Empty(pending.BookmarkOps(WorkId));
        Assert.True(pending.IsEmpty);
    }

    // ---------------------------------------------------------------- Review changes

    [Fact]
    public void Review_ListsOneBookmarksRowPerOperation_InItsOwnGroup_WithTheLinesOfTheTab()
    {
        var pending = new PendingEditList();
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Add, Twist), EditorBookmarkWording.AddedLine(Row(Twist, "The twist", 3100)));
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Remove, Opening), EditorBookmarkWording.RemovedLine(Row(Opening, "Opening", 724)));
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Rename, Twist, name: "Cold open"), "Renamed to Cold open");

        var rows = EditReviewRows.Build(pending, _ => "Dune: Part One", null);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(EditReviewGroup.Bookmarks, row.Group);
            Assert.Equal(EditTool.Bookmarks, row.Tool);
            Assert.Equal("Dune: Part One", row.Item);
            Assert.False(string.IsNullOrWhiteSpace(row.FieldKey));
            Assert.Equal(EditReviewRows.EveryoneAsItIsNow, row.Before);
        });
        Assert.Equal("Added to Global: 51:40 The twist", rows[0].After);
        Assert.Equal("Removed from Global: 12:04 Opening", rows[1].After);
        Assert.Equal("Renamed to Cold open", rows[2].After);
        Assert.Equal("Bookmarks", EditReviewRows.GroupTitle(EditReviewGroup.Bookmarks));
    }

    [Fact]
    public void StartingOver_IsOneReviewRowThatWarnsEverythingStagedBeforeItIsGone()
    {
        var pending = new PendingEditList();
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Add, Twist), "Added");
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.StartOver), EditorBookmarkWording.StartOverLine);

        var row = Assert.Single(EditReviewRows.Build(pending, _ => "Dune: Part One", null));

        Assert.Equal("Cleared Global", row.After);
        Assert.NotNull(row.Warning);
    }

    [Fact]
    public void ARowsOperationIsTakenBackOutOnItsOwn()
    {
        var pending = new PendingEditList();
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Add, Twist), "Added");
        pending.StageBookmarkOp(WorkId, Op(EditorBookmarkOpKinds.Rename, Opening, name: "Cold open"), "Renamed");
        var row = EditReviewRows.Build(pending, _ => "Dune", null)[0];

        Assert.True(pending.ClearBookmarkOp(row.EntityId, row.FieldKey!));

        var left = Assert.Single(pending.BookmarkOps(WorkId));
        Assert.Equal(EditorBookmarkOpKinds.Rename, left.Kind);
    }

    // ---------------------------------------------------------------- wording

    [Theory]
    [InlineData(7, "0:07")]
    [InlineData(724, "12:04")]
    [InlineData(3723, "1:02:03")]
    public void ATimeIsWrittenAsMinutesAndSeconds_OrWithHours(double seconds, string expected)
    {
        Assert.Equal(expected, EditorBookmarkWording.Time(seconds));
    }

    [Theory]
    [InlineData("12:04", 724)]
    [InlineData("1:02:03", 3723)]
    [InlineData("90", 90)]
    public void ATimeIsReadFromMinutesAndSecondsHoursOrPlainSeconds(string text, double expected)
    {
        Assert.True(EditorBookmarkWording.TryParseTime(text, null, out var seconds));
        Assert.Equal(expected, seconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("1:2:3:4")]
    public void ANonsenseTime_IsRefused(string text)
    {
        Assert.False(EditorBookmarkWording.TryParseTime(text, null, out _));
    }

    [Fact]
    public void ATimePastTheRunningTime_IsRefused()
    {
        Assert.False(EditorBookmarkWording.TryParseTime("2:00:01", 7200, out _));
        Assert.True(EditorBookmarkWording.TryParseTime("2:00:00", 7200, out _));
    }

    [Fact]
    public void ThePictureOfAMoment_IsDrawnFromTheDashboardsOwnAddress_KeepingTheEnginesVersion()
    {
        var row = Row(Opening, "Opening", 724) with { FrameUrl = $"/api/v1/player/bookmarks/{Opening:D}/frame?v=42" };

        Assert.Equal($"/engine-bookmark-frame/{Opening:D}?v=42", EditorBookmarkWording.FrameSource(row));
        Assert.Null(EditorBookmarkWording.FrameSource(row with { FrameUrl = null }));
    }

    [Fact]
    public void PageBookmarks_AreWrittenAsPages_AndImportedAsPageBookmarks()
    {
        var row = new BookmarkSetCompareRowDto
        {
            Incoming = new BookmarkSetItemDto { PositionSeconds = 14, Locator = "page:14", Note = "The reveal" },
            Yours = new BookmarkSetExistingDto { PositionSeconds = 12, Locator = "page:12" },
            GapPages = 2,
            Closeness = BookmarkClosenessLabels.Close,
        };

        Assert.Equal("Page 14", EditorBookmarkWording.IncomingPlace(row.Incoming));
        Assert.Equal("Page 12", EditorBookmarkWording.NearestPlace(row.Yours));
        var item = EditorBookmarkWording.ImportItem(row, replaceNearest: false);
        Assert.Equal("page", item.PositionKind);
        Assert.Equal("page:14", item.Locator);
        Assert.Null(item.ChapterIndex);
    }
}
