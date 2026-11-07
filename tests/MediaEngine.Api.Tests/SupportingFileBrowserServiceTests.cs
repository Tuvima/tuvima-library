using MediaEngine.Api.Services.Extras;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Engine side of the supporting-files browser (universal editor v5 WP4): the list with its search, Where, type and group filters and
/// paging, and the bulk actions the editor sends at Save. A show with 238 generated files stands in for a big course.
/// </summary>
public sealed class SupportingFileBrowserServiceTests : IDisposable
{
    private readonly SupportingFileTestWorld _world = new();
    private readonly Guid _title = Guid.NewGuid();

    public void Dispose() => _world.Dispose();

    /// <summary>Applies a change the way the edit session does (the undo it hands back is not needed here).</summary>
    private static async Task<SupportingFileBulkOutcome<SupportingFileBulkUpdateResultDto>> SaveAsync(
        SupportingFileBulkService bulk, Guid titleId, SupportingFileBulkUpdateRequest request) =>
        (await bulk.ApplyAsync(titleId, request)).Outcome;

    // ── The list ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_FilteredBySeasonAndType_ReturnsTheRightCount_ForAShowWith238Files()
    {
        await _world.SeedGeneratedFilesAsync();

        var everything = await _world.List.ListAsync(_title, new SupportingFileListQuery());
        var seasonTwo = await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:2"));
        var seasonTwoDocuments = await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:2", Type: SupportingFileListDefaults.TypeDocument));
        var seasonTwoPictures = await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:2", Type: SupportingFileListDefaults.TypePicture));
        var seasonTwoAudio = await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:2", Type: SupportingFileListDefaults.TypeAudio));
        var allClips = await _world.List.ListAsync(_title, new SupportingFileListQuery(Type: SupportingFileListDefaults.TypeClip));

        Assert.Equal(SupportingFileTestWorld.GeneratedFiles, everything.Total);
        Assert.Equal(SupportingFileTestWorld.GeneratedFiles, everything.TotalAll);
        Assert.Equal(34, seasonTwo.Total);
        Assert.Equal(9, seasonTwoDocuments.Total);
        Assert.Equal(9, seasonTwoPictures.Total);
        Assert.Equal(8, seasonTwoAudio.Total);
        Assert.Equal(7 * 8, allClips.Total);
        Assert.All(seasonTwoDocuments.Items, row => Assert.Equal("season:2", row.WhereKey));
    }

    [Fact]
    public async Task List_PagesAtTheDefaultSize_AndTakesAPageSizeFromTheRequest()
    {
        await _world.SeedGeneratedFilesAsync();

        var first = await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:2"));
        var second = await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:2", Page: 2));
        var small = await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:2", PageSize: 10, Page: 4));
        var past = await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:2", Page: 99));

        Assert.Equal(SupportingFileListDefaults.PageSize, first.PageSize);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(2, first.PageCount);
        Assert.Equal(9, second.Items.Count);
        Assert.Empty(first.Items.Select(row => row.Id).Intersect(second.Items.Select(row => row.Id)));
        Assert.Equal(4, small.Items.Count);
        Assert.Equal(4, small.PageCount);
        Assert.Equal(2, past.Page);
    }

    [Fact]
    public async Task List_FindsAFileByAWordInItsDescription_ItsTitle_OrItsFileName_IgnoringCase()
    {
        await _world.SeedGeneratedFilesAsync();
        var worksheet = (await _world.Extras.GetByPathAsync(Path.Combine(_world.SeasonFolder(3), "worksheet-003.pdf")))!;
        await _world.Extras.UpdateOwnedAsync(worksheet.Id, new ExtraOwnedUpdate { Description = ExtraValue<string>.Set("The lighthouse keeper's timeline") });

        var byDescription = await _world.List.ListAsync(_title, new SupportingFileListQuery(Search: "LIGHTHOUSE timeline"));
        var byTitle = await _world.List.ListAsync(_title, new SupportingFileListQuery(Search: "worksheet 3"));
        var byFileName = await _world.List.ListAsync(_title, new SupportingFileListQuery(Search: "scene-016.mp4"));
        var nothing = await _world.List.ListAsync(_title, new SupportingFileListQuery(Search: "lighthouse zebra"));

        Assert.Equal(worksheet.Id.ToString("D"), Assert.Single(byDescription.Items).Id);
        Assert.Contains(byTitle.Items, row => row.Id == worksheet.Id.ToString("D"));
        Assert.Equal("scene-016.mp4", Assert.Single(byFileName.Items).FileName);
        Assert.Empty(nothing.Items);
    }

    [Fact]
    public async Task List_ReportsTheWhereOptionsWithCounts_AndTheTitleLevelBeforeTheSeasons()
    {
        await _world.SeedGeneratedFilesAsync();

        var list = await _world.List.ListAsync(_title, new SupportingFileListQuery(Type: SupportingFileListDefaults.TypeDocument));

        Assert.Equal(["all", "root", "season:1", "season:2", "season:3", "season:4", "season:5", "season:6"], list.WhereOptions.Select(option => option.Key));
        Assert.Equal(SupportingFileTestWorld.GeneratedFiles, list.WhereOptions.Single(option => option.Key == "all").FileCount);
        Assert.Equal(34, list.WhereOptions.Single(option => option.Key == "season:5").FileCount);
        Assert.Equal(SupportingFileScopes.WholeTitleLabel, list.WhereOptions[0].Label);
        Assert.Equal("Title level", list.WhereOptions.Single(option => option.Key == "root").Label);
        Assert.Equal(34, list.WhereOptions.Single(option => option.Key == "root").FileCount);
    }

    [Fact]
    public async Task List_CountsFilesWithNoGroupAsUngrouped_AndFiltersByGroup()
    {
        await _world.SeedGeneratedFilesAsync();
        var homework = (await _world.Extras.GetByPathAsync(Path.Combine(_world.SeasonFolder(1), "worksheet-001.pdf")))!;
        await _world.Extras.UpdateOwnedAsync(homework.Id, new ExtraOwnedUpdate { GroupName = ExtraValue<string>.Set("Homework") });

        var all = await _world.List.ListAsync(_title, new SupportingFileListQuery());
        var ungrouped = await _world.List.ListAsync(_title, new SupportingFileListQuery(Group: SupportingFileListDefaults.GroupUngrouped));
        var group = Assert.Single(all.Groups);
        var grouped = await _world.List.ListAsync(_title, new SupportingFileListQuery(Group: group.Id));

        Assert.Equal("Homework", group.Name);
        Assert.Equal(1, group.FileCount);
        Assert.Equal(SupportingFileTestWorld.GeneratedFiles - 1, all.UngroupedCount);
        Assert.Equal(SupportingFileTestWorld.GeneratedFiles - 1, ungrouped.Total);
        Assert.Equal(homework.Id.ToString("D"), Assert.Single(grouped.Items).Id);
    }

    [Fact]
    public async Task List_SortsByTitle_Type_OrWhere_AndCanReverse()
    {
        await _world.SeedGeneratedFilesAsync();

        var byTitle = await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:1", Sort: SupportingFileListDefaults.SortTitle, PageSize: 200));
        var reversed = await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:1", Sort: SupportingFileListDefaults.SortTitle, Descending: true, PageSize: 200));
        var byType = await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:1", Sort: SupportingFileListDefaults.SortType, PageSize: 200));

        Assert.Equal(byTitle.Items.Select(row => row.Title).Order(StringComparer.OrdinalIgnoreCase), byTitle.Items.Select(row => row.Title));
        Assert.Equal(byTitle.Items.Select(row => row.Id).Reverse(), reversed.Items.Select(row => row.Id));
        // Sorted by type, every type forms one run: none comes back after another type has started.
        var types = byType.Items.Select(row => row.Type).ToList();
        Assert.Equal(types.Distinct().Count(), types.Where((type, index) => index == 0 || type != types[index - 1]).Count());
    }

    [Fact]
    public async Task List_ForATitleWithNoFolder_IsEmpty()
    {
        _world.Titles.HasFolder = false;

        var list = await _world.List.ListAsync(_title, new SupportingFileListQuery());

        Assert.Empty(list.Items);
        Assert.Equal(0, list.Total);
        Assert.Equal(1, list.PageCount);
    }

    // ── Bulk update ───────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkUpdate_SetsTheGroupOf25TickedFilesInOneCall_AndTheUngroupedCountFalls()
    {
        await _world.SeedGeneratedFilesAsync();
        var before = await _world.List.ListAsync(_title, new SupportingFileListQuery());
        var ticked = (await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:3", PageSize: 25))).Items.Select(row => row.Id).ToList();

        var outcome = await SaveAsync(_world.Bulk, _title, new SupportingFileBulkUpdateRequest
        {
            Ids = ticked,
            Changes = new SupportingFileBulkChangesDto { GroupName = "Homework" },
        });

        Assert.Equal(25, ticked.Count);
        var result = Assert.IsType<SupportingFileBulkUpdateResultDto>(outcome.Value);
        Assert.Equal(25, result.Changed);
        Assert.All(result.Results, item => Assert.Equal(SupportingFileBulkStatuses.Changed, item.Status));
        Assert.Empty(result.Moves);

        var after = await _world.List.ListAsync(_title, new SupportingFileListQuery());
        Assert.Equal(SupportingFileTestWorld.GeneratedFiles, before.UngroupedCount);
        Assert.Equal(SupportingFileTestWorld.GeneratedFiles - 25, after.UngroupedCount);
        var group = Assert.Single(after.Groups);
        Assert.Equal(("Homework", 25), (group.Name, group.FileCount));
        var inGroup = await _world.List.ListAsync(_title, new SupportingFileListQuery(Group: group.Id));
        Assert.Equal(25, inGroup.Total);
    }

    [Fact]
    public async Task BulkUpdate_RepeatedWithTheSameChange_ChangesNothingTheSecondTime()
    {
        await _world.SeedGeneratedFilesAsync();
        var ids = (await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:4", PageSize: 10))).Items.Select(row => row.Id).ToList();
        var request = new SupportingFileBulkUpdateRequest
        {
            Ids = ids,
            Changes = new SupportingFileBulkChangesDto { GroupName = "Extra reading", AddTags = ["maths", "Maths"] },
        };

        var first = (await SaveAsync(_world.Bulk, _title, request)).Value!;
        var second = (await SaveAsync(_world.Bulk, _title, request)).Value!;

        Assert.Equal(10, first.Changed);
        Assert.Equal(0, second.Changed);
        Assert.All(second.Results, item => Assert.Equal(SupportingFileBulkStatuses.Unchanged, item.Status));
        var list = await _world.List.ListAsync(_title, new SupportingFileListQuery(Where: "season:4", PageSize: 200));
        Assert.Single(list.Groups);
        Assert.All(list.Items.Where(row => ids.Contains(row.Id)), row => Assert.Equal(["maths"], row.Tags));
    }

    [Fact]
    public async Task BulkUpdate_AddsAndRemovesTags_AndSetsTheRole()
    {
        await _world.SeedGeneratedFilesAsync();
        var worksheet = (await _world.Extras.GetByPathAsync(Path.Combine(_world.SeasonFolder(1), "worksheet-001.pdf")))!;
        var id = worksheet.Id.ToString("D");
        await SaveAsync(_world.Bulk, _title, new SupportingFileBulkUpdateRequest
        {
            Ids = [id],
            Changes = new SupportingFileBulkChangesDto { AddTags = ["maths", "term one"] },
        });

        var outcome = await SaveAsync(_world.Bulk, _title, new SupportingFileBulkUpdateRequest
        {
            Ids = [id],
            Changes = new SupportingFileBulkChangesDto { RemoveTags = ["maths"], Role = ExtraRoles.Worksheet },
        });

        Assert.Equal(1, outcome.Value!.Changed);
        var row = Assert.Single((await _world.List.ListAsync(_title, new SupportingFileListQuery(Search: "worksheet 1", Where: "season:1"))).Items, item => item.Id == id);
        Assert.Equal(["term one"], row.Tags);
        Assert.Equal(ExtraRoles.Worksheet, row.Role);
    }

    [Fact]
    public async Task BulkUpdate_RefusesARoleThatDoesNotFitTheKindOfFile_WithAPlainReason()
    {
        await _world.SeedGeneratedFilesAsync();
        var worksheet = (await _world.Extras.GetByPathAsync(Path.Combine(_world.SeasonFolder(1), "worksheet-001.pdf")))!;

        var outcome = await SaveAsync(_world.Bulk, _title, new SupportingFileBulkUpdateRequest
        {
            Ids = [worksheet.Id.ToString("D")],
            Changes = new SupportingFileBulkChangesDto { Role = ExtraRoles.Trailer },
        });

        Assert.Null(outcome.Value);
        Assert.Equal(SupportingFileBulkProblem.Invalid, outcome.Problem);
        Assert.Contains("is not available for this kind of file", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(ExtraRoles.Other, (await _world.Extras.GetByPathAsync(worksheet.FilePath))!.EffectiveRole);
    }

    [Fact]
    public async Task BulkUpdate_ReportsAFileOfAnotherTitleAsRefused_AndAFileThatIsGoneAsNotFound_WhileChangingTheRest()
    {
        await _world.SeedGeneratedFilesAsync();
        var mine = (await _world.Extras.GetByPathAsync(Path.Combine(_world.SeasonFolder(1), "worksheet-001.pdf")))!;
        var otherFolder = Path.Combine(_world.Root, "Another Show");
        var other = _world.Row(Path.Combine(otherFolder, "notes.pdf"), ItemExtraKinds.Document, "Notes", ExtraScope.Root) with { ItemFolder = otherFolder };
        await _world.Extras.SyncItemFolderAsync(otherFolder, [other], []);
        var gone = Guid.NewGuid().ToString("D");

        var outcome = await SaveAsync(_world.Bulk, _title, new SupportingFileBulkUpdateRequest
        {
            Ids = [mine.Id.ToString("D"), other.Id.ToString("D"), gone],
            Changes = new SupportingFileBulkChangesDto { GroupName = "Homework" },
        });

        var statuses = outcome.Value!.Results.ToDictionary(item => item.Id, item => item.Status);
        Assert.Equal(SupportingFileBulkStatuses.Changed, statuses[mine.Id.ToString("D")]);
        Assert.Equal(SupportingFileBulkStatuses.Refused, statuses[other.Id.ToString("D")]);
        Assert.Equal(SupportingFileBulkStatuses.NotFound, statuses[gone]);
        Assert.Null((await _world.Extras.GetAsync(other.Id))!.GroupName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SupportingFileBulkService.MaxFilesPerCall + 1)]
    public async Task BulkUpdate_RefusesNothingPickedOrTooManyFiles(int count)
    {
        var outcome = await SaveAsync(_world.Bulk, _title, new SupportingFileBulkUpdateRequest
        {
            Ids = [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString("D"))],
            Changes = new SupportingFileBulkChangesDto { GroupName = "Homework" },
        });

        Assert.Equal(SupportingFileBulkProblem.Invalid, outcome.Problem);
    }

    [Fact]
    public async Task BulkUpdate_ForATitleWithNoFolder_IsNotFound()
    {
        _world.Titles.HasFolder = false;

        var outcome = await SaveAsync(_world.Bulk, _title, new SupportingFileBulkUpdateRequest
        {
            Ids = [Guid.NewGuid().ToString("D")],
            Changes = new SupportingFileBulkChangesDto { GroupName = "Homework" },
        });

        Assert.Equal(SupportingFileBulkProblem.TitleNotFound, outcome.Problem);
    }

    // ── Belongs to and the file moves ────────────────────────────────────────

    [Fact]
    public async Task BelongsToChange_InAManagedFolder_ReturnsThePlannedMove_AndOnlyMovesTheFileOnSave()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");
        var request = new SupportingFileBulkUpdateRequest
        {
            Ids = [guide.Id.ToString("D")],
            Changes = new SupportingFileBulkChangesDto { ScopeKey = "season:2" },
        };
        var target = Path.Combine(_world.SeasonFolder(2), "guide.pdf");

        var preview = (await _world.Bulk.PreviewAsync(_title, request)).Value!;

        var planned = Assert.Single(preview.Moves);
        Assert.True(preview.Preview);
        Assert.Equal(SupportingFileMoveStatuses.Planned, planned.Status);
        Assert.Equal(Path.GetFullPath(guide.FilePath), planned.From);
        Assert.Equal(Path.GetFullPath(target), planned.To);
        Assert.True(File.Exists(guide.FilePath));
        Assert.False(File.Exists(target));
        Assert.Null((await _world.Extras.GetAsync(guide.Id))!.Scope);

        var applied = (await SaveAsync(_world.Bulk, _title, new SupportingFileBulkUpdateRequest { Ids = request.Ids, Changes = request.Changes })).Value!;

        Assert.Equal(SupportingFileMoveStatuses.Moved, Assert.Single(applied.Moves).Status);
        Assert.False(File.Exists(guide.FilePath));
        Assert.True(File.Exists(target));
        var saved = (await _world.Extras.GetByPathAsync(Path.GetFullPath(target)))!;
        Assert.Equal(guide.Id, saved.Id);
        Assert.Equal(ExtraScope.ForSeason(2), saved.EffectiveScope);
    }

    [Fact]
    public async Task BelongsToChange_RepeatedAfterTheMove_PlansNoSecondMove()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");
        var request = new SupportingFileBulkUpdateRequest
        {
            Ids = [guide.Id.ToString("D")],
            Changes = new SupportingFileBulkChangesDto { ScopeKey = "season:2" },
        };
        await SaveAsync(_world.Bulk, _title, request);

        var again = (await SaveAsync(_world.Bulk, _title, request)).Value!;

        Assert.Empty(again.Moves);
        Assert.Equal(0, again.Changed);
    }

    [Fact]
    public async Task BelongsToChange_InAReadOnlyFolder_ReturnsNoMove_AndTheFileStaysWhereItIs()
    {
        using var readOnly = new SupportingFileTestWorld(managedFolder: false);
        var guide = await readOnly.SeedDiskFileAsync("guide.pdf", "Guide");

        var result = (await readOnly.Bulk.PreviewAsync(Guid.NewGuid(), new SupportingFileBulkUpdateRequest
        {
            Ids = [guide.Id.ToString("D")],
            Changes = new SupportingFileBulkChangesDto { ScopeKey = "season:2" },
        })).Value!;

        Assert.Empty(result.Moves);
        Assert.Equal(SupportingFileDiskService.StaysWhereItIs, Assert.Single(result.Results).Note);

        var applied = (await SaveAsync(readOnly.Bulk, Guid.NewGuid(), new SupportingFileBulkUpdateRequest
        {
            Ids = [guide.Id.ToString("D")],
            Changes = new SupportingFileBulkChangesDto { ScopeKey = "season:2" },
        })).Value!;

        Assert.Empty(applied.Moves);
        Assert.True(File.Exists(guide.FilePath));
        Assert.Equal(ExtraScope.ForSeason(2), (await readOnly.Extras.GetAsync(guide.Id))!.EffectiveScope);
    }

    [Fact]
    public async Task BelongsToChange_WhenTheNewFolderAlreadyHoldsAFileOfThatName_KeepsTheFileWhereItIs()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");
        Directory.CreateDirectory(_world.SeasonFolder(2));
        await File.WriteAllTextAsync(Path.Combine(_world.SeasonFolder(2), "guide.pdf"), "another file");

        var result = (await SaveAsync(_world.Bulk, _title, new SupportingFileBulkUpdateRequest
        {
            Ids = [guide.Id.ToString("D")],
            Changes = new SupportingFileBulkChangesDto { ScopeKey = "season:2" },
        })).Value!;

        Assert.Empty(result.Moves);
        Assert.Equal(SupportingFileDiskService.NameTaken, Assert.Single(result.Results).Note);
        Assert.True(File.Exists(guide.FilePath));
    }

    [Fact]
    public async Task UseFolderLocation_ClearsWhatAPersonSet_AndTheFileFollowsItsFolderAgain()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");
        await _world.Extras.UpdateOwnedAsync(guide.Id, new ExtraOwnedUpdate { Scope = ExtraValue<ExtraScope>.Set(ExtraScope.ForSeason(3)) });

        var result = (await SaveAsync(_world.Bulk, _title, new SupportingFileBulkUpdateRequest
        {
            Ids = [guide.Id.ToString("D")],
            Changes = new SupportingFileBulkChangesDto { UseFolderLocation = true },
        })).Value!;

        Assert.Equal(1, result.Changed);
        Assert.Null((await _world.Extras.GetAsync(guide.Id))!.Scope);
    }

    [Fact]
    public async Task BelongsToChange_ToAPlaceThatIsNotAPlace_IsRefused()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");

        var outcome = await SaveAsync(_world.Bulk, _title, new SupportingFileBulkUpdateRequest
        {
            Ids = [guide.Id.ToString("D")],
            Changes = new SupportingFileBulkChangesDto { ScopeKey = "nonsense" },
        });

        Assert.Equal(SupportingFileBulkProblem.Invalid, outcome.Problem);
        Assert.Equal(SupportingFileBulkService.BadPlace, outcome.Message);
    }

    // ── Bulk delete ───────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkDelete_RemovesTheRecords_AndDeletesTheFilesOnDisk_InAManagedFolder()
    {
        var one = await _world.SeedDiskFileAsync("one.pdf", "One");
        var two = await _world.SeedDiskFileAsync("two.pdf", "Two");
        var keep = await _world.SeedDiskFileAsync("keep.pdf", "Keep");

        var outcome = await _world.Bulk.DeleteAsync(
            _title, new SupportingFileBulkDeleteRequest { Ids = [one.Id.ToString("D"), two.Id.ToString("D")] }, null, "Shy");

        var result = outcome.Value!;
        Assert.Equal(2, result.Removed);
        Assert.All(result.Results, item => Assert.Equal(SupportingFileBulkStatuses.Removed, item.Status));
        Assert.All(result.Results, item => Assert.Equal(SupportingFileDiskOutcomes.Deleted, item.Disk));
        Assert.False(File.Exists(one.FilePath));
        Assert.False(File.Exists(two.FilePath));
        Assert.True(File.Exists(keep.FilePath));
        Assert.Equal([keep.Id], (await _world.Extras.ListAllInItemFolderAsync(_world.Show)).Select(row => row.Id));
    }

    [Fact]
    public async Task BulkDelete_MovesTheFilesToTheRecycleBin_WhenTheLibraryKeepsOne()
    {
        using var world = new SupportingFileTestWorld(managedFolder: true, recycleBin: true);
        var one = await world.SeedDiskFileAsync("one.pdf", "One");

        var result = (await world.Bulk.DeleteAsync(
            Guid.NewGuid(), new SupportingFileBulkDeleteRequest { Ids = [one.Id.ToString("D")] }, null, "Shy")).Value!;

        Assert.Equal(SupportingFileDiskOutcomes.Recycled, Assert.Single(result.Results).Disk);
        Assert.False(File.Exists(one.FilePath));
        Assert.Empty(await world.Extras.ListAllInItemFolderAsync(world.Show));
    }

    [Fact]
    public async Task BulkDelete_InAReadOnlyFolder_RemovesTheRecordOnly_KeepsTheFile_AndRemembersItAsExcluded()
    {
        using var world = new SupportingFileTestWorld(managedFolder: false);
        var one = await world.SeedDiskFileAsync("one.pdf", "One");

        var result = (await world.Bulk.DeleteAsync(
            Guid.NewGuid(), new SupportingFileBulkDeleteRequest { Ids = [one.Id.ToString("D")] }, null, "Shy")).Value!;

        var item = Assert.Single(result.Results);
        Assert.Equal(SupportingFileBulkStatuses.Removed, item.Status);
        Assert.Equal(SupportingFileDiskOutcomes.Kept, item.Disk);
        Assert.NotNull(item.Reason);
        Assert.True(File.Exists(one.FilePath));
        Assert.Empty(await world.Extras.ListAllInItemFolderAsync(world.Show));
        var excluded = Assert.Single(await world.Excluded.ListAllAsync());
        Assert.Equal($"{SupportingFileTestWorld.ShowName}/one.pdf", excluded.RelativePath);
        Assert.True(world.Filter.Invalidated > 0);
    }

    [Fact]
    public async Task BulkDelete_RepeatedForTheSameFiles_ReportsThemGoneAndChangesNothingElse()
    {
        var one = await _world.SeedDiskFileAsync("one.pdf", "One");
        var request = new SupportingFileBulkDeleteRequest { Ids = [one.Id.ToString("D")] };
        await _world.Bulk.DeleteAsync(_title, request, null, null);

        var again = (await _world.Bulk.DeleteAsync(_title, request, null, null)).Value!;

        Assert.Equal(0, again.Removed);
        Assert.Equal(SupportingFileBulkStatuses.NotFound, Assert.Single(again.Results).Status);
    }
}
