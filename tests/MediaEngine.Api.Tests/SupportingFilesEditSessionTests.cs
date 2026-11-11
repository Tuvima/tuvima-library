using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Extras;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Supporting-file changes inside the editor's one Save (universal editor v5 D16, D30): group, role, Belongs to and tags are a change of the
/// staged save, so one Save changes the title's fields and its files together or not at all. The real services and data store run; the
/// other changes in the session are hand-written spies.
/// </summary>
public sealed class SupportingFilesEditSessionTests : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly SupportingFileTestWorld _world = new();
    private readonly Guid _title = Guid.NewGuid();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task OneSave_WithADetailsChangeAndAGroupOn25Files_CommitsBoth()
    {
        await _world.SeedGeneratedFilesAsync();
        var ids = await FirstFileIdsAsync(25);
        var details = new SpyHandler(EditSessionChangeKinds.Details);

        var result = await Service(details).RunAsync(
            Request(Details("details"), Files("files", ids, new SupportingFileBulkChangesDto { GroupName = "Homework" })), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.All(result.Changes, change => Assert.Equal(EditSessionChangeStatuses.Ok, change.Status));
        Assert.Equal(["apply:details"], details.Calls);
        var group = Assert.Single(await _world.Extras.ListGroupsAsync(_world.Show));
        Assert.Equal("Homework", group.Name);
        var inGroup = (await _world.Extras.ListAllInItemFolderAsync(_world.Show)).Count(row => row.GroupName == "Homework");
        Assert.Equal(25, inGroup);
    }

    [Fact]
    public async Task OneSave_WhenALaterChangeFails_PutsEveryFileRowBack_IncludingTheGroupAndTags()
    {
        await _world.SeedGeneratedFilesAsync();
        var ids = await FirstFileIdsAsync(25);
        var before = await _world.Extras.ListAllInItemFolderAsync(_world.Show);

        // An identity change runs after the supporting files and cannot be put back itself, so its failure undoes everything before it.
        var result = await Service(new SpyHandler(EditSessionChangeKinds.Identity, fail: true)).RunAsync(
            Request(
                Files("files", ids, new SupportingFileBulkChangesDto { GroupName = "Homework", AddTags = ["maths"] }),
                new EditSessionChange { Id = "identity", Kind = EditSessionChangeKinds.Identity, EntityId = _title, TargetId = "{}" }),
            NoRefusals);

        Assert.False(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.Failed, result.Changes.Single(change => change.Id == "identity").Status);
        var files = result.Changes.Single(change => change.Id == "files");
        Assert.Equal(EditSessionChangeStatuses.NotApplied, files.Status);
        Assert.Equal(EditSessionReasons.UndoneBecauseAnotherFailed, files.Reason);
        var after = await _world.Extras.ListAllInItemFolderAsync(_world.Show);
        Assert.Equal(before.Count, after.Count);
        Assert.All(after, row => Assert.Null(row.GroupName));
        Assert.All(after, row => Assert.Empty(row.TagList));
    }

    [Fact]
    public async Task OneSave_CanRenameAndDescribeAFile()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");

        var result = await Service().RunAsync(
            Request(Files("files", [guide.Id], new SupportingFileBulkChangesDto
            {
                Title = "Shooting workbook",
                Description = "Drills for form, footwork and release.",
            })),
            NoRefusals);

        Assert.True(result.DataSaved);
        var saved = (await _world.Extras.GetByPathAsync(Path.GetFullPath(guide.FilePath)))!;
        Assert.Equal("Shooting workbook", saved.OwnTitle);
        Assert.Equal("Shooting workbook", saved.DisplayTitle);
        Assert.Equal("Drills for form, footwork and release.", saved.Description);
        // The file name read from the folder is untouched: clearing the title later returns to it.
        Assert.Equal("Guide", saved.Title);
    }

    [Fact]
    public async Task OneSave_WhenALaterChangeFails_PutsTheTitleAndDescriptionBack()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");

        var result = await Service(new SpyHandler(EditSessionChangeKinds.Identity, fail: true)).RunAsync(
            Request(
                Files("files", [guide.Id], new SupportingFileBulkChangesDto { Title = "Shooting workbook", Description = "Drills" }),
                new EditSessionChange { Id = "identity", Kind = EditSessionChangeKinds.Identity, EntityId = _title, TargetId = "{}" }),
            NoRefusals);

        Assert.False(result.DataSaved);
        var row = (await _world.Extras.GetByPathAsync(Path.GetFullPath(guide.FilePath)))!;
        Assert.Null(row.OwnTitle);
        Assert.Null(row.Description);
        Assert.Equal("Guide", row.DisplayTitle);
    }

    [Fact]
    public async Task ASave_ThatTitlesSeveralFilesAtOnce_IsInvalid_BecauseOneTitleCannotFitMany()
    {
        await _world.SeedGeneratedFilesAsync();
        var ids = await FirstFileIdsAsync(2);

        var result = await Service().RunAsync(
            new EditSessionRequest { PreviewOnly = true, Changes = [Files("files", ids, new SupportingFileBulkChangesDto { Title = "Same title" })] },
            NoRefusals);

        var refused = Assert.Single(result.Changes);
        Assert.Equal(EditSessionChangeStatuses.Invalid, refused.Status);
        Assert.Equal(SupportingFileBulkService.OneFileOnly, refused.Reason);
    }

    [Fact]
    public async Task OneSave_WhenALaterChangeFails_MovesTheFilesBack_AndRestoresTheirPlace()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");
        var target = Path.Combine(_world.SeasonFolder(2), "guide.pdf");

        var result = await Service(new SpyHandler(EditSessionChangeKinds.Identity, fail: true)).RunAsync(
            Request(
                Files("files", [guide.Id], new SupportingFileBulkChangesDto { ScopeKey = "season:2" }),
                new EditSessionChange { Id = "identity", Kind = EditSessionChangeKinds.Identity, EntityId = _title, TargetId = "{}" }),
            NoRefusals);

        Assert.False(result.DataSaved);
        Assert.True(File.Exists(guide.FilePath));
        Assert.False(File.Exists(target));
        var row = (await _world.Extras.GetByPathAsync(Path.GetFullPath(guide.FilePath)))!;
        Assert.Equal(guide.Id, row.Id);
        Assert.Equal(ItemExtraOrigins.Rule, row.Origin);
        Assert.Null(row.Scope);
        Assert.Equal(ExtraScope.Root, row.InferredScope);
    }

    [Fact]
    public async Task OneSave_ThatSucceeds_MovesTheFile_AndTellsTheScannerSoItIsNotReadAsANewFile()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");
        var target = Path.Combine(_world.SeasonFolder(2), "guide.pdf");

        var result = await Service().RunAsync(
            Request(Files("files", [guide.Id], new SupportingFileBulkChangesDto { ScopeKey = "season:2" })), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(guide.FilePath));
        Assert.Contains(Path.GetFullPath(target), _world.Scanner.Noted);
        Assert.Contains(Path.GetFullPath(guide.FilePath), _world.Scanner.Noted);
        var moved = (await _world.Extras.GetByPathAsync(Path.GetFullPath(target)))!;
        Assert.Equal(guide.Id, moved.Id);
        Assert.Equal(ItemExtraOrigins.UserExtra, moved.Origin);
        Assert.Equal(ExtraScope.ForSeason(2), moved.EffectiveScope);
    }

    [Fact]
    public async Task WhatOneSaveSets_SurvivesTheFoldersBeingReadAgain_AndIsWrittenToTheFilesHistory()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");

        var result = await Service().RunAsync(
            Request(Files("files", [guide.Id], new SupportingFileBulkChangesDto
            {
                Title = "Viewer's guide",
                Description = "Read before the pilot.",
                GroupName = "Guides",
                Role = ExtraRoles.Guide,
                AddTags = ["week 1"],
            })),
            NoRefusals);

        Assert.True(result.DataSaved);

        // The folders are read again and know nothing of what the person chose.
        await _world.Extras.SyncItemFolderAsync(_world.Show, [_world.Row(guide.FilePath, ItemExtraKinds.Document, "guide", ExtraScope.Root)], []);

        var after = (await _world.Extras.GetAsync(guide.Id))!;
        Assert.Equal("Viewer's guide", after.DisplayTitle);
        Assert.Equal("Read before the pilot.", after.Description);
        Assert.Equal("Guides", after.GroupName);
        Assert.Equal(ExtraRoles.Guide, after.EffectiveRole);
        Assert.Equal(["week 1"], after.TagList);
        var history = await SupportingFileHistory.Read(_world.Timeline, guide.Id, default);
        Assert.Equal(5, history.Count);
    }

    [Fact]
    public async Task ASave_ThatFindsNothingToChange_WritesNoHistory()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");
        var change = new SupportingFileBulkChangesDto { Title = "Viewer's guide" };

        await Service().RunAsync(Request(Files("files", [guide.Id], change)), NoRefusals);
        await Service().RunAsync(Request(Files("files", [guide.Id], change)), NoRefusals);

        Assert.Single(await SupportingFileHistory.Read(_world.Timeline, guide.Id, default));
    }

    [Fact]
    public async Task Preview_OfASave_ReportsTheMoves_AndWritesNothing()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");

        var result = await Service().RunAsync(
            new EditSessionRequest
            {
                PreviewOnly = true,
                Changes = [Files("files", [guide.Id], new SupportingFileBulkChangesDto { ScopeKey = "season:2", GroupName = "Homework" })],
            },
            NoRefusals);

        Assert.True(result.Valid);
        Assert.False(result.DataSaved);
        Assert.True(File.Exists(guide.FilePath));
        Assert.Empty(await _world.Extras.ListGroupsAsync(_world.Show));
        Assert.Empty(_world.Scanner.Noted);
    }

    [Fact]
    public async Task ASave_NamingAFileThatIsNotThisTitles_IsInvalid_AndNothingInTheSessionIsSaved()
    {
        await _world.SeedGeneratedFilesAsync();
        var details = new SpyHandler(EditSessionChangeKinds.Details);

        var result = await Service(details).RunAsync(
            Request(
                Details("details"),
                Files("files", [Guid.NewGuid()], new SupportingFileBulkChangesDto { GroupName = "Homework" })),
            NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionChangeStatuses.Invalid, result.Changes.Single(change => change.Id == "files").Status);
        Assert.Empty(details.Calls);
        Assert.Empty(await _world.Extras.ListGroupsAsync(_world.Show));
    }

    [Fact]
    public async Task ASave_MovingFilesToAPlaceTheTitleDoesNotHave_IsInvalid_WhileARealSeasonIsAccepted()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "Guide");

        var missing = await Service().RunAsync(
            new EditSessionRequest { PreviewOnly = true, Changes = [Files("files", [guide.Id], new SupportingFileBulkChangesDto { ScopeKey = "season:9" })] },
            NoRefusals);
        var real = await Service().RunAsync(
            new EditSessionRequest { PreviewOnly = true, Changes = [Files("files", [guide.Id], new SupportingFileBulkChangesDto { ScopeKey = "season:2" })] },
            NoRefusals);

        var refused = Assert.Single(missing.Changes);
        Assert.Equal(EditSessionChangeStatuses.Invalid, refused.Status);
        Assert.Equal(SupportingFileBulkService.BadPlace, refused.Reason);
        Assert.Equal(EditSessionChangeStatuses.Ok, Assert.Single(real.Changes).Status);
    }

    [Fact]
    public async Task ASaveWithNoFileListAtAll_IsInvalid()
    {
        var result = await Service().RunAsync(
            new EditSessionRequest
            {
                PreviewOnly = true,
                Changes = [new EditSessionChange { Id = "files", Kind = EditSessionChangeKinds.SupportingFiles, EntityId = _title }],
            },
            NoRefusals);

        Assert.Equal(SupportingFilesEditSessionChangeHandler.NoFiles, Assert.Single(result.Changes).Reason);
    }

    private EditSessionService Service(params IEditSessionChangeHandler[] others) => new(
        [new SupportingFilesEditSessionChangeHandler(_world.Bulk), .. others],
        NullLogger<EditSessionService>.Instance);

    private static EditSessionRequest Request(params EditSessionChange[] changes) => new() { Changes = [.. changes] };

    private EditSessionChange Details(string id) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.Details,
        EntityId = _title,
        Fields = [new EditSessionFieldChange { Key = "title", Value = "Orbital" }],
    };

    private EditSessionChange Files(string id, IEnumerable<Guid> ids, SupportingFileBulkChangesDto changes) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.SupportingFiles,
        EntityId = _title,
        SupportingFiles = new SupportingFileBulkUpdateRequest { Ids = [.. ids.Select(item => item.ToString("D"))], Changes = changes },
    };

    private async Task<List<Guid>> FirstFileIdsAsync(int count) =>
        (await _world.Extras.ListAllInItemFolderAsync(_world.Show)).Take(count).Select(row => row.Id).ToList();

    /// <summary>Stands in for another change in the session: it records that it ran, or fails the way an unfinished write would.</summary>
    private sealed class SpyHandler(string kind, bool fail = false) : IEditSessionChangeHandler
    {
        public List<string> Calls { get; } = [];

        public string Kind => kind;

        public Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
        {
            if (fail)
            {
                throw new InvalidOperationException("The match could not be changed.");
            }

            Calls.Add($"apply:{change.Id}");
            return Task.FromResult<Func<CancellationToken, Task>?>(null);
        }
    }
}
