using MediaEngine.Api.Services.Extras;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>
/// What the supporting-file editor stages for one file (universal editor v5 WP7): a title, a description and the episode or lesson the file is
/// shown alongside, saved through the same bulk service as the browser's actions, and kept when the folders are read again.
/// </summary>
public sealed class SupportingFileEditorBulkTests : IDisposable
{
    private readonly SupportingFileTestWorld _world = new();
    private readonly Guid _title = Guid.NewGuid();

    public void Dispose() => _world.Dispose();

    private static SupportingFileBulkUpdateRequest One(ItemExtra file, SupportingFileBulkChangesDto changes) =>
        new() { Ids = [file.Id.ToString("D")], Changes = changes };

    private async Task<SupportingFileBulkOutcome<SupportingFileBulkUpdateResultDto>> SaveAsync(SupportingFileBulkUpdateRequest request) =>
        (await _world.Bulk.ApplyAsync(_title, request)).Outcome;

    [Fact]
    public async Task ATitleAndADescription_OnOneFile_AreSaved_AndShownInsteadOfTheFileName()
    {
        var guide = await _world.SeedDiskFileAsync("worksheet-03.pdf", "worksheet 03");

        var outcome = await SaveAsync(One(guide, new SupportingFileBulkChangesDto { Title = "Fractions worksheet", Description = "Practice for week 3." }));

        Assert.Equal(1, outcome.Value!.Changed);
        var saved = (await _world.Extras.GetAsync(guide.Id))!;
        Assert.Equal("Fractions worksheet", saved.DisplayTitle);
        Assert.Equal("Practice for week 3.", saved.Description);
        Assert.Equal("worksheet 03", saved.Title);
    }

    [Fact]
    public async Task ClearingTheTitleAndDescription_GoesBackToTheNameReadFromTheFile()
    {
        var guide = await _world.SeedDiskFileAsync("worksheet-03.pdf", "worksheet 03");
        await SaveAsync(One(guide, new SupportingFileBulkChangesDto { Title = "Fractions worksheet", Description = "Practice." }));

        var outcome = await SaveAsync(One(guide, new SupportingFileBulkChangesDto { ClearTitle = true, ClearDescription = true }));

        Assert.Equal(1, outcome.Value!.Changed);
        var saved = (await _world.Extras.GetAsync(guide.Id))!;
        Assert.Equal("worksheet 03", saved.DisplayTitle);
        Assert.Null(saved.Description);
    }

    [Fact]
    public async Task ATitleForSeveralFiles_IsRefused_BecauseATitleBelongsToOneFile()
    {
        var first = await _world.SeedDiskFileAsync("a.pdf", "A");
        var second = await _world.SeedDiskFileAsync("b.pdf", "B");

        var outcome = await SaveAsync(new SupportingFileBulkUpdateRequest
        {
            Ids = [first.Id.ToString("D"), second.Id.ToString("D")],
            Changes = new SupportingFileBulkChangesDto { Title = "Same title" },
        });

        Assert.Equal(SupportingFileBulkProblem.Invalid, outcome.Problem);
        Assert.Equal(SupportingFileBulkService.OneFileOnly, outcome.Message);
        Assert.Equal("A", (await _world.Extras.GetAsync(first.Id))!.DisplayTitle);
    }

    [Fact]
    public async Task ARepeatedTitleChange_ReportsNothingChanged()
    {
        var guide = await _world.SeedDiskFileAsync("a.pdf", "A");
        var request = One(guide, new SupportingFileBulkChangesDto { Title = "Guide to A" });
        await SaveAsync(request);

        var again = await SaveAsync(request);

        Assert.Equal(0, again.Value!.Changed);
    }

    [Fact]
    public async Task ShownAlongside_NamesAnEpisode_WithoutMovingTheFileOrChangingWhereItBelongs()
    {
        _world.Scanner.Places.Add(ExtraScope.ForEpisode(2, 3));
        var guide = await _world.SeedDiskFileAsync("recap.pdf", "Recap", ExtraScope.ForSeason(2));

        var outcome = await SaveAsync(One(guide, new SupportingFileBulkChangesDto { AlongsideKey = "episode:2:3" }));

        Assert.Equal(1, outcome.Value!.Changed);
        Assert.Empty(outcome.Value.Moves);
        var saved = (await _world.Extras.GetAsync(guide.Id))!;
        Assert.Equal(ExtraScope.ForEpisode(2, 3), saved.EffectiveAlongside);
        Assert.True(File.Exists(guide.FilePath));
        Assert.Equal(guide.EffectiveScope, saved.EffectiveScope);
    }

    [Fact]
    public async Task ShownAlongside_NamingATitleLevelPlaceOrAnUnknownEpisode_IsRefused()
    {
        var guide = await _world.SeedDiskFileAsync("recap.pdf", "Recap");

        var season = await SaveAsync(One(guide, new SupportingFileBulkChangesDto { AlongsideKey = "season:2" }));
        var unknown = await SaveAsync(One(guide, new SupportingFileBulkChangesDto { AlongsideKey = "episode:9:9" }));

        Assert.Equal(SupportingFileBulkProblem.Invalid, season.Problem);
        Assert.Equal(SupportingFileBulkService.BadAlongside, season.Message);
        Assert.Equal(SupportingFileBulkProblem.Invalid, unknown.Problem);
        Assert.Null((await _world.Extras.GetAsync(guide.Id))!.Alongside);
    }

    [Fact]
    public async Task ShownAlongside_CanBeSetToNothing_AndBackToTheGuess()
    {
        var guide = await _world.SeedDiskFileAsync("recap.pdf", "Recap");

        await SaveAsync(One(guide, new SupportingFileBulkChangesDto { NoAlongside = true }));
        var none = (await _world.Extras.GetAsync(guide.Id))!;
        var back = await SaveAsync(One(guide, new SupportingFileBulkChangesDto { UseGuessedAlongside = true }));

        Assert.Equal(ExtraScope.None, none.Alongside);
        Assert.Null(none.EffectiveAlongside);
        Assert.Equal(1, back.Value!.Changed);
        Assert.Null((await _world.Extras.GetAsync(guide.Id))!.Alongside);
    }

    [Fact]
    public async Task WhatAPersonSet_SurvivesTheFoldersBeingReadAgain()
    {
        _world.Scanner.Places.Add(ExtraScope.ForEpisode(1, 1));
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "guide");
        await SaveAsync(One(guide, new SupportingFileBulkChangesDto
        {
            Title = "Viewer's guide",
            Description = "Read before the pilot.",
            AlongsideKey = "episode:1:1",
            GroupName = "Guides",
            Role = ExtraRoles.Guide,
            ScopeKey = "season:2",
        }));
        var moved = (await _world.Extras.GetAsync(guide.Id))!;

        // A rescan finds the same file again with what the folders say about it, which is not what the person chose.
        var rescanned = _world.Row(moved.FilePath, ItemExtraKinds.Document, "guide", ExtraScope.Root);
        await _world.Extras.SyncItemFolderAsync(_world.Show, [rescanned], []);

        var after = (await _world.Extras.GetByPathAsync(moved.FilePath))!;
        Assert.Equal(guide.Id, after.Id);
        Assert.Equal("Viewer's guide", after.DisplayTitle);
        Assert.Equal("Read before the pilot.", after.Description);
        Assert.Equal(ExtraScope.ForEpisode(1, 1), after.EffectiveAlongside);
        Assert.Equal("Guides", after.GroupName);
        Assert.Equal(ExtraRoles.Guide, after.EffectiveRole);
        Assert.Equal(ExtraScope.ForSeason(2), after.EffectiveScope);
    }

    [Fact]
    public async Task UseFolderLocation_PutsTheFolderPlaceBack_AndAFileInAManagedFolderFollowsIt()
    {
        var guide = await _world.SeedDiskFileAsync("guide.pdf", "guide");
        await SaveAsync(One(guide, new SupportingFileBulkChangesDto { ScopeKey = "season:2" }));
        var movedPath = Path.Combine(_world.SeasonFolder(2), "guide.pdf");
        Assert.True(File.Exists(movedPath));

        var outcome = await SaveAsync(One(guide, new SupportingFileBulkChangesDto { UseFolderLocation = true }));

        // The place read from the folder is now the season, because the file moved there; going back to it changes nothing on disk.
        Assert.Empty(outcome.Value!.Moves);
        var after = (await _world.Extras.GetAsync(guide.Id))!;
        Assert.Null(after.Scope);
        Assert.True(File.Exists(movedPath));
    }

    [Fact]
    public async Task ABelongsToChange_InAReadOnlyFolder_KeepsTheFileWhereItIs()
    {
        using var readOnly = new SupportingFileTestWorld(managedFolder: false);
        var guide = await readOnly.SeedDiskFileAsync("guide.pdf", "guide");

        var outcome = (await readOnly.Bulk.ApplyAsync(_title, One(guide, new SupportingFileBulkChangesDto { ScopeKey = "season:3", Title = "Season 3 guide" }))).Outcome;

        Assert.Empty(outcome.Value!.Moves);
        Assert.True(File.Exists(guide.FilePath));
        var saved = (await readOnly.Extras.GetAsync(guide.Id))!;
        Assert.Equal(ExtraScope.ForSeason(3), saved.EffectiveScope);
        Assert.Equal("Season 3 guide", saved.DisplayTitle);
    }
}
